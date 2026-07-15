using UnityEngine;
using TMPro;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Displays delivery zone information in UI with three text fields:
    /// - Delivery zone name (always visible)
    /// - Active status (green if active, red if inactive)
    /// - Quota progress (only visible when active)
    /// </summary>
    public class DeliveryZoneInfoDisplay : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The delivery zone this display represents. Auto-found if null.")]
        [SerializeField] private PackageDeliveryZone deliveryZone;

        [Header("UI Text Elements")]
        [SerializeField] private TMP_Text zoneNameText;
        [SerializeField] private TMP_Text activeStatusText;
        [SerializeField] private TMP_Text quotaText;

        [Header("Display Text")]
        [SerializeField] private string activeText = "ACTIVE";
        [SerializeField] private string inactiveText = "INACTIVE";

        [Header("Colors")]
        [SerializeField] private Color activeColor = Color.green;
        [SerializeField] private Color inactiveColor = Color.red;
        [SerializeField] private Color zoneNameColor = Color.white;

        [Header("Update Settings")]
        [SerializeField] private float updateInterval = 0.1f;

        private DeliveryZoneDefinition _zoneDefinition;
        private float _lastUpdateTime;

        private void Awake()
        {
            // Auto-find delivery zone if not assigned
            if (deliveryZone == null)
            {
                deliveryZone = GetComponentInParent<PackageDeliveryZone>();
                if (deliveryZone == null)
                {
                    deliveryZone = FindFirstObjectByType<PackageDeliveryZone>();
                }
            }

            // Get the zone definition
            if (deliveryZone != null)
            {
                _zoneDefinition = deliveryZone.GetComponent<DeliveryZoneDefinition>();
            }

            // Initial update
            UpdateDisplay();
        }

        private void OnEnable()
        {
            _lastUpdateTime = 0f; // Force immediate update
        }

        private void Update()
        {
            // Update at intervals for performance
            if (Time.time - _lastUpdateTime >= updateInterval)
            {
                _lastUpdateTime = Time.time;
                UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            if (deliveryZone == null || _zoneDefinition == null)
            {
                ClearDisplay();
                return;
            }

            // Check if zone is active
            bool isActive = deliveryZone.enabled && deliveryZone.gameObject.activeInHierarchy;

            // Update zone name (always visible)
            UpdateZoneName();

            // Update active status (always visible, color-coded)
            UpdateActiveStatus(isActive);

            // Update quota (only visible when active)
            UpdateQuota(isActive);
        }

        private void UpdateZoneName()
        {
            if (zoneNameText != null && _zoneDefinition != null)
            {
                zoneNameText.text = _zoneDefinition.ZoneName;
                zoneNameText.color = zoneNameColor;
            }
        }

        private void UpdateActiveStatus(bool isActive)
        {
            if (activeStatusText != null)
            {
                activeStatusText.text = isActive ? activeText : inactiveText;
                activeStatusText.color = isActive ? activeColor : inactiveColor;
            }
        }

        private void UpdateQuota(bool isActive)
        {
            if (quotaText != null)
            {
                if (isActive)
                {
                    int currentValue = deliveryZone.TotalValueInZone;
                    int quota = deliveryZone.IndividualQuota;

                    quotaText.text = $"Quota: ${currentValue} / ${quota}";
                    quotaText.gameObject.SetActive(true);
                }
                else
                {
                    // Hide quota when inactive
                    quotaText.gameObject.SetActive(false);
                }
            }
        }

        private void ClearDisplay()
        {
            if (zoneNameText != null)
                zoneNameText.text = "";

            if (activeStatusText != null)
                activeStatusText.text = "";

            if (quotaText != null)
            {
                quotaText.text = "";
                quotaText.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Force an immediate update of the display.
        /// </summary>
        public void ForceUpdate()
        {
            _lastUpdateTime = 0f;
            UpdateDisplay();
        }

        /// <summary>
        /// Assign a specific delivery zone to this display.
        /// </summary>
        public void SetDeliveryZone(PackageDeliveryZone zone)
        {
            deliveryZone = zone;
            if (deliveryZone != null)
            {
                _zoneDefinition = deliveryZone.GetComponent<DeliveryZoneDefinition>();
            }
            ForceUpdate();
        }
    }
}