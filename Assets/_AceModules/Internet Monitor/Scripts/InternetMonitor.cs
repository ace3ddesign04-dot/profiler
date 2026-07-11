using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

public enum InternetConnectionState {
    Disabled,
    Checking,
    Connected,
    Disconnected
}

public enum PingQuality {
    None,
    Good,
    Warning,
    Critical
}


public class InternetMonitor : MonoBehaviour {
    [Serializable] public class InternetConnectivityEvents {
        public UnityEvent OnInternetDisconnected;
        public UnityEvent OnInternetConnected;

        public UnityEvent<int> OnPingGood;
        public UnityEvent<int> OnPingWarning;
        public UnityEvent<int> OnPingCritical;

        public UnityEvent<int, PingQuality> OnPingUpdated;
    }

    public static event Action InternetConnected;
    public static event Action InternetDisconnected;
    public static event Action<int, PingQuality> PingUpdated;
    public static InternetMonitor Instance { get; private set; }

    public const int FailedPingValue = -1;

    private Coroutine _monitoringRoutine;
    private bool _hasCompletedFirstCheck = false;

    [Header("Debug Section")]
    [SerializeField] private bool active = true;
    [SerializeField] private bool dontDestroyOnLoad = true;
    [SerializeField] private bool verboseLogs = false;

    [Header("Configurations")]
    [Tooltip("A small reliable endpoint is best. Use your own backend endpoint if possible.")]
    [SerializeField] private string connectivityTestUrl = "https://www.google.com/";

    [Tooltip("How often internet should be checked.")]
    [SerializeField] private float checkIntervalSeconds = 5f;

    [Tooltip("UnityWebRequest timeout in seconds.")]
    [SerializeField] private int requestTimeoutSeconds = 5;

    [Tooltip("Ping below or equal to this value is Good.")]
    [SerializeField] private int goodPingThresholdMs = 250;

    [Tooltip("Ping below or equal to this value is Warning.")]
    [SerializeField] private int warningPingThresholdMs = 800;

    [Tooltip("Ping below or equal to this value is Critical. Above this will be treated as disconnected.")]
    [SerializeField] private int criticalPingThresholdMs = 2000;

    [Tooltip("How many failed checks are required before internet is considered disconnected.")]
    [SerializeField] private int failedChecksBeforeDisconnected = 2;

    [Tooltip("Adds a timestamp query parameter so the request is not served from cache.")]
    [SerializeField] private bool cacheBustRequests = true;

    [Tooltip("If true, ping quality events fire on every successful check. If false, they fire only when quality changes.")]
    [SerializeField] private bool invokePingEventsEveryCheck = true;

    [Header("Ping Colors")]
    [SerializeField] private Color goodPingColor = Color.green;
    [SerializeField] private Color warningPingColor = Color.yellow;
    [SerializeField] private Color criticalPingColor = new Color(1f, 0.45f, 0f);
    [SerializeField] private Color disconnectedColor = Color.red;
    [SerializeField] private Color disabledColor = Color.gray;

    [Header("Events")]
    [SerializeField] private InternetConnectivityEvents events;

    [Header("Runtime State")]
    [SerializeField] private InternetConnectionState currentState = InternetConnectionState.Disabled;
    [SerializeField] private PingQuality currentPingQuality = PingQuality.None;
    [SerializeField] private int lastPingMs = FailedPingValue;
    [SerializeField] private int consecutiveFailedChecks = 0;
    [SerializeField] private bool isChecking = false;
    [SerializeField] private string lastError = "";

    public bool Active => active;
    public bool IsConnected => currentState == InternetConnectionState.Connected;
    public bool IsChecking => isChecking;
    public InternetConnectionState CurrentState => currentState;
    public PingQuality CurrentPingQuality => currentPingQuality;
    public int LastPingMs => lastPingMs;
    public string LastError => lastError;

    public InternetConnectivityEvents Events => events;

    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (dontDestroyOnLoad) {
            DontDestroyOnLoad(gameObject);
        }
    }

    private void Start() {
        if (active) {
            StartMonitoring();
        }
        else {
            ApplyState(InternetConnectionState.Disabled, PingQuality.None, FailedPingValue, "Monitor is disabled.", false);
        }
    }

    private void OnDisable() {
        StopMonitoring();
    }

    private void OnValidate() {
        checkIntervalSeconds = Mathf.Max(0.5f, checkIntervalSeconds);
        requestTimeoutSeconds = Mathf.Max(1, requestTimeoutSeconds);

        goodPingThresholdMs = Mathf.Max(1, goodPingThresholdMs);
        warningPingThresholdMs = Mathf.Max(goodPingThresholdMs + 1, warningPingThresholdMs);
        criticalPingThresholdMs = Mathf.Max(warningPingThresholdMs + 1, criticalPingThresholdMs);

        failedChecksBeforeDisconnected = Mathf.Max(1, failedChecksBeforeDisconnected);
    }

    private IEnumerator MonitoringLoop() {
        while (active) {
            if (!isChecking) {
                yield return CheckConnectivity();
            }

            yield return new WaitForSecondsRealtime(checkIntervalSeconds);
        }
    }

    private IEnumerator CheckConnectivity() {
        isChecking = true;

        if (!_hasCompletedFirstCheck) {
            ApplyState(InternetConnectionState.Checking, PingQuality.None, FailedPingValue, "", false);
        }

        float startTime = Time.realtimeSinceStartup;
        string requestUrl = BuildRequestUrl();

        using (UnityWebRequest request = UnityWebRequest.Get(requestUrl)) {
            request.timeout = requestTimeoutSeconds;
            request.SetRequestHeader("Cache-Control", "no-cache");
            request.SetRequestHeader("Pragma", "no-cache");

            yield return request.SendWebRequest();

            int pingMs = Mathf.RoundToInt((Time.realtimeSinceStartup - startTime) * 1000f);

            if (IsRequestSuccessful(request)) {
                HandleSuccessfulCheck(pingMs);
            }
            else {
                HandleFailedCheck(request.error);
            }
        }

        _hasCompletedFirstCheck = true;
        isChecking = false;
    }

    private void HandleSuccessfulCheck(int pingMs) {
        PingQuality quality = GetPingQualityForPing(pingMs);

        if (quality == PingQuality.None) {
            HandleFailedCheck("Ping exceeded critical threshold: " + pingMs + " ms");
            return;
        }

        lastPingMs = pingMs;
        consecutiveFailedChecks = 0;
        lastError = "";

        ApplyState(InternetConnectionState.Connected, quality, pingMs, "", true);

        Log("Internet connected. Ping: " + pingMs + " ms. Quality: " + quality);
    }

    private void HandleFailedCheck(string error) {
        consecutiveFailedChecks++;
        lastPingMs = FailedPingValue;
        lastError = string.IsNullOrEmpty(error) ? "Connectivity check failed." : error;

        if (consecutiveFailedChecks >= failedChecksBeforeDisconnected) {
            ApplyState(InternetConnectionState.Disconnected, PingQuality.None, FailedPingValue, lastError, true);
            Log("Internet disconnected. Error: " + lastError);
        }
        else {
            Log("Connectivity check failed, waiting for more failed checks. Error: " + lastError);
        }
    }

    private void ApplyState(InternetConnectionState newState, PingQuality newQuality, int pingMs, string error, bool invokeEvents) {
        InternetConnectionState previousState = currentState;
        PingQuality previousQuality = currentPingQuality;

        currentState = newState;
        currentPingQuality = newQuality;
        lastPingMs = pingMs;
        lastError = error;

        if (!invokeEvents) {
            return;
        }

        if (newState == InternetConnectionState.Connected &&
            previousState != InternetConnectionState.Connected) {
            events.OnInternetConnected?.Invoke();
            InternetConnected?.Invoke();
        }

        if (newState == InternetConnectionState.Disconnected &&
            previousState != InternetConnectionState.Disconnected) {
            events.OnInternetDisconnected?.Invoke();
            InternetDisconnected?.Invoke();
        }

        if (newState == InternetConnectionState.Connected) {
            bool shouldInvokePingQualityEvent = invokePingEventsEveryCheck || previousQuality != newQuality;

            if (shouldInvokePingQualityEvent) {
                InvokePingQualityEvent(newQuality, pingMs);
            }

            events.OnPingUpdated?.Invoke(pingMs, newQuality);
            PingUpdated?.Invoke(pingMs, newQuality);
        }
    }

    private void InvokePingQualityEvent(PingQuality quality, int pingMs) {
        switch (quality) {
            case PingQuality.Good:
                events.OnPingGood?.Invoke(pingMs);
                break;

            case PingQuality.Warning:
                events.OnPingWarning?.Invoke(pingMs);
                break;

            case PingQuality.Critical:
                events.OnPingCritical?.Invoke(pingMs);
                break;
        }
    }

    private Color GetColorForPingQuality(PingQuality quality) {
        switch (quality) {
            case PingQuality.Good:
                return goodPingColor;

            case PingQuality.Warning:
                return warningPingColor;

            case PingQuality.Critical:
                return criticalPingColor;

            case PingQuality.None:
            default:
                return disconnectedColor;
        }
    }

    private bool IsRequestSuccessful(UnityWebRequest request) {
        if (request.result != UnityWebRequest.Result.Success) {
            return false;
        }

        return request.responseCode >= 200 && request.responseCode < 400;
    }

    private string BuildRequestUrl() {
        if (!cacheBustRequests) {
            return connectivityTestUrl;
        }

        string separator = connectivityTestUrl.Contains("?") ? "&" : "?";
        return connectivityTestUrl + separator + "t=" + DateTime.UtcNow.Ticks;
    }

    private void Log(string message) {
        if (verboseLogs) {
            Debug.Log("[InternetConnectivityMonitor] " + message);
        }
    }
    public void SetActive(bool value) {
        if (active == value) {
            return;
        }

        active = value;

        if (!Application.isPlaying) {
            return;
        }

        if (active) {
            StartMonitoring();
        }
        else {
            StopMonitoring();
            ApplyState(InternetConnectionState.Disabled, PingQuality.None, FailedPingValue, "Monitor is disabled.", false);
        }
    }

    public void StartMonitoring() {
        if (!Application.isPlaying || !active) {
            return;
        }

        StopMonitoring();
        _monitoringRoutine = StartCoroutine(MonitoringLoop());
    }

    public void StopMonitoring() {
        if (_monitoringRoutine != null) {
            StopCoroutine(_monitoringRoutine);
            _monitoringRoutine = null;
        }

        isChecking = false;
    }

    public void ForceCheckNow() {
        if (!Application.isPlaying || !active || isChecking) {
            return;
        }

        StartCoroutine(CheckConnectivity());
    }

    public Color GetPingColor(int pingMs) {
        PingQuality quality = GetPingQualityForPing(pingMs);
        return GetColorForPingQuality(quality);
    }

    public Color GetCurrentPingColor() {
        if (currentState == InternetConnectionState.Disabled) {
            return disabledColor;
        }

        if (currentState == InternetConnectionState.Disconnected) {
            return disconnectedColor;
        }

        return GetColorForPingQuality(currentPingQuality);
    }

    public PingQuality GetPingQualityForPing(int pingMs) {
        if (pingMs < 0) {
            return PingQuality.None;
        }

        if (pingMs <= goodPingThresholdMs) {
            return PingQuality.Good;
        }

        if (pingMs <= warningPingThresholdMs) {
            return PingQuality.Warning;
        }

        if (pingMs <= criticalPingThresholdMs) {
            return PingQuality.Critical;
        }

        return PingQuality.None;
    }
    public void SimulateDisconnected() {
        if (!Application.isPlaying) {
            return;
        }

        consecutiveFailedChecks = failedChecksBeforeDisconnected;

        ApplyState(InternetConnectionState.Disconnected, PingQuality.None, FailedPingValue, "Simulated disconnected state.", true);
    }

    public void SimulatePing(int pingMs) {
        if (!Application.isPlaying) {
            return;
        }

        PingQuality quality = GetPingQualityForPing(pingMs);

        if (quality == PingQuality.None) {
            HandleFailedCheck("Simulated ping exceeded critical threshold: " + pingMs + " ms");
            return;
        }

        consecutiveFailedChecks = 0;

        ApplyState(InternetConnectionState.Connected, quality, pingMs, "", true);
    }
}