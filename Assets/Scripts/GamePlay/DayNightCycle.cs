using UnityEngine;
using Unity.Netcode;
using System; // Added for Action

public class DayNightCycle : MonoBehaviour
{
    [Header("Time Settings")]
    [Tooltip("Duration of a day in seconds.")]
    [SerializeField] private float dayDurationSeconds = 180f;

    [Tooltip("Automatically start the first day if no day has started yet.")]
    [SerializeField] private bool autoStartFirstDay = true;

    [Tooltip("If true, immediately advance to the next day on success without requiring a button press.")]
    [SerializeField] private bool autoAdvanceOnSuccess = false;

    [Header("Light Settings")]
    [SerializeField] private Light directionalLight;
    [SerializeField] private bool autoFindDirectionalLight = true;

    [Tooltip("Light temperature (Kelvin) at the start of the day.")]
    [SerializeField] private float dayKelvin = 6500f;

    [Tooltip("Light temperature (Kelvin) when the day ends (night).")]
    [SerializeField] private float nightKelvin = 2000f;

    [Tooltip("Directional light intensity at the start of the day.")]
    [SerializeField] private float dayIntensity = 1.2f;

    [Tooltip("Directional light intensity at night.")]
    [SerializeField] private float nightIntensity = 0.05f;

    [Header("Optional Sun Rotation")]
    [Tooltip("Enable to rotate the sun from day to night orientation over time.")]
    [SerializeField] private bool rotateSun = true;

    [Tooltip("Night pitch delta relative to the current day rotation captured at day start.")]
    [SerializeField] private float nightPitchDeltaDegrees = 120f;

    [Tooltip("Ease curve across the day: 0 = day start, 1 = night end.")]
    [SerializeField] private AnimationCurve dayToNightCurve = AnimationCurve.Linear(0, 0, 1, 1);

    [Header("UI")]
    [SerializeField] private GameUIController uiController;
    [SerializeField] private bool autoFindUIController = true;

    // Runtime state
    private float remainingTime;
    private float totalTime;
    private bool running;
    private bool awaitingNextDay;
    private bool lastDaySuccess;

    private Quaternion dayRotation;
    private Quaternion nightRotation;

    private MoneyTargetManager money;
    private GameManager gm;

    // NEW: network state access for clients
    private NetworkGameState netState;
    private int lastSeenNetDay = -1;

    // Public read-only
    public float RemainingTime => Mathf.Max(0f, remainingTime);
    public float TotalTime => totalTime;
    public float Normalized => totalTime <= 0.001f ? 1f : 1f - Mathf.Clamp01(remainingTime / totalTime); // 0..1 (day->night)
    public bool IsRunning => running;
    public bool AwaitingNextDay => awaitingNextDay;
    public bool WasSuccessAtDayEnd => lastDaySuccess;

    // NEW: Fired exactly when the timer hits zero, BEFORE success is evaluated.
    public event Action OnDayTimerAboutToExpire;

    private bool IsServerOrStandalone =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        gm = GameManager.Instance; // May be null if not yet initialized; we handle later in EnsureRefs()
    }

    private void OnEnable()
    {
        EnsureRefs();

        if (money != null)
        {
            money.OnDayAdvanced += HandleDayAdvanced;
        }

        if (money != null && autoStartFirstDay && money.CurrentDay <= 0 && IsServerOrStandalone)
        {
            money.AdvanceDay();
        }
        else if (money != null && money.CurrentDay > 0 && IsServerOrStandalone)
        {
            if (!running && !awaitingNextDay)
            {
                StartNewDayTimer();
            }
        }

        // Clients: on first enable, if server already started a day, prime visuals
        if (!IsServerOrStandalone && netState != null && netState.CurrentDay > 0)
        {
            lastSeenNetDay = netState.CurrentDay;
            CaptureDayRotation();
            ApplyLighting(0f);
            PushDayNightUI(Mathf.Clamp01(netState.DayNightProgress));
        }
    }

    private void OnDisable()
    {
        if (money != null)
        {
            money.OnDayAdvanced -= HandleDayAdvanced;
        }
    }

    private void EnsureRefs()
    {
        if (gm == null)
            gm = GameManager.Instance;

        if (money == null)
            money = FindFirstObjectByType<MoneyTargetManager>();

        if (uiController == null && autoFindUIController)
            uiController = FindFirstObjectByType<GameUIController>();

        if (directionalLight == null && autoFindDirectionalLight)
        {
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l != null && l.type == LightType.Directional && l.enabled)
                {
                    directionalLight = l;
                    break;
                }
            }
        }

        if (directionalLight != null)
        {
            directionalLight.useColorTemperature = true;
        }

        if (netState == null)
        {
            netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
        }
    }

    private void HandleDayAdvanced(int newDayIndex)
    {
        if (!IsServerOrStandalone) return;
        StartNewDayTimer();
    }

    private void StartNewDayTimer()
    {
        awaitingNextDay = false;
        lastDaySuccess = false;

        totalTime = Mathf.Max(1f, dayDurationSeconds);
        remainingTime = totalTime;
        running = true;

        CaptureDayRotation();
        ApplyLighting(0f);
        PushDayNightUI(0f);
    }

    private void CaptureDayRotation()
    {
        if (directionalLight == null) return;

        dayRotation = directionalLight.transform.rotation;
        if (rotateSun)
        {
            var axis = directionalLight.transform.right;
            nightRotation = Quaternion.AngleAxis(nightPitchDeltaDegrees, axis) * dayRotation;
        }
        else
        {
            nightRotation = dayRotation;
        }
    }

    private void Update()
    {
        // Do nothing if gameplay is inactive for this peer
        if (gm != null && !gm.IsGameplayActive) return;

        if (IsServerOrStandalone)
        {
            // Server/standalone: authoritative timer
            if (!running) return;

            remainingTime -= Time.deltaTime;
            if (remainingTime <= 0f)
            {
                remainingTime = 0f;
                TickLightingFromNormalized(1f);
                PushDayNightUI(1f);
                OnTimeExpired();
                return;
            }

            TickLightingFromNormalized(Mathf.Clamp01(Normalized));
            PushDayNightUI(Mathf.Clamp01(Normalized));
        }
        else
        {
            // Client: render visuals from networked day/night progress and recapture on day changes
            if (netState == null) return;

            // Detect day change to recapture baseline rotation
            int currentNetDay = netState.CurrentDay;
            if (currentNetDay != lastSeenNetDay && currentNetDay > 0)
            {
                lastSeenNetDay = currentNetDay;
                CaptureDayRotation();
            }

            float t = Mathf.Clamp01(netState.DayNightProgress);
            TickLightingFromNormalized(t);
            PushDayNightUI(t);
        }
    }

    private void TickLightingFromNormalized(float t)
    {
        float eased = dayToNightCurve != null ? Mathf.Clamp01(dayToNightCurve.Evaluate(t)) : t;
        ApplyLighting(eased);
    }

    private void ApplyLighting(float t01)
    {
        if (directionalLight == null) return;

        float kelvin = Mathf.Lerp(dayKelvin, nightKelvin, t01);
        directionalLight.colorTemperature = Mathf.Clamp(kelvin, 1500f, 20000f);
        directionalLight.intensity = Mathf.Lerp(dayIntensity, nightIntensity, t01);

        if (rotateSun)
        {
            directionalLight.transform.rotation = Quaternion.Slerp(dayRotation, nightRotation, t01);
        }
    }

    private void PushDayNightUI(float normalized01)
    {
        if (uiController == null) return;
        uiController.SetDayNightProgress(normalized01);
    }

    private void OnTimeExpired()
    {
        running = false;

        // NEW: allow systems (e.g., delivery zones) to deposit earnings BEFORE success check.
        OnDayTimerAboutToExpire?.Invoke();

        bool success = money != null && money.IsTargetReached;
        lastDaySuccess = success;
        awaitingNextDay = success;

        if (!success)
        {
            if (gm != null)
            {
                Debug.Log("[DayNightCycle] Time up. Target NOT reached. Ending game.");
                gm.EndGame();
            }
            else
            {
                Debug.LogWarning("[DayNightCycle] Time up and target not reached, but GameManager not found.");
            }
        }
        else
        {
            Debug.Log("[DayNightCycle] Time up. Target reached. Awaiting next day confirmation.");
            if (autoAdvanceOnSuccess)
            {
                ConfirmStartNextDay();
            }
        }
    }

    public void ConfirmStartNextDay()
    {
        if (!awaitingNextDay) return;
        if (!IsServerOrStandalone)
        {
            Debug.LogWarning("[DayNightCycle] ConfirmStartNextDay ignored on client; server is authoritative.");
            return;
        }

        awaitingNextDay = false;
        if (money != null)
        {
            money.AdvanceDay();
        }
        else
        {
            Debug.LogWarning("[DayNightCycle] ConfirmStartNextDay called but MoneyTargetManager not found.");
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        dayDurationSeconds = Mathf.Max(1f, dayDurationSeconds);
        dayIntensity = Mathf.Max(0f, dayIntensity);
        nightIntensity = Mathf.Max(0f, nightIntensity);
        dayKelvin = Mathf.Clamp(dayKelvin, 1500f, 20000f);
        nightKelvin = Mathf.Clamp(nightKelvin, 1500f, 20000f);
        nightPitchDeltaDegrees = Mathf.Clamp(nightPitchDeltaDegrees, 0f, 180f);
    }
#endif
}
