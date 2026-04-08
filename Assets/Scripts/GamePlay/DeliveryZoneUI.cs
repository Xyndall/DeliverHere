using UnityEngine;
using TMPro;
using System.Collections;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// UI component to display active delivery zones to players.
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
        [SerializeField] private float retryDelay = 0.5f;

        private Coroutine _retryCoroutine;

        private void Awake()
        {
            if (zoneManager == null && autoFindManager)
                zoneManager = FindFirstObjectByType<DailyDeliveryZoneManager>();
        }

        private void OnEnable()
        {
            if (zoneManager != null)
            {
                zoneManager.OnZonesSelectedForDay += HandleZonesSelected;
                UpdateDisplay();
            }
            else if (autoFindManager)
            {
                // Retry finding the manager in case it hasn't spawned yet
                _retryCoroutine = StartCoroutine(RetryFindManager());
            }
        }

        private void OnDisable()
        {
            if (_retryCoroutine != null)
            {
                StopCoroutine(_retryCoroutine);
                _retryCoroutine = null;
            }

            if (zoneManager != null)
                zoneManager.OnZonesSelectedForDay -= HandleZonesSelected;
        }

        private IEnumerator RetryFindManager()
        {
            for (int i = 0; i < 10; i++) // Try 10 times
            {
                yield return new WaitForSeconds(retryDelay);
                
                zoneManager = FindFirstObjectByType<DailyDeliveryZoneManager>();
                if (zoneManager != null)
                {
                    zoneManager.OnZonesSelectedForDay += HandleZonesSelected;
                    UpdateDisplay();
                    yield break;
                }
            }

            Debug.LogWarning("[DeliveryZoneUI] Could not find DailyDeliveryZoneManager after retries.");
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

        // Public method to force update (can be called from other systems)
        public void ForceUpdate()
        {
            UpdateDisplay();
        }
    }
}