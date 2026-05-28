using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using DeliverHere.Settings;

namespace DeliverHere.UI
{
    /// <summary>
    /// UI controller for the settings menu.
    /// Handles user input and updates the SettingsManager.
    /// </summary>
    public class SettingsMenuUI : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private GameObject settingsPanel;

        [Header("Display Settings")]
        [SerializeField] private TMP_Dropdown screenModeDropdown;
        [SerializeField] private TMP_Dropdown resolutionDropdown;
        [SerializeField] private Toggle vsyncToggle;

        [Header("Graphics Settings")]
        [SerializeField] private TMP_Dropdown qualityDropdown;

        [Header("Audio Settings")]
        [SerializeField] private Slider masterVolumeSlider;
        [SerializeField] private Slider musicVolumeSlider;
        [SerializeField] private Slider sfxVolumeSlider;

        [Header("Buttons")]
        [SerializeField] private Button applyButton;
        [SerializeField] private Button applyButtonAlt;  // E.g. apply button on the audio panel
        [SerializeField] private Button resetButton;

        [Header("Debug")]
        [SerializeField] private bool enableLogs = false;

        private List<Resolution> uniqueResolutions;
        private bool isDirty = false; // Tracks if settings have changed

        private void Awake()
        {
            // Setup button listeners
            if (applyButton != null)
                applyButton.onClick.AddListener(OnApplyClicked);

            if (applyButtonAlt != null)
                applyButtonAlt.onClick.AddListener(OnApplyClicked);

            if (resetButton != null)
                resetButton.onClick.AddListener(OnResetClicked);

            // Setup dropdown listeners
            if (screenModeDropdown != null)
                screenModeDropdown.onValueChanged.AddListener(OnScreenModeChanged);

            if (resolutionDropdown != null)
                resolutionDropdown.onValueChanged.AddListener(OnResolutionChanged);

            if (qualityDropdown != null)
                qualityDropdown.onValueChanged.AddListener(OnQualityChanged);

            if (vsyncToggle != null)
                vsyncToggle.onValueChanged.AddListener(OnVSyncChanged);

            // Setup audio slider listeners
            if (masterVolumeSlider != null)
                masterVolumeSlider.onValueChanged.AddListener(OnMasterVolumeChanged);

            if (musicVolumeSlider != null)
                musicVolumeSlider.onValueChanged.AddListener(OnMusicVolumeChanged);

            if (sfxVolumeSlider != null)
                sfxVolumeSlider.onValueChanged.AddListener(OnSFXVolumeChanged);
        }

        private void Start()
        {
            SetupAudioSliders();
            PopulateDropdowns();
            RefreshUI();

            // Hide settings panel by default
            if (settingsPanel != null)
                settingsPanel.SetActive(false);
        }

        private void OnDestroy()
        {
            if (applyButton != null)
                applyButton.onClick.RemoveListener(OnApplyClicked);

            if (applyButtonAlt != null)
                applyButtonAlt.onClick.RemoveListener(OnApplyClicked);

            if (resetButton != null)
                resetButton.onClick.RemoveListener(OnResetClicked);

            if (screenModeDropdown != null)
                screenModeDropdown.onValueChanged.RemoveListener(OnScreenModeChanged);

            if (resolutionDropdown != null)
                resolutionDropdown.onValueChanged.RemoveListener(OnResolutionChanged);

            if (qualityDropdown != null)
                qualityDropdown.onValueChanged.RemoveListener(OnQualityChanged);

            if (vsyncToggle != null)
                vsyncToggle.onValueChanged.RemoveListener(OnVSyncChanged);

            if (masterVolumeSlider != null)
                masterVolumeSlider.onValueChanged.RemoveListener(OnMasterVolumeChanged);

            if (musicVolumeSlider != null)
                musicVolumeSlider.onValueChanged.RemoveListener(OnMusicVolumeChanged);

            if (sfxVolumeSlider != null)
                sfxVolumeSlider.onValueChanged.RemoveListener(OnSFXVolumeChanged);
        }

        /// <summary>
        /// Sets the min/max range on each audio slider (0-1).
        /// </summary>
        private void SetupAudioSliders()
        {
            if (masterVolumeSlider != null)
            {
                masterVolumeSlider.minValue = 0f;
                masterVolumeSlider.maxValue = 1f;
            }

            if (musicVolumeSlider != null)
            {
                musicVolumeSlider.minValue = 0f;
                musicVolumeSlider.maxValue = 1f;
            }

            if (sfxVolumeSlider != null)
            {
                sfxVolumeSlider.minValue = 0f;
                sfxVolumeSlider.maxValue = 1f;
            }
        }

        /// <summary>
        /// Populates all dropdowns with available options.
        /// </summary>
        private void PopulateDropdowns()
        {
            if (SettingsManager.Instance == null)
            {
                Debug.LogError("[SettingsMenuUI] SettingsManager.Instance is null!");
                return;
            }

            // Populate screen mode dropdown
            if (screenModeDropdown != null)
            {
                screenModeDropdown.ClearOptions();
                screenModeDropdown.AddOptions(new List<string> { "Fullscreen", "Windowed", "Borderless" });
            }

            // Populate resolution dropdown
            if (resolutionDropdown != null)
            {
                uniqueResolutions = SettingsManager.Instance.GetUniqueResolutions();
                resolutionDropdown.ClearOptions();

                List<string> resOptions = new List<string>();
                foreach (var res in uniqueResolutions)
                {
                    resOptions.Add($"{res.width} x {res.height}");
                }
                resolutionDropdown.AddOptions(resOptions);
            }

            // Populate quality dropdown
            if (qualityDropdown != null)
            {
                qualityDropdown.ClearOptions();
                qualityDropdown.AddOptions(new List<string>(QualitySettings.names));
            }
        }

        /// <summary>
        /// Refreshes the UI to match current settings.
        /// </summary>
        public void RefreshUI()
        {
            if (SettingsManager.Instance == null) return;

            var settings = SettingsManager.Instance.CurrentSettings;

            // Update screen mode dropdown
            if (screenModeDropdown != null)
            {
                int modeIndex = settings.screenMode switch
                {
                    FullScreenMode.ExclusiveFullScreen => 0,
                    FullScreenMode.Windowed => 1,
                    FullScreenMode.FullScreenWindow => 2,
                    _ => 2
                };
                screenModeDropdown.SetValueWithoutNotify(modeIndex);
            }

            // Update resolution dropdown
            if (resolutionDropdown != null && uniqueResolutions != null)
            {
                int resIndex = 0;
                for (int i = 0; i < uniqueResolutions.Count; i++)
                {
                    if (uniqueResolutions[i].width == settings.resolutionWidth &&
                        uniqueResolutions[i].height == settings.resolutionHeight)
                    {
                        resIndex = i;
                        break;
                    }
                }
                resolutionDropdown.SetValueWithoutNotify(resIndex);
            }

            // Update quality dropdown
            if (qualityDropdown != null)
                qualityDropdown.SetValueWithoutNotify(settings.qualityLevel);

            // Update VSync toggle
            if (vsyncToggle != null)
                vsyncToggle.SetIsOnWithoutNotify(QualitySettings.vSyncCount > 0);

            // Update audio sliders
            if (masterVolumeSlider != null)
                masterVolumeSlider.SetValueWithoutNotify(settings.masterVolume);

            if (musicVolumeSlider != null)
                musicVolumeSlider.SetValueWithoutNotify(settings.musicVolume);

            if (sfxVolumeSlider != null)
                sfxVolumeSlider.SetValueWithoutNotify(settings.sfxVolume);

            isDirty = false;
            UpdateApplyButtonState();
        }

        /// <summary>
        /// Shows the settings menu.
        /// </summary>
        public void Show()
        {
            if (settingsPanel != null)
                settingsPanel.SetActive(true);

            RefreshUI();

            if (enableLogs)
                Debug.Log("[SettingsMenuUI] Settings menu shown.");
        }

        /// <summary>
        /// Hides the settings menu.
        /// </summary>
        public void Hide()
        {
            if (settingsPanel != null)
                settingsPanel.SetActive(false);

            if (enableLogs)
                Debug.Log("[SettingsMenuUI] Settings menu hidden.");
        }

        /// <summary>
        /// Toggles the visibility of the settings menu.
        /// </summary>
        public void Toggle()
        {
            if (settingsPanel != null)
            {
                if (settingsPanel.activeSelf)
                    Hide();
                else
                    Show();
            }
        }

        private void OnScreenModeChanged(int index)
        {
            if (SettingsManager.Instance == null) return;

            FullScreenMode mode = index switch
            {
                0 => FullScreenMode.ExclusiveFullScreen,
                1 => FullScreenMode.Windowed,
                2 => FullScreenMode.FullScreenWindow,
                _ => FullScreenMode.FullScreenWindow
            };

            SettingsManager.Instance.CurrentSettings.screenMode = mode;
            MarkDirty();

            if (enableLogs)
                Debug.Log($"[SettingsMenuUI] Screen mode changed to: {mode}");
        }

        private void OnResolutionChanged(int index)
        {
            if (SettingsManager.Instance == null || uniqueResolutions == null) return;
            if (index < 0 || index >= uniqueResolutions.Count) return;

            var resolution = uniqueResolutions[index];
            SettingsManager.Instance.CurrentSettings.resolutionWidth = resolution.width;
            SettingsManager.Instance.CurrentSettings.resolutionHeight = resolution.height;
            SettingsManager.Instance.CurrentSettings.refreshRate = (int)resolution.refreshRateRatio.numerator;
            MarkDirty();

            if (enableLogs)
                Debug.Log($"[SettingsMenuUI] Resolution changed to: {resolution.width}x{resolution.height}");
        }

        private void OnQualityChanged(int index)
        {
            if (SettingsManager.Instance == null) return;

            SettingsManager.Instance.CurrentSettings.qualityLevel = index;
            MarkDirty();

            if (enableLogs)
                Debug.Log($"[SettingsMenuUI] Quality changed to: {QualitySettings.names[index]}");
        }

        private void OnVSyncChanged(bool enabled)
        {
            QualitySettings.vSyncCount = enabled ? 1 : 0;
            MarkDirty();

            if (enableLogs)
                Debug.Log($"[SettingsMenuUI] VSync changed to: {enabled}");
        }

        private void OnMasterVolumeChanged(float value)
        {
            if (SettingsManager.Instance == null) return;

            SettingsManager.Instance.CurrentSettings.masterVolume = value;
            SettingsManager.Instance.ApplyAudioSettings();
            MarkDirty();

            if (enableLogs)
                Debug.Log($"[SettingsMenuUI] Master volume changed to: {value}");
        }

        private void OnMusicVolumeChanged(float value)
        {
            if (SettingsManager.Instance == null) return;

            SettingsManager.Instance.CurrentSettings.musicVolume = value;
            SettingsManager.Instance.ApplyAudioSettings();
            MarkDirty();

            if (enableLogs)
                Debug.Log($"[SettingsMenuUI] Music volume changed to: {value}");
        }

        private void OnSFXVolumeChanged(float value)
        {
            if (SettingsManager.Instance == null) return;

            SettingsManager.Instance.CurrentSettings.sfxVolume = value;
            SettingsManager.Instance.ApplyAudioSettings();
            MarkDirty();

            if (enableLogs)
                Debug.Log($"[SettingsMenuUI] SFX volume changed to: {value}");
        }

        private void OnApplyClicked()
        {
            if (SettingsManager.Instance == null) return;

            SettingsManager.Instance.ApplyAllSettings();
            SettingsManager.Instance.SaveSettings();

            isDirty = false;
            UpdateApplyButtonState();

            if (enableLogs)
                Debug.Log("[SettingsMenuUI] Settings applied and saved.");
        }

        private void OnResetClicked()
        {
            if (SettingsManager.Instance == null) return;

            SettingsManager.Instance.ResetToDefaults();
            RefreshUI();

            if (enableLogs)
                Debug.Log("[SettingsMenuUI] Settings reset to defaults.");
        }

        private void MarkDirty()
        {
            isDirty = true;
            UpdateApplyButtonState();
        }

        private void UpdateApplyButtonState()
        {
            if (applyButton != null)
                applyButton.interactable = isDirty;

            if (applyButtonAlt != null)
                applyButtonAlt.interactable = isDirty;
        }

#if UNITY_EDITOR
        [ContextMenu("Test Show")]
        private void TestShow() => Show();

        [ContextMenu("Test Hide")]
        private void TestHide() => Hide();
#endif
    }
}