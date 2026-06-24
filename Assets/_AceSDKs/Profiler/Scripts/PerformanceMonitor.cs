using UnityEngine.Audio;
using System.Collections.Generic;
using System.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Profiling;

/// <summary>
/// Canvas/TextMeshPro version of MyProfiler.
/// Target: PC, Android, iOS.
///
/// Setup:
/// 1. Create a Canvas.
/// 2. Add TMP_Text objects for the stats you want.
/// 3. Assign those TMP_Text fields in this script.
/// 4. For mobile toggle, call ToggleVisible() from a UI Button.
/// 5. Keep this script on a separate GameObject, not inside the panel you disable.
///
/// Important:
/// Enable:
/// Project Settings -> Player -> Other Settings -> Rendering -> Frame Timing Stats
/// </summary>
public class PerformanceMonitor : MonoBehaviour {
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Refresh Rates")]
    public float refreshRate = 0.25f;
    public float sceneCountRefreshRate = 1f;

    // ─── Canvas Text Fields: Timing ───────────────────────────────────────────

    [Header("TIMING")]
    public Text cpuMainText;
    public Text playerLoopText;
    public Text cameraRenderText;
    public Text gpuFrameText;

    // ─── Canvas Text Fields: Rendering ────────────────────────────────────────

    [Header("RENDERING")]
    public Text batchesText;
    public Text drawCallsText;
    public Text setPassText;
    public Text trianglesText;
    public Text verticesText;
    public Text shadowCastersText;
    public Text visibleLightsText;
    public Text screenSizeText;

    // ─── Canvas Text Fields: RAM ──────────────────────────────────────────────

    [Header("RAM")]
    public Text ramAllocatedText;
    public Text ramReservedText;
    public Text ramUnusedReservedText;
    public Text ramUsedApproxText;
    public Text systemRamTotalText;
    public Text gcHeapUsedText;

    // ─── Canvas Text Fields: GPU / Graphics ───────────────────────────────────

    [Header("GPU / GRAPHICS")]
    public Text gpuUsedText;
    public Text gpuReservedText;
    public Text gpuFreeText;
    public Text gpuTotalText;
    public Text gpuSourceText;
    public Text usedBuffersText;
    public Text renderTexturesText;
    public Text videoMemoryText;

    // ─── Canvas Text Fields: Assets ───────────────────────────────────────────

    [Header("ASSETS")]
    public Text textureCountText;
    public Text textureMemoryText;
    public Text meshCountText;
    public Text meshMemoryText;
    public Text audioMemoryText;
    public Text assetCountText;

    // ─── Canvas Text Fields: Garbage Collector ────────────────────────────────

    [Header("GARBAGE COLLECTOR")]
    public Text gcAllocFrameText;
    public Text gcCollectTimeText;
    public Text gcTotalCountText;
    public Text gcPerSecondText;
    public Text gcActiveSamplesText;
    public Text gcRanLatestSampleText;
    public Text gcAppTotalText;
    public Text monoHeapText;
    public Text monoUsedText;

    // ─── Canvas Text Fields: Animation ────────────────────────────────────────

    [Header("ANIMATION")]
    public Text animatorUpdateText;
    public Text animatorCountText;

    // ─── Canvas Text Fields: Physics 3D ───────────────────────────────────────

    [Header("PHYSICS 3D")]
    public Text rb3dActiveText;
    public Text rb3dTotalText;
    public Text col3dActiveText;
    public Text boxColliderText;
    public Text sphereColliderText;
    public Text capsuleColliderText;
    public Text meshColliderText;
    public Text convexMeshColliderText;
    public Text terrainColliderText;
    public Text wheelColliderText;
    public Text other3dColliderText;

    // ─── Canvas Text Fields: Physics 2D ───────────────────────────────────────

    [Header("PHYSICS 2D")]
    public Text rb2dActiveText;
    public Text rb2dTotalText;
    public Text col2dActiveText;
    public Text boxCollider2dText;
    public Text circleCollider2dText;
    public Text polygonCollider2dText;
    public Text edgeCollider2dText;
    public Text capsuleCollider2dText;
    public Text compositeCollider2dText;
    public Text other2dColliderText;

    // ─── Canvas Text Fields: Particles ────────────────────────────────────────────

    [Header("PARTICLES")]
    public Text particleSystemsActiveText;
    public Text particleSystemsTotalText;

    // ─── Canvas Text Fields: Audio ────────────────────────────────────────────────

    [Header("AUDIO")]
    public Text audioSourcesActiveText;
    public Text audioSourcesPlayingText;
    public Text audioSourcesTotalText;
    public Text audioClipsLoadedText;
    public Text audioMixersInUseText;
    public Text audioMixersLoadedText;

    // ─── Canvas Text Fields: Renderers ────────────────────────────────────────────

    [Header("TRAIL / LINE RENDERERS")]
    public Text trailRenderersActiveText;
    public Text trailRenderersTotalText;
    public Text lineRenderersActiveText;
    public Text lineRenderersTotalText;

    // ─── Canvas Text Fields: Cameras ──────────────────────────────────────────────

    [Header("CAMERAS")]
    public Text camerasActiveText;
    public Text camerasTotalText;

    // ─── Profiler Recorders ───────────────────────────────────────────────────

    // Timing
    ProfilerRecorder _recCpuMain;
    ProfilerRecorder _recPlayerLoop;
    ProfilerRecorder _recCameraRender;
    ProfilerRecorder _recGpuFrame;

    // Rendering
    ProfilerRecorder _recBatches;
    ProfilerRecorder _recTriangles;
    ProfilerRecorder _recVertices;
    ProfilerRecorder _recSetPass;
    ProfilerRecorder _recDrawCalls;
    ProfilerRecorder _recShadowCasters;
    ProfilerRecorder _recVisibleLights;

    // RAM / GC
    ProfilerRecorder _recGCUsed;

    // GPU / Graphics memory
    ProfilerRecorder _recGfxUsed;
    ProfilerRecorder _recGfxReserved;
    ProfilerRecorder _recUsedBuffersBytes;
    ProfilerRecorder _recRenderTexturesBytes;
    ProfilerRecorder _recVideoMemoryBytes;

    // Assets
    ProfilerRecorder _recTextureCount;
    ProfilerRecorder _recTextureMemory;
    ProfilerRecorder _recMeshCount;
    ProfilerRecorder _recMeshMemory;
    ProfilerRecorder _recAudioMemory;
    ProfilerRecorder _recAssetCount;

    // GC / Mono
    ProfilerRecorder _recGCAlloc;
    ProfilerRecorder _recGCCollect;

    // Animation
    ProfilerRecorder _recAnimatorUpdate;

    // Physics
    ProfilerRecorder _recActiveRb3d;
    ProfilerRecorder _recActiveCol3d;
    ProfilerRecorder _recActiveRb2d;
    ProfilerRecorder _recActiveCol2d;

    // ─── Cached display strings ───────────────────────────────────────────────

    string _sCpuMain = "–";
    string _sPlayerLoop = "–";
    string _sCameraRender = "–";
    string _sGpuFrame = "–";

    string _sBatches = "–";
    string _sDrawCalls = "–";
    string _sSetPass = "–";
    string _sTriangles = "–";
    string _sVertices = "–";
    string _sShadowCasters = "–";
    string _sVisibleLights = "–";
    string _sScreenSize = "–";

    string _sRamAllocated = "–";
    string _sRamReserved = "–";
    string _sRamUnusedReserved = "–";
    string _sRamUsedApprox = "–";
    string _sRamTotal = "–";
    string _sGcUsed = "–";

    string _sGpuUsed = "–";
    string _sGpuReserved = "–";
    string _sGpuFree = "N/A";
    string _sGpuTotal = "–";
    string _sGpuSource = "–";
    string _sUsedBuffers = "–";
    string _sRenderTextures = "–";
    string _sVideoMemory = "–";

    string _sTextureCount = "–";
    string _sTextureMemory = "–";
    string _sMeshCount = "–";
    string _sMeshMemory = "–";
    string _sAudioMemory = "–";
    string _sAssetCount = "–";

    string _sGcAllocFrame = "–";
    string _sGcCollectMs = "–";
    string _sGcTotalCount = "0";
    string _sGcPerSecond = "0";
    string _sGcActiveSamples = "0";
    string _sGcRanLatestSample = "No";
    string _sGcAppTotalCount = "0";
    string _sMonoHeap = "–";
    string _sMonoUsed = "–";

    int _gcStartCountGen0;
    int _gcLastCountGen0;
    int _gcTotalSinceStart;
    int _gcCollectionsThisSecond;
    int _gcCollectionsPerSecond;
    float _gcSecondTimer;
    long _gcActiveSampleCount;

    string _sAnimatorMs = "–";
    string _sAnimatorCount = "–";

    string _sRb3dActive = "–";
    string _sRb3dTotal = "–";
    string _sCol3dActive = "–";

    string _sRb2dActive = "–";
    string _sRb2dTotal = "–";
    string _sCol2dActive = "–";

    string _sColBox = "–";
    string _sColSphere = "–";
    string _sColCapsule = "–";
    string _sColMesh = "–";
    string _sColConvexMesh = "–";
    string _sColTerrain = "–";
    string _sColWheel = "–";
    string _sColOther3d = "–";

    string _sCol2dBox = "–";
    string _sCol2dCircle = "–";
    string _sCol2dPoly = "–";
    string _sCol2dEdge = "–";
    string _sCol2dCapsule = "–";
    string _sCol2dComposite = "–";
    string _sCol2dOther = "–";

    string _sParticleSystemsActive = "–";
    string _sParticleSystemsTotal = "–";

    string _sAudioSourcesActive = "–";
    string _sAudioSourcesPlaying = "–";
    string _sAudioSourcesTotal = "–";
    string _sAudioClipsLoaded = "–";
    string _sAudioMixersInUse = "–";
    string _sAudioMixersLoaded = "–";

    string _sTrailRenderersActive = "–";
    string _sTrailRenderersTotal = "–";
    string _sLineRenderersActive = "–";
    string _sLineRenderersTotal = "–";

    string _sCamerasActive = "–";
    string _sCamerasTotal = "–";

    // ─── Internal state ───────────────────────────────────────────────────────

    float _nextRefresh;
    bool _sceneCountRunning;

    // ─── Awake ────────────────────────────────────────────────────────────────

    void Awake() {
        Profiler.enabled = true;

        // Timing
        _recCpuMain = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
        _recPlayerLoop = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "PlayerLoop", 15);
        _recCameraRender = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Camera.Render", 15);
        _recGpuFrame = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GPU Frame Time", 15);

        // Rendering
        _recBatches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        _recTriangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        _recVertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
        _recSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        _recDrawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _recShadowCasters = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count");
        _recVisibleLights = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Visible Lights", 1);

        // RAM / GC
        _recGCUsed = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Used Memory");

        // GPU / Graphics
        _recGfxUsed = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory");
        _recGfxReserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Reserved Memory");
        _recUsedBuffersBytes = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Used Buffers Bytes");
        _recRenderTexturesBytes = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Render Textures Bytes");
        _recVideoMemoryBytes = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Video Memory Bytes");

        // Assets
        _recTextureCount = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Count");
        _recTextureMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
        _recMeshCount = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Mesh Count");
        _recMeshMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Mesh Memory");
        _recAudioMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Audio Used Memory");
        _recAssetCount = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Asset Count");

        // GC
        _recGCAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 15);
        _recGCCollect = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Collect", 15);

        InitGcCounters();

        // Animation
        _recAnimatorUpdate = ProfilerRecorder.StartNew(ProfilerCategory.Animation, "Animator.Update", 15);

        // Physics
        _recActiveRb3d = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Active Rigidbodies", 1);
        _recActiveCol3d = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Active Colliders", 1);
        _recActiveRb2d = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Active Rigidbody2D", 1);
        _recActiveCol2d = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Active Collider2D", 1);

        DontDestroyOnLoad(gameObject);

        DebugCounterValidity();
    }

    void InitGcCounters() {
        _gcStartCountGen0 = GetGcCollectionCountSafe(0);
        _gcLastCountGen0 = _gcStartCountGen0;
        _gcSecondTimer = Time.unscaledTime;

        _gcTotalSinceStart = 0;
        _gcCollectionsThisSecond = 0;
        _gcCollectionsPerSecond = 0;
        _gcActiveSampleCount = 0;

        _sGcTotalCount = "0";
        _sGcPerSecond = "0";
        _sGcActiveSamples = "0";
        _sGcRanLatestSample = "No";
        _sGcAppTotalCount = _gcStartCountGen0.ToString("N0");
    }

    void DebugCounterValidity() {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        Debug.Log(
            "[MyProfiler] Counter validity -> " +
            "GPU Frame: " + _recGpuFrame.Valid +
            ", Gfx Used: " + _recGfxUsed.Valid +
            ", Gfx Reserved: " + _recGfxReserved.Valid +
            ", Used Buffers: " + _recUsedBuffersBytes.Valid +
            ", Render Textures: " + _recRenderTexturesBytes.Valid +
            ", Video Memory: " + _recVideoMemoryBytes.Valid +
            ", GC Used: " + _recGCUsed.Valid +
            ", GC Collect: " + _recGCCollect.Valid +
            ", GC Alloc: " + _recGCAlloc.Valid
        );
#endif
    }

    // ─── OnDestroy ────────────────────────────────────────────────────────────

    void OnDestroy() {
        StopAllCoroutines();

        DisposeRecorder(ref _recCpuMain);
        DisposeRecorder(ref _recPlayerLoop);
        DisposeRecorder(ref _recCameraRender);
        DisposeRecorder(ref _recGpuFrame);

        DisposeRecorder(ref _recBatches);
        DisposeRecorder(ref _recTriangles);
        DisposeRecorder(ref _recVertices);
        DisposeRecorder(ref _recSetPass);
        DisposeRecorder(ref _recDrawCalls);
        DisposeRecorder(ref _recShadowCasters);
        DisposeRecorder(ref _recVisibleLights);

        DisposeRecorder(ref _recGCUsed);

        DisposeRecorder(ref _recGfxUsed);
        DisposeRecorder(ref _recGfxReserved);
        DisposeRecorder(ref _recUsedBuffersBytes);
        DisposeRecorder(ref _recRenderTexturesBytes);
        DisposeRecorder(ref _recVideoMemoryBytes);

        DisposeRecorder(ref _recTextureCount);
        DisposeRecorder(ref _recTextureMemory);
        DisposeRecorder(ref _recMeshCount);
        DisposeRecorder(ref _recMeshMemory);
        DisposeRecorder(ref _recAudioMemory);
        DisposeRecorder(ref _recAssetCount);

        DisposeRecorder(ref _recGCAlloc);
        DisposeRecorder(ref _recGCCollect);

        DisposeRecorder(ref _recAnimatorUpdate);

        DisposeRecorder(ref _recActiveRb3d);
        DisposeRecorder(ref _recActiveCol3d);
        DisposeRecorder(ref _recActiveRb2d);
        DisposeRecorder(ref _recActiveCol2d);
    }

    // ─── Update / LateUpdate ──────────────────────────────────────────────────

    public void DoUpdate() {
        if (!_sceneCountRunning)
            StartCoroutine(SceneCountCoroutine());
    }

    void LateUpdate() {
        float t = Time.unscaledTime;

        if (t >= _nextRefresh) {
            _nextRefresh = t + refreshRate;
            RefreshRecorderStats();
            UpdateCanvasTexts();
        }
    }

    // ─── Refresh Recorder Stats ───────────────────────────────────────────────

    void RefreshRecorderStats() {
        // Timing
        _sCpuMain = RecMs(_recCpuMain);
        _sPlayerLoop = RecMs(_recPlayerLoop);
        _sCameraRender = RecMs(_recCameraRender);
        _sGpuFrame = RecMs(_recGpuFrame);

        // Rendering
        _sBatches = LongStat(_recBatches);
        _sDrawCalls = LongStat(_recDrawCalls);
        _sSetPass = LongStat(_recSetPass);
        _sTriangles = LongStat(_recTriangles);
        _sVertices = LongStat(_recVertices);
        _sShadowCasters = LongStat(_recShadowCasters);
        _sScreenSize = Screen.width + " × " + Screen.height;

        RefreshRamStats();
        RefreshGpuStats();

        // Assets
        _sTextureCount = LongStat(_recTextureCount);
        _sTextureMemory = BytesMB_Rec(_recTextureMemory);
        _sMeshCount = LongStat(_recMeshCount);
        _sMeshMemory = BytesMB_Rec(_recMeshMemory);
        _sAudioMemory = BytesMB_Rec(_recAudioMemory);
        _sAssetCount = LongStat(_recAssetCount);

        // GC
        _sGcAllocFrame = _recGCAlloc.Valid
            ? FormatBytesSmart(_recGCAlloc.LastValue)
            : "–";

        _sGcCollectMs = RecMs(_recGCCollect);
        RefreshGcCountStats();

        _sMonoHeap = BytesMB(Profiler.GetMonoHeapSizeLong());
        _sMonoUsed = BytesMB(Profiler.GetMonoUsedSizeLong());

        // Animation
        _sAnimatorMs = _recAnimatorUpdate.Valid ? RecMs(_recAnimatorUpdate) + " ms" : "–";
    }

    void RefreshRamStats() {
        long allocated = Profiler.GetTotalAllocatedMemoryLong();
        long reserved = Profiler.GetTotalReservedMemoryLong();
        long unusedReserved = Profiler.GetTotalUnusedReservedMemoryLong();

        long usedApprox = reserved - unusedReserved;

        if (usedApprox < 0)
            usedApprox = 0;

        _sRamAllocated = BytesMB(allocated);
        _sRamReserved = BytesMB(reserved);
        _sRamUnusedReserved = BytesMB(unusedReserved);
        _sRamUsedApprox = BytesMB(usedApprox);

        _sRamTotal = SystemInfo.systemMemorySize > 0
            ? SystemInfo.systemMemorySize.ToString("N0") + " MB"
            : "Unknown";

        _sGcUsed = BytesMB_Rec(_recGCUsed);
    }

    void RefreshGcCountStats() {
        int currentCount = GetGcCollectionCountSafe(0);

        if (currentCount > _gcLastCountGen0) {
            int diff = currentCount - _gcLastCountGen0;
            _gcCollectionsThisSecond += diff;
            _gcLastCountGen0 = currentCount;
        }

        _gcTotalSinceStart = currentCount - _gcStartCountGen0;

        if (_gcTotalSinceStart < 0)
            _gcTotalSinceStart = 0;

        float now = Time.unscaledTime;

        if (now - _gcSecondTimer >= 1f) {
            _gcCollectionsPerSecond = _gcCollectionsThisSecond;
            _gcCollectionsThisSecond = 0;
            _gcSecondTimer = now;
        }

        bool gcActiveLatestSample = _recGCCollect.Valid && _recGCCollect.LastValue > 0;

        if (gcActiveLatestSample)
            _gcActiveSampleCount++;

        _sGcTotalCount = _gcTotalSinceStart.ToString("N0");
        _sGcPerSecond = _gcCollectionsPerSecond.ToString("N0");
        _sGcActiveSamples = _gcActiveSampleCount.ToString("N0");
        _sGcRanLatestSample = gcActiveLatestSample ? "Yes" : "No";
        _sGcAppTotalCount = currentCount.ToString("N0");
    }

    void RefreshGpuStats() {
        string gfxUsed = BytesMB_Rec(_recGfxUsed);
        string gfxReserved = BytesMB_Rec(_recGfxReserved);

        _sUsedBuffers = BytesMB_Rec(_recUsedBuffersBytes);
        _sRenderTextures = BytesMB_Rec(_recRenderTexturesBytes);
        _sVideoMemory = BytesMB_Rec(_recVideoMemoryBytes);

        long fallbackBytes = 0;

        if (_recUsedBuffersBytes.Valid && _recUsedBuffersBytes.LastValue > 0)
            fallbackBytes += _recUsedBuffersBytes.LastValue;

        if (_recRenderTexturesBytes.Valid && _recRenderTexturesBytes.LastValue > 0)
            fallbackBytes += _recRenderTexturesBytes.LastValue;

        if (_recVideoMemoryBytes.Valid && _recVideoMemoryBytes.LastValue > 0)
            fallbackBytes += _recVideoMemoryBytes.LastValue;

        string fallback = fallbackBytes > 0 ? BytesMB(fallbackBytes) : "–";

        if (gfxUsed != "–") {
            _sGpuUsed = gfxUsed;
            _sGpuSource = "Gfx Used Memory";
        }
        else if (fallback != "–") {
            _sGpuUsed = fallback;
            _sGpuSource = "Fallback";
        }
        else {
            _sGpuUsed = "–";
            _sGpuSource = "Unavailable";
        }

        _sGpuReserved = gfxReserved != "–" ? gfxReserved : "N/A";
        _sGpuFree = "N/A";

        _sGpuTotal = SystemInfo.graphicsMemorySize > 0
            ? SystemInfo.graphicsMemorySize.ToString("N0") + " MB approx"
            : "Unknown";
    }

    // ─── Scene Count Coroutine ────────────────────────────────────────────────

    IEnumerator SceneCountCoroutine() {
        _sceneCountRunning = true;

        if (_recVisibleLights.Valid && _recVisibleLights.LastValue > 0) {
            _sVisibleLights = _recVisibleLights.LastValue.ToString("N0");
        }
        else {
            int n = 0;

            foreach (Light l in FindSceneObjects<Light>()) {
                if (l != null && l.enabled && l.gameObject.activeInHierarchy)
                    n++;
            }

            _sVisibleLights = n.ToString("N0") + " scene";
        }

        yield return null;

        {
            int total = 0;
            int active = 0;

            foreach (Animator a in FindSceneObjects<Animator>()) {
                if (a == null)
                    continue;

                total++;

                if (a.isActiveAndEnabled)
                    active++;
            }

            _sAnimatorCount = active.ToString("N0") + " active / " + total.ToString("N0") + " total";
        }

        yield return null;

        {
            int active = 0;
            int total = 0;

            if (_recActiveRb3d.Valid && _recActiveRb3d.LastValue >= 0) {
                active = (int)_recActiveRb3d.LastValue;

                foreach (Rigidbody rb in FindSceneObjects<Rigidbody>()) {
                    if (rb != null && rb.gameObject.activeInHierarchy)
                        total++;
                }
            }
            else {
                foreach (Rigidbody rb in FindSceneObjects<Rigidbody>()) {
                    if (rb == null || !rb.gameObject.activeInHierarchy)
                        continue;

                    total++;

                    if (!rb.IsSleeping())
                        active++;
                }
            }

            _sRb3dActive = active.ToString("N0");
            _sRb3dTotal = total.ToString("N0");
        }

        yield return null;

        {
            int active = 0;
            int total = 0;

            if (_recActiveRb2d.Valid && _recActiveRb2d.LastValue >= 0) {
                active = (int)_recActiveRb2d.LastValue;

                foreach (Rigidbody2D rb in FindSceneObjects<Rigidbody2D>()) {
                    if (rb != null && rb.gameObject.activeInHierarchy)
                        total++;
                }
            }
            else {
                foreach (Rigidbody2D rb in FindSceneObjects<Rigidbody2D>()) {
                    if (rb == null || !rb.gameObject.activeInHierarchy)
                        continue;

                    total++;

                    if (!rb.IsSleeping())
                        active++;
                }
            }

            _sRb2dActive = active.ToString("N0");
            _sRb2dTotal = total.ToString("N0");
        }

        yield return null;

        _sCol3dActive = _recActiveCol3d.Valid && _recActiveCol3d.LastValue >= 0
            ? _recActiveCol3d.LastValue.ToString("N0")
            : "–";

        _sCol2dActive = _recActiveCol2d.Valid && _recActiveCol2d.LastValue >= 0
            ? _recActiveCol2d.LastValue.ToString("N0")
            : "–";

        {
            int box = 0;
            int sphere = 0;
            int capsule = 0;
            int mesh = 0;
            int convex = 0;
            int terrain = 0;
            int wheel = 0;
            int other = 0;

            foreach (Collider c in FindSceneObjects<Collider>()) {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy)
                    continue;

                if (c is BoxCollider)
                    box++;
                else if (c is SphereCollider)
                    sphere++;
                else if (c is CapsuleCollider)
                    capsule++;
                else if (c is MeshCollider mc) {
                    if (mc.convex)
                        convex++;
                    else
                        mesh++;
                }
                else if (c is TerrainCollider)
                    terrain++;
                else if (c is WheelCollider)
                    wheel++;
                else
                    other++;
            }

            _sColBox = box.ToString("N0");
            _sColSphere = sphere.ToString("N0");
            _sColCapsule = capsule.ToString("N0");
            _sColMesh = mesh.ToString("N0");
            _sColConvexMesh = convex.ToString("N0");
            _sColTerrain = terrain.ToString("N0");
            _sColWheel = wheel.ToString("N0");
            _sColOther3d = other.ToString("N0");
        }

        yield return null;

        {
            int box = 0;
            int circle = 0;
            int poly = 0;
            int edge = 0;
            int capsule = 0;
            int composite = 0;
            int other = 0;

            foreach (Collider2D c in FindSceneObjects<Collider2D>()) {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy)
                    continue;

                if (c is BoxCollider2D)
                    box++;
                else if (c is CircleCollider2D)
                    circle++;
                else if (c is PolygonCollider2D)
                    poly++;
                else if (c is EdgeCollider2D)
                    edge++;
                else if (c is CapsuleCollider2D)
                    capsule++;
                else if (c is CompositeCollider2D)
                    composite++;
                else
                    other++;
            }

            _sCol2dBox = box.ToString("N0");
            _sCol2dCircle = circle.ToString("N0");
            _sCol2dPoly = poly.ToString("N0");
            _sCol2dEdge = edge.ToString("N0");
            _sCol2dCapsule = capsule.ToString("N0");
            _sCol2dComposite = composite.ToString("N0");
            _sCol2dOther = other.ToString("N0");
        }

        yield return null;

        {
            int active = 0;
            int total = 0;

            foreach (ParticleSystem ps in FindSceneObjects<ParticleSystem>()) {
                if (ps == null || !ps.gameObject.activeInHierarchy)
                    continue;

                total++;

                if (ps.isPlaying || ps.isEmitting)
                    active++;
            }

            _sParticleSystemsActive = active.ToString("N0");
            _sParticleSystemsTotal = total.ToString("N0");
        }

        yield return null;

        {
            int audioSourcesActive = 0;
            int audioSourcesPlaying = 0;
            int audioSourcesTotal = 0;

            HashSet<AudioMixer> mixersInUse = new HashSet<AudioMixer>();

            foreach (AudioSource src in FindSceneObjects<AudioSource>()) {
                if (src == null || !src.gameObject.activeInHierarchy)
                    continue;

                audioSourcesTotal++;

                if (src.isActiveAndEnabled)
                    audioSourcesActive++;

                if (src.isPlaying) {
                    audioSourcesPlaying++;

                    if (src.outputAudioMixerGroup != null && src.outputAudioMixerGroup.audioMixer != null)
                        mixersInUse.Add(src.outputAudioMixerGroup.audioMixer);
                }
            }

            int audioClipsLoaded = 0;

            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>()) {
                if (clip != null)
                    audioClipsLoaded++;
            }

            int audioMixersLoaded = 0;

            foreach (AudioMixer mixer in Resources.FindObjectsOfTypeAll<AudioMixer>()) {
                if (mixer != null)
                    audioMixersLoaded++;
            }

            _sAudioSourcesActive = audioSourcesActive.ToString("N0");
            _sAudioSourcesPlaying = audioSourcesPlaying.ToString("N0");
            _sAudioSourcesTotal = audioSourcesTotal.ToString("N0");
            _sAudioClipsLoaded = audioClipsLoaded.ToString("N0");
            _sAudioMixersInUse = mixersInUse.Count.ToString("N0");
            _sAudioMixersLoaded = audioMixersLoaded.ToString("N0");
        }

        yield return null;

        {
            int trailActive = 0;
            int trailTotal = 0;

            foreach (TrailRenderer tr in FindSceneObjects<TrailRenderer>()) {
                if (tr == null || !tr.gameObject.activeInHierarchy)
                    continue;

                trailTotal++;

                if (tr.enabled)
                    trailActive++;
            }

            int lineActive = 0;
            int lineTotal = 0;

            foreach (LineRenderer lr in FindSceneObjects<LineRenderer>()) {
                if (lr == null || !lr.gameObject.activeInHierarchy)
                    continue;

                lineTotal++;

                if (lr.enabled)
                    lineActive++;
            }

            _sTrailRenderersActive = trailActive.ToString("N0");
            _sTrailRenderersTotal = trailTotal.ToString("N0");
            _sLineRenderersActive = lineActive.ToString("N0");
            _sLineRenderersTotal = lineTotal.ToString("N0");
        }

        yield return null;

        {
            int active = 0;
            int total = 0;

            foreach (Camera cam in FindSceneObjects<Camera>()) {
                if (cam == null || !cam.gameObject.activeInHierarchy)
                    continue;

                total++;

                if (cam.isActiveAndEnabled)
                    active++;
            }

            _sCamerasActive = active.ToString("N0");
            _sCamerasTotal = total.ToString("N0");
        }

        yield return new WaitForSecondsRealtime(sceneCountRefreshRate);
        _sceneCountRunning = false;
    }

    // ─── Canvas Text Update ───────────────────────────────────────────────────

    void UpdateCanvasTexts() {
        // Timing
        SetText(cpuMainText, "CPU Main", _sCpuMain, " ms");
        SetText(playerLoopText, "PlayerLoop", _sPlayerLoop, " ms");
        SetText(cameraRenderText, "Camera.Render", _sCameraRender, " ms");
        SetText(gpuFrameText, "GPU Frame", _sGpuFrame, " ms");

        // Rendering
        SetText(batchesText, "Batches", _sBatches);
        SetText(drawCallsText, "Draw Calls", _sDrawCalls);
        SetText(setPassText, "SetPass Calls", _sSetPass);
        SetText(trianglesText, "Triangles", _sTriangles);
        SetText(verticesText, "Vertices", _sVertices);
        SetText(shadowCastersText, "Shadow Casters", _sShadowCasters);
        SetText(visibleLightsText, "Visible Lights", _sVisibleLights);
        SetText(screenSizeText, "Screen Size", _sScreenSize);

        // RAM
        SetText(ramAllocatedText, "Unity Allocated", _sRamAllocated, " MB");
        SetText(ramReservedText, "Unity Reserved", _sRamReserved, " MB");
        SetText(ramUnusedReservedText, "Unused Reserved", _sRamUnusedReserved, " MB");
        SetText(ramUsedApproxText, "Used Approx", _sRamUsedApprox, " MB");
        SetText(systemRamTotalText, "System Total", _sRamTotal);
        SetText(gcHeapUsedText, "GC Heap Used", _sGcUsed, " MB");

        // GPU
        SetText(gpuUsedText, "GPU Used", _sGpuUsed, " MB");
        SetText(gpuReservedText, "GPU Reserved", _sGpuReserved, _sGpuReserved == "N/A" ? "" : " MB");
        SetText(gpuFreeText, "GPU Free", _sGpuFree);
        SetText(gpuTotalText, "GPU Total", _sGpuTotal);
        SetText(gpuSourceText, "GPU Source", _sGpuSource);
        SetText(usedBuffersText, "Used Buffers", _sUsedBuffers, " MB");
        SetText(renderTexturesText, "Render Textures", _sRenderTextures, " MB");
        SetText(videoMemoryText, "Video Memory", _sVideoMemory, " MB");

        // Assets
        SetText(textureCountText, "Textures", _sTextureCount);
        SetText(textureMemoryText, "Texture Memory", _sTextureMemory, " MB");
        SetText(meshCountText, "Meshes", _sMeshCount);
        SetText(meshMemoryText, "Mesh Memory", _sMeshMemory, " MB");
        SetText(audioMemoryText, "Audio Memory", _sAudioMemory, " MB");
        SetText(assetCountText, "Total Assets", _sAssetCount);

        // GC
        SetText(gcAllocFrameText, "Alloc / Frame", _sGcAllocFrame);
        SetText(gcCollectTimeText, "Collect Time", _sGcCollectMs, " ms");
        SetText(gcTotalCountText, "GC Total Count", _sGcTotalCount);
        SetText(gcPerSecondText, "GC / Second", _sGcPerSecond);
        SetText(gcActiveSamplesText, "GC Active Samples", _sGcActiveSamples);
        SetText(gcRanLatestSampleText, "GC Ran Latest Sample", _sGcRanLatestSample);
        SetText(gcAppTotalText, "GC App Total", _sGcAppTotalCount);
        SetText(monoHeapText, "Mono Heap", _sMonoHeap, " MB");
        SetText(monoUsedText, "Mono Used", _sMonoUsed, " MB");

        // Animation
        SetText(animatorUpdateText, "Animator.Update", _sAnimatorMs);
        SetText(animatorCountText, "Animators", _sAnimatorCount);

        // Physics 3D
        SetText(rb3dActiveText, "Rigidbodies Active", _sRb3dActive);
        SetText(rb3dTotalText, "Rigidbodies Total", _sRb3dTotal);
        SetText(col3dActiveText, "Colliders Active", _sCol3dActive);
        SetText(boxColliderText, "Box Colliders", _sColBox);
        SetText(sphereColliderText, "Sphere Colliders", _sColSphere);
        SetText(capsuleColliderText, "Capsule Colliders", _sColCapsule);
        SetText(meshColliderText, "Mesh Colliders", _sColMesh);
        SetText(convexMeshColliderText, "Convex Mesh Col.", _sColConvexMesh);
        SetText(terrainColliderText, "Terrain Colliders", _sColTerrain);
        SetText(wheelColliderText, "Wheel Colliders", _sColWheel);
        SetText(other3dColliderText, "Other 3D Col.", _sColOther3d);

        // Physics 2D
        SetText(rb2dActiveText, "Rigidbody2D Active", _sRb2dActive);
        SetText(rb2dTotalText, "Rigidbody2D Total", _sRb2dTotal);
        SetText(col2dActiveText, "Collider2D Active", _sCol2dActive);
        SetText(boxCollider2dText, "Box Collider2D", _sCol2dBox);
        SetText(circleCollider2dText, "Circle Collider2D", _sCol2dCircle);
        SetText(polygonCollider2dText, "Polygon Collider2D", _sCol2dPoly);
        SetText(edgeCollider2dText, "Edge Collider2D", _sCol2dEdge);
        SetText(capsuleCollider2dText, "Capsule Collider2D", _sCol2dCapsule);
        SetText(compositeCollider2dText, "Composite Col.2D", _sCol2dComposite);
        SetText(other2dColliderText, "Other 2D Col.", _sCol2dOther);

        // Particles
        SetText(particleSystemsActiveText, "Particle Systems Active", _sParticleSystemsActive);
        SetText(particleSystemsTotalText, "Particle Systems Total", _sParticleSystemsTotal);

        // Audio
        SetText(audioSourcesActiveText, "AudioSources Active", _sAudioSourcesActive);
        SetText(audioSourcesPlayingText, "AudioSources Playing", _sAudioSourcesPlaying);
        SetText(audioSourcesTotalText, "AudioSources Total", _sAudioSourcesTotal);
        SetText(audioClipsLoadedText, "AudioClips Loaded", _sAudioClipsLoaded);
        SetText(audioMixersInUseText, "AudioMixers In Use", _sAudioMixersInUse);
        SetText(audioMixersLoadedText, "AudioMixers Loaded", _sAudioMixersLoaded);

        // Trail / Line Renderers
        SetText(trailRenderersActiveText, "TrailRenderers Active", _sTrailRenderersActive);
        SetText(trailRenderersTotalText, "TrailRenderers Total", _sTrailRenderersTotal);
        SetText(lineRenderersActiveText, "LineRenderers Active", _sLineRenderersActive);
        SetText(lineRenderersTotalText, "LineRenderers Total", _sLineRenderersTotal);

        // Cameras
        SetText(camerasActiveText, "Cameras Active", _sCamerasActive);
        SetText(camerasTotalText, "Cameras Total", _sCamerasTotal);
    }

    void SetText(Text target, string label, string value, string suffix = "") {
        if (target == null)
            return;

        string finalText;

        finalText = string.IsNullOrEmpty(suffix) ? label + ": " + value : label + ": " + value + suffix;


        if (target.text != finalText)
            target.text = finalText;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    static void DisposeRecorder(ref ProfilerRecorder r) {
        if (r.Valid)
            r.Dispose();

        r = default;
    }

    static string RecMs(ProfilerRecorder r) {
        if (!r.Valid || r.Count == 0)
            return "–";

        double sum = 0;

        for (int i = 0; i < r.Count; i++)
            sum += r.GetSample(i).Value;

        return (sum / r.Count * 1e-6).ToString("F2");
    }

    static string LongStat(ProfilerRecorder r) {
        return r.Valid ? r.LastValue.ToString("N0") : "–";
    }

    static string BytesMB(long bytes) {
        return bytes > 0
            ? (bytes / 1_048_576f).ToString("F1")
            : "–";
    }

    static string BytesMB_Rec(ProfilerRecorder r) {
        return r.Valid && r.LastValue > 0
            ? BytesMB(r.LastValue)
            : "–";
    }

    static string FormatBytesSmart(long bytes) {
        if (bytes <= 0)
            return "0 B";

        if (bytes < 1024)
            return bytes + " B";

        if (bytes < 1024 * 1024)
            return (bytes / 1024f).ToString("F1") + " KB";

        return (bytes / 1_048_576f).ToString("F1") + " MB";
    }

    static int GetGcCollectionCountSafe(int generation) {
        if (generation < 0)
            return 0;

        if (generation > System.GC.MaxGeneration)
            return 0;

        return System.GC.CollectionCount(generation);
    }

    static T[] FindSceneObjects<T>() where T : UnityEngine.Object {
#if UNITY_2023_1_OR_NEWER
        return Object.FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
        return Object.FindObjectsOfType<T>();
#endif
    }
}