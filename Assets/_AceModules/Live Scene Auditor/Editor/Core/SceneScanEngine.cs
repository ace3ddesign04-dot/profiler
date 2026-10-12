using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AceModules.LiveSceneAuditor
{
    public static class SceneScanEngine
    {
        // Internal lightweight snapshot struct captured safely on Main Thread
        private struct RendererSnapshot
        {
            public int instanceId;
            public string name;
            public Bounds worldBounds;
            public int triangleCount;
            public int vertexCount;
            public bool isPartOfLOD;
            public bool isActiveLOD;
            public int materialCount;
            public List<int> materialInstanceIds;
            public List<string> materialNames;
            public List<int> textureInstanceIds;
            public List<string> textureNames;
            public bool isModelAsset;
            public string fbxAssetPath;
            public string fbxName;
            public int modelInstanceRootId;
            public int fbxSceneOccurrences;
            public List<int> fbxSceneRootIds;
        }

        private struct TextureSnapshot
        {
            public int instanceId;
            public string name;
            public int width;
            public int height;
            public float score;
            public List<string> usedInMaterials;
        }

        private struct MaterialSnapshot
        {
            public int materialInstanceId;
            public string materialName;
            public string shaderName;
            public int textureInstanceId;
            public string textureName;
            public int textureWidth;
            public int textureHeight;
            public float textureScore;
            public List<int> rendererInstanceIds;
            public List<string> rendererNames;
            public List<int> visibleRendererInstanceIds;
            public List<string> visibleRendererNames;
        }

        private struct LightSnapshot
        {
            public int instanceId;
            public string name;
            public LightType type;
            public LightShadows shadows;
            public bool isRealtime;
            public bool isBaked;
            public bool isMixed;
            public bool isActiveAndEnabled;
            public bool isVisibleInView;
            public float range;
            public float intensity;
            public Color color;
            public Vector3 position;
            public float spotAngle;
            public LightRenderMode renderMode;
        }

        private struct CameraSnapshot
        {
            public Vector3 position;
            public Quaternion rotation;
            public Plane[] frustumPlanes;
            public float fov;
            public bool isGameCamera;
            public bool isPlaying;
            public int unityStatsTriangles;
            public int unityStatsVertices;
            public bool isValid;
        }

        private struct ViewpointSnapshot
        {
            public string name;
            public int instanceId;
            public Vector3 position;
            public Vector3 rotationEuler;
            public Plane[] frustumPlanes;
            public float fov;
        }

        private static CancellationTokenSource _activeCts;
        private static SynchronizationContext _mainThreadContext;

        private static int _lastGameCameraTris = 0;
        private static int _lastGameCameraVerts = 0;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            _mainThreadContext = SynchronizationContext.Current;
            Camera.onPostRender -= OnGlobalPostRender;
            Camera.onPostRender += OnGlobalPostRender;
        }

        private static void OnGlobalPostRender(Camera cam)
        {
            if (cam == null || cam.targetTexture != null) return;
            // Capture when Game Camera renders naturally to screen / GameView
            if (cam == Camera.main || cam.CompareTag("MainCamera") || cam.cameraType == CameraType.Game)
            {
                _lastGameCameraTris = UnityEditor.UnityStats.triangles;
                _lastGameCameraVerts = UnityEditor.UnityStats.vertices;
            }
        }

        public static void RequestGameViewRepaint()
        {
            try
            {
                System.Type gameViewType = System.Type.GetType("UnityEditor.GameView,UnityEditor");
                if (gameViewType != null)
                {
                    EditorWindow gv = EditorWindow.GetWindow(gameViewType, false, null, false);
                    if (gv != null)
                    {
                        gv.Repaint();
                    }
                }
            }
            catch {}
        }

        private static List<ViewpointSnapshot> CaptureViewpointSnapshots(List<Transform> viewpoints, Camera refCam)
        {
            List<ViewpointSnapshot> list = new List<ViewpointSnapshot>();
            if (viewpoints == null || viewpoints.Count == 0) return list;

            GameObject tempCamGo = null;
            Camera tempCam = null;

            try
            {
                tempCamGo = new GameObject("__Auditor_TempViewpointCam") { hideFlags = HideFlags.HideAndDontSave };
                tempCam = tempCamGo.AddComponent<Camera>();
                if (refCam != null)
                {
                    tempCam.CopyFrom(refCam);
                }
                else
                {
                    tempCam.fieldOfView = 60f;
                    tempCam.nearClipPlane = 0.3f;
                    tempCam.farClipPlane = 1000f;
                }

                foreach (var vp in viewpoints)
                {
                    if (vp == null) continue;
                    tempCam.transform.position = vp.position;
                    tempCam.transform.rotation = vp.rotation;

                    Plane[] planes = GeometryUtility.CalculateFrustumPlanes(tempCam);
                    list.Add(new ViewpointSnapshot
                    {
                        name = vp.name,
                        instanceId = vp.gameObject.GetInstanceID(),
                        position = vp.position,
                        rotationEuler = vp.rotation.eulerAngles,
                        frustumPlanes = planes,
                        fov = tempCam.fieldOfView
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Live Scene Auditor] Failed to capture viewpoint frustums: {ex.Message}");
            }
            finally
            {
                if (tempCamGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempCamGo);
                }
            }

            return list;
        }

        /// <summary>
        /// Initiates a scan.
        /// </summary>
        public static void ScanScene(
            bool frustumOnly, 
            BudgetProfile profile, 
            bool asyncMode, 
            bool includeInactive,
            Camera targetCamera,
            bool isGameCamera,
            Action<SceneAuditReport> onCompleted,
            List<Transform> viewpoints = null)
        {
            if (_mainThreadContext == null)
            {
                _mainThreadContext = SynchronizationContext.Current;
            }

            Stopwatch sw = Stopwatch.StartNew();

            // Cancel any pending async scan
            if (_activeCts != null)
            {
                _activeCts.Cancel();
                _activeCts.Dispose();
                _activeCts = null;
            }

            // Phase 1: Capture Main-Thread Snapshot
            if (isGameCamera)
            {
                RequestGameViewRepaint();
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main != null ? Camera.main : (SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null);
            }
            CameraSnapshot camSnap = CaptureCameraSnapshot(targetCamera, isGameCamera);
            List<RendererSnapshot> renderers = CaptureRenderersSnapshot(includeInactive, camSnap, out int fbxCount);
            List<TextureSnapshot> textures = CaptureTexturesSnapshot();
            List<MaterialSnapshot> materials = CaptureMaterialsSnapshot(includeInactive, camSnap);
            List<LightSnapshot> lights = CaptureLightsSnapshot(includeInactive, camSnap);
            List<ViewpointSnapshot> viewpointSnaps = CaptureViewpointSnapshots(viewpoints, targetCamera);

            if (!asyncMode)
            {
                // Synchronous immediate processing
                SceneAuditReport report = ProcessSnapshot(renderers, textures, materials, lights, camSnap, frustumOnly, profile, fbxCount, viewpointSnaps);
                sw.Stop();
                report.scanDurationMs = (float)sw.Elapsed.TotalMilliseconds;
                onCompleted?.Invoke(report);
            }
            else
            {
                // Asynchronous background processing
                _activeCts = new CancellationTokenSource();
                CancellationToken token = _activeCts.Token;
                SynchronizationContext context = _mainThreadContext;

                Task.Run(() =>
                {
                    try
                    {
                        if (token.IsCancellationRequested) return;

                        SceneAuditReport report = ProcessSnapshot(renderers, textures, materials, lights, camSnap, frustumOnly, profile, fbxCount, viewpointSnaps);
                        sw.Stop();
                        report.scanDurationMs = (float)sw.Elapsed.TotalMilliseconds;

                        if (!token.IsCancellationRequested)
                        {
                            if (context != null)
                            {
                                context.Post(_ =>
                                {
                                    if (!token.IsCancellationRequested)
                                    {
                                        onCompleted?.Invoke(report);
                                    }
                                }, null);
                            }
                            else
                            {
                                EditorApplication.delayCall += () =>
                                {
                                    if (!token.IsCancellationRequested)
                                    {
                                        onCompleted?.Invoke(report);
                                    }
                                };
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Live Scene Auditor] Error during async scan: {ex}");
                        if (context != null)
                        {
                            context.Post(_ => onCompleted?.Invoke(null), null);
                        }
                    }
                }, token);
            }
        }

        private static CameraSnapshot CaptureCameraSnapshot(Camera cam, bool isGameCamera)
        {
            CameraSnapshot snap = new CameraSnapshot();
            snap.isPlaying = EditorApplication.isPlaying;

            if (cam != null)
            {
                snap.position = cam.transform.position;
                snap.rotation = cam.transform.rotation;
                snap.frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);
                snap.fov = cam.fieldOfView > 0 ? cam.fieldOfView : 60f;
                snap.isGameCamera = isGameCamera || (cam == Camera.main || cam.CompareTag("MainCamera") || cam.cameraType == CameraType.Game);
                snap.unityStatsTriangles = _lastGameCameraTris > 0 ? _lastGameCameraTris : UnityEditor.UnityStats.triangles;
                snap.unityStatsVertices = _lastGameCameraVerts > 0 ? _lastGameCameraVerts : UnityEditor.UnityStats.vertices;
                snap.isValid = true;
            }
            else
            {
                snap.isValid = false;
                snap.isGameCamera = isGameCamera;
                snap.unityStatsTriangles = UnityEditor.UnityStats.triangles;
                snap.unityStatsVertices = UnityEditor.UnityStats.vertices;
                snap.fov = 60f;
                snap.frustumPlanes = new Plane[0];
            }
            return snap;
        }

        private static List<RendererSnapshot> CaptureRenderersSnapshot(bool includeInactive, CameraSnapshot camSnap, out int fbxCount)
        {
            List<RendererSnapshot> list = new List<RendererSnapshot>();
            HashSet<string> uniqueFbxPaths = new HashSet<string>();

            // Identify LOD groups first with strict active and inactive sets
            LODGroup[] lodGroups = GameObject.FindObjectsOfType<LODGroup>(includeInactive);
            HashSet<Renderer> activeLodRenderers = new HashSet<Renderer>();
            HashSet<Renderer> inactiveLodRenderers = new HashSet<Renderer>();

            foreach (var lodGroup in lodGroups)
            {
                if (lodGroup == null) continue;
                LOD[] lods = lodGroup.GetLODs();
                if (lods == null || lods.Length == 0) continue;

                // Determine active LOD based on accurate screen coverage metric
                int activeLodIndex = -1; // -1 means completely culled by LOD distance
                if (camSnap.isValid)
                {
                    Vector3 lodCenter = lodGroup.transform.TransformPoint(lodGroup.localReferencePoint);
                    float distance = Vector3.Distance(camSnap.position, lodCenter);
                    float halfFovRad = (camSnap.fov * 0.5f) * Mathf.Deg2Rad;
                    float screenRelativeMetric = (lodGroup.size * QualitySettings.lodBias) / (Mathf.Max(0.01f, distance) * 2.0f * Mathf.Tan(halfFovRad));

                    for (int i = 0; i < lods.Length; i++)
                    {
                        if (screenRelativeMetric >= lods[i].screenRelativeTransitionHeight)
                        {
                            activeLodIndex = i;
                            break;
                        }
                    }
                }
                else
                {
                    activeLodIndex = 0; // Default to LOD0 if no camera
                }

                for (int i = 0; i < lods.Length; i++)
                {
                    bool isActive = (i == activeLodIndex);
                    foreach (var r in lods[i].renderers)
                    {
                        if (r == null) continue;
                        if (isActive)
                        {
                            activeLodRenderers.Add(r);
                        }
                        else
                        {
                            inactiveLodRenderers.Add(r);
                        }
                    }
                }
            }

            // Pass 1: Build scene-wide FBX model occurrence mapping across ALL scene objects
            Renderer[] allSceneRenderers = GameObject.FindObjectsOfType<Renderer>(true);
            Dictionary<string, HashSet<int>> fbxToAllSceneRootIds = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in allSceneRenderers)
            {
                if (r == null || IsIgnoredRenderer(r)) continue;

                Mesh m = null;
                if (r is MeshRenderer)
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null) m = mf.sharedMesh;
                }
                else if (r is SkinnedMeshRenderer smr)
                {
                    m = smr.sharedMesh;
                }

                if (m == null) continue;

                string path = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(path) && IsModelAssetPath(path))
                {
                    uniqueFbxPaths.Add(path);

                    if (!fbxToAllSceneRootIds.TryGetValue(path, out var rootSet))
                    {
                        rootSet = new HashSet<int>();
                        fbxToAllSceneRootIds[path] = rootSet;
                    }

                    GameObject rootGo = PrefabUtility.GetNearestPrefabInstanceRoot(r.gameObject);
                    if (rootGo == null) rootGo = r.gameObject;
                    rootSet.Add(rootGo.GetInstanceID());
                }
            }

            // Collect all MeshRenderers & SkinnedMeshRenderers according to includeInactive
            Renderer[] allRenderers = GameObject.FindObjectsOfType<Renderer>(includeInactive);

            foreach (var r in allRenderers)
            {
                if (r == null || !r.enabled || (!r.gameObject.activeInHierarchy && !includeInactive))
                    continue;

                // Ignore TextMeshPro and Canvas/UI components
                if (IsIgnoredRenderer(r))
                    continue;

                Mesh mesh = null;
                if (r is MeshRenderer)
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                else if (r is SkinnedMeshRenderer smr)
                {
                    mesh = smr.sharedMesh;
                }

                if (mesh == null) continue;

                string assetPath = AssetDatabase.GetAssetPath(mesh);
                bool isModel = !string.IsNullOrEmpty(assetPath) && IsModelAssetPath(assetPath);
                string fbxName = isModel ? System.IO.Path.GetFileNameWithoutExtension(assetPath) : "";
                int occurrences = 0;
                List<int> rootIds = null;
                GameObject rootInstanceGo = PrefabUtility.GetNearestPrefabInstanceRoot(r.gameObject);
                if (rootInstanceGo == null) rootInstanceGo = r.gameObject;
                int rootInstanceId = rootInstanceGo.GetInstanceID();

                if (isModel && fbxToAllSceneRootIds.TryGetValue(assetPath, out var rRoots))
                {
                    occurrences = rRoots.Count;
                    rootIds = rRoots.ToList();
                }

                // Strict LOD rule: If part of an LOD group and NOT in the active set, SKIP ENTIRELY!
                bool isPartOfLod = activeLodRenderers.Contains(r) || inactiveLodRenderers.Contains(r);
                if (inactiveLodRenderers.Contains(r) && !activeLodRenderers.Contains(r))
                {
                    continue; // Skip inactive LOD meshes
                }

                int triCount = 0;
                try
                {
                    int subMeshCount = mesh.subMeshCount;
                    for (int s = 0; s < subMeshCount; s++)
                    {
                        triCount += (int)(mesh.GetIndexCount(s) / 3);
                    }
                }
                catch
                {
                    triCount = 0;
                }

                Material[] sharedMats = r.sharedMaterials;
                List<int> matIds = new List<int>();
                List<string> matNames = new List<string>();
                List<int> texIds = new List<int>();
                List<string> texNames = new List<string>();
                HashSet<int> seenTex = new HashSet<int>();

                if (sharedMats != null)
                {
                    foreach (var m in sharedMats)
                    {
                        if (m != null && !IsIgnoredAsset(m))
                        {
                            matIds.Add(m.GetInstanceID());
                            matNames.Add(m.name);

                            try
                            {
                                string[] propNames = m.GetTexturePropertyNames();
                                if (propNames != null)
                                {
                                    foreach (var prop in propNames)
                                    {
                                        Texture tex = m.GetTexture(prop);
                                        if (tex != null && !IsIgnoredAsset(tex) && !seenTex.Contains(tex.GetInstanceID()))
                                        {
                                            seenTex.Add(tex.GetInstanceID());
                                            texIds.Add(tex.GetInstanceID());
                                            texNames.Add(tex.name);
                                        }
                                    }
                                }
                            }
                            catch {}
                        }
                    }
                }

                list.Add(new RendererSnapshot
                {
                    instanceId = r.gameObject.GetInstanceID(),
                    name = r.gameObject.name,
                    worldBounds = r.bounds,
                    triangleCount = triCount,
                    vertexCount = mesh.vertexCount,
                    isPartOfLOD = isPartOfLod,
                    isActiveLOD = true,
                    materialCount = matIds.Count,
                    materialInstanceIds = matIds,
                    materialNames = matNames,
                    textureInstanceIds = texIds,
                    textureNames = texNames,
                    isModelAsset = isModel,
                    fbxAssetPath = isModel ? assetPath : "",
                    fbxName = fbxName,
                    modelInstanceRootId = rootInstanceId,
                    fbxSceneOccurrences = occurrences,
                    fbxSceneRootIds = rootIds ?? new List<int>()
                });
            }

            fbxCount = uniqueFbxPaths.Count;
            return list;
        }

        private static List<TextureSnapshot> CaptureTexturesSnapshot()
        {
            List<TextureSnapshot> list = new List<TextureSnapshot>();
            HashSet<int> processedTextures = new HashSet<int>();

            // Collect textures from all scene 3D renderers
            Renderer[] allRenderers = GameObject.FindObjectsOfType<Renderer>(true);
            foreach (var r in allRenderers)
            {
                if (r == null || IsIgnoredRenderer(r)) continue;
                Material[] mats = r.sharedMaterials;
                if (mats == null) continue;

                foreach (var mat in mats)
                {
                    if (mat == null || IsIgnoredAsset(mat)) continue;

                    try
                    {
                        string[] propNames = mat.GetTexturePropertyNames();
                        if (propNames == null) continue;

                        foreach (var propName in propNames)
                        {
                            Texture tex = mat.GetTexture(propName);
                            if (tex != null && !IsIgnoredAsset(tex) && !processedTextures.Contains(tex.GetInstanceID()))
                            {
                                processedTextures.Add(tex.GetInstanceID());
                                float score = ((float)tex.width / 512f) * ((float)tex.height / 512f);

                                list.Add(new TextureSnapshot
                                {
                                    instanceId = tex.GetInstanceID(),
                                    name = tex.name,
                                    width = tex.width,
                                    height = tex.height,
                                    score = score,
                                    usedInMaterials = new List<string> { mat.name }
                                });
                            }
                        }
                    }
                    catch
                    {
                        // Safely skip any materials with invalid shader properties
                    }
                }
            }

            return list;
        }

        private static List<MaterialSnapshot> CaptureMaterialsSnapshot(bool includeInactive, CameraSnapshot camSnap)
        {
            List<MaterialSnapshot> list = new List<MaterialSnapshot>();
            Dictionary<int, MaterialSnapshot> matMap = new Dictionary<int, MaterialSnapshot>();

            Renderer[] allRenderers = GameObject.FindObjectsOfType<Renderer>(includeInactive);
            foreach (var r in allRenderers)
            {
                if (r == null || !r.enabled || (!r.gameObject.activeInHierarchy && !includeInactive))
                    continue;

                if (IsIgnoredRenderer(r))
                    continue;

                Material[] sharedMats = r.sharedMaterials;
                if (sharedMats == null) continue;

                bool isVisibleInView = camSnap.isValid && TestPlanesAABBThreadSafe(camSnap.frustumPlanes, r.bounds);

                foreach (var m in sharedMats)
                {
                    if (m == null || IsIgnoredAsset(m)) continue;

                    int mId = m.GetInstanceID();
                    if (!matMap.TryGetValue(mId, out MaterialSnapshot snap))
                    {
                        snap = new MaterialSnapshot
                        {
                            materialInstanceId = mId,
                            materialName = m.name,
                            shaderName = m.shader != null ? m.shader.name : "Standard",
                            rendererInstanceIds = new List<int>(),
                            rendererNames = new List<string>(),
                            visibleRendererInstanceIds = new List<int>(),
                            visibleRendererNames = new List<string>()
                        };

                        Texture mainTex = null;
                        try
                        {
                            mainTex = m.mainTexture;
                        }
                        catch {}

                        if (mainTex == null)
                        {
                            try
                            {
                                if (m.HasProperty("_BaseMap")) mainTex = m.GetTexture("_BaseMap");
                                else if (m.HasProperty("_MainTex")) mainTex = m.GetTexture("_MainTex");
                            }
                            catch {}
                        }

                        if (mainTex != null && !IsIgnoredAsset(mainTex))
                        {
                            snap.textureInstanceId = mainTex.GetInstanceID();
                            snap.textureName = mainTex.name;
                            snap.textureWidth = mainTex.width;
                            snap.textureHeight = mainTex.height;
                            snap.textureScore = ((float)mainTex.width / 512f) * ((float)mainTex.height / 512f);
                        }
                        else
                        {
                            snap.textureInstanceId = 0;
                            snap.textureName = "(No Texture / Flat Color)";
                            snap.textureWidth = 0;
                            snap.textureHeight = 0;
                            snap.textureScore = 0f;
                        }

                        matMap[mId] = snap;
                    }

                    int rId = r.gameObject.GetInstanceID();
                    if (!snap.rendererInstanceIds.Contains(rId))
                    {
                        snap.rendererInstanceIds.Add(rId);
                        snap.rendererNames.Add(r.gameObject.name);
                    }

                    if (isVisibleInView && !snap.visibleRendererInstanceIds.Contains(rId))
                    {
                        snap.visibleRendererInstanceIds.Add(rId);
                        snap.visibleRendererNames.Add(r.gameObject.name);
                    }
                }
            }

            list.AddRange(matMap.Values);
            return list;
        }

        private static List<LightSnapshot> CaptureLightsSnapshot(bool includeInactive, CameraSnapshot camSnap)
        {
            List<LightSnapshot> list = new List<LightSnapshot>();
            Light[] allLights = GameObject.FindObjectsOfType<Light>(includeInactive);

            foreach (var light in allLights)
            {
                if (light == null) continue;
                if ((light.gameObject.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave)) != 0) continue;
                if (!light.gameObject.scene.IsValid() || !light.gameObject.scene.isLoaded) continue;

                bool isActive = light.isActiveAndEnabled;
                if (!includeInactive && !isActive)
                {
                    continue;
                }

                var bakeType = light.bakingOutput.lightmapBakeType;
                bool isRealtime = (bakeType == LightmapBakeType.Realtime);
                bool isBaked = (bakeType == LightmapBakeType.Baked);
                bool isMixed = (bakeType == LightmapBakeType.Mixed);

                bool isVisible = false;
                if (isActive)
                {
                    if (light.type == LightType.Directional)
                    {
                        // Directional lights have infinite reach across the camera frustum
                        isVisible = true;
                    }
                    else if (camSnap.isValid)
                    {
                        // Test sphere vs camera frustum planes
                        isVisible = TestSphereFrustum(light.transform.position, Mathf.Max(0.1f, light.range), camSnap.frustumPlanes);
                    }
                    else
                    {
                        isVisible = true;
                    }
                }

                list.Add(new LightSnapshot
                {
                    instanceId = light.gameObject.GetInstanceID(),
                    name = light.gameObject.name,
                    type = light.type,
                    shadows = light.shadows,
                    isRealtime = isRealtime,
                    isBaked = isBaked,
                    isMixed = isMixed,
                    isActiveAndEnabled = isActive,
                    isVisibleInView = isVisible,
                    range = light.range,
                    intensity = light.intensity,
                    color = light.color,
                    position = light.transform.position,
                    spotAngle = (light.type == LightType.Spot) ? light.spotAngle : 0f,
                    renderMode = light.renderMode
                });
            }

            return list;
        }

        public static bool TestSphereFrustum(Vector3 center, float radius, Plane[] planes)
        {
            if (planes == null || planes.Length == 0) return true;
            for (int i = 0; i < planes.Length; i++)
            {
                if (planes[i].GetDistanceToPoint(center) < -radius)
                {
                    return false;
                }
            }
            return true;
        }

        private static List<AtlasGroupSuggestion> GenerateAtlasSuggestions(List<MaterialSnapshot> materials)
        {
            List<AtlasGroupSuggestion> list = new List<AtlasGroupSuggestion>();
            if (materials == null || materials.Count == 0) return list;

            var groups = materials
                .GroupBy(m => m.shaderName)
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Count());

            foreach (var group in groups)
            {
                string fullShader = group.Key;
                string simpleShader = GetSimpleShaderName(fullShader);
                var matList = group.OrderBy(m => m.rendererInstanceIds.Count).ToList();

                AtlasGroupSuggestion suggestion = new AtlasGroupSuggestion
                {
                    shaderName = fullShader,
                    groupTitle = $"{simpleShader} ({matList.Count} Materials)",
                    potentialMaterialSavings = matList.Count - 1,
                    isFoldedOut = true
                };

                float combinedScore = 0f;
                long totalArea = 0;
                int totalRenderers = 0;

                foreach (var mat in matList)
                {
                    combinedScore += mat.textureScore;
                    totalRenderers += mat.rendererInstanceIds.Count;

                    int w = mat.textureWidth > 0 ? mat.textureWidth : 256;
                    int h = mat.textureHeight > 0 ? mat.textureHeight : 256;
                    totalArea += (long)(w * h);

                    suggestion.candidates.Add(new AtlasCandidateItem
                    {
                        materialName = mat.materialName,
                        materialInstanceId = mat.materialInstanceId,
                        instanceId = mat.materialInstanceId,
                        rendererName = mat.rendererNames.Count > 0 ? mat.rendererNames[0] : "None",
                        textureName = mat.textureName,
                        textureInstanceId = mat.textureInstanceId,
                        width = mat.textureWidth,
                        height = mat.textureHeight,
                        textureScore = mat.textureScore,
                        rendererUsageCount = mat.rendererInstanceIds.Count,
                        rendererInstanceIds = mat.rendererInstanceIds,
                        rendererNames = mat.rendererNames,
                        isVisibleInView = mat.visibleRendererInstanceIds.Count > 0,
                        visibleRendererUsageCount = mat.visibleRendererInstanceIds.Count,
                        visibleRendererInstanceIds = mat.visibleRendererInstanceIds,
                        visibleRendererNames = mat.visibleRendererNames
                    });
                }

                suggestion.currentCombinedTextureScore = combinedScore;
                suggestion.totalRenderersCount = totalRenderers;

                long estimatedPackArea = (long)(totalArea * 1.25);
                if (estimatedPackArea <= 512 * 512)
                    suggestion.suggestedAtlasSize = "512 x 512";
                else if (estimatedPackArea <= 1024 * 1024)
                    suggestion.suggestedAtlasSize = "1024 x 1024";
                else if (estimatedPackArea <= 2048 * 2048)
                    suggestion.suggestedAtlasSize = "2048 x 2048";
                else if (estimatedPackArea <= 4096 * 4096)
                    suggestion.suggestedAtlasSize = "4096 x 4096";
                else
                    suggestion.suggestedAtlasSize = "Multiple 2048 x 2048 Atlases";

                list.Add(suggestion);
            }

            return list;
        }

        public static string GetSimpleShaderName(string fullShader)
        {
            if (string.IsNullOrEmpty(fullShader)) return "Standard";
            int idx = fullShader.LastIndexOf('/');
            if (idx >= 0 && idx < fullShader.Length - 1)
                fullShader = fullShader.Substring(idx + 1);
            int bsIdx = fullShader.LastIndexOf('\\');
            if (bsIdx >= 0 && bsIdx < fullShader.Length - 1)
                fullShader = fullShader.Substring(bsIdx + 1);
            return fullShader.Replace(".shader", "");
        }

        private static bool IsIgnoredRenderer(Renderer r)
        {
            if (r == null) return true;
            if (r.GetComponent<CanvasRenderer>() != null) return true;

            string typeName = r.GetType().Name;
            if (typeName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("TMP", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private static bool IsIgnoredAsset(UnityEngine.Object obj)
        {
            if (obj == null) return true;
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return false;

            if (path.StartsWith("Packages/com.unity.textmeshpro", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.IndexOf("TextMesh Pro/Resources", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (path.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (path.StartsWith("Resources/unity_builtin_extra", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith("Library/unity default resources", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        public static bool IsModelAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".blend", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".dae", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".3ds", StringComparison.OrdinalIgnoreCase);
        }

        private static SceneAuditReport ProcessSnapshot(
            List<RendererSnapshot> renderers,
            List<TextureSnapshot> textures,
            List<MaterialSnapshot> materials,
            List<LightSnapshot> lights,
            CameraSnapshot camSnap,
            bool frustumOnly,
            BudgetProfile profile,
            int fbxCount,
            List<ViewpointSnapshot> viewpointSnaps = null)
        {
            SceneAuditReport report = new SceneAuditReport();
            report.isFrustumViewOnly = frustumOnly;

            HashSet<int> uniqueMaterialIds = new HashSet<int>();
            int visibleTris = 0;
            int visibleVerts = 0;
            int totalTris = 0;
            int totalVerts = 0;
            int visibleRenderersCount = 0;

            List<RendererSnapshot> visibleRenderers = new List<RendererSnapshot>();
            List<CulpritItem> meshCulprits = new List<CulpritItem>();
            Dictionary<string, HashSet<int>> fbxVisibleRoots = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in renderers)
            {
                // Always accumulate total scene stats for active LODs
                if (r.isActiveLOD)
                {
                    totalTris += r.triangleCount;
                    totalVerts += r.vertexCount;
                }

                // Check Frustum Visibility (Thread-safe pure C# math)
                bool isVisible = true;
                if (frustumOnly && camSnap.isValid)
                {
                    isVisible = TestPlanesAABBThreadSafe(camSnap.frustumPlanes, r.worldBounds);
                }

                if (isVisible && r.isActiveLOD)
                {
                    visibleRenderers.Add(r);
                    visibleTris += r.triangleCount;
                    visibleVerts += r.vertexCount;
                    visibleRenderersCount++;

                    foreach (var matId in r.materialInstanceIds)
                    {
                        uniqueMaterialIds.Add(matId);
                    }

                    if (r.isModelAsset && !string.IsNullOrEmpty(r.fbxAssetPath))
                    {
                        if (!fbxVisibleRoots.TryGetValue(r.fbxAssetPath, out var visSet))
                        {
                            visSet = new HashSet<int>();
                            fbxVisibleRoots[r.fbxAssetPath] = visSet;
                        }
                        visSet.Add(r.modelInstanceRootId);
                    }
                }
            }

            // Collect and rank culprits from visible renderers
            // Group FBX model assets so repeating models (like 756 coins) collapse into 1 clean row with total geometry & occurrence counts
            var fbxGroups = visibleRenderers
                .Where(r => r.isModelAsset && !string.IsNullOrEmpty(r.fbxAssetPath))
                .GroupBy(r => r.fbxAssetPath);

            foreach (var group in fbxGroups)
            {
                var rep = group.First();
                string fbxPath = group.Key;
                string fbxName = !string.IsNullOrEmpty(rep.fbxName) 
                    ? rep.fbxName 
                    : System.IO.Path.GetFileNameWithoutExtension(fbxPath);

                int occurrences = rep.fbxSceneOccurrences;
                int visOccurrences = 0;
                if (fbxVisibleRoots.TryGetValue(fbxPath, out var visSet))
                {
                    visOccurrences = visSet.Count;
                }
                else
                {
                    visOccurrences = group.Select(r => r.modelInstanceRootId).Distinct().Count();
                }

                // Total triangles across all visible instances of this FBX
                int groupTotalTris = group.Sum(r => r.triangleCount);
                int groupTotalVerts = group.Sum(r => r.vertexCount);

                int visInstanceCount = Mathf.Max(1, visOccurrences);
                int trisPerInstance = groupTotalTris / visInstanceCount;
                int vertsPerInstance = groupTotalVerts / visInstanceCount;

                List<string> allMatNames = group.SelectMany(r => r.materialNames).Distinct().ToList();

                float triBudgetRatio = profile.maxTriangles > 0 ? ((float)groupTotalTris / profile.maxTriangles) : 0f;
                float triPercent = triBudgetRatio * 100f;

                bool isHighPoly = trisPerInstance >= profile.singleMeshHeavyTriangles;
                bool isMultiMat = allMatNames.Count > profile.maxMaterialsPerObject;
                bool isMissingLod = !rep.isPartOfLOD && trisPerInstance >= profile.requireLodAboveTriangles;

                List<string> reasons = new List<string>();
                float weight = triBudgetRatio * 1000f;
                if (occurrences > 1)
                {
                    // High occurrence scaling: surfaces repeated objects (e.g. 756 coins) in culprits ranking
                    weight += Mathf.Min(500f, occurrences * 3f);
                }

                if (isHighPoly)
                {
                    reasons.Add($"High Poly ({trisPerInstance:N0} Tris)");
                    weight += 150f;
                }
                if (isMultiMat)
                {
                    reasons.Add($"Multi-Material ({allMatNames.Count} Mats)");
                    weight += (allMatNames.Count * 75f);
                }
                if (isMissingLod)
                {
                    reasons.Add("Missing LOD Group");
                    weight += 120f;
                }
                if (occurrences >= 20)
                {
                    reasons.Add($"High Instance Count ({occurrences} Occurrences)");
                }

                string occWord = occurrences == 1 ? "Occurrence" : "Occurrences";
                string occText;
                if (frustumOnly && visOccurrences > 0 && visOccurrences != occurrences)
                {
                    occText = $" • {occurrences} FBX {occWord} in Scene ({visOccurrences} in View)";
                }
                else
                {
                    occText = $" • {occurrences} FBX {occWord} in Scene";
                }

                string primaryMetric;
                if (occurrences > 1)
                {
                    primaryMetric = $"{trisPerInstance:N0} Tris/inst ({groupTotalTris:N0} Total • {triPercent:F1}%)";
                }
                else
                {
                    primaryMetric = $"{trisPerInstance:N0} Tris ({triPercent:F1}%)";
                }

                string secondaryDetail = $"{vertsPerInstance:N0} Verts • {allMatNames.Count} Mats{occText}";

                string displayName = occurrences > 1 
                    ? $"{occurrences}× {fbxName}" 
                    : fbxName;

                List<int> rootIds = rep.fbxSceneRootIds != null && rep.fbxSceneRootIds.Count > 0 
                    ? rep.fbxSceneRootIds 
                    : group.Select(r => r.modelInstanceRootId).Distinct().ToList();

                meshCulprits.Add(new CulpritItem
                {
                    name = displayName,
                    instanceId = rep.instanceId,
                    type = CulpritType.MeshGeometry,
                    triangleCount = groupTotalTris,
                    vertexCount = groupTotalVerts,
                    primaryMetricText = primaryMetric,
                    secondaryDetailText = secondaryDetail,
                    severityScore = weight,
                    isHighPoly = isHighPoly,
                    isMultiMaterial = isMultiMat,
                    isMissingLod = isMissingLod,
                    flaggedReasons = reasons,
                    dependencyNames = allMatNames,
                    fbxAssetPath = fbxPath,
                    fbxName = fbxName,
                    fbxOccurrences = occurrences,
                    fbxVisibleOccurrences = visOccurrences,
                    fbxInstanceRootIds = rootIds
                });
            }

            // Non-FBX standalone renderers (procedural meshes, primitives, etc.)
            var nonFbxRenderers = visibleRenderers.Where(r => !r.isModelAsset || string.IsNullOrEmpty(r.fbxAssetPath));
            foreach (var r in nonFbxRenderers)
            {
                if (r.triangleCount <= 0) continue;
                float triBudgetRatio = profile.maxTriangles > 0 ? ((float)r.triangleCount / profile.maxTriangles) : 0f;
                float triPercent = triBudgetRatio * 100f;

                bool isHighPoly = r.triangleCount >= profile.singleMeshHeavyTriangles;
                bool isMultiMat = r.materialCount > profile.maxMaterialsPerObject;
                bool isMissingLod = !r.isPartOfLOD && r.triangleCount >= profile.requireLodAboveTriangles;

                List<string> reasons = new List<string>();
                float weight = triBudgetRatio * 1000f;

                if (isHighPoly)
                {
                    reasons.Add($"High Poly ({r.triangleCount:N0} Tris)");
                    weight += 150f;
                }
                if (isMultiMat)
                {
                    reasons.Add($"Multi-Material ({r.materialCount} Mats)");
                    weight += (r.materialCount * 75f);
                }
                if (isMissingLod)
                {
                    reasons.Add("Missing LOD Group");
                    weight += 120f;
                }

                meshCulprits.Add(new CulpritItem
                {
                    name = r.name,
                    instanceId = r.instanceId,
                    type = CulpritType.MeshGeometry,
                    triangleCount = r.triangleCount,
                    vertexCount = r.vertexCount,
                    primaryMetricText = $"{r.triangleCount:N0} Tris ({triPercent:F1}%)",
                    secondaryDetailText = $"{r.vertexCount:N0} Verts • {r.materialCount} Mats",
                    severityScore = weight,
                    isHighPoly = isHighPoly,
                    isMultiMaterial = isMultiMat,
                    isMissingLod = isMissingLod,
                    flaggedReasons = reasons,
                    dependencyNames = r.materialNames,
                    fbxAssetPath = "",
                    fbxName = "",
                    fbxOccurrences = 1,
                    fbxVisibleOccurrences = 1,
                    fbxInstanceRootIds = new List<int> { r.instanceId }
                });
            }

            report.visibleTriangles = (frustumOnly && camSnap.isGameCamera && camSnap.unityStatsTriangles > 0) ? camSnap.unityStatsTriangles : visibleTris;
            report.visibleVertices = (frustumOnly && camSnap.isGameCamera && camSnap.unityStatsVertices > 0) ? camSnap.unityStatsVertices : visibleVerts;
            report.totalSceneTriangles = totalTris;
            report.totalSceneVertices = totalVerts;
            report.uniqueMaterialsCount = uniqueMaterialIds.Count;
            report.fbxModelCount = fbxCount;

            // Texture Score & Texture Culprits (Whole Scene VRAM)
            float totalTexScore = 0f;
            List<CulpritItem> texCulprits = new List<CulpritItem>();

            foreach (var t in textures)
            {
                totalTexScore += t.score;

                if (t.score > 0f)
                {
                    float texBudgetRatio = profile.maxTextureScore > 0 ? (t.score / profile.maxTextureScore) : 0f;
                    float texPercent = texBudgetRatio * 100f;

                    texCulprits.Add(new CulpritItem
                    {
                        name = t.name,
                        instanceId = t.instanceId,
                        type = CulpritType.TextureSize,
                        textureScore = t.score,
                        textureWidth = t.width,
                        textureHeight = t.height,
                        primaryMetricText = $"{t.width}x{t.height} (Score: {t.score:F1} • {texPercent:F1}%)",
                        secondaryDetailText = $"Used in: {(t.usedInMaterials.Count > 0 ? string.Join(", ", t.usedInMaterials.Take(2)) : "Scene")}",
                        severityScore = texBudgetRatio * 1000f,
                        dependencyNames = t.usedInMaterials
                    });
                }
            }

            report.textureAreaScore = totalTexScore;

            // Process Lights
            int totalLights = 0;
            int dirLights = 0;
            int ptLights = 0;
            int sptLights = 0;
            int areaLights = 0;
            int otherLights = 0;

            int rtLights = 0;
            int bkLights = 0;
            int mxLights = 0;
            int shadowLights = 0;
            int visLights = 0;
            int inactLights = 0;

            List<CulpritItem> lightCulprits = new List<CulpritItem>();

            if (lights != null)
            {
                // First pass: tally total scene-wide stats
                foreach (var l in lights)
                {
                    if (!l.isActiveAndEnabled)
                    {
                        inactLights++;
                        continue;
                    }

                    totalLights++;
                    if (l.isVisibleInView) visLights++;

                    switch (l.type)
                    {
                        case LightType.Directional:
                            dirLights++;
                            break;
                        case LightType.Point:
                            ptLights++;
                            break;
                        case LightType.Spot:
                            sptLights++;
                            break;
                        case LightType.Area:
                        case LightType.Disc:
                            areaLights++;
                            break;
                        default:
                            otherLights++;
                            break;
                    }

                    if (l.isRealtime) rtLights++;
                    else if (l.isBaked) bkLights++;
                    else if (l.isMixed) mxLights++;

                    if (l.shadows != LightShadows.None && !l.isBaked)
                    {
                        shadowLights++;
                    }
                }

                // Second pass: Group lights by identical component settings
                // Lights sharing identical type, baking type, shadows, intensity, range, color, spot angle, and active state collapse into a single row
                var groupedLights = lights.GroupBy(l =>
                {
                    string hexCol = ColorUtility.ToHtmlStringRGB(l.color);
                    float r = (l.type == LightType.Directional) ? 0f : (Mathf.Round(l.range * 100f) / 100f);
                    float intens = Mathf.Round(l.intensity * 100f) / 100f;
                    float angle = (l.type == LightType.Spot) ? (Mathf.Round(l.spotAngle * 10f) / 10f) : 0f;
                    string bakeMode = l.isBaked ? "Baked" : (l.isMixed ? "Mixed" : "Realtime");
                    return $"{l.type}_{bakeMode}_{l.shadows}_{intens}_{r}_{hexCol}_{angle}_{l.renderMode}_{l.isActiveAndEnabled}";
                });

                foreach (var group in groupedLights)
                {
                    var rep = group.First();
                    int count = group.Count();
                    int groupVisCount = group.Count(l => l.isVisibleInView);
                    List<int> instanceIds = group.Select(l => l.instanceId).ToList();
                    List<string> lightNames = group.Select(l => l.name).ToList();

                    bool castsShadows = (rep.shadows != LightShadows.None);
                    bool isBaked = rep.isBaked;
                    string shadowText = castsShadows ? $"{rep.shadows} Shadows" : "No Shadows";
                    string bakeMode = isBaked ? "Baked" : (rep.isMixed ? "Mixed" : "Realtime");
                    if (!rep.isActiveAndEnabled) bakeMode = "Inactive";

                    List<string> reasons = new List<string>();
                    float baseSeverity = 0f;

                    if (!rep.isActiveAndEnabled)
                    {
                        baseSeverity = 0f;
                    }
                    else
                    {
                        if (rep.type == LightType.Point && castsShadows && !isBaked)
                        {
                            reasons.Add("Point Light Realtime Shadows (6 Cubemap Passes)");
                            baseSeverity += 120f;
                        }

                        if (rep.type == LightType.Directional && dirLights > profile.maxDirectionalLights)
                        {
                            reasons.Add($"Exceeds Directional Budget ({dirLights} in scene, max {profile.maxDirectionalLights})");
                            baseSeverity += 100f;
                        }

                        if (rep.type == LightType.Spot && castsShadows && !isBaked && shadowLights > profile.maxRealtimeShadowCasters)
                        {
                            reasons.Add($"Exceeds Shadow Casters Budget ({shadowLights} in scene, max {profile.maxRealtimeShadowCasters})");
                            baseSeverity += 70f;
                        }

                        if (rep.range > 25f && rep.type == LightType.Point)
                        {
                            reasons.Add($"Large Point Range ({rep.range:F0}m affects many objects)");
                            baseSeverity += 40f;
                        }
                    }

                    float totalSeverity = baseSeverity * count;

                    string primaryMetric = count > 1
                        ? $"{bakeMode} {rep.type} ({shadowText}) • {count} Lights"
                        : $"{bakeMode} {rep.type} ({shadowText})";

                    string hexCol = ColorUtility.ToHtmlStringRGB(rep.color);
                    string colName = hexCol == "FFFFFF" ? "White" : $"#{hexCol}";

                    string secondaryDetail;
                    if (!rep.isActiveAndEnabled)
                    {
                        secondaryDetail = $"Disabled • Range: {rep.range:F1}m • Intensity: {rep.intensity:F2} • ({count} instances)";
                    }
                    else if (rep.type == LightType.Directional)
                    {
                        secondaryDetail = $"Intensity: {rep.intensity:F2} • Color: {colName} • In View: {groupVisCount}/{count}";
                    }
                    else
                    {
                        secondaryDetail = $"Range: {rep.range:F1}m • Intensity: {rep.intensity:F2} • Color: {colName} • In View: {groupVisCount}/{count}";
                    }

                    string displayName = count > 1 
                        ? $"{count}× {rep.type} Light" 
                        : rep.name;

                    lightCulprits.Add(new CulpritItem
                    {
                        name = displayName,
                        instanceId = rep.instanceId,
                        type = CulpritType.Light,
                        primaryMetricText = primaryMetric,
                        secondaryDetailText = secondaryDetail,
                        severityScore = totalSeverity,
                        flaggedReasons = reasons,
                        dependencyNames = lightNames,
                        lightType = rep.type,
                        lightShadows = rep.shadows,
                        lightRange = rep.range,
                        lightIntensity = rep.intensity,
                        lightIsRealtime = rep.isRealtime,
                        lightIsBaked = rep.isBaked,
                        lightIsMixed = rep.isMixed,
                        lightOccurrences = count,
                        lightVisibleOccurrences = groupVisCount,
                        lightInstanceIds = instanceIds,
                        lightNames = lightNames
                    });
                }
            }

            report.totalLightsCount = totalLights;
            report.directionalLightsCount = dirLights;
            report.pointLightsCount = ptLights;
            report.spotLightsCount = sptLights;
            report.areaLightsCount = areaLights;
            report.otherLightsCount = otherLights;
            report.realtimeLightsCount = rtLights;
            report.bakedLightsCount = bkLights;
            report.mixedLightsCount = mxLights;
            report.shadowCasterLightsCount = shadowLights;
            report.visibleLightsCount = visLights;
            report.inactiveLightsCount = inactLights;

            // Store all detected mesh, texture, and light culprits (ordered by severity score)
            // The display limit (Top N) is dynamically controlled by the designer in the LiveSceneAuditorWindow
            report.culprits.AddRange(meshCulprits.OrderByDescending(c => c.severityScore));
            report.culprits.AddRange(texCulprits.OrderByDescending(c => c.severityScore));
            report.culprits.AddRange(lightCulprits.OrderByDescending(c => c.severityScore));
            report.culprits = report.culprits.OrderByDescending(c => c.severityScore).ToList();

            // Generate Smart Atlasing Suggestions
            report.atlasSuggestions = GenerateAtlasSuggestions(materials);

            // Calculate budget comparison based on Geometry, Materials, FBXs, Textures, and Lights
            float triRatio = profile.maxTriangles > 0 ? ((float)report.visibleTriangles / profile.maxTriangles) : 0f;
            float vertRatio = profile.maxVertices > 0 ? ((float)report.visibleVertices / profile.maxVertices) : 0f;
            float matRatio = profile.maxMaterialsInScene > 0 ? ((float)uniqueMaterialIds.Count / profile.maxMaterialsInScene) : 0f;
            float texRatio = profile.maxTextureScore > 0 ? (totalTexScore / profile.maxTextureScore) : 0f;
            float fbxRatio = profile.maxFbxInScene > 0 ? ((float)fbxCount / profile.maxFbxInScene) : 0f;

            float dirRatio = profile.maxDirectionalLights > 0 ? ((float)report.directionalLightsCount / profile.maxDirectionalLights) : 0f;
            float ptRatio = profile.maxPointLights > 0 ? ((float)report.pointLightsCount / profile.maxPointLights) : 0f;
            float sptRatio = profile.maxSpotLights > 0 ? ((float)report.spotLightsCount / profile.maxSpotLights) : 0f;
            float totLightRatio = profile.maxTotalLights > 0 ? ((float)report.totalLightsCount / profile.maxTotalLights) : 0f;
            float shadowRatio = profile.maxRealtimeShadowCasters > 0 ? ((float)report.shadowCasterLightsCount / profile.maxRealtimeShadowCasters) : 0f;

            float maxRatio = Mathf.Max(triRatio, Mathf.Max(vertRatio, Mathf.Max(matRatio, Mathf.Max(texRatio, Mathf.Max(fbxRatio,
                Mathf.Max(dirRatio, Mathf.Max(ptRatio, Mathf.Max(sptRatio, Mathf.Max(totLightRatio, shadowRatio)))))))));
            report.overallHealthPercentage = Mathf.Round(maxRatio * 100f);
            report.isOverBudget = maxRatio >= 1.0f;

            if (report.isOverBudget)
            {
                report.statusSummary = $"OVER BUDGET ({report.overallHealthPercentage}%)";
            }
            else if (maxRatio >= 0.75f)
            {
                report.statusSummary = $"WARNING ({report.overallHealthPercentage}%)";
            }
            else
            {
                report.statusSummary = $"HEALTHY ({report.overallHealthPercentage}%)";
            }

            // Compute View-Based Stats for each configured vantage point
            if (viewpointSnaps != null && viewpointSnaps.Count > 0)
            {
                foreach (var vp in viewpointSnaps)
                {
                    int vpTris = 0;
                    int vpVerts = 0;
                    int vpRenderersCount = 0;
                    HashSet<int> vpMatIds = new HashSet<int>();
                    List<string> vpMatNames = new List<string>();
                    List<ViewpointContributingObject> vpContributingRends = new List<ViewpointContributingObject>();
                    List<ViewpointContributingLight> vpContributingLights = new List<ViewpointContributingLight>();

                    string topCulpritName = "None";
                    int topCulpritTris = 0;
                    float topCulpritSeverity = 0f;

                    foreach (var r in renderers)
                    {
                        if (!r.isActiveLOD) continue;
                        if (TestPlanesAABBThreadSafe(vp.frustumPlanes, r.worldBounds))
                        {
                            vpTris += r.triangleCount;
                            vpVerts += r.vertexCount;
                            vpRenderersCount++;

                            for (int m = 0; m < r.materialInstanceIds.Count; m++)
                            {
                                int matId = r.materialInstanceIds[m];
                                if (!vpMatIds.Contains(matId))
                                {
                                    vpMatIds.Add(matId);
                                    if (m < r.materialNames.Count)
                                    {
                                        vpMatNames.Add(r.materialNames[m]);
                                    }
                                }
                            }

                            vpContributingRends.Add(new ViewpointContributingObject
                            {
                                instanceId = r.instanceId,
                                name = r.name,
                                triangleCount = r.triangleCount,
                                vertexCount = r.vertexCount,
                                materialCount = r.materialCount,
                                materialInstanceIds = r.materialInstanceIds != null ? new List<int>(r.materialInstanceIds) : new List<int>(),
                                materialNames = r.materialNames != null ? new List<string>(r.materialNames) : new List<string>(),
                                textureInstanceIds = r.textureInstanceIds != null ? new List<int>(r.textureInstanceIds) : new List<int>(),
                                textureNames = r.textureNames != null ? new List<string>(r.textureNames) : new List<string>(),
                                isModelAsset = r.isModelAsset,
                                fbxName = r.fbxName,
                                fbxAssetPath = r.fbxAssetPath
                            });

                            if (r.triangleCount > topCulpritTris)
                            {
                                topCulpritTris = r.triangleCount;
                                topCulpritName = !string.IsNullOrEmpty(r.fbxName) ? r.fbxName : r.name;
                                float triBudgetRatio = profile.maxTriangles > 0 ? ((float)r.triangleCount / profile.maxTriangles) : 0f;
                                topCulpritSeverity = triBudgetRatio * 1000f;
                            }
                        }
                    }

                    // Sort contributing meshes by polygon count (highest impact first)
                    vpContributingRends.Sort((a, b) => b.triangleCount.CompareTo(a.triangleCount));

                    // Tally visible lights in this viewpoint's frustum
                    int vpVisLights = 0;
                    int vpDirLights = 0;
                    int vpPtLights = 0;
                    int vpSptLights = 0;
                    int vpShadowCasters = 0;

                    if (lights != null)
                    {
                        foreach (var l in lights)
                        {
                            if (!l.isActiveAndEnabled) continue;

                            bool isVis = false;
                            if (l.type == LightType.Directional)
                            {
                                isVis = true;
                            }
                            else
                            {
                                isVis = TestSphereFrustum(l.position, Mathf.Max(0.1f, l.range), vp.frustumPlanes);
                            }

                            if (isVis)
                            {
                                vpVisLights++;
                                if (l.type == LightType.Directional) vpDirLights++;
                                else if (l.type == LightType.Point) vpPtLights++;
                                else if (l.type == LightType.Spot) vpSptLights++;

                                if (l.shadows != LightShadows.None && !l.isBaked)
                                {
                                    vpShadowCasters++;
                                }

                                vpContributingLights.Add(new ViewpointContributingLight
                                {
                                    instanceId = l.instanceId,
                                    name = l.name,
                                    type = l.type,
                                    shadows = l.shadows,
                                    isRealtime = l.isRealtime,
                                    isBaked = l.isBaked,
                                    isMixed = l.isMixed,
                                    range = l.range,
                                    intensity = l.intensity
                                });
                            }
                        }
                    }

                    // Calculate budget health for this viewpoint
                    float vpTriRatio = profile.maxTriangles > 0 ? ((float)vpTris / profile.maxTriangles) : 0f;
                    float vpVertRatio = profile.maxVertices > 0 ? ((float)vpVerts / profile.maxVertices) : 0f;
                    float vpMatRatio = profile.maxMaterialsInScene > 0 ? ((float)vpMatIds.Count / profile.maxMaterialsInScene) : 0f;
                    float vpDirRatio = profile.maxDirectionalLights > 0 ? ((float)vpDirLights / profile.maxDirectionalLights) : 0f;
                    float vpPtRatio = profile.maxPointLights > 0 ? ((float)vpPtLights / profile.maxPointLights) : 0f;
                    float vpSptRatio = profile.maxSpotLights > 0 ? ((float)vpSptLights / profile.maxSpotLights) : 0f;
                    float vpTotLightRatio = profile.maxTotalLights > 0 ? ((float)vpVisLights / profile.maxTotalLights) : 0f;
                    float vpShadowRatio = profile.maxRealtimeShadowCasters > 0 ? ((float)vpShadowCasters / profile.maxRealtimeShadowCasters) : 0f;

                    float vpMaxRatio = Mathf.Max(vpTriRatio, Mathf.Max(vpVertRatio, Mathf.Max(vpMatRatio,
                        Mathf.Max(vpDirRatio, Mathf.Max(vpPtRatio, Mathf.Max(vpSptRatio, Mathf.Max(vpTotLightRatio, vpShadowRatio)))))));

                    float vpHealthPct = Mathf.Round(vpMaxRatio * 100f);
                    bool vpIsOver = vpMaxRatio >= 1.0f;
                    string vpStatus = vpIsOver ? $"OVER BUDGET ({vpHealthPct:F0}%)" : (vpMaxRatio >= 0.75f ? $"WARNING ({vpHealthPct:F0}%)" : $"PASS ({vpHealthPct:F0}%)");

                    report.viewpointStats.Add(new ViewpointAuditStat
                    {
                        viewpointName = vp.name,
                        transformInstanceId = vp.instanceId,
                        position = vp.position,
                        rotationEuler = vp.rotationEuler,
                        visibleTriangles = vpTris,
                        visibleVertices = vpVerts,
                        visibleRenderersCount = vpRenderersCount,
                        uniqueMaterialsCount = vpMatIds.Count,
                        materialNames = vpMatNames,
                        visibleLightsCount = vpVisLights,
                        directionalLightsCount = vpDirLights,
                        pointLightsCount = vpPtLights,
                        spotLightsCount = vpSptLights,
                        shadowCasterLightsCount = vpShadowCasters,
                        healthPercentage = vpHealthPct,
                        isOverBudget = vpIsOver,
                        statusSummary = vpStatus,
                        topCulpritName = topCulpritName,
                        topCulpritTris = topCulpritTris,
                        topCulpritSeverity = topCulpritSeverity,
                        contributingRenderers = vpContributingRends,
                        contributingLights = vpContributingLights
                    });
                }
            }

            return report;
        }

        /// <summary>
        /// Pure C# thread-safe AABB vs Frustum Planes test.
        /// </summary>
        public static bool TestPlanesAABBThreadSafe(Plane[] planes, Bounds bounds)
        {
            if (planes == null || planes.Length == 0) return true;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;

            for (int i = 0; i < planes.Length; i++)
            {
                Vector3 normal = planes[i].normal;
                float distance = planes[i].distance;

                // Pick the corner of the bounding box that is furthest along the normal
                Vector3 p = new Vector3(
                    normal.x > 0f ? max.x : min.x,
                    normal.y > 0f ? max.y : min.y,
                    normal.z > 0f ? max.z : min.z
                );

                // If this corner is behind the plane, the whole box is outside
                if ((normal.x * p.x + normal.y * p.y + normal.z * p.z + distance) < 0f)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
