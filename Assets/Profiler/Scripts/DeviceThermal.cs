using UnityEngine;
using UnityEngine.UI;

public class DeviceThermal : MonoBehaviour {
    [Header("Text Fields")]
    public Text elapsedText;
    public Text batteryTempText;
    public Text cpuTempText;
    public Text gpuTempText;
    public Text thermalStatusText;

    [Header("Refresh")]
    public float refreshRate = 0.5f;

    [Header("Temperature Thresholds")]
    public float warmTempC = 38f;
    public float hotTempC = 42f;
    public float criticalTempC = 46f;

    [Header("Threshold Colors")]
    public Color unavailableColor = Color.gray;
    public Color goodColor = Color.green;
    public Color warmColor = Color.yellow;
    public Color hotColor = new Color(1f, 0.5f, 0f);
    public Color criticalColor = Color.red;

    float _nextRefresh;

    public enum ThermalColorLevel {
        Unavailable,
        Good,
        Warm,
        Hot,
        Critical
    }

    public void DoUpdate() {
        if (Time.unscaledTime < _nextRefresh)
            return;

        _nextRefresh = Time.unscaledTime + refreshRate;
        RefreshTexts();
    }

    void RefreshTexts() {
        float battery = BatteryTempC;
        float cpu = CpuTempC;
        float gpu = GpuTempC;

        SetText(elapsedText, "Time Elapsed: " + TimeElapsedFormatted);

        SetTempText(batteryTempText, "Battery Temp", battery);
        SetTempText(cpuTempText, "CPU Temp", cpu);
        SetTempText(gpuTempText, "GPU Temp", gpu);

        SetText(thermalStatusText, "Thermal Status: " + ThermalStatus);
    }

    void SetTempText(Text target, string label, float tempC) {
        if (target == null)
            return;

        ThermalColorLevel level = GetTempLevel(tempC, warmTempC, hotTempC, criticalTempC);

        target.text = $"{label}: {FormatTemp(tempC)} [{level}]";
        target.color = GetColorForLevel(level);
    }

    static void SetText(Text target, string value) {
        if (target != null)
            target.text = value;
    }

    Color GetColorForLevel(ThermalColorLevel level) {
        return level switch {
            ThermalColorLevel.Good => goodColor,
            ThermalColorLevel.Warm => warmColor,
            ThermalColorLevel.Hot => hotColor,
            ThermalColorLevel.Critical => criticalColor,
            _ => unavailableColor
        };
    }

    public static string Platform =>
#if UNITY_ANDROID && !UNITY_EDITOR
        "Android";
#elif UNITY_IOS && !UNITY_EDITOR
        "iOS";
#else
        "Editor/Unsupported";
#endif

    public static float BatteryTempC => GetBatteryTemperatureC();
    public static float CpuTempC => GetCpuTemperatureC();
    public static float GpuTempC => GetGpuTemperatureC();
    public static string ThermalStatus => GetThermalStatusString();

    public static float TimeElapsedSeconds => Time.realtimeSinceStartup;

    public static string TimeElapsedFormatted {
        get {
            float seconds = Time.realtimeSinceStartup;

            int h = Mathf.FloorToInt(seconds / 3600f);
            int m = Mathf.FloorToInt((seconds % 3600f) / 60f);
            int s = Mathf.FloorToInt(seconds % 60f);

            return h > 0
                ? $"{h:00}:{m:00}:{s:00}"
                : $"{m:00}:{s:00}";
        }
    }

    public static string FormatTemp(float value) {
        return value >= 0f ? value.ToString("F1") + " °C" : "N/A";
    }

    public static ThermalColorLevel GetTempLevel(
        float tempC,
        float warm = 38f,
        float hot = 42f,
        float critical = 46f
    ) {
        if (tempC < 0f)
            return ThermalColorLevel.Unavailable;

        if (tempC >= critical)
            return ThermalColorLevel.Critical;

        if (tempC >= hot)
            return ThermalColorLevel.Hot;

        if (tempC >= warm)
            return ThermalColorLevel.Warm;

        return ThermalColorLevel.Good;
    }

#if UNITY_ANDROID && !UNITY_EDITOR

    static float GetBatteryTemperatureC()
    {
        try
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

            using var filter = new AndroidJavaObject(
                "android.content.IntentFilter",
                "android.intent.action.BATTERY_CHANGED"
            );

            using var intent = activity.Call<AndroidJavaObject>(
                "registerReceiver",
                null,
                filter
            );

            if (intent == null)
                return -1f;

            int tempTenths = intent.Call<int>("getIntExtra", "temperature", -1);
            return tempTenths > 0 ? tempTenths / 10f : -1f;
        }
        catch
        {
            return -1f;
        }
    }

    static float GetCpuTemperatureC()
    {
        return GetHardwareTemperatureC(0);
    }

    static float GetGpuTemperatureC()
    {
        return GetHardwareTemperatureC(1);
    }

    static float GetHardwareTemperatureC(int type)
    {
        if (GetSdkInt() < 24)
            return -1f;

        try
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

            using var contextClass = new AndroidJavaClass("android.content.Context");
            string service = contextClass.GetStatic<string>("HARDWARE_PROPERTIES_SERVICE");

            using var hpm = activity.Call<AndroidJavaObject>("getSystemService", service);

            if (hpm == null)
                return -1f;

            const int TEMPERATURE_CURRENT = 0;

            float[] temps = hpm.Call<float[]>(
                "getDeviceTemperatures",
                type,
                TEMPERATURE_CURRENT
            );

            if (temps == null || temps.Length == 0)
                return -1f;

            float hottest = -1f;

            for (int i = 0; i < temps.Length; i++)
            {
                float t = temps[i];

                if (t > 0f && t < 150f)
                {
                    if (hottest < 0f || t > hottest)
                        hottest = t;
                }
            }

            return hottest;
        }
        catch
        {
            return -1f;
        }
    }

    static string GetThermalStatusString()
    {
        if (GetSdkInt() < 29)
            return "Unavailable";

        try
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

            using var contextClass = new AndroidJavaClass("android.content.Context");
            string service = contextClass.GetStatic<string>("POWER_SERVICE");

            using var powerManager = activity.Call<AndroidJavaObject>("getSystemService", service);

            if (powerManager == null)
                return "Unavailable";

            int status = powerManager.Call<int>("getCurrentThermalStatus");

            return status switch
            {
                0 => "None",
                1 => "Light",
                2 => "Moderate",
                3 => "Severe",
                4 => "Critical",
                5 => "Emergency",
                6 => "Shutdown",
                _ => "Unknown"
            };
        }
        catch
        {
            return "Unavailable";
        }
    }

    static int GetSdkInt()
    {
        using var version = new AndroidJavaClass("android.os.Build$VERSION");
        return version.GetStatic<int>("SDK_INT");
    }

#elif UNITY_IOS && !UNITY_EDITOR

    static float GetBatteryTemperatureC() => -1f;
    static float GetCpuTemperatureC() => -1f;
    static float GetGpuTemperatureC() => -1f;

    static string GetThermalStatusString()
    {
        return UnityEngine.iOS.Device.generation + " / " +
               UnityEngine.iOS.Device.systemVersion + " / " +
               UnityEngine.iOS.Device.thermalState;
    }

#else

    static float GetBatteryTemperatureC() => -1f;
    static float GetCpuTemperatureC() => -1f;
    static float GetGpuTemperatureC() => -1f;
    static string GetThermalStatusString() => "Unavailable";

#endif
}