using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class RandomEventSystem : MonoBehaviour
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

    private class EventRuntime
    {
        public RandomEventDefinition def;
        public int triggersThisDay;
        public float nextEligibleTime;     // world time when cooldown ends
        public float nextEvaluationTime;   // world time when next eval can occur
    }

    private readonly List<EventRuntime> runtime = new List<EventRuntime>();
    private GameManager gm;

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
                nextEvaluationTime = 0f
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

        float normalizedProgress = gameTimer.Normalized; // 0 at start, 1 at end
        float now = Time.time;

        foreach (var evt in runtime)
        {
            var def = evt.def;
            if (def == null) continue;

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
                TriggerEvent(evt);
                OnEventTriggered?.Invoke(def);
            }
        }
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
        foreach (var evt in runtime)
        {
            evt.triggersThisDay = 0;
            evt.nextEligibleTime = now;
            evt.nextEvaluationTime = now;
        }
    }

    // Diagnostics/UI hooks
    public event Action<RandomEventDefinition, float, bool> OnEventEvaluated; // (def, rollValue, fired)
    public event Action<RandomEventDefinition> OnEventTriggered;

    [ContextMenu("Diagnostics/Force Trigger First Event")]
    private void ForceTriggerFirstEvent()
    {
        if (runtime.Count == 0) return;
        TriggerEvent(runtime[0]);
        OnEventTriggered?.Invoke(runtime[0].def);
    }
}
