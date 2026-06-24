using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Release-build scene scanner for level/performance profiling.
///
/// New workflow:
/// 1. Attach this script to an empty GameObject.
/// 2. Assign Target Camera.
/// 3. Assign FPSCounter / PerformanceMonitor / DeviceThermal, or keep Auto Find enabled.
/// 4. In Thresholds, add elements and select Stat Item from the enum dropdown.
/// 5. Set comparison + threshold.
/// 6. Run scan with F8, stop with F9, or call StartScan() / StopScanAndWriteReport().
///
/// Report writes JSON to:
/// Application.persistentDataPath / reportFolderName
///
/// Report contains only violated threshold records.
/// No LINQ.
/// Uses UnityEngine.UI.Text because your profiler scripts expose Text fields.
/// </summary>
public class ReleaseBuildSceneScanner : MonoBehaviour {
    public enum ScanMode {
        AutoBounds,
        Waypoints,
        Manual
    }

    public enum NumericComparison {
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        Equal,
        NotEqual
    }

    public enum AutoRotationMode {
        KeepInitialRotation,
        FixedEuler,
        FaceTravelDirection
    }

    public enum WaypointRotationMode {
        /// <summary>
        /// While moving, camera faces path direction.
        /// At each waypoint, camera stops, rotates to waypoint rotation, pauses,
        /// then rotates back to next path direction before moving again.
        /// </summary>
        UseWaypointRotations,

        /// <summary>
        /// Camera always faces movement/path direction.
        /// </summary>
        FacePathDirection,

        /// <summary>
        /// Camera keeps the rotation it had when scan started.
        /// </summary>
        KeepInitialRotation
    }

    public enum StatItem {
        FPS_Current,
        FPS_Average,
        FPS_Min,
        FPS_Max,

        Perf_CPU_Main,
        Perf_PlayerLoop,
        Perf_CameraRender,
        Perf_GPU_Frame,

        Perf_Batches,
        Perf_DrawCalls,
        Perf_SetPass,
        Perf_Triangles,
        Perf_Vertices,
        Perf_ShadowCasters,
        Perf_VisibleLights,
        Perf_ScreenSize,

        Perf_RAM_Allocated,
        Perf_RAM_Reserved,
        Perf_RAM_UnusedReserved,
        Perf_RAM_UsedApprox,
        Perf_SystemRAM_Total,
        Perf_GC_HeapUsed,

        Perf_GPU_Used,
        Perf_GPU_Reserved,
        Perf_GPU_Free,
        Perf_GPU_Total,
        Perf_GPU_Source,
        Perf_UsedBuffers,
        Perf_RenderTextures,
        Perf_VideoMemory,

        Perf_TextureCount,
        Perf_TextureMemory,
        Perf_MeshCount,
        Perf_MeshMemory,
        Perf_AudioMemory,
        Perf_AssetCount,

        Perf_GC_AllocFrame,
        Perf_GC_CollectTime,
        Perf_GC_TotalCount,
        Perf_GC_PerSecond,
        Perf_GC_ActiveSamples,
        Perf_GC_RanLatestSample,
        Perf_GC_AppTotal,
        Perf_MonoHeap,
        Perf_MonoUsed,

        Perf_AnimatorUpdate,
        Perf_AnimatorCount,

        Perf_Rigidbody3D_Active,
        Perf_Rigidbody3D_Total,
        Perf_Collider3D_Active,
        Perf_BoxCollider,
        Perf_SphereCollider,
        Perf_CapsuleCollider,
        Perf_MeshCollider,
        Perf_ConvexMeshCollider,
        Perf_TerrainCollider,
        Perf_WheelCollider,
        Perf_Other3DCollider,

        Perf_Rigidbody2D_Active,
        Perf_Rigidbody2D_Total,
        Perf_Collider2D_Active,
        Perf_BoxCollider2D,
        Perf_CircleCollider2D,
        Perf_PolygonCollider2D,
        Perf_EdgeCollider2D,
        Perf_CapsuleCollider2D,
        Perf_CompositeCollider2D,
        Perf_Other2DCollider,

        Perf_ParticleSystems_Active,
        Perf_ParticleSystems_Total,

        Perf_AudioSources_Active,
        Perf_AudioSources_Playing,
        Perf_AudioSources_Total,
        Perf_AudioClips_Loaded,
        Perf_AudioMixers_InUse,
        Perf_AudioMixers_Loaded,

        Perf_TrailRenderers_Active,
        Perf_TrailRenderers_Total,
        Perf_LineRenderers_Active,
        Perf_LineRenderers_Total,

        Perf_Cameras_Active,
        Perf_Cameras_Total,

        Thermal_Elapsed,
        Thermal_BatteryTemp,
        Thermal_CPUTemp,
        Thermal_GPUTemp,
        Thermal_Status
    }

    [Serializable]
    public class StatThreshold {
        public bool enabled = true;

        [Tooltip("Select stat directly from enum. No manual Text reference required.")]
        public StatItem statItem;

        public NumericComparison comparison = NumericComparison.GreaterThan;
        public float threshold = 0f;

        [Tooltip("Recommended ON. Records only when this stat enters violation state instead of recording every sample while bad.")]
        public bool recordOnlyOnViolationEnter = true;

        [Tooltip("Used only when Record Only On Violation Enter is OFF. Prevents report spam while stat remains bad.")]
        public float sustainedViolationCooldown = 2f;

        [NonSerialized] public bool wasViolating;
        [NonSerialized] public float lastViolationRecordTime = -999999f;
    }

    [Serializable]
    public class ScanReport {
        public string project;
        public string mode;
        public string startedAtLocal;
        public string endedAtLocal;
        public float durationSeconds;
        public int totalSamples;
        public int totalViolations;
        public int configuredThresholdCount;
        public int enabledThresholdCount;
        public string unityVersion;
        public string platform;
        public Vector3 boundsCenter;
        public Vector3 boundsSize;
        public List<ViolationRecord> violations = new List<ViolationRecord>();
    }

    [Serializable]
    public class ViolationRecord {
        public int sampleIndex;
        public string statId;
        public string displayName;
        public string comparison;
        public float threshold;
        public float value;
        public string sceneName;
        public float scanTimeSeconds;
        public int frame;
        public Vector3 cameraPosition;
        public Vector3 cameraEulerAngles;
        public string pathLabel;
    }

    private struct PathPoint {
        public Vector3 position;
        public Quaternion rotation;
        public string label;

        public PathPoint(Vector3 position, Quaternion rotation, string label) {
            this.position = position;
            this.rotation = rotation;
            this.label = label;
        }
    }

    [Header("Scanner")]
    public ScanMode scanMode = ScanMode.AutoBounds;
    public Camera targetCamera;
    public bool startOnPlay;
    public bool useUnscaledTime = true;
    [Tooltip("Used only in Manual mode. If true, pressing Stop in Manual mode also invokes On Scan Completed.")]
    public bool manualStopCountsAsCompleted = true;
    public KeyCode startKey = KeyCode.F8;
    public KeyCode stopKey = KeyCode.F9;

    [Header("Output Information")]
    public Text textScanProgress;

    [Header("Profiler Sources")]
    [Tooltip("If enabled, scanner tries to find FPSCounter / PerformanceMonitor / DeviceThermal automatically on Awake and StartScan.")]
    public bool autoFindProfilerComponents = true;

    public FPSCounter fpsCounter;
    public PerformanceMonitor performanceMonitor;
    public DeviceThermal deviceThermal;

    [Header("Movement")]
    [Min(0.01f)] public float moveSpeed = 8f;
    [Min(1f)] public float rotationSpeedDegrees = 360f;
    [Min(0.01f)] public float sampleInterval = 0.25f;
    [Min(0f)] public float warmupSecondsBeforeScan = 1f;
    public bool returnCameraToOriginalPoseWhenFinished;

    [Header("Auto Bounds Scan")]
    public Vector3 boundsCenter = Vector3.zero;
    public Vector3 boundsSize = new Vector3(100f, 15f, 100f);
    [Min(0.1f)] public float scanLineSpacing = 10f;
    [Min(1)] public int heightLayers = 1;
    public bool reverseEveryHeightLayer = true;
    public AutoRotationMode autoRotationMode = AutoRotationMode.KeepInitialRotation;
    public Vector3 fixedAutoRotationEuler = new Vector3(25f, 0f, 0f);
    public Vector3 faceTravelRotationOffsetEuler = Vector3.zero;

    [Header("Waypoint Scan")]
    public Transform waypointsContainer;
    public bool includeInactiveWaypoints;
    public WaypointRotationMode waypointRotationMode = WaypointRotationMode.UseWaypointRotations;
    public bool loopWaypointPath;
    public Vector3 facePathRotationOffsetEuler = Vector3.zero;

    [Tooltip("Used only when Waypoint Rotation Mode = Use Waypoint Rotations.")]
    [Min(0f)] public float waypointRotationPauseSeconds = 2f;

    [Tooltip("If enabled, scanner also pauses and matches rotation at the first waypoint before starting movement.")]
    public bool pauseAtFirstWaypoint;

    [Header("Thresholds")]
    public List<StatThreshold> thresholds = new List<StatThreshold>();

    [Tooltip("Safety limit so a broken threshold cannot create a massive JSON file.")]
    [Min(1)] public int maxViolationsInReport = 10000;

    [Header("Report Output")]
    public string reportFolderName = "SceneScanReports";
    public string reportFilePrefix = Application.productName + "_SceneScan";
    [TextArea(1, 3)] public string lastReportPath;

    [Header("Editor Report Viewer")]
    public bool openReportWindowWhenScanCompletes = true; 
    
    [Header("Runtime Report GUI")]
    public RuntimeSceneScanReportGUI runtimeReportGUI;
    public bool openRuntimeReportGUIWhenScanCompletes = true;

    [Header("Runtime Progress - Read Only")]
    public bool isScanning;
    [Range(0f, 1f)] public float scanProgress01;
    public string scanProgressText;
    public int currentSampleIndex;
    public int currentViolationCount;
    public Vector3 currentCameraPosition;
    public Vector3 currentCameraEulerAngles;
    public string currentPathLabel;

    [Header("Gizmos")]
    public bool drawGizmos = true;
    public bool drawGizmosOnlyWhenSelected = false;
    public bool drawAutoScanPath = true;
    public bool drawWaypointPath = true;
    public Color boundsGizmoColor = new Color(0f, 1f, 1f, 0.7f);
    public Color autoPathGizmoColor = new Color(0.2f, 0.8f, 1f, 0.8f);
    public Color waypointPathGizmoColor = new Color(1f, 0.85f, 0f, 0.9f);
    public Color waypointForwardGizmoColor = new Color(0.2f, 1f, 0.2f, 0.9f);
    [Min(0.1f)] public float waypointForwardArrowLength = 4f;
    [Min(0.05f)] public float gizmoSphereRadius = 0.8f;

    [Header("Scan Events")]
    public UnityEvent onScanStarted;
    public UnityEvent onScanStopped;
    public UnityEvent onScanCompleted;

    private readonly List<PathPoint> _path = new List<PathPoint>();
    private Coroutine _scanRoutine;
    private ScanReport _activeReport;
    private float _scanStartRealtime;
    private float _nextSampleRealtime;
    private Vector3 _originalCameraPosition;
    private Quaternion _originalCameraRotation;
    private Quaternion _initialScannerRotation;

    private static readonly Regex NumberRegex = new Regex(@"[-+]?\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);

    private void Awake() {
        if (targetCamera == null)
            targetCamera = Camera.main;

        _initialScannerRotation = targetCamera != null ? targetCamera.transform.rotation : transform.rotation;

        if (autoFindProfilerComponents)
            AutoFindProfilers();
    }

    private void Start() {
        if (startOnPlay)
            StartScan();
    }

    private void Update() {
        if (Input.GetKeyDown(startKey))
            StartScan();

        if (Input.GetKeyDown(stopKey))
            StopScanAndWriteReport();

        if (targetCamera != null) {
            currentCameraPosition = targetCamera.transform.position;
            currentCameraEulerAngles = targetCamera.transform.eulerAngles;
        }

        if (textScanProgress != null)
            textScanProgress.text = scanProgressText;
    }

    [ContextMenu("Auto Find Profilers")]
    public void AutoFindProfilers() {
        if (fpsCounter == null)
            fpsCounter = FindObjectOfType<FPSCounter>();

        if (performanceMonitor == null)
            performanceMonitor = FindObjectOfType<PerformanceMonitor>();

        if (deviceThermal == null)
            deviceThermal = FindObjectOfType<DeviceThermal>();
    }

    [ContextMenu("Start Scan")]
    public void StartScan() {
        if (targetCamera == null)
            targetCamera = Camera.main;

        if (targetCamera == null) {
            Debug.LogWarning("ReleaseBuildSceneScanner: Target Camera is missing.", this);
            return;
        }

        if (autoFindProfilerComponents)
            AutoFindProfilers();

        if (_scanRoutine != null)
            StopCoroutine(_scanRoutine);

        ResetThresholdRuntimeState();

        _path.Clear();

        if (scanMode == ScanMode.AutoBounds)
            BuildAutoBoundsPath(_path);
        else if (scanMode == ScanMode.Waypoints)
            BuildWaypointPath(_path);

        if (scanMode != ScanMode.Manual && _path.Count == 0) {
            Debug.LogWarning("ReleaseBuildSceneScanner: No scan path generated.", this);
            return;
        }

        _originalCameraPosition = targetCamera.transform.position;
        _originalCameraRotation = targetCamera.transform.rotation;
        _initialScannerRotation = targetCamera.transform.rotation;

        _scanRoutine = StartCoroutine(ScanRoutine());
    }

    [ContextMenu("Stop Scan And Write Report")]
    public void StopScanAndWriteReport() {
        bool wasScanning = isScanning || _scanRoutine != null;
        bool wasManualScan = scanMode == ScanMode.Manual;

        if (_scanRoutine != null) {
            StopCoroutine(_scanRoutine);
            _scanRoutine = null;
        }

        if (isScanning)
            FinishReportAndWriteFile();

        isScanning = false;

        if (wasManualScan)
            scanProgressText = "Manual Scan Stopped";
        else
            scanProgressText = "Stopped";

        if (wasScanning) {
            if (wasManualScan && manualStopCountsAsCompleted)
                onScanCompleted?.Invoke();

            onScanStopped?.Invoke();
        }
    }

    [ContextMenu("Clear Runtime Report Data")]
    public void ClearRuntimeReportData() {
        lastReportPath = string.Empty;
        scanProgress01 = 0f;
        scanProgressText = string.Empty;
        textScanProgress.text = "";
        currentSampleIndex = 0;
        currentViolationCount = 0;
        currentPathLabel = string.Empty;
        _activeReport = null;
    }

    private void ResetThresholdRuntimeState() {
        for (int i = 0; i < thresholds.Count; i++) {
            StatThreshold t = thresholds[i];
            if (t == null)
                continue;

            t.wasViolating = false;
            t.lastViolationRecordTime = -999999f;
        }
    }

    private IEnumerator ScanRoutine() {
        isScanning = true;
        scanProgress01 = 0f;
        scanProgressText = "Starting";
        currentSampleIndex = 0;
        currentViolationCount = 0;
        currentPathLabel = string.Empty;

        _scanStartRealtime = Time.realtimeSinceStartup;
        _nextSampleRealtime = Time.realtimeSinceStartup + warmupSecondsBeforeScan;

        onScanStarted?.Invoke();

        _activeReport = new ScanReport();
        _activeReport.project = Application.productName;
        _activeReport.mode = scanMode.ToString();
        _activeReport.startedAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        _activeReport.unityVersion = Application.unityVersion;
        _activeReport.platform = Application.platform.ToString();
        _activeReport.boundsCenter = boundsCenter;
        _activeReport.boundsSize = boundsSize;
        _activeReport.configuredThresholdCount = thresholds != null ? thresholds.Count : 0;
        _activeReport.enabledThresholdCount = CountEnabledThresholds();

        if (scanMode == ScanMode.Manual) {
            yield return RunManualScan();
            yield break;
        }

        targetCamera.transform.position = _path[0].position;
        targetCamera.transform.rotation = GetStartRotation();
        currentPathLabel = _path[0].label;

        if (warmupSecondsBeforeScan > 0f) {
            float warmupStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - warmupStart < warmupSecondsBeforeScan) {
                scanProgressText = "Warming up";
                TrySampleStats(false);
                yield return null;
            }
        }

        TrySampleStats(true);

        if (scanMode == ScanMode.Waypoints && waypointRotationMode == WaypointRotationMode.UseWaypointRotations)
            yield return RunWaypointRotationStopAndLookScan();
        else
            yield return RunNormalPathScan();

        scanProgress01 = 1f;
        scanProgressText = "Writing report";
        TrySampleStats(true);
        FinishReportAndWriteFile();

        if (returnCameraToOriginalPoseWhenFinished) {
            targetCamera.transform.position = _originalCameraPosition;
            targetCamera.transform.rotation = _originalCameraRotation;
        }

        isScanning = false;
        _scanRoutine = null;
        scanProgressText = "Finished";

        onScanCompleted?.Invoke();
        onScanStopped?.Invoke();
    }
    private IEnumerator RunManualScan() {
        currentPathLabel = "Manual";
        scanProgress01 = 0f;
        scanProgressText = "Manual Scan Starting";

        if (warmupSecondsBeforeScan > 0f) {
            float warmupStart = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - warmupStart < warmupSecondsBeforeScan) {
                scanProgressText = "Manual Scan Warming Up";
                TrySampleStats(false);
                yield return null;
            }
        }

        TrySampleStats(true);

        while (isScanning) {
            currentPathLabel = "Manual";

            if (targetCamera != null) {
                currentCameraPosition = targetCamera.transform.position;
                currentCameraEulerAngles = targetCamera.transform.eulerAngles;
            }

            scanProgressText =
                "Manual Scan Running | Samples: " +
                currentSampleIndex +
                " | Violations: " +
                currentViolationCount;

            TrySampleStats(false);

            yield return null;
        }
    }

    private Quaternion GetStartRotation() {
        if (scanMode == ScanMode.AutoBounds)
            return _path[0].rotation;

        if (waypointRotationMode == WaypointRotationMode.KeepInitialRotation)
            return _initialScannerRotation;

        if (waypointRotationMode == WaypointRotationMode.FacePathDirection) {
            if (_path.Count > 1) {
                Vector3 direction = _path[1].position - _path[0].position;
                if (direction.sqrMagnitude > 0.0001f)
                    return Quaternion.LookRotation(direction.normalized, Vector3.up) * Quaternion.Euler(facePathRotationOffsetEuler);
            }
        }

        if (waypointRotationMode == WaypointRotationMode.UseWaypointRotations) {
            if (pauseAtFirstWaypoint)
                return _path[0].rotation;

            if (_path.Count > 1) {
                Vector3 direction = _path[1].position - _path[0].position;
                if (direction.sqrMagnitude > 0.0001f)
                    return Quaternion.LookRotation(direction.normalized, Vector3.up) * Quaternion.Euler(facePathRotationOffsetEuler);
            }
        }

        return _path[0].rotation;
    }

    private IEnumerator RunNormalPathScan() {
        float totalDistance = CalculatePathDistance(_path);
        float previousDistanceBeforeSegment = 0f;

        for (int i = 1; i < _path.Count; i++) {
            PathPoint from = _path[i - 1];
            PathPoint to = _path[i];

            yield return MoveSegment(
                from,
                to,
                previousDistanceBeforeSegment,
                totalDistance
            );

            previousDistanceBeforeSegment += Vector3.Distance(from.position, to.position);
        }
    }

    private IEnumerator RunWaypointRotationStopAndLookScan() {
        float totalDistance = CalculatePathDistance(_path);
        float previousDistanceBeforeSegment = 0f;

        if (pauseAtFirstWaypoint && _path.Count > 0) {
            currentPathLabel = _path[0].label;
            yield return RotateTo(_path[0].rotation, "Waypoint Look: " + _path[0].label);
            yield return PauseAndSample(waypointRotationPauseSeconds, "Waypoint Pause: " + _path[0].label);
        }

        for (int i = 1; i < _path.Count; i++) {
            PathPoint from = _path[i - 1];
            PathPoint to = _path[i];

            Vector3 moveDirection = to.position - from.position;
            Quaternion pathRotation = GetPathDirectionRotation(moveDirection);

            yield return RotateTo(pathRotation, "Path Direction: " + to.label);

            PathPoint moveFrom = new PathPoint(from.position, pathRotation, from.label);
            PathPoint moveTo = new PathPoint(to.position, pathRotation, to.label);

            yield return MoveSegment(
                moveFrom,
                moveTo,
                previousDistanceBeforeSegment,
                totalDistance
            );

            previousDistanceBeforeSegment += Vector3.Distance(from.position, to.position);

            targetCamera.transform.position = to.position;
            currentPathLabel = to.label;

            yield return RotateTo(to.rotation, "Waypoint Look: " + to.label);
            yield return PauseAndSample(waypointRotationPauseSeconds, "Waypoint Pause: " + to.label);

            if (i < _path.Count - 1) {
                Vector3 nextDirection = _path[i + 1].position - to.position;
                Quaternion nextPathRotation = GetPathDirectionRotation(nextDirection);
                yield return RotateTo(nextPathRotation, "Next Path Direction: " + _path[i + 1].label);
            }
        }
    }

    private IEnumerator MoveSegment(
        PathPoint from,
        PathPoint to,
        float previousDistanceBeforeSegment,
        float totalDistance
    ) {
        Vector3 segment = to.position - from.position;
        float segmentLength = segment.magnitude;

        currentPathLabel = to.label;

        if (segmentLength <= 0.0001f) {
            targetCamera.transform.position = to.position;
            targetCamera.transform.rotation = to.rotation;
            TrySampleStats(false);
            yield break;
        }

        Vector3 direction = segment / segmentLength;
        float segmentTravel = 0f;

        while (segmentTravel < segmentLength) {
            float dt = GetDeltaTime();
            float step = Mathf.Max(0.001f, moveSpeed) * dt;
            float nextTravel = Mathf.Min(segmentLength, segmentTravel + step);
            float actualStep = nextTravel - segmentTravel;
            segmentTravel = nextTravel;

            float t = segmentTravel / segmentLength;

            targetCamera.transform.position = Vector3.Lerp(from.position, to.position, t);
            targetCamera.transform.rotation = GetInterpolatedRotation(from.rotation, to.rotation, direction, t);

            float traveledDistance = previousDistanceBeforeSegment + segmentTravel;
            scanProgress01 = totalDistance > 0f ? Mathf.Clamp01(traveledDistance / totalDistance) : 1f;
            scanProgressText = Mathf.RoundToInt(scanProgress01 * 100f) + "%";

            TrySampleStats(false);

            if (actualStep <= 0f)
                break;

            yield return null;
        }

        targetCamera.transform.position = to.position;
        targetCamera.transform.rotation = GetInterpolatedRotation(from.rotation, to.rotation, direction, 1f);
        TrySampleStats(true);
    }

    private IEnumerator RotateTo(Quaternion targetRotation, string label) {
        currentPathLabel = label;

        while (Quaternion.Angle(targetCamera.transform.rotation, targetRotation) > 0.1f) {
            float step = Mathf.Max(1f, rotationSpeedDegrees) * GetDeltaTime();
            targetCamera.transform.rotation = Quaternion.RotateTowards(targetCamera.transform.rotation, targetRotation, step);

            TrySampleStats(false);
            yield return null;
        }

        targetCamera.transform.rotation = targetRotation;
        TrySampleStats(true);
    }

    private IEnumerator PauseAndSample(float seconds, string label) {
        currentPathLabel = label;

        if (seconds <= 0f) {
            TrySampleStats(true);
            yield break;
        }

        float start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < seconds) {
            TrySampleStats(false);
            yield return null;
        }

        TrySampleStats(true);
    }

    private Quaternion GetInterpolatedRotation(Quaternion fromRotation, Quaternion toRotation, Vector3 direction, float t) {
        if (scanMode == ScanMode.AutoBounds) {
            if (autoRotationMode == AutoRotationMode.FaceTravelDirection) {
                Quaternion desired = GetPathDirectionRotation(direction, faceTravelRotationOffsetEuler);
                return Quaternion.RotateTowards(targetCamera.transform.rotation, desired, rotationSpeedDegrees * GetDeltaTime());
            }

            return Quaternion.Slerp(fromRotation, toRotation, t);
        }

        if (waypointRotationMode == WaypointRotationMode.FacePathDirection) {
            Quaternion desired = GetPathDirectionRotation(direction, facePathRotationOffsetEuler);
            return Quaternion.RotateTowards(targetCamera.transform.rotation, desired, rotationSpeedDegrees * GetDeltaTime());
        }

        if (waypointRotationMode == WaypointRotationMode.KeepInitialRotation)
            return _initialScannerRotation;

        if (waypointRotationMode == WaypointRotationMode.UseWaypointRotations) {
            Quaternion desired = GetPathDirectionRotation(direction, facePathRotationOffsetEuler);
            return Quaternion.RotateTowards(targetCamera.transform.rotation, desired, rotationSpeedDegrees * GetDeltaTime());
        }

        return Quaternion.Slerp(fromRotation, toRotation, t);
    }

    private Quaternion GetPathDirectionRotation(Vector3 direction) {
        return GetPathDirectionRotation(direction, facePathRotationOffsetEuler);
    }

    private Quaternion GetPathDirectionRotation(Vector3 direction, Vector3 offsetEuler) {
        if (direction.sqrMagnitude < 0.0001f)
            direction = targetCamera != null ? targetCamera.transform.forward : transform.forward;

        if (direction.sqrMagnitude < 0.0001f)
            direction = Vector3.forward;

        return Quaternion.LookRotation(direction.normalized, Vector3.up) * Quaternion.Euler(offsetEuler);
    }

    private float GetDeltaTime() {
        return useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
    }

    private void TrySampleStats(bool force) {
        float now = Time.realtimeSinceStartup;

        if (!force && now < _nextSampleRealtime)
            return;

        _nextSampleRealtime = now + Mathf.Max(0.01f, sampleInterval);
        currentSampleIndex++;

        if (_activeReport != null)
            _activeReport.totalSamples = currentSampleIndex;

        for (int i = 0; i < thresholds.Count; i++) {
            StatThreshold thresholdConfig = thresholds[i];

            if (thresholdConfig == null || !thresholdConfig.enabled)
                continue;

            Text text = GetTextForStat(thresholdConfig.statItem);
            if (text == null)
                continue;

            string raw = text.text;
            float value;

            if (!TryParseFirstNumberAfterColon(raw, out value))
                continue;

            bool violated = IsViolation(value, thresholdConfig.comparison, thresholdConfig.threshold);

            if (violated) {
                bool shouldRecord;

                if (thresholdConfig.recordOnlyOnViolationEnter)
                    shouldRecord = !thresholdConfig.wasViolating;
                else
                    shouldRecord = now - thresholdConfig.lastViolationRecordTime >= Mathf.Max(0f, thresholdConfig.sustainedViolationCooldown);

                if (
                    shouldRecord &&
                    _activeReport != null &&
                    _activeReport.violations.Count < maxViolationsInReport
                ) {
                    thresholdConfig.lastViolationRecordTime = now;
                    AddViolation(thresholdConfig, value, raw);
                }
            }

            thresholdConfig.wasViolating = violated;
        }
    }

    private void AddViolation(StatThreshold thresholdConfig, float value, string rawText) {
        ViolationRecord record = new ViolationRecord();
        record.sampleIndex = currentSampleIndex;
        record.statId = thresholdConfig.statItem.ToString();
        record.displayName = GetDisplayName(thresholdConfig.statItem);
        record.comparison = thresholdConfig.comparison.ToString();
        record.threshold = thresholdConfig.threshold;
        record.value = value;
        record.sceneName = SceneManager.GetActiveScene().name;
        record.scanTimeSeconds = Time.realtimeSinceStartup - _scanStartRealtime;
        record.frame = Time.frameCount;
        record.cameraPosition = targetCamera.transform.position;
        record.cameraEulerAngles = targetCamera.transform.eulerAngles;
        record.pathLabel = currentPathLabel;

        _activeReport.violations.Add(record);
        _activeReport.totalViolations = _activeReport.violations.Count;
        currentViolationCount = _activeReport.totalViolations;
    }

    private bool TryParseFirstNumberAfterColon(string raw, out float value) {
        value = 0f;

        if (string.IsNullOrEmpty(raw))
            return false;

        string parseArea = raw;
        int colon = raw.IndexOf(':');

        if (colon >= 0 && colon + 1 < raw.Length)
            parseArea = raw.Substring(colon + 1);

        Match match = NumberRegex.Match(parseArea);

        if (!match.Success)
            return false;

        string number = match.Value.Replace(",", string.Empty);
        return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private bool IsViolation(float value, NumericComparison comparison, float threshold) {
        switch (comparison) {
            case NumericComparison.GreaterThan:
                return value > threshold;

            case NumericComparison.GreaterThanOrEqual:
                return value >= threshold;

            case NumericComparison.LessThan:
                return value < threshold;

            case NumericComparison.LessThanOrEqual:
                return value <= threshold;

            case NumericComparison.Equal:
                return Mathf.Approximately(value, threshold);

            case NumericComparison.NotEqual:
                return !Mathf.Approximately(value, threshold);
        }

        return false;
    }

    private int CountEnabledThresholds() {
        int count = 0;

        if (thresholds == null)
            return 0;

        for (int i = 0; i < thresholds.Count; i++) {
            if (thresholds[i] != null && thresholds[i].enabled)
                count++;
        }

        return count;
    }

    private void FinishReportAndWriteFile() {
        if (_activeReport == null)
            return;

        _activeReport.endedAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        _activeReport.durationSeconds = Time.realtimeSinceStartup - _scanStartRealtime;
        _activeReport.totalSamples = currentSampleIndex;
        _activeReport.totalViolations = _activeReport.violations.Count;

        string folder = Path.Combine(Application.persistentDataPath, reportFolderName);

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        string safeScene = MakeFileSafe(SceneManager.GetActiveScene().name);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string fileName = reportFilePrefix + "_" + safeScene + "_" + timestamp + ".json";
        string path = Path.Combine(folder, fileName);

        string json = JsonUtility.ToJson(_activeReport, true);
        File.WriteAllText(path, json);
        lastReportPath = path;

        Debug.Log("ReleaseBuildSceneScanner report written: " + path, this);
        if (openRuntimeReportGUIWhenScanCompletes && runtimeReportGUI != null) {
            runtimeReportGUI.ShowAndLoadReport(path);
        }
#if UNITY_EDITOR
        if (openReportWindowWhenScanCompletes) {
            UnityEditor.EditorPrefs.SetString("ReleaseBuildSceneScanner.LatestReportPath", path);

            UnityEditor.EditorApplication.delayCall += () => {
                UnityEditor.EditorApplication.ExecuteMenuItem("Tools/Scene Scanner/Report Viewer");
            };
        }
#endif
    }

    private string MakeFileSafe(string value) {
        if (string.IsNullOrEmpty(value))
            return "Scene";

        char[] invalid = Path.GetInvalidFileNameChars();
        System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length);

        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            bool bad = false;

            for (int j = 0; j < invalid.Length; j++) {
                if (c == invalid[j]) {
                    bad = true;
                    break;
                }
            }

            builder.Append(bad ? '_' : c);
        }

        return builder.ToString();
    }

    private void BuildAutoBoundsPath(List<PathPoint> output) {
        output.Clear();

        Vector3 size = new Vector3(
            Mathf.Max(0.1f, Mathf.Abs(boundsSize.x)),
            Mathf.Max(0.1f, Mathf.Abs(boundsSize.y)),
            Mathf.Max(0.1f, Mathf.Abs(boundsSize.z))
        );

        Vector3 min = boundsCenter - size * 0.5f;
        Vector3 max = boundsCenter + size * 0.5f;

        int rows = Mathf.Max(2, Mathf.FloorToInt(size.z / Mathf.Max(0.1f, scanLineSpacing)) + 1);
        int layers = Mathf.Max(1, heightLayers);

        Quaternion fixedRotation = _initialScannerRotation;

        if (autoRotationMode == AutoRotationMode.FixedEuler)
            fixedRotation = Quaternion.Euler(fixedAutoRotationEuler);

        for (int layer = 0; layer < layers; layer++) {
            float layerT = layers == 1 ? 0.5f : (float)layer / (layers - 1);
            float y = Mathf.Lerp(min.y, max.y, layerT);
            bool reverseLayer = reverseEveryHeightLayer && (layer % 2 == 1);

            for (int r = 0; r < rows; r++) {
                int rowIndex = reverseLayer ? rows - 1 - r : r;
                float rowT = rows == 1 ? 0.5f : (float)rowIndex / (rows - 1);
                float z = Mathf.Lerp(min.z, max.z, rowT);

                bool leftToRight = (r % 2 == 0);

                if (reverseLayer)
                    leftToRight = !leftToRight;

                Vector3 a = new Vector3(leftToRight ? min.x : max.x, y, z);
                Vector3 b = new Vector3(leftToRight ? max.x : min.x, y, z);

                output.Add(new PathPoint(a, fixedRotation, "Auto L" + layer + " R" + rowIndex + " A"));
                output.Add(new PathPoint(b, fixedRotation, "Auto L" + layer + " R" + rowIndex + " B"));
            }
        }

        if (autoRotationMode == AutoRotationMode.FaceTravelDirection)
            ApplyFaceTravelRotations(output, faceTravelRotationOffsetEuler);
    }

    private void BuildWaypointPath(List<PathPoint> output) {
        output.Clear();

        if (waypointsContainer == null)
            return;

        Quaternion keepRotation = _initialScannerRotation;

        for (int i = 0; i < waypointsContainer.childCount; i++) {
            Transform child = waypointsContainer.GetChild(i);

            if (child == null)
                continue;

            if (!includeInactiveWaypoints && !child.gameObject.activeInHierarchy)
                continue;

            Quaternion rotation = child.rotation;

            if (waypointRotationMode == WaypointRotationMode.KeepInitialRotation)
                rotation = keepRotation;

            output.Add(new PathPoint(child.position, rotation, child.name));
        }

        if (loopWaypointPath && output.Count > 1)
            output.Add(output[0]);

        if (waypointRotationMode == WaypointRotationMode.FacePathDirection)
            ApplyFaceTravelRotations(output, facePathRotationOffsetEuler);
    }

    private void ApplyFaceTravelRotations(List<PathPoint> points, Vector3 rotationOffsetEuler) {
        if (points.Count == 0)
            return;

        for (int i = 0; i < points.Count; i++) {
            Vector3 direction = Vector3.forward;

            if (i < points.Count - 1)
                direction = points[i + 1].position - points[i].position;
            else if (i > 0)
                direction = points[i].position - points[i - 1].position;

            if (direction.sqrMagnitude < 0.0001f)
                direction = targetCamera != null ? targetCamera.transform.forward : transform.forward;

            Quaternion rot = Quaternion.LookRotation(direction.normalized, Vector3.up) * Quaternion.Euler(rotationOffsetEuler);
            PathPoint p = points[i];
            p.rotation = rot;
            points[i] = p;
        }
    }

    private float CalculatePathDistance(List<PathPoint> points) {
        float distance = 0f;

        for (int i = 1; i < points.Count; i++)
            distance += Vector3.Distance(points[i - 1].position, points[i].position);

        return distance;
    }

    private void OnDrawGizmos() {
        if (drawGizmosOnlyWhenSelected)
            return;

        DrawScannerGizmos();
    }

    private void OnDrawGizmosSelected() {
        DrawScannerGizmos();
    }

    private void DrawScannerGizmos() {
        if (!drawGizmos)
            return;

        if (scanMode == ScanMode.AutoBounds) {
            Gizmos.color = boundsGizmoColor;
            Gizmos.DrawWireCube(boundsCenter, boundsSize);

            if (drawAutoScanPath) {
                List<PathPoint> previewPath = new List<PathPoint>();
                BuildAutoBoundsPath(previewPath);
                DrawPath(previewPath, autoPathGizmoColor, true);
            }
        }
        else if (scanMode == ScanMode.Waypoints) {
            if (drawWaypointPath) {
                List<PathPoint> previewPath = new List<PathPoint>();
                BuildWaypointPath(previewPath);
                DrawPath(previewPath, waypointPathGizmoColor, true);
            }

            DrawWaypointForwardArrows();
        }
    }

    private void DrawPath(List<PathPoint> points, Color color, bool drawSpheres) {
        if (points == null || points.Count == 0)
            return;

        Gizmos.color = color;

        for (int i = 0; i < points.Count; i++) {
            if (drawSpheres)
                Gizmos.DrawWireSphere(points[i].position, gizmoSphereRadius);

            if (i < points.Count - 1)
                Gizmos.DrawLine(points[i].position, points[i + 1].position);
        }
    }

    private void DrawWaypointForwardArrows() {
        if (waypointsContainer == null)
            return;

        Gizmos.color = waypointForwardGizmoColor;

        for (int i = 0; i < waypointsContainer.childCount; i++) {
            Transform child = waypointsContainer.GetChild(i);

            if (child == null)
                continue;

            if (!includeInactiveWaypoints && !child.gameObject.activeInHierarchy)
                continue;

            DrawArrow(child.position, child.forward, waypointForwardArrowLength);
        }
    }

    private void DrawArrow(Vector3 start, Vector3 direction, float length) {
        if (direction.sqrMagnitude < 0.0001f)
            return;

        Vector3 dir = direction.normalized;
        Vector3 end = start + dir * length;

        Gizmos.DrawLine(start, end);

        Quaternion look = Quaternion.LookRotation(dir, Vector3.up);
        Vector3 rightWing = look * Quaternion.Euler(0f, 150f, 0f) * Vector3.forward;
        Vector3 leftWing = look * Quaternion.Euler(0f, -150f, 0f) * Vector3.forward;
        float wingLength = length * 0.25f;

        Gizmos.DrawLine(end, end + rightWing * wingLength);
        Gizmos.DrawLine(end, end + leftWing * wingLength);
    }

    private Text GetTextForStat(StatItem statItem) {
        switch (statItem) {
            case StatItem.FPS_Current:
                return fpsCounter != null ? fpsCounter.textCurrentFPS : null;

            case StatItem.FPS_Average:
                return fpsCounter != null ? fpsCounter.textAverageFPS : null;

            case StatItem.FPS_Min:
                return fpsCounter != null ? fpsCounter.textMinFPS : null;

            case StatItem.FPS_Max:
                return fpsCounter != null ? fpsCounter.textMaxFPS : null;

            case StatItem.Perf_CPU_Main:
                return performanceMonitor != null ? performanceMonitor.cpuMainText : null;

            case StatItem.Perf_PlayerLoop:
                return performanceMonitor != null ? performanceMonitor.playerLoopText : null;

            case StatItem.Perf_CameraRender:
                return performanceMonitor != null ? performanceMonitor.cameraRenderText : null;

            case StatItem.Perf_GPU_Frame:
                return performanceMonitor != null ? performanceMonitor.gpuFrameText : null;

            case StatItem.Perf_Batches:
                return performanceMonitor != null ? performanceMonitor.batchesText : null;

            case StatItem.Perf_DrawCalls:
                return performanceMonitor != null ? performanceMonitor.drawCallsText : null;

            case StatItem.Perf_SetPass:
                return performanceMonitor != null ? performanceMonitor.setPassText : null;

            case StatItem.Perf_Triangles:
                return performanceMonitor != null ? performanceMonitor.trianglesText : null;

            case StatItem.Perf_Vertices:
                return performanceMonitor != null ? performanceMonitor.verticesText : null;

            case StatItem.Perf_ShadowCasters:
                return performanceMonitor != null ? performanceMonitor.shadowCastersText : null;

            case StatItem.Perf_VisibleLights:
                return performanceMonitor != null ? performanceMonitor.visibleLightsText : null;

            case StatItem.Perf_ScreenSize:
                return performanceMonitor != null ? performanceMonitor.screenSizeText : null;

            case StatItem.Perf_RAM_Allocated:
                return performanceMonitor != null ? performanceMonitor.ramAllocatedText : null;

            case StatItem.Perf_RAM_Reserved:
                return performanceMonitor != null ? performanceMonitor.ramReservedText : null;

            case StatItem.Perf_RAM_UnusedReserved:
                return performanceMonitor != null ? performanceMonitor.ramUnusedReservedText : null;

            case StatItem.Perf_RAM_UsedApprox:
                return performanceMonitor != null ? performanceMonitor.ramUsedApproxText : null;

            case StatItem.Perf_SystemRAM_Total:
                return performanceMonitor != null ? performanceMonitor.systemRamTotalText : null;

            case StatItem.Perf_GC_HeapUsed:
                return performanceMonitor != null ? performanceMonitor.gcHeapUsedText : null;

            case StatItem.Perf_GPU_Used:
                return performanceMonitor != null ? performanceMonitor.gpuUsedText : null;

            case StatItem.Perf_GPU_Reserved:
                return performanceMonitor != null ? performanceMonitor.gpuReservedText : null;

            case StatItem.Perf_GPU_Free:
                return performanceMonitor != null ? performanceMonitor.gpuFreeText : null;

            case StatItem.Perf_GPU_Total:
                return performanceMonitor != null ? performanceMonitor.gpuTotalText : null;

            case StatItem.Perf_GPU_Source:
                return performanceMonitor != null ? performanceMonitor.gpuSourceText : null;

            case StatItem.Perf_UsedBuffers:
                return performanceMonitor != null ? performanceMonitor.usedBuffersText : null;

            case StatItem.Perf_RenderTextures:
                return performanceMonitor != null ? performanceMonitor.renderTexturesText : null;

            case StatItem.Perf_VideoMemory:
                return performanceMonitor != null ? performanceMonitor.videoMemoryText : null;

            case StatItem.Perf_TextureCount:
                return performanceMonitor != null ? performanceMonitor.textureCountText : null;

            case StatItem.Perf_TextureMemory:
                return performanceMonitor != null ? performanceMonitor.textureMemoryText : null;

            case StatItem.Perf_MeshCount:
                return performanceMonitor != null ? performanceMonitor.meshCountText : null;

            case StatItem.Perf_MeshMemory:
                return performanceMonitor != null ? performanceMonitor.meshMemoryText : null;

            case StatItem.Perf_AudioMemory:
                return performanceMonitor != null ? performanceMonitor.audioMemoryText : null;

            case StatItem.Perf_AssetCount:
                return performanceMonitor != null ? performanceMonitor.assetCountText : null;

            case StatItem.Perf_GC_AllocFrame:
                return performanceMonitor != null ? performanceMonitor.gcAllocFrameText : null;

            case StatItem.Perf_GC_CollectTime:
                return performanceMonitor != null ? performanceMonitor.gcCollectTimeText : null;

            case StatItem.Perf_GC_TotalCount:
                return performanceMonitor != null ? performanceMonitor.gcTotalCountText : null;

            case StatItem.Perf_GC_PerSecond:
                return performanceMonitor != null ? performanceMonitor.gcPerSecondText : null;

            case StatItem.Perf_GC_ActiveSamples:
                return performanceMonitor != null ? performanceMonitor.gcActiveSamplesText : null;

            case StatItem.Perf_GC_RanLatestSample:
                return performanceMonitor != null ? performanceMonitor.gcRanLatestSampleText : null;

            case StatItem.Perf_GC_AppTotal:
                return performanceMonitor != null ? performanceMonitor.gcAppTotalText : null;

            case StatItem.Perf_MonoHeap:
                return performanceMonitor != null ? performanceMonitor.monoHeapText : null;

            case StatItem.Perf_MonoUsed:
                return performanceMonitor != null ? performanceMonitor.monoUsedText : null;

            case StatItem.Perf_AnimatorUpdate:
                return performanceMonitor != null ? performanceMonitor.animatorUpdateText : null;

            case StatItem.Perf_AnimatorCount:
                return performanceMonitor != null ? performanceMonitor.animatorCountText : null;

            case StatItem.Perf_Rigidbody3D_Active:
                return performanceMonitor != null ? performanceMonitor.rb3dActiveText : null;

            case StatItem.Perf_Rigidbody3D_Total:
                return performanceMonitor != null ? performanceMonitor.rb3dTotalText : null;

            case StatItem.Perf_Collider3D_Active:
                return performanceMonitor != null ? performanceMonitor.col3dActiveText : null;

            case StatItem.Perf_BoxCollider:
                return performanceMonitor != null ? performanceMonitor.boxColliderText : null;

            case StatItem.Perf_SphereCollider:
                return performanceMonitor != null ? performanceMonitor.sphereColliderText : null;

            case StatItem.Perf_CapsuleCollider:
                return performanceMonitor != null ? performanceMonitor.capsuleColliderText : null;

            case StatItem.Perf_MeshCollider:
                return performanceMonitor != null ? performanceMonitor.meshColliderText : null;

            case StatItem.Perf_ConvexMeshCollider:
                return performanceMonitor != null ? performanceMonitor.convexMeshColliderText : null;

            case StatItem.Perf_TerrainCollider:
                return performanceMonitor != null ? performanceMonitor.terrainColliderText : null;

            case StatItem.Perf_WheelCollider:
                return performanceMonitor != null ? performanceMonitor.wheelColliderText : null;

            case StatItem.Perf_Other3DCollider:
                return performanceMonitor != null ? performanceMonitor.other3dColliderText : null;

            case StatItem.Perf_Rigidbody2D_Active:
                return performanceMonitor != null ? performanceMonitor.rb2dActiveText : null;

            case StatItem.Perf_Rigidbody2D_Total:
                return performanceMonitor != null ? performanceMonitor.rb2dTotalText : null;

            case StatItem.Perf_Collider2D_Active:
                return performanceMonitor != null ? performanceMonitor.col2dActiveText : null;

            case StatItem.Perf_BoxCollider2D:
                return performanceMonitor != null ? performanceMonitor.boxCollider2dText : null;

            case StatItem.Perf_CircleCollider2D:
                return performanceMonitor != null ? performanceMonitor.circleCollider2dText : null;

            case StatItem.Perf_PolygonCollider2D:
                return performanceMonitor != null ? performanceMonitor.polygonCollider2dText : null;

            case StatItem.Perf_EdgeCollider2D:
                return performanceMonitor != null ? performanceMonitor.edgeCollider2dText : null;

            case StatItem.Perf_CapsuleCollider2D:
                return performanceMonitor != null ? performanceMonitor.capsuleCollider2dText : null;

            case StatItem.Perf_CompositeCollider2D:
                return performanceMonitor != null ? performanceMonitor.compositeCollider2dText : null;

            case StatItem.Perf_Other2DCollider:
                return performanceMonitor != null ? performanceMonitor.other2dColliderText : null;

            case StatItem.Perf_ParticleSystems_Active:
                return performanceMonitor != null ? performanceMonitor.particleSystemsActiveText : null;

            case StatItem.Perf_ParticleSystems_Total:
                return performanceMonitor != null ? performanceMonitor.particleSystemsTotalText : null;

            case StatItem.Perf_AudioSources_Active:
                return performanceMonitor != null ? performanceMonitor.audioSourcesActiveText : null;

            case StatItem.Perf_AudioSources_Playing:
                return performanceMonitor != null ? performanceMonitor.audioSourcesPlayingText : null;

            case StatItem.Perf_AudioSources_Total:
                return performanceMonitor != null ? performanceMonitor.audioSourcesTotalText : null;

            case StatItem.Perf_AudioClips_Loaded:
                return performanceMonitor != null ? performanceMonitor.audioClipsLoadedText : null;

            case StatItem.Perf_AudioMixers_InUse:
                return performanceMonitor != null ? performanceMonitor.audioMixersInUseText : null;

            case StatItem.Perf_AudioMixers_Loaded:
                return performanceMonitor != null ? performanceMonitor.audioMixersLoadedText : null;

            case StatItem.Perf_TrailRenderers_Active:
                return performanceMonitor != null ? performanceMonitor.trailRenderersActiveText : null;

            case StatItem.Perf_TrailRenderers_Total:
                return performanceMonitor != null ? performanceMonitor.trailRenderersTotalText : null;

            case StatItem.Perf_LineRenderers_Active:
                return performanceMonitor != null ? performanceMonitor.lineRenderersActiveText : null;

            case StatItem.Perf_LineRenderers_Total:
                return performanceMonitor != null ? performanceMonitor.lineRenderersTotalText : null;

            case StatItem.Perf_Cameras_Active:
                return performanceMonitor != null ? performanceMonitor.camerasActiveText : null;

            case StatItem.Perf_Cameras_Total:
                return performanceMonitor != null ? performanceMonitor.camerasTotalText : null;

            case StatItem.Thermal_Elapsed:
                return deviceThermal != null ? deviceThermal.elapsedText : null;

            case StatItem.Thermal_BatteryTemp:
                return deviceThermal != null ? deviceThermal.batteryTempText : null;

            case StatItem.Thermal_CPUTemp:
                return deviceThermal != null ? deviceThermal.cpuTempText : null;

            case StatItem.Thermal_GPUTemp:
                return deviceThermal != null ? deviceThermal.gpuTempText : null;

            case StatItem.Thermal_Status:
                return deviceThermal != null ? deviceThermal.thermalStatusText : null;
        }

        return null;
    }

    private string GetDisplayName(StatItem statItem) {
        switch (statItem) {
            case StatItem.FPS_Current: return "FPS Current";
            case StatItem.FPS_Average: return "FPS Average";
            case StatItem.FPS_Min: return "FPS Min";
            case StatItem.FPS_Max: return "FPS Max";

            case StatItem.Perf_CPU_Main: return "CPU Main";
            case StatItem.Perf_PlayerLoop: return "Player Loop";
            case StatItem.Perf_CameraRender: return "Camera Render";
            case StatItem.Perf_GPU_Frame: return "GPU Frame";

            case StatItem.Perf_Batches: return "Batches";
            case StatItem.Perf_DrawCalls: return "Draw Calls";
            case StatItem.Perf_SetPass: return "Set Pass";
            case StatItem.Perf_Triangles: return "Triangles";
            case StatItem.Perf_Vertices: return "Vertices";
            case StatItem.Perf_ShadowCasters: return "Shadow Casters";
            case StatItem.Perf_VisibleLights: return "Visible Lights";
            case StatItem.Perf_ScreenSize: return "Screen Size";

            case StatItem.Perf_RAM_Allocated: return "RAM Allocated";
            case StatItem.Perf_RAM_Reserved: return "RAM Reserved";
            case StatItem.Perf_RAM_UnusedReserved: return "RAM Unused Reserved";
            case StatItem.Perf_RAM_UsedApprox: return "RAM Used Approx";
            case StatItem.Perf_SystemRAM_Total: return "System RAM Total";
            case StatItem.Perf_GC_HeapUsed: return "GC Heap Used";

            case StatItem.Perf_GPU_Used: return "GPU Used";
            case StatItem.Perf_GPU_Reserved: return "GPU Reserved";
            case StatItem.Perf_GPU_Free: return "GPU Free";
            case StatItem.Perf_GPU_Total: return "GPU Total";
            case StatItem.Perf_GPU_Source: return "GPU Source";
            case StatItem.Perf_UsedBuffers: return "Used Buffers";
            case StatItem.Perf_RenderTextures: return "Render Textures";
            case StatItem.Perf_VideoMemory: return "Video Memory";

            case StatItem.Perf_TextureCount: return "Texture Count";
            case StatItem.Perf_TextureMemory: return "Texture Memory";
            case StatItem.Perf_MeshCount: return "Mesh Count";
            case StatItem.Perf_MeshMemory: return "Mesh Memory";
            case StatItem.Perf_AudioMemory: return "Audio Memory";
            case StatItem.Perf_AssetCount: return "Asset Count";

            case StatItem.Perf_GC_AllocFrame: return "GC Alloc Frame";
            case StatItem.Perf_GC_CollectTime: return "GC Collect Time";
            case StatItem.Perf_GC_TotalCount: return "GC Total Count";
            case StatItem.Perf_GC_PerSecond: return "GC Per Second";
            case StatItem.Perf_GC_ActiveSamples: return "GC Active Samples";
            case StatItem.Perf_GC_RanLatestSample: return "GC Ran Latest Sample";
            case StatItem.Perf_GC_AppTotal: return "GC App Total";
            case StatItem.Perf_MonoHeap: return "Mono Heap";
            case StatItem.Perf_MonoUsed: return "Mono Used";

            case StatItem.Perf_AnimatorUpdate: return "Animator Update";
            case StatItem.Perf_AnimatorCount: return "Animator Count";

            case StatItem.Perf_Rigidbody3D_Active: return "Rigidbody 3D Active";
            case StatItem.Perf_Rigidbody3D_Total: return "Rigidbody 3D Total";
            case StatItem.Perf_Collider3D_Active: return "Collider 3D Active";
            case StatItem.Perf_BoxCollider: return "Box Collider";
            case StatItem.Perf_SphereCollider: return "Sphere Collider";
            case StatItem.Perf_CapsuleCollider: return "Capsule Collider";
            case StatItem.Perf_MeshCollider: return "Mesh Collider";
            case StatItem.Perf_ConvexMeshCollider: return "Convex Mesh Collider";
            case StatItem.Perf_TerrainCollider: return "Terrain Collider";
            case StatItem.Perf_WheelCollider: return "Wheel Collider";
            case StatItem.Perf_Other3DCollider: return "Other 3D Collider";

            case StatItem.Perf_Rigidbody2D_Active: return "Rigidbody 2D Active";
            case StatItem.Perf_Rigidbody2D_Total: return "Rigidbody 2D Total";
            case StatItem.Perf_Collider2D_Active: return "Collider 2D Active";
            case StatItem.Perf_BoxCollider2D: return "Box Collider 2D";
            case StatItem.Perf_CircleCollider2D: return "Circle Collider 2D";
            case StatItem.Perf_PolygonCollider2D: return "Polygon Collider 2D";
            case StatItem.Perf_EdgeCollider2D: return "Edge Collider 2D";
            case StatItem.Perf_CapsuleCollider2D: return "Capsule Collider 2D";
            case StatItem.Perf_CompositeCollider2D: return "Composite Collider 2D";
            case StatItem.Perf_Other2DCollider: return "Other 2D Collider";

            case StatItem.Perf_ParticleSystems_Active: return "Particle Systems Active";
            case StatItem.Perf_ParticleSystems_Total: return "Particle Systems Total";

            case StatItem.Perf_AudioSources_Active: return "Audio Sources Active";
            case StatItem.Perf_AudioSources_Playing: return "Audio Sources Playing";
            case StatItem.Perf_AudioSources_Total: return "Audio Sources Total";
            case StatItem.Perf_AudioClips_Loaded: return "Audio Clips Loaded";
            case StatItem.Perf_AudioMixers_InUse: return "Audio Mixers In Use";
            case StatItem.Perf_AudioMixers_Loaded: return "Audio Mixers Loaded";

            case StatItem.Perf_TrailRenderers_Active: return "Trail Renderers Active";
            case StatItem.Perf_TrailRenderers_Total: return "Trail Renderers Total";
            case StatItem.Perf_LineRenderers_Active: return "Line Renderers Active";
            case StatItem.Perf_LineRenderers_Total: return "Line Renderers Total";

            case StatItem.Perf_Cameras_Active: return "Cameras Active";
            case StatItem.Perf_Cameras_Total: return "Cameras Total";

            case StatItem.Thermal_Elapsed: return "Thermal Elapsed";
            case StatItem.Thermal_BatteryTemp: return "Battery Temperature";
            case StatItem.Thermal_CPUTemp: return "CPU Temperature";
            case StatItem.Thermal_GPUTemp: return "GPU Temperature";
            case StatItem.Thermal_Status: return "Thermal Status";
        }

        return statItem.ToString();
    }
}