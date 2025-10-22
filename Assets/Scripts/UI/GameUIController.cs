using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GameUIController : MonoBehaviour
{
    [Header("HUD Root")]
    [SerializeField] private GameObject hudRoot; // Assign the HUD Canvas/Panel to toggle visibility

    [Header("HUD - Text")]
    [SerializeField] private TMP_Text dayText;
    [SerializeField] private TMP_Text currentMoneyText;
    [SerializeField] private TMP_Text targetMoneyText;
    [SerializeField] private TMP_Text bankedMoneyText;

    [Header("HUD - Day/Night Timer")]
    [SerializeField] private Slider dayNightSlider;

    [Header("Panels")]
    [SerializeField] private GameObject winPanel;
    [SerializeField] private GameObject dayEndSummaryPanel;

    [Header("Day End Summary UI")]
    [SerializeField] private TMP_Text dayEarnedText;
    [SerializeField] private TMP_Text dayNewBankedText;

    [Header("Target Increase UI")]
    [SerializeField] private TMP_Text targetIncreaseText; // Optional popup/text

    private void Awake()
    {
        HideWinPanel();
        HideDayEndSummary();
        HideHUD(); // Hide all HUD text until the game starts
        if (targetIncreaseText != null) targetIncreaseText.gameObject.SetActive(false);

        if (dayNightSlider != null)
        {
            dayNightSlider.minValue = 0f;
            dayNightSlider.maxValue = 1f;
            dayNightSlider.value = 0f;
        }
    }

    // ---- HUD visibility ----
    public void ShowHUD()
    {
        if (hudRoot != null) hudRoot.SetActive(true);
    }

    public void HideHUD()
    {
        if (hudRoot != null) hudRoot.SetActive(false);
    }

    // ---- Setters used by GameManager ----
    public void SetDay(int day)
    {
        if (dayText != null) dayText.text = $"Day: {day}";
    }

    public void SetTarget(int target)
    {
        if (targetMoneyText != null) targetMoneyText.text = $"Target: {target}";
    }

    public void SetDailyEarnings(int current, int target, float progress)
    {
        if (currentMoneyText != null) currentMoneyText.text = $"Today: {current}";
    }

    public void SetBankedMoney(int banked)
    {
        if (bankedMoneyText != null) bankedMoneyText.text = $"Banked: {banked}";
    }

    public void ShowTargetIncrease(int newTarget, int delta)
    {
        if (targetIncreaseText == null) return;
        targetIncreaseText.gameObject.SetActive(true);
        targetIncreaseText.text = $"+{delta} target -> {newTarget}";
        // Optionally hide after a delay via coroutine/tween.
    }

    public void ShowDayEndSummary(int earnedToday, int newBanked)
    {
        if (dayEndSummaryPanel != null) dayEndSummaryPanel.SetActive(true);
        if (dayEarnedText != null) dayEarnedText.text = $"Earned Today: {earnedToday}";
        if (dayNewBankedText != null) dayNewBankedText.text = $"Banked Total: {newBanked}";
    }

    public void HideDayEndSummary()
    {
        if (dayEndSummaryPanel != null) dayEndSummaryPanel.SetActive(false);
    }

    public void ShowWinPanel()
    {
        if (winPanel != null) winPanel.SetActive(true);
    }

    public void HideWinPanel()
    {
        if (winPanel != null) winPanel.SetActive(false);
    }

    // ---- Day/Night timer UI ----
    public void SetDayNightProgress(float normalized01)
    {
        if (dayNightSlider == null) return;
        dayNightSlider.value = Mathf.Clamp01(normalized01);
    }

    public void SetDayNightVisible(bool visible)
    {
        if (dayNightSlider == null) return;
        dayNightSlider.gameObject.SetActive(visible);
    }
}