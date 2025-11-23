using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using DeliverHere.GamePlay;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Lifecycle")]
    [SerializeField] private bool dontDestroyOnLoad = true;

    [Header("References")]
    [SerializeField] private MoneyTargetManager moneyTargetManager;
    [SerializeField] private bool autoFindMoneyTargetManager = true;
    [SerializeField] private GameUIController uiController;
    [SerializeField] private bool autoFindUIController = true;

    [Header("Package Spawning")]
    [SerializeField] private bool autoFindPackageSpawners = true;
    [SerializeField] private List<PackageSpawner> packageSpawners = new List<PackageSpawner>();
    [SerializeField] private int baseMinPackages = 5;
    [SerializeField] private int baseMaxPackages = 12;
    [SerializeField] private int minIncreasePerDay = 1;
    [SerializeField] private int maxIncreasePerDay = 2;
    [SerializeField] private int dailyHardCap = 100;

    [Header("Spawn Safety")]
    [SerializeField] private bool preventDuplicateSpawnsPerDay = true;

    public event Action OnWinCondition;
    public bool IsGameplayActive { get; private set; }

    private InputSystem_Actions _input;
    private InputAction _pauseAction;

    private int lastSpawnedDay = -1;
    private int dailyPackagesDelivered = 0;
    private int currentDayTargetMoney = 0;
    private bool endOfDayPopupShown = false;

    private DayNightCycle _dayNight;

    private bool IsServerOrStandalone =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        if (dontDestroyOnLoad)
            DontDestroyOnLoad(gameObject);

        EnsureMoneyTargetReference();
        EnsureUIReference();
        EnsureSpawnerReferences();
    }

    private void OnEnable()
    {
        EnsureMoneyTargetReference();
        EnsureUIReference();
        EnsureSpawnerReferences();

        SubscribeToMoneyTarget();
        FindDayNightAndSubscribe();
        SyncAllUI();

        IsGameplayActive = false;

        if (_input == null)
            _input = new InputSystem_Actions();

        _pauseAction = _input.Player.Pause;
        _pauseAction.performed += OnPausePerformed;

        _input.Enable();
        _pauseAction.Enable();

        uiController?.HideHUD();
        uiController?.ConfigureDayEndButtons(HostAdvanceToNextDay, HostRestartRun);
    }

    private void OnDisable()
    {
        UnsubscribeFromMoneyTarget();
        UnsubscribeDayNight();

        if (_pauseAction != null)
            _pauseAction.performed -= OnPausePerformed;

        _input?.Disable();
        _input?.Dispose();
        _input = null;
        _pauseAction = null;
    }

    private void FindDayNightAndSubscribe()
    {
        if (_dayNight == null)
            _dayNight = FindFirstObjectByType<DayNightCycle>();
        if (_dayNight != null)
            _dayNight.OnDayEndedEvaluated += HandleDayEndedEvaluated;
    }

    private void UnsubscribeDayNight()
    {
        if (_dayNight != null)
            _dayNight.OnDayEndedEvaluated -= HandleDayEndedEvaluated;
    }

    private void EnsureMoneyTargetReference()
    {
        if (moneyTargetManager == null && autoFindMoneyTargetManager)
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();
    }

    private void EnsureUIReference()
    {
        if (uiController == null && autoFindUIController)
            uiController = FindFirstObjectByType<GameUIController>();
    }

    private void EnsureSpawnerReferences()
    {
        if (!autoFindPackageSpawners) return;
        var found = FindObjectsByType<PackageSpawner>(FindObjectsSortMode.None);
        packageSpawners.Clear();
        packageSpawners.AddRange(found);
    }

    private void SubscribeToMoneyTarget()
    {
        if (moneyTargetManager == null) return;

        moneyTargetManager.OnMoneyChanged += HandleMoneyChanged;
        moneyTargetManager.OnBankedMoneyChanged += HandleBankedMoneyChanged;
        moneyTargetManager.OnDailyEarningsBanked += HandleDailyEarningsBanked;
        moneyTargetManager.OnTargetChanged += HandleTargetChanged;
        moneyTargetManager.OnTargetIncreased += HandleTargetIncreased;
        moneyTargetManager.OnDayAdvanced += HandleDayAdvanced;
        moneyTargetManager.OnTargetReached += HandleTargetReached;

        currentDayTargetMoney = moneyTargetManager.TargetMoney;
    }

    private void UnsubscribeFromMoneyTarget()
    {
        if (moneyTargetManager == null) return;

        moneyTargetManager.OnMoneyChanged -= HandleMoneyChanged;
        moneyTargetManager.OnBankedMoneyChanged -= HandleBankedMoneyChanged;
        moneyTargetManager.OnDailyEarningsBanked -= HandleDailyEarningsBanked;
        moneyTargetManager.OnTargetChanged -= HandleTargetChanged;
        moneyTargetManager.OnTargetIncreased -= HandleTargetIncreased;
        moneyTargetManager.OnDayAdvanced -= HandleDayAdvanced;
        moneyTargetManager.OnTargetReached -= HandleTargetReached;
    }

    private void SyncAllUI()
    {
        if (uiController == null || moneyTargetManager == null) return;
        uiController.SetDay(GetCurrentDay());
        uiController.SetTarget(GetTargetMoney());
        uiController.SetDailyEarnings(GetCurrentMoney(), GetTargetMoney(), GetProgress());
        uiController.SetBankedMoney(GetBankedMoney());
    }

    public void StartGame()
    {
        EnsureMoneyTargetReference();
        EnsureUIReference();
        EnsureSpawnerReferences();

        IsGameplayActive = true;

        if (moneyTargetManager != null && moneyTargetManager.CurrentDay <= 0 && IsServerOrStandalone)
        {
            moneyTargetManager.AdvanceDay();
        }
        else if (moneyTargetManager != null && moneyTargetManager.CurrentDay > 0 && IsServerOrStandalone)
        {
            TrySpawnPackagesForDay(moneyTargetManager.CurrentDay);
        }

        dailyPackagesDelivered = 0;
        currentDayTargetMoney = GetTargetMoney();
        endOfDayPopupShown = false;

        SyncAllUI();
        uiController?.HideWinPanel();
        uiController?.HideDayEndSummary();
        uiController?.ShowHUD();
    }

    public void EndGame()
    {
        IsGameplayActive = false;
        lastSpawnedDay = -1;
        ResetRun();
        dailyPackagesDelivered = 0;
        endOfDayPopupShown = false;

        uiController?.HideWinPanel();
        uiController?.HideDayEndSummary();
        uiController?.HideHUD();
    }

    public void RegisterPackagesDelivered(int count)
    {
        if (count <= 0) return;
        dailyPackagesDelivered += count;
    }

    public void HostAdvanceToNextDay()
    {
        if (!IsServerOrStandalone) return;
        uiController?.HideDayEndSummary();
        endOfDayPopupShown = false;
        moneyTargetManager?.AdvanceDay();
    }

    public void HostRestartRun()
    {
        if (!IsServerOrStandalone) return;
        uiController?.HideDayEndSummary();
        ResetRun();
        dailyPackagesDelivered = 0;
        lastSpawnedDay = -1;
        endOfDayPopupShown = false;
        moneyTargetManager?.AdvanceDay();
    }

    private void OnPausePerformed(InputAction.CallbackContext ctx) => ToggleCursorVisibility();

    public void ToggleCursorVisibility()
    {
        bool show = !Cursor.visible;
        Cursor.visible = show;
        Cursor.lockState = show ? CursorLockMode.None : CursorLockMode.Locked;
    }

    public void AddMoney(int amount) => moneyTargetManager?.AddMoney(amount);
    public void RemoveMoney(int amount) => moneyTargetManager?.RemoveMoney(amount);
    public bool SpendBanked(int amount) => moneyTargetManager != null && moneyTargetManager.SpendBanked(amount);
    public void AdvanceDay() => moneyTargetManager?.AdvanceDay();
    public void AdvanceDays(int days) => moneyTargetManager?.AdvanceDays(days);

    public void ResetRun()
    {
        moneyTargetManager?.ResetProgress();
        currentDayTargetMoney = GetTargetMoney();
    }

    public void SetTargetMoney(int value)
    {
        if (moneyTargetManager == null) return;
        moneyTargetManager.TargetMoney = value;
        uiController?.SetTarget(value);
        uiController?.SetDailyEarnings(GetCurrentMoney(), value, GetProgress());
        currentDayTargetMoney = value;
    }

    public int PreviewIncreaseForNextDay() => moneyTargetManager != null ? moneyTargetManager.PreviewIncreaseForNextDay() : 0;

    public int GetCurrentMoney() => moneyTargetManager != null ? moneyTargetManager.CurrentMoney : 0;
    public int GetBankedMoney() => moneyTargetManager != null ? moneyTargetManager.BankedMoney : 0;
    public int GetTargetMoney() => moneyTargetManager != null ? moneyTargetManager.TargetMoney : 0;
    public int GetCurrentDay() => moneyTargetManager != null ? moneyTargetManager.CurrentDay : 0;
    public float GetProgress() => moneyTargetManager != null ? moneyTargetManager.Progress : 0f;

    private void HandleMoneyChanged(int newAmount)
    {
        uiController?.SetDailyEarnings(newAmount, GetTargetMoney(), GetProgress());
    }

    private void HandleBankedMoneyChanged(int newBanked)
    {
        uiController?.SetBankedMoney(newBanked);
    }

    private void HandleDailyEarningsBanked(int newBanked, int amountAdded)
    {
        // Only show summary here if we did NOT already show it at timer end.
        if (endOfDayPopupShown) return;

        bool metQuota = amountAdded >= currentDayTargetMoney;
        bool hostControls = IsServerOrStandalone;
        uiController?.ShowDayEndSummary(amountAdded, newBanked, dailyPackagesDelivered, metQuota, hostControls);
        dailyPackagesDelivered = 0;
        endOfDayPopupShown = true;
    }

    private void HandleTargetChanged(int newTarget)
    {
        uiController?.SetTarget(newTarget);
        uiController?.SetDailyEarnings(GetCurrentMoney(), newTarget, GetProgress());
        currentDayTargetMoney = newTarget;
    }

    private void HandleTargetIncreased(int newTarget, int deltaAdded)
    {
        uiController?.ShowTargetIncrease(newTarget, deltaAdded);
        currentDayTargetMoney = newTarget;
    }

    private void HandleDayAdvanced(int newDayIndex)
    {
        uiController?.SetDay(newDayIndex);
        uiController?.SetDailyEarnings(GetCurrentMoney(), GetTargetMoney(), GetProgress());
        currentDayTargetMoney = GetTargetMoney();
        dailyPackagesDelivered = 0;
        endOfDayPopupShown = false;

        if (IsServerOrStandalone)
            TrySpawnPackagesForDay(newDayIndex);
    }

    private void HandleTargetReached()
    {
        TriggerWinCondition();
    }

    private void HandleDayEndedEvaluated(bool success)
    {
        // Show popup immediately when timer ends
        int earnedToday = GetCurrentMoney();
        int previewBankedTotal = GetBankedMoney() + earnedToday; // not yet banked
        bool hostControls = IsServerOrStandalone;

        uiController?.ShowDayEndSummary(earnedToday, previewBankedTotal, dailyPackagesDelivered, success, hostControls);
        endOfDayPopupShown = true;

        if (!success)
        {
            // On failure we halt gameplay but keep summary visible.
            IsGameplayActive = false;
        }
        else
        {
            // On success gameplay pauses awaiting host button.
            IsGameplayActive = false;
        }
    }

    private void TriggerWinCondition()
    {
        OnWinCondition?.Invoke();
        uiController?.ShowWinPanel();
        Debug.Log("[GameManager] Win condition met.");
    }

    public void SetUIController(GameUIController controller)
    {
        uiController = controller;
        uiController?.ConfigureDayEndButtons(HostAdvanceToNextDay, HostRestartRun);
        SyncAllUI();
    }

    public void SetMoneyTargetManager(MoneyTargetManager manager)
    {
        if (moneyTargetManager == manager) return;
        UnsubscribeFromMoneyTarget();
        moneyTargetManager = manager;
        SubscribeToMoneyTarget();
        SyncAllUI();
    }

    private void TrySpawnPackagesForDay(int dayIndex)
    {
        if (preventDuplicateSpawnsPerDay && lastSpawnedDay == dayIndex)
            return;

        lastSpawnedDay = dayIndex;
        SpawnPackagesForDay(dayIndex);
    }

    private void SpawnPackagesForDay(int dayIndex)
    {
        if (packageSpawners == null) return;

        var active = new List<PackageSpawner>();
        foreach (var s in packageSpawners)
        {
            if (s != null && s.isActiveAndEnabled)
                active.Add(s);
        }
        if (active.Count == 0) return;

        int dayOffset = Mathf.Max(0, dayIndex - 1);
        int minForDay = Mathf.Max(0, baseMinPackages + minIncreasePerDay * dayOffset);
        int maxForDay = Mathf.Max(minForDay, baseMaxPackages + maxIncreasePerDay * dayOffset);
        maxForDay = Mathf.Min(maxForDay, Mathf.Max(0, dailyHardCap));

        int totalToSpawn = UnityEngine.Random.Range(minForDay, maxForDay + 1);

        int baseEach = totalToSpawn / active.Count;
        int remainder = totalToSpawn % active.Count;

        int spawnedTotal = 0;
        for (int i = 0; i < active.Count; i++)
        {
            int toSpawn = baseEach + (i < remainder ? 1 : 0);
            if (toSpawn <= 0) continue;
            spawnedTotal += active[i].SpawnCount(toSpawn);
        }

        if (spawnedTotal < totalToSpawn)
        {
            Debug.LogWarning($"[GameManager] Requested total {totalToSpawn} across {active.Count} spawners, actually spawned {spawnedTotal}.");
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        baseMinPackages = Mathf.Max(0, baseMinPackages);
        baseMaxPackages = Mathf.Max(baseMinPackages, baseMaxPackages);
        minIncreasePerDay = Mathf.Max(0, minIncreasePerDay);
        maxIncreasePerDay = Mathf.Max(0, maxIncreasePerDay);
        dailyHardCap = Mathf.Max(0, dailyHardCap);
    }
#endif
}