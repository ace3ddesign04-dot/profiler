using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// FPS Counter for release builds using UnityEngine.UI.
/// Attach to any GameObject in your Canvas hierarchy.
/// Wire up the Text fields and optional buttons in the Inspector.
/// </summary>
public class FPSCounter : MonoBehaviour {
    private enum AverageWindow {
        ThreeSeconds = 3,
        FiveSeconds = 5,
        TenSeconds = 10,
        ThirtySeconds = 30
    }

    private bool pause;
    private float _currentFPS;
    private float _averageFPS;
    private float _minFPS = float.MaxValue;
    private float _maxFPS = float.MinValue;
    private readonly Queue<(float time, float fps)> _fpsHistory = new Queue<(float, float)>();
    private float _windowSum;
    private float _smoothDelta;
    private const float SmoothFactor = 0.1f;
    private AverageWindow[] _windowValues;

    [Header("UI Text Fields")]
    [SerializeField] private Text textCurrentFPS;
    [SerializeField] private Text textAverageFPS;
    [SerializeField] private Text textMinFPS;
    [SerializeField] private Text textMaxFPS;
    [SerializeField] private Button buttonPause;
    [SerializeField] private Button buttonReset;

    [Header("UI Dropdown")]
    [Tooltip("Dropdown for selecting the average window at runtime. Options are populated automatically.")]
    [SerializeField] private Dropdown dropdownAverageWindow;

    [Header("Keyboard Shortcuts")]
    [SerializeField] private KeyCode pauseKey = KeyCode.F3;
    [SerializeField] private KeyCode resetKey = KeyCode.F4;

    [Header("Average Window")]
    [SerializeField] private AverageWindow averageWindow = AverageWindow.FiveSeconds;

    [Header("Color Thresholds")]
    [SerializeField] private int criticalThreshold = 20;
    [SerializeField] private int lowThreshold = 30;
    [SerializeField] private int goodThreshold = 60;
    [SerializeField] private Color colorCritical = new Color(0.95f, 0.22f, 0.22f);
    [SerializeField] private Color colorLow = new Color(0.96f, 0.76f, 0.08f);
    [SerializeField] private Color colorGood = new Color(0.30f, 0.85f, 0.35f);
    [SerializeField] private Color colorExcellent = new Color(0.20f, 0.70f, 1.00f);

    private void Start() {
        buttonPause.onClick.AddListener(OnClickPauseResume);
        buttonReset.onClick.AddListener(OnClickResetResetStats);
        InitDropdown();
    }

    private void Update() {

        HandleInput();
        if (!pause) {
            _smoothDelta = Mathf.Lerp(_smoothDelta, Time.unscaledDeltaTime, SmoothFactor);
            _currentFPS = _smoothDelta > 0f ? 1f / _smoothDelta : 0f;

            UpdateRollingAverage();
            UpdateMinMax(_currentFPS);
        }
        RefreshTexts();
    }
    private void OnClickPauseResume() {
        pause = !pause;
        buttonPause.GetComponentInChildren<Text>().text = pause ? "Resume" : "Pause";
    }
    private void OnClickResetResetStats() {
        _minFPS = float.MaxValue;
        _maxFPS = float.MinValue;
        _fpsHistory.Clear();
        _windowSum = 0f;
        _averageFPS = 0f;
        _currentFPS = 0f;
    }
    private void InitDropdown() {
        if (dropdownAverageWindow == null) return;

        _windowValues = (AverageWindow[])System.Enum.GetValues(typeof(AverageWindow));

        dropdownAverageWindow.ClearOptions();

        int selectedIndex = 0;
        var options = new List<string>();

        for (int i = 0; i < _windowValues.Length; i++) {
            // Convert enum name to readable label: "FiveSeconds" -> "5 Seconds"
            int seconds = (int)_windowValues[i];
            options.Add($"{seconds} Seconds");

            if (_windowValues[i] == averageWindow)
                selectedIndex = i;
        }

        dropdownAverageWindow.AddOptions(options);
        dropdownAverageWindow.value = selectedIndex;
        dropdownAverageWindow.RefreshShownValue();
        dropdownAverageWindow.onValueChanged.AddListener(OnDropdownChanged);
    }

    private void OnDropdownChanged(int index) {
        averageWindow = _windowValues[index];
        // Clear history so the new window takes effect immediately
        OnClickResetResetStats();
    }
    private void RefreshTexts() {
        float displayMin = _minFPS == float.MaxValue ? 0f : _minFPS;
        float displayMax = _maxFPS == float.MinValue ? 0f : _maxFPS;
        int windowSec = (int)averageWindow;

        SetText(textCurrentFPS, $"FPS: {_currentFPS:0}", _currentFPS);
        SetText(textAverageFPS, $"Avg ({windowSec}s): {_averageFPS:0}", _averageFPS);
        SetText(textMinFPS, $"Min: {displayMin:0}", displayMin);
        SetText(textMaxFPS, $"Max: {displayMax:0}", displayMax);
    }

    private void SetText(Text field, string content, float fps) {
        if (field == null) return;
        field.text = content;
        field.color = GetFPSColor(fps);
    }

    private void UpdateRollingAverage() {
        float now = Time.unscaledTime;
        float windowSec = (float)(int)averageWindow;

        _fpsHistory.Enqueue((now, _currentFPS));
        _windowSum += _currentFPS;

        while (_fpsHistory.Count > 0 && (now - _fpsHistory.Peek().time) > windowSec) {
            var old = _fpsHistory.Dequeue();
            _windowSum -= old.fps;
        }

        _averageFPS = _fpsHistory.Count > 0 ? _windowSum / _fpsHistory.Count : 0f;
    }

    private void UpdateMinMax(float fps) {
        if (Time.frameCount < 5) return;
        if (fps < _minFPS) _minFPS = fps;
        if (fps > _maxFPS) _maxFPS = fps;
    }

    private void HandleInput() {
        if (Input.GetKeyDown(pauseKey)) OnClickPauseResume();
        if (Input.GetKeyDown(resetKey)) OnClickResetResetStats();
    }

    private Color GetFPSColor(float fps) {
        if (fps < criticalThreshold) return colorCritical;
        if (fps < lowThreshold) return colorLow;
        if (fps < goodThreshold) return colorGood;
        return colorExcellent;
    }
}