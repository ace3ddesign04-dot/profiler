using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// Comprehensive in-game performance overlay using ProfilerRecorder.
/// Works in Editor AND Release builds (no UNITY_EDITOR guards needed).
///
/// SETUP:
///   1. Attach to any persistent GameObject (DontDestroyOnLoad is called automatically).
///   2. Player Settings → Other Settings → Strip Engine Code = OFF
///      (otherwise profiler counters get stripped in release).
///   3. For GPU memory stats: device must support GPU profiling
///      (most modern Android Adreno/Mali GPUs do; iOS is limited).
///   4. Toggle overlay at runtime with the configured toggleKey (default F1).
/// </summary>
public class MyProfiler : MonoBehaviour {
    // ─── Inspector ────────────────────────────────────────────────────────────
    [Header("Toggle")]
    public bool showOnStart = true;
    public KeyCode toggleKey = KeyCode.F1;

    [Header("Layout")]
    public Vector2 panelPosition = new Vector2(10, 10);
    public float panelWidth = 360f;
    public int fontSize = 22;

    [Header("Refresh Rates")]
    [Tooltip("How often profiler recorder stats update (seconds).")]
    public float refreshRate = 0.25f;
    [Tooltip("How often scene-object counts update (FindObjectsOfType is slow).")]
    public float sceneCountRefreshRate = 1f;

    // ─── Profiler Recorders ───────────────────────────────────────────────────

    // Timing
    ProfilerRecorder _recCpuMain;           // "Main Thread"          Internal
    ProfilerRecorder _recPlayerLoop;        // "PlayerLoop"           Internal  ← matches Profiler window "PlayerLoop"
    ProfilerRecorder _recCameraRender;      // "Camera.Render"        Render    ← matches Profiler window "Camera.Render"
    ProfilerRecorder _recGpuFrame;          // "GPU Frame Time"       Internal

    // Rendering counters
    ProfilerRecorder _recBatches;
    ProfilerRecorder _recTriangles;
    ProfilerRecorder _recVertices;
    ProfilerRecorder _recSetPass;
    ProfilerRecorder _recDrawCalls;
    ProfilerRecorder _recShadowCasters;
    ProfilerRecorder _recVisibleLights;     // URP/HDRP only; scene-fallback if invalid

    // Memory – RAM
    ProfilerRecorder _recTotalUsed;         // "Total Used Memory"
    ProfilerRecorder _recTotalAllocated;    // "Total Allocated Memory"  ← was missing before
    ProfilerRecorder _recTotalReserved;     // "Total Reserved Memory"
    ProfilerRecorder _recGCUsed;            // "GC Used Memory"

    // Memory – GPU
    ProfilerRecorder _recGpuUsed;
    ProfilerRecorder _recGpuFree;
    ProfilerRecorder _recGpuAllocated;

    // Memory – Assets
    ProfilerRecorder _recTextureCount;
    ProfilerRecorder _recTextureMemory;
    ProfilerRecorder _recMeshCount;
    ProfilerRecorder _recAudioMemory;       // bonus: audio memory
    ProfilerRecorder _recAssetCount;        // bonus: total asset count

    // GC / Mono
    ProfilerRecorder _recGCAlloc;           // "GC.Alloc" per frame (bytes allocated)
    ProfilerRecorder _recGCCollect;         // "GC.Collect" time cost

    // Animation
    // NOTE: Unity has no "active animator count" counter — we use update-time markers instead.
    // "Animator.Update" (Animation category) = total animator update cost this frame.
    ProfilerRecorder _recAnimatorUpdate;

    // Physics (3D) — Unity 2022.2+ Physics category markers
    ProfilerRecorder _recActiveRb3d;        // "Active Rigidbodies"
    ProfilerRecorder _recActiveCol3d;       // "Active Colliders"

    // Physics (2D)
    ProfilerRecorder _recActiveRb2d;        // "Active Rigidbody2D"
    ProfilerRecorder _recActiveCol2d;       // "Active Collider2D"

    // ─── Cached display strings ───────────────────────────────────────────────
    // Timing
    string _sFps = "–";
    string _sCpuMain = "–";
    string _sPlayerLoop = "–";
    string _sCameraRender = "–";
    string _sGpuFrame = "–";

    // Rendering
    string _sBatches = "–";
    string _sDrawCalls = "–";
    string _sSetPass = "–";
    string _sTriangles = "–";
    string _sVertices = "–";
    string _sShadowCasters = "–";
    string _sVisibleLights = "–";
    string _sScreenSize = "–";

    // RAM
    string _sRamUsed = "–";
    string _sRamAllocated = "–";
    string _sRamReserved = "–";
    string _sRamTotal = "–";
    string _sGcUsed = "–";

    // GPU
    string _sGpuUsed = "–";
    string _sGpuFree = "–";
    string _sGpuAllocated = "–";

    // Assets
    string _sTextureCount = "–";
    string _sTextureMemory = "–";
    string _sMeshCount = "–";
    string _sAudioMemory = "–";
    string _sAssetCount = "–";

    // GC
    string _sGcAllocFrame = "–";
    string _sGcCollectMs = "–";
    string _sMonoHeap = "–";
    string _sMonoUsed = "–";

    // Animation
    string _sAnimatorMs = "–";
    string _sAnimatorCount = "–";   // scene fallback count

    // Physics 3D
    string _sRb3dActive = "–";
    string _sRb3dTotal = "–";
    // Physics 2D
    string _sRb2dActive = "–";
    string _sRb2dTotal = "–";

    // Colliders – broken down by type (scene counts)
    string _sColBox = "–";
    string _sColSphere = "–";
    string _sColCapsule = "–";
    string _sColMesh = "–";
    string _sColConvexMesh = "–";
    string _sColTerrain = "–";
    string _sColWheel = "–";
    string _sColOther3d = "–";
    // 2D colliders
    string _sCol2dBox = "–";
    string _sCol2dCircle = "–";
    string _sCol2dPoly = "–";
    string _sCol2dEdge = "–";
    string _sCol2dCapsule = "–";
    string _sCol2dComposite = "–";
    string _sCol2dOther = "–";

    // ─── Internal state ───────────────────────────────────────────────────────
    bool _visible;
    float _nextRefresh;
    float _nextSceneCount;
    GUIStyle _styleBox, _styleLabel, _styleHeader, _styleSectionLabel;
    bool _stylesReady;

    // ─── Awake ────────────────────────────────────────────────────────────────
    void Awake() {
        UnityEngine.Profiling.Profiler.enabled = true;
        _visible = showOnStart;

        // ── Timing ────────────────────────────────────────────────────────────
        // "Main Thread"  = raw total time the main thread was active this frame (ns)
        // "PlayerLoop"   = total scripted player loop cost — matches the "PlayerLoop" row
        //                  at the top of the Profiler Timeline window
        // "Camera.Render"= render cost per camera — matches the Profiler window row
        //                  (NOT "Render Thread" which is an OS thread label, not a marker)
        _recCpuMain = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
        _recPlayerLoop = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "PlayerLoop", 15);
        _recCameraRender = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Camera.Render", 15);
        _recGpuFrame = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GPU Frame Time", 15);

        // ── Rendering ─────────────────────────────────────────────────────────
        _recBatches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        _recTriangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        _recVertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
        _recSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        _recDrawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _recShadowCasters = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count");
        _recVisibleLights = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Visible Lights", 1);

        // ── RAM ───────────────────────────────────────────────────────────────
        // Total Used Memory    = memory Unity has actually filled with data
        // Total Allocated Memory = memory Unity has asked the OS for (committed)
        // Total Reserved Memory  = memory Unity has reserved (may not all be committed)
        // GC Used Memory         = size of the managed (Mono) GC heap that is in use
        _recTotalUsed = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
        _recTotalAllocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Allocated Memory");
        _recTotalReserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Reserved Memory");
        _recGCUsed = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Used Memory");

        // ── GPU Memory ────────────────────────────────────────────────────────
        // These are device-driver reported values. Not available on all platforms.
        // On Android (Adreno/Mali/Xclipse) they typically work.
        // On iOS Metal they are usually 0 / invalid — use Xcode Instruments there.
        _recGpuUsed = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GPU Used Memory");
        _recGpuFree = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GPU Free Memory");
        _recGpuAllocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GPU Reserved Memory");

        // ── Assets ────────────────────────────────────────────────────────────
        _recTextureCount = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Count");
        _recTextureMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
        _recMeshCount = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Mesh Count");
        _recAudioMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Audio Used Memory");
        _recAssetCount = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Asset Count");

        // ── GC ────────────────────────────────────────────────────────────────
        // "GC.Alloc"   = bytes allocated on the managed heap THIS frame (triggers GC if large)
        // "GC.Collect" = time spent in GC collection this frame (ns)
        _recGCAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 15);
        _recGCCollect = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Collect", 15);

        // ── Animation ─────────────────────────────────────────────────────────
        // "Animator.Update" is the correct marker name (Animation category).
        // If your project uses Unity 2022+ it may also appear as "AnimatorController.Update".
        _recAnimatorUpdate = ProfilerRecorder.StartNew(ProfilerCategory.Animation, "Animator.Update", 15);

        // ── Physics ───────────────────────────────────────────────────────────
        _recActiveRb3d = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Active Rigidbodies", 1);
        _recActiveCol3d = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Active Colliders", 1);
        _recActiveRb2d = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Active Rigidbody2D", 1);
        _recActiveCol2d = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Active Collider2D", 1);

        DontDestroyOnLoad(gameObject);
    }

    // ─── OnDestroy ────────────────────────────────────────────────────────────
    void OnDestroy() {
        _recCpuMain.Dispose(); _recPlayerLoop.Dispose();
        _recCameraRender.Dispose(); _recGpuFrame.Dispose();
        _recBatches.Dispose(); _recTriangles.Dispose();
        _recVertices.Dispose(); _recSetPass.Dispose();
        _recDrawCalls.Dispose(); _recShadowCasters.Dispose();
        _recVisibleLights.Dispose();
        _recTotalUsed.Dispose(); _recTotalAllocated.Dispose();
        _recTotalReserved.Dispose(); _recGCUsed.Dispose();
        _recGpuUsed.Dispose(); _recGpuFree.Dispose();
        _recGpuAllocated.Dispose();
        _recTextureCount.Dispose(); _recTextureMemory.Dispose();
        _recMeshCount.Dispose(); _recAudioMemory.Dispose();
        _recAssetCount.Dispose();
        _recGCAlloc.Dispose(); _recGCCollect.Dispose();
        _recAnimatorUpdate.Dispose();
        _recActiveRb3d.Dispose(); _recActiveCol3d.Dispose();
        _recActiveRb2d.Dispose(); _recActiveCol2d.Dispose();
    }

    // ─── Update ───────────────────────────────────────────────────────────────
    void Update() {
        if (Input.GetKeyDown(toggleKey))
            _visible = !_visible;

        if (!_visible) return;

        float t = Time.unscaledTime;
        if (t >= _nextRefresh) { _nextRefresh = t + refreshRate; RefreshRecorderStats(); }
        if (t >= _nextSceneCount) { _nextSceneCount = t + sceneCountRefreshRate; RefreshSceneCounts(); }
    }

    // ─── RefreshRecorderStats ─────────────────────────────────────────────────
    void RefreshRecorderStats() {
        // FPS
        _sFps = (1f / Time.unscaledDeltaTime).ToString("F1");

        // ── Timing ────────────────────────────────────────────────────────────
        _sCpuMain = RecMs(_recCpuMain);
        _sPlayerLoop = RecMs(_recPlayerLoop);
        _sCameraRender = RecMs(_recCameraRender);
        _sGpuFrame = RecMs(_recGpuFrame);

        // ── Rendering ─────────────────────────────────────────────────────────
        _sBatches = LongStat(_recBatches);
        _sDrawCalls = LongStat(_recDrawCalls);
        _sSetPass = LongStat(_recSetPass);
        _sTriangles = LongStat(_recTriangles);
        _sVertices = LongStat(_recVertices);
        _sShadowCasters = LongStat(_recShadowCasters);
        _sScreenSize = $"{Screen.width} × {Screen.height}";

        // ── RAM ───────────────────────────────────────────────────────────────
        // "Total Used Memory" = what Unity actually filled
        // "Total Allocated Memory" = what Unity committed from OS (≈ "Total Allocated" in Profiler window)
        // "Total Reserved Memory"  = what Unity reserved from OS  (≈ "Total Reserved" in Profiler window)
        // System total: SystemInfo.systemMemorySize (MB, integer)
        _sRamUsed = BytesMB_Rec(_recTotalUsed);
        _sRamAllocated = BytesMB_Rec(_recTotalAllocated);
        _sRamReserved = BytesMB_Rec(_recTotalReserved);
        _sRamTotal = ((long)SystemInfo.systemMemorySize).ToString("N0") + " MB";
        _sGcUsed = BytesMB_Rec(_recGCUsed);

        // ── GPU Memory ────────────────────────────────────────────────────────
        _sGpuUsed = BytesMB_Rec(_recGpuUsed);
        _sGpuFree = BytesMB_Rec(_recGpuFree);
        _sGpuAllocated = BytesMB_Rec(_recGpuAllocated);

        // ── Assets ────────────────────────────────────────────────────────────
        _sTextureCount = LongStat(_recTextureCount);
        _sTextureMemory = BytesMB_Rec(_recTextureMemory);
        _sMeshCount = LongStat(_recMeshCount);
        _sAudioMemory = BytesMB_Rec(_recAudioMemory);
        _sAssetCount = LongStat(_recAssetCount);

        // ── GC ────────────────────────────────────────────────────────────────
        // GC.Alloc per frame: recorder gives cumulative bytes; LastValue = this frame's allocs
        if (_recGCAlloc.Valid) {
            long allocBytes = _recGCAlloc.LastValue;
            _sGcAllocFrame = allocBytes > 1024
                ? (allocBytes / 1024f).ToString("F1") + " KB"
                : allocBytes + " B";
        }
        else _sGcAllocFrame = "–";

        _sGcCollectMs = RecMs(_recGCCollect);

        // Mono heap via Profiler API (always available, no marker needed)
        _sMonoHeap = BytesMB(UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong());
        _sMonoUsed = BytesMB(UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong());

        // ── Animation ─────────────────────────────────────────────────────────
        _sAnimatorMs = _recAnimatorUpdate.Valid ? RecMs(_recAnimatorUpdate) + " ms" : "–";
    }

    // ─── RefreshSceneCounts ───────────────────────────────────────────────────
    // Uses FindObjectsOfType — only runs every sceneCountRefreshRate seconds.
    void RefreshSceneCounts() {
        // ── Visible Lights ────────────────────────────────────────────────────
        if (_recVisibleLights.Valid && _recVisibleLights.LastValue > 0) {
            _sVisibleLights = _recVisibleLights.LastValue.ToString("N0");
        }
        else {
            int n = 0;
            foreach (var l in FindObjectsOfType<Light>())
                if (l.enabled && l.gameObject.activeInHierarchy) n++;
            _sVisibleLights = n + " (scene)";
        }

        // ── Animators ─────────────────────────────────────────────────────────
        // Count active Animator components (not culled = enabled + activeInHierarchy)
        {
            int total = 0, active = 0;
            foreach (var a in FindObjectsOfType<Animator>()) {
                total++;
                if (a.enabled && a.gameObject.activeInHierarchy && !a.cullingMode.Equals(AnimatorCullingMode.AlwaysAnimate) == false
                    || (a.enabled && a.isActiveAndEnabled))
                    active++;
            }
            // Simpler reliable version: just count enabled animators
            active = 0; total = 0;
            foreach (var a in FindObjectsOfType<Animator>()) {
                total++;
                if (a.isActiveAndEnabled) active++;
            }
            _sAnimatorCount = $"{active} active / {total} total";
        }

        // ── Rigidbodies 3D ────────────────────────────────────────────────────
        {
            int active = 0, total = 0;
            if (_recActiveRb3d.Valid && _recActiveRb3d.LastValue >= 0) {
                // recorder gives active count; do scene scan only for total
                active = (int)_recActiveRb3d.LastValue;
                foreach (var rb in FindObjectsOfType<Rigidbody>())
                    if (rb.gameObject.activeInHierarchy) total++;
            }
            else {
                foreach (var rb in FindObjectsOfType<Rigidbody>()) {
                    if (!rb.gameObject.activeInHierarchy) continue;
                    total++;
                    if (!rb.IsSleeping()) active++;
                }
            }
            _sRb3dActive = active.ToString("N0");
            _sRb3dTotal = total.ToString("N0");
        }

        // ── Rigidbodies 2D ────────────────────────────────────────────────────
        {
            int active = 0, total = 0;
            if (_recActiveRb2d.Valid && _recActiveRb2d.LastValue >= 0) {
                active = (int)_recActiveRb2d.LastValue;
                foreach (var rb in FindObjectsOfType<Rigidbody2D>())
                    if (rb.gameObject.activeInHierarchy) total++;
            }
            else {
                foreach (var rb in FindObjectsOfType<Rigidbody2D>()) {
                    if (!rb.gameObject.activeInHierarchy) continue;
                    total++;
                    if (!rb.IsSleeping()) active++;
                }
            }
            _sRb2dActive = active.ToString("N0");
            _sRb2dTotal = total.ToString("N0");
        }

        // ── 3D Colliders by type ──────────────────────────────────────────────
        {
            int box = 0, sphere = 0, capsule = 0, mesh = 0, convex = 0,
                terrain = 0, wheel = 0, other = 0;
            foreach (var c in FindObjectsOfType<Collider>()) {
                if (!c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (c is BoxCollider) box++;
                else if (c is SphereCollider) sphere++;
                else if (c is CapsuleCollider) capsule++;
                else if (c is MeshCollider mc) { if (mc.convex) convex++; else mesh++; }
                else if (c is TerrainCollider) terrain++;
                else if (c is WheelCollider) wheel++;
                else other++;
            }
            _sColBox = box.ToString();
            _sColSphere = sphere.ToString();
            _sColCapsule = capsule.ToString();
            _sColMesh = mesh.ToString();
            _sColConvexMesh = convex.ToString();
            _sColTerrain = terrain.ToString();
            _sColWheel = wheel.ToString();
            _sColOther3d = other.ToString();
        }

        // ── 2D Colliders by type ──────────────────────────────────────────────
        {
            int box = 0, circle = 0, poly = 0, edge = 0,
                capsule = 0, composite = 0, other = 0;
            foreach (var c in FindObjectsOfType<Collider2D>()) {
                if (!c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (c is BoxCollider2D) box++;
                else if (c is CircleCollider2D) circle++;
                else if (c is PolygonCollider2D) poly++;
                else if (c is EdgeCollider2D) edge++;
                else if (c is CapsuleCollider2D) capsule++;
                else if (c is CompositeCollider2D) composite++;
                else other++;
            }
            _sCol2dBox = box.ToString();
            _sCol2dCircle = circle.ToString();
            _sCol2dPoly = poly.ToString();
            _sCol2dEdge = edge.ToString();
            _sCol2dCapsule = capsule.ToString();
            _sCol2dComposite = composite.ToString();
            _sCol2dOther = other.ToString();
        }
    }

    // ─── OnGUI ────────────────────────────────────────────────────────────────
    void OnGUI() {
        if (!_visible) return;
        EnsureStyles();

        float dpi = Screen.dpi > 0 ? Screen.dpi : 160f;
        float scale = dpi / 160f;
        float pw = panelWidth * scale;
        float rowH = (fontSize + 5) * scale;
        float padV = 8f * scale;
        float padH = 10f * scale;

        // Build row list: (label, value, color)  — empty value = section header
        var rows = new List<(string lbl, string val, Color col)>();

        void Section(string title) => rows.Add((title, "", Color.yellow));
        void Row(string lbl, string val, Color col) => rows.Add((lbl, val, col));
        void RowW(string lbl, string val) => Row(lbl, val, Color.white);

        // ── TIMING ────────────────────────────────────────────────────────────
        Section("── TIMING ──");
        Row("FPS", _sFps, FpsColor());
        RowW("CPU Main", _sCpuMain + " ms");
        // PlayerLoop matches the top-level "PlayerLoop" entry in Unity Profiler window
        RowW("PlayerLoop", _sPlayerLoop + " ms");
        // Camera.Render matches the "Camera.Render" entry in Profiler window
        RowW("Camera.Render", _sCameraRender + " ms");
        RowW("GPU Frame", _sGpuFrame + " ms");

        // ── RENDERING ─────────────────────────────────────────────────────────
        Section("── RENDERING ──");
        RowW("Batches", _sBatches);
        RowW("Draw Calls", _sDrawCalls);
        RowW("SetPass Calls", _sSetPass);
        RowW("Triangles", _sTriangles);
        RowW("Vertices", _sVertices);
        RowW("Shadow Casters", _sShadowCasters);
        RowW("Visible Lights", _sVisibleLights);
        RowW("Screen Size", _sScreenSize);

        // ── RAM ───────────────────────────────────────────────────────────────
        Section("── RAM ──");
        RowW("Used", _sRamUsed + " MB");
        RowW("Allocated", _sRamAllocated + " MB");
        RowW("Reserved", _sRamReserved + " MB");
        RowW("System Total", _sRamTotal);
        RowW("GC Heap Used", _sGcUsed + " MB");

        // ── GPU MEMORY ────────────────────────────────────────────────────────
        Section("── GPU MEMORY ──");
        RowW("Used", _sGpuUsed + " MB");
        RowW("Free", _sGpuFree + " MB");
        RowW("Allocated", _sGpuAllocated + " MB");

        // ── ASSETS ────────────────────────────────────────────────────────────
        Section("── ASSETS ──");
        RowW("Textures", _sTextureCount);
        RowW("Texture Memory", _sTextureMemory + " MB");
        RowW("Meshes", _sMeshCount);
        RowW("Audio Memory", _sAudioMemory + " MB");
        RowW("Total Assets", _sAssetCount);

        // ── GARBAGE COLLECTOR ─────────────────────────────────────────────────
        Section("── GARBAGE COLLECTOR ──");
        RowW("Alloc / Frame", _sGcAllocFrame);
        RowW("Collect Time", _sGcCollectMs + " ms");
        RowW("Mono Heap", _sMonoHeap + " MB");
        RowW("Mono Used", _sMonoUsed + " MB");

        // ── ANIMATION ─────────────────────────────────────────────────────────
        Section("── ANIMATION ──");
        RowW("Animator.Update", _sAnimatorMs);
        RowW("Animators", _sAnimatorCount);

        // ── PHYSICS 3D ────────────────────────────────────────────────────────
        Section("── PHYSICS 3D ──");
        RowW("Rigidbodies Active", _sRb3dActive);
        RowW("Rigidbodies Total", _sRb3dTotal);
        RowW("Box Colliders", _sColBox);
        RowW("Sphere Colliders", _sColSphere);
        RowW("Capsule Colliders", _sColCapsule);
        RowW("Mesh Colliders", _sColMesh);
        RowW("Convex Mesh Col.", _sColConvexMesh);
        RowW("Terrain Colliders", _sColTerrain);
        RowW("Wheel Colliders", _sColWheel);
        if (_sColOther3d != "0") RowW("Other 3D Col.", _sColOther3d);

        // ── PHYSICS 2D ────────────────────────────────────────────────────────
        Section("── PHYSICS 2D ──");
        RowW("Rigidbody2D Active", _sRb2dActive);
        RowW("Rigidbody2D Total", _sRb2dTotal);
        RowW("Box Collider2D", _sCol2dBox);
        RowW("Circle Collider2D", _sCol2dCircle);
        RowW("Polygon Collider2D", _sCol2dPoly);
        RowW("Edge Collider2D", _sCol2dEdge);
        RowW("Capsule Collider2D", _sCol2dCapsule);
        RowW("Composite Col.2D", _sCol2dComposite);
        if (_sCol2dOther != "0") RowW("Other 2D Col.", _sCol2dOther);

        // ── Draw panel ────────────────────────────────────────────────────────
        float panelH = rows.Count * rowH + padV * 2f + rowH * 1.5f;
        Rect panel = new Rect(panelPosition.x * scale, panelPosition.y * scale, pw, panelH);
        GUI.Box(panel, "", _styleBox);

        float y = panel.y + padV;
        GUI.Label(new Rect(panel.x + padH, y, pw - padH * 2f, rowH * 1.5f),
                  "PERFORMANCE STATS", _styleHeader);
        y += rowH * 1.5f;

        float colW = (pw - padH * 2f) * 0.60f;

        foreach (var (lbl, val, col) in rows) {
            if (string.IsNullOrEmpty(val)) {
                _styleLabel.fontStyle = FontStyle.Bold;
                _styleLabel.normal.textColor = col;
                _styleLabel.alignment = TextAnchor.MiddleLeft;
                GUI.Label(new Rect(panel.x + padH, y, pw - padH * 2f, rowH), lbl, _styleLabel);
                _styleLabel.fontStyle = FontStyle.Normal;
            }
            else {
                _styleLabel.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
                _styleLabel.alignment = TextAnchor.MiddleLeft;
                GUI.Label(new Rect(panel.x + padH, y, colW, rowH), lbl, _styleLabel);

                _styleLabel.normal.textColor = col;
                _styleLabel.alignment = TextAnchor.MiddleRight;
                GUI.Label(new Rect(panel.x + padH + colW, y, pw - padH * 2f - colW, rowH), val, _styleLabel);
                _styleLabel.alignment = TextAnchor.MiddleLeft;
            }
            y += rowH;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // Average sample ring-buffer (ns → ms)
    static string RecMs(ProfilerRecorder r) {
        if (!r.Valid || r.Count == 0) return "–";
        double sum = 0;
        for (int i = 0; i < r.Count; i++) sum += r.GetSample(i).Value;
        return (sum / r.Count * 1e-6).ToString("F2");
    }

    static string LongStat(ProfilerRecorder r) =>
        r.Valid ? r.LastValue.ToString("N0") : "–";

    static string BytesMB(long b) =>
        b > 0 ? (b / 1_048_576f).ToString("F1") : "–";

    static string BytesMB_Rec(ProfilerRecorder r) =>
        (r.Valid && r.LastValue > 0) ? BytesMB(r.LastValue) : "–";

    Color FpsColor() {
        if (!float.TryParse(_sFps, out float f)) return Color.white;
        return f >= 55 ? Color.green : f >= 30 ? Color.yellow : Color.red;
    }

    void EnsureStyles() {
        if (_stylesReady) return;
        _stylesReady = true;

        float dpi = Screen.dpi > 0 ? Screen.dpi : 160f;
        int fs = Mathf.RoundToInt(fontSize * dpi / 160f);
        var bg = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.82f));

        _styleBox = new GUIStyle(GUI.skin.box) { normal = { background = bg } };

        _styleLabel = new GUIStyle(GUI.skin.label) {
            fontSize = fs,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(0, 0, 0, 0),
            normal = { textColor = Color.white }
        };

        _styleHeader = new GUIStyle(_styleLabel) {
            fontSize = fs + 2,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.45f, 0.9f, 1f) }
        };

        _styleSectionLabel = new GUIStyle(_styleLabel) {
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.yellow }
        };
    }

    static Texture2D MakeTex(int w, int h, Color col) {
        var p = new Color[w * h];
        for (int i = 0; i < p.Length; i++) p[i] = col;
        var t = new Texture2D(w, h);
        t.SetPixels(p); t.Apply();
        return t;
    }
}