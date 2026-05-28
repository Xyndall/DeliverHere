using UnityEngine;

namespace DeliverHere.Settings
{
    /// <summary>
    /// ScriptableObject that holds game settings data.
    /// Create via: Assets > Create > DeliverHere > Game Settings
    /// </summary>
    [CreateAssetMenu(fileName = "GameSettings", menuName = "DeliverHere/Settings/Game Settings", order = 1)]
    public class GameSettings : ScriptableObject
    {
        [Header("Display Settings")]
        [Tooltip("Window mode: Fullscreen, Windowed, or Borderless")]
        public FullScreenMode screenMode = FullScreenMode.FullScreenWindow;

        [Tooltip("Screen resolution width")]
        public int resolutionWidth = 1920;

        [Tooltip("Screen resolution height")]
        public int resolutionHeight = 1080;

        [Tooltip("Refresh rate (Hz). 0 = use system default")]
        public int refreshRate = 0;

        [Header("Graphics Settings")]
        [Tooltip("Unity Quality Level (0 = Very Low, 1 = Low, 2 = Medium, 3 = High, 4 = Very High, 5 = Ultra)")]
        [Range(0, 5)]
        public int qualityLevel = 3;

        [Header("Audio Settings")]
        [Range(0f, 1f)]
        public float masterVolume = 1f;

        [Range(0f, 1f)]
        public float musicVolume = 0.7f;

        [Range(0f, 1f)]
        public float sfxVolume = 1f;

        /// <summary>
        /// Resets all settings to default values.
        /// </summary>
        public void ResetToDefaults()
        {
            screenMode = FullScreenMode.FullScreenWindow;
            resolutionWidth = 1920;
            resolutionHeight = 1080;
            refreshRate = 0;
            qualityLevel = 3;
            masterVolume = 1f;
            musicVolume = 0.7f;
            sfxVolume = 1f;
        }

        /// <summary>
        /// Copies settings from another GameSettings instance.
        /// </summary>
        public void CopyFrom(GameSettings other)
        {
            if (other == null) return;

            screenMode = other.screenMode;
            resolutionWidth = other.resolutionWidth;
            resolutionHeight = other.resolutionHeight;
            refreshRate = other.refreshRate;
            qualityLevel = other.qualityLevel;
            masterVolume = other.masterVolume;
            musicVolume = other.musicVolume;
            sfxVolume = other.sfxVolume;
        }
    }
}