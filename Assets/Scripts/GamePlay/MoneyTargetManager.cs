using System;
using Unity.Netcode;
using UnityEngine;

public class MoneyTargetManager : NetworkBehaviour
{
    public enum GrowthMode
    {
        FixedAmount,
        PercentageOfCurrentTarget,
        FixedPlusPercentage
    }

    [Header("Goal Settings")]
    [SerializeField] private int targetMoney = 1000;
    [SerializeField] private int startingMoney = 0;

    [Header("Target Growth Settings")]
    [SerializeField] private bool enableDailyIncrease = false;
    [SerializeField] private GrowthMode growthMode = GrowthMode.FixedAmount;

    [Tooltip("Base flat increase used by FixedAmount or as the base for FixedPlusPercentage.")]
    [SerializeField] private int baseDailyIncrease = 100;

    [Tooltip("0..1 range suggested. Used by Percentage modes.")]
    [Range(0f, 1f)]
    [SerializeField] private float percentDailyIncrease = 0.1f;

    [Tooltip("Optional multiplier curve evaluated by day index (1-based). Toggle with 'useScalingCurve'.")]
    [SerializeField] private bool useScalingCurve = false;
    [SerializeField] private AnimationCurve dailyScalingCurve = AnimationCurve.Linear(1f, 1f, 30f, 1f);

    [Tooltip("Round the computed daily increase to a multiple of this step. 0 or 1 disables rounding.")]
    [SerializeField] private int roundingStep = 0;

    [Tooltip("Hard cap for TargetMoney after increases. 0 means no cap.")]
    [SerializeField] private int maxTargetMoneyCap = 0;

    // Replicated money values (server authoritative)
    private readonly NetworkVariable<int> nvCurrentMoney =
        new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<int> nvBankedMoney =
        new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private bool targetReached;
    private int currentDay; // Still local/server-driven in this class (NetworkGameState mirrors it anyway)

    // Cache of the initial target to restore on ResetProgress
    private int initialTargetMoney;

    // Events for other systems (e.g., GameManager/UI)
    public event Action<int> OnMoneyChanged;
    public event Action<int> OnBankedMoneyChanged;
    public event Action<int, int> OnDailyEarningsBanked;
    public event Action<int> OnTargetChanged;
    public event Action OnTargetReached;
    public event Action<int> OnDayAdvanced;
    public event Action<int, int> OnTargetIncreased;

    public int TargetMoney
    {
        get => targetMoney;
        set
        {
            int newTarget = Mathf.Max(0, value);
            if (targetMoney == newTarget) return;

            targetMoney = newTarget;
            OnTargetChanged?.Invoke(targetMoney);
            EvaluateTargetReached();
        }
    }

    public int CurrentMoney => nvCurrentMoney.Value;
    public int BankedMoney => nvBankedMoney.Value;
    public float Progress => targetMoney <= 0 ? 1f : Mathf.Clamp01((float)CurrentMoney / targetMoney);
    public bool IsTargetReached => targetReached;
    public int CurrentDay => currentDay;

    private void Awake()
    {
        // Cache the initial configured target before any resets
        initialTargetMoney = targetMoney;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        nvCurrentMoney.OnValueChanged += OnNvCurrentMoneyChanged;
        nvBankedMoney.OnValueChanged += OnNvBankedMoneyChanged;

        // Push initial state to listeners (useful for late join + UI init)
        OnMoneyChanged?.Invoke(nvCurrentMoney.Value);
        OnBankedMoneyChanged?.Invoke(nvBankedMoney.Value);

        // Only the server should run initial reset to avoid clients attempting to write server-only NVs.
        if (IsServer)
            ResetProgress();
    }

    public override void OnNetworkDespawn()
    {
        nvCurrentMoney.OnValueChanged -= OnNvCurrentMoneyChanged;
        nvBankedMoney.OnValueChanged -= OnNvBankedMoneyChanged;

        base.OnNetworkDespawn();
    }

    private void OnNvCurrentMoneyChanged(int previous, int current)
    {
        OnMoneyChanged?.Invoke(current);
        EvaluateTargetReached();
    }

    private void OnNvBankedMoneyChanged(int previous, int current)
    {
        OnBankedMoneyChanged?.Invoke(current);
    }

    public void AddMoney(int amount)
    {
        if (!IsServer) return;
        if (amount == 0) return;

        if (amount < 0)
        {
            RemoveMoney(-amount);
            return;
        }

        long sum = (long)nvCurrentMoney.Value + amount;
        int newValue = (int)Mathf.Clamp(sum, 0, int.MaxValue);

        if (newValue == nvCurrentMoney.Value) return;

        nvCurrentMoney.Value = newValue;
        // Events fire through OnValueChanged callback.
    }

    public void RemoveMoney(int amount)
    {
        if (!IsServer) return;
        if (amount <= 0) return;

        int newValue = Mathf.Max(0, nvCurrentMoney.Value - amount);
        bool wasReached = targetReached;

        if (newValue == nvCurrentMoney.Value) return;

        nvCurrentMoney.Value = newValue;

        if (wasReached && nvCurrentMoney.Value < targetMoney)
        {
            targetReached = false;
        }
    }

    public bool SpendBanked(int amount)
    {
        if (!IsServer) return false;
        if (amount <= 0) return false;
        if (nvBankedMoney.Value < amount) return false;

        nvBankedMoney.Value -= amount;
        return true;
    }

    public void SetMoney(int value)
    {
        if (!IsServer) return;

        int newValue = Mathf.Max(0, value);
        if (newValue == nvCurrentMoney.Value) return;

        nvCurrentMoney.Value = newValue;
    }

    public void SetBankedMoney(int value)
    {
        if (!IsServer) return;

        int newValue = Mathf.Max(0, value);
        if (newValue == nvBankedMoney.Value) return;

        nvBankedMoney.Value = newValue;
    }

    public void ResetProgress()
    {
        if (!IsServer) return;

        // Restore target to its initial configured value first
        TargetMoney = initialTargetMoney;

        nvCurrentMoney.Value = Mathf.Max(0, startingMoney);
        nvBankedMoney.Value = 0;

        targetReached = false;
        currentDay = 0;

        EvaluateTargetReached();
    }

    public void AdvanceDay()
    {
        AdvanceDays(1);
    }

    public void AdvanceDays(int days)
    {
        if (!IsServer) return;
        if (days <= 0) return;

        for (int i = 0; i < days; i++)
        {
            if (nvCurrentMoney.Value > 0)
            {
                long sum = (long)nvBankedMoney.Value + nvCurrentMoney.Value;
                nvBankedMoney.Value = (int)Mathf.Clamp(sum, 0, int.MaxValue);

                OnDailyEarningsBanked?.Invoke(nvBankedMoney.Value, nvCurrentMoney.Value);
                // OnBankedMoneyChanged fires via NV callback.
            }

            nvCurrentMoney.Value = 0;
            targetReached = false;
            // OnMoneyChanged fires via NV callback.

            currentDay++;

            if (enableDailyIncrease)
            {
                int delta = CalculateDailyIncrease(currentDay);
                if (delta > 0)
                {
                    int newTarget = targetMoney + delta;

                    if (maxTargetMoneyCap > 0)
                        newTarget = Mathf.Min(newTarget, maxTargetMoneyCap);

                    if (newTarget != targetMoney)
                    {
                        TargetMoney = newTarget;
                        OnTargetIncreased?.Invoke(TargetMoney, delta);
                    }
                }
            }

            OnDayAdvanced?.Invoke(currentDay);
        }
    }

    private int CalculateDailyIncrease(int dayIndex1Based)
    {
        float scale = 1f;
        if (useScalingCurve && dailyScalingCurve != null)
        {
            scale = Mathf.Max(0f, dailyScalingCurve.Evaluate(dayIndex1Based));
        }

        float amount;
        switch (growthMode)
        {
            case GrowthMode.FixedAmount:
                amount = baseDailyIncrease * scale;
                break;
            case GrowthMode.PercentageOfCurrentTarget:
                amount = targetMoney * percentDailyIncrease * scale;
                break;
            case GrowthMode.FixedPlusPercentage:
                amount = (baseDailyIncrease + targetMoney * percentDailyIncrease) * scale;
                break;
            default:
                amount = 0f;
                break;
        }

        int delta = Mathf.Max(0, Mathf.RoundToInt(amount));
        if (roundingStep > 1)
        {
            delta = RoundToStep(delta, roundingStep);
        }
        return delta;
    }

    private static int RoundToStep(int value, int step)
    {
        if (step <= 1) return value;
        return Mathf.RoundToInt(value / (float)step) * step;
    }

    private void EvaluateTargetReached()
    {
        if (!targetReached && (targetMoney == 0 || nvCurrentMoney.Value >= targetMoney))
        {
            targetReached = true;
            OnTargetReached?.Invoke();
        }
        else if (targetReached && nvCurrentMoney.Value < targetMoney)
        {
            targetReached = false;
        }
    }

    // Editor convenience to test day progression
    [ContextMenu("Debug/Advance One Day")]
    private void Debug_AdvanceOneDay()
    {
        AdvanceDay();
    }
}
