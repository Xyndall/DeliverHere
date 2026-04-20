using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GameUIController : MonoBehaviour
{
    [Header("HUD Root")]
    [SerializeField] private GameObject hudRoot;

    [Header("HUD - Text")]
    [SerializeField] private TMP_Text dayText;
    [SerializeField] private TMP_Text deliveryZoneValueText; // NEW: Shows target/current in zone
    [SerializeField] private TMP_Text bankedMoneyText;

    [Header("HUD - Timer")]
    [SerializeField] private TMP_Text timerText; // display seconds

    [Header("HUD - Stamina")]
    [SerializeField] private Slider staminaSlider;

    [Header("HUD - Interaction Prompt (Upgrades)")]
    [SerializeField] private TMP_Text upgradePromptText;

    [Header("Panels")]
    [SerializeField] private GameObject winPanel;
    [SerializeField] private GameObject dayEndSummaryPanel;

    [Header("Day End Summary UI")]
    [SerializeField] private TMP_Text earnedTodayText; // NEW: Shows money earned today
    [SerializeField] private TMP_Text totalBankedText; // NEW: Shows total banked money
    [SerializeField] private TMP_Text packagesDeliveredText;
    [SerializeField] private TMP_Text quotaStatusText;
    [SerializeField] private Button nextDayButton;
    [SerializeField] private Button restartButton;

    public UIStateManager uiStateManager; // assign

    private bool isPaused;

    private PlayerInputController _localPlayerInput;
    private PlayerMovement _localPlayerMovement;

    private void Awake()
    {
        HideHUD();
        HideDayEndSummary();
        HideWinPanel();
        ClearUpgradePrompt();

        if (staminaSlider != null)
        {
            staminaSlider.minValue = 0f;
            staminaSlider.maxValue = 1f;
            staminaSlider.value = 1f;
        }
    }

    private void Update()
    {
        if (_localPlayerInput != null && _localPlayerInput.PausePressedThisFrame)
        {
            TogglePause();
        }

        UpdateStaminaBar();
    }

    public void SetLocalPlayerInput(PlayerInputController input)
    {
        _localPlayerInput = input;

        // Assume input controller lives on the same player object as movement.
        _localPlayerMovement = input != null ? input.GetComponent<PlayerMovement>() : null;
    }

    private void UpdateStaminaBar()
    {
        if (staminaSlider == null) return;

        if (_localPlayerMovement == null)
        {
            staminaSlider.value = 0f;
            return;
        }

        float max = Mathf.Max(1f, _localPlayerMovement.CurrentMaxStamina);
        float current = Mathf.Clamp(_localPlayerMovement.Stamina.Value, 0f, max);
        staminaSlider.value = current / max;
    }

    // NEW: upgrade prompt UI ---------------------------------------------

    public void SetUpgradePrompt(string text)
    {
        if (upgradePromptText == null) return;

        if (string.IsNullOrWhiteSpace(text))
        {
            upgradePromptText.gameObject.SetActive(false);
            upgradePromptText.text = "";
            return;
        }

        upgradePromptText.gameObject.SetActive(true);
        upgradePromptText.text = text;
    }

    public void ClearUpgradePrompt() => SetUpgradePrompt(null);

    // Helper: returns current prompt text (empty string if none)
    public string GetUpgradePromptText()
    {
        if (upgradePromptText == null) return string.Empty;
        return upgradePromptText.text ?? string.Empty;
    }

    // Helper: clear only if the current prompt equals the provided expected text.
    // Prevents other systems' prompts from being unintentionally cleared.
    public void ClearUpgradePromptIfEquals(string expected)
    {
        if (upgradePromptText == null) return;
        if (string.IsNullOrEmpty(expected))
        {
            // no-op: caller shouldn't try to clear with empty expected
            return;
        }

        if ((upgradePromptText.text ?? string.Empty) == expected)
            ClearUpgradePrompt();
    }

    // -------------------------------------------------------------------

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
    
    // NEW: Display delivery zone value vs target
    public void SetDeliveryZoneValue(int currentValueInZone, int targetMoney)
    {
        if (deliveryZoneValueText != null)
        {
            deliveryZoneValueText.text = $"Quota: ${targetMoney} / ${currentValueInZone}";
        }
    }

    public void SetBankedMoney(int banked) { if (bankedMoneyText != null) bankedMoneyText.text = $"Banked: ${banked}"; }

    // NEW: Enhanced day-end summary with earned today and total banked displays
    public void ShowDayEndSummary(int earnedToday, int newBanked, int packagesDelivered, bool metQuota, bool hostHasControl)
    {
        if (dayEndSummaryPanel != null) dayEndSummaryPanel.SetActive(true);

        // NEW: Display earned today
        if (earnedTodayText != null) 
            earnedTodayText.text = $"Earned Today: ${earnedToday}";

        // NEW: Display total banked money
        if (totalBankedText != null) 
            totalBankedText.text = $"Total Banked: ${newBanked}";

        if (packagesDeliveredText != null) 
            packagesDeliveredText.text = $"Packages Delivered: {packagesDelivered}";

        if (quotaStatusText != null)
        {
            quotaStatusText.text = metQuota ? "Quota: PASSED" : "Quota: FAILED";
            quotaStatusText.color = metQuota ? Color.green : Color.red;
        }

        if (nextDayButton != null) nextDayButton.gameObject.SetActive(hostHasControl && metQuota);
        if (restartButton != null) restartButton.gameObject.SetActive(hostHasControl);
    }

    public void HideDayEndSummary()
    {
        if (dayEndSummaryPanel != null) dayEndSummaryPanel.SetActive(false);
    }

    public void ShowWinPanel() { if (winPanel != null) winPanel.SetActive(true); }
    public void HideWinPanel() { if (winPanel != null) winPanel.SetActive(false); }

    // ---- Timer UI ----
    public void SetTimerSeconds(float secondsRemaining)
    {
        if (timerText == null) return;

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

    public void TogglePause()
    {
        isPaused = !isPaused;

        Cursor.visible = isPaused;
        Cursor.lockState = isPaused ? CursorLockMode.None : CursorLockMode.Locked;

        if (uiStateManager != null)
        {
            uiStateManager.SetPaused(isPaused);
            uiStateManager.SetGameState(isPaused ? GameState.Paused : GameState.InGame);
        }
    }
}