using System;
using UnityEngine;

public class MoneyTargetManager : MonoBehaviour
{
    public enum GrowthMode
    {
        FixedAmount,                 // Adds a flat amount each day.
        PercentageOfCurrentTarget,   // Adds a percentage of the current target.
        FixedPlusPercentage          // Adds (fixed + percentage) per day.
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

    // Events for other systems (e.g., GameManager/UI)
    public event Action<int> OnMoneyChanged;         // daily earnings changed
    public event Action<int> OnBankedMoneyChanged;   // banked money changed
    public event Action<int, int> OnDailyEarningsBanked; // (newBanked, amountAdded)
    public event Action<int> OnTargetChanged;        // target changed
    public event Action OnTargetReached;             // fired once when daily earnings reach target
    public event Action<int> OnDayAdvanced;          // new current day index (1-based after first advance)
    public event Action<int, int> OnTargetIncreased; // (newTarget, deltaAdded)

    public int TargetMoney
    {
        get => targetMoney;
        set
        {
            int newTarget = Mathf.Max(0, value);
            if (targetMoney == newTarget) return;

            targetMoney = newTarget;
            OnTargetChanged?.Invoke(targetMoney);

            // Re-evaluate in case changing the target immediately satisfies/fails it
            EvaluateTargetReached();
        }
    }

    // Daily earnings toward today's target
    public int CurrentMoney => currentMoney;

    // Total spendable currency
    public int BankedMoney => bankedMoney;

    public float Progress => targetMoney <= 0 ? 1f : Mathf.Clamp01((float)currentMoney / targetMoney);
    public bool IsTargetReached => targetReached;
    public int CurrentDay => currentDay;

    private void Awake()
    {
        ResetProgress();
    }

    // Call this from your future money-earning logic (adds to today's earnings; not clamped to target)
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

    // Optional: penalties that reduce today's earnings
    public void RemoveMoney(int amount)
    {
        if (amount <= 0) return;

        int newValue = Mathf.Max(0, currentMoney - amount);
        bool wasReached = targetReached;

        if (newValue == currentMoney) return;

        currentMoney = newValue;
        OnMoneyChanged?.Invoke(currentMoney);

        // If daily earnings drop below target, allow re-reaching
        if (wasReached && currentMoney < targetMoney)
        {
            targetReached = false;
        }
    }

    // Spend from banked money; returns true if successful
    public bool SpendBanked(int amount)
    {
        if (amount <= 0) return false;
        if (bankedMoney < amount) return false;

        bankedMoney -= amount;
        OnBankedMoneyChanged?.Invoke(bankedMoney);
        return true;
    }

    // Directly set daily earnings (useful for debugging)
    public void SetMoney(int value)
    {
        int newValue = Mathf.Max(0, value);
        if (newValue == currentMoney) return;

        currentMoney = newValue;
        OnMoneyChanged?.Invoke(currentMoney);
        EvaluateTargetReached();
    }

    // Directly set banked (debug/admin)
    public void SetBankedMoney(int value)
    {
        int newValue = Mathf.Max(0, value);
        if (newValue == bankedMoney) return;

        bankedMoney = newValue;
        OnBankedMoneyChanged?.Invoke(bankedMoney);
    }

    public void ResetProgress()
    {
        currentMoney = Mathf.Max(0, startingMoney);
        bankedMoney = 0;
        targetReached = false;
        currentDay = 0;
        OnMoneyChanged?.Invoke(currentMoney);
        OnBankedMoneyChanged?.Invoke(bankedMoney);
        EvaluateTargetReached();
    }

    // Advance the in-game day by 1: bank today's earnings, reset daily, then optionally increase target.
    public void AdvanceDay()
    {
        AdvanceDays(1);
    }

    // Advance several days at once (applies: bank -> reset -> increase target -> advance event).
    public void AdvanceDays(int days)
    {
        if (days <= 0) return;

        for (int i = 0; i < days; i++)
        {
            // 1) Bank today's earnings and reset daily
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

            // 2) Advance day counter
            currentDay++;

            // 3) Increase target for the new day if enabled
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
                        TargetMoney = newTarget; // triggers OnTargetChanged + re-evaluation
                        OnTargetIncreased?.Invoke(TargetMoney, delta);
                    }
                }
            }

            // 4) Notify listeners day has advanced
            OnDayAdvanced?.Invoke(currentDay);
        }
    }

    // Peek at what the next day’s increase would be (considering caps and rounding).
    public int PreviewIncreaseForNextDay()
    {
        int nextDay = currentDay + 1;
        int inc = CalculateDailyIncrease(nextDay);

        if (maxTargetMoneyCap > 0)
            inc = Mathf.Clamp(inc, 0, Math.Max(0, maxTargetMoneyCap - targetMoney));

        return inc;
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
        // Target of 0 means instantly "reached"
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
