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

        private class MaterialSnapshot
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

        // ==========================================
        // HIGH PERFORMANCE O(1) ASSET & MESH CACHES
        // ==========================================
        private struct CachedMeshData
        {
            public int triCount;
            public int vertCount;
            public string assetPath;
            public string fbxName;
            public bool isModelAsset;
        }

        private struct CachedTextureData
        {
            public int instanceId;
            public string name;
            public int width;
            public int height;
            public float score;
        }

        private struct CachedMaterialData
        {
            public int instanceId;
            public string name;
            public string shaderName;
            public List<int> textureInstanceIds;
            public List<string> textureNames;
            public int mainTexId;
            public string mainTexName;
            public int mainTexWidth;
            public int mainTexHeight;
            public float mainTexScore;
        }

        private static readonly Dictionary<int, CachedMeshData> _meshCache = new Dictionary<int, CachedMeshData>();
        private static readonly Dictionary<int, CachedTextureData> _textureCache = new Dictionary<int, CachedTextureData>();
        private static readonly Dictionary<int, CachedMaterialData> _materialCache = new Dictionary<int, CachedMaterialData>();

        /// <summary>
        /// Clears all cached mesh geometry and material reflection data.
        /// Automatically called when scene assets or properties change.
        /// </summary>
        public static void ClearCaches()
        {
            _meshCache.Clear();
            _textureCache.Clear();
            _materialCache.Clear();
        }

        private static CancellationTokenSource _activeCts;
        private static SynchronizationContext _mainThreadContext;
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadDispatchQueue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        private static long _activeScanGen = 0;

        private static int _lastGameCameraTris = 0;
        private static int _lastGameCameraVerts = 0;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            _mainThreadContext = SynchronizationContext.Current;
            Camera.onPostRender -= OnGlobalPostRender;
            Camera.onPostRender += OnGlobalPostRender;

            // Register main thread dispatch pump on Editor updates
            EditorApplication.update -= ProcessMainThreadDispatchQueue;
            EditorApplication.update += ProcessMainThreadDispatchQueue;

            // Invalidate caches when project assets change, reimport, or undo/redo occurs
            EditorApplication.projectChanged -= ClearCaches;
            EditorApplication.projectChanged += ClearCaches;

            Undo.undoRedoPerformed -= ClearCaches;
            Undo.undoRedoPerformed += ClearCaches;

            Undo.postprocessModifications -= OnPostprocessModifications;
            Undo.postprocessModifications += OnPostprocessModifications;

            UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;

            AssemblyReloadEvents.beforeAssemblyReload -= CleanupViewpointCamera;
            AssemblyReloadEvents.beforeAssemblyReload += CleanupViewpointCamera;

            EditorApplication.quitting -= CleanupViewpointCamera;
            EditorApplication.quitting += CleanupViewpointCamera;
        }

        public static void ProcessMainThreadDispatchQueue()
        {
            while (_mainThreadDispatchQueue.TryDequeue(out var action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Live Scene Auditor] Dispatch queue error: {ex}");
                }
            }
        }

        public static void CancelCurrentScan()
        {
            Interlocked.Increment(ref _activeScanGen);
            if (_activeCts != null)
            {
                try { _activeCts.Cancel(); } catch {}
                try { _activeCts.Dispose(); } catch {}
                _activeCts = null;
            }
        }

        private static void CleanupViewpointCamera()
        {
            if (_persistentViewpointCam != null && _persistentViewpointCam.gameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_persistentViewpointCam.gameObject);
                _persistentViewpointCam = null;
            }
        }

        private static void OnActiveSceneChanged(UnityEngine.SceneManagement.Scene s1, UnityEngine.SceneManagement.Scene s2)
        {
            ClearCaches();
        }

        private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            ClearCaches();
            return modifications;
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

        private static Camera _persistentViewpointCam;

        private static Camera GetOrCreateViewpointCamera()
        {
            if (_persistentViewpointCam == null)
            {
                GameObject go = EditorUtility.CreateGameObjectWithHideFlags(
                    "__Auditor_PersistentViewCam", 
                    HideFlags.HideAndDontSave, 
                    typeof(Camera));
                _persistentViewpointCam = go.GetComponent<Camera>();
                _persistentViewpointCam.enabled = false;
                _persistentViewpointCam.cullingMask = 0;
            }
            return _persistentViewpointCam;
        }

        private static List<ViewpointSnapshot> CaptureViewpointSnapshots(List<Transform> viewpoints, Camera refCam)
        {
            List<ViewpointSnapshot> list = new List<ViewpointSnapshot>();
            if (viewpoints == null || viewpoints.Count == 0) return list;

            try
            {
                Camera cam = GetOrCreateViewpointCamera();
                if (refCam != null)
                {
                    cam.CopyFrom(refCam);
                }
                else
                {
                    cam.fieldOfView = 60f;
                    cam.nearClipPlane = 0.3f;
                    cam.farClipPlane = 1000f;
                }
                cam.enabled = false;
                cam.cullingMask = 0;

                foreach (var vp in viewpoints)
                {
                    if (vp == null) continue;
                    cam.transform.position = vp.position;
                    cam.transform.rotation = vp.rotation;

                    Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
                    list.Add(new ViewpointSnapshot
                    {
                        name = vp.name,
                        instanceId = vp.gameObject.GetInstanceID(),
                        position = vp.position,
                        rotationEuler = vp.rotation.eulerAngles,
                        frustumPlanes = planes,
                        fov = cam.fieldOfView
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Live Scene Auditor] Failed to capture viewpoint frustums: {ex.Message}");
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

            // Increment scan generation and cancel any pending async scan
            long thisScanGen = Interlocked.Increment(ref _activeScanGen);
            if (_activeCts != null)
            {
                try { _activeCts.Cancel(); } catch {}
                try { _activeCts.Dispose(); } catch {}
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
            CaptureSceneRenderData(includeInactive, camSnap, out List<RendererSnapshot> renderers, out List<TextureSnapshot> textures, out List<MaterialSnapshot> materials, out int fbxCount);
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
                // Asynchronous background processing with generation-safe dispatch
                _activeCts = new CancellationTokenSource();
                CancellationToken token = _activeCts.Token;
                if (_mainThreadContext == null || SynchronizationContext.Current != null)
                {
                    _mainThreadContext = SynchronizationContext.Current ?? _mainThreadContext;
                }
                SynchronizationContext context = _mainThreadContext;

                Task.Run(() =>
                {
                    try
                    {
                        if (token.IsCancellationRequested || thisScanGen != Interlocked.Read(ref _activeScanGen))
                        {
                            return;
                        }

                        SceneAuditReport report = ProcessSnapshot(renderers, textures, materials, lights, camSnap, frustumOnly, profile, fbxCount, viewpointSnaps);
                        sw.Stop();
                        report.scanDurationMs = (float)sw.Elapsed.TotalMilliseconds;

                        if (token.IsCancellationRequested || thisScanGen != Interlocked.Read(ref _activeScanGen))
                        {
                            return;
                        }

                        _mainThreadDispatchQueue.Enqueue(() =>
                        {
                            if (thisScanGen == Interlocked.Read(ref _activeScanGen))
                            {
                                onCompleted?.Invoke(report);
                            }
                        });

                        if (context != null)
                        {
                            try
                            {
                                context.Post(_ => ProcessMainThreadDispatchQueue(), null);
                            }
                            catch {}
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Live Scene Auditor] Error during async scan: {ex}");
                        _mainThreadDispatchQueue.Enqueue(() =>
                        {
                            if (thisScanGen == Interlocked.Read(ref _activeScanGen))
                            {
                                onCompleted?.Invoke(null);
                            }
                        });

                        if (context != null)
                        {
                            try
                            {
                                context.Post(_ => ProcessMainThreadDispatchQueue(), null);
                            }
                            catch {}
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

        private static Mesh GetRendererSharedMesh(Renderer r)
        {
            if (r is MeshRenderer)
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            if (r is SkinnedMeshRenderer smr)
            {
                return smr.sharedMesh;
            }
            return null;
        }

        private static CachedMeshData GetOrComputeMeshData(Mesh mesh)
        {
            if (mesh == null) return default;
            int id = mesh.GetInstanceID();
            if (_meshCache.TryGetValue(id, out var data))
            {
                return data;
            }

            string path = AssetDatabase.GetAssetPath(mesh);
            bool isModel = !string.IsNullOrEmpty(path) && IsModelAssetPath(path);
            string fbxName = isModel ? System.IO.Path.GetFileNameWithoutExtension(path) : "";

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

            int vertCount = 0;
            try
            {
                vertCount = mesh.vertexCount;
            }
            catch {}

            data = new CachedMeshData
            {
                triCount = triCount,
                vertCount = vertCount,
                assetPath = path ?? "",
                fbxName = fbxName ?? "",
                isModelAsset = isModel
            };

            _meshCache[id] = data;
            return data;
        }

        private static CachedMaterialData GetOrComputeMaterialData(Material mat)
        {
            if (mat == null) return default;
            int id = mat.GetInstanceID();
            if (_materialCache.TryGetValue(id, out var data))
            {
                return data;
            }

            string shaderName = "Standard";
            try
            {
                if (mat.shader != null) shaderName = mat.shader.name;
            }
            catch {}

            List<int> texIds = new List<int>();
            List<string> texNames = new List<string>();
            HashSet<int> seenTex = new HashSet<int>();

            try
            {
                string[] propNames = mat.GetTexturePropertyNames();
                if (propNames != null)
                {
                    foreach (var prop in propNames)
                    {
                        Texture tex = mat.GetTexture(prop);
                        if (tex != null && !IsIgnoredAsset(tex) && seenTex.Add(tex.GetInstanceID()))
                        {
                            int tId = tex.GetInstanceID();
                            texIds.Add(tId);
                            texNames.Add(tex.name);

                            if (!_textureCache.ContainsKey(tId))
                            {
                                float score = ((float)tex.width / 512f) * ((float)tex.height / 512f);
                                _textureCache[tId] = new CachedTextureData
                                {
                                    instanceId = tId,
                                    name = tex.name,
                                    width = tex.width,
                                    height = tex.height,
                                    score = score
                                };
                            }
                        }
                    }
                }
            }
            catch {}

            Texture mainTex = null;
            try { mainTex = mat.mainTexture; } catch {}
            if (mainTex == null)
            {
                try
                {
                    if (mat.HasProperty("_BaseMap")) mainTex = mat.GetTexture("_BaseMap");
                    else if (mat.HasProperty("_MainTex")) mainTex = mat.GetTexture("_MainTex");
                }
                catch {}
            }

            int mainTexId = 0;
            string mainTexName = "(No Texture / Flat Color)";
            int mainTexW = 0;
            int mainTexH = 0;
            float mainTexScore = 0f;

            if (mainTex != null && !IsIgnoredAsset(mainTex))
            {
                mainTexId = mainTex.GetInstanceID();
                mainTexName = mainTex.name;
                mainTexW = mainTex.width;
                mainTexH = mainTex.height;
                mainTexScore = ((float)mainTex.width / 512f) * ((float)mainTex.height / 512f);

                if (!_textureCache.ContainsKey(mainTexId))
                {
                    _textureCache[mainTexId] = new CachedTextureData
                    {
                        instanceId = mainTexId,
                        name = mainTexName,
                        width = mainTexW,
                        height = mainTexH,
                        score = mainTexScore
                    };
                }
            }

            data = new CachedMaterialData
            {
                instanceId = id,
                name = mat.name,
                shaderName = shaderName,
                textureInstanceIds = texIds,
                textureNames = texNames,
                mainTexId = mainTexId,
                mainTexName = mainTexName,
                mainTexWidth = mainTexW,
                mainTexHeight = mainTexH,
                mainTexScore = mainTexScore
            };

            _materialCache[id] = data;
            return data;
        }

        private static void CaptureSceneRenderData(
            bool includeInactive, 
            CameraSnapshot camSnap,
            out List<RendererSnapshot> renderers,
            out List<TextureSnapshot> textures,
            out List<MaterialSnapshot> materials,
            out int fbxCount)
        {
            renderers = new List<RendererSnapshot>();
            textures = new List<TextureSnapshot>();
            materials = new List<MaterialSnapshot>();
            fbxCount = 0;

            // 1. Identify LOD groups first with strict active and inactive sets
            LODGroup[] lodGroups = GameObject.FindObjectsOfType<LODGroup>(includeInactive);
            HashSet<Renderer> activeLodRenderers = new HashSet<Renderer>();
            HashSet<Renderer> inactiveLodRenderers = new HashSet<Renderer>();

            if (lodGroups != null && lodGroups.Length > 0)
            {
                foreach (var lodGroup in lodGroups)
                {
                    if (lodGroup == null) continue;
                    LOD[] lods = lodGroup.GetLODs();
                    if (lods == null || lods.Length == 0) continue;

                    int activeLodIndex = -1;
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
                        activeLodIndex = 0;
                    }

                    for (int i = 0; i < lods.Length; i++)
                    {
                        bool isActive = (i == activeLodIndex);
                        foreach (var r in lods[i].renderers)
                        {
                            if (r == null) continue;
                            if (isActive) activeLodRenderers.Add(r);
                            else inactiveLodRenderers.Add(r);
                        }
                    }
                }
            }

            // 2. Query ALL scene renderers EXACTLY ONCE across the entire scan
            Renderer[] allSceneRenderers = GameObject.FindObjectsOfType<Renderer>(true);
            if (allSceneRenderers == null || allSceneRenderers.Length == 0) return;

            HashSet<string> uniqueFbxPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<int>> fbxToAllSceneRootIds = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<int, int> goRootIdMap = new Dictionary<int, int>();
            Dictionary<int, HashSet<string>> texToMaterials = new Dictionary<int, HashSet<string>>();

            // PASS 1: Build FBX occurrence mapping and populate mesh/material/texture caches
            foreach (var r in allSceneRenderers)
            {
                if (r == null || IsIgnoredRenderer(r)) continue;

                Mesh m = GetRendererSharedMesh(r);
                if (m != null)
                {
                    CachedMeshData meshData = GetOrComputeMeshData(m);
                    if (meshData.isModelAsset && !string.IsNullOrEmpty(meshData.assetPath))
                    {
                        uniqueFbxPaths.Add(meshData.assetPath);

                        if (!fbxToAllSceneRootIds.TryGetValue(meshData.assetPath, out var rootSet))
                        {
                            rootSet = new HashSet<int>();
                            fbxToAllSceneRootIds[meshData.assetPath] = rootSet;
                        }

                        int goId = r.gameObject.GetInstanceID();
                        if (!goRootIdMap.TryGetValue(goId, out int rootId))
                        {
                            GameObject rootGo = PrefabUtility.GetNearestPrefabInstanceRoot(r.gameObject);
                            rootId = rootGo != null ? rootGo.GetInstanceID() : goId;
                            goRootIdMap[goId] = rootId;
                        }

                        rootSet.Add(rootId);
                    }
                }

                // Gather all textures used across all scene materials (for scene-wide texture memory audit)
                Material[] mats = r.sharedMaterials;
                if (mats != null)
                {
                    foreach (var mat in mats)
                    {
                        if (mat == null || IsIgnoredAsset(mat)) continue;
                        CachedMaterialData matData = GetOrComputeMaterialData(mat);
                        for (int t = 0; t < matData.textureInstanceIds.Count; t++)
                        {
                            int tId = matData.textureInstanceIds[t];
                            if (!texToMaterials.TryGetValue(tId, out var matSet))
                            {
                                matSet = new HashSet<string>();
                                texToMaterials[tId] = matSet;
                            }
                            matSet.Add(matData.name);
                        }
                    }
                }
            }

            fbxCount = uniqueFbxPaths.Count;

            // Build TextureSnapshots list
            foreach (var kvp in texToMaterials)
            {
                int texId = kvp.Key;
                if (_textureCache.TryGetValue(texId, out CachedTextureData texData))
                {
                    textures.Add(new TextureSnapshot
                    {
                        instanceId = texData.instanceId,
                        name = texData.name,
                        width = texData.width,
                        height = texData.height,
                        score = texData.score,
                        usedInMaterials = kvp.Value.ToList()
                    });
                }
            }

            // PASS 2: Collect active Renderers and Materials according to includeInactive and LODs
            Dictionary<int, MaterialSnapshot> matMap = new Dictionary<int, MaterialSnapshot>();

            foreach (var r in allSceneRenderers)
            {
                if (r == null || !r.enabled) continue;
                if (!r.gameObject.activeInHierarchy && !includeInactive) continue;
                if (IsIgnoredRenderer(r)) continue;

                Mesh mesh = GetRendererSharedMesh(r);
                if (mesh == null) continue;

                CachedMeshData meshData = GetOrComputeMeshData(mesh);

                // Strict LOD rule: If part of an LOD group and NOT in the active set, skip
                bool isPartOfLod = activeLodRenderers.Contains(r) || inactiveLodRenderers.Contains(r);
                if (inactiveLodRenderers.Contains(r) && !activeLodRenderers.Contains(r))
                {
                    continue;
                }

                int occurrences = 0;
                List<int> rootIds = null;
                int goId = r.gameObject.GetInstanceID();
                if (!goRootIdMap.TryGetValue(goId, out int rootInstanceId))
                {
                    GameObject rootGo = PrefabUtility.GetNearestPrefabInstanceRoot(r.gameObject);
                    rootInstanceId = rootGo != null ? rootGo.GetInstanceID() : goId;
                    goRootIdMap[goId] = rootInstanceId;
                }

                if (meshData.isModelAsset && fbxToAllSceneRootIds.TryGetValue(meshData.assetPath, out var rRoots))
                {
                    occurrences = rRoots.Count;
                    rootIds = rRoots.ToList();
                }

                Material[] sharedMats = r.sharedMaterials;
                List<int> matIds = new List<int>();
                List<string> matNames = new List<string>();
                List<int> texIds = new List<int>();
                List<string> texNames = new List<string>();
                HashSet<int> seenTex = new HashSet<int>();

                int rId = r.gameObject.GetInstanceID();
                string rName = r.gameObject.name;

                if (sharedMats != null)
                {
                    foreach (var m in sharedMats)
                    {
                        if (m == null || IsIgnoredAsset(m)) continue;

                        CachedMaterialData matData = GetOrComputeMaterialData(m);
                        matIds.Add(matData.instanceId);
                        matNames.Add(matData.name);

                        for (int t = 0; t < matData.textureInstanceIds.Count; t++)
                        {
                            int tId = matData.textureInstanceIds[t];
                            if (seenTex.Add(tId))
                            {
                                texIds.Add(tId);
                                texNames.Add(matData.textureNames[t]);
                            }
                        }

                        // Add to MaterialSnapshot
                        if (!matMap.TryGetValue(matData.instanceId, out MaterialSnapshot snap))
                        {
                            snap = new MaterialSnapshot
                            {
                                materialInstanceId = matData.instanceId,
                                materialName = matData.name,
                                shaderName = matData.shaderName,
                                textureInstanceId = matData.mainTexId,
                                textureName = matData.mainTexName,
                                textureWidth = matData.mainTexWidth,
                                textureHeight = matData.mainTexHeight,
                                textureScore = matData.mainTexScore,
                                rendererInstanceIds = new List<int>(),
                                rendererNames = new List<string>(),
                                visibleRendererInstanceIds = new List<int>(),
                                visibleRendererNames = new List<string>()
                            };
                            matMap[matData.instanceId] = snap;
                        }

                        if (!snap.rendererInstanceIds.Contains(rId))
                        {
                            snap.rendererInstanceIds.Add(rId);
                            snap.rendererNames.Add(rName);
                        }
                    }
                }

                renderers.Add(new RendererSnapshot
                {
                    instanceId = rId,
                    name = rName,
                    worldBounds = r.bounds,
                    triangleCount = meshData.triCount,
                    vertexCount = meshData.vertCount,
                    isPartOfLOD = isPartOfLod,
                    isActiveLOD = true,
                    materialCount = matIds.Count,
                    materialInstanceIds = matIds,
                    materialNames = matNames,
                    textureInstanceIds = texIds,
                    textureNames = texNames,
                    isModelAsset = meshData.isModelAsset,
                    fbxAssetPath = meshData.isModelAsset ? meshData.assetPath : "",
                    fbxName = meshData.fbxName,
                    modelInstanceRootId = rootInstanceId,
                    fbxSceneOccurrences = occurrences,
                    fbxSceneRootIds = rootIds ?? new List<int>()
                });
            }

            materials.AddRange(matMap.Values);
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

            // Link visible renderers to material snapshots (performed completely off the Main Thread)
            if (materials != null && materials.Count > 0)
            {
                Dictionary<int, MaterialSnapshot> matLookup = new Dictionary<int, MaterialSnapshot>(materials.Count);
                for (int i = 0; i < materials.Count; i++)
                {
                    matLookup[materials[i].materialInstanceId] = materials[i];
                }

                foreach (var r in renderers)
                {
                    if (!r.isActiveLOD) continue;
                    bool isVisInCam = camSnap.isValid && TestPlanesAABBThreadSafe(camSnap.frustumPlanes, r.worldBounds);

                    if (isVisInCam)
                    {
                        for (int m = 0; m < r.materialInstanceIds.Count; m++)
                        {
                            int mId = r.materialInstanceIds[m];
                            if (matLookup.TryGetValue(mId, out var matSnap))
                            {
                                if (!matSnap.visibleRendererInstanceIds.Contains(r.instanceId))
                                {
                                    matSnap.visibleRendererInstanceIds.Add(r.instanceId);
                                    matSnap.visibleRendererNames.Add(r.name);
                                }
                            }
                        }
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
            report.totalTexturesCount = textures.Count;

            // Group textures by resolution (width x height) to calculate count with respect to size
            report.textureSizeBreakdown = textures
                .GroupBy(t => $"{t.width}x{t.height}")
                .Select(g =>
                {
                    var first = g.First();
                    int count = g.Count();
                    float sumScore = g.Sum(x => x.score);
                    float pct = textures.Count > 0 ? ((float)count / textures.Count * 100f) : 0f;
                    return new TextureSizeBucket
                    {
                        resolution = $"{first.width}x{first.height}",
                        width = first.width,
                        height = first.height,
                        count = count,
                        totalScore = sumScore,
                        percentageOfTotal = pct,
                        textureNames = g.Select(x => x.name).Distinct().ToList(),
                        textureInstanceIds = g.Select(x => x.instanceId).ToList()
                    };
                })
                .OrderByDescending(b => b.width * b.height)
                .ThenByDescending(b => b.count)
                .ToList();

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
            report.isOverBudget = maxRatio > 1.0f;

            if (report.isOverBudget)
            {
                report.statusSummary = $"OVER BUDGET ({report.overallHealthPercentage}%)";
            }
            else if (maxRatio >= 1.0f)
            {
                report.statusSummary = $"FULLY USED BUDGET ({report.overallHealthPercentage}%)";
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
                    bool vpIsOver = vpMaxRatio > 1.0f;
                    string vpStatus = vpIsOver 
                        ? $"OVER BUDGET ({vpHealthPct:F0}%)" 
                        : (vpMaxRatio >= 1.0f 
                            ? $"FULLY USED ({vpHealthPct:F0}%)" 
                            : (vpMaxRatio >= 0.75f ? $"WARNING ({vpHealthPct:F0}%)" : $"PASS ({vpHealthPct:F0}%)"));

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
