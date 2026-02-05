using System;
using UnityEngine;

public class MoneyTargetManager : MonoBehaviour
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

    // Daily earnings (resets each day)
    private int currentMoney;

    // Cumulative money carried across days for spending
    private int bankedMoney;

    private bool targetReached;
    private int currentDay; // Day counter, starts at 0 on Awake. First AdvanceDay() -> day 1.

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

    public int CurrentMoney => currentMoney;
    public int BankedMoney => bankedMoney;
    public float Progress => targetMoney <= 0 ? 1f : Mathf.Clamp01((float)currentMoney / targetMoney);
    public bool IsTargetReached => targetReached;
    public int CurrentDay => currentDay;

    private void Awake()
    {
        // Cache the initial configured target before any resets
        initialTargetMoney = targetMoney;

        ResetProgress();
    }

    public void AddMoney(int amount)
    {
        if (amount == 0) return;

        if (amount < 0)
        {
            RemoveMoney(-amount);
            return;
        }

        long sum = (long)currentMoney + amount;
        int newValue = (int)Mathf.Clamp(sum, 0, int.MaxValue);

        if (newValue == currentMoney) return;

        currentMoney = newValue;
        OnMoneyChanged?.Invoke(currentMoney);
        EvaluateTargetReached();
    }

    public void RemoveMoney(int amount)
    {
        if (amount <= 0) return;

        int newValue = Mathf.Max(0, currentMoney - amount);
        bool wasReached = targetReached;

        if (newValue == currentMoney) return;

        currentMoney = newValue;
        OnMoneyChanged?.Invoke(currentMoney);

        if (wasReached && currentMoney < targetMoney)
        {
            targetReached = false;
        }
    }

    public bool SpendBanked(int amount)
    {
        if (amount <= 0) return false;
        if (bankedMoney < amount) return false;

        bankedMoney -= amount;
        OnBankedMoneyChanged?.Invoke(bankedMoney);
        return true;
    }

    public void SetMoney(int value)
    {
        int newValue = Mathf.Max(0, value);
        if (newValue == currentMoney) return;

        currentMoney = newValue;
        OnMoneyChanged?.Invoke(currentMoney);
        EvaluateTargetReached();
    }

    public void SetBankedMoney(int value)
    {
        int newValue = Mathf.Max(0, value);
        if (newValue == bankedMoney) return;

        bankedMoney = newValue;
        OnBankedMoneyChanged?.Invoke(bankedMoney);
    }

    public void ResetProgress()
    {
        // Restore target to its initial configured value first
        TargetMoney = initialTargetMoney;

        // Reset earnings/banked/day state
        currentMoney = Mathf.Max(0, startingMoney);
        bankedMoney = 0;
        targetReached = false;
        currentDay = 0;

        // Notify listeners for UI sync
        OnMoneyChanged?.Invoke(currentMoney);
        OnBankedMoneyChanged?.Invoke(bankedMoney);

        EvaluateTargetReached();
    }

    public void AdvanceDay()
    {
        AdvanceDays(1);
    }

    public void AdvanceDays(int days)
    {
        if (days <= 0) return;

        for (int i = 0; i < days; i++)
        {
            if (currentMoney > 0)
            {
                long sum = (long)bankedMoney + currentMoney;
                bankedMoney = (int)Mathf.Clamp(sum, 0, int.MaxValue);
                OnDailyEarningsBanked?.Invoke(bankedMoney, currentMoney);
                OnBankedMoneyChanged?.Invoke(bankedMoney);
            }

            currentMoney = 0;
            targetReached = false;
            OnMoneyChanged?.Invoke(currentMoney);

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
        if (!targetReached && (targetMoney == 0 || currentMoney >= targetMoney))
        {
            targetReached = true;
            OnTargetReached?.Invoke();
        }
        else if (targetReached && currentMoney < targetMoney)
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
