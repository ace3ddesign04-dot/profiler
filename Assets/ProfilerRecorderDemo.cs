using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

public class ProfilerRecorderDemo : MonoBehaviour {
    public FPSCounter fpsCounter;
    public PerformanceMonitor performanceMonitor;
    public DeviceThermal deviceThermal;

    // ── Markers for our own code ─────────────────────────────────────────
    private static readonly ProfilerMarker _markerGameLogic = new ProfilerMarker("Game.Logic");

    // ── Recorders — attach to any marker (ours or Unity's built-ins) ─────
    private ProfilerRecorder _gameLogicRecorder;

    // ── Unity built-in markers you can tap into for free ─────────────────
    private ProfilerRecorder _mainThreadRecorder;
    private ProfilerRecorder _gcAllocRecorder;
    private ProfilerRecorder _drawCallRecorder;

    [SerializeField] private Text _statsLabel;

    private void OnEnable() {
        // ProfilerRecorderOptions.SumOverCapacity → keeps N samples, gives avg/sum
        _gameLogicRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Game.Logic", 15);

        // Built-in Unity markers — no ProfilerMarker needed on your side
        _mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
        _gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 15);
        _drawCallRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);
    }

    private void OnDisable() {
        // Always dispose — they hold native resources
        _gameLogicRecorder.Dispose();
        _mainThreadRecorder.Dispose();
        _gcAllocRecorder.Dispose();
        _drawCallRecorder.Dispose();
    }

    private void Update() {
        // ── Wrap your logic in the marker ────────────────────────────────
        using (_markerGameLogic.Auto()) {
            //fpsCounter.DoUpdate();
            //deviceThermal.DoUpdate();
            //performanceMonitor.DoUpdate();
        }

        // ── Read back stats every frame (or throttle it) ─────────────────
        if (_statsLabel != null)
            UpdateStatsDisplay();
    }

    private void UpdateStatsDisplay() {
        // LastValue is in nanoseconds — convert to ms
        double gameLogicMs = _gameLogicRecorder.LastValue / 1_000_000.0;
        double mainThreadMs = _mainThreadRecorder.LastValue / 1_000_000.0;
        long gcAllocBytes = _gcAllocRecorder.LastValue;
        long drawCalls = _drawCallRecorder.LastValue;

        _statsLabel.text =
            $"Main Thread : {mainThreadMs:F3} ms\n" +
            $"Game Logic  : {gameLogicMs:F3} ms\n" +
            $"GC Alloc    : {gcAllocBytes} B\n" +
            $"Draw Calls  : {drawCalls}";
    }
}