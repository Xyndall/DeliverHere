using System;
using UnityEngine;

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

    // Raised when the target is reached for the current day.
    public event Action OnWinCondition;

    // Gameplay state drives cursor lock
    public bool IsGameplayActive { get; private set; }

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
    }

    private void OnEnable()
    {
        SubscribeToMoneyTarget();
        SyncAllUI();

        // Start in menu: gameplay inactive, ensure cursor is free and visible
        IsGameplayActive = false;
        ApplyGameplayCursor();

        // Ensure HUD is hidden at boot; Menu will call StartGame to show it.
        uiController?.HideHUD();
    }

    private void OnDisable()
    {
        UnsubscribeFromMoneyTarget();
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

        // Enter gameplay; lock cursor
        IsGameplayActive = true;
        ApplyGameplayCursor();

        // If the run hasn't started yet, begin Day 1 so systems tied to day state can initialize.
        if (moneyTargetManager != null && moneyTargetManager.CurrentDay <= 0)
        {
            moneyTargetManager.AdvanceDay();
        }

        // Show HUD and sync UI
        SyncAllUI();
        uiController?.HideWinPanel();
        uiController?.HideDayEndSummary();
        uiController?.ShowHUD();
    }

    public void EndGame()
    {
        // Exit gameplay; unlock cursor
        IsGameplayActive = false;
        ApplyGameplayCursor();

        // Reset money/state and hide HUD.
        ResetRun();
        uiController?.HideWinPanel();
        uiController?.HideDayEndSummary();
        uiController?.HideHUD();
    }

    public void ApplyGameplayCursor()
    {
        Cursor.lockState = IsGameplayActive ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !IsGameplayActive;
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
}