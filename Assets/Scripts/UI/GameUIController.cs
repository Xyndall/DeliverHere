using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GameUIController : MonoBehaviour
{
    [Header("HUD Root")]
    [SerializeField] private GameObject hudRoot;

    [Header("HUD - Text")]
    [SerializeField] private TMP_Text dayText;
    [SerializeField] private TMP_Text currentMoneyText;
    [SerializeField] private TMP_Text targetMoneyText;
    [SerializeField] private TMP_Text bankedMoneyText;

    [Header("HUD - Timer")]
    [SerializeField] private TMP_Text timerText; // display seconds
    // [SerializeField] private Slider timerSlider; // removed: using text instead

    [Header("Panels")]
    [SerializeField] private GameObject winPanel;
    [SerializeField] private GameObject dayEndSummaryPanel;

    [Header("Day End Summary UI")]
    [SerializeField] private TMP_Text dayEarnedText;
    [SerializeField] private TMP_Text dayNewBankedText;
    [SerializeField] private TMP_Text packagesDeliveredText;
    [SerializeField] private TMP_Text quotaStatusText;
    [SerializeField] private Button nextDayButton;
    [SerializeField] private Button restartButton;


    private void Awake()
    {
        HideHUD();
        HideDayEndSummary();
        HideWinPanel();
    }

    // Set button callbacks (called by GameManager after it sets itself up)
    public void ConfigureDayEndButtons(System.Action onNextDay, System.Action onRestart)
    {
        if (nextDayButton != null)
        {
            nextDayButton.onClick.RemoveAllListeners();
            nextDayButton.onClick.AddListener(() => onNextDay?.Invoke());
        }

        if (restartButton != null)
        {
            restartButton.onClick.RemoveAllListeners();
            restartButton.onClick.AddListener(() => onRestart?.Invoke());
        }
    }

    // ---- HUD visibility ----
    public void ShowHUD() { if (hudRoot != null) hudRoot.SetActive(true); }
    public void HideHUD() { if (hudRoot != null) hudRoot.SetActive(false); }

    // ---- Setters used by GameManager ----
    public void SetDay(int day) { if (dayText != null) dayText.text = $"Day {day}"; }
    public void SetTarget(int target) { if (targetMoneyText != null) targetMoneyText.text = $"Target: ${target}"; }
    public void SetDailyEarnings(int current, int target, float progress)
    {
        if (currentMoneyText != null) currentMoneyText.text = $"Today: ${current}";
        if (targetMoneyText != null) targetMoneyText.text = $"Target: ${target}";
        // Previously updated slider progress; now unused. If you still receive normalized progress, you can ignore or map to seconds elsewhere.
    }
    public void SetBankedMoney(int banked) { if (bankedMoneyText != null) bankedMoneyText.text = $"Banked: ${banked}"; }

    // New extended summary (replaces old)
    public void ShowDayEndSummary(int earnedToday, int newBanked, int packagesDelivered, bool metQuota, bool hostHasControl)
    {
        if (dayEndSummaryPanel != null) dayEndSummaryPanel.SetActive(true);

        if (dayEarnedText != null) dayEarnedText.text = $"Earned Today: ${earnedToday}";
        if (dayNewBankedText != null) dayNewBankedText.text = $"New Banked Total: ${newBanked}";
        if (packagesDeliveredText != null) packagesDeliveredText.text = $"Packages Delivered: {packagesDelivered}";
        if (quotaStatusText != null)
        {
            quotaStatusText.text = metQuota ? "Quota: PASSED" : "Quota: FAILED";
            quotaStatusText.color = metQuota ? Color.green : Color.red;
        }

        if (nextDayButton != null) nextDayButton.gameObject.SetActive(hostHasControl);
        if (restartButton != null) restartButton.gameObject.SetActive(hostHasControl);
    }

    public void HideDayEndSummary()
    {
        if (dayEndSummaryPanel != null) dayEndSummaryPanel.SetActive(false);
    }

    public void ShowWinPanel() { if (winPanel != null) winPanel.SetActive(true); }
    public void HideWinPanel() { if (winPanel != null) winPanel.SetActive(false); }

    // ---- Timer UI ----
    // New: set timer by seconds (rounded or mm:ss)
    public void SetTimerSeconds(float secondsRemaining)
    {
        if (timerText == null) return;

        // Choose formatting: integer seconds or mm:ss. Here we use mm:ss.
        int totalSeconds = Mathf.Max(0, Mathf.FloorToInt(secondsRemaining));
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        timerText.text = $"{minutes:00}:{seconds:00}";
    }

    public void SetTimerVisible(bool visible)
    {
        if (timerText == null) return;
        timerText.gameObject.SetActive(visible);
    }

}