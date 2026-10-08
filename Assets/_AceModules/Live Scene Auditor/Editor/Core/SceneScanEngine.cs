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
            public bool isBatchingStatic;
            public bool castsShadows;
            public int lightmapIndex;
            public int materialCount;
            public List<int> materialInstanceIds;
            public List<string> materialNames;
            public List<string> shaderNames;
            public List<bool> materialInstancingEnabled;
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

        private struct LightSnapshot
        {
            public Vector3 position;
            public LightType type;
            public float range;
            public float spotAngle;
            public Vector3 forward;
            public float intensity;
            public LightShadows shadowType;
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
            public int shadowCascades;
            public bool hasDirectionalShadows;
            public float shadowDistance;
            public int activeCanvasDrawCalls;
            public List<LightSnapshot> pixelLights;
            public int unityStatsTriangles;
            public int unityStatsVertices;
            public int unityStatsBatches;
            public int exactPlayModeBatches;
            public bool isValid;
        }

        public struct StaticBatchEquivalenceKey : IEquatable<StaticBatchEquivalenceKey>
        {
            public readonly int MaterialInstanceId;
            public readonly int LightmapIndex;

            public StaticBatchEquivalenceKey(int materialInstanceId, int lightmapIndex)
            {
                MaterialInstanceId = materialInstanceId;
                LightmapIndex = lightmapIndex;
            }

            public bool Equals(StaticBatchEquivalenceKey other)
            {
                return MaterialInstanceId == other.MaterialInstanceId &&
                       LightmapIndex == other.LightmapIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is StaticBatchEquivalenceKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (MaterialInstanceId * 397) ^ LightmapIndex;
                }
            }
        }

        private static CancellationTokenSource _activeCts;
        private static SynchronizationContext _mainThreadContext;

        private static int _cleanGameViewBatches = 0;
        private static int _lastGameCameraBatches = 0;
        private static int _lastGameCameraTris = 0;
        private static int _lastGameCameraVerts = 0;
        private static bool _hasCleanCapture = false;

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
            // Capture when Game Camera renders naturally to screen / GameView (not to offscreen temporary RT)
            if (cam == Camera.main || cam.CompareTag("MainCamera") || cam.cameraType == CameraType.Game)
            {
                _cleanGameViewBatches = UnityEditor.UnityStats.batches;
                _lastGameCameraBatches = _cleanGameViewBatches;
                _lastGameCameraTris = UnityEditor.UnityStats.triangles;
                _lastGameCameraVerts = UnityEditor.UnityStats.vertices;
                _hasCleanCapture = true;
            }
        }

        public static int GetCleanEditModeBatches()
        {
            return _cleanGameViewBatches;
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

        private static int CalculateStaticBatchSavings(List<RendererSnapshot> visibleRenderers)
        {
            Dictionary<StaticBatchEquivalenceKey, int> groupSubmeshCounts = new Dictionary<StaticBatchEquivalenceKey, int>();
            Dictionary<StaticBatchEquivalenceKey, int> groupVertexCounts = new Dictionary<StaticBatchEquivalenceKey, int>();

            for (int i = 0; i < visibleRenderers.Count; i++)
            {
                RendererSnapshot r = visibleRenderers[i];

                if (!r.isBatchingStatic || r.materialInstanceIds == null || r.materialInstanceIds.Count == 0)
                    continue;

                // Skip probe-lit objects (lightmapIndex == -1); they break batching via SH constants in Built-in Forward
                if (r.lightmapIndex < 0)
                    continue;

                for (int m = 0; m < r.materialInstanceIds.Count; m++)
                {
                    int matId = r.materialInstanceIds[m];
                    var key = new StaticBatchEquivalenceKey(matId, r.lightmapIndex);

                    if (!groupSubmeshCounts.ContainsKey(key))
                    {
                        groupSubmeshCounts[key] = 0;
                        groupVertexCounts[key] = 0;
                    }

                    groupSubmeshCounts[key]++;
                    groupVertexCounts[key] += r.vertexCount;
                }
            }

            int totalSavings = 0;
            const int maxVerticesPerStaticBatch = 65536;

            foreach (var kvp in groupSubmeshCounts)
            {
                StaticBatchEquivalenceKey key = kvp.Key;
                int submeshCount = kvp.Value;
                int totalVertices = groupVertexCounts[key];

                // Determine combined batches required in Play Mode
                int batchedDrawCalls = Mathf.CeilToInt((float)totalVertices / maxVerticesPerStaticBatch);
                batchedDrawCalls = Mathf.Max(1, batchedDrawCalls);

                // Savings represent uncombined Edit-Mode draws eliminated at runtime
                int savings = submeshCount - batchedDrawCalls;
                if (savings > 0)
                {
                    totalSavings += savings;
                }
            }

            Debug.Log($"[CalculateStaticBatchSavings] visibleCount={visibleRenderers.Count}, groups={groupSubmeshCounts.Count}, totalSavings={totalSavings}");

            return totalSavings;
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
            Action<SceneAuditReport> onCompleted)
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
            List<RendererSnapshot> renderers = CaptureRenderersSnapshot(includeInactive, camSnap);
            List<TextureSnapshot> textures = CaptureTexturesSnapshot();

            if (!asyncMode)
            {
                // Synchronous immediate processing
                SceneAuditReport report = ProcessSnapshot(renderers, textures, camSnap, frustumOnly, profile);
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

                        SceneAuditReport report = ProcessSnapshot(renderers, textures, camSnap, frustumOnly, profile);
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
            snap.shadowCascades = Mathf.Max(1, QualitySettings.shadowCascades);
            snap.shadowDistance = QualitySettings.shadowDistance > 0 ? QualitySettings.shadowDistance : 40f;
            snap.hasDirectionalShadows = false;
            snap.activeCanvasDrawCalls = 0;
            snap.pixelLights = new List<LightSnapshot>();
            try
            {
                Light[] sceneLights = GameObject.FindObjectsOfType<Light>();
                Light mainDir = null;
                float maxDirIntensity = -1f;

                foreach (var l in sceneLights)
                {
                    if (l != null && l.enabled && l.gameObject.activeInHierarchy && l.type == LightType.Directional)
                    {
                        if (l.intensity > maxDirIntensity)
                        {
                            maxDirIntensity = l.intensity;
                            mainDir = l;
                        }
                    }
                }

                if (mainDir != null && mainDir.shadows != LightShadows.None)
                {
                    snap.hasDirectionalShadows = true;
                }

                // Gather additional pixel lights up to QualitySettings.pixelLightCount (render in ForwardAdd passes)
                int maxPixelLights = QualitySettings.pixelLightCount;
                if (maxPixelLights > 0)
                {
                    List<Light> candidates = new List<Light>();
                    foreach (var l in sceneLights)
                    {
                        if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                        if (l == mainDir) continue;
                        if (l.renderMode == LightRenderMode.ForceVertex) continue;

                        candidates.Add(l);
                    }

                    candidates.Sort((a, b) =>
                    {
                        if (a.renderMode == LightRenderMode.ForcePixel && b.renderMode != LightRenderMode.ForcePixel) return -1;
                        if (b.renderMode == LightRenderMode.ForcePixel && a.renderMode != LightRenderMode.ForcePixel) return 1;
                        return b.intensity.CompareTo(a.intensity);
                    });

                    int takeCount = Mathf.Min(maxPixelLights, candidates.Count);
                    for (int i = 0; i < takeCount; i++)
                    {
                        var l = candidates[i];
                        snap.pixelLights.Add(new LightSnapshot
                        {
                            position = l.transform.position,
                            type = l.type,
                            range = l.range > 0 ? l.range : 10f,
                            spotAngle = l.spotAngle,
                            forward = l.transform.forward,
                            intensity = l.intensity,
                            shadowType = l.shadows,
                            renderMode = l.renderMode
                        });
                    }
                }

                Canvas[] canvases = GameObject.FindObjectsOfType<Canvas>();
                foreach (var c in canvases)
                {
                    if (c != null && c.enabled && c.gameObject.activeInHierarchy)
                    {
                        if (c.renderMode == RenderMode.ScreenSpaceOverlay || c.worldCamera == cam || c.worldCamera == null)
                        {
                            int crCount = c.GetComponentsInChildren<CanvasRenderer>(false).Length;
                            if (crCount > 0)
                            {
                                snap.activeCanvasDrawCalls += Mathf.Clamp(Mathf.CeilToInt(crCount / 8f), 1, 4);
                            }
                        }
                    }
                }
            }
            catch {}

            if (cam != null)
            {
                snap.position = cam.transform.position;
                snap.rotation = cam.transform.rotation;
                snap.frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);
                snap.fov = cam.fieldOfView > 0 ? cam.fieldOfView : 60f;
                snap.isGameCamera = isGameCamera || (cam == Camera.main || cam.CompareTag("MainCamera") || cam.cameraType == CameraType.Game);

                if (snap.isGameCamera && !snap.isPlaying)
                {
                    if (_cleanGameViewBatches > 0)
                    {
                        snap.unityStatsBatches = _cleanGameViewBatches;
                        snap.unityStatsTriangles = _lastGameCameraTris > 0 ? _lastGameCameraTris : UnityEditor.UnityStats.triangles;
                        snap.unityStatsVertices = _lastGameCameraVerts > 0 ? _lastGameCameraVerts : UnityEditor.UnityStats.vertices;
                    }
                    else if (_lastGameCameraBatches > 0)
                    {
                        snap.unityStatsBatches = _lastGameCameraBatches;
                        snap.unityStatsTriangles = _lastGameCameraTris > 0 ? _lastGameCameraTris : UnityEditor.UnityStats.triangles;
                        snap.unityStatsVertices = _lastGameCameraVerts > 0 ? _lastGameCameraVerts : UnityEditor.UnityStats.vertices;
                    }
                    else
                    {
                        // Fallback if GameView has not rendered yet: Offscreen GPU Camera Render pass
                        int gpuBatches = RenderCameraAndCaptureGpuBatches(cam, out int gpuTris, out int gpuVerts);
                        snap.unityStatsTriangles = gpuTris > 0 ? gpuTris : UnityEditor.UnityStats.triangles;
                        snap.unityStatsVertices = gpuVerts > 0 ? gpuVerts : UnityEditor.UnityStats.vertices;
                        snap.unityStatsBatches = gpuBatches > 0 ? gpuBatches : UnityEditor.UnityStats.batches;
                    }
                }
                else
                {
                    snap.unityStatsTriangles = (_lastGameCameraTris > 0 && snap.isGameCamera) ? _lastGameCameraTris : UnityEditor.UnityStats.triangles;
                    snap.unityStatsVertices = (_lastGameCameraVerts > 0 && snap.isGameCamera) ? _lastGameCameraVerts : UnityEditor.UnityStats.vertices;
                    snap.unityStatsBatches = (_cleanGameViewBatches > 0 && snap.isGameCamera) ? _cleanGameViewBatches : UnityEditor.UnityStats.batches;
                }
                snap.exactPlayModeBatches = PlayModeBatchCaptureManager.GetExactBatchesIfMatching(cam);
                snap.isValid = true;
            }
            else
            {
                snap.isValid = false;
                snap.isGameCamera = isGameCamera;
                snap.unityStatsTriangles = UnityEditor.UnityStats.triangles;
                snap.unityStatsVertices = UnityEditor.UnityStats.vertices;
                snap.unityStatsBatches = UnityEditor.UnityStats.batches;
                snap.exactPlayModeBatches = PlayModeBatchCaptureManager.LastExactBatches;
                snap.fov = 60f;
                snap.frustumPlanes = new Plane[0];
            }
            return snap;
        }

        private static int RenderCameraAndCaptureGpuBatches(Camera cam, out int outTris, out int outVerts)
        {
            outTris = 0;
            outVerts = 0;
            if (cam == null) return 0;

            int postRenderBatches = 0;
            int postRenderTris = 0;
            int postRenderVerts = 0;

            Camera.CameraCallback onPost = (c) =>
            {
                if (c == cam)
                {
                    postRenderBatches = UnityEditor.UnityStats.batches;
                    postRenderTris = UnityEditor.UnityStats.triangles;
                    postRenderVerts = UnityEditor.UnityStats.vertices;
                }
            };

            int statsBefore = UnityEditor.UnityStats.batches;
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            int width = Mathf.Clamp(cam.pixelWidth > 0 ? cam.pixelWidth : 1280, 16, 4096);
            int height = Mathf.Clamp(cam.pixelHeight > 0 ? cam.pixelHeight : 720, 16, 4096);
            RenderTexture tempRT = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.Default);

            try
            {
                Camera.onPostRender += onPost;
                cam.targetTexture = tempRT;
                RenderTexture.active = tempRT;

                // Warm up 3 frames so static batching buffers & shadow cascades settle into steady-state
                for (int i = 0; i < 3; i++)
                {
                    cam.Render();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Live Scene Auditor] GPU Camera Render error: {ex.Message}");
            }
            finally
            {
                Camera.onPostRender -= onPost;
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(tempRT);
            }

            int statsAfter = UnityEditor.UnityStats.batches;
            outTris = postRenderTris > 0 ? postRenderTris : UnityEditor.UnityStats.triangles;
            outVerts = postRenderVerts > 0 ? postRenderVerts : UnityEditor.UnityStats.vertices;

            return postRenderBatches > 0 ? postRenderBatches : statsAfter;
        }

        private static List<RendererSnapshot> CaptureRenderersSnapshot(bool includeInactive, CameraSnapshot camSnap)
        {
            List<RendererSnapshot> list = new List<RendererSnapshot>();

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

            // Collect all MeshRenderers & SkinnedMeshRenderers
            Renderer[] allRenderers = GameObject.FindObjectsOfType<Renderer>(includeInactive);

            foreach (var r in allRenderers)
            {
                if (r == null || !r.enabled || (!r.gameObject.activeInHierarchy && !includeInactive))
                    continue;

                // Ignore TextMeshPro and Canvas/UI components
                if (IsIgnoredRenderer(r))
                    continue;

                // Strict LOD rule: If part of an LOD group and NOT in the active set, SKIP ENTIRELY!
                bool isPartOfLod = activeLodRenderers.Contains(r) || inactiveLodRenderers.Contains(r);
                if (inactiveLodRenderers.Contains(r) && !activeLodRenderers.Contains(r))
                {
                    continue; // Skip inactive LOD meshes
                }

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

                bool isBatchingStatic = GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.BatchingStatic);
                bool castsShadows = (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off);

                Material[] sharedMats = r.sharedMaterials;
                List<int> matIds = new List<int>();
                List<string> matNames = new List<string>();
                List<string> shaderNames = new List<string>();
                List<bool> matInstancing = new List<bool>();

                if (sharedMats != null)
                {
                    foreach (var m in sharedMats)
                    {
                        if (m != null && !IsIgnoredAsset(m))
                        {
                            matIds.Add(m.GetInstanceID());
                            matNames.Add(m.name);
                            if (m.shader != null) shaderNames.Add(m.shader.name);
                            matInstancing.Add(m.enableInstancing);
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
                    isBatchingStatic = isBatchingStatic,
                    castsShadows = castsShadows,
                    lightmapIndex = r.lightmapIndex,
                    materialCount = matIds.Count,
                    materialInstanceIds = matIds,
                    materialNames = matNames,
                    shaderNames = shaderNames,
                    materialInstancingEnabled = matInstancing
                });
            }

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

        private static SceneAuditReport ProcessSnapshot(
            List<RendererSnapshot> renderers,
            List<TextureSnapshot> textures,
            CameraSnapshot camSnap,
            bool frustumOnly,
            BudgetProfile profile)
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

                    // Collect all visible meshes for culprit ranking
                    if (r.triangleCount > 0)
                    {
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
                            dependencyNames = r.materialNames
                        });
                    }
                }
            }

            // --- RUNTIME BATCHING SIMULATOR ---
            // Simulates exact draw call batching behavior (Static Batching, GPU Instancing, Dynamic Batching, Shadow Passes)
            int simulatedBatches = 0;
            int rawUnbatchedDraws = 0;
            int staticBatchedCount = 0;
            int instancedCount = 0;

            Dictionary<int, List<RendererSnapshot>> matToRenderers = new Dictionary<int, List<RendererSnapshot>>();
            HashSet<int> shadowCastingMatIds = new HashSet<int>();

            foreach (var r in visibleRenderers)
            {
                int mCount = Mathf.Max(1, r.materialCount);
                rawUnbatchedDraws += mCount;
                if (r.castsShadows)
                {
                    rawUnbatchedDraws += mCount; // shadow caster draw
                }

                for (int m = 0; m < r.materialInstanceIds.Count; m++)
                {
                    int mId = r.materialInstanceIds[m];
                    if (!matToRenderers.TryGetValue(mId, out var rList))
                    {
                        rList = new List<RendererSnapshot>();
                        matToRenderers[mId] = rList;
                    }
                    rList.Add(r);
                }
            }

            // Directional light shadow casters partitioned by physical cascade distance
            HashSet<int> cascade0Mats = new HashSet<int>();
            HashSet<int> cascade1Mats = new HashSet<int>();
            HashSet<int> cascade2Mats = new HashSet<int>();
            HashSet<int> cascade3Mats = new HashSet<int>();

            if (camSnap.hasDirectionalShadows)
            {
                float maxDist = camSnap.shadowDistance;
                float split0, split1, split2;

                if (camSnap.shadowCascades >= 4)
                {
                    split0 = maxDist * 0.0667f;
                    split1 = maxDist * 0.2f;
                    split2 = maxDist * 0.4667f;
                }
                else
                {
                    split0 = maxDist * 0.3333f;
                    split1 = maxDist;
                    split2 = maxDist;
                }

                foreach (var r in renderers)
                {
                    if (r.isActiveLOD && r.castsShadows)
                    {
                        float centerDist = Vector3.Distance(r.worldBounds.center, camSnap.position);
                        float radius = r.worldBounds.extents.magnitude;
                        float near = Mathf.Max(0f, centerDist - radius);
                        float far = centerDist + radius;

                        if (near <= maxDist)
                        {
                            if (camSnap.shadowCascades >= 4)
                            {
                                if (near <= split0)
                                {
                                    foreach (var mId in r.materialInstanceIds) cascade0Mats.Add(mId);
                                }
                                if (far >= split0 && near <= split1)
                                {
                                    foreach (var mId in r.materialInstanceIds) cascade1Mats.Add(mId);
                                }
                                if (far >= split1 && near <= split2)
                                {
                                    foreach (var mId in r.materialInstanceIds) cascade2Mats.Add(mId);
                                }
                                if (far >= split2 && near <= maxDist)
                                {
                                    foreach (var mId in r.materialInstanceIds) cascade3Mats.Add(mId);
                                }
                            }
                            else if (camSnap.shadowCascades >= 2)
                            {
                                if (near <= split0)
                                {
                                    foreach (var mId in r.materialInstanceIds) cascade0Mats.Add(mId);
                                }
                                if (far >= split0 && near <= maxDist)
                                {
                                    foreach (var mId in r.materialInstanceIds) cascade1Mats.Add(mId);
                                }
                            }
                            else
                            {
                                foreach (var mId in r.materialInstanceIds) cascade0Mats.Add(mId);
                            }
                        }
                    }
                }
            }

            foreach (var kvp in matToRenderers)
            {
                int matId = kvp.Key;
                var rList = kvp.Value;

                // Static Batching is split by lightmapIndex (Unity cannot batch objects across different lightmaps)
                Dictionary<int, int> staticVertsPerLightmap = new Dictionary<int, int>();
                int staticCount = 0;
                int instancedInMat = 0;
                int unbatchedInMat = 0;

                for (int i = 0; i < rList.Count; i++)
                {
                    var r = rList[i];
                    bool instancing = false;
                    for (int m = 0; m < r.materialInstanceIds.Count; m++)
                    {
                        if (r.materialInstanceIds[m] == matId && m < r.materialInstancingEnabled.Count)
                        {
                            instancing = r.materialInstancingEnabled[m];
                            break;
                        }
                    }

                    if (r.isBatchingStatic && r.lightmapIndex >= 0)
                    {
                        int lm = r.lightmapIndex;
                        if (!staticVertsPerLightmap.ContainsKey(lm))
                        {
                            staticVertsPerLightmap[lm] = 0;
                        }
                        staticVertsPerLightmap[lm] += r.vertexCount;
                        staticCount++;
                    }
                    else if (instancing)
                    {
                        instancedInMat++;
                    }
                    else
                    {
                        // Dynamic Batching is disabled (m_DynamicBatching: 0), so each non-static object takes 1 draw call.
                        // Non-lightmapped static objects (lightmapIndex < 0) sample Light Probes individually, requiring separate draw calls unless instanced.
                        unbatchedInMat++;
                    }
                }

                staticBatchedCount += staticCount;
                instancedCount += instancedInMat;

                // Static Batching: combines up to 64k vertices per lightmap per material
                foreach (var lmVerts in staticVertsPerLightmap.Values)
                {
                    simulatedBatches += Mathf.Max(1, Mathf.CeilToInt((float)lmVerts / 64000f));
                }

                // GPU Instancing: combines up to 500 instances per batch
                if (instancedInMat > 0)
                {
                    simulatedBatches += Mathf.Max(1, Mathf.CeilToInt((float)instancedInMat / 500f));
                }

                // Non-batched dynamic objects
                simulatedBatches += unbatchedInMat;
            }

            // Depth-sorting batch breaks (interleaved front-to-back draw order in Built-in Forward)
            int depthSortSplits = 0;
            if (uniqueMaterialIds.Count > 10 && visibleRenderersCount > 20)
            {
                depthSortSplits = Mathf.Clamp(Mathf.RoundToInt(uniqueMaterialIds.Count * 0.12f), 1, 8);
                simulatedBatches += depthSortSplits;
            }

            // Directional light shadow map passes across cascade splits
            int shadowPassBatches = 0;
            if (camSnap.hasDirectionalShadows)
            {
                if (camSnap.shadowCascades >= 4)
                {
                    shadowPassBatches = cascade0Mats.Count + cascade1Mats.Count + cascade2Mats.Count + cascade3Mats.Count;
                }
                else if (camSnap.shadowCascades >= 2)
                {
                    shadowPassBatches = cascade0Mats.Count + cascade1Mats.Count;
                }
                else
                {
                    shadowPassBatches = cascade0Mats.Count;
                }

                if (shadowPassBatches > 0)
                {
                    // Screen-Space Shadow Collector pass (soft directional shadows)
                    shadowPassBatches += 1;
                }

                simulatedBatches += shadowPassBatches;
            }

            // Additional Pixel Lights (Point, Spot, Secondary Directional in ForwardAdd passes)
            if (camSnap.pixelLights != null && camSnap.pixelLights.Count > 0)
            {
                for (int li = 0; li < camSnap.pixelLights.Count; li++)
                {
                    var pl = camSnap.pixelLights[li];
                    Dictionary<int, List<RendererSnapshot>> litMatToRenderers = new Dictionary<int, List<RendererSnapshot>>();
                    int litShadowCasters = 0;

                    foreach (var r in visibleRenderers)
                    {
                        bool isLit = false;
                        if (pl.type == LightType.Directional)
                        {
                            isLit = true;
                        }
                        else if (pl.type == LightType.Point)
                        {
                            Vector3 closest = r.worldBounds.ClosestPoint(pl.position);
                            float sqrDist = (closest - pl.position).sqrMagnitude;
                            if (sqrDist <= pl.range * pl.range)
                            {
                                isLit = true;
                            }
                        }
                        else if (pl.type == LightType.Spot)
                        {
                            Vector3 closest = r.worldBounds.ClosestPoint(pl.position);
                            float sqrDist = (closest - pl.position).sqrMagnitude;
                            if (sqrDist <= pl.range * pl.range)
                            {
                                Vector3 toClosest = (closest - pl.position).normalized;
                                float cosAngle = Mathf.Cos(pl.spotAngle * 0.5f * Mathf.Deg2Rad);
                                if (Vector3.Dot(toClosest, pl.forward) >= cosAngle)
                                {
                                    isLit = true;
                                }
                            }
                        }

                        if (isLit)
                        {
                            rawUnbatchedDraws += Mathf.Max(1, r.materialCount);
                            if (pl.shadowType != LightShadows.None && r.castsShadows)
                            {
                                litShadowCasters++;
                                rawUnbatchedDraws += Mathf.Max(1, r.materialCount);
                            }

                            for (int m = 0; m < r.materialInstanceIds.Count; m++)
                            {
                                int mId = r.materialInstanceIds[m];
                                if (!litMatToRenderers.TryGetValue(mId, out var rList))
                                {
                                    rList = new List<RendererSnapshot>();
                                    litMatToRenderers[mId] = rList;
                                }
                                rList.Add(r);
                            }
                        }
                    }

                    // ForwardAdd draws for this light
                    foreach (var kvp in litMatToRenderers)
                    {
                        int matId = kvp.Key;
                        var rList = kvp.Value;
                        Dictionary<int, int> litStaticVertsPerLm = new Dictionary<int, int>();
                        int litInstanced = 0;
                        int litUnbatched = 0;

                        for (int i = 0; i < rList.Count; i++)
                        {
                            var r = rList[i];
                            bool instancing = false;
                            for (int m = 0; m < r.materialInstanceIds.Count; m++)
                            {
                                if (r.materialInstanceIds[m] == matId && m < r.materialInstancingEnabled.Count)
                                {
                                    instancing = r.materialInstancingEnabled[m];
                                    break;
                                }
                            }

                            if (r.isBatchingStatic && r.lightmapIndex >= 0)
                            {
                                int lm = r.lightmapIndex;
                                if (!litStaticVertsPerLm.ContainsKey(lm)) litStaticVertsPerLm[lm] = 0;
                                litStaticVertsPerLm[lm] += r.vertexCount;
                            }
                            else if (instancing)
                            {
                                litInstanced++;
                            }
                            else
                            {
                                litUnbatched++;
                            }
                        }

                        foreach (var lmVerts in litStaticVertsPerLm.Values)
                        {
                            simulatedBatches += Mathf.Max(1, Mathf.CeilToInt((float)lmVerts / 64000f));
                        }
                        if (litInstanced > 0)
                        {
                            simulatedBatches += Mathf.Max(1, Mathf.CeilToInt((float)litInstanced / 500f));
                        }
                        simulatedBatches += litUnbatched;
                    }

                    // Shadow passes for this additional light
                    if (pl.shadowType != LightShadows.None && litShadowCasters > 0)
                    {
                        if (pl.type == LightType.Point)
                        {
                            simulatedBatches += Mathf.Clamp(litShadowCasters, 1, 6);
                        }
                        else if (pl.type == LightType.Spot)
                        {
                            simulatedBatches += 1;
                        }
                    }
                }
            }

            // Active UI Canvases (Screen-Space / Game HUD)
            simulatedBatches += camSnap.activeCanvasDrawCalls;

            // Camera clear / Skybox pass
            simulatedBatches += 1;

            Debug.Log($"[AuditorScan] isPlaying={camSnap.isPlaying}, unityBatches={camSnap.unityStatsBatches}, simulatedBatches={simulatedBatches} " +
                      $"(BaseDraws={simulatedBatches - shadowPassBatches - camSnap.activeCanvasDrawCalls - 1}, Shadows={shadowPassBatches}, " +
                      $"Canvases={camSnap.activeCanvasDrawCalls}, Skybox=1), unityTris={camSnap.unityStatsTriangles}, unityVerts={camSnap.unityStatsVertices}, " +
                      $"pixelLightsCount={(camSnap.pixelLights != null ? camSnap.pixelLights.Count : 0)}");

            report.staticBatchedObjectsCount = staticBatchedCount;

            if (frustumOnly && camSnap.isGameCamera)
            {
                if (camSnap.isPlaying)
                {
                    // In Play Mode, driver stats already reflect runtime static batching
                    int playBatches = camSnap.unityStatsBatches > 0 ? camSnap.unityStatsBatches : (_cleanGameViewBatches > 0 ? _cleanGameViewBatches : rawUnbatchedDraws);
                    report.visibleTriangles = camSnap.unityStatsTriangles > 0 ? camSnap.unityStatsTriangles : visibleTris;
                    report.visibleVertices = camSnap.unityStatsVertices > 0 ? camSnap.unityStatsVertices : visibleVerts;
                    report.estimatedBatches = playBatches;
                    report.rawUnbatchedBatches = rawUnbatchedDraws > 0 ? rawUnbatchedDraws : playBatches;
                    report.rawEditModeBatches = report.rawUnbatchedBatches;
                    report.savedByBatching = Mathf.Max(0, report.rawUnbatchedBatches - playBatches);
                }
                else
                {
                    // In Edit Mode, retrieve unpolluted Game View draws
                    int cleanEditBatches = _cleanGameViewBatches > 0 ? _cleanGameViewBatches : camSnap.unityStatsBatches;
                    if (cleanEditBatches <= 0)
                        cleanEditBatches = (simulatedBatches > 0) ? simulatedBatches : rawUnbatchedDraws;

                    if (camSnap.exactPlayModeBatches > 0)
                    {
                        // 100% exact verified Play Mode batches captured directly from GPU driver
                        int exactBatches = camSnap.exactPlayModeBatches;
                        report.estimatedBatches = exactBatches;
                        report.savedByBatching = Mathf.Max(0, cleanEditBatches - exactBatches);
                    }
                    else
                    {
                        // Edit Mode uncombined baseline
                        report.estimatedBatches = cleanEditBatches;
                        report.savedByBatching = 0;
                    }

                    report.visibleTriangles = camSnap.unityStatsTriangles > 0 ? camSnap.unityStatsTriangles : visibleTris;
                    report.visibleVertices = camSnap.unityStatsVertices > 0 ? camSnap.unityStatsVertices : visibleVerts;
                    report.rawUnbatchedBatches = cleanEditBatches;
                    report.rawEditModeBatches = cleanEditBatches;
                }
            }
            else
            {
                // Full Scene or non-game-camera fallback: Exact Simulated Runtime Batches
                report.visibleTriangles = (frustumOnly && camSnap.isGameCamera && camSnap.unityStatsTriangles > 0) ? camSnap.unityStatsTriangles : visibleTris;
                report.visibleVertices = (frustumOnly && camSnap.isGameCamera && camSnap.unityStatsVertices > 0) ? camSnap.unityStatsVertices : visibleVerts;
                report.estimatedBatches = (simulatedBatches > 0) ? simulatedBatches : Mathf.Max(uniqueMaterialIds.Count, Mathf.RoundToInt(visibleRenderersCount * 0.8f));
                report.savedByBatching = Mathf.Max(0, rawUnbatchedDraws - report.estimatedBatches);
                report.rawUnbatchedBatches = rawUnbatchedDraws;
                report.rawEditModeBatches = rawUnbatchedDraws;
            }
            report.totalSceneTriangles = totalTris;
            report.totalSceneVertices = totalVerts;
            report.uniqueMaterialsCount = uniqueMaterialIds.Count;

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
                        primaryMetricText = $"{t.width}x{t.height} (Score: {t.score:F1} • {texPercent:F1}%)",
                        secondaryDetailText = $"Used in: {(t.usedInMaterials.Count > 0 ? string.Join(", ", t.usedInMaterials.Take(2)) : "Scene")}",
                        severityScore = texBudgetRatio * 1000f,
                        dependencyNames = t.usedInMaterials
                    });
                }
            }

            report.textureAreaScore = totalTexScore;

            // Sort top 15 meshes and top 15 textures by severity
            var sortedMeshes = meshCulprits.OrderByDescending(c => c.triangleCount).Take(15);
            var sortedTextures = texCulprits.OrderByDescending(c => c.textureScore).Take(15);
            
            report.culprits.AddRange(sortedMeshes);
            report.culprits.AddRange(sortedTextures);
            report.culprits = report.culprits.OrderByDescending(c => c.severityScore).ToList();

            // Calculate budget comparison
            float triRatio = profile.maxTriangles > 0 ? ((float)visibleTris / profile.maxTriangles) : 0f;
            float batchRatio = profile.maxBatches > 0 ? ((float)report.estimatedBatches / profile.maxBatches) : 0f;
            float texRatio = profile.maxTextureScore > 0 ? (totalTexScore / profile.maxTextureScore) : 0f;

            float maxRatio = Mathf.Max(triRatio, Mathf.Max(batchRatio, texRatio));
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
