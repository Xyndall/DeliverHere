using UnityEngine;
using Unity.Netcode;
using System;
using DeliverHere.GamePlay;
using DeliverHere.NetworkScripts;

[DisallowMultipleComponent]
public class GameTimer : MonoBehaviour
{
    [Header("Timer Settings")]
    [Tooltip("Duration of a day/round in seconds.")]
    [SerializeField] private float durationSeconds = 180f;

    [Tooltip("Automatically start the first timer if no day has started yet.")]
    [SerializeField] private bool autoStartFirstDay = false;

    [Tooltip("If true, immediately advance to the next day on success without requiring a button press.")]
    [SerializeField] private bool autoAdvanceOnSuccess = false;

    [Header("UI")]
    [SerializeField] private GameUIController uiController;
    [SerializeField] private bool autoFindUIController = true;

    [Header("Debug")]
    [SerializeField] private bool enableDebugLogs = false;

    // Runtime state
    private float remainingTime;
    private float totalTime;
    private bool running;
    private bool awaitingNextDay;
    private bool lastDaySuccess;

    private MoneyTargetManager money;
    private GameManager gm;
    private NetworkGameState netState;
    private NetworkUISync _uiSync;

    // Public read-only
    public float RemainingTime => Mathf.Max(0f, remainingTime);
    public float TotalTime => totalTime;
    public float Normalized => totalTime <= 0.001f ? 1f : 1f - Mathf.Clamp01(remainingTime / totalTime);
    public bool IsRunning => running;
    public bool AwaitingNextDay => awaitingNextDay;
    public bool WasSuccessAtDayEnd => lastDaySuccess;

    // Fired exactly when the timer hits zero, BEFORE success is evaluated.
    public event Action OnDayTimerAboutToExpire;
    // Fired AFTER success/fail is evaluated (GameManager listens to show popup).
    public event Action<bool> OnDayEndedEvaluated;

    private bool IsServerOrStandalone =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        gm = GameManager.Instance;
    }

    private void OnEnable()
    {
        EnsureRefs();

        if (money != null)
        {
            money.OnDayAdvanced += HandleDayAdvanced;
        }

        // Initialize totalTime locally to support client display from normalized progress
        if (totalTime <= 0f)
            totalTime = Mathf.Max(1f, durationSeconds);

        if (autoStartFirstDay && money != null && money.CurrentDay <= 0 && IsServerOrStandalone)
        {
            money.AdvanceDay();
        }
        else if (money != null && money.CurrentDay > 0 && IsServerOrStandalone)
        {
            if (!running && !awaitingNextDay)
            {
                StartNewTimer();
            }
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

        if (netState == null)
            netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
        
        if (_uiSync == null)
            _uiSync = NetworkUISync.Instance ?? FindFirstObjectByType<NetworkUISync>();
    }

    private void HandleDayAdvanced(int newDayIndex)
    {
        if (!IsServerOrStandalone) return;
        StartNewTimer();
    }

    private void StartNewTimer()
    {
        awaitingNextDay = false;
        lastDaySuccess = false;

        totalTime = Mathf.Max(1f, durationSeconds);
        remainingTime = totalTime;
        running = true;

        uiController?.SetTimerVisible(true);
        PushTimerUI();

        if (enableDebugLogs)
            Debug.Log($"[GameTimer] Timer started: {totalTime}s");
    }

    private void Update()
    {
        // Do nothing if gameplay is inactive for this peer
        if (gm != null && !gm.IsGameplayActive) return;

        if (IsServerOrStandalone)
        {
            remainingTime -= Time.deltaTime;
            if (remainingTime <= 0f)
            {
                remainingTime = 0f;
                PushTimerUI();
                OnTimeExpired();
                return;
            }
            PushTimerUI();
        }
        else
        {
            // CHANGED: Clients get timer from NetworkGameState's replicated progress
            if (netState != null)
            {
                float t = Mathf.Clamp01(netState.TimerProgress);
                remainingTime = Mathf.Clamp((1f - t) * totalTime, 0f, totalTime);
                PushTimerUI();
            }
            else if (enableDebugLogs && Time.frameCount % 60 == 0)
            {
                Debug.LogWarning("[GameTimer] Client: NetworkGameState not found, timer won't update");
            }
        }
    }

    private void PushTimerUI()
    {
        if (uiController == null) return;
        uiController.SetTimerSeconds(remainingTime);
        
        if (IsServerOrStandalone && _uiSync != null)
        {
            _uiSync.ServerSetTimer(remainingTime);
        }
    }

    private void OnTimeExpired()
    {
        running = false;

        OnDayTimerAboutToExpire?.Invoke();

        bool success = false;
        var zoneManager = FindFirstObjectByType<DailyDeliveryZoneManager>();
        
        if (zoneManager != null)
        {
            success = zoneManager.AreAllZoneQuotasMet();
        }
        else if (money != null)
        {
            success = money.CompleteDayAndEvaluate();
        }

        lastDaySuccess = success;
        awaitingNextDay = success;

        OnDayEndedEvaluated?.Invoke(success);

        if (enableDebugLogs)
            Debug.Log($"[GameTimer] Time expired. Success: {success}");

        if (success && autoAdvanceOnSuccess)
        {
            ConfirmStartNextDay();
        }
    }

    public void ConfirmStartNextDay()
    {
        if (!awaitingNextDay) return;
        if (!IsServerOrStandalone)
        {
            Debug.LogWarning("[GameTimer] ConfirmStartNextDay ignored on client; server is authoritative.");
            return;
        }

        awaitingNextDay = false;

        if (money != null)
        {
            money.AdvanceDay();
        }
        else
        {
            Debug.LogWarning("[GameTimer] ConfirmStartNextDay called but MoneyTargetManager not found.");
        }

        StartNewTimer();
    }

    public void StartTimerNowIfNeeded()
    {
        if (!IsServerOrStandalone) return;
        if (running || awaitingNextDay) return;
        StartNewTimer();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        durationSeconds = Mathf.Max(1f, durationSeconds);
    }
#endif
}