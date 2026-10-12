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

        private AuditorTab _currentTab = AuditorTab.Overview;
        private static readonly string[] _tabNames = { "Overview & Budgets", "Culprits Inspector", "Atlasing Suggestions", "Settings" };
        
        private const string PrefsKeyBudgetProfile = "AceModules_Auditor_BudgetProfile";
        private static BudgetProfile _activeProfile;
        public static BudgetProfile ActiveProfile
        {
            get
            {
                if (_activeProfile == null)
                {
                    LoadBudgetProfile();
                }
                return _activeProfile;
            }
        }

        private const string PrefsKeySyncCam = "AceModules_Auditor_SyncGameCam";
        private const string PrefsKeyScanMode = "AceModules_Auditor_ScanMode";
        private const string PrefsKeyAutoScope = "AceModules_Auditor_AutoScope";
        private const string PrefsKeyScanInterval = "AceModules_Auditor_ScanInterval";
        private const string PrefsKeyIncludeInactive = "AceModules_Auditor_IncludeInactive";
        private const string PrefsKeyUseAsync = "AceModules_Auditor_UseAsync";

        public static ScanMode CurrentScanMode
        {
            get => (ScanMode)EditorPrefs.GetInt(PrefsKeyScanMode, (int)ScanMode.Manual);
            set => EditorPrefs.SetInt(PrefsKeyScanMode, (int)value);
        }

        public static ScanScope AutoScope
        {
            get => (ScanScope)EditorPrefs.GetInt(PrefsKeyAutoScope, (int)ScanScope.ViewFrustum);
            set => EditorPrefs.SetInt(PrefsKeyAutoScope, (int)value);
        }

        public static float ScanIntervalSeconds
        {
            get => EditorPrefs.GetFloat(PrefsKeyScanInterval, 10f);
            set => EditorPrefs.SetFloat(PrefsKeyScanInterval, Mathf.Clamp(value, 3f, 60f));
        }

        public static bool IncludeInactive
        {
            get => EditorPrefs.GetBool(PrefsKeyIncludeInactive, false);
            set => EditorPrefs.SetBool(PrefsKeyIncludeInactive, value);
        }

        public static bool UseAsyncScan
        {
            get => EditorPrefs.GetBool(PrefsKeyUseAsync, true);
            set => EditorPrefs.SetBool(PrefsKeyUseAsync, value);
        }

        private const string PrefsKeyCulpritDisplayLimit = "AceModules_Auditor_CulpritDisplayLimit";
        public static int CulpritDisplayLimit
        {
            get => EditorPrefs.GetInt(PrefsKeyCulpritDisplayLimit, 25);
            set => EditorPrefs.SetInt(PrefsKeyCulpritDisplayLimit, Mathf.Clamp(value, 1, 1000));
        }

        public static bool SyncWithSceneView
        {
            get => EditorPrefs.GetBool(PrefsKeySyncCam, false);
            set
            {
                EditorPrefs.SetBool(PrefsKeySyncCam, value);
                if (value && SceneView.lastActiveSceneView != null)
                {
                    SyncCameraNow(SceneView.lastActiveSceneView);
                }
            }
        }
        public static Camera CustomCamera;

        public static SceneAuditReport CurrentReport { get; private set; }
        public static bool IsScanning { get; private set; }
        
        public static float BackgroundIntervalTimer = 10f;
        public static float BackgroundChangeDebounceTimer = -1f;
        public const float ChangeDebounceDuration = 0.8f;

        private Vector2 _scrollPos;
        private int _culpritSubFilter = 0;
        private static readonly string[] _culpritFilters = { "All Culprits", "Heavy Meshes", "Heavy Textures", "Lights", "📍 Views" };

        // Culprit sorting state
        public enum CulpritSortField { Severity, Tris, Verts, Occurrences, Materials, TexScore, Resolution, Name, Range, Intensity, Shadows, LightType }
        private CulpritSortField _meshSortField = CulpritSortField.Severity;
        private bool _meshSortAscending = false;
        private CulpritSortField _texSortField = CulpritSortField.Severity;
        private bool _texSortAscending = false;
        private CulpritSortField _lightSortField = CulpritSortField.Severity;
        private bool _lightSortAscending = false;

        // Viewpoint sorting & foldout state
        public enum ViewpointSortField { Severity, Tris, Verts, Materials, Lights, Name }
        private ViewpointSortField _viewSortField = ViewpointSortField.Severity;
        private bool _viewSortAscending = false;
        private readonly HashSet<int> _expandedViewpoints = new HashSet<int>();
        private readonly HashSet<string> _expandedSections = new HashSet<string>();

        // Atlasing sorting state
        private enum AtlasScopeFilter { EntireScene, CurrentView }
        private AtlasScopeFilter _atlasScope = AtlasScopeFilter.EntireScene;
        private readonly HashSet<string> _collapsedAtlasGroups = new HashSet<string>();

        public enum AtlasGroupSortField { Materials, Savings, TextureScore, ShaderName }
        private AtlasGroupSortField _atlasGroupSortField = AtlasGroupSortField.Materials;
        private bool _atlasGroupSortAscending = false;

        public enum AtlasMaterialSortField { Usage, TextureScore, TextureSize, MaterialName }
        private AtlasMaterialSortField _atlasMaterialSort = AtlasMaterialSortField.Usage;
        private bool _atlasMaterialSortAscending = false;

        // View-based audit vantage points
        private const string PrefsKeyViewpointPaths = "AceModules_Auditor_Viewpoints";
        [SerializeField] private List<Transform> _auditViewpoints = new List<Transform>();

        public static List<Transform> GetValidViewpoints()
        {
            var win = GetActiveWindow();
            if (win != null && win._auditViewpoints != null)
            {
                return win._auditViewpoints.Where(t => t != null).ToList();
            }
            return new List<Transform>();
        }

        private void SaveViewpointPaths()
        {
            if (_auditViewpoints == null) return;
            List<string> paths = new List<string>();
            foreach (var t in _auditViewpoints)
            {
                if (t != null)
                {
                    paths.Add(GetHierarchyPath(t.gameObject));
                }
            }
            EditorPrefs.SetString(PrefsKeyViewpointPaths, string.Join(";", paths));
        }

        private void RestoreViewpointPaths()
        {
            if (_auditViewpoints == null) _auditViewpoints = new List<Transform>();
            _auditViewpoints.RemoveAll(t => t == null);

            if (_auditViewpoints.Count == 0)
            {
                string saved = EditorPrefs.GetString(PrefsKeyViewpointPaths, string.Empty);
                if (!string.IsNullOrEmpty(saved))
                {
                    string[] parts = saved.Split(';');
                    foreach (var p in parts)
                    {
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        GameObject go = GameObject.Find(p);
                        if (go != null && !_auditViewpoints.Contains(go.transform))
                        {
                            _auditViewpoints.Add(go.transform);
                        }
                    }
                }
            }
        }

        private static string GetHierarchyPath(GameObject obj)
        {
            if (obj == null) return string.Empty;
            string path = "/" + obj.name;
            Transform cur = obj.transform;
            while (cur.parent != null)
            {
                cur = cur.parent;
                path = "/" + cur.name + path;
            }
            return path;
        }

        public void JumpToViewpoint(Transform vp)
        {
            if (vp == null) return;

            // 1. Select the viewpoint GameObject and ping it in the hierarchy
            Selection.activeGameObject = vp.gameObject;
            EditorGUIUtility.PingObject(vp.gameObject);

            // 2. Ensure Scene View is focused and trigger Unity's native "Align View to Selected" command
            if (SceneView.lastActiveSceneView == null && SceneView.sceneViews.Count > 0)
            {
                ((SceneView)SceneView.sceneViews[0]).Focus();
            }
            else if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.Focus();
            }

            // Execute Unity's native built-in menu item: "GameObject/Align View to Selected"
            EditorApplication.ExecuteMenuItem("GameObject/Align View to Selected");

            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.Repaint();
            }

            // 3. Align target Game Camera to viewpoint
            Camera targetCam = GetTargetGameCamera();
            if (targetCam != null)
            {
                targetCam.transform.SetPositionAndRotation(vp.position, vp.rotation);
                SceneScanEngine.RequestGameViewRepaint();
            }
        }

        public void CaptureSceneViewAsViewpoint()
        {
            if (SceneView.lastActiveSceneView == null || SceneView.lastActiveSceneView.camera == null)
            {
                EditorUtility.DisplayDialog("Scene View Inactive", "Please open or click into the Scene View to capture camera vantage point.", "OK");
                return;
            }

            Camera svCam = SceneView.lastActiveSceneView.camera;
            if (_auditViewpoints == null) _auditViewpoints = new List<Transform>();

            GameObject vpGo = new GameObject($"Viewpoint_{_auditViewpoints.Count + 1}");
            vpGo.transform.SetPositionAndRotation(svCam.transform.position, svCam.transform.rotation);
            Undo.RegisterCreatedObjectUndo(vpGo, "Create Auditor Viewpoint");
            Selection.activeGameObject = vpGo;

            _auditViewpoints.Add(vpGo.transform);
            SaveViewpointPaths();
            TriggerScan(AutoScope == ScanScope.EntireScene);
        }

        private void OnEnable()
        {
            LoadBudgetProfile();
            RestoreViewpointPaths();

            if (CurrentReport == null)
            {
                RequestScan(AutoScope == ScanScope.EntireScene);
            }
        }

        private void OnDisable()
        {
        }

        private void Update()
        {
            if (SyncWithSceneView && SceneView.lastActiveSceneView != null)
            {
                SyncCameraNow(SceneView.lastActiveSceneView);
            }

            if (AuditorUIStyles.HasActiveAnimations || CurrentScanMode == ScanMode.Interval)
            {
                Repaint();
            }
        }

        [MenuItem("Tools/Ace Modules/Live Scene Auditor")]
        public static void ShowWindow()
        {
            var window = GetWindow<LiveSceneAuditorWindow>("Scene Auditor");
            window.minSize = new Vector2(480, 520);
            window.Show();
        }

        public static void LoadBudgetProfile()
        {
            string json = EditorPrefs.GetString(PrefsKeyBudgetProfile, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    _activeProfile = JsonUtility.FromJson<BudgetProfile>(json);
                }
                catch {}
            }
            if (_activeProfile == null)
            {
                _activeProfile = BudgetProfile.GetDefault();
            }
        }

        public static void SaveBudgetProfile()
        {
            if (_activeProfile != null)
            {
                string json = JsonUtility.ToJson(_activeProfile);
                EditorPrefs.SetString(PrefsKeyBudgetProfile, json);
            }
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
            
            EditorGUILayout.LabelField("Mode:", EditorStyles.miniBoldLabel, GUILayout.Width(40));
            
            string[] scanModeNames = { "Manual", "On Change", "Interval" };
            EditorGUI.BeginChangeCheck();
            ScanMode newMode = (ScanMode)EditorGUILayout.Popup((int)CurrentScanMode, scanModeNames, GUILayout.Width(85));
            if (EditorGUI.EndChangeCheck())
            {
                CurrentScanMode = newMode;
                BackgroundIntervalTimer = ScanIntervalSeconds; // Reset timer on mode change
            }

            GUILayout.Space(8);
            EditorGUI.BeginChangeCheck();
            bool newSync = EditorGUILayout.ToggleLeft(
                new GUIContent("🔄 Sync with Scene", "Aligns Game View Camera position & angle to Scene View in real-time as you navigate"), 
                SyncWithSceneView, 
                GUILayout.Width(135));
            if (EditorGUI.EndChangeCheck())
            {
                SyncWithSceneView = newSync;
            }

            GUILayout.FlexibleSpace();

            GUI.enabled = !IsScanning;
            if (GUILayout.Button("⚡ Scan View", EditorStyles.miniButtonLeft, GUILayout.Width(90), GUILayout.Height(20)))
            {
                RequestScan(false);
            }
            if (GUILayout.Button("🌐 Scan All", EditorStyles.miniButtonMid, GUILayout.Width(80), GUILayout.Height(20)))
            {
                RequestScan(true);
            }
            if (GUILayout.Button("📄 Generate Report (PDF)", EditorStyles.miniButtonRight, GUILayout.Width(155), GUILayout.Height(20)))
            {
                GenerateReport();
            }
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
                float newInterval = EditorGUILayout.Slider(ScanIntervalSeconds, 3f, 60f, GUILayout.Width(130));
                if (EditorGUI.EndChangeCheck())
                {
                    ScanIntervalSeconds = newInterval;
                    BackgroundIntervalTimer = Mathf.Min(BackgroundIntervalTimer, newInterval);
                }

                GUILayout.Space(6);
                EditorGUILayout.LabelField("Scope:", EditorStyles.miniBoldLabel, GUILayout.Width(45));
                string[] scopeOptions = { "👁️ View", "🌐 All" };
                EditorGUI.BeginChangeCheck();
                ScanScope newScope = (ScanScope)EditorGUILayout.Popup((int)AutoScope, scopeOptions, GUILayout.Width(75));
                if (EditorGUI.EndChangeCheck())
                {
                    AutoScope = newScope;
                }

                GUILayout.Space(6);
                
                // Countdown progress bar
                float ratio = Mathf.Clamp01(BackgroundIntervalTimer / Mathf.Max(1f, ScanIntervalSeconds));
                string countdownText = $"{Mathf.Max(0f, BackgroundIntervalTimer):F1}s";
                
                EditorGUILayout.LabelField(countdownText, EditorStyles.miniBoldLabel, GUILayout.Width(35));

                Rect barRect = GUILayoutUtility.GetRect(60, 14, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(barRect, AuditorUIStyles.ColorBarBg);
                
                // Animate fill bar
                float fillWidth = (1f - ratio) * barRect.width;
                EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, fillWidth, barRect.height), AuditorUIStyles.ColorWarning);

                if (GUILayout.Button("Reset", EditorStyles.miniButton, GUILayout.Width(45), GUILayout.Height(16)))
                {
                    BackgroundIntervalTimer = ScanIntervalSeconds;
                }

                EditorGUILayout.EndHorizontal();
            }
            else if (CurrentScanMode == ScanMode.OnChange)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField("⚡ Auto-Scan Scope:", EditorStyles.miniBoldLabel, GUILayout.Width(125));
                string[] scopeOptions = { "👁️ View Frustum", "🌐 Entire Scene" };
                EditorGUI.BeginChangeCheck();
                ScanScope newScope = (ScanScope)EditorGUILayout.Popup((int)AutoScope, scopeOptions, GUILayout.Width(130));
                if (EditorGUI.EndChangeCheck())
                {
                    AutoScope = newScope;
                }
                
                GUILayout.Space(8);
                EditorGUILayout.LabelField("(Debounced on scene & view changes)", AuditorUIStyles.SubtleText);
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
            EditorGUILayout.LabelField("🎯 Scene Budget Audit", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"Scan: {CurrentReport.scanDurationMs:F1}ms", AuditorUIStyles.SubtleText, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();

            Camera activeCam = GetTargetGameCamera();
            string camName = activeCam != null ? activeCam.name : "Main Camera";
            string camDesc = SyncWithSceneView 
                ? $"🎮 Game Camera ({camName}) • 🔄 Synced with Scene View" 
                : $"🎮 Game Camera ({camName})";
            EditorGUILayout.LabelField(CurrentReport.isFrustumViewOnly ? $"Perspective: {camDesc}" : "Perspective: Entire Scene (All Objects)", AuditorUIStyles.SubtleText);

            if (CurrentReport.isFrustumViewOnly)
            {
                EditorGUILayout.LabelField($"🎮 Game View Perspective • {CurrentReport.visibleTriangles:N0} tris • {CurrentReport.visibleVertices:N0} verts", AuditorUIStyles.SubtleText);
            }

            EditorGUILayout.EndVertical();

            // Category 1: Geometry in View Frustum
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField(CurrentReport.isFrustumViewOnly ? "👁️ Visible Geometry (Camera View Frustum)" : "🌐 Scene Geometry (All Objects)", AuditorUIStyles.CardHeader);
            EditorGUILayout.Space(4);

            AuditorUIStyles.DrawBudgetProgressBar("Triangles", CurrentReport.visibleTriangles, _activeProfile.maxTriangles, "tris");
            AuditorUIStyles.DrawBudgetProgressBar("Vertices", CurrentReport.visibleVertices, _activeProfile.maxVertices, "verts");
            
            EditorGUILayout.LabelField($"Total Scene Geometry: {CurrentReport.totalSceneTriangles:N0} tris | {CurrentReport.totalSceneVertices:N0} verts", AuditorUIStyles.SubtleText);
            EditorGUILayout.EndVertical();

            // Category 2: Materials & FBX Assets
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField("🎨 Materials & FBX Assets", AuditorUIStyles.CardHeader);
            EditorGUILayout.Space(4);

            AuditorUIStyles.DrawBudgetProgressBar("Unique Scene Materials", CurrentReport.uniqueMaterialsCount, _activeProfile.maxMaterialsInScene, "mats");
            AuditorUIStyles.DrawBudgetProgressBar("FBX Models in Scene", CurrentReport.fbxModelCount, _activeProfile.maxFbxInScene, "models");

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("💡 Optimize multi-material meshes and use texture atlasing to minimize unique materials.", AuditorUIStyles.SubtleText);
            EditorGUILayout.EndVertical();

            // Category 3: Texture Memory & Score
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField("🖼️ Textures & Asset Footprint", AuditorUIStyles.CardHeader);
            EditorGUILayout.Space(4);

            AuditorUIStyles.DrawBudgetProgressBar("Texture Area Score", CurrentReport.textureAreaScore, _activeProfile.maxTextureScore, "pts");
            
            EditorGUILayout.HelpBox("Base score standard: 512x512 = 1.0 pt | 1024x1024 = 4.0 pts | 2048x2048 = 16.0 pts", MessageType.None);
            EditorGUILayout.EndVertical();

            // Category 4: Lighting & Shadow Budgets
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("💡 Lighting & Shadow Budgets", AuditorUIStyles.CardHeader);
            GUILayout.FlexibleSpace();
            
            // Status badges
            string inactBadge = IncludeInactive && CurrentReport.inactiveLightsCount > 0 
                ? $" ({CurrentReport.totalLightsCount} Active, {CurrentReport.inactiveLightsCount} Inactive)" 
                : $" ({CurrentReport.totalLightsCount} Active)";
            AuditorUIStyles.DrawBadge($"{CurrentReport.totalLightsCount + (IncludeInactive ? CurrentReport.inactiveLightsCount : 0)} Lights{inactBadge}", AuditorUIStyles.ColorCardBg, Color.white);
            GUILayout.Space(4);
            AuditorUIStyles.DrawBadge($"{CurrentReport.visibleLightsCount} in View", AuditorUIStyles.ColorCardBg, Color.white);
            GUILayout.Space(4);
            Color shadowCol = CurrentReport.shadowCasterLightsCount > _activeProfile.maxRealtimeShadowCasters ? AuditorUIStyles.ColorDanger * 0.4f : AuditorUIStyles.ColorCardBg;
            AuditorUIStyles.DrawBadge($"{CurrentReport.shadowCasterLightsCount} Shadow Casters", shadowCol, Color.white);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // Progress bars for light budgets
            AuditorUIStyles.DrawBudgetProgressBar("Directional Lights", CurrentReport.directionalLightsCount, _activeProfile.maxDirectionalLights, "lights");
            AuditorUIStyles.DrawBudgetProgressBar("Point Lights", CurrentReport.pointLightsCount, _activeProfile.maxPointLights, "lights");
            AuditorUIStyles.DrawBudgetProgressBar("Spot Lights", CurrentReport.spotLightsCount, _activeProfile.maxSpotLights, "lights");
            AuditorUIStyles.DrawBudgetProgressBar("Total Scene Lights", CurrentReport.totalLightsCount, _activeProfile.maxTotalLights, "lights");
            AuditorUIStyles.DrawBudgetProgressBar("Realtime Shadow Casters", CurrentReport.shadowCasterLightsCount, _activeProfile.maxRealtimeShadowCasters, "casters");

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Bake Types: {CurrentReport.realtimeLightsCount} Realtime • {CurrentReport.bakedLightsCount} Baked • {CurrentReport.mixedLightsCount} Mixed", AuditorUIStyles.SubtleText);
            GUILayout.FlexibleSpace();
            if (CurrentReport.areaLightsCount > 0 || CurrentReport.otherLightsCount > 0)
            {
                GUILayout.Label($"Other: {CurrentReport.areaLightsCount} Area/Disc", AuditorUIStyles.SubtleText);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            // Informative Guide on Computation Cost Outside Camera View
            EditorGUILayout.HelpBox(
                "💡 Does light take computation if it's not in camera view?\n\n" +
                "• Directional Light: ALWAYS COMPUTED. Directional lights have infinite reach; they illuminate all visible surfaces across the entire view frustum regardless of light position, and their realtime shadow cascades must be updated every frame.\n\n" +
                "• Point & Spot Lights (Shading): NO. Unity's frustum culling skips direct shading on geometry for lights whose bounding radius does not intersect the camera frustum.\n\n" +
                "• Shadow Casting (GPU): YES, if casting shadows. A point or spot light outside the camera view still renders shadow maps if its volume touches visible shadow-casting objects. Note: Realtime Point Lights render 6 cubemap faces (6 separate draw passes!).\n\n" +
                "• CPU Culling: YES (minor). Unity tests light bounding spheres against camera frustum planes and sorts per-object lights on the CPU each frame.\n\n" +
                "• Best Practice: Set static lights to 'Baked' (0 runtime computation cost) and limit realtime shadow casters to 1.",
                MessageType.Info);

            EditorGUILayout.EndVertical();

            // Category 5: Views Specific Breakdown (Non-Expandable Summary)
            if (CurrentReport.viewpointStats != null && CurrentReport.viewpointStats.Count > 0)
            {
                EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"📍 Views Specific Breakdown ({CurrentReport.viewpointStats.Count} Viewpoints)", AuditorUIStyles.CardHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("⚙️ Manage Viewpoints", EditorStyles.miniButton, GUILayout.Width(140)))
                {
                    _currentTab = AuditorTab.Settings;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(4);

                foreach (var vp in CurrentReport.viewpointStats)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"👁️ {vp.viewpointName}", EditorStyles.boldLabel);

                    Color vpCol = vp.isOverBudget ? AuditorUIStyles.ColorDanger : (vp.healthPercentage >= 75f ? AuditorUIStyles.ColorWarning : AuditorUIStyles.ColorGood);
                    AuditorUIStyles.DrawBadge(vp.statusSummary, vpCol * 0.4f, Color.white);

                    Transform targetT = _auditViewpoints.FirstOrDefault(t => t != null && t.gameObject.GetInstanceID() == vp.transformInstanceId);
                    if (targetT != null)
                    {
                        if (GUILayout.Button("👁️ Jump", EditorStyles.miniButton, GUILayout.Width(55), GUILayout.Height(18)))
                        {
                            JumpToViewpoint(targetT);
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.LabelField($"Triangles: {vp.visibleTriangles:N0}  •  Vertices: {vp.visibleVertices:N0}  •  Materials: {vp.uniqueMaterialsCount}  •  Lights: {vp.visibleLightsCount}", EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(vp.topCulpritName) && vp.topCulpritName != "None")
                    {
                        EditorGUILayout.LabelField($"Top Culprit in View: {vp.topCulpritName} ({vp.topCulpritTris:N0} tris)", AuditorUIStyles.SubtleText);
                    }
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndVertical();
            }
            else
            {
                EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("📍 Views Specific Breakdown", AuditorUIStyles.CardHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("+ Add Viewpoints in Settings", EditorStyles.miniButton, GUILayout.Width(185)))
                {
                    _currentTab = AuditorTab.Settings;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField("No vantage points configured yet. Assign empty scene GameObjects in Settings to audit camera frustum stats across key positions.", AuditorUIStyles.SubtleText);
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawCulpritsTab()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Top Culprits in Scene", AuditorUIStyles.CardHeader);
            GUILayout.FlexibleSpace();

            GUILayout.Label("Show Top:", EditorStyles.miniBoldLabel, GUILayout.Width(62));
            EditorGUI.BeginChangeCheck();
            int newLimit = EditorGUILayout.IntField(CulpritDisplayLimit, GUILayout.Width(45));
            if (EditorGUI.EndChangeCheck())
            {
                CulpritDisplayLimit = Mathf.Clamp(newLimit, 1, 1000);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
            _culpritSubFilter = GUILayout.Toolbar(_culpritSubFilter, _culpritFilters, EditorStyles.miniButton, GUILayout.Height(24));

            if (_culpritSubFilter == 4) // Views Tab
            {
                DrawViewsCulpritsTab();
                return;
            }

            EditorGUILayout.Space(4);

            // Sort Toolbar (5 buttons per tab section with ascending/descending toggle)
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Sort by:", EditorStyles.miniBoldLabel, GUILayout.Width(55));

            if (_culpritSubFilter == 3) // Lights
            {
                DrawSortButton("Severity", CulpritSortField.Severity, ref _lightSortField, ref _lightSortAscending, 75);
                DrawSortButton("Occurrences", CulpritSortField.Occurrences, ref _lightSortField, ref _lightSortAscending, 85);
                DrawSortButton("Range", CulpritSortField.Range, ref _lightSortField, ref _lightSortAscending, 65);
                DrawSortButton("Intensity", CulpritSortField.Intensity, ref _lightSortField, ref _lightSortAscending, 75);
                DrawSortButton("Shadows", CulpritSortField.Shadows, ref _lightSortField, ref _lightSortAscending, 75);
                DrawSortButton("Type", CulpritSortField.LightType, ref _lightSortField, ref _lightSortAscending, 60);

                GUILayout.FlexibleSpace();
                string dirText = _lightSortAscending ? "▲ Ascending" : "▼ Descending";
                if (GUILayout.Button(dirText, EditorStyles.miniButton, GUILayout.Width(95)))
                {
                    _lightSortAscending = !_lightSortAscending;
                }
            }
            else if (_culpritSubFilter == 2) // Heavy Textures
            {
                DrawSortButton("Severity", CulpritSortField.Severity, ref _texSortField, ref _texSortAscending, 70);
                DrawSortButton("Tex Score", CulpritSortField.TexScore, ref _texSortField, ref _texSortAscending, 75);
                DrawSortButton("Resolution", CulpritSortField.Resolution, ref _texSortField, ref _texSortAscending, 78);
                DrawSortButton("Used in Mats", CulpritSortField.Materials, ref _texSortField, ref _texSortAscending, 85);
                DrawSortButton("Name", CulpritSortField.Name, ref _texSortField, ref _texSortAscending, 60);

                GUILayout.FlexibleSpace();
                string dirText = _texSortAscending ? "▲ Ascending" : "▼ Descending";
                if (GUILayout.Button(dirText, EditorStyles.miniButton, GUILayout.Width(95)))
                {
                    _texSortAscending = !_texSortAscending;
                }
            }
            else // All Culprits (0) or Heavy Meshes (1)
            {
                DrawSortButton("Severity", CulpritSortField.Severity, ref _meshSortField, ref _meshSortAscending, 70);
                DrawSortButton("Tris", CulpritSortField.Tris, ref _meshSortField, ref _meshSortAscending, 65);
                DrawSortButton("Verts", CulpritSortField.Verts, ref _meshSortField, ref _meshSortAscending, 65);
                DrawSortButton("Occurrences", CulpritSortField.Occurrences, ref _meshSortField, ref _meshSortAscending, 85);
                DrawSortButton("Materials", CulpritSortField.Materials, ref _meshSortField, ref _meshSortAscending, 75);

                GUILayout.FlexibleSpace();
                string dirText = _meshSortAscending ? "▲ Ascending" : "▼ Descending";
                if (GUILayout.Button(dirText, EditorStyles.miniButton, GUILayout.Width(95)))
                {
                    _meshSortAscending = !_meshSortAscending;
                }
            }
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
                if (_culpritSubFilter == 3) return c.type == CulpritType.Light;
                // In All Culprits (0): only show culprits with issues / severity > 0
                return c.severityScore > 0f || c.flaggedReasons.Count > 0;
            });

            // Apply selected sort parameters
            if (_culpritSubFilter == 3) // Lights
            {
                filteredCulprits = _lightSortField switch
                {
                    CulpritSortField.Occurrences => _lightSortAscending ? filteredCulprits.OrderBy(c => c.lightOccurrences) : filteredCulprits.OrderByDescending(c => c.lightOccurrences),
                    CulpritSortField.Range => _lightSortAscending ? filteredCulprits.OrderBy(c => c.lightRange) : filteredCulprits.OrderByDescending(c => c.lightRange),
                    CulpritSortField.Intensity => _lightSortAscending ? filteredCulprits.OrderBy(c => c.lightIntensity) : filteredCulprits.OrderByDescending(c => c.lightIntensity),
                    CulpritSortField.Shadows => _lightSortAscending ? filteredCulprits.OrderBy(c => (int)c.lightShadows) : filteredCulprits.OrderByDescending(c => (int)c.lightShadows),
                    CulpritSortField.LightType => _lightSortAscending ? filteredCulprits.OrderBy(c => (int)c.lightType) : filteredCulprits.OrderByDescending(c => (int)c.lightType),
                    _ => _lightSortAscending ? filteredCulprits.OrderBy(c => c.severityScore) : filteredCulprits.OrderByDescending(c => c.severityScore)
                };
            }
            else if (_culpritSubFilter == 2) // Heavy Textures
            {
                filteredCulprits = _texSortField switch
                {
                    CulpritSortField.TexScore => _texSortAscending ? filteredCulprits.OrderBy(c => c.textureScore) : filteredCulprits.OrderByDescending(c => c.textureScore),
                    CulpritSortField.Resolution => _texSortAscending ? filteredCulprits.OrderBy(c => c.textureWidth * c.textureHeight) : filteredCulprits.OrderByDescending(c => c.textureWidth * c.textureHeight),
                    CulpritSortField.Materials => _texSortAscending ? filteredCulprits.OrderBy(c => c.dependencyNames.Count) : filteredCulprits.OrderByDescending(c => c.dependencyNames.Count),
                    CulpritSortField.Name => _texSortAscending ? filteredCulprits.OrderBy(c => c.name) : filteredCulprits.OrderByDescending(c => c.name),
                    _ => _texSortAscending ? filteredCulprits.OrderBy(c => c.severityScore) : filteredCulprits.OrderByDescending(c => c.severityScore)
                };
            }
            else // Meshes / All
            {
                filteredCulprits = _meshSortField switch
                {
                    CulpritSortField.Tris => _meshSortAscending ? filteredCulprits.OrderBy(c => c.triangleCount) : filteredCulprits.OrderByDescending(c => c.triangleCount),
                    CulpritSortField.Verts => _meshSortAscending ? filteredCulprits.OrderBy(c => c.vertexCount) : filteredCulprits.OrderByDescending(c => c.vertexCount),
                    CulpritSortField.Occurrences => _meshSortAscending ? filteredCulprits.OrderBy(c => c.fbxOccurrences) : filteredCulprits.OrderByDescending(c => c.fbxOccurrences),
                    CulpritSortField.Materials => _meshSortAscending ? filteredCulprits.OrderBy(c => c.dependencyNames.Count) : filteredCulprits.OrderByDescending(c => c.dependencyNames.Count),
                    _ => _meshSortAscending ? filteredCulprits.OrderBy(c => c.severityScore) : filteredCulprits.OrderByDescending(c => c.severityScore)
                };
            }

            var allFiltered = filteredCulprits.ToList();
            if (allFiltered.Count == 0)
            {
                EditorGUILayout.HelpBox("No culprits found matching the selected filter.", MessageType.Info);
                return;
            }

            var culpritList = allFiltered.Take(CulpritDisplayLimit).ToList();

            if (allFiltered.Count > culpritList.Count)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"Showing top {culpritList.Count} of {allFiltered.Count} items (edit 'Show Top' above to change)", AuditorUIStyles.SubtleText);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button($"Show All ({allFiltered.Count})", EditorStyles.miniButton, GUILayout.Width(95)))
                {
                    CulpritDisplayLimit = allFiltered.Count;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(2);
            }

            foreach (var culprit in culpritList)
            {
                Color badgeCol = AuditorUIStyles.ColorGood;
                if (culprit.severityScore >= 150f)
                    badgeCol = AuditorUIStyles.ColorDanger;
                else if (culprit.severityScore >= 50f)
                    badgeCol = AuditorUIStyles.ColorWarning;

                string icon = culprit.type switch
                {
                    CulpritType.MeshGeometry => "📦 ",
                    CulpritType.TextureSize => "🖼️ ",
                    CulpritType.Light => "💡 ",
                    _ => "⚠️ "
                };
                List<int> rootIds = culprit.type == CulpritType.Light ? culprit.lightInstanceIds : culprit.fbxInstanceRootIds;
                DrawCulpritCard(icon + culprit.name, culprit.primaryMetricText, culprit.secondaryDetailText, badgeCol, culprit.instanceId, culprit.dependencyNames, culprit.flaggedReasons, rootIds);
            }
        }

        private void DrawSortButton<T>(string label, T field, ref T currentField, ref bool ascending, float width) where T : Enum
        {
            bool isActive = EqualityComparer<T>.Default.Equals(currentField, field);
            string arrow = isActive ? (ascending ? " ▲" : " ▼") : "";
            GUIStyle style = isActive ? EditorStyles.miniButtonMid : EditorStyles.miniButton;

            Color oldBg = GUI.backgroundColor;
            if (isActive) GUI.backgroundColor = new Color(0.4f, 0.75f, 1f, 1f);

            if (GUILayout.Button(label + arrow, style, GUILayout.Width(width)))
            {
                if (isActive)
                {
                    ascending = !ascending;
                }
                else
                {
                    currentField = field;
                    ascending = false; // Default descending (highest impact first)
                }
            }
            GUI.backgroundColor = oldBg;
        }

        private void DrawCulpritCard(string name, string primaryBadge, string description, Color badgeColor, int instanceId, List<string> dependencies = null, List<string> reasons = null, List<int> targetInstanceIds = null)
        {
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label(name, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            AuditorUIStyles.DrawBadge(primaryBadge, badgeColor * 0.4f, Color.white);
            GUILayout.Space(6);

            if (targetInstanceIds != null && targetInstanceIds.Count > 1)
            {
                if (GUILayout.Button($"Select All ({targetInstanceIds.Count})", EditorStyles.miniButton, GUILayout.Width(95)))
                {
                    List<UnityEngine.Object> allObjs = new List<UnityEngine.Object>();
                    foreach (var id in targetInstanceIds)
                    {
                        var o = EditorUtility.InstanceIDToObject(id);
                        if (o != null && !allObjs.Contains(o)) allObjs.Add(o);
                    }
                    if (allObjs.Count > 0)
                    {
                        Selection.objects = allObjs.ToArray();
                        EditorGUIUtility.PingObject(allObjs[0]);
                    }
                }
                GUILayout.Space(4);
            }
            
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

            GUILayout.Label(description, AuditorUIStyles.SubtleText);

            if (dependencies != null && dependencies.Count > 0)
            {
                string labelPrefix = name.StartsWith("💡") ? "GameObjects: " : "Materials: ";
                string depsText = labelPrefix + string.Join(", ", dependencies.Take(4));
                if (dependencies.Count > 4) depsText += $" (+{dependencies.Count - 4} more)";
                GUILayout.Label(depsText, AuditorUIStyles.SubtleText);
            }
            
            EditorGUILayout.EndVertical();
        }

        private void DrawViewsCulpritsTab()
        {
            EditorGUILayout.Space(4);

            // Sort Toolbar for Viewpoints
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Sort by:", EditorStyles.miniBoldLabel, GUILayout.Width(55));
            DrawSortButton("Severity", ViewpointSortField.Severity, ref _viewSortField, ref _viewSortAscending, 75);
            DrawSortButton("Tris", ViewpointSortField.Tris, ref _viewSortField, ref _viewSortAscending, 55);
            DrawSortButton("Verts", ViewpointSortField.Verts, ref _viewSortField, ref _viewSortAscending, 55);
            DrawSortButton("Materials", ViewpointSortField.Materials, ref _viewSortField, ref _viewSortAscending, 75);
            DrawSortButton("Lights", ViewpointSortField.Lights, ref _viewSortField, ref _viewSortAscending, 60);
            DrawSortButton("Name", ViewpointSortField.Name, ref _viewSortField, ref _viewSortAscending, 60);

            GUILayout.FlexibleSpace();
            string dirText = _viewSortAscending ? "▲ Ascending" : "▼ Descending";
            if (GUILayout.Button(dirText, EditorStyles.miniButton, GUILayout.Width(95)))
            {
                _viewSortAscending = !_viewSortAscending;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Expand any vantage point below to inspect contributing meshes & lights, and click 'Ping' to highlight them.", AuditorUIStyles.SubtleText);
            EditorGUILayout.Space(6);

            if (CurrentReport == null || CurrentReport.viewpointStats == null || CurrentReport.viewpointStats.Count == 0)
            {
                EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
                EditorGUILayout.HelpBox("No vantage point viewpoints configured yet. Add scene transforms in Settings tab or capture your current Scene View.", MessageType.Info);
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("⚙️ Manage Viewpoints in Settings", EditorStyles.miniButton, GUILayout.Width(220), GUILayout.Height(22)))
                {
                    _currentTab = AuditorTab.Settings;
                }
                if (GUILayout.Button("📸 Capture Current Scene View", EditorStyles.miniButton, GUILayout.Width(190), GUILayout.Height(22)))
                {
                    CaptureSceneViewAsViewpoint();
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }

            var sortedViews = CurrentReport.viewpointStats.AsEnumerable();
            sortedViews = _viewSortField switch
            {
                ViewpointSortField.Tris => _viewSortAscending ? sortedViews.OrderBy(v => v.visibleTriangles) : sortedViews.OrderByDescending(v => v.visibleTriangles),
                ViewpointSortField.Verts => _viewSortAscending ? sortedViews.OrderBy(v => v.visibleVertices) : sortedViews.OrderByDescending(v => v.visibleVertices),
                ViewpointSortField.Materials => _viewSortAscending ? sortedViews.OrderBy(v => v.uniqueMaterialsCount) : sortedViews.OrderByDescending(v => v.uniqueMaterialsCount),
                ViewpointSortField.Lights => _viewSortAscending ? sortedViews.OrderBy(v => v.visibleLightsCount) : sortedViews.OrderByDescending(v => v.visibleLightsCount),
                ViewpointSortField.Name => _viewSortAscending ? sortedViews.OrderBy(v => v.viewpointName) : sortedViews.OrderByDescending(v => v.viewpointName),
                _ => _viewSortAscending ? sortedViews.OrderBy(v => v.healthPercentage) : sortedViews.OrderByDescending(v => v.healthPercentage)
            };

            foreach (var vp in sortedViews)
            {
                bool isExpanded = _expandedViewpoints.Contains(vp.transformInstanceId);
                Transform targetT = _auditViewpoints.FirstOrDefault(t => t != null && t.gameObject.GetInstanceID() == vp.transformInstanceId);

                EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);

                // Viewpoint Card Header - Line 1: Name, Badge, Actions
                EditorGUILayout.BeginHorizontal();

                string foldIcon = isExpanded ? "▼ " : "▶ ";
                if (GUILayout.Button(new GUIContent(foldIcon + "👁️ " + vp.viewpointName, "Click to toggle expanded viewpoint details"), EditorStyles.boldLabel, GUILayout.ExpandWidth(true)))
                {
                    if (isExpanded) _expandedViewpoints.Remove(vp.transformInstanceId);
                    else _expandedViewpoints.Add(vp.transformInstanceId);
                }

                Color vpCol = vp.isOverBudget ? AuditorUIStyles.ColorDanger : (vp.healthPercentage >= 75f ? AuditorUIStyles.ColorWarning : AuditorUIStyles.ColorGood);
                AuditorUIStyles.DrawBadge(vp.statusSummary, vpCol * 0.4f, Color.white);
                GUILayout.Space(8);

                GUI.enabled = targetT != null;
                GUIContent jumpContent = new GUIContent("👁️ Jump", "Align Scene View and Game Camera exactly to this viewport and select it");
                float jumpW = GUI.skin.button.CalcSize(jumpContent).x + 16f;
                if (GUILayout.Button(jumpContent, EditorStyles.miniButton, GUILayout.Width(jumpW), GUILayout.Height(19)))
                {
                    JumpToViewpoint(targetT);
                }

                GUIContent pingContent = new GUIContent("Ping View", "Select and Ping Viewpoint GameObject in Hierarchy");
                float pingW = GUI.skin.button.CalcSize(pingContent).x + 16f;
                if (GUILayout.Button(pingContent, EditorStyles.miniButton, GUILayout.Width(pingW), GUILayout.Height(19)))
                {
                    if (targetT != null)
                    {
                        Selection.activeGameObject = targetT.gameObject;
                        EditorGUIUtility.PingObject(targetT.gameObject);
                    }
                }
                GUI.enabled = true;

                EditorGUILayout.EndHorizontal();

                // Line 2: Stats summary (Full width to avoid any text clipping)
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(16);
                GUILayout.Label($"Stats: {vp.visibleTriangles:N0} Tris  •  {vp.visibleVertices:N0} Verts  •  {vp.uniqueMaterialsCount} Unique Mats  •  {vp.visibleLightsCount} Lights", AuditorUIStyles.SubtleText, GUILayout.ExpandWidth(true));
                EditorGUILayout.EndHorizontal();

                // Expanded Section: Contributing Assets
                if (isExpanded)
                {
                    EditorGUILayout.Space(4);

                    // Coordinate and Action Strip
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"Coordinates: ({vp.position.x:F1}, {vp.position.y:F1}, {vp.position.z:F1}) | Rotation: (Pitch {vp.rotationEuler.x:F0}°, Yaw {vp.rotationEuler.y:F0}°)", AuditorUIStyles.SubtleText, GUILayout.ExpandWidth(true));
                    GUILayout.FlexibleSpace();

                    int totalObjs = vp.contributingRenderers.Count + vp.contributingLights.Count;
                    GUIContent pingAllContent = new GUIContent($"Ping All Objects ({totalObjs})", "Select and Ping all GameObjects visible in this viewport");
                    float pingAllW = GUI.skin.button.CalcSize(pingAllContent).x + 16f;
                    if (GUILayout.Button(pingAllContent, EditorStyles.miniButton, GUILayout.Width(pingAllW), GUILayout.Height(19)))
                    {
                        List<UnityEngine.Object> allObjs = new List<UnityEngine.Object>();
                        foreach (var r in vp.contributingRenderers)
                        {
                            var o = EditorUtility.InstanceIDToObject(r.instanceId);
                            if (o != null) allObjs.Add(o);
                        }
                        foreach (var l in vp.contributingLights)
                        {
                            var o = EditorUtility.InstanceIDToObject(l.instanceId);
                            if (o != null) allObjs.Add(o);
                        }
                        if (allObjs.Count > 0)
                        {
                            Selection.objects = allObjs.ToArray();
                            EditorGUIUtility.PingObject(allObjs[0]);
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();

                    EditorGUILayout.Space(4);

                    // Group contributing renderers by Model / FBX
                    var fbxGroups = GroupContributingRenderers(vp.contributingRenderers);

                    // Extract all unique materials across this viewpoint
                    var vpMaterials = ExtractUniqueMaterials(vp.contributingRenderers);

                    // Extract all unique textures across this viewpoint
                    var vpTextures = ExtractUniqueTextures(vp.contributingRenderers);

                    // 1. FBX MODELS & MESHES SECTION
                    DrawViewpointFbxSection(vp.transformInstanceId, fbxGroups, vp.visibleTriangles);

                    EditorGUILayout.Space(4);

                    // 2. MATERIALS SECTION
                    DrawViewpointMaterialsSection(vp.transformInstanceId, vpMaterials);

                    EditorGUILayout.Space(4);

                    // 3. TEXTURES SECTION
                    DrawViewpointTexturesSection(vp.transformInstanceId, vpTextures);

                    // 4. LIGHTS SECTION (if any)
                    if (vp.contributingLights != null && vp.contributingLights.Count > 0)
                    {
                        EditorGUILayout.Space(4);
                        DrawViewpointLightsSection(vp.transformInstanceId, vp.contributingLights);
                    }
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }
        }

        private class ViewpointFbxGroup
        {
            public string Key;
            public string ModelName;
            public bool IsModelAsset;
            public string FbxAssetPath;
            public int Instances;
            public int TotalTris;
            public int TotalVerts;
            public List<int> InstanceIds = new List<int>();
            public List<int> MaterialIds = new List<int>();
            public List<string> MaterialNames = new List<string>();
            public List<int> TextureIds = new List<int>();
            public List<string> TextureNames = new List<string>();
        }

        private struct ViewpointAssetEntry
        {
            public int InstanceId;
            public string Name;
        }

        private List<ViewpointFbxGroup> GroupContributingRenderers(List<ViewpointContributingObject> renderers)
        {
            var list = new List<ViewpointFbxGroup>();
            if (renderers == null || renderers.Count == 0) return list;

            var groups = renderers.GroupBy(r => !string.IsNullOrEmpty(r.fbxAssetPath) 
                ? r.fbxAssetPath 
                : (!string.IsNullOrEmpty(r.fbxName) ? r.fbxName : r.name));

            foreach (var g in groups)
            {
                var first = g.First();
                string modelName = !string.IsNullOrEmpty(first.fbxName) ? first.fbxName : first.name;
                var grp = new ViewpointFbxGroup
                {
                    Key = g.Key,
                    ModelName = modelName,
                    IsModelAsset = first.isModelAsset,
                    FbxAssetPath = first.fbxAssetPath,
                    Instances = g.Count(),
                    TotalTris = g.Sum(r => r.triangleCount),
                    TotalVerts = g.Sum(r => r.vertexCount),
                    InstanceIds = g.Select(r => r.instanceId).Distinct().ToList()
                };

                HashSet<int> seenMat = new HashSet<int>();
                HashSet<int> seenTex = new HashSet<int>();

                foreach (var r in g)
                {
                    for (int m = 0; m < r.materialInstanceIds.Count; m++)
                    {
                        int mid = r.materialInstanceIds[m];
                        if (!seenMat.Contains(mid))
                        {
                            seenMat.Add(mid);
                            grp.MaterialIds.Add(mid);
                            grp.MaterialNames.Add(m < r.materialNames.Count ? r.materialNames[m] : "Material");
                        }
                    }

                    for (int t = 0; t < r.textureInstanceIds.Count; t++)
                    {
                        int tid = r.textureInstanceIds[t];
                        if (!seenTex.Contains(tid))
                        {
                            seenTex.Add(tid);
                            grp.TextureIds.Add(tid);
                            grp.TextureNames.Add(t < r.textureNames.Count ? r.textureNames[t] : "Texture");
                        }
                    }
                }

                list.Add(grp);
            }

            list.Sort((a, b) => b.TotalTris.CompareTo(a.TotalTris));
            return list;
        }

        private List<ViewpointAssetEntry> ExtractUniqueMaterials(List<ViewpointContributingObject> renderers)
        {
            var list = new List<ViewpointAssetEntry>();
            if (renderers == null) return list;

            HashSet<int> seen = new HashSet<int>();
            foreach (var r in renderers)
            {
                for (int m = 0; m < r.materialInstanceIds.Count; m++)
                {
                    int mid = r.materialInstanceIds[m];
                    if (!seen.Contains(mid))
                    {
                        seen.Add(mid);
                        list.Add(new ViewpointAssetEntry
                        {
                            InstanceId = mid,
                            Name = m < r.materialNames.Count ? r.materialNames[m] : "Material"
                        });
                    }
                }
            }
            return list;
        }

        private List<ViewpointAssetEntry> ExtractUniqueTextures(List<ViewpointContributingObject> renderers)
        {
            var list = new List<ViewpointAssetEntry>();
            if (renderers == null) return list;

            HashSet<int> seen = new HashSet<int>();
            foreach (var r in renderers)
            {
                for (int t = 0; t < r.textureInstanceIds.Count; t++)
                {
                    int tid = r.textureInstanceIds[t];
                    if (!seen.Contains(tid))
                    {
                        seen.Add(tid);
                        list.Add(new ViewpointAssetEntry
                        {
                            InstanceId = tid,
                            Name = t < r.textureNames.Count ? r.textureNames[t] : "Texture"
                        });
                    }
                }
            }
            return list;
        }

        private void DrawViewpointFbxSection(int vpId, List<ViewpointFbxGroup> fbxGroups, int totalTris)
        {
            if (fbxGroups == null || fbxGroups.Count == 0)
            {
                EditorGUILayout.LabelField("No 3D meshes or FBX models in this view.", AuditorUIStyles.SubtleText);
                return;
            }

            if (fbxGroups.Count == 1)
            {
                // Only 1 model -> BUTTONS ONLY!
                var grp = fbxGroups[0];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                string tag = grp.IsModelAsset ? "📦 FBX Model:" : "🔷 Mesh:";
                string instText = grp.Instances > 1 ? $" ({grp.Instances} instances in view)" : "";
                GUILayout.Label($"{tag} {grp.ModelName}{instText} • {grp.TotalTris:N0} Tris", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));

                if (grp.Instances > 1)
                {
                    DrawSelectAllButton($"Select All ({grp.Instances})", grp.InstanceIds);
                }
                else
                {
                    DrawPingButton("Ping Object", grp.InstanceIds[0]);
                }

                if (grp.IsModelAsset && !string.IsNullOrEmpty(grp.FbxAssetPath))
                {
                    DrawPingAssetButton("Ping FBX", grp.FbxAssetPath);
                }
                EditorGUILayout.EndHorizontal();

                DrawModelMaterials(vpId, grp.Key, grp.MaterialIds, grp.MaterialNames);
                DrawModelTextures(vpId, grp.Key, grp.TextureIds, grp.TextureNames);

                EditorGUILayout.EndVertical();
            }
            else
            {
                // More than 1 model -> COLLAPSIBLE LIST!
                string secKey = $"{vpId}_fbx";
                bool exp = DrawSectionHeader($"📦 FBX Models & Meshes ({fbxGroups.Count} Models in View • {totalTris:N0} Tris)", secKey);
                if (exp)
                {
                    foreach (var grp in fbxGroups)
                    {
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        EditorGUILayout.BeginHorizontal();
                        string tag = grp.IsModelAsset ? "📦 " : "🔷 ";
                        string instText = grp.Instances > 1 ? $" ({grp.Instances} instances)" : "";
                        GUILayout.Label($"{tag}{grp.ModelName}{instText} • {grp.TotalTris:N0} Tris", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));

                        if (grp.Instances > 1)
                        {
                            DrawSelectAllButton($"Select All ({grp.Instances})", grp.InstanceIds);
                        }
                        else
                        {
                            DrawPingButton("Ping Object", grp.InstanceIds[0]);
                        }

                        if (grp.IsModelAsset && !string.IsNullOrEmpty(grp.FbxAssetPath))
                        {
                            DrawPingAssetButton("Ping FBX", grp.FbxAssetPath);
                        }
                        EditorGUILayout.EndHorizontal();

                        DrawModelMaterials(vpId, grp.Key, grp.MaterialIds, grp.MaterialNames);
                        DrawModelTextures(vpId, grp.Key, grp.TextureIds, grp.TextureNames);

                        EditorGUILayout.EndVertical();
                        EditorGUILayout.Space(2);
                    }
                }
            }
        }

        private void DrawModelMaterials(int vpId, string modelKey, List<int> matIds, List<string> matNames)
        {
            if (matIds == null || matIds.Count == 0) return;

            if (matIds.Count == 1)
            {
                // Only 1 material -> BUTTON ONLY!
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(20);
                string mName = matNames.Count > 0 ? matNames[0] : "Material";
                GUILayout.Label($"🎨 Material: {mName}", EditorStyles.label, GUILayout.ExpandWidth(true));
                DrawPingButton("Ping Material", matIds[0], $"Ping Material '{mName}' in Project");
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                // More than 1 material -> COLLAPSIBLE LIST!
                string secKey = $"{vpId}_{modelKey}_mats";
                bool exp = DrawSubSectionHeader($"🎨 Materials ({matIds.Count})", secKey);
                if (exp)
                {
                    for (int i = 0; i < matIds.Count; i++)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(36);
                        string mName = i < matNames.Count ? matNames[i] : "Material";
                        GUILayout.Label($"🎨 {mName}", EditorStyles.label, GUILayout.ExpandWidth(true));
                        DrawPingButton("Ping Material", matIds[i], $"Ping Material '{mName}' in Project");
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
        }

        private void DrawModelTextures(int vpId, string modelKey, List<int> texIds, List<string> texNames)
        {
            if (texIds == null || texIds.Count == 0) return;

            if (texIds.Count == 1)
            {
                // Only 1 texture -> BUTTON ONLY!
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(20);
                string tName = texNames.Count > 0 ? texNames[0] : "Texture";
                GUILayout.Label($"🖼️ Texture: {tName}", EditorStyles.label, GUILayout.ExpandWidth(true));
                DrawPingButton("Ping Texture", texIds[0], $"Ping Texture '{tName}' in Project");
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                // More than 1 texture -> COLLAPSIBLE LIST!
                string secKey = $"{vpId}_{modelKey}_texs";
                bool exp = DrawSubSectionHeader($"🖼️ Textures ({texIds.Count})", secKey);
                if (exp)
                {
                    for (int i = 0; i < texIds.Count; i++)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(36);
                        string tName = i < texNames.Count ? texNames[i] : "Texture";
                        GUILayout.Label($"🖼️ {tName}", EditorStyles.label, GUILayout.ExpandWidth(true));
                        DrawPingButton("Ping Texture", texIds[i], $"Ping Texture '{tName}' in Project");
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
        }

        private void DrawViewpointMaterialsSection(int vpId, List<ViewpointAssetEntry> materials)
        {
            if (materials == null || materials.Count == 0) return;

            if (materials.Count == 1)
            {
                // Only 1 material -> BUTTON ONLY!
                var mat = materials[0];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label($"🎨 Unique Material: {mat.Name}", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
                DrawPingButton("Ping Material", mat.InstanceId, $"Ping Material '{mat.Name}' in Project");
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            else
            {
                // More than 1 material -> COLLAPSIBLE LIST!
                string secKey = $"{vpId}_all_mats";
                bool exp = DrawSectionHeader($"🎨 Unique Materials ({materials.Count} in View)", secKey);
                if (exp)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    for (int i = 0; i < materials.Count; i++)
                    {
                        var mat = materials[i];
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(12);
                        GUILayout.Label($"{i + 1}.  🎨 {mat.Name}", EditorStyles.label, GUILayout.ExpandWidth(true));
                        DrawPingButton("Ping Material", mat.InstanceId, $"Ping Material '{mat.Name}' in Project");
                        EditorGUILayout.EndHorizontal();
                        if (i < materials.Count - 1) EditorGUILayout.Space(1);
                    }
                    EditorGUILayout.EndVertical();
                }
            }
        }

        private void DrawViewpointTexturesSection(int vpId, List<ViewpointAssetEntry> textures)
        {
            if (textures == null || textures.Count == 0) return;

            if (textures.Count == 1)
            {
                // Only 1 texture -> BUTTON ONLY!
                var tex = textures[0];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label($"🖼️ Unique Texture: {tex.Name}", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
                DrawPingButton("Ping Texture", tex.InstanceId, $"Ping Texture '{tex.Name}' in Project");
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            else
            {
                // More than 1 texture -> COLLAPSIBLE LIST!
                string secKey = $"{vpId}_all_texs";
                bool exp = DrawSectionHeader($"🖼️ Unique Textures ({textures.Count} in View)", secKey);
                if (exp)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    for (int i = 0; i < textures.Count; i++)
                    {
                        var tex = textures[i];
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(12);
                        GUILayout.Label($"{i + 1}.  🖼️ {tex.Name}", EditorStyles.label, GUILayout.ExpandWidth(true));
                        DrawPingButton("Ping Texture", tex.InstanceId, $"Ping Texture '{tex.Name}' in Project");
                        EditorGUILayout.EndHorizontal();
                        if (i < textures.Count - 1) EditorGUILayout.Space(1);
                    }
                    EditorGUILayout.EndVertical();
                }
            }
        }

        private void DrawViewpointLightsSection(int vpId, List<ViewpointContributingLight> lights)
        {
            if (lights == null || lights.Count == 0) return;

            if (lights.Count == 1)
            {
                // Only 1 light -> BUTTON ONLY!
                var l = lights[0];
                string mode = l.isBaked ? "Baked" : (l.isMixed ? "Mixed" : "Realtime");
                string shadow = l.shadows != LightShadows.None ? $"{l.shadows} Shadows" : "No Shadows";

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label($"💡 Light: {l.name} • {l.type} • {mode} • {shadow}", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
                DrawPingButton("Ping Light", l.instanceId, $"Ping Light '{l.name}' in Hierarchy");
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            else
            {
                // More than 1 light -> COLLAPSIBLE LIST!
                string secKey = $"{vpId}_lights";
                bool exp = DrawSectionHeader($"💡 Visible Lights ({lights.Count} in View)", secKey);
                if (exp)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    for (int i = 0; i < lights.Count; i++)
                    {
                        var l = lights[i];
                        string mode = l.isBaked ? "Baked" : (l.isMixed ? "Mixed" : "Realtime");
                        string shadow = l.shadows != LightShadows.None ? $"{l.shadows}" : "None";

                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(12);
                        GUILayout.Label($"{i + 1}.  💡 {l.name} • {l.type} • {mode} • Range {l.range:F1}m • Shadows: {shadow}", EditorStyles.label, GUILayout.ExpandWidth(true));
                        DrawPingButton("Ping Light", l.instanceId, $"Ping Light '{l.name}' in Hierarchy");
                        EditorGUILayout.EndHorizontal();
                        if (i < lights.Count - 1) EditorGUILayout.Space(1);
                    }
                    EditorGUILayout.EndVertical();
                }
            }
        }

        private bool DrawSectionHeader(string text, string key)
        {
            bool isExp = _expandedSections.Contains(key);
            string icon = isExp ? "▼ " : "▶ ";
            GUIContent content = new GUIContent(icon + text, "Click to toggle expanded list");

            EditorGUILayout.BeginHorizontal();
            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = isExp ? new Color(0.35f, 0.75f, 1f, 1f) : oldBg;
            if (GUILayout.Button(content, EditorStyles.miniButtonMid, GUILayout.ExpandWidth(true), GUILayout.Height(22)))
            {
                if (isExp) _expandedSections.Remove(key);
                else _expandedSections.Add(key);
            }
            GUI.backgroundColor = oldBg;
            EditorGUILayout.EndHorizontal();

            return isExp;
        }

        private bool DrawSubSectionHeader(string text, string key)
        {
            bool isExp = _expandedSections.Contains(key);
            string icon = isExp ? "▼ " : "▶ ";
            GUIContent content = new GUIContent(icon + text, "Click to toggle list");

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(20);
            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = isExp ? new Color(0.85f, 0.9f, 1f, 0.8f) : oldBg;
            if (GUILayout.Button(content, EditorStyles.miniButton, GUILayout.ExpandWidth(true), GUILayout.Height(19)))
            {
                if (isExp) _expandedSections.Remove(key);
                else _expandedSections.Add(key);
            }
            GUI.backgroundColor = oldBg;
            EditorGUILayout.EndHorizontal();

            return isExp;
        }

        private bool DrawPingButton(string label, int instanceId, string tooltip = null)
        {
            GUIContent content = new GUIContent(label, tooltip ?? $"Select and Ping in Hierarchy / Project");
            float width = GUI.skin.button.CalcSize(content).x + 16f;
            if (GUILayout.Button(content, EditorStyles.miniButton, GUILayout.Width(width), GUILayout.Height(18)))
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj != null)
                {
                    Selection.activeObject = obj;
                    EditorGUIUtility.PingObject(obj);
                    return true;
                }
            }
            return false;
        }

        private bool DrawPingAssetButton(string label, string assetPath, string tooltip = null)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            GUIContent content = new GUIContent(label, tooltip ?? $"Select and Ping asset in Project Assets");
            float width = GUI.skin.button.CalcSize(content).x + 16f;
            if (GUILayout.Button(content, EditorStyles.miniButton, GUILayout.Width(width), GUILayout.Height(18)))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset != null)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                    return true;
                }
            }
            return false;
        }

        private bool DrawSelectAllButton(string label, List<int> instanceIds)
        {
            if (instanceIds == null || instanceIds.Count == 0) return false;
            GUIContent content = new GUIContent(label, $"Select all {instanceIds.Count} instances in Scene");
            float width = GUI.skin.button.CalcSize(content).x + 16f;
            if (GUILayout.Button(content, EditorStyles.miniButton, GUILayout.Width(width), GUILayout.Height(18)))
            {
                List<UnityEngine.Object> allObjs = new List<UnityEngine.Object>();
                foreach (var id in instanceIds)
                {
                    var o = EditorUtility.InstanceIDToObject(id);
                    if (o != null) allObjs.Add(o);
                }
                if (allObjs.Count > 0)
                {
                    Selection.objects = allObjs.ToArray();
                    EditorGUIUtility.PingObject(allObjs[0]);
                    return true;
                }
            }
            return false;
        }

        private void DrawAtlasingSuggestionsTab()
        {
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField("🧩 Smart Atlasing & Material Optimizer", AuditorUIStyles.CardHeader);
            EditorGUILayout.LabelField("Identifies materials and textures sharing identical shaders that can be combined into a texture atlas to eliminate draw calls and state switches.", AuditorUIStyles.SubtleText);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);

            if (CurrentReport == null)
            {
                EditorGUILayout.HelpBox("No audit data available yet. Click '⚡ Scan View' or '🌐 Scan All' to analyze materials.", MessageType.Info);
                return;
            }

            var suggestions = CurrentReport.atlasSuggestions;
            if (suggestions == null || suggestions.Count == 0)
            {
                EditorGUILayout.HelpBox("No atlasing opportunities detected. All materials in the scene either already share materials or use distinct shaders with no merge candidates.", MessageType.Info);
                return;
            }

            // Filter by Scope
            bool isViewFrustumScope = (_atlasScope == AtlasScopeFilter.CurrentView);

            // Compute groups and candidates matching scope
            var scopedGroups = new List<(AtlasGroupSuggestion group, List<AtlasCandidateItem> candidates, int potentialSavings)>();
            foreach (var g in suggestions)
            {
                var validCandidates = isViewFrustumScope
                    ? g.candidates.Where(c => c.isVisibleInView).ToList()
                    : g.candidates;

                if (validCandidates.Count > 0)
                {
                    int savings = validCandidates.Count > 1 ? (validCandidates.Count - 1) : 0;
                    scopedGroups.Add((g, validCandidates, savings));
                }
            }

            int totalEligibleMaterials = scopedGroups.Sum(s => s.candidates.Count);
            int totalPotentialSavings = scopedGroups.Sum(s => s.potentialSavings);

            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            
            // Row 1: Optimization Potential Badges
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Optimization Potential:", EditorStyles.boldLabel, GUILayout.Width(150));
            AuditorUIStyles.DrawBadge($"{scopedGroups.Count} Atlas Groups", AuditorUIStyles.ColorCardBg, Color.white);
            GUILayout.Space(6);
            AuditorUIStyles.DrawBadge($"{totalEligibleMaterials} Materials", AuditorUIStyles.ColorCardBg, Color.white);
            GUILayout.Space(6);
            string savingsText = isViewFrustumScope ? $"Save {totalPotentialSavings} in View" : $"Save up to {totalPotentialSavings} Draw Calls";
            AuditorUIStyles.DrawBadge(savingsText, AuditorUIStyles.ColorGood * 0.4f, Color.white);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            // Row 2: Scope Selector (Prominent Toggle Buttons)
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Auditing Scope:", EditorStyles.boldLabel, GUILayout.Width(110));
            string[] scopeOptions = { "Entire Scene", "Current View" };
            EditorGUI.BeginChangeCheck();
            _atlasScope = (AtlasScopeFilter)GUILayout.Toolbar((int)_atlasScope, scopeOptions, EditorStyles.miniButton, GUILayout.Width(220), GUILayout.Height(22));
            if (EditorGUI.EndChangeCheck())
            {
                Repaint();
            }
            GUILayout.Space(10);
            string scopeDescription = isViewFrustumScope 
                ? "Analyzing objects currently visible in Scene camera frustum" 
                : "Analyzing all objects across the entire scene";
            GUILayout.Label(scopeDescription, AuditorUIStyles.SubtleText);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            // Row 3: Sort Atlas Groups
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Sort Groups:", EditorStyles.boldLabel, GUILayout.Width(90));
            DrawSortButton("Materials", AtlasGroupSortField.Materials, ref _atlasGroupSortField, ref _atlasGroupSortAscending, 85);
            DrawSortButton("Savings", AtlasGroupSortField.Savings, ref _atlasGroupSortField, ref _atlasGroupSortAscending, 75);
            DrawSortButton("Tex Score", AtlasGroupSortField.TextureScore, ref _atlasGroupSortField, ref _atlasGroupSortAscending, 80);
            DrawSortButton("Shader Name", AtlasGroupSortField.ShaderName, ref _atlasGroupSortField, ref _atlasGroupSortAscending, 95);

            GUILayout.FlexibleSpace();
            string groupDirText = _atlasGroupSortAscending ? "▲ Ascending" : "▼ Descending";
            if (GUILayout.Button(groupDirText, EditorStyles.miniButton, GUILayout.Width(95)))
            {
                _atlasGroupSortAscending = !_atlasGroupSortAscending;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);

            if (scopedGroups.Count == 0)
            {
                EditorGUILayout.HelpBox(isViewFrustumScope 
                    ? "No candidate materials from multi-material shaders are visible in the current camera view. Pan the camera or switch scope back to 'Entire Scene'." 
                    : "No matching atlas groups found.", MessageType.Info);
                return;
            }

            // Apply selected group sort parameters
            scopedGroups = _atlasGroupSortField switch
            {
                AtlasGroupSortField.Materials => _atlasGroupSortAscending 
                    ? scopedGroups.OrderBy(s => s.candidates.Count).ToList() 
                    : scopedGroups.OrderByDescending(s => s.candidates.Count).ToList(),
                AtlasGroupSortField.Savings => _atlasGroupSortAscending 
                    ? scopedGroups.OrderBy(s => s.potentialSavings).ToList() 
                    : scopedGroups.OrderByDescending(s => s.potentialSavings).ToList(),
                AtlasGroupSortField.TextureScore => _atlasGroupSortAscending 
                    ? scopedGroups.OrderBy(s => s.group.currentCombinedTextureScore).ToList() 
                    : scopedGroups.OrderByDescending(s => s.group.currentCombinedTextureScore).ToList(),
                AtlasGroupSortField.ShaderName => _atlasGroupSortAscending 
                    ? scopedGroups.OrderBy(s => s.group.shaderName).ToList() 
                    : scopedGroups.OrderByDescending(s => s.group.shaderName).ToList(),
                _ => scopedGroups
            };

            // Render Atlas Groups
            foreach (var (group, candidates, groupSavings) in scopedGroups)
            {
                string groupKey = group.shaderName;
                bool isCollapsed = _collapsedAtlasGroups.Contains(groupKey);

                EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);

                // Group Header
                EditorGUILayout.BeginHorizontal();

                string foldoutIcon = isCollapsed ? "▶ " : "▼ ";
                string groupHeaderTitle = isViewFrustumScope
                    ? $"{foldoutIcon}{SceneScanEngine.GetSimpleShaderName(group.shaderName)} ({candidates.Count} in View)"
                    : $"{foldoutIcon}{group.groupTitle}";

                if (GUILayout.Button(groupHeaderTitle, EditorStyles.boldLabel))
                {
                    if (isCollapsed) _collapsedAtlasGroups.Remove(groupKey);
                    else _collapsedAtlasGroups.Add(groupKey);
                }

                GUILayout.FlexibleSpace();

                // Badges
                AuditorUIStyles.DrawBadge($"{candidates.Count} Mats", AuditorUIStyles.ColorCardBg, Color.white);
                GUILayout.Space(4);
                if (groupSavings > 0)
                {
                    AuditorUIStyles.DrawBadge($"Save {groupSavings} Calls", AuditorUIStyles.ColorGood * 0.4f, Color.white);
                }
                else
                {
                    AuditorUIStyles.DrawBadge("1 Mat in View", AuditorUIStyles.ColorCardBg, Color.white);
                }
                GUILayout.Space(4);
                AuditorUIStyles.DrawBadge($"Atlas: {group.suggestedAtlasSize}", AuditorUIStyles.ColorWarning * 0.35f, Color.white);

                GUILayout.Space(8);

                // Select All Objects Button
                if (GUILayout.Button(isViewFrustumScope ? "Select View Objects" : "Select All Objects", EditorStyles.miniButton, GUILayout.Width(130)))
                {
                    List<UnityEngine.Object> objs = new List<UnityEngine.Object>();
                    foreach (var cand in candidates)
                    {
                        var ids = isViewFrustumScope ? cand.visibleRendererInstanceIds : cand.rendererInstanceIds;
                        foreach (var rId in ids)
                        {
                            var go = EditorUtility.InstanceIDToObject(rId);
                            if (go != null && !objs.Contains(go)) objs.Add(go);
                        }
                    }
                    if (objs.Count > 0)
                    {
                        Selection.objects = objs.ToArray();
                    }
                }

                EditorGUILayout.EndHorizontal();

                // Sub-header details
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label($"Shader: {group.shaderName}", AuditorUIStyles.SubtleText);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"Combined Tex Score: {group.currentCombinedTextureScore:F1} pts", AuditorUIStyles.SubtleText);
                EditorGUILayout.EndHorizontal();

                // Detailed Candidate Items (if unfolded)
                if (!isCollapsed)
                {
                    EditorGUILayout.Space(4);
                    EditorGUI.indentLevel++;

                    // Row: Sort Materials within this group
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Sort Materials:", EditorStyles.miniBoldLabel, GUILayout.Width(95));
                    DrawSortButton("Objects Used", AtlasMaterialSortField.Usage, ref _atlasMaterialSort, ref _atlasMaterialSortAscending, 95);
                    DrawSortButton("Tex Score", AtlasMaterialSortField.TextureScore, ref _atlasMaterialSort, ref _atlasMaterialSortAscending, 80);
                    DrawSortButton("Tex Size", AtlasMaterialSortField.TextureSize, ref _atlasMaterialSort, ref _atlasMaterialSortAscending, 75);
                    DrawSortButton("Name", AtlasMaterialSortField.MaterialName, ref _atlasMaterialSort, ref _atlasMaterialSortAscending, 65);

                    GUILayout.FlexibleSpace();
                    string matDirText = _atlasMaterialSortAscending ? "▲ Ascending" : "▼ Descending";
                    if (GUILayout.Button(matDirText, EditorStyles.miniButton, GUILayout.Width(95)))
                    {
                        _atlasMaterialSortAscending = !_atlasMaterialSortAscending;
                    }
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.Space(2);

                    var sortedCandidates = _atlasMaterialSort switch
                    {
                        AtlasMaterialSortField.Usage => _atlasMaterialSortAscending
                            ? candidates.OrderBy(c => isViewFrustumScope ? c.visibleRendererUsageCount : c.rendererUsageCount).ToList()
                            : candidates.OrderByDescending(c => isViewFrustumScope ? c.visibleRendererUsageCount : c.rendererUsageCount).ToList(),
                        AtlasMaterialSortField.TextureScore => _atlasMaterialSortAscending
                            ? candidates.OrderBy(c => c.textureScore).ToList()
                            : candidates.OrderByDescending(c => c.textureScore).ToList(),
                        AtlasMaterialSortField.TextureSize => _atlasMaterialSortAscending
                            ? candidates.OrderBy(c => c.width * c.height).ToList()
                            : candidates.OrderByDescending(c => c.width * c.height).ToList(),
                        AtlasMaterialSortField.MaterialName => _atlasMaterialSortAscending
                            ? candidates.OrderBy(c => c.materialName).ToList()
                            : candidates.OrderByDescending(c => c.materialName).ToList(),
                        _ => candidates
                    };

                    foreach (var cand in sortedCandidates)
                    {
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        EditorGUILayout.BeginHorizontal();

                        // Left column: Material & Texture Information
                        EditorGUILayout.BeginVertical();

                        // Line 1: Material name & usage
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Label("🎨 " + cand.materialName, EditorStyles.boldLabel);
                        
                        int usageCount = isViewFrustumScope ? cand.visibleRendererUsageCount : cand.rendererUsageCount;
                        if (usageCount == 1)
                        {
                            GUILayout.Space(4);
                            AuditorUIStyles.DrawBadge(isViewFrustumScope ? "🚨 1 in View" : "🚨 Single-Use", AuditorUIStyles.ColorWarning * 0.35f, Color.white);
                        }
                        else
                        {
                            GUILayout.Space(4);
                            string usageText = isViewFrustumScope 
                                ? $"Used by {usageCount} objects in view ({cand.rendererUsageCount} total)" 
                                : $"Used by {usageCount} objects";
                            GUILayout.Label(usageText, AuditorUIStyles.SubtleText);
                        }
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.EndHorizontal();

                        // Line 2: Texture name & dimensions (using GUILayout.Label to avoid clipping)
                        string texDetail = cand.textureInstanceId != 0
                            ? $"🖼️ Texture: {cand.textureName} ({cand.width}×{cand.height}) • Score: {cand.textureScore:F1} pts"
                            : "🖼️ Texture: (No Texture / Flat Color)";
                        GUILayout.Label(texDetail, AuditorUIStyles.SubtleText);

                        EditorGUILayout.EndVertical();

                        GUILayout.Space(8);

                        // Right column: All Action Buttons in a Single Line
                        EditorGUILayout.BeginHorizontal(GUILayout.Width(255));
                        
                        if (GUILayout.Button("Ping Mat", EditorStyles.miniButtonLeft, GUILayout.Width(72), GUILayout.Height(20)))
                        {
                            var matObj = EditorUtility.InstanceIDToObject(cand.materialInstanceId);
                            if (matObj != null)
                            {
                                EditorGUIUtility.PingObject(matObj);
                                Selection.activeObject = matObj;
                            }
                        }

                        if (cand.textureInstanceId != 0)
                        {
                            if (GUILayout.Button("Ping Tex", EditorStyles.miniButtonMid, GUILayout.Width(72), GUILayout.Height(20)))
                            {
                                var texObj = EditorUtility.InstanceIDToObject(cand.textureInstanceId);
                                if (texObj != null)
                                {
                                    EditorGUIUtility.PingObject(texObj);
                                    Selection.activeObject = texObj;
                                }
                            }
                        }
                        else
                        {
                            GUI.enabled = false;
                            GUILayout.Button("No Tex", EditorStyles.miniButtonMid, GUILayout.Width(72), GUILayout.Height(20));
                            GUI.enabled = true;
                        }

                        if (GUILayout.Button("Ping Objects", EditorStyles.miniButtonRight, GUILayout.Width(96), GUILayout.Height(20)))
                        {
                            List<UnityEngine.Object> rendObjs = new List<UnityEngine.Object>();
                            var ids = isViewFrustumScope ? cand.visibleRendererInstanceIds : cand.rendererInstanceIds;
                            foreach (var rId in ids)
                            {
                                var rObj = EditorUtility.InstanceIDToObject(rId);
                                if (rObj != null) rendObjs.Add(rObj);
                            }
                            if (rendObjs.Count > 0)
                            {
                                Selection.objects = rendObjs.ToArray();
                                EditorGUIUtility.PingObject(rendObjs[0]);
                            }
                        }

                        EditorGUILayout.EndHorizontal();

                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.EndVertical();
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "💡 Pro-Tip for Level Designers:\n" +
                "1. Meshes sharing an identical shader can have their textures combined into a single atlas texture.\n" +
                "2. After baking/packing the atlas, create 1 shared Material and assign it to all candidate meshes.\n" +
                "3. This enables Unity's SRP Batcher or GPU Instancing to draw all those objects in a single batch!", 
                MessageType.Info);
        }

        private void DrawSettingsTab()
        {
            EditorGUILayout.BeginVertical(AuditorUIStyles.CardBox);
            EditorGUILayout.LabelField("⚙️ Audit Configuration & Limits", AuditorUIStyles.CardHeader);
            EditorGUILayout.Space(6);

            UseAsyncScan = EditorGUILayout.Toggle("Async (Background Thread)", UseAsyncScan);
            ScanIntervalSeconds = EditorGUILayout.Slider("Scan Interval (sec)", ScanIntervalSeconds, 3f, 60f);
            
            EditorGUI.BeginChangeCheck();
            bool newInact = EditorGUILayout.Toggle("Include Inactive Objects", IncludeInactive);
            if (EditorGUI.EndChangeCheck())
            {
                IncludeInactive = newInact;
                TriggerScan(AutoScope == ScanScope.EntireScene);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Camera Configuration", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            CustomCamera = (Camera)EditorGUILayout.ObjectField("Target Game Camera", CustomCamera, typeof(Camera), true);
            SyncWithSceneView = EditorGUILayout.Toggle("Sync with Scene View", SyncWithSceneView);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Performance Budgets", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            EditorGUI.indentLevel++;
            _activeProfile.maxTriangles = EditorGUILayout.IntField("Max Visible Tris", _activeProfile.maxTriangles);
            _activeProfile.maxVertices = EditorGUILayout.IntField("Max Visible Verts", _activeProfile.maxVertices);
            _activeProfile.maxMaterialsInScene = EditorGUILayout.IntField("Max Materials", _activeProfile.maxMaterialsInScene);
            _activeProfile.maxTextureScore = EditorGUILayout.FloatField("Max Texture Score", _activeProfile.maxTextureScore);
            _activeProfile.maxFbxInScene = EditorGUILayout.IntField("Max Scene FBXs", _activeProfile.maxFbxInScene);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Lighting Budgets", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            _activeProfile.maxDirectionalLights = EditorGUILayout.IntField("Max Directional Lights", _activeProfile.maxDirectionalLights);
            _activeProfile.maxPointLights = EditorGUILayout.IntField("Max Point Lights", _activeProfile.maxPointLights);
            _activeProfile.maxSpotLights = EditorGUILayout.IntField("Max Spot Lights", _activeProfile.maxSpotLights);
            _activeProfile.maxTotalLights = EditorGUILayout.IntField("Max Total Lights", _activeProfile.maxTotalLights);
            _activeProfile.maxRealtimeShadowCasters = EditorGUILayout.IntField("Max Realtime Shadow Casters", _activeProfile.maxRealtimeShadowCasters);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Single Object Culprit Thresholds", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            _activeProfile.singleMeshHeavyTriangles = EditorGUILayout.IntField("Heavy Mesh Threshold (Tris)", _activeProfile.singleMeshHeavyTriangles);
            _activeProfile.maxMaterialsPerObject = EditorGUILayout.IntField("Max Materials Per Object", _activeProfile.maxMaterialsPerObject);
            _activeProfile.requireLodAboveTriangles = EditorGUILayout.IntField("Require LOD If Above (Tris)", _activeProfile.requireLodAboveTriangles);
            EditorGUI.indentLevel--;
            if (EditorGUI.EndChangeCheck())
            {
                SaveBudgetProfile();
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Auditor View Display Limits", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            int newDispLimit = EditorGUILayout.IntField("Max Culprits Displayed", CulpritDisplayLimit);
            if (EditorGUI.EndChangeCheck())
            {
                CulpritDisplayLimit = newDispLimit;
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("📍 View-Based Audit (Camera Vantage Points)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Assign empty GameObjects or camera transforms from the scene. The auditor calculates camera frustum stats (tris, verts, materials, lights) from the perspective of each viewpoint and includes a dedicated 'Views Specific Report' in the PDF export.", MessageType.None);

            EditorGUI.indentLevel++;
            if (_auditViewpoints == null) _auditViewpoints = new List<Transform>();

            for (int i = 0; i < _auditViewpoints.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"#{i + 1}", EditorStyles.miniBoldLabel, GUILayout.Width(25));
                EditorGUI.BeginChangeCheck();
                Transform prevT = _auditViewpoints[i];
                Transform newT = (Transform)EditorGUILayout.ObjectField(prevT, typeof(Transform), true);
                if (EditorGUI.EndChangeCheck())
                {
                    _auditViewpoints[i] = newT;
                    SaveViewpointPaths();
                    TriggerScan(AutoScope == ScanScope.EntireScene);
                }

                GUI.enabled = newT != null;
                if (GUILayout.Button(new GUIContent("👁️ Jump", "Fly Scene View & Camera to this viewpoint"), EditorStyles.miniButton, GUILayout.Width(55), GUILayout.Height(18)))
                {
                    JumpToViewpoint(newT);
                }
                GUI.enabled = true;

                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(24), GUILayout.Height(18)))
                {
                    _auditViewpoints.RemoveAt(i);
                    SaveViewpointPaths();
                    TriggerScan(AutoScope == ScanScope.EntireScene);
                    GUIUtility.ExitGUI();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Slot", EditorStyles.miniButton, GUILayout.Width(85)))
            {
                _auditViewpoints.Add(null);
                SaveViewpointPaths();
            }
            if (GUILayout.Button("📸 Capture Scene View", EditorStyles.miniButton, GUILayout.Width(145)))
            {
                CaptureSceneViewAsViewpoint();
            }
            if (Camera.main != null && GUILayout.Button("🎥 Add Main Cam", EditorStyles.miniButton, GUILayout.Width(110)))
            {
                if (!_auditViewpoints.Contains(Camera.main.transform))
                {
                    _auditViewpoints.Add(Camera.main.transform);
                    SaveViewpointPaths();
                    TriggerScan(AutoScope == ScanScope.EntireScene);
                }
            }
            if (_auditViewpoints.Count > 0 && GUILayout.Button("Clear All", EditorStyles.miniButton, GUILayout.Width(65)))
            {
                if (EditorUtility.DisplayDialog("Clear Viewpoints", "Remove all configured viewpoints?", "Clear", "Cancel"))
                {
                    _auditViewpoints.Clear();
                    SaveViewpointPaths();
                    TriggerScan(AutoScope == ScanScope.EntireScene);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(10);
            if (GUILayout.Button("↺ Reset Budgets to Defaults", GUILayout.Width(200), GUILayout.Height(22)))
            {
                _activeProfile = BudgetProfile.GetDefault();
                SaveBudgetProfile();
                TriggerScan(false);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawFooter()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Live Scene Auditor v1.0 • Ready", AuditorUIStyles.SubtleText);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("📄 Generate Report (.pdf)", EditorStyles.miniButton, GUILayout.Width(160)))
            {
                GenerateReport();
            }
            GUILayout.Space(8);
            if (GUILayout.Button("Documentation / Help", EditorStyles.linkLabel))
            {
                Application.OpenURL("https://docs.unity3d.com/Manual/OptimizingGraphicsPerformance.html");
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        public void GenerateReport()
        {
            string defaultName = SceneAuditReportExporter.GenerateDefaultFileName();
            string savePath = EditorUtility.SaveFilePanel("Save Scene Audit Report (PDF)", "", defaultName, "pdf");
            if (string.IsNullOrEmpty(savePath))
            {
                return; // User cancelled
            }

            EditorUtility.DisplayProgressBar("Generating Scene Audit Report", "Auditing full scene data...", 0.3f);
            try
            {
                // Always audit the entire scene (full scene, no camera frustum restriction) for the report
                List<Transform> validVps = GetValidViewpoints();
                SceneScanEngine.ScanScene(
                    frustumOnly: false,
                    profile: ActiveProfile ?? BudgetProfile.GetDefault(),
                    asyncMode: false, // Run synchronously for immediate export
                    includeInactive: IncludeInactive,
                    targetCamera: null,
                    isGameCamera: false,
                    onCompleted: (fullReport) =>
                    {
                        CurrentReport = fullReport; // Update window data with the full report
                        EditorUtility.DisplayProgressBar("Generating Scene Audit Report", "Generating formatted PDF report with tables...", 0.75f);

                        bool success = SceneAuditReportExporter.ExportPdf(
                            fullReport, 
                            ActiveProfile ?? BudgetProfile.GetDefault(), 
                            IncludeInactive,
                            savePath);

                        EditorUtility.ClearProgressBar();

                        if (success && System.IO.File.Exists(savePath))
                        {
                            bool openNow = EditorUtility.DisplayDialog(
                                "PDF Report Generated Successfully", 
                                $"Scene audit report saved with tables and rows to:\n\n{savePath}\n\nWould you like to open it now?", 
                                "Open PDF", 
                                "Show in Explorer");

                            if (openNow)
                            {
                                try
                                {
                                    System.Diagnostics.Process.Start(savePath);
                                }
                                catch
                                {
                                    EditorUtility.RevealInFinder(savePath);
                                }
                            }
                            else
                            {
                                EditorUtility.RevealInFinder(savePath);
                            }
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Report Generation Failed", "Failed to generate PDF file. Please check editor console for details.", "OK");
                        }
                    },
                    viewpoints: validVps
                );
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[Live Scene Auditor] Failed to generate report: {ex}");
                EditorUtility.DisplayDialog("Report Generation Error", $"Failed to generate report: {ex.Message}", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        public void TriggerScan(bool fullScene)
        {
            BackgroundIntervalTimer = ScanIntervalSeconds;
            IsScanning = true;

            Camera targetCam = GetTargetGameCamera();
            if (SyncWithSceneView && SceneView.lastActiveSceneView != null)
            {
                SyncCameraNow(SceneView.lastActiveSceneView);
            }

            List<Transform> validVps = GetValidViewpoints();
            SceneScanEngine.ScanScene(
                frustumOnly: !fullScene,
                profile: ActiveProfile ?? BudgetProfile.GetDefault(),
                asyncMode: UseAsyncScan,
                includeInactive: IncludeInactive,
                targetCamera: targetCam,
                isGameCamera: true,
                onCompleted: (report) =>
                {
                    CurrentReport = report;
                    IsScanning = false;
                    Repaint();
                    SceneView.RepaintAll();
                },
                viewpoints: validVps
            );
        }

        public static void RequestScan(bool fullScene)
        {
            var window = GetActiveWindow();
            if (window != null)
            {
                window.TriggerScan(fullScene);
            }
            else
            {
                IsScanning = true;
                BackgroundIntervalTimer = ScanIntervalSeconds;
                Camera targetCam = GetTargetGameCamera();
                if (SyncWithSceneView && SceneView.lastActiveSceneView != null)
                {
                    SyncCameraNow(SceneView.lastActiveSceneView);
                }

                List<Transform> validVps = GetValidViewpoints();
                SceneScanEngine.ScanScene(
                    frustumOnly: !fullScene,
                    profile: ActiveProfile ?? BudgetProfile.GetDefault(),
                    asyncMode: UseAsyncScan,
                    includeInactive: IncludeInactive,
                    targetCamera: targetCam,
                    isGameCamera: true,
                    onCompleted: (report) =>
                    {
                        CurrentReport = report;
                        IsScanning = false;
                        SceneView.RepaintAll();
                    },
                    viewpoints: validVps
                );
            }
        }

        public static void SyncCameraNow(SceneView sceneView)
        {
            if (!SyncWithSceneView || sceneView == null || sceneView.camera == null) return;

            Camera gameCam = GetTargetGameCamera();
            if (gameCam == null) return;

            Camera svCam = sceneView.camera;
            Transform camT = gameCam.transform;

            if (camT.position != svCam.transform.position || camT.rotation != svCam.transform.rotation)
            {
                camT.SetPositionAndRotation(svCam.transform.position, svCam.transform.rotation);
                SceneScanEngine.RequestGameViewRepaint();

                if (CurrentScanMode == ScanMode.OnChange)
                {
                    BackgroundChangeDebounceTimer = ChangeDebounceDuration;
                }
            }
        }

        public static Camera GetTargetGameCamera()
        {
            if (CustomCamera != null) return CustomCamera;
            if (Camera.main != null) return Camera.main;

            foreach (var c in Camera.allCameras)
            {
                if (c != null && (c.CompareTag("MainCamera") || c.cameraType == CameraType.Game))
                {
                    return c;
                }
            }
            return null;
        }

        public static LiveSceneAuditorWindow GetActiveWindow()
        {
            var windows = Resources.FindObjectsOfTypeAll<LiveSceneAuditorWindow>();
            return (windows != null && windows.Length > 0) ? windows[0] : null;
        }
    }
}
