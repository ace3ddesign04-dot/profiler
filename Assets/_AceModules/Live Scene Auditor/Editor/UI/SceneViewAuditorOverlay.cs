using UnityEditor;
using UnityEngine;

namespace AceModules.LiveSceneAuditor
{
    [InitializeOnLoad]
    public static class SceneViewAuditorOverlay
    {
        public enum DockAnchor
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight,
            Custom
        }

        private static bool _isEnabled = true;
        private static bool _isExpanded = false;
        private static Rect _overlayRect = new Rect(20, 30, 240, 36);
        private static DockAnchor _currentAnchor = DockAnchor.TopRight;
        private const int WindowId = 849201;

        private const string PrefsKeyEnabled = "AceModules_Auditor_Overlay_Enabled";
        private const string PrefsKeyExpanded = "AceModules_Auditor_Overlay_Expanded";
        private const string PrefsKeyAnchor = "AceModules_Auditor_Overlay_Anchor";
        private const string PrefsKeyPosX = "AceModules_Auditor_Overlay_PosX";
        private const string PrefsKeyPosY = "AceModules_Auditor_Overlay_PosY";

        private const float SnapThreshold = 50f; // Distance in pixels to snap to a corner
        private const float Margin = 15f;

        private static double _lastEditorTime = 0;
        private static bool _initialScanDone = false;
        private static Vector3 _lastSceneCamPos;
        private static Quaternion _lastSceneCamRot;

        static SceneViewAuditorOverlay()
        {
            _isEnabled = EditorPrefs.GetBool(PrefsKeyEnabled, true);
            _isExpanded = EditorPrefs.GetBool(PrefsKeyExpanded, false);
            _currentAnchor = (DockAnchor)EditorPrefs.GetInt(PrefsKeyAnchor, (int)DockAnchor.TopRight);

            float savedX = EditorPrefs.GetFloat(PrefsKeyPosX, 20f);
            float savedY = EditorPrefs.GetFloat(PrefsKeyPosY, 30f);
            float width = _isExpanded ? 260f : 230f;
            float height = _isExpanded ? 274f : 36f;
            _overlayRect = new Rect(savedX, savedY, width, height);

            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;

            Undo.postprocessModifications -= OnPostprocessModifications;
            Undo.postprocessModifications += OnPostprocessModifications;
        }

        [MenuItem("Tools/Ace Modules/Toggle Scene View Overlay", false, 100)]
        public static void ToggleOverlay()
        {
            _isEnabled = !_isEnabled;
            EditorPrefs.SetBool(PrefsKeyEnabled, _isEnabled);
            SceneView.RepaintAll();
        }

        private static bool _isDragging = false;
        private static Vector2 _dragOffset;

        private static void OnSceneGUI(SceneView sceneView)
        {
            LiveSceneAuditorWindow.SyncCameraNow(sceneView);

            // Track camera changes in SceneView for OnChange mode
            if (sceneView != null && sceneView.camera != null)
            {
                Transform svCamT = sceneView.camera.transform;
                if ((svCamT.position - _lastSceneCamPos).sqrMagnitude > 0.0005f || Quaternion.Angle(svCamT.rotation, _lastSceneCamRot) > 0.15f)
                {
                    _lastSceneCamPos = svCamT.position;
                    _lastSceneCamRot = svCamT.rotation;

                    if (LiveSceneAuditorWindow.CurrentScanMode == LiveSceneAuditorWindow.ScanMode.OnChange &&
                        LiveSceneAuditorWindow.AutoScope == LiveSceneAuditorWindow.ScanScope.ViewFrustum)
                    {
                        LiveSceneAuditorWindow.BackgroundChangeDebounceTimer = LiveSceneAuditorWindow.ChangeDebounceDuration;
                    }
                }
            }

            if (!_isEnabled) return;

            Handles.BeginGUI();

            float targetWidth = _isExpanded ? 260f : 230f;
            float targetHeight = _isExpanded ? 274f : 34f;
            _overlayRect.width = targetWidth;
            _overlayRect.height = targetHeight;

            float viewWidth = sceneView.position.width;
            float viewHeight = sceneView.position.height;

            Event evt = Event.current;
            Rect dragHandleRect = new Rect(_overlayRect.x, _overlayRect.y, _overlayRect.width - 60f, 28f);

            // Handle Mouse Dragging directly
            switch (evt.type)
            {
                case EventType.MouseDown:
                    if (evt.button == 0 && dragHandleRect.Contains(evt.mousePosition))
                    {
                        _isDragging = true;
                        _dragOffset = evt.mousePosition - new Vector2(_overlayRect.x, _overlayRect.y);
                        _currentAnchor = DockAnchor.Custom;
                        evt.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (_isDragging)
                    {
                        Vector2 newPos = evt.mousePosition - _dragOffset;
                        _overlayRect.x = newPos.x;
                        _overlayRect.y = newPos.y;
                        ClampOverlayRect(viewWidth, viewHeight);
                        sceneView.Repaint();
                        evt.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (_isDragging)
                    {
                        _isDragging = false;
                        ApplySnapping(viewWidth, viewHeight);
                        ClampOverlayRect(viewWidth, viewHeight);
                        sceneView.Repaint();
                        evt.Use();
                    }
                    break;
            }

            // Apply docked position if anchored and not dragging
            if (!_isDragging)
            {
                ApplyAnchorPosition(viewWidth, viewHeight);
            }

            // Draw Background Panel
            GUI.Box(_overlayRect, GUIContent.none, EditorStyles.helpBox);

            // Draw Controls inside overlay
            GUILayout.BeginArea(_overlayRect);
            DrawOverlayContent(sceneView);
            GUILayout.EndArea();

            Handles.EndGUI();
        }

        private static void ApplyAnchorPosition(float viewWidth, float viewHeight)
        {
            switch (_currentAnchor)
            {
                case DockAnchor.TopLeft:
                    _overlayRect.x = Margin;
                    _overlayRect.y = 25f;
                    break;
                case DockAnchor.TopRight:
                    _overlayRect.x = viewWidth - _overlayRect.width - Margin;
                    _overlayRect.y = 25f;
                    break;
                case DockAnchor.BottomLeft:
                    _overlayRect.x = Margin;
                    _overlayRect.y = viewHeight - _overlayRect.height - Margin;
                    break;
                case DockAnchor.BottomRight:
                    _overlayRect.x = viewWidth - _overlayRect.width - Margin;
                    _overlayRect.y = viewHeight - _overlayRect.height - Margin;
                    break;
                case DockAnchor.Custom:
                    ClampOverlayRect(viewWidth, viewHeight);
                    break;
            }
        }

        private static void ClampOverlayRect(float viewWidth, float viewHeight)
        {
            float maxX = Mathf.Max(Margin, viewWidth - _overlayRect.width - Margin);
            float maxY = Mathf.Max(25f, viewHeight - _overlayRect.height - Margin);
            _overlayRect.x = Mathf.Clamp(_overlayRect.x, Margin, maxX);
            _overlayRect.y = Mathf.Clamp(_overlayRect.y, 25f, maxY);
        }

        private static void DrawOverlayContent(SceneView sceneView)
        {
            EditorGUILayout.BeginVertical();

            // Header line
            EditorGUILayout.BeginHorizontal();

            var report = LiveSceneAuditorWindow.CurrentReport;
            bool isScanning = LiveSceneAuditorWindow.IsScanning;

            Color statusColor = AuditorUIStyles.ColorGood;
            string statusTitle = "● Ready";

            if (isScanning)
            {
                statusColor = AuditorUIStyles.ColorWarning;
                statusTitle = "● Auditing...";
            }
            else if (report != null)
            {
                if (report.isOverBudget)
                {
                    statusColor = AuditorUIStyles.ColorDanger;
                    statusTitle = $"● Over ({report.overallHealthPercentage}%)";
                }
                else if (report.overallHealthPercentage >= 100f)
                {
                    statusColor = AuditorUIStyles.ColorWarning;
                    statusTitle = $"● Full ({report.overallHealthPercentage}%)";
                }
                else if (report.overallHealthPercentage >= 75f)
                {
                    statusColor = AuditorUIStyles.ColorWarning;
                    statusTitle = $"● Warn ({report.overallHealthPercentage}%)";
                }
                else
                {
                    statusColor = AuditorUIStyles.ColorGood;
                    statusTitle = $"● OK ({report.overallHealthPercentage}%)";
                }
            }

            GUIStyle statusStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                normal = { textColor = statusColor }
            };
            GUILayout.Label("⋮⋮ " + statusTitle, statusStyle, GUILayout.Width(130));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(_isExpanded ? "▲" : "▼", EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18)))
            {
                _isExpanded = !_isExpanded;
                EditorPrefs.SetBool(PrefsKeyExpanded, _isExpanded);
                sceneView.Repaint();
            }

            if (GUILayout.Button("⚡", EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18)))
            {
                LiveSceneAuditorWindow.RequestScan(false);
            }

            EditorGUILayout.EndHorizontal();

            // Expanded details
            if (_isExpanded)
            {
                EditorGUILayout.Space(4);

                var profile = LiveSceneAuditorWindow.ActiveProfile ?? BudgetProfile.GetDefault();

                int maxTris = profile.maxTriangles;
                int maxVerts = profile.maxVertices;
                int maxMats = profile.maxMaterialsInScene;
                float maxTex = profile.maxTextureScore;
                int maxLights = profile.maxTotalLights;

                int curTris = report != null ? report.visibleTriangles : 0;
                int curVerts = report != null ? report.visibleVertices : 0;
                int curMats = report != null ? report.uniqueMaterialsCount : 0;
                float curTex = report != null ? report.textureAreaScore : 0f;
                int curLights = report != null ? report.totalLightsCount : 0;

                DrawMiniBar("Tris", curTris, maxTris);
                DrawMiniBar("Verts", curVerts, maxVerts);
                DrawMiniBar("Materials", curMats, maxMats);
                DrawMiniBar("Tex Score", curTex, maxTex);
                DrawMiniBar("Lights", curLights, maxLights);

                EditorGUILayout.Space(4);

                // Mode Row (Persistent)
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Mode:", EditorStyles.miniBoldLabel, GUILayout.Width(38));
                string[] modeNames = { "Manual", "On Change", "Interval" };
                EditorGUI.BeginChangeCheck();
                var newMode = (LiveSceneAuditorWindow.ScanMode)EditorGUILayout.Popup((int)LiveSceneAuditorWindow.CurrentScanMode, modeNames, EditorStyles.miniButton, GUILayout.Width(88));
                if (EditorGUI.EndChangeCheck())
                {
                    LiveSceneAuditorWindow.CurrentScanMode = newMode;
                    LiveSceneAuditorWindow.BackgroundIntervalTimer = LiveSceneAuditorWindow.ScanIntervalSeconds;
                }

                if (LiveSceneAuditorWindow.CurrentScanMode == LiveSceneAuditorWindow.ScanMode.Interval)
                {
                    GUILayout.Space(4);
                    float cd = Mathf.Max(0f, LiveSceneAuditorWindow.BackgroundIntervalTimer);
                    GUILayout.Label($"{cd:F0}s", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(2);
                EditorGUI.BeginChangeCheck();
                bool syncVal = EditorGUILayout.ToggleLeft(
                    new GUIContent("🔄 Sync Game Cam", "Aligns Game View Camera to Scene View in real-time as you navigate"), 
                    LiveSceneAuditorWindow.SyncWithSceneView, 
                    EditorStyles.miniBoldLabel);
                if (EditorGUI.EndChangeCheck())
                {
                    LiveSceneAuditorWindow.SyncWithSceneView = syncVal;
                }

                EditorGUILayout.Space(2);
                if (GUILayout.Button("Open Auditor Window", EditorStyles.miniButton, GUILayout.Height(20)))
                {
                    LiveSceneAuditorWindow.ShowWindow();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private static bool _pendingRescanAfterCurrentScan = false;

        private static void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_lastEditorTime <= 0)
            {
                _lastEditorTime = now;
                return;
            }

            float dt = (float)(now - _lastEditorTime);
            _lastEditorTime = now;

            if (dt <= 0f || dt > 1f) dt = 0.033f;

            // Watchdog check: ensure IsScanning never hangs indefinitely
            LiveSceneAuditorWindow.CheckScanWatchdog();

            // Trigger initial scan if needed once scene is loaded
            if (!_initialScanDone && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                _initialScanDone = true;
                if (LiveSceneAuditorWindow.CurrentReport == null && !LiveSceneAuditorWindow.IsScanning)
                {
                    LiveSceneAuditorWindow.RequestScan(LiveSceneAuditorWindow.AutoScope == LiveSceneAuditorWindow.ScanScope.EntireScene);
                }
            }

            // Always tick OnChange debounce timer down, even if a scan is currently in flight!
            if (LiveSceneAuditorWindow.CurrentScanMode == LiveSceneAuditorWindow.ScanMode.OnChange)
            {
                if (LiveSceneAuditorWindow.BackgroundChangeDebounceTimer > 0f)
                {
                    LiveSceneAuditorWindow.BackgroundChangeDebounceTimer -= dt;
                    if (LiveSceneAuditorWindow.BackgroundChangeDebounceTimer <= 0f)
                    {
                        LiveSceneAuditorWindow.BackgroundChangeDebounceTimer = -1f;
                        if (LiveSceneAuditorWindow.IsScanning)
                        {
                            // A scan is currently running in background; queue an updated rescan as soon as it finishes
                            _pendingRescanAfterCurrentScan = true;
                        }
                        else
                        {
                            _pendingRescanAfterCurrentScan = false;
                            LiveSceneAuditorWindow.RequestScan(LiveSceneAuditorWindow.AutoScope == LiveSceneAuditorWindow.ScanScope.EntireScene);
                        }
                    }
                }
            }

            // If a previous scan finished and an updated scan was queued while it ran, dispatch it immediately!
            if (!LiveSceneAuditorWindow.IsScanning && _pendingRescanAfterCurrentScan)
            {
                _pendingRescanAfterCurrentScan = false;
                LiveSceneAuditorWindow.RequestScan(LiveSceneAuditorWindow.AutoScope == LiveSceneAuditorWindow.ScanScope.EntireScene);
            }

            // Handle Interval mode in background
            if (!LiveSceneAuditorWindow.IsScanning && LiveSceneAuditorWindow.CurrentScanMode == LiveSceneAuditorWindow.ScanMode.Interval)
            {
                LiveSceneAuditorWindow.BackgroundIntervalTimer -= dt;
                if (LiveSceneAuditorWindow.BackgroundIntervalTimer <= 0f)
                {
                    LiveSceneAuditorWindow.BackgroundIntervalTimer = LiveSceneAuditorWindow.ScanIntervalSeconds;
                    LiveSceneAuditorWindow.RequestScan(LiveSceneAuditorWindow.AutoScope == LiveSceneAuditorWindow.ScanScope.EntireScene);
                }
            }
        }

        private static void OnHierarchyChanged()
        {
            if (LiveSceneAuditorWindow.CurrentScanMode == LiveSceneAuditorWindow.ScanMode.OnChange)
            {
                LiveSceneAuditorWindow.BackgroundChangeDebounceTimer = LiveSceneAuditorWindow.ChangeDebounceDuration;
            }
        }

        private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            if (LiveSceneAuditorWindow.CurrentScanMode == LiveSceneAuditorWindow.ScanMode.OnChange)
            {
                LiveSceneAuditorWindow.BackgroundChangeDebounceTimer = LiveSceneAuditorWindow.ChangeDebounceDuration;
            }
            return modifications;
        }

        private static void ApplySnapping(float viewWidth, float viewHeight)
        {
            float distToLeft = _overlayRect.x - Margin;
            float distToRight = (viewWidth - _overlayRect.xMax) - Margin;
            float distToTop = _overlayRect.y - 25f;
            float distToBottom = (viewHeight - _overlayRect.yMax) - Margin;

            bool snapLeft = distToLeft < SnapThreshold;
            bool snapRight = distToRight < SnapThreshold;
            bool snapTop = distToTop < SnapThreshold;
            bool snapBottom = distToBottom < SnapThreshold;

            if (snapTop && snapLeft) _currentAnchor = DockAnchor.TopLeft;
            else if (snapTop && snapRight) _currentAnchor = DockAnchor.TopRight;
            else if (snapBottom && snapLeft) _currentAnchor = DockAnchor.BottomLeft;
            else if (snapBottom && snapRight) _currentAnchor = DockAnchor.BottomRight;
            else if (snapLeft) _currentAnchor = DockAnchor.TopLeft; // Snap to left edge
            else if (snapRight) _currentAnchor = DockAnchor.TopRight; // Snap to right edge
            else _currentAnchor = DockAnchor.Custom;

            EditorPrefs.SetInt(PrefsKeyAnchor, (int)_currentAnchor);
            EditorPrefs.SetFloat(PrefsKeyPosX, _overlayRect.x);
            EditorPrefs.SetFloat(PrefsKeyPosY, _overlayRect.y);
            SceneView.RepaintAll();
        }

        private static void DrawMiniBar(string label, float current, float max)
        {
            float animCurrent = AuditorUIStyles.GetAnimatedValue("Overlay_" + label, current);
            float ratio = max > 0 ? (animCurrent / max) : 0f;
            Color barColor = ratio > 1.0f ? AuditorUIStyles.ColorDanger : (ratio >= 0.75f ? AuditorUIStyles.ColorWarning : AuditorUIStyles.ColorGood);

            Rect rowRect = GUILayoutUtility.GetRect(100, 16, GUILayout.ExpandWidth(true));

            // 1. Label (left aligned, vertically centered)
            Rect labelRect = new Rect(rowRect.x, rowRect.y, 62, rowRect.height);
            GUIStyle labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft
            };
            GUI.Label(labelRect, label, labelStyle);

            // 2. Value percent (right aligned, vertically centered)
            Rect valRect = new Rect(rowRect.xMax - 44, rowRect.y, 44, rowRect.height);
            GUIStyle valStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = barColor }
            };
            string valText;
            if (ratio <= 0f) valText = "0%";
            else if (ratio < 0.10f) valText = $"{(ratio * 100f):F1}%";
            else valText = $"{Mathf.Round(ratio * 100f):N0}%";

            GUI.Label(valRect, valText, valStyle);

            // 3. Progress bar (middle, vertically centered)
            float barX = labelRect.xMax + 4;
            float barWidth = Mathf.Max(20, valRect.x - barX - 4);
            float barHeight = 8f;
            float barY = rowRect.y + (rowRect.height - barHeight) * 0.5f;

            Rect barBgRect = new Rect(barX, barY, barWidth, barHeight);
            EditorGUI.DrawRect(barBgRect, AuditorUIStyles.ColorBarBg);

            float fillWidth = Mathf.Clamp01(ratio) * barWidth;
            Rect fillRect = new Rect(barX, barY, fillWidth, barHeight);
            EditorGUI.DrawRect(fillRect, barColor);
        }
    }
}
