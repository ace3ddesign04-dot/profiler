using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class RuntimeSceneScanReportGUI : MonoBehaviour {
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
        public string rawText;
        public float scanTimeSeconds;
        public int frame;
        public Vector3 cameraPosition;
        public Vector3 cameraEulerAngles;
        public string pathLabel;
    }

    [Header("GUI")]
    public bool visible;
    public KeyCode toggleKey = KeyCode.F10;
    public Rect windowRect = new Rect(30f, 30f, Screen.width, Screen.height);
    public float minWindowWidth = 500f;
    public float minWindowHeight = 350f;

    [Header("Camera Inspect")]
    public Camera targetCamera;
    public bool restoreTimeScaleOnClose = true;

    [Header("Report Loading")]
    public string reportFolderName = "SceneScanReports";
    public bool loadLatestOnStart;
    public bool refreshReportListWhenOpened = true;

    [Header("Style")]
    [Range(0f, 1f)] public float backgroundAlpha = 0.88f;
    public int fontSize = 13;
    public int titleFontSize = 15;

    [Header("Style - Layout")]
    [Min(18f)] public float rowHeight = 26f;
    [Min(18f)] public float reportFileRowHeight = 26f;
    [Min(18f)] public float buttonHeight = 26f;
    [Min(18f)] public float closeButtonHeight = 24f;

    [Header("Style - Button Widths")]
    [Min(20f)] public float refreshButtonWidth = 90f;
    [Min(20f)] public float loadLatestButtonWidth = 110f;
    [Min(20f)] public float hideButtonWidth = 70f;
    [Min(20f)] public float loadReportButtonWidth = 70f;
    [Min(20f)] public float inspectButtonWidth = 90f;
    [Min(20f)] public float inspectDetailsButtonWidth = 180f;
    [Min(20f)] public float copyPoseButtonWidth = 190f;
    [Min(20f)] public float closeButtonWidth = 32f;

    [Header("Style - Scroll Areas")]
    [Min(8f)] public float scrollBarWidth = 22f;
    public bool alwaysShowVerticalScrollBars = true;
    [Min(40f)] public float reportFilesMaxHeight = 120f;
    [Min(40f)] public float violationRowsMinHeight = 220f;
    [Min(0f)] public float violationRowsHeightOffset = 390f;


    private ScanReport _report;
    private string _loadedReportPath;
    private string[] _reportFiles;

    private Vector2 _mainScroll;
    private Vector2 _reportFilesScroll;
    private Vector2 _violationsScroll;
    private bool[] _expandedRows;

    private GUIStyle _windowStyle;
    private GUIStyle _blackBoxStyle;
    private GUIStyle _headerStyle;
    private GUIStyle _smallLabelStyle;
    private GUIStyle _rowButtonStyle;
    private GUIStyle _closeButtonStyle;
    private GUIStyle _toolbarButtonStyle; 
    private GUIStyle _horizontalScrollbarStyle;
    private GUIStyle _verticalScrollbarStyle;
    private GUIStyle _horizontalScrollbarThumbStyle;
    private GUIStyle _verticalScrollbarThumbStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _toggleStyle;

    private Texture2D _blackTexture;
    private float _previousTimeScale = 1f;
    private bool _timeScalePausedByThisOverlay;

    private void Awake() {
        if (targetCamera == null)
            targetCamera = Camera.main;
    }

    private void Start() {
        RefreshReportFiles();

        if (loadLatestOnStart)
            LoadLatestReport();
    }

    private void Update() {
        if (Input.GetKeyDown(toggleKey)) {
            if (visible)
                Hide();
            else
                Show();
        }
    }

    private void OnDestroy() {
        if (_blackTexture != null)
            Destroy(_blackTexture);
    }

    private void OnGUI() {
        if (!visible)
            return;

        InitStyles();

        windowRect.width = Mathf.Max(minWindowWidth, windowRect.width);
        windowRect.height = Mathf.Max(minWindowHeight, windowRect.height);
        windowRect = ClampWindowToScreen(windowRect);

        windowRect = GUI.Window(GetInstanceID(), windowRect, DrawWindow, string.Empty, _windowStyle);
    }

    public void Show() {
        visible = true;

        if (refreshReportListWhenOpened)
            RefreshReportFiles();

        ApplyPauseState();
    }

    public void Hide() {
        visible = false;
        RestorePauseStateIfNeeded();
    }

    public void ShowAndLoadReport(string reportPath) {
        Show();
        RefreshReportFiles();
        LoadReport(reportPath);
    }

    public void ShowAndLoadLatestReport() {
        Show();
        RefreshReportFiles();
        LoadLatestReport();
    }

    public void RefreshReportFiles() {
        string folder = GetReportsFolder();

        if (!Directory.Exists(folder)) {
            _reportFiles = new string[0];
            return;
        }

        string[] files;

        try {
            files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception e) {
            Debug.LogWarning("RuntimeSceneScanReportGUI: Failed to read report folder:\n" + e);
            files = new string[0];
        }

        SortFilesNewestFirst(files);
        _reportFiles = files;
    }

    public void LoadLatestReport() {
        RefreshReportFiles();

        if (_reportFiles == null || _reportFiles.Length == 0) {
            Debug.LogWarning("RuntimeSceneScanReportGUI: No report files found in: " + GetReportsFolder());
            return;
        }

        LoadReport(_reportFiles[0]);
    }

    public void LoadReport(string path) {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
            Debug.LogWarning("RuntimeSceneScanReportGUI: Report file does not exist: " + path);
            return;
        }

        string json;

        try {
            json = File.ReadAllText(path);
        }
        catch (Exception e) {
            Debug.LogError("RuntimeSceneScanReportGUI: Failed to read report:\n" + e);
            return;
        }

        ScanReport loaded;

        try {
            loaded = JsonUtility.FromJson<ScanReport>(json);
        }
        catch (Exception e) {
            Debug.LogError("RuntimeSceneScanReportGUI: Failed to parse report JSON:\n" + e);
            return;
        }

        if (loaded == null) {
            Debug.LogError("RuntimeSceneScanReportGUI: Report parsed as null.");
            return;
        }

        _report = loaded;
        _loadedReportPath = path;
        _expandedRows = null;
        _mainScroll = Vector2.zero;
        _violationsScroll = Vector2.zero;

        EnsureExpandedRows();

        Debug.Log("RuntimeSceneScanReportGUI loaded report: " + path);
    }

    private void InitStyles() {
        if (_blackTexture == null)
            _blackTexture = MakeTexture(1, 1, new Color(0f, 0f, 0f, backgroundAlpha));

        if (_windowStyle == null)
            _windowStyle = new GUIStyle(GUI.skin.window);

        _windowStyle.padding = new RectOffset(8, 8, 8, 8);

        if (_blackBoxStyle == null)
            _blackBoxStyle = new GUIStyle(GUI.skin.box);

        _blackBoxStyle.normal.background = _blackTexture;
        _blackBoxStyle.padding = new RectOffset(10, 10, 8, 10);

        if (_headerStyle == null)
            _headerStyle = new GUIStyle(GUI.skin.label);

        _headerStyle.fontStyle = FontStyle.Bold;
        _headerStyle.fontSize = titleFontSize;
        _headerStyle.normal.textColor = Color.white;

        if (_smallLabelStyle == null)
            _smallLabelStyle = new GUIStyle(GUI.skin.label);

        _smallLabelStyle.fontSize = fontSize;
        _smallLabelStyle.normal.textColor = new Color(0.86f, 0.86f, 0.86f, 1f);
        _smallLabelStyle.wordWrap = true;

        if (_rowButtonStyle == null)
            _rowButtonStyle = new GUIStyle(GUI.skin.button);

        _rowButtonStyle.alignment = TextAnchor.MiddleLeft;
        _rowButtonStyle.fontSize = fontSize;
        _rowButtonStyle.wordWrap = false;

        if (_closeButtonStyle == null)
            _closeButtonStyle = new GUIStyle(GUI.skin.button);

        _closeButtonStyle.fontStyle = FontStyle.Bold;
        _closeButtonStyle.fontSize = fontSize;
        _closeButtonStyle.alignment = TextAnchor.MiddleCenter;

        if (_toolbarButtonStyle == null)
            _toolbarButtonStyle = new GUIStyle(GUI.skin.button);

        _toolbarButtonStyle.fontSize = fontSize;
        _toolbarButtonStyle.alignment = TextAnchor.MiddleCenter;

        if (_buttonStyle == null)
            _buttonStyle = new GUIStyle(GUI.skin.button);

        _buttonStyle.fontSize = fontSize;
        _buttonStyle.alignment = TextAnchor.MiddleCenter;

        if (_horizontalScrollbarStyle == null)
            _horizontalScrollbarStyle = new GUIStyle(GUI.skin.horizontalScrollbar);

        if (_verticalScrollbarStyle == null)
            _verticalScrollbarStyle = new GUIStyle(GUI.skin.verticalScrollbar);

        if (_horizontalScrollbarThumbStyle == null)
            _horizontalScrollbarThumbStyle = new GUIStyle(GUI.skin.horizontalScrollbarThumb);

        if (_verticalScrollbarThumbStyle == null)
            _verticalScrollbarThumbStyle = new GUIStyle(GUI.skin.verticalScrollbarThumb);

        if (_toggleStyle == null)
            _toggleStyle = new GUIStyle(GUI.skin.toggle);

        _toggleStyle.fontSize = fontSize;
        _toggleStyle.normal.textColor = Color.white;
        _toggleStyle.onNormal.textColor = Color.white;
        _toggleStyle.hover.textColor = Color.white;
        _toggleStyle.onHover.textColor = Color.white;

        _horizontalScrollbarStyle.fixedHeight = scrollBarWidth;
        _verticalScrollbarStyle.fixedWidth = scrollBarWidth;

        _horizontalScrollbarThumbStyle.fixedHeight = scrollBarWidth;
        _verticalScrollbarThumbStyle.fixedWidth = scrollBarWidth;

        GUI.skin.horizontalScrollbar.fixedHeight = scrollBarWidth;
        GUI.skin.verticalScrollbar.fixedWidth = scrollBarWidth;

        GUI.skin.horizontalScrollbarThumb.fixedHeight = scrollBarWidth;
        GUI.skin.verticalScrollbarThumb.fixedWidth = scrollBarWidth;

        GUI.skin.horizontalScrollbarLeftButton.fixedHeight = scrollBarWidth;
        GUI.skin.horizontalScrollbarRightButton.fixedHeight = scrollBarWidth;

        GUI.skin.verticalScrollbarUpButton.fixedWidth = scrollBarWidth;
        GUI.skin.verticalScrollbarDownButton.fixedWidth = scrollBarWidth;

        GUI.skin.button.fontSize = fontSize;
    }

    private Texture2D MakeTexture(int width, int height, Color color) {
        Color[] pixels = new Color[width * height];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = color;

        Texture2D texture = new Texture2D(width, height);
        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
    }

    private void DrawWindow(int id) {
        GUILayout.BeginVertical(_blackBoxStyle);

        DrawTitleBar();
        DrawToolbar();

        _mainScroll = GUILayout.BeginScrollView(_mainScroll, false, alwaysShowVerticalScrollBars, _horizontalScrollbarStyle, _verticalScrollbarStyle, GUILayout.ExpandHeight(true));

        DrawReportFilesBlock();

        if (_report == null)
            DrawEmptyState();
        else {
            DrawReportHeader();
            DrawViolationRows();
        }

        GUILayout.EndScrollView();

        GUILayout.EndVertical();

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 45f, 28f));
    }

    private void DrawTitleBar() {
        GUILayout.BeginHorizontal();

        GUILayout.Label("Scene Scan Report", _headerStyle);

        GUILayout.FlexibleSpace();

        if (GUILayout.Button("X", _closeButtonStyle, GUILayout.Width(closeButtonWidth), GUILayout.Height(closeButtonHeight)))
            Hide();

        GUILayout.EndHorizontal();
    }

    private void DrawToolbar() {
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Refresh", _toolbarButtonStyle, GUILayout.Width(refreshButtonWidth), GUILayout.Height(buttonHeight)))
            RefreshReportFiles();

        if (GUILayout.Button("Load Latest", _toolbarButtonStyle, GUILayout.Width(loadLatestButtonWidth), GUILayout.Height(buttonHeight)))
            LoadLatestReport();

        if (GUILayout.Button("Hide", _toolbarButtonStyle, GUILayout.Width(hideButtonWidth), GUILayout.Height(buttonHeight)))
            Hide();

        GUILayout.Space(12f);

        GUILayout.FlexibleSpace();

        GUILayout.Label("Toggle: " + toggleKey, _smallLabelStyle, GUILayout.Width(200f));

        GUILayout.EndHorizontal();

        GUILayout.Space(10f);
    }

    private void DrawReportFilesBlock() {
        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.Label("Available Reports", _headerStyle);

        if (_reportFiles == null || _reportFiles.Length == 0) {
            GUILayout.Label("No JSON reports found in:", _smallLabelStyle);
            GUILayout.TextField(GetReportsFolder());
            GUILayout.EndVertical();
            return;
        }

        float height = Mathf.Min(reportFilesMaxHeight, 30f + (_reportFiles.Length * reportFileRowHeight));

        _reportFilesScroll = GUILayout.BeginScrollView(_reportFilesScroll, false, alwaysShowVerticalScrollBars, _horizontalScrollbarStyle, _verticalScrollbarStyle, GUILayout.Height(height));

        for (int i = 0; i < _reportFiles.Length; i++) {
            string path = _reportFiles[i];
            string fileName = Path.GetFileName(path);
            bool isLoaded = path == _loadedReportPath;

            GUILayout.BeginHorizontal();

            GUILayout.Label(isLoaded ? "●" : "○", _smallLabelStyle, GUILayout.Width(22f));
            GUILayout.Label(fileName, _smallLabelStyle, GUILayout.ExpandWidth(true));

            if (GUILayout.Button("Load", _buttonStyle, GUILayout.Width(loadReportButtonWidth), GUILayout.Height(reportFileRowHeight)))
                LoadReport(path);

            GUILayout.EndHorizontal();
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawEmptyState() {
        GUILayout.Space(8f);

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("No report loaded.", _headerStyle);
        GUILayout.Label("Click Load Latest or choose a JSON report from Available Reports.", _smallLabelStyle);
        GUILayout.EndVertical();
    }

    private void DrawReportHeader() {
        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.Label("Report Summary", _headerStyle);

        GUILayout.Label("Loaded File: " + Safe(Path.GetFileName(_loadedReportPath)), _smallLabelStyle);
        GUILayout.Label("Scene: " + Safe(_report.sceneName) + " | Mode: " + Safe(_report.mode), _smallLabelStyle);
        GUILayout.Label("Started: " + Safe(_report.startedAtLocal) + " | Ended: " + Safe(_report.endedAtLocal), _smallLabelStyle);

        GUILayout.Label(
            "Duration: " + _report.durationSeconds.ToString("F2") +
            " sec | Samples: " + _report.totalSamples +
            " | Violations: " + _report.totalViolations,
            _smallLabelStyle
        );

        GUILayout.Label(
            "Thresholds Enabled: " + _report.enabledThresholdCount +
            " / Configured: " + _report.configuredThresholdCount,
            _smallLabelStyle
        );

        GUILayout.Label("Unity: " + Safe(_report.unityVersion) + " | Platform: " + Safe(_report.platform), _smallLabelStyle);
        GUILayout.Label("Bounds Center: " + FormatVector3(_report.boundsCenter), _smallLabelStyle);
        GUILayout.Label("Bounds Size: " + FormatVector3(_report.boundsSize), _smallLabelStyle);

        GUILayout.EndVertical();
    }

    private void DrawViolationRows() {
        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.Label("Violations", _headerStyle);

        if (_report.violations == null || _report.violations.Count == 0) {
            GUILayout.Label("This report has no threshold violations.", _smallLabelStyle);
            GUILayout.EndVertical();
            return;
        }

        EnsureExpandedRows();

        float rowsHeight = Mathf.Max(violationRowsMinHeight, windowRect.height - violationRowsHeightOffset);

        _violationsScroll = GUILayout.BeginScrollView(_violationsScroll, false, alwaysShowVerticalScrollBars, _horizontalScrollbarStyle, _verticalScrollbarStyle, GUILayout.Height(rowsHeight));

        for (int i = 0; i < _report.violations.Count; i++)
            DrawViolationRow(i, _report.violations[i]);

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawViolationRow(int index, ViolationRecord violation) {
        if (violation == null)
            return;

        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.BeginHorizontal();

        string arrow = _expandedRows[index] ? "▼" : "▶";

        string rowText =
            arrow +
            " #" + index.ToString("000") +
            " | Sample " + violation.sampleIndex +
            " | " + Safe(violation.displayName) +
            " | Value " + violation.value.ToString("F3") +
            " " + Safe(violation.comparison) +
            " " + violation.threshold.ToString("F3") +
            " | " + Safe(violation.pathLabel);

        if (GUILayout.Button(rowText, _rowButtonStyle, GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight)))
            _expandedRows[index] = !_expandedRows[index];

        if (GUILayout.Button("Inspect", _buttonStyle, GUILayout.Width(inspectButtonWidth), GUILayout.Height(rowHeight)))
            InspectViolation(violation);

        GUILayout.EndHorizontal();

        if (_expandedRows[index])
            DrawViolationDetails(violation);

        GUILayout.EndVertical();
    }

    private void DrawViolationDetails(ViolationRecord violation) {
        GUILayout.Space(4f);

        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.Label("Stat Id: " + Safe(violation.statId), _smallLabelStyle);
        GUILayout.Label("Display Name: " + Safe(violation.displayName), _smallLabelStyle);
        GUILayout.Label("Comparison: " + Safe(violation.comparison), _smallLabelStyle);
        GUILayout.Label("Threshold: " + violation.threshold.ToString("F3"), _smallLabelStyle);
        GUILayout.Label("Value: " + violation.value.ToString("F3"), _smallLabelStyle);
        GUILayout.Label("Raw Text: " + Safe(violation.rawText), _smallLabelStyle);

        GUILayout.Space(4f);

        GUILayout.Label("Sample Index: " + violation.sampleIndex, _smallLabelStyle);
        GUILayout.Label("Frame: " + violation.frame, _smallLabelStyle);
        GUILayout.Label("Scan Time: " + violation.scanTimeSeconds.ToString("F3") + " sec", _smallLabelStyle);
        GUILayout.Label("Path Label: " + Safe(violation.pathLabel), _smallLabelStyle);

        GUILayout.Space(4f);

        GUILayout.Label("Camera Position: " + FormatVector3(violation.cameraPosition), _smallLabelStyle);
        GUILayout.Label("Camera Euler: " + FormatVector3(violation.cameraEulerAngles), _smallLabelStyle);

        GUILayout.Space(4f);

        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Inspect This Violation", _buttonStyle, GUILayout.Width(inspectDetailsButtonWidth), GUILayout.Height(buttonHeight)))
            InspectViolation(violation);

        if (GUILayout.Button("Copy Position/Rotation", _buttonStyle, GUILayout.Width(copyPoseButtonWidth), GUILayout.Height(buttonHeight)))
            CopyPoseToClipboard(violation);

        GUILayout.FlexibleSpace();

        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
    }

    private void InspectViolation(ViolationRecord violation) {
        if (violation == null)
            return;

        if (targetCamera == null)
            targetCamera = Camera.main;

        if (targetCamera == null) {
            Debug.LogWarning("RuntimeSceneScanReportGUI: Target Camera is missing.");
            return;
        }

        Vector3 position = violation.cameraPosition;
        Quaternion rotation = Quaternion.Euler(violation.cameraEulerAngles);

        targetCamera.transform.SetPositionAndRotation(position, rotation);

        Debug.Log(
            "RuntimeSceneScanReportGUI inspecting violation: " +
            Safe(violation.displayName) +
            " at " +
            FormatVector3(position)
        );
    }

    private void CopyPoseToClipboard(ViolationRecord violation) {
        if (violation == null)
            return;

        GUIUtility.systemCopyBuffer =
            "Position: " + FormatVector3(violation.cameraPosition) + "\n" +
            "Euler: " + FormatVector3(violation.cameraEulerAngles);

        Debug.Log("RuntimeSceneScanReportGUI copied pose:\n" + GUIUtility.systemCopyBuffer);
    }

    private void EnsureExpandedRows() {
        int count = 0;

        if (_report != null && _report.violations != null)
            count = _report.violations.Count;

        if (_expandedRows != null && _expandedRows.Length == count)
            return;

        _expandedRows = new bool[count];
    }

    private void ApplyPauseState() {
        if (_timeScalePausedByThisOverlay)
            return;

        _previousTimeScale = Time.timeScale;
        Time.timeScale = 0f;
        _timeScalePausedByThisOverlay = true;
    }

    private void RestorePauseStateIfNeeded() {
        if (!restoreTimeScaleOnClose)
            return;

        if (!_timeScalePausedByThisOverlay)
            return;

        Time.timeScale = _previousTimeScale;
        _timeScalePausedByThisOverlay = false;
    }

    private string GetReportsFolder() {
        return Path.Combine(Application.persistentDataPath, reportFolderName);
    }

    private void SortFilesNewestFirst(string[] files) {
        if (files == null)
            return;

        for (int i = 0; i < files.Length - 1; i++) {
            for (int j = i + 1; j < files.Length; j++) {
                DateTime a = File.GetLastWriteTime(files[i]);
                DateTime b = File.GetLastWriteTime(files[j]);

                if (b > a) {
                    string temp = files[i];
                    files[i] = files[j];
                    files[j] = temp;
                }
            }
        }
    }

    private Rect ClampWindowToScreen(Rect rect) {
        if (rect.width > Screen.width)
            rect.width = Screen.width;

        if (rect.height > Screen.height)
            rect.height = Screen.height;

        if (rect.x < 0f)
            rect.x = 0f;

        if (rect.y < 0f)
            rect.y = 0f;

        if (rect.xMax > Screen.width)
            rect.x = Screen.width - rect.width;

        if (rect.yMax > Screen.height)
            rect.y = Screen.height - rect.height;

        return rect;
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