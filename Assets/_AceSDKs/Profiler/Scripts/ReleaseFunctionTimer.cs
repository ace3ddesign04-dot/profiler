using System.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

public class ReleaseFunctionTimer : MonoBehaviour {
    public FPSCounter fpsCounter;
    public PerformanceMonitor performanceMonitor;
    public DeviceThermal deviceThermal;

    [SerializeField] private Text statsLabel;

    [Header("Runtime Stats")]
    public double lastMs;
    public double averageMs;
    public double maxMs;
    public int sampleCount;

    private double _totalMs;

    private void Update() {
        long start = Stopwatch.GetTimestamp();

        DoMeasuredWork();

        long end = Stopwatch.GetTimestamp();

        double elapsedMs = (end - start) * 1000.0 / Stopwatch.Frequency;

        lastMs = elapsedMs;
        _totalMs += elapsedMs;
        sampleCount++;
        averageMs = _totalMs / sampleCount;

        if (elapsedMs > maxMs)
            maxMs = elapsedMs;

        if (statsLabel != null) {
            statsLabel.text = "Profiler Time Overhead\n" +
                "Last : " + lastMs.ToString("F3") + " ms\n" +
                "Avg  : " + averageMs.ToString("F3") + " ms\n" +
                "Max  : " + maxMs.ToString("F3") + " ms";
        }
    }

    private void DoMeasuredWork() {
        fpsCounter.DoUpdate();
        performanceMonitor.DoUpdate();
        deviceThermal.DoUpdate();
    }

    public void ResetStats() {
        lastMs = 0.0;
        averageMs = 0.0;
        maxMs = 0.0;
        sampleCount = 0;
        _totalMs = 0.0;
    }
}