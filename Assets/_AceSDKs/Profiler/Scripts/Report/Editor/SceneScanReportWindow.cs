#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class SceneScanReportWindow : EditorWindow {
    private const string WindowTitle = "Scene Scan Report";
    private const string MenuPath = "Tools/Scene Scanner/Report Viewer";

    // Runtime scanner writes this key after finishing a scan in Editor Play Mode.
    public const string LatestReportPathPrefsKey = "ReleaseBuildSceneScanner.LatestReportPath";

    [Serializable]
    private class ScanReport {
        public string sceneName;
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
    private class ViolationRecord {
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

    private ScanReport _report;
    private string _loadedReportPath;
    private Vector2 _scroll;
    private bool[] _expandedRows;

    private bool _snapSceneView = true;
    private bool _snapMainCamera = true;
    private bool _selectCameraAfterSnap = true;

    private bool _hasInspectedViolation;
    private ViolationRecord _inspectedViolation;

    private GUIStyle _rowButtonStyle;
    private GUIStyle _headerStyle;
    private GUIStyle _smallGrayStyle;

    [MenuItem(MenuPath)]
    public static void OpenWindow() {
        SceneScanReportWindow window = GetWindow<SceneScanReportWindow>(WindowTitle);
        window.minSize = new Vector2(900f, 500f);
        window.Show();

        string pendingReportPath = EditorPrefs.GetString(LatestReportPathPrefsKey, string.Empty);

        if (!string.IsNullOrEmpty(pendingReportPath) && File.Exists(pendingReportPath)) {
            window.LoadReport(pendingReportPath);
            EditorPrefs.DeleteKey(LatestReportPathPrefsKey);
        }
    }

    private void OnEnable() {
        SceneView.duringSceneGui += OnSceneViewGUI;

        string pendingReportPath = EditorPrefs.GetString(LatestReportPathPrefsKey, string.Empty);

        if (!string.IsNullOrEmpty(pendingReportPath) && File.Exists(pendingReportPath)) {
            LoadReport(pendingReportPath);
            EditorPrefs.DeleteKey(LatestReportPathPrefsKey);
        }
    }

    private void OnDisable() {
        SceneView.duringSceneGui -= OnSceneViewGUI;
    }

    private void OnGUI() {
        InitStyles();

        DrawToolbar();
        DrawSnapOptions();

        if (_report == null) {
            DrawEmptyState();
            return;
        }

        DrawReportHeader();
        DrawViolationRows();
    }

    private void InitStyles() {
        if (_rowButtonStyle == null) {
            _rowButtonStyle = new GUIStyle(EditorStyles.label);
            _rowButtonStyle.wordWrap = false;
            _rowButtonStyle.alignment = TextAnchor.MiddleLeft;
            _rowButtonStyle.padding = new RectOffset(2, 2, 2, 2);
        }

        if (_headerStyle == null) {
            _headerStyle = new GUIStyle(EditorStyles.boldLabel);
            _headerStyle.fontSize = 13;
        }

        if (_smallGrayStyle == null) {
            _smallGrayStyle = new GUIStyle(EditorStyles.miniLabel);
            _smallGrayStyle.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
            _smallGrayStyle.wordWrap = true;
        }
    }

    private void DrawToolbar() {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("Load Report", EditorStyles.toolbarButton, GUILayout.Width(90f)))
            LoadReportFromFilePanel();

        if (GUILayout.Button("Load Latest", EditorStyles.toolbarButton, GUILayout.Width(90f)))
            LoadLatestReportFromDefaultFolder();

        if (GUILayout.Button("Reveal File", EditorStyles.toolbarButton, GUILayout.Width(80f)))
            RevealLoadedReport();

        GUILayout.FlexibleSpace();

        if (!string.IsNullOrEmpty(_loadedReportPath))
            GUILayout.Label(Path.GetFileName(_loadedReportPath), EditorStyles.miniLabel);

        EditorGUILayout.EndHorizontal();
    }

    private void DrawSnapOptions() {
        EditorGUILayout.BeginVertical("box");

        EditorGUILayout.LabelField("Inspect Behaviour", _headerStyle);

        EditorGUILayout.BeginHorizontal();

        _snapSceneView = EditorGUILayout.ToggleLeft(
            "Snap Scene View",
            _snapSceneView,
            GUILayout.Width(150f)
        );

        _snapMainCamera = EditorGUILayout.ToggleLeft(
            "Snap Main Camera",
            _snapMainCamera,
            GUILayout.Width(160f)
        );

        _selectCameraAfterSnap = EditorGUILayout.ToggleLeft(
            "Select Camera",
            _selectCameraAfterSnap,
            GUILayout.Width(130f)
        );

        GUILayout.FlexibleSpace();

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox(
            "Inspect snaps to the violation camera position and rotation. Scene View is for editor inspection. Main Camera snap is exact and modifies the scene camera transform.",
            MessageType.Info
        );

        EditorGUILayout.EndVertical();
    }

    private void DrawEmptyState() {
        GUILayout.Space(20f);

        EditorGUILayout.HelpBox(
            "No scene scan report loaded. Click Load Report, or click Load Latest if the report was generated from this Unity project.",
            MessageType.Info
        );

        string defaultFolder = GetDefaultReportsFolder();

        EditorGUILayout.LabelField("Default Report Folder", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel(defaultFolder, GUILayout.Height(20f));
    }

    private void DrawReportHeader() {
        EditorGUILayout.BeginVertical("box");

        EditorGUILayout.LabelField("Report Summary", _headerStyle);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Scene", Safe(_report.sceneName));
        EditorGUILayout.LabelField("Mode", Safe(_report.mode));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Started", Safe(_report.startedAtLocal));
        EditorGUILayout.LabelField("Ended", Safe(_report.endedAtLocal));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Duration", _report.durationSeconds.ToString("F2") + " sec");
        EditorGUILayout.LabelField("Samples", _report.totalSamples.ToString());
        EditorGUILayout.LabelField("Violations", _report.totalViolations.ToString());
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Enabled Thresholds", _report.enabledThresholdCount.ToString());
        EditorGUILayout.LabelField("Configured Thresholds", _report.configuredThresholdCount.ToString());
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Unity", Safe(_report.unityVersion));
        EditorGUILayout.LabelField("Platform", Safe(_report.platform));
        EditorGUILayout.EndHorizontal();

        string activeSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

        if (!string.IsNullOrEmpty(_report.sceneName) && _report.sceneName != activeSceneName) {
            EditorGUILayout.HelpBox(
                "Loaded report scene is '" + _report.sceneName + "', but active Unity scene is '" + activeSceneName + "'. Inspect positions may not match unless the correct scene is open.",
                MessageType.Warning
            );
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawViolationRows() {
        if (_report.violations == null || _report.violations.Count == 0) {
            EditorGUILayout.HelpBox("This report has no threshold violations.", MessageType.Info);
            return;
        }

        EnsureExpandedArray();

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Violations", _headerStyle);
        EditorGUILayout.EndVertical();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        for (int i = 0; i < _report.violations.Count; i++) {
            ViolationRecord violation = _report.violations[i];
            DrawViolationRow(i, violation);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawViolationRow(int index, ViolationRecord violation) {
        if (violation == null)
            return;

        EditorGUILayout.BeginVertical("box");

        EditorGUILayout.BeginHorizontal();

        string arrow = _expandedRows[index] ? "▼" : "▶";

        if (GUILayout.Button(arrow, GUILayout.Width(24f)))
            _expandedRows[index] = !_expandedRows[index];

        string rowText =
            "#" + index.ToString("000") +
            " | Sample " + violation.sampleIndex +
            " | " + Safe(violation.displayName) +
            " | " + violation.value.ToString("F3") +
            " " + Safe(violation.comparison) +
            " " + violation.threshold.ToString("F3") +
            " | " + Safe(violation.pathLabel);

        if (GUILayout.Button(rowText, _rowButtonStyle, GUILayout.ExpandWidth(true), GUILayout.Height(20f)))
            _expandedRows[index] = !_expandedRows[index];

        if (GUILayout.Button("Inspect", GUILayout.Width(80f)))
            InspectViolation(violation);

        EditorGUILayout.EndHorizontal();

        if (_expandedRows[index])
            DrawViolationDetails(violation);

        EditorGUILayout.EndVertical();
    }

    private void DrawViolationDetails(ViolationRecord violation) {
        EditorGUI.indentLevel++;

        EditorGUILayout.Space(4f);

        EditorGUILayout.LabelField("Stat", Safe(violation.statId));
        EditorGUILayout.LabelField("Display Name", Safe(violation.displayName));
        EditorGUILayout.LabelField("Comparison", Safe(violation.comparison));
        EditorGUILayout.FloatField("Threshold", violation.threshold);
        EditorGUILayout.FloatField("Value", violation.value);
        EditorGUILayout.LabelField("Scene Name", Safe(violation.sceneName));

        EditorGUILayout.Space(4f);

        EditorGUILayout.IntField("Sample Index", violation.sampleIndex);
        EditorGUILayout.IntField("Frame", violation.frame);
        EditorGUILayout.FloatField("Scan Time Seconds", violation.scanTimeSeconds);
        EditorGUILayout.LabelField("Path Label", Safe(violation.pathLabel));

        EditorGUILayout.Space(4f);

        EditorGUILayout.Vector3Field("Camera Position", violation.cameraPosition);
        EditorGUILayout.Vector3Field("Camera Euler Angles", violation.cameraEulerAngles);

        EditorGUILayout.Space(4f);

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Inspect This Violation", GUILayout.Width(180f)))
            InspectViolation(violation);

        if (GUILayout.Button("Copy Position/Rotation", GUILayout.Width(180f)))
            CopyPoseToClipboard(violation);

        GUILayout.FlexibleSpace();

        EditorGUILayout.EndHorizontal();

        EditorGUI.indentLevel--;
    }

    private void InspectViolation(ViolationRecord violation) {
        if (violation == null)
            return;

        _hasInspectedViolation = true;
        _inspectedViolation = violation;

        Vector3 position = violation.cameraPosition;
        Quaternion rotation = Quaternion.Euler(violation.cameraEulerAngles);

        if (_snapMainCamera)
            SnapMainCamera(position, rotation);

        if (_snapSceneView)
            SnapSceneView(position, rotation);

        SceneView.RepaintAll();
        Repaint();
    }

    private void SnapMainCamera(Vector3 position, Quaternion rotation) {
        Camera camera = Camera.main;

        if (camera == null)
            camera = FindObjectOfType<Camera>();

        if (camera == null) {
            Debug.LogWarning("SceneScanReportWindow: No camera found in the active scene.");
            return;
        }

        if (!Application.isPlaying)
            Undo.RecordObject(camera.transform, "Snap Camera To Scene Scan Violation");

        camera.transform.SetPositionAndRotation(position, rotation);

        if (!Application.isPlaying)
            EditorUtility.SetDirty(camera.transform);

        if (_selectCameraAfterSnap)
            Selection.activeGameObject = camera.gameObject;
    }

    private void SnapSceneView(Vector3 position, Quaternion rotation) {
        SceneView sceneView = SceneView.lastActiveSceneView;

        if (sceneView == null)
            sceneView = GetWindow<SceneView>();

        if (sceneView == null)
            return;

        GameObject poseObject = GetOrCreateHiddenPoseObject();
        poseObject.transform.SetPositionAndRotation(position, rotation);

        sceneView.AlignViewToObject(poseObject.transform);
        sceneView.Repaint();
    }

    private GameObject GetOrCreateHiddenPoseObject() {
        const string objectName = "__SceneScanReport_InspectPose__";

        GameObject existing = GameObject.Find(objectName);

        if (existing != null)
            return existing;

        GameObject obj = new GameObject(objectName);
        obj.hideFlags = HideFlags.HideAndDontSave;
        return obj;
    }

    private void CopyPoseToClipboard(ViolationRecord violation) {
        if (violation == null)
            return;

        string text =
            "Position: " + FormatVector3(violation.cameraPosition) + "\n" +
            "Euler: " + FormatVector3(violation.cameraEulerAngles);

        EditorGUIUtility.systemCopyBuffer = text;
        Debug.Log("Scene scan pose copied:\n" + text);
    }

    private void LoadReportFromFilePanel() {
        string initialFolder = GetDefaultReportsFolder();

        if (!Directory.Exists(initialFolder))
            initialFolder = Application.dataPath;

        string path = EditorUtility.OpenFilePanel(
            "Load Scene Scan Report",
            initialFolder,
            "json"
        );

        if (!string.IsNullOrEmpty(path))
            LoadReport(path);
    }

    private void LoadLatestReportFromDefaultFolder() {
        string folder = GetDefaultReportsFolder();

        if (!Directory.Exists(folder)) {
            EditorUtility.DisplayDialog(
                "Scene Scan Report",
                "Default report folder does not exist:\n" + folder,
                "OK"
            );
            return;
        }

        string latestPath = FindLatestJsonFile(folder);

        if (string.IsNullOrEmpty(latestPath)) {
            EditorUtility.DisplayDialog(
                "Scene Scan Report",
                "No JSON report files found in:\n" + folder,
                "OK"
            );
            return;
        }

        LoadReport(latestPath);
    }

    private string FindLatestJsonFile(string folder) {
        string[] files;

        try {
            files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch {
            return string.Empty;
        }

        if (files == null || files.Length == 0)
            return string.Empty;

        string latest = files[0];
        DateTime latestTime = File.GetLastWriteTime(latest);

        for (int i = 1; i < files.Length; i++) {
            DateTime time = File.GetLastWriteTime(files[i]);

            if (time > latestTime) {
                latest = files[i];
                latestTime = time;
            }
        }

        return latest;
    }

    private void LoadReport(string path) {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
            Debug.LogWarning("SceneScanReportWindow: Report file does not exist: " + path);
            return;
        }

        string json;

        try {
            json = File.ReadAllText(path);
        }
        catch (Exception e) {
            Debug.LogError("SceneScanReportWindow: Failed to read report:\n" + e);
            return;
        }

        ScanReport loaded;

        try {
            loaded = JsonUtility.FromJson<ScanReport>(json);
        }
        catch (Exception e) {
            Debug.LogError("SceneScanReportWindow: Failed to parse report JSON:\n" + e);
            return;
        }

        if (loaded == null) {
            Debug.LogError("SceneScanReportWindow: Report JSON parsed as null.");
            return;
        }

        _report = loaded;
        _loadedReportPath = path;
        _expandedRows = null;
        _hasInspectedViolation = false;
        _inspectedViolation = null;
        _scroll = Vector2.zero;

        EnsureExpandedArray();

        Debug.Log("SceneScanReportWindow loaded report: " + path);
        Repaint();
    }

    private void RevealLoadedReport() {
        if (string.IsNullOrEmpty(_loadedReportPath) || !File.Exists(_loadedReportPath)) {
            EditorUtility.DisplayDialog(
                "Scene Scan Report",
                "No valid report file is currently loaded.",
                "OK"
            );
            return;
        }

        EditorUtility.RevealInFinder(_loadedReportPath);
    }

    private void EnsureExpandedArray() {
        int count = 0;

        if (_report != null && _report.violations != null)
            count = _report.violations.Count;

        if (_expandedRows != null && _expandedRows.Length == count)
            return;

        _expandedRows = new bool[count];
    }

    private string GetDefaultReportsFolder() {
        return Path.Combine(Application.persistentDataPath, "SceneScanReports");
    }

    private void OnSceneViewGUI(SceneView sceneView) {
        if (!_hasInspectedViolation || _inspectedViolation == null)
            return;

        Vector3 position = _inspectedViolation.cameraPosition;
        Quaternion rotation = Quaternion.Euler(_inspectedViolation.cameraEulerAngles);

        Handles.color = Color.red;
        Handles.SphereHandleCap(
            0,
            position,
            Quaternion.identity,
            1.2f,
            EventType.Repaint
        );

        Handles.ArrowHandleCap(
            0,
            position,
            rotation,
            5f,
            EventType.Repaint
        );

        Handles.Label(
            position + Vector3.up * 2.0f,
            "Violation: " + Safe(_inspectedViolation.displayName) +
            "\nValue: " + _inspectedViolation.value.ToString("F3") +
            "\nPath: " + Safe(_inspectedViolation.pathLabel)
        );
    }

    private string Safe(string value) {
        return string.IsNullOrEmpty(value) ? "-" : value;
    }

    private string FormatVector3(Vector3 value) {
        return value.x.ToString("F4") + ", " +
               value.y.ToString("F4") + ", " +
               value.z.ToString("F4");
    }
}

#endif