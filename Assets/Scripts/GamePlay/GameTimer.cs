using UnityEngine;
using Unity.Netcode;
using System;

[DisallowMultipleComponent]
public class GameTimer : MonoBehaviour
{
    [Header("Timer Settings")]
    [Tooltip("Duration of a day/round in seconds.")]
    [SerializeField] private float durationSeconds = 180f;

    [Tooltip("Automatically start the first timer if no day has started yet.")]
    [SerializeField] private bool autoStartFirstDay = true;

    [Tooltip("If true, immediately advance to the next day on success without requiring a button press.")]
    [SerializeField] private bool autoAdvanceOnSuccess = false;

    [Header("UI")]
    [SerializeField] private GameUIController uiController;
    [SerializeField] private bool autoFindUIController = true;

    [Header("Debug (Inspector View)")]
    [SerializeField] private float inspectorRemainingSeconds;
    [SerializeField] private float inspectorProgress01;
    [SerializeField] private string inspectorFormattedTime;

    // Runtime state
    private float remainingTime;
    private float totalTime;
    private bool running;
    private bool awaitingNextDay;
    private bool lastDaySuccess;

    private MoneyTargetManager money;
    private GameManager gm;
    private NetworkGameState netState;

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

        // Start flow based on current day
        if (money != null && autoStartFirstDay && money.CurrentDay <= 0 && IsServerOrStandalone)
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

        UpdateInspectorDebug();
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
        PushTimerUI(); // Ensure slider shows start value immediately
        UpdateInspectorDebug();
    }

    private void Update()
    {
        // Do nothing if gameplay is inactive for this peer
        if (gm != null && !gm.IsGameplayActive) return;

        if (IsServerOrStandalone)
        {
            // Server/standalone: authoritative timer
            if (!running) { UpdateInspectorDebug(); return; }

            remainingTime -= Time.deltaTime;
            if (remainingTime <= 0f)
            {
                remainingTime = 0f;
                PushTimerUI();
                UpdateInspectorDebug();
                OnTimeExpired();
                return;
            }
            PushTimerUI();
        }
        else
        {
            // Client: render visuals from networked progress only
            if (netState == null) { UpdateInspectorDebug(); return; }
            float t = Mathf.Clamp01(netState.TimerProgress);
            PushTimerUI();
        }

        UpdateInspectorDebug();
    }

    private void PushTimerUI()
    {
        if (uiController == null) return;
        uiController.SetTimerSeconds(remainingTime);
    }

    private void UpdateInspectorDebug()
    {
        inspectorRemainingSeconds = RemainingTime;
        inspectorProgress01 = Mathf.Clamp01(IsServerOrStandalone ? Normalized : (netState != null ? netState.TimerProgress : 0f));
        inspectorFormattedTime = FormatTime(inspectorRemainingSeconds);
    }

    private static string FormatTime(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int mins = Mathf.FloorToInt(seconds / 60f);
        int secs = Mathf.FloorToInt(seconds % 60f);
        return $"{mins:00}:{secs:00}";
    }

    private void OnTimeExpired()
    {
        running = false;

        OnDayTimerAboutToExpire?.Invoke();

        bool success = money != null && money.IsTargetReached;
        lastDaySuccess = success;
        awaitingNextDay = success;

        OnDayEndedEvaluated?.Invoke(success);

        if (!success)
        {
            Debug.Log("[GameTimer] Time up. Target NOT reached. Showing failure popup.");
        }
        else
        {
            Debug.Log("[GameTimer] Time up. Target reached. Awaiting next day confirmation.");
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

        // Ensure next round starts
        StartNewTimer();
    }

    public void StartTimerNowIfNeeded()
    {
        // Server/standalone only; clients render from networked progress
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