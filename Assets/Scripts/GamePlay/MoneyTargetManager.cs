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

    [Tooltip("Initial banked money at the start of a run.")]
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

    // Replicated money value (server authoritative)
    private readonly NetworkVariable<int> nvBankedMoney =
        new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private bool targetReached;
    private int currentDay;

    private int initialTargetMoney;

    // Events for other systems (e.g., GameManager/UI)
    public event Action<int> OnBankedMoneyChanged;
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

    // CurrentMoney removed; banked money is the only currency.
    public int BankedMoney => nvBankedMoney.Value;

    public float Progress => targetMoney <= 0 ? 1f : Mathf.Clamp01((float)BankedMoney / targetMoney);
    public bool IsTargetReached => targetReached;
    public int CurrentDay => currentDay;

    private void Awake()
    {
        initialTargetMoney = targetMoney;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        nvBankedMoney.OnValueChanged += OnNvBankedMoneyChanged;

        // Push initial state to listeners
        OnBankedMoneyChanged?.Invoke(nvBankedMoney.Value);

        if (IsServer)
            ResetProgress();
    }

    public override void OnNetworkDespawn()
    {
        nvBankedMoney.OnValueChanged -= OnNvBankedMoneyChanged;
        base.OnNetworkDespawn();
    }

    private void OnNvBankedMoneyChanged(int previous, int current)
    {
        OnBankedMoneyChanged?.Invoke(current);
        EvaluateTargetReached();
    }

    /// <summary>
    /// Server: Adds directly to banked money (single-currency model).
    /// </summary>
    public void AddMoney(int amount)
    {
        if (!IsServer) return;
        if (amount == 0) return;

        if (amount < 0)
        {
            RemoveMoney(-amount);
            return;
        }

        long sum = (long)nvBankedMoney.Value + amount;
        int newValue = (int)Mathf.Clamp(sum, 0, int.MaxValue);

        if (newValue == nvBankedMoney.Value) return;

        nvBankedMoney.Value = newValue;
    }

    /// <summary>
    /// Server: Removes directly from banked money.
    /// </summary>
    public void RemoveMoney(int amount)
    {
        if (!IsServer) return;
        if (amount <= 0) return;

        int newValue = Mathf.Max(0, nvBankedMoney.Value - amount);
        if (newValue == nvBankedMoney.Value) return;

        nvBankedMoney.Value = newValue;
    }

    public bool SpendBanked(int amount)
    {
        if (!IsServer) return false;
        if (amount <= 0) return false;
        if (nvBankedMoney.Value < amount) return false;

        nvBankedMoney.Value -= amount;
        return true;
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

        TargetMoney = initialTargetMoney;

        nvBankedMoney.Value = Mathf.Max(0, startingMoney);

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
            scale = Mathf.Max(0f, dailyScalingCurve.Evaluate(dayIndex1Based));

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
            delta = RoundToStep(delta, roundingStep);

        return delta;
    }

    private static int RoundToStep(int value, int step)
    {
        if (step <= 1) return value;
        return Mathf.RoundToInt(value / (float)step) * step;
    }

    private void EvaluateTargetReached()
    {
        if (!targetReached && (targetMoney == 0 || nvBankedMoney.Value >= targetMoney))
        {
            targetReached = true;
            OnTargetReached?.Invoke();
        }
        else if (targetReached && nvBankedMoney.Value < targetMoney)
        {
            targetReached = false;
        }
    }

    [ContextMenu("Debug/Advance One Day")]
    private void Debug_AdvanceOneDay()
    {
        AdvanceDay();
    }
}
