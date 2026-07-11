using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[FilePath(
    "ProjectSettings/SceneAutoBackupSettings.asset",
    FilePathAttribute.Location.ProjectFolder)]
public class SceneAutoBackupSettings :
    ScriptableSingleton<SceneAutoBackupSettings> {

    [SerializeField] private bool backupsEnabled = true;
    [SerializeField] private bool automaticSceneSaving = true;
    [SerializeField] private string backupLocation = "";
    [SerializeField] private float backupIntervalMinutes = 5f;
    [SerializeField] private int maximumBackupsPerScene = 20;
    [SerializeField] private List<string> trackedSceneGuids = new();

    public bool BackupsEnabled {
        get => backupsEnabled;
        set {
            backupsEnabled = value;
            SaveSettings();
        }
    }

    public bool AutomaticSceneSaving {
        get => automaticSceneSaving;
        set {
            automaticSceneSaving = value;
            SaveSettings();
        }
    }

    public string BackupLocation {
        get => backupLocation;
        set {
            backupLocation = value;
            SaveSettings();
        }
    }

    public float BackupIntervalMinutes {
        get => backupIntervalMinutes;
        set {
            backupIntervalMinutes = Mathf.Max(1f, value);
            SaveSettings();
        }
    }

    public int MaximumBackupsPerScene {
        get => maximumBackupsPerScene;
        set {
            maximumBackupsPerScene = Mathf.Max(1, value);
            SaveSettings();
        }
    }

    public List<string> TrackedSceneGuids => trackedSceneGuids;

    public bool IsSceneTracked(string scenePath) {
        if (string.IsNullOrEmpty(scenePath)) {
            return false;
        }

        string guid = AssetDatabase.AssetPathToGUID(scenePath);

        return !string.IsNullOrEmpty(guid) &&
               trackedSceneGuids.Contains(guid);
    }

    public void AddScene(string scenePath) {
        if (string.IsNullOrEmpty(scenePath)) {
            return;
        }

        string guid = AssetDatabase.AssetPathToGUID(scenePath);

        if (string.IsNullOrEmpty(guid)) {
            return;
        }

        if (trackedSceneGuids.Contains(guid)) {
            return;
        }

        trackedSceneGuids.Add(guid);
        SaveSettings();
    }

    public void RemoveScene(string guid) {
        if (!trackedSceneGuids.Remove(guid)) {
            return;
        }

        SaveSettings();
    }

    public void RemoveInvalidScenes() {
        bool changed = false;

        for (int i = trackedSceneGuids.Count - 1; i >= 0; i--) {
            string path =
                AssetDatabase.GUIDToAssetPath(
                    trackedSceneGuids[i]);

            if (!string.IsNullOrEmpty(path)) {
                continue;
            }

            trackedSceneGuids.RemoveAt(i);
            changed = true;
        }

        if (changed) {
            SaveSettings();
        }
    }

    public void SaveSettings() {
        Save(true);
    }
}

[InitializeOnLoad]
public static class SceneAutoBackupManager {

    private static double nextAutomaticSaveTime;
    private static bool isProcessing;

    static SceneAutoBackupManager() {
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.update += OnEditorUpdate;

        EditorSceneManager.sceneSaved -= OnSceneSaved;
        EditorSceneManager.sceneSaved += OnSceneSaved;

        ResetAutomaticSaveTimer();
    }

    private static void OnEditorUpdate() {
        SceneAutoBackupSettings settings =
            SceneAutoBackupSettings.instance;

        if (!settings.BackupsEnabled ||
            !settings.AutomaticSceneSaving) {
            return;
        }

        if (isProcessing ||
            EditorApplication.isPlayingOrWillChangePlaymode ||
            EditorApplication.isCompiling ||
            EditorApplication.isUpdating) {
            return;
        }

        if (EditorApplication.timeSinceStartup <
            nextAutomaticSaveTime) {
            return;
        }

        ResetAutomaticSaveTimer();
        SaveDirtyTrackedScenes();
    }

    private static void SaveDirtyTrackedScenes() {
        if (!ValidateBackupLocation(false)) {
            return;
        }

        isProcessing = true;

        try {
            for (int i = 0; i < SceneManager.sceneCount; i++) {
                Scene scene = SceneManager.GetSceneAt(i);

                if (!CanProcessScene(scene)) {
                    continue;
                }

                if (!scene.isDirty) {
                    continue;
                }

                if (!SceneAutoBackupSettings.instance
                        .IsSceneTracked(scene.path)) {
                    continue;
                }

                bool saved =
                    EditorSceneManager.SaveScene(scene);

                if (!saved) {
                    Debug.LogWarning(
                        $"Scene Auto Backup: Failed to save " +
                        $"scene '{scene.name}'.");
                }
            }
        }
        catch (Exception exception) {
            Debug.LogException(exception);
        }
        finally {
            isProcessing = false;
        }
    }

    private static void OnSceneSaved(Scene scene) {
        SceneAutoBackupSettings settings =
            SceneAutoBackupSettings.instance;

        if (!settings.BackupsEnabled) {
            return;
        }

        if (!CanProcessScene(scene)) {
            return;
        }

        if (!settings.IsSceneTracked(scene.path)) {
            return;
        }

        CreateBackup(scene);
    }

    public static void CreateBackup(Scene scene) {
        if (!ValidateBackupLocation(true)) {
            return;
        }

        if (!CanProcessScene(scene)) {
            return;
        }

        string sourceScenePath =
            AssetPathToAbsolutePath(scene.path);

        if (!File.Exists(sourceScenePath)) {
            Debug.LogWarning(
                $"Scene Auto Backup: Source scene was not found:\n" +
                sourceScenePath);

            return;
        }

        try {
            SceneAutoBackupSettings settings =
                SceneAutoBackupSettings.instance;

            string sceneFolderName =
                SanitizeFileName(scene.name);

            string sceneBackupFolder =
                Path.Combine(
                    settings.BackupLocation,
                    sceneFolderName);

            Directory.CreateDirectory(sceneBackupFolder);

            string timestamp =
                DateTime.Now.ToString(
                    "yyyy-MM-dd_HH-mm-ss-fff");

            string backupFileName =
                $"{sceneFolderName}_{timestamp}.unity";

            string backupFilePath =
                Path.Combine(
                    sceneBackupFolder,
                    backupFileName);

            File.Copy(
                sourceScenePath,
                backupFilePath,
                false);

            CleanupOldBackups(sceneBackupFolder);

            Debug.Log(
                $"Scene backup created successfully:\n" +
                backupFilePath);
        }
        catch (Exception exception) {
            Debug.LogError(
                $"Scene Auto Backup: Failed to back up " +
                $"scene '{scene.name}'.");

            Debug.LogException(exception);
        }
    }

    public static void BackupAllOpenTrackedScenes() {
        if (!ValidateBackupLocation(true)) {
            return;
        }

        if (EditorApplication.isPlayingOrWillChangePlaymode) {
            EditorUtility.DisplayDialog(
                "Scene Auto Backup",
                "Scenes cannot be backed up while entering or " +
                "running Play Mode.",
                "OK");

            return;
        }

        isProcessing = true;

        try {
            for (int i = 0; i < SceneManager.sceneCount; i++) {
                Scene scene = SceneManager.GetSceneAt(i);

                if (!CanProcessScene(scene)) {
                    continue;
                }

                if (!SceneAutoBackupSettings.instance
                        .IsSceneTracked(scene.path)) {
                    continue;
                }

                if (scene.isDirty) {
                    bool saved =
                        EditorSceneManager.SaveScene(scene);

                    if (!saved) {
                        Debug.LogWarning(
                            $"Scene Auto Backup: Failed to save " +
                            $"scene '{scene.name}'.");

                        continue;
                    }
                }

                /*
                 * sceneSaved creates the backup when a dirty scene is
                 * saved. Clean scenes need to be backed up directly.
                 */
                if (!scene.isDirty) {
                    CreateBackup(scene);
                }
            }
        }
        catch (Exception exception) {
            Debug.LogException(exception);
        }
        finally {
            isProcessing = false;
        }
    }

    public static void ResetAutomaticSaveTimer() {
        float interval =
            SceneAutoBackupSettings.instance
                .BackupIntervalMinutes;

        nextAutomaticSaveTime =
            EditorApplication.timeSinceStartup +
            interval * 60d;
    }

    public static bool ValidateBackupLocation(
        bool showWarning) {

        string backupLocation =
            SceneAutoBackupSettings.instance
                .BackupLocation;

        if (string.IsNullOrWhiteSpace(backupLocation)) {
            if (showWarning) {
                EditorUtility.DisplayDialog(
                    "Scene Auto Backup",
                    "Select a backup location first.",
                    "OK");
            }

            return false;
        }

        try {
            Directory.CreateDirectory(backupLocation);
            return true;
        }
        catch (Exception exception) {
            Debug.LogError(
                $"Scene Auto Backup: Invalid backup location.\n" +
                exception.Message);

            if (showWarning) {
                EditorUtility.DisplayDialog(
                    "Invalid Backup Location",
                    exception.Message,
                    "OK");
            }

            return false;
        }
    }

    private static void CleanupOldBackups(
        string sceneBackupFolder) {

        int maximumBackups =
            SceneAutoBackupSettings.instance
                .MaximumBackupsPerScene;

        DirectoryInfo directory =
            new DirectoryInfo(sceneBackupFolder);

        FileInfo[] backupFiles =
            directory.GetFiles("*.unity");

        Array.Sort(
            backupFiles,
            (first, second) =>
                second.LastWriteTimeUtc.CompareTo(
                    first.LastWriteTimeUtc));

        for (int i = maximumBackups;
             i < backupFiles.Length;
             i++) {

            try {
                backupFiles[i].Delete();
            }
            catch (Exception exception) {
                Debug.LogWarning(
                    $"Scene Auto Backup: Could not delete old " +
                    $"backup:\n{backupFiles[i].FullName}\n" +
                    exception.Message);
            }
        }
    }

    private static bool CanProcessScene(Scene scene) {
        return scene.IsValid() &&
               scene.isLoaded &&
               !string.IsNullOrEmpty(scene.path);
    }

    private static string AssetPathToAbsolutePath(
        string assetPath) {

        string projectRoot =
            Directory.GetParent(
                Application.dataPath)?.FullName;

        if (string.IsNullOrEmpty(projectRoot)) {
            throw new InvalidOperationException(
                "Could not determine the Unity project folder.");
        }

        return Path.GetFullPath(
            Path.Combine(projectRoot, assetPath));
    }

    private static string SanitizeFileName(
        string fileName) {

        foreach (char invalidCharacter in
                 Path.GetInvalidFileNameChars()) {

            fileName =
                fileName.Replace(
                    invalidCharacter,
                    '_');
        }

        return fileName;
    }
}

public class SceneAutoBackupWindow : EditorWindow {

    private Vector2 mainScrollPosition;
    private Vector2 trackedScenesScrollPosition;
    private Vector2 backupFilesScrollPosition;

    private string selectedBackupPath = "";
    private string restoreDestinationFolderPath = "";
    private bool openSceneAfterRestore = true;

    private readonly List<string> discoveredBackupFiles = new();

    [MenuItem("Tools/Scene Auto Backup")]
    private static void OpenWindow() {
        SceneAutoBackupWindow window =
            GetWindow<SceneAutoBackupWindow>();

        window.titleContent =
            new GUIContent("Scene Backup");

        window.minSize =
            new Vector2(560f, 650f);

        window.Show();
    }

    private void OnEnable() {
        RefreshBackupFiles();
    }

    private void OnGUI() {
        SceneAutoBackupSettings settings =
            SceneAutoBackupSettings.instance;

        mainScrollPosition =
            EditorGUILayout.BeginScrollView(
                mainScrollPosition);

        EditorGUILayout.Space(8f);

        EditorGUILayout.LabelField(
            "Scene Auto Backup",
            EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "Tracked scenes are backed up whenever they are saved. " +
            "Dirty tracked scenes can also be saved automatically " +
            "at the selected interval.",
            MessageType.Info);

        EditorGUILayout.Space(5f);

        DrawGeneralSettings(settings);

        EditorGUILayout.Space(10f);

        DrawBackupLocation(settings);

        EditorGUILayout.Space(12f);

        DrawTrackedScenes(settings);

        EditorGUILayout.Space(12f);

        DrawManualBackupSection(settings);

        EditorGUILayout.Space(16f);

        DrawRestoreSection(settings);

        EditorGUILayout.Space(20f);

        EditorGUILayout.EndScrollView();
    }

    private void DrawGeneralSettings(
        SceneAutoBackupSettings settings) {

        EditorGUILayout.LabelField(
            "Settings",
            EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();

        bool backupsEnabled =
            EditorGUILayout.Toggle(
                "Enable Backups",
                settings.BackupsEnabled);

        bool automaticSaving =
            EditorGUILayout.Toggle(
                "Automatic Scene Saving",
                settings.AutomaticSceneSaving);

        float interval =
            EditorGUILayout.FloatField(
                "Save Interval (Minutes)",
                settings.BackupIntervalMinutes);

        int maximumBackups =
            EditorGUILayout.IntField(
                "Maximum Backups Per Scene",
                settings.MaximumBackupsPerScene);

        if (!EditorGUI.EndChangeCheck()) {
            return;
        }

        settings.BackupsEnabled = backupsEnabled;
        settings.AutomaticSceneSaving = automaticSaving;

        settings.BackupIntervalMinutes =
            Mathf.Max(1f, interval);

        settings.MaximumBackupsPerScene =
            Mathf.Max(1, maximumBackups);

        SceneAutoBackupManager.ResetAutomaticSaveTimer();
    }

    private void DrawBackupLocation(
        SceneAutoBackupSettings settings) {

        EditorGUILayout.LabelField(
            "Backup Location",
            EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.SelectableLabel(
            string.IsNullOrWhiteSpace(settings.BackupLocation)
                ? "No backup folder selected"
                : settings.BackupLocation,
            EditorStyles.textField,
            GUILayout.Height(
                EditorGUIUtility.singleLineHeight));

        if (GUILayout.Button(
                "Browse",
                GUILayout.Width(80f))) {

            SelectBackupLocation(settings);
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();

        using (new EditorGUI.DisabledScope(
                   string.IsNullOrWhiteSpace(
                       settings.BackupLocation))) {

            if (GUILayout.Button("Open Backup Folder")) {
                Directory.CreateDirectory(
                    settings.BackupLocation);

                EditorUtility.RevealInFinder(
                    settings.BackupLocation);
            }

            if (GUILayout.Button("Refresh Backup List")) {
                RefreshBackupFiles();
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    private void SelectBackupLocation(
        SceneAutoBackupSettings settings) {

        string projectRoot =
            Directory.GetParent(
                Application.dataPath)?.FullName;

        string startingFolder =
            Directory.Exists(settings.BackupLocation)
                ? settings.BackupLocation
                : projectRoot;

        string selectedFolder =
            EditorUtility.OpenFolderPanel(
                "Select Scene Backup Location",
                startingFolder,
                "SceneBackups");

        if (string.IsNullOrEmpty(selectedFolder)) {
            return;
        }

        settings.BackupLocation =
            Path.GetFullPath(selectedFolder);

        RefreshBackupFiles();
    }

    private void DrawTrackedScenes(
    SceneAutoBackupSettings settings) {

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField(
            "Tracked Scenes",
            EditorStyles.boldLabel);

        if (GUILayout.Button(
                "Add Open Scenes",
                GUILayout.Width(120f))) {

            AddOpenScenes(settings);
        }

        if (GUILayout.Button(
                "Remove Missing",
                GUILayout.Width(110f))) {

            settings.RemoveInvalidScenes();
        }

        EditorGUILayout.EndHorizontal();

        DrawSceneDropArea(settings);

        trackedScenesScrollPosition =
            EditorGUILayout.BeginScrollView(
                trackedScenesScrollPosition,
                GUILayout.MinHeight(120f),
                GUILayout.MaxHeight(200f));

        List<string> guids =
            settings.TrackedSceneGuids;

        for (int i = guids.Count - 1; i >= 0; i--) {
            string guid = guids[i];

            string scenePath =
                AssetDatabase.GUIDToAssetPath(guid);

            EditorGUILayout.BeginHorizontal(
                EditorStyles.helpBox);

            if (string.IsNullOrEmpty(scenePath)) {
                EditorGUILayout.LabelField(
                    "Missing or deleted scene");

                if (GUILayout.Button(
                        "Remove",
                        GUILayout.Width(70f))) {

                    settings.RemoveScene(guid);
                }
            }
            else {
                SceneAsset sceneAsset =
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(
                        scenePath);

                EditorGUILayout.ObjectField(
                    sceneAsset,
                    typeof(SceneAsset),
                    false);

                if (GUILayout.Button(
                        "Remove",
                        GUILayout.Width(70f))) {

                    settings.RemoveScene(guid);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        if (guids.Count == 0) {
            EditorGUILayout.HelpBox(
                "No scenes are currently tracked. Add open scenes " +
                "or drag scene assets into the area above.",
                MessageType.Warning);
        }

        EditorGUILayout.EndScrollView();
    }
    private static void DrawSceneDropArea(
    SceneAutoBackupSettings settings) {

        Rect dropArea =
            GUILayoutUtility.GetRect(
                0f,
                55f,
                GUILayout.ExpandWidth(true));

        GUI.Box(
            dropArea,
            "Drag and Drop Scene Assets Here",
            EditorStyles.helpBox);

        Event currentEvent = Event.current;

        if (!dropArea.Contains(
                currentEvent.mousePosition)) {
            return;
        }

        if (currentEvent.type == EventType.DragUpdated) {
            DragAndDrop.visualMode =
                ContainsValidSceneAsset(
                    DragAndDrop.objectReferences)
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;

            currentEvent.Use();
            return;
        }

        if (currentEvent.type != EventType.DragPerform) {
            return;
        }

        DragAndDrop.AcceptDrag();

        int addedCount = 0;

        foreach (UnityEngine.Object draggedObject
                 in DragAndDrop.objectReferences) {

            if (!(draggedObject is SceneAsset)) {
                continue;
            }

            string scenePath =
                AssetDatabase.GetAssetPath(
                    draggedObject);

            if (string.IsNullOrEmpty(scenePath) ||
                !scenePath.EndsWith(
                    ".unity",
                    StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            bool alreadyTracked =
                settings.IsSceneTracked(scenePath);

            settings.AddScene(scenePath);

            if (!alreadyTracked) {
                addedCount++;
            }
        }

        if (addedCount > 0) {
            Debug.Log(
                $"Scene Auto Backup: Added {addedCount} " +
                $"scene(s) using drag and drop.");
        }

        currentEvent.Use();
    }
    private static bool ContainsValidSceneAsset(
    UnityEngine.Object[] draggedObjects) {

        if (draggedObjects == null ||
            draggedObjects.Length == 0) {
            return false;
        }

        foreach (UnityEngine.Object draggedObject
                 in draggedObjects) {

            if (draggedObject is SceneAsset) {
                return true;
            }
        }

        return false;
    }

    private void DrawManualBackupSection(
        SceneAutoBackupSettings settings) {

        using (new EditorGUI.DisabledScope(
                   string.IsNullOrWhiteSpace(
                       settings.BackupLocation) ||
                   EditorApplication.isPlayingOrWillChangePlaymode)) {

            if (GUILayout.Button(
                    "Backup All Open Tracked Scenes",
                    GUILayout.Height(32f))) {

                SceneAutoBackupManager
                    .BackupAllOpenTrackedScenes();

                RefreshBackupFiles();
            }
        }
    }

    private void DrawRestoreSection(
        SceneAutoBackupSettings settings) {

        EditorGUILayout.LabelField(
            "Restore Scene From Backup",
            EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "Select a backup and choose a destination folder " +
            "inside Assets. The scene will be copied using the " +
            "exact filename stored in the backup folder.",
            MessageType.Info);

        DrawBackupFilesList();

        EditorGUILayout.Space(5f);

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.SelectableLabel(
            string.IsNullOrEmpty(selectedBackupPath)
                ? "No backup selected"
                : selectedBackupPath,
            EditorStyles.textField,
            GUILayout.Height(
                EditorGUIUtility.singleLineHeight));

        if (GUILayout.Button(
                "Browse File",
                GUILayout.Width(100f))) {

            SelectBackupFile(settings);
        }

        EditorGUILayout.EndHorizontal();

        DrawRestoreDestinationFolder();

        openSceneAfterRestore =
            EditorGUILayout.Toggle(
                "Open After Restore",
                openSceneAfterRestore);

        bool validBackup = !string.IsNullOrEmpty(selectedBackupPath) &&
            File.Exists(selectedBackupPath);

        bool validDestination =
    IsValidDestinationAssetPath(
        restoreDestinationFolderPath);

        using (new EditorGUI.DisabledScope(
                   !validBackup ||
                   !validDestination ||
                   EditorApplication.isPlayingOrWillChangePlaymode ||
                   EditorApplication.isCompiling)) {

            if (GUILayout.Button(
                    "Restore Selected Scene",
                    GUILayout.Height(34f))) {

                RestoreSelectedBackup();
            }
        }
    }

    private void DrawBackupFilesList() {
        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField(
            $"Available Backups ({discoveredBackupFiles.Count})",
            EditorStyles.miniBoldLabel);

        if (GUILayout.Button(
                "Refresh",
                GUILayout.Width(70f))) {

            RefreshBackupFiles();
        }

        EditorGUILayout.EndHorizontal();

        backupFilesScrollPosition =
            EditorGUILayout.BeginScrollView(
                backupFilesScrollPosition,
                GUILayout.MinHeight(100f),
                GUILayout.MaxHeight(180f));

        if (discoveredBackupFiles.Count == 0) {
            EditorGUILayout.HelpBox(
                "No backup scene files were found.",
                MessageType.None);
        }

        foreach (string backupPath in discoveredBackupFiles) {
            bool selected =
                string.Equals(
                    selectedBackupPath,
                    backupPath,
                    StringComparison.OrdinalIgnoreCase);

            EditorGUILayout.BeginHorizontal(
                selected
                    ? EditorStyles.helpBox
                    : GUIStyle.none);

            string relativeDisplayName =
                GetBackupDisplayName(backupPath);

            if (GUILayout.Toggle(
                    selected,
                    relativeDisplayName,
                    "Button")) {

                selectedBackupPath = backupPath;
            }

            if (GUILayout.Button(
                    "Show",
                    GUILayout.Width(50f))) {

                EditorUtility.RevealInFinder(backupPath);
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    private void SelectBackupFile(
        SceneAutoBackupSettings settings) {

        string projectRoot =
            Directory.GetParent(
                Application.dataPath)?.FullName;

        string startingFolder =
            Directory.Exists(settings.BackupLocation)
                ? settings.BackupLocation
                : projectRoot;

        string selectedPath =
            EditorUtility.OpenFilePanel(
                "Select Scene Backup",
                startingFolder,
                "unity");

        if (string.IsNullOrEmpty(selectedPath)) {
            return;
        }

        selectedBackupPath =
            Path.GetFullPath(selectedPath);

        Repaint();
    }

    private void RestoreSelectedBackup() {
        if (string.IsNullOrEmpty(selectedBackupPath) ||
            !File.Exists(selectedBackupPath)) {

            EditorUtility.DisplayDialog(
                "Scene Auto Backup",
                "The selected backup file does not exist.",
                "OK");

            return;
        }

        if (!IsValidDestinationAssetPath(
        restoreDestinationFolderPath)) {

            EditorUtility.DisplayDialog(
                "Scene Auto Backup",
                "Select a valid destination folder located inside " +
                "the project's Assets folder.",
                "OK");

            return;
        }

        string destinationFolderPath =
            restoreDestinationFolderPath;

        string backupFileName =
            Path.GetFileName(selectedBackupPath);

        string destinationAssetPath =
            CombineAssetPath(
                destinationFolderPath,
                backupFileName);

        string destinationAbsolutePath =
            AssetPathToAbsolutePath(
                destinationAssetPath);

        if (File.Exists(destinationAbsolutePath)) {
            bool overwrite =
                EditorUtility.DisplayDialog(
                    "Scene Already Exists",
                    $"A scene with this exact name already exists:\n\n" +
                    $"{destinationAssetPath}\n\n" +
                    "Do you want to overwrite it?",
                    "Overwrite",
                    "Cancel");

            if (!overwrite) {
                return;
            }
        }

        bool canContinue =
            EditorSceneManager
                .SaveCurrentModifiedScenesIfUserWantsTo();

        if (!canContinue) {
            return;
        }

        try {
            string destinationDirectory =
                Path.GetDirectoryName(
                    destinationAbsolutePath);

            if (string.IsNullOrEmpty(destinationDirectory)) {
                throw new InvalidOperationException(
                    "The destination directory is invalid.");
            }

            Directory.CreateDirectory(destinationDirectory);

            File.Copy(
                selectedBackupPath,
                destinationAbsolutePath,
                true);

            AssetDatabase.ImportAsset(
                destinationAssetPath,
                ImportAssetOptions.ForceSynchronousImport |
                ImportAssetOptions.ForceUpdate);

            AssetDatabase.Refresh();

            SceneAsset restoredScene =
                AssetDatabase.LoadAssetAtPath<SceneAsset>(
                    destinationAssetPath);

            if (restoredScene == null) {
                throw new InvalidOperationException(
                    "Unity copied the file but could not import it " +
                    "as a valid scene.");
            }

            Selection.activeObject = restoredScene;
            EditorGUIUtility.PingObject(restoredScene);

            Debug.Log(
                $"Scene restored successfully:\n" +
                destinationAssetPath);

            if (openSceneAfterRestore) {
                EditorSceneManager.OpenScene(
                    destinationAssetPath,
                    OpenSceneMode.Single);
            }
            else {
                EditorUtility.DisplayDialog(
                    "Scene Restored",
                    $"The scene was restored successfully:\n\n" +
                    destinationAssetPath,
                    "OK");
            }
        }
        catch (Exception exception) {
            Debug.LogError(
                $"Scene Auto Backup: Failed to restore:\n" +
                selectedBackupPath);

            Debug.LogException(exception);

            EditorUtility.DisplayDialog(
                "Restore Failed",
                exception.Message,
                "OK");
        }
    }
    private void DrawRestoreDestinationFolder() {
        EditorGUILayout.LabelField(
            "Restore Destination",
            EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.SelectableLabel(
            string.IsNullOrWhiteSpace(
                restoreDestinationFolderPath)
                ? "No destination folder selected"
                : restoreDestinationFolderPath,
            EditorStyles.textField,
            GUILayout.Height(
                EditorGUIUtility.singleLineHeight));

        if (GUILayout.Button(
                "Browse",
                GUILayout.Width(80f))) {

            SelectRestoreDestinationFolder();
        }

        EditorGUILayout.EndHorizontal();
    }

    private void SelectRestoreDestinationFolder() {
        string startingFolder =
            Application.dataPath;

        if (IsValidDestinationAssetPath(
                restoreDestinationFolderPath)) {

            startingFolder =
                AssetPathToAbsolutePath(
                    restoreDestinationFolderPath);
        }

        string selectedFolder =
            EditorUtility.OpenFolderPanel(
                "Select Scene Restore Destination",
                startingFolder,
                "");

        if (string.IsNullOrEmpty(selectedFolder)) {
            return;
        }

        selectedFolder =
            Path.GetFullPath(selectedFolder)
                .Replace("\\", "/");

        string assetsAbsolutePath =
            Path.GetFullPath(Application.dataPath)
                .Replace("\\", "/");

        bool insideAssets =
            selectedFolder.Equals(
                assetsAbsolutePath,
                StringComparison.OrdinalIgnoreCase) ||
            selectedFolder.StartsWith(
                assetsAbsolutePath + "/",
                StringComparison.OrdinalIgnoreCase);

        if (!insideAssets) {
            EditorUtility.DisplayDialog(
                "Invalid Destination",
                "The restore destination must be located inside " +
                "this project's Assets folder.",
                "OK");

            return;
        }

        string relativePath =
            selectedFolder.Substring(
                assetsAbsolutePath.Length);

        restoreDestinationFolderPath =
            "Assets" + relativePath;

        restoreDestinationFolderPath =
            restoreDestinationFolderPath
                .Replace("\\", "/");

        Repaint();
    }

    private static bool IsValidDestinationAssetPath(
        string assetPath) {

        if (string.IsNullOrWhiteSpace(assetPath)) {
            return false;
        }

        assetPath =
            assetPath.Replace("\\", "/");

        if (assetPath != "Assets" &&
            !assetPath.StartsWith(
                "Assets/",
                StringComparison.Ordinal)) {

            return false;
        }

        string absolutePath =
            AssetPathToAbsolutePath(assetPath);

        return Directory.Exists(absolutePath);
    }
    private void RefreshBackupFiles() {
        discoveredBackupFiles.Clear();

        string root =
            SceneAutoBackupSettings.instance
                .BackupLocation;

        if (string.IsNullOrWhiteSpace(root) ||
            !Directory.Exists(root)) {

            Repaint();
            return;
        }

        try {
            string[] files =
                Directory.GetFiles(
                    root,
                    "*.unity",
                    SearchOption.AllDirectories);

            Array.Sort(
                files,
                (first, second) =>
                    File.GetLastWriteTimeUtc(second)
                        .CompareTo(
                            File.GetLastWriteTimeUtc(first)));

            discoveredBackupFiles.AddRange(files);
        }
        catch (Exception exception) {
            Debug.LogWarning(
                $"Scene Auto Backup: Failed to scan backup " +
                $"folder.\n{exception.Message}");
        }

        Repaint();
    }

    private static void AddOpenScenes(
        SceneAutoBackupSettings settings) {

        int addedCount = 0;

        for (int i = 0; i < SceneManager.sceneCount; i++) {
            Scene scene = SceneManager.GetSceneAt(i);

            if (!scene.IsValid() ||
                !scene.isLoaded ||
                string.IsNullOrEmpty(scene.path)) {
                continue;
            }

            bool alreadyTracked =
                settings.IsSceneTracked(scene.path);

            settings.AddScene(scene.path);

            if (!alreadyTracked) {
                addedCount++;
            }
        }

        if (addedCount == 0) {
            Debug.Log(
                "Scene Auto Backup: No new saved scenes were found.");
        }
        else {
            Debug.Log(
                $"Scene Auto Backup: Added {addedCount} scene(s).");
        }
    }
    private static string GetBackupDisplayName(
        string backupPath) {

        string root =
            SceneAutoBackupSettings.instance
                .BackupLocation;

        if (string.IsNullOrEmpty(root)) {
            return Path.GetFileName(backupPath);
        }

        try {
            Uri rootUri =
                new Uri(
                    Path.GetFullPath(root)
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar);

            Uri fileUri =
                new Uri(Path.GetFullPath(backupPath));

            return Uri.UnescapeDataString(
                    rootUri.MakeRelativeUri(fileUri).ToString())
                .Replace("/", " / ");
        }
        catch {
            return Path.GetFileName(backupPath);
        }
    }

    private static string CombineAssetPath(
        string folder,
        string fileName) {

        return $"{folder.TrimEnd('/')}/{fileName}"
            .Replace("\\", "/");
    }

    private static string AssetPathToAbsolutePath(
        string assetPath) {

        string projectRoot =
            Directory.GetParent(
                Application.dataPath)?.FullName;

        if (string.IsNullOrEmpty(projectRoot)) {
            throw new InvalidOperationException(
                "Could not determine the Unity project folder.");
        }

        return Path.GetFullPath(
            Path.Combine(projectRoot, assetPath));
    }
}