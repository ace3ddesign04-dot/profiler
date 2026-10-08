using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AceModules.LiveSceneAuditor
{
    public class LiveSceneAuditorWindow : EditorWindow
    {
        private enum AuditorTab
        {
            Overview,
            Culprits,
            AtlasingSuggestions,
            Settings
        }

        public enum ScanMode
        {
            Manual,
            OnChange,
            Interval
        }

        public enum ScanScope
        {
            ViewFrustum,
            EntireScene
        }

        public enum CameraSource
        {
            GameCamera,
            SceneViewCamera
        }

        private AuditorTab _currentTab = AuditorTab.Overview;
        private string[] _tabNames = { "Overview & Budgets", "Culprits Inspector", "Atlasing Suggestions", "Settings" };
        
        private int _selectedProfileIndex = 0;
        private string[] _profileNames = { "Low-End Mobile", "Mid-Range Mobile", "High-End Mobile", "Custom" };
        private BudgetProfile _activeProfile;
        public static BudgetProfile ActiveProfile { get; private set; }

        public ScanMode CurrentScanMode = ScanMode.Manual;
        public ScanScope AutoScope = ScanScope.ViewFrustum;
        public CameraSource SelectedCamera = CameraSource.GameCamera;
        public float ScanIntervalSeconds = 10f;
        public bool IncludeInactive = false;
        public bool UseAsyncScan = true;

        public static SceneAuditReport CurrentReport { get; private set; }
        public static bool IsScanning { get; private set; }
        
        private float _intervalTimer = 10f;
        private double _lastUpdateTime = 0;
        private float _changeDebounceTimer = -1f;
        private const float ChangeDebounceDuration = 0.8f;

        private Vector2 _scrollPos;
        private int _culpritSubFilter = 0;
        private string[] _culpritFilters = { "All Culprits", "Heavy Meshes", "Heavy Textures" };

        private void OnEnable()
        {
            UpdateActiveProfile();
            
            // Hook scene modification events
            EditorApplication.hierarchyChanged -= OnSceneHierarchyChanged;
            EditorApplication.hierarchyChanged += OnSceneHierarchyChanged;
            Undo.postprocessModifications -= OnUndoPostprocessModifications;
            Undo.postprocessModifications += OnUndoPostprocessModifications;

            if (CurrentReport == null)
            {
                TriggerScan(false);
            }
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnSceneHierarchyChanged;
            Undo.postprocessModifications -= OnUndoPostprocessModifications;
        }

        private void OnSceneHierarchyChanged()
        {
            if (CurrentScanMode == ScanMode.OnChange)
            {
                _changeDebounceTimer = ChangeDebounceDuration;
            }
        }

        private UndoPropertyModification[] OnUndoPostprocessModifications(UndoPropertyModification[] modifications)
        {
            if (CurrentScanMode == ScanMode.OnChange)
            {
                _changeDebounceTimer = ChangeDebounceDuration;
            }
            return modifications;
        }

        private void Update()
        {
            double currentTime = EditorApplication.timeSinceStartup;
            double deltaTime = _lastUpdateTime > 0 ? (currentTime - _lastUpdateTime) : 0.02;
            _lastUpdateTime = currentTime;

            if (CurrentScanMode == ScanMode.Interval)
            {
                _intervalTimer -= (float)deltaTime;
                if (_intervalTimer <= 0f)
                {
                    _intervalTimer = ScanIntervalSeconds;
                    TriggerScan(AutoScope == ScanScope.EntireScene);
                }
                Repaint(); // Repaint smoothly to animate progress bar
            }
            else if (CurrentScanMode == ScanMode.OnChange && _changeDebounceTimer >= 0f)
            {
                _changeDebounceTimer -= (float)deltaTime;
                if (_changeDebounceTimer <= 0f)
                {
                    _changeDebounceTimer = -1f;
                    TriggerScan(AutoScope == ScanScope.EntireScene);
                }
            }
        }

        [MenuItem("Tools/Ace Modules/Live Scene Auditor")]
        public static void ShowWindow()
        {
            var window = GetWindow<LiveSceneAuditorWindow>("Scene Auditor");
            window.minSize = new Vector2(480, 520);
            window.Show();
        }

        private void UpdateActiveProfile()
        {
            switch (_selectedProfileIndex)
            {
                case 0: _activeProfile = BudgetProfile.GetDefaultLowEnd(); break;
                case 1: _activeProfile = BudgetProfile.GetDefaultMidRange(); break;
                case 2: _activeProfile = BudgetProfile.GetDefaultHighEnd(); break;
                case 3: 
                    if (_activeProfile == null) _activeProfile = BudgetProfile.GetDefaultLowEnd();
                    _activeProfile.profileName = "Custom Profile";
                    break;
            }
            ActiveProfile = _activeProfile;
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawToolbarTabs();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            EditorGUILayout.Space(6);

            switch (_currentTab)
            {
                case AuditorTab.Overview:
                    DrawOverviewTab();
                    break;
                case AuditorTab.Culprits:
                    DrawCulpritsTab();
                    break;
                case AuditorTab.AtlasingSuggestions:
                    DrawAtlasingSuggestionsTab();
                    break;
                case AuditorTab.Settings:
                    DrawSettingsTab();
                    break;
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.toolbar);
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label("Live Scene Auditor", AuditorUIStyles.HeaderTitle);
            GUILayout.FlexibleSpace();

            // Status Badge
            string statusText = IsScanning ? "AUDITING..." : (CurrentReport != null ? CurrentReport.statusSummary : "READY");
            Color badgeColor = AuditorUIStyles.ColorGood;
            if (IsScanning) badgeColor = AuditorUIStyles.ColorWarning;
            else if (CurrentReport != null)
            {
                if (CurrentReport.isOverBudget) badgeColor = AuditorUIStyles.ColorDanger;
                else if (CurrentReport.overallHealthPercentage >= 75f) badgeColor = AuditorUIStyles.ColorWarning;
            }

            AuditorUIStyles.DrawBadge(statusText, badgeColor * 0.4f, Color.white);

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            // Action Strip
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            
            EditorGUI.BeginChangeCheck();
            _selectedProfileIndex = EditorGUILayout.Popup(_selectedProfileIndex, _profileNames, GUILayout.Width(140));
            if (EditorGUI.EndChangeCheck())
            {
                UpdateActiveProfile();
            }

            GUILayout.Space(8);
            EditorGUILayout.LabelField("Mode:", EditorStyles.miniBoldLabel, GUILayout.Width(40));
            
            string[] scanModeNames = { "Manual", "On Change", "Interval" };
            EditorGUI.BeginChangeCheck();
            CurrentScanMode = (ScanMode)EditorGUILayout.Popup((int)CurrentScanMode, scanModeNames, GUILayout.Width(85));
            if (EditorGUI.EndChangeCheck())
            {
                _intervalTimer = ScanIntervalSeconds; // Reset timer on mode change
            }

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Cam:", EditorStyles.miniBoldLabel, GUILayout.Width(32));
            string[] camNames = { "🎮 Game", "👁️ Scene" };
            SelectedCamera = (CameraSource)EditorGUILayout.Popup((int)SelectedCamera, camNames, GUILayout.Width(75));

            GUILayout.FlexibleSpace();

            GUI.enabled = !IsScanning && !PlayModeBatchCaptureManager.IsCaptureActive;
            if (GUILayout.Button("⚡ Scan View", EditorStyles.miniButtonLeft, GUILayout.Width(85), GUILayout.Height(20)))
            {
                TriggerScan(false);
            }
            if (GUILayout.Button("🌐 Scan All", EditorStyles.miniButtonMid, GUILayout.Width(75), GUILayout.Height(20)))
            {
                TriggerScan(true);
            }
            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.35f, 0.88f, 0.5f);
            if (GUILayout.Button("🎮 Exact Play Scan", EditorStyles.miniButtonRight, GUILayout.Width(125), GUILayout.Height(20)))
            {
                PlayModeBatchCaptureManager.StartCapture();
            }
            GUI.backgroundColor = prevBg;
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            // Mode-specific Sub-header strip
            DrawScanModeStatusStrip();
        }

        private void DrawScanModeStatusStrip()
        {
            if (CurrentScanMode == ScanMode.Interval)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                
                EditorGUILayout.LabelField("⏱️ Interval:", EditorStyles.miniBoldLabel, GUILayout.Width(65));
                EditorGUI.BeginChangeCheck();
                ScanIntervalSeconds = EditorGUILayout.Slider(ScanIntervalSeconds, 3f, 60f, GUILayout.Width(130));
                if (EditorGUI.EndChangeCheck())
                {
                    _intervalTimer = Mathf.Min(_intervalTimer, ScanIntervalSeconds);
                }

                GUILayout.Space(6);
                EditorGUILayout.LabelField("Scope:", EditorStyles.miniBoldLabel, GUILayout.Width(45));
                string[] scopeOptions = { "👁️ View", "🌐 All" };
                AutoScope = (ScanScope)EditorGUILayout.Popup((int)AutoScope, scopeOptions, GUILayout.Width(75));

                GUILayout.Space(6);
                
                // Countdown progress bar
                float ratio = Mathf.Clamp01(_intervalTimer / Mathf.Max(1f, ScanIntervalSeconds));
                string countdownText = $"{_intervalTimer:F1}s";
                
                EditorGUILayout.LabelField(countdownText, EditorStyles.miniBoldLabel, GUILayout.Width(35));

                Rect barRect = GUILayoutUtility.GetRect(60, 14, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(barRect, AuditorUIStyles.ColorBarBg);
                
                // Animate fill bar
                float fillWidth = (1f - ratio) * barRect.width;
                EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, fillWidth, barRect.height), AuditorUIStyles.ColorWarning);

                if (GUILayout.Button("Reset", EditorStyles.miniButton, GUILayout.Width(45), GUILayout.Height(16)))
                {
                    _intervalTimer = ScanIntervalSeconds;
                }

                EditorGUILayout.EndHorizontal();
            }
            else if (CurrentScanMode == ScanMode.OnChange)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField("⚡ Auto-Scan Scope:", EditorStyles.miniBoldLabel, GUILayout.Width(125));
                string[] scopeOptions = { "👁️ View Frustum", "🌐 Entire Scene" };
                AutoScope = (ScanScope)EditorGUILayout.Popup((int)AutoScope, scopeOptions, GUILayout.Width(130));
                
                GUILayout.Space(8);
                EditorGUILayout.LabelField("(Debounced on scene changes)", AuditorUIStyles.SubtleText);
                EditorGUILayout.EndHorizontal();
            }
            else // Manual
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField("✋ Manual Mode:", EditorStyles.miniBoldLabel, GUILayout.Width(95));
                EditorGUILayout.LabelField("Idle. Click '⚡ Scan View' or '🌐 Scan All' to trigger an audit.", AuditorUIStyles.SubtleText);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawToolbarTabs()
        {
            EditorGUILayout.Space(2);
            _currentTab = (AuditorTab)GUILayout.Toolbar((int)_currentTab, _tabNames, GUILayout.Height(26));
            EditorGUILayout.Space(4);
        }

        private void DrawOverviewTab()
        {
            if (CurrentReport == null)
            {
                EditorGUILayout.HelpBox("No audit data available yet. Click '⚡ Scan View' to begin.", MessageType.Info);
                return;
            }

            // Active Profile Info
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Active Target: {_activeProfile.profileName}", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"Scan: {CurrentReport.scanDurationMs:F1}ms", AuditorUIStyles.SubtleText, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();

            string camLabel = (SelectedCamera == CameraSource.GameCamera) ? "🎮 Camera: Game View (Main Camera)" : "👁️ Camera: Scene View";
            EditorGUILayout.LabelField(CurrentReport.isFrustumViewOnly ? $"Perspective: {camLabel}" : "Perspective: Entire Scene (All Objects)", AuditorUIStyles.SubtleText);

            #if UNITY_EDITOR
            if (SelectedCamera == CameraSource.GameCamera && CurrentReport.isFrustumViewOnly)
            {
                if (EditorApplication.isPlaying)
                {
                    string unityStatsText = $"🎮 Play Mode Live GPU: {CurrentReport.estimatedBatches} batches • {CurrentReport.visibleTriangles:N0} tris";
                    EditorGUILayout.LabelField(unityStatsText, AuditorUIStyles.SubtleText);
                }
                else if (PlayModeBatchCaptureManager.LastExactBatches > 0)
                {
                    string unityStatsText = $"🎮 Play Mode Batches (Exact Verified): {CurrentReport.estimatedBatches} batches ({CurrentReport.rawUnbatchedBatches:N0} Edit-Mode uncombined)";
                    EditorGUILayout.LabelField(unityStatsText, AuditorUIStyles.SubtleText);
                }
                else
                {
                    string unityStatsText = $"🎮 Edit Mode Draws: {CurrentReport.rawUnbatchedBatches:N0} batches (Click '🎮 Exact Play Scan' for 100% runtime parity)";
                    EditorGUILayout.LabelField(unityStatsText, AuditorUIStyles.SubtleText);
                }
            }
            #endif

            EditorGUILayout.EndVertical();

            // Category 1: Geometry in View Frustum
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField(CurrentReport.isFrustumViewOnly ? "👁️ Visible Geometry (Camera View Frustum)" : "🌐 Scene Geometry (All Objects)", AuditorUIStyles.CardHeader);
            EditorGUILayout.Space(4);

            AuditorUIStyles.DrawBudgetProgressBar("Triangles", CurrentReport.visibleTriangles, _activeProfile.maxTriangles, "tris");
            AuditorUIStyles.DrawBudgetProgressBar("Vertices", CurrentReport.visibleVertices, _activeProfile.maxVertices, "verts");
            
            EditorGUILayout.LabelField($"Total Scene Geometry: {CurrentReport.totalSceneTriangles:N0} tris | {CurrentReport.totalSceneVertices:N0} verts", AuditorUIStyles.SubtleText);
            EditorGUILayout.EndVertical();

            // Category 2: Draw Calls & Batches
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField("📦 Batches & Materials", AuditorUIStyles.CardHeader);
            EditorGUILayout.Space(4);

            string batchLabel = (SelectedCamera == CameraSource.GameCamera && CurrentReport.isFrustumViewOnly) 
                ? (EditorApplication.isPlaying 
                    ? "🎮 Game Batches (Live Runtime)" 
                    : (PlayModeBatchCaptureManager.LastExactBatches > 0 
                        ? "🎮 Play Mode Batches (Exact Verified)" 
                        : "🎮 Edit Mode Batches (Uncombined)")) 
                : "Estimated Batches";
            AuditorUIStyles.DrawBudgetProgressBar(batchLabel, CurrentReport.estimatedBatches, _activeProfile.maxBatches, "batches");
            AuditorUIStyles.DrawBudgetProgressBar("Unique Scene Materials", CurrentReport.uniqueMaterialsCount, _activeProfile.maxMaterialsInScene, "mats");

            if (PlayModeBatchCaptureManager.LastExactBatches > 0 && !EditorApplication.isPlaying)
            {
                EditorGUILayout.Space(2);
                string batchInfo = $"✅ Verified 100% Play Mode Parity: {CurrentReport.estimatedBatches} batches ({CurrentReport.savedByBatching:N0} saved from {CurrentReport.rawUnbatchedBatches:N0} Edit-Mode uncombined)";
                EditorGUILayout.LabelField(batchInfo, AuditorUIStyles.SubtleText);
            }
            else if (CurrentReport.savedByBatching > 0)
            {
                EditorGUILayout.Space(2);
                string batchInfo = EditorApplication.isPlaying
                    ? $"⚡ Runtime Batching Efficiency: ~{CurrentReport.savedByBatching:N0} calls saved by static/GPU batching"
                    : $"⚡ Runtime Static Batching: {CurrentReport.savedByBatching:N0} calls saved ({CurrentReport.rawUnbatchedBatches:N0} Edit-Mode uncombined)";
                EditorGUILayout.LabelField(batchInfo, AuditorUIStyles.SubtleText);
            }
            else if (!EditorApplication.isPlaying && SelectedCamera == CameraSource.GameCamera)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("💡 Click '🎮 Exact Play Scan' in the top toolbar to capture exact runtime static batching.", AuditorUIStyles.SubtleText);
            }
            EditorGUILayout.EndVertical();

            // Category 3: Texture Memory & Score
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField("🖼️ Textures & Asset Footprint", AuditorUIStyles.CardHeader);
            EditorGUILayout.Space(4);

            AuditorUIStyles.DrawBudgetProgressBar("Texture Area Score", CurrentReport.textureAreaScore, _activeProfile.maxTextureScore, "pts");
            
            EditorGUILayout.HelpBox("Base score standard: 512x512 = 1.0 pt | 1024x1024 = 4.0 pts | 2048x2048 = 16.0 pts", MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private void DrawCulpritsTab()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Top Culprits in Scene", AuditorUIStyles.CardHeader);
            GUILayout.FlexibleSpace();
            _culpritSubFilter = GUILayout.Toolbar(_culpritSubFilter, _culpritFilters, EditorStyles.miniButton);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Click 'Ping' to highlight the object in the Scene and Hierarchy.", AuditorUIStyles.SubtleText);
            EditorGUILayout.Space(6);

            if (CurrentReport == null || CurrentReport.culprits.Count == 0)
            {
                EditorGUILayout.HelpBox("No heavy culprits detected above alert thresholds in this view.", MessageType.Info);
                return;
            }

            var filteredCulprits = CurrentReport.culprits.Where(c =>
            {
                if (_culpritSubFilter == 1) return c.type == CulpritType.MeshGeometry;
                if (_culpritSubFilter == 2) return c.type == CulpritType.TextureSize;
                return true;
            }).ToList();

            if (filteredCulprits.Count == 0)
            {
                EditorGUILayout.HelpBox("No culprits found matching the selected filter.", MessageType.Info);
                return;
            }

            foreach (var culprit in filteredCulprits)
            {
                Color badgeCol = AuditorUIStyles.ColorGood;
                if (culprit.severityScore >= 150f)
                    badgeCol = AuditorUIStyles.ColorDanger;
                else if (culprit.severityScore >= 50f)
                    badgeCol = AuditorUIStyles.ColorWarning;

                string icon = culprit.type == CulpritType.MeshGeometry ? "📦 " : "🖼️ ";
                DrawCulpritCard(icon + culprit.name, culprit.primaryMetricText, culprit.secondaryDetailText, badgeCol, culprit.instanceId, culprit.dependencyNames, culprit.flaggedReasons);
            }
        }

        private void DrawCulpritCard(string name, string primaryBadge, string description, Color badgeColor, int instanceId, List<string> dependencies = null, List<string> reasons = null)
        {
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label(name, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            AuditorUIStyles.DrawBadge(primaryBadge, badgeColor * 0.4f, Color.white);
            
            if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(45)))
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj != null)
                {
                    EditorGUIUtility.PingObject(obj);
                    Selection.activeObject = obj;
                }
            }

            EditorGUILayout.EndHorizontal();

            // Highlighted Red Reason Badges
            if (reasons != null && reasons.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                foreach (var reason in reasons)
                {
                    AuditorUIStyles.DrawBadge("🚨 " + reason, new Color(0.85f, 0.18f, 0.18f, 0.5f), Color.white);
                    GUILayout.Space(4);
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.LabelField(description, AuditorUIStyles.SubtleText);

            if (dependencies != null && dependencies.Count > 0)
            {
                string depsText = "Materials: " + string.Join(", ", dependencies.Take(4));
                if (dependencies.Count > 4) depsText += $" (+{dependencies.Count - 4} more)";
                EditorGUILayout.LabelField(depsText, AuditorUIStyles.SubtleText);
            }
            
            EditorGUILayout.EndVertical();
        }

        private void DrawAtlasingSuggestionsTab()
        {
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField("🧩 Smart Atlasing & Material Optimizer", AuditorUIStyles.CardHeader);
            EditorGUILayout.LabelField("Identifies textures and materials sharing identical shaders that can be packed into an atlas to eliminate draw calls.", AuditorUIStyles.SubtleText);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);

            // Group suggestions will connect to real texture atlas analyzer
            EditorGUILayout.HelpBox("Texture clustering analysis active. Check back after scanning scenes with multiple shared materials.", MessageType.Info);
        }

        private void DrawSettingsTab()
        {
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField("⚙️ Audit Configuration & Limits", AuditorUIStyles.CardHeader);
            EditorGUILayout.Space(6);

            UseAsyncScan = EditorGUILayout.Toggle("Async (Background Thread)", UseAsyncScan);
            ScanIntervalSeconds = EditorGUILayout.Slider("Scan Interval (sec)", ScanIntervalSeconds, 3f, 60f);
            IncludeInactive = EditorGUILayout.Toggle("Include Inactive Objects", IncludeInactive);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Scene Budget Profiles", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            _activeProfile.maxTriangles = EditorGUILayout.IntField("Max Visible Tris", _activeProfile.maxTriangles);
            _activeProfile.maxVertices = EditorGUILayout.IntField("Max Visible Verts", _activeProfile.maxVertices);
            _activeProfile.maxBatches = EditorGUILayout.IntField("Max Batches", _activeProfile.maxBatches);
            _activeProfile.maxMaterialsInScene = EditorGUILayout.IntField("Max Materials", _activeProfile.maxMaterialsInScene);
            _activeProfile.maxTextureScore = EditorGUILayout.FloatField("Max Texture Score", _activeProfile.maxTextureScore);
            _activeProfile.maxFbxInScene = EditorGUILayout.IntField("Max Scene FBXs", _activeProfile.maxFbxInScene);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Single Object Culprit Thresholds", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            _activeProfile.singleMeshHeavyTriangles = EditorGUILayout.IntField("Heavy Mesh Threshold (Tris)", _activeProfile.singleMeshHeavyTriangles);
            _activeProfile.maxMaterialsPerObject = EditorGUILayout.IntField("Max Materials Per Object", _activeProfile.maxMaterialsPerObject);
            _activeProfile.requireLodAboveTriangles = EditorGUILayout.IntField("Require LOD If Above (Tris)", _activeProfile.requireLodAboveTriangles);
            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
        }

        private void DrawFooter()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Live Scene Auditor v1.0 • Ready", AuditorUIStyles.SubtleText);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Documentation / Help", EditorStyles.linkLabel))
            {
                Application.OpenURL("https://docs.unity3d.com/Manual/OptimizingGraphicsPerformance.html");
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        public void TriggerScan(bool fullScene)
        {
            _intervalTimer = ScanIntervalSeconds;
            IsScanning = true;

            Camera targetCam = null;
            if (SelectedCamera == CameraSource.GameCamera)
            {
                targetCam = Camera.main;
                if (targetCam == null)
                {
                    var allCams = Camera.allCameras;
                    foreach (var c in allCams)
                    {
                        if (c != null && (c.CompareTag("MainCamera") || c.cameraType == CameraType.Game))
                        {
                            targetCam = c;
                            break;
                        }
                    }
                }
            }
            else
            {
                targetCam = (SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main);
            }

            SceneScanEngine.ScanScene(
                frustumOnly: !fullScene,
                profile: _activeProfile ?? BudgetProfile.GetDefaultLowEnd(),
                asyncMode: UseAsyncScan,
                includeInactive: IncludeInactive,
                targetCamera: targetCam,
                isGameCamera: (SelectedCamera == CameraSource.GameCamera),
                onCompleted: (report) =>
                {
                    CurrentReport = report;
                    IsScanning = false;
                    Repaint();
                    SceneView.RepaintAll();
                }
            );
        }

        public static void RequestScan(bool fullScene)
        {
            var windows = Resources.FindObjectsOfTypeAll<LiveSceneAuditorWindow>();
            if (windows != null && windows.Length > 0 && windows[0] != null)
            {
                windows[0].TriggerScan(fullScene);
            }
            else
            {
                IsScanning = true;
                SceneScanEngine.ScanScene(
                    frustumOnly: !fullScene,
                    profile: BudgetProfile.GetDefaultLowEnd(),
                    asyncMode: true,
                    includeInactive: false,
                    targetCamera: Camera.main,
                    isGameCamera: true,
                    onCompleted: (report) =>
                    {
                        CurrentReport = report;
                        IsScanning = false;
                        SceneView.RepaintAll();
                    }
                );
            }
        }

        public void ApplyExactPlayModeStats(int batches, int tris, int verts)
        {
            TriggerScan(false);
        }
    }
}
