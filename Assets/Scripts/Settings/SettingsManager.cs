using UnityEngine;
using UnityEngine.Audio;
using System.Collections.Generic;
using System.Linq;

namespace DeliverHere.Settings
{
    /// <summary>
    /// Manages game settings: loading, saving, and applying them.
    /// Singleton pattern for easy access throughout the game.
    /// </summary>
    public class SettingsManager : MonoBehaviour
    {
        public static SettingsManager Instance { get; private set; }

        [Header("Settings Asset")]
        [Tooltip("Reference to the GameSettings ScriptableObject")]
        [SerializeField] private GameSettings defaultSettings;

        [Header("Audio Mixer")]
        [Tooltip("The main AudioMixer with exposed Master, Music and SFX parameters")]
        [SerializeField] private AudioMixer audioMixer;

        [Header("Debug")]
        [SerializeField] private bool enableLogs = true;

        // Current active settings (runtime instance)
        private GameSettings currentSettings;

        // Available resolutions cache
        private Resolution[] availableResolutions;

        // AudioMixer exposed parameter names (must match exactly in the Mixer)
        private const string MIXER_MASTER = "Master";
        private const string MIXER_MUSIC  = "Music";
        private const string MIXER_SFX    = "SFX";

        // PlayerPrefs keys
        private const string PREF_SCREEN_MODE = "Settings_ScreenMode";
        private const string PREF_RES_WIDTH    = "Settings_ResWidth";
        private const string PREF_RES_HEIGHT   = "Settings_ResHeight";
        private const string PREF_REFRESH_RATE = "Settings_RefreshRate";
        private const string PREF_QUALITY      = "Settings_Quality";
        private const string PREF_MASTER_VOL   = "Settings_MasterVol";
        private const string PREF_MUSIC_VOL    = "Settings_MusicVol";
        private const string PREF_SFX_VOL      = "Settings_SfxVol";

        public GameSettings CurrentSettings => currentSettings;

        private void Awake()
        {
            // Singleton pattern
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Cache available resolutions
            availableResolutions = Screen.resolutions;

            // Create runtime instance of settings
            if (defaultSettings != null)
            {
                currentSettings = Instantiate(defaultSettings);
            }
            else
            {
                currentSettings = ScriptableObject.CreateInstance<GameSettings>();
                currentSettings.ResetToDefaults();
                Debug.LogWarning("[SettingsManager] No default settings assigned. Using runtime defaults.");
            }

            // Load settings from PlayerPrefs (or use defaults)
            LoadSettings();

            // Apply loaded settings
            ApplyAllSettings();
        }

        /// <summary>
        /// Loads settings from PlayerPrefs into currentSettings.
        /// </summary>
        public void LoadSettings()
        {
            if (PlayerPrefs.HasKey(PREF_SCREEN_MODE))
            {
                currentSettings.screenMode    = (FullScreenMode)PlayerPrefs.GetInt(PREF_SCREEN_MODE, (int)FullScreenMode.FullScreenWindow);
                currentSettings.resolutionWidth  = PlayerPrefs.GetInt(PREF_RES_WIDTH, Screen.currentResolution.width);
                currentSettings.resolutionHeight = PlayerPrefs.GetInt(PREF_RES_HEIGHT, Screen.currentResolution.height);
                currentSettings.refreshRate   = PlayerPrefs.GetInt(PREF_REFRESH_RATE, (int)Screen.currentResolution.refreshRateRatio.numerator);
                currentSettings.qualityLevel  = PlayerPrefs.GetInt(PREF_QUALITY, 3);
                currentSettings.masterVolume  = PlayerPrefs.GetFloat(PREF_MASTER_VOL, 1f);
                currentSettings.musicVolume   = PlayerPrefs.GetFloat(PREF_MUSIC_VOL, 0.7f);
                currentSettings.sfxVolume     = PlayerPrefs.GetFloat(PREF_SFX_VOL, 1f);

                if (enableLogs)
                    Debug.Log("[SettingsManager] Settings loaded from PlayerPrefs.");
            }
            else
            {
                // First launch: use default settings and save them
                if (enableLogs)
                    Debug.Log("[SettingsManager] No saved settings found. Using defaults.");

                SaveSettings();
            }
        }

        /// <summary>
        /// Saves current settings to PlayerPrefs.
        /// </summary>
        public void SaveSettings()
        {
            PlayerPrefs.SetInt(PREF_SCREEN_MODE, (int)currentSettings.screenMode);
            PlayerPrefs.SetInt(PREF_RES_WIDTH, currentSettings.resolutionWidth);
            PlayerPrefs.SetInt(PREF_RES_HEIGHT, currentSettings.resolutionHeight);
            PlayerPrefs.SetInt(PREF_REFRESH_RATE, currentSettings.refreshRate);
            PlayerPrefs.SetInt(PREF_QUALITY, currentSettings.qualityLevel);
            PlayerPrefs.SetFloat(PREF_MASTER_VOL, currentSettings.masterVolume);
            PlayerPrefs.SetFloat(PREF_MUSIC_VOL, currentSettings.musicVolume);
            PlayerPrefs.SetFloat(PREF_SFX_VOL, currentSettings.sfxVolume);
            PlayerPrefs.Save();

            if (enableLogs)
                Debug.Log("[SettingsManager] Settings saved to PlayerPrefs.");
        }

        /// <summary>
        /// Applies all settings to the game.
        /// </summary>
        public void ApplyAllSettings()
        {
            ApplyDisplaySettings();
            ApplyGraphicsSettings();
            ApplyAudioSettings();

            if (enableLogs)
                Debug.Log("[SettingsManager] All settings applied.");
        }

        /// <summary>
        /// Applies display settings (resolution, fullscreen mode).
        /// </summary>
        public void ApplyDisplaySettings()
        {
            Screen.SetResolution(
                currentSettings.resolutionWidth,
                currentSettings.resolutionHeight,
                currentSettings.screenMode,
                new RefreshRate { numerator = (uint)currentSettings.refreshRate, denominator = 1 }
            );

            if (enableLogs)
            {
                Debug.Log($"[SettingsManager] Display settings applied: {currentSettings.resolutionWidth}x{currentSettings.resolutionHeight} " +
                         $"{currentSettings.screenMode} @ {currentSettings.refreshRate}Hz");
            }
        }

        /// <summary>
        /// Applies graphics quality settings.
        /// </summary>
        public void ApplyGraphicsSettings()
        {
            QualitySettings.SetQualityLevel(currentSettings.qualityLevel, applyExpensiveChanges: true);

            if (enableLogs)
                Debug.Log($"[SettingsManager] Graphics quality set to: {QualitySettings.names[currentSettings.qualityLevel]}");
        }

        /// <summary>
        /// Applies audio settings to the AudioMixer via exposed parameters.
        /// Slider values (0-1) are converted to decibels (-80dB to 0dB).
        /// </summary>
        public void ApplyAudioSettings()
        {
            if (audioMixer == null)
            {
                Debug.LogWarning("[SettingsManager] AudioMixer is not assigned. Audio settings will not be applied.");
                return;
            }

            audioMixer.SetFloat(MIXER_MASTER, LinearToDecibels(currentSettings.masterVolume));
            audioMixer.SetFloat(MIXER_MUSIC,  LinearToDecibels(currentSettings.musicVolume));
            audioMixer.SetFloat(MIXER_SFX,    LinearToDecibels(currentSettings.sfxVolume));

            if (enableLogs)
            {
                Debug.Log($"[SettingsManager] Audio settings applied: " +
                         $"Master={currentSettings.masterVolume}, " +
                         $"Music={currentSettings.musicVolume}, " +
                         $"SFX={currentSettings.sfxVolume}");
            }
        }

        /// <summary>
        /// Converts a linear volume value (0-1) to decibels (-80 to 0).
        /// Clamps to -80dB at zero to avoid log(0) being undefined.
        /// </summary>
        private float LinearToDecibels(float linear)
        {
            return linear > 0.0001f ? Mathf.Log10(linear) * 20f : -80f;
        }

        /// <summary>
        /// Gets all available screen resolutions.
        /// </summary>
        public Resolution[] GetAvailableResolutions()
        {
            return availableResolutions;
        }

        /// <summary>
        /// Gets unique resolutions (filters out duplicates with different refresh rates).
        /// </summary>
        public List<Resolution> GetUniqueResolutions()
        {
            return availableResolutions
                .GroupBy(r => new { r.width, r.height })
                .Select(g => g.First())
                .OrderByDescending(r => r.width)
                .ThenByDescending(r => r.height)
                .ToList();
        }

        /// <summary>
        /// Sets screen mode and applies immediately.
        /// </summary>
        public void SetScreenMode(FullScreenMode mode)
        {
            currentSettings.screenMode = mode;
            ApplyDisplaySettings();
        }

        /// <summary>
        /// Sets resolution and applies immediately.
        /// </summary>
        public void SetResolution(int width, int height, int refreshRate = 0)
        {
            currentSettings.resolutionWidth = width;
            currentSettings.resolutionHeight = height;
            currentSettings.refreshRate = refreshRate;
            ApplyDisplaySettings();
        }

        /// <summary>
        /// Sets quality level and applies immediately.
        /// </summary>
        public void SetQualityLevel(int level)
        {
            currentSettings.qualityLevel = Mathf.Clamp(level, 0, QualitySettings.names.Length - 1);
            ApplyGraphicsSettings();
        }

        /// <summary>
        /// Resets settings to defaults, applies them, and saves.
        /// </summary>
        public void ResetToDefaults()
        {
            if (defaultSettings != null)
            {
                currentSettings.CopyFrom(defaultSettings);
            }
            else
            {
                currentSettings.ResetToDefaults();
            }

            ApplyAllSettings();
            SaveSettings();

            if (enableLogs)
                Debug.Log("[SettingsManager] Settings reset to defaults.");
        }

#if UNITY_EDITOR
        [ContextMenu("Debug/Print Current Settings")]
        private void DebugPrintSettings()
        {
            Debug.Log($"[SettingsManager] Current Settings:\n" +
                     $"Screen Mode: {currentSettings.screenMode}\n" +
                     $"Resolution: {currentSettings.resolutionWidth}x{currentSettings.resolutionHeight} @ {currentSettings.refreshRate}Hz\n" +
                     $"Quality: {currentSettings.qualityLevel} ({QualitySettings.names[currentSettings.qualityLevel]})\n" +
                     $"Volumes: Master={currentSettings.masterVolume}, Music={currentSettings.musicVolume}, SFX={currentSettings.sfxVolume}");
        }

        [ContextMenu("Debug/Clear Saved Settings")]
        private void DebugClearSettings()
        {
            PlayerPrefs.DeleteKey(PREF_SCREEN_MODE);
            PlayerPrefs.DeleteKey(PREF_RES_WIDTH);
            PlayerPrefs.DeleteKey(PREF_RES_HEIGHT);
            PlayerPrefs.DeleteKey(PREF_REFRESH_RATE);
            PlayerPrefs.DeleteKey(PREF_QUALITY);
            PlayerPrefs.DeleteKey(PREF_MASTER_VOL);
            PlayerPrefs.DeleteKey(PREF_MUSIC_VOL);
            PlayerPrefs.DeleteKey(PREF_SFX_VOL);
            PlayerPrefs.Save();
            Debug.Log("[SettingsManager] Saved settings cleared from PlayerPrefs.");
        }
#endif
    }
}