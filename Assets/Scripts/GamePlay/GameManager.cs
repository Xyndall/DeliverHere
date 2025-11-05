using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using DeliverHere.GamePlay;

public class GameManager : MonoBehaviour
{
    // Singleton
    public static GameManager Instance { get; private set; }

    [Header("Lifecycle")]
    [SerializeField] private bool dontDestroyOnLoad = true;

    [Header("References")]
    [SerializeField] private MoneyTargetManager moneyTargetManager;
    [SerializeField] private bool autoFindMoneyTargetManager = true;
    [SerializeField] private GameUIController uiController;
    [SerializeField] private bool autoFindUIController = true;

    [Header("Package Spawning")]
    [Tooltip("Auto-find all PackageSpawner components in the scene each time this enables.")]
    [SerializeField] private bool autoFindPackageSpawners = true;
    [Tooltip("Optional explicit list of spawners. Leave empty and enable Auto Find to discover automatically.")]
    [SerializeField] private List<PackageSpawner> packageSpawners = new List<PackageSpawner>();
    [Tooltip("Base min package count for Day 1.")]
    [SerializeField] private int baseMinPackages = 5;
    [Tooltip("Base max package count for Day 1.")]
    [SerializeField] private int baseMaxPackages = 12;
    [Tooltip("Increase applied to min packages each day after Day 1.")]
    [SerializeField] private int minIncreasePerDay = 1;
    [Tooltip("Increase applied to max packages each day after Day 1.")]
    [SerializeField] private int maxIncreasePerDay = 2;
    [Tooltip("Hard cap for daily maximum to avoid runaway growth.")]
    [SerializeField] private int dailyHardCap = 100;

    [Header("Spawn Safety")]
    [Tooltip("Prevents spawning more than once for the same day index.")]
    [SerializeField] private bool preventDuplicateSpawnsPerDay = true;

    // Raised when the target is reached for the current day.
    public event Action OnWinCondition;

    // Gameplay state (no longer controls cursor)
    public bool IsGameplayActive { get; private set; }

    // Input
    private InputSystem_Actions _input;
    private InputAction _pauseAction;

    private int lastSpawnedDay = -1;

    private bool IsServerOrStandalone =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        // Singleton setup
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
        SyncAllUI();

        // Start in menu: gameplay inactive
        IsGameplayActive = false;

        // Input setup for Pause -> toggle cursor visibility
        if (_input == null)
            _input = new InputSystem_Actions();

        _pauseAction = _input.Player.Pause;
        _pauseAction.performed += OnPausePerformed;

        _input.Enable();
        _pauseAction.Enable();

        // Ensure HUD is hidden at boot; Menu will call StartGame to show it.
        uiController?.HideHUD();
    }

    private void OnDisable()
    {
        UnsubscribeFromMoneyTarget();

        if (_pauseAction != null)
            _pauseAction.performed -= OnPausePerformed;

        _input?.Disable();
        _input?.Dispose();
        _input = null;
        _pauseAction = null;
    }

    private void EnsureMoneyTargetReference()
    {
        if (moneyTargetManager == null && autoFindMoneyTargetManager)
        {
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();
        }
    }

    private void EnsureUIReference()
    {
        if (uiController == null && autoFindUIController)
        {
            uiController = FindFirstObjectByType<GameUIController>();
        }
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

    // ---- Public API for other systems ----

    public void StartGame()
    {
        // Ensure refs in case this is called very early
        EnsureMoneyTargetReference();
        EnsureUIReference();
        EnsureSpawnerReferences();

        // Enter gameplay
        IsGameplayActive = true;

        // If the run hasn't started yet, only the server begins Day 1.
        // IMPORTANT: use else-if to avoid spawning twice on day start.
        if (moneyTargetManager != null && moneyTargetManager.CurrentDay <= 0 &&
            IsServerOrStandalone)
        {
            moneyTargetManager.AdvanceDay(); // HandleDayAdvanced will spawn
        }
        else if (moneyTargetManager != null && moneyTargetManager.CurrentDay > 0 && IsServerOrStandalone)
        {
            TrySpawnPackagesForDay(moneyTargetManager.CurrentDay);
        }

        // Show HUD and sync UI
        SyncAllUI();
        uiController?.HideWinPanel();
        uiController?.HideDayEndSummary();
        uiController?.ShowHUD();
    }

    public void EndGame()
    {
        // Exit gameplay
        IsGameplayActive = false;

        // Reset guard and money/state, hide HUD.
        lastSpawnedDay = -1;
        ResetRun();
        uiController?.HideWinPanel();
        uiController?.HideDayEndSummary();
        uiController?.HideHUD();
    }

    // Toggle cursor on Pause press
    private void OnPausePerformed(InputAction.CallbackContext ctx)
    {
        ToggleCursorVisibility();
    }

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
    public void ResetRun() => moneyTargetManager?.ResetProgress();

    public void SetTargetMoney(int value)
    {
        if (moneyTargetManager == null) return;
        moneyTargetManager.TargetMoney = value;
        uiController?.SetTarget(value);
        uiController?.SetDailyEarnings(GetCurrentMoney(), value, GetProgress());
    }

    public int PreviewIncreaseForNextDay() => moneyTargetManager != null ? moneyTargetManager.PreviewIncreaseForNextDay() : 0;

    // Convenience getters
    public int GetCurrentMoney() => moneyTargetManager != null ? moneyTargetManager.CurrentMoney : 0;
    public int GetBankedMoney() => moneyTargetManager != null ? moneyTargetManager.BankedMoney : 0;
    public int GetTargetMoney() => moneyTargetManager != null ? moneyTargetManager.TargetMoney : 0;
    public int GetCurrentDay() => moneyTargetManager != null ? moneyTargetManager.CurrentDay : 0;
    public float GetProgress() => moneyTargetManager != null ? moneyTargetManager.Progress : 0f;

    // ---- Event handlers ----

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
        uiController?.ShowDayEndSummary(amountAdded, newBanked);
    }

    private void HandleTargetChanged(int newTarget)
    {
        uiController?.SetTarget(newTarget);
        uiController?.SetDailyEarnings(GetCurrentMoney(), newTarget, GetProgress());
    }

    private void HandleTargetIncreased(int newTarget, int deltaAdded)
    {
        uiController?.ShowTargetIncrease(newTarget, deltaAdded);
    }

    private void HandleDayAdvanced(int newDayIndex)
    {
        uiController?.SetDay(newDayIndex);
        uiController?.SetDailyEarnings(GetCurrentMoney(), GetTargetMoney(), GetProgress());

        // Server/host spawns packages at the start of each new day
        if (IsServerOrStandalone)
        {
            TrySpawnPackagesForDay(newDayIndex);
        }
    }

    private void HandleTargetReached()
    {
        TriggerWinCondition();
    }
       
    private void TriggerWinCondition()
    {
        OnWinCondition?.Invoke();
        uiController?.ShowWinPanel();
        Debug.Log("[GameManager] Win condition met.");
    }

    // ---- Optional setters ----
    public void SetUIController(GameUIController controller)
    {
        uiController = controller;
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

    // ---------- Package spawn control ----------

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

        // Filter active spawners
        var active = new List<PackageSpawner>();
        foreach (var s in packageSpawners)
        {
            if (s != null && s.isActiveAndEnabled)
                active.Add(s);
        }
        if (active.Count == 0) return;

        // Day 1 uses base values. Day 2 adds one step, etc.
        int dayOffset = Mathf.Max(0, dayIndex - 1);
        int minForDay = Mathf.Max(0, baseMinPackages + minIncreasePerDay * dayOffset);
        int maxForDay = Mathf.Max(minForDay, baseMaxPackages + maxIncreasePerDay * dayOffset);
        maxForDay = Mathf.Min(maxForDay, Mathf.Max(0, dailyHardCap));

        // Choose a single global count and distribute across spawners
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