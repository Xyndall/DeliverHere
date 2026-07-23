using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class RandomEventSystem : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private GameTimer gameTimer;
    [SerializeField] private bool autoFindGameTimer = true;

    [Header("Event Catalog")]
    [SerializeField] private List<RandomEventDefinition> events = new List<RandomEventDefinition>();

    [Header("Global Controls")]
    [Tooltip("If true, only the server/host evaluates and triggers events.")]
    [SerializeField] private bool serverAuthoritative = true;

    [Tooltip("Optional parent transform for spawned event prefabs.")]
    [SerializeField] private Transform eventParent;

    [Tooltip("If true, only one event can trigger per day across all event types.")]
    [SerializeField] private bool oneEventPerDay = true;

    [Header("Warning Indicator")]
    [Tooltip("The persistent EventWarningUI object already in the canvas (disabled by default).")]
    [SerializeField] private EventWarningUI warningUI;

    [Tooltip("How many seconds the warning is shown before the event actually triggers.")]
    [SerializeField] private float warningDurationSeconds = 3f;

    private class EventRuntime
    {
        public RandomEventDefinition def;
        public int triggersThisDay;
        public float nextEligibleTime;     // world time when cooldown ends
        public float nextEvaluationTime;   // world time when next eval can occur
        public bool isPending;             // true while the warning countdown is running
    }

    private readonly List<EventRuntime> runtime = new List<EventRuntime>();
    private GameManager gm;
    private bool anyEventTriggeredToday = false; // NEW: Track if any event has triggered

    private bool IsServerOrStandalone =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        gm = GameManager.Instance;
    }

    private void OnEnable()
    {
        EnsureRefs();
        BuildRuntime();
        SubscribeTimer();
    }

    private void OnDisable()
    {
        UnsubscribeTimer();
    }

    private void EnsureRefs()
    {
        if (gameTimer == null && autoFindGameTimer)
            gameTimer = FindFirstObjectByType<GameTimer>();
    }

    private void SubscribeTimer()
    {
        if (gameTimer == null) return;
        gameTimer.OnDayTimerAboutToExpire += HandleDayEndingSoon;
        gameTimer.OnDayEndedEvaluated += HandleDayEnded;
    }

    private void UnsubscribeTimer()
    {
        if (gameTimer == null) return;
        gameTimer.OnDayTimerAboutToExpire -= HandleDayEndingSoon;
        gameTimer.OnDayEndedEvaluated -= HandleDayEnded;
    }

    private void BuildRuntime()
    {
        runtime.Clear();
        if (events == null) return;

        foreach (var def in events)
        {
            if (def == null) continue;
            runtime.Add(new EventRuntime
            {
                def = def,
                triggersThisDay = 0,
                nextEligibleTime = 0f,
                nextEvaluationTime = 0f,
                isPending = false
            });
        }
    }

    private void Start()
    {
        ResetForNewDay();
    }

    private void Update()
    {
        // Only process when gameplay is active
        if (gm != null && !gm.IsGameplayActive) return;

        // Respect authority
        if (serverAuthoritative && !IsServerOrStandalone) return;

        if (gameTimer == null) return;
        if (!gameTimer.IsRunning) return;
        if (runtime.Count == 0) return;

        // NEW: Skip evaluation if one event per day limit is enabled and already triggered
        if (oneEventPerDay && anyEventTriggeredToday) return;

        float normalizedProgress = gameTimer.Normalized; // 0 at start, 1 at end
        float now = Time.time;

        foreach (var evt in runtime)
        {
            var def = evt.def;
            if (def == null) continue;

            // Skip if already waiting to trigger
            if (evt.isPending) continue;

            // Respect max triggers
            if (evt.triggersThisDay >= Mathf.Max(0, def.maxTriggersPerDay)) continue;

            // Window check
            float start01 = Mathf.Min(def.windowStart01, def.windowEnd01);
            float end01 = Mathf.Max(def.windowStart01, def.windowEnd01);
            bool inWindow = normalizedProgress >= start01 && normalizedProgress <= end01;
            if (!inWindow) continue;

            // Cooldown check
            if (now < evt.nextEligibleTime) continue;

            // Evaluation interval check
            if (now < evt.nextEvaluationTime) continue;

            // Schedule next evaluation
            float interval = Mathf.Max(0.1f, def.evaluationIntervalSeconds);
            evt.nextEvaluationTime = now + interval;

            // Roll chance
            float p = Mathf.Clamp01(def.baseChance);
            float roll = UnityEngine.Random.value;
            bool fired = roll <= p;

            OnEventEvaluated?.Invoke(def, roll, fired);

            if (fired)
            {
                StartCoroutine(WarnThenTrigger(evt));
                
                // NEW: If oneEventPerDay is enabled, stop evaluating other events
                if (oneEventPerDay)
                {
                    anyEventTriggeredToday = true;
                    break; // Exit the foreach loop
                }
            }
        }
    }

    private IEnumerator WarnThenTrigger(EventRuntime evt)
    {
        evt.isPending = true;

        string message = !string.IsNullOrEmpty(evt.def.warningMessage)
            ? evt.def.warningMessage
            : evt.def.eventId;

        // Show warning on all clients (and the host itself)
        if (warningUI != null)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                ShowWarningClientRpc(message);
            else
                warningUI.Show(message); // standalone / editor without NGO
        }

        yield return new WaitForSeconds(warningDurationSeconds);

        TriggerEvent(evt);
        OnEventTriggered?.Invoke(evt.def);

        evt.isPending = false;
    }

    /// <summary>
    /// Runs on every connected client and the host to show the local warning UI.
    /// </summary>
    [ClientRpc]
    private void ShowWarningClientRpc(string message)
    {
        if (warningUI != null)
            warningUI.Show(message);
    }

    private void TriggerEvent(EventRuntime evt)
    {
        var def = evt.def;
        float now = Time.time;

        evt.triggersThisDay += 1;
        evt.nextEligibleTime = now + Mathf.Max(0f, def.cooldownSeconds);

        if (def.eventPrefab != null)
        {
            Instantiate(def.eventPrefab, eventParent);
        }
    }

    private void HandleDayEndingSoon()
    {
        // Optional: cleanup or stop evaluations when the timer is about to hit 0.
    }

    private void HandleDayEnded(bool success)
    {
        ResetForNewDay();
    }

    private void ResetForNewDay()
    {
        float now = Time.time;
        anyEventTriggeredToday = false; // NEW: Reset the daily flag
        
        foreach (var evt in runtime)
        {
            evt.triggersThisDay = 0;
            evt.nextEligibleTime = now;
            evt.nextEvaluationTime = now;
            evt.isPending = false;
        }
    }

    // Diagnostics/UI hooks
    public event Action<RandomEventDefinition, float, bool> OnEventEvaluated; // (def, rollValue, fired)
    public event Action<RandomEventDefinition> OnEventTriggered;

    [ContextMenu("Diagnostics/Force Trigger First Event")]
    private void ForceTriggerFirstEvent()
    {
        if (runtime.Count == 0) return;
        StartCoroutine(WarnThenTrigger(runtime[0]));
    }
}
