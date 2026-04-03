using UnityEngine;
using TMPro;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// UI component to display active delivery zones to players.
    /// Attach to your UI canvas and wire up the DailyDeliveryZoneManager.
    /// </summary>
    [DisallowMultipleComponent]
    public class DeliveryZoneUI : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DailyDeliveryZoneManager zoneManager;
        [SerializeField] private TMP_Text zoneDisplayText;
        [SerializeField] private GameObject uiPanel;

        [Header("Settings")]
        [SerializeField] private bool autoFindManager = true;
        [SerializeField] private bool hideWhenNoZones = true;

        private void Awake()
        {
            if (zoneManager == null && autoFindManager)
                zoneManager = FindFirstObjectByType<DailyDeliveryZoneManager>();
        }

        private void OnEnable()
        {
            if (zoneManager != null)
                zoneManager.OnZonesSelectedForDay += HandleZonesSelected;

            UpdateDisplay();
        }

        private void OnDisable()
        {
            if (zoneManager != null)
                zoneManager.OnZonesSelectedForDay -= HandleZonesSelected;
        }

        private void HandleZonesSelected(System.Collections.Generic.List<DeliveryZoneDefinition> zones)
        {
            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            if (zoneManager == null || zoneDisplayText == null)
                return;

            string displayText = zoneManager.GetActiveZonesDisplayText();
            zoneDisplayText.text = displayText;

            if (hideWhenNoZones && uiPanel != null)
            {
                bool hasZones = zoneManager.ActiveZones != null && zoneManager.ActiveZones.Count > 0;
                uiPanel.SetActive(hasZones);
            }
        }
    }
}