using UnityEngine;
using TMPro;
using Unity.Netcode;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Displays quota progress for a delivery zone in world space.
    /// Shows: "Quota: $1000 / $350" or for multi-zone: "$500 / $350"
    /// </summary>
    [RequireComponent(typeof(TextMeshPro))]
    public class DeliveryZoneWorldText : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The delivery zone this text represents. Auto-found if null.")]
        [SerializeField] private PackageDeliveryZone deliveryZone;

        [Header("Display Settings")]
        [SerializeField] private bool showZoneName = true;
        [SerializeField] private bool showQuotaMet = true;
        [SerializeField] private Vector3 localOffset = new Vector3(0f, 5f, 0f);

        [Header("Colors")]
        [SerializeField] private Color defaultColor = Color.white;
        [SerializeField] private Color quotaMetColor = Color.green;
        [SerializeField] private Color quotaFailedColor = Color.red;

        [Header("Behavior")]
        [SerializeField] private bool billboardToCamera = true;
        [SerializeField] private bool hideWhenInactive = true;
        [SerializeField] private float updateInterval = 0.1f; // Update every 0.1 seconds

        private TextMeshPro _textMesh;
        private DeliveryZoneDefinition _zoneDefinition;
        private float _lastUpdateTime;
        private bool _isZoneActive;

        private void Awake()
        {
            _textMesh = GetComponent<TextMeshPro>();

            // Auto-find delivery zone on parent
            if (deliveryZone == null)
            {
                deliveryZone = GetComponentInParent<PackageDeliveryZone>();
            }

            // Auto-find zone definition
            if (deliveryZone != null)
            {
                _zoneDefinition = deliveryZone.GetComponent<DeliveryZoneDefinition>();
            }

            // Set initial position offset
            if (deliveryZone != null)
            {
                transform.localPosition = localOffset;
            }

            // Initial state
            UpdateDisplay();
        }

        private void OnEnable()
        {
            _lastUpdateTime = 0f; // Force immediate update
        }

        private void Update()
        {
            // Billboard to camera
            if (billboardToCamera && Camera.main != null)
            {
                transform.LookAt(transform.position + Camera.main.transform.rotation * Vector3.forward,
                                 Camera.main.transform.rotation * Vector3.up);
            }

            // Update text at intervals (not every frame for performance)
            if (Time.time - _lastUpdateTime >= updateInterval)
            {
                _lastUpdateTime = Time.time;
                UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            if (_textMesh == null || deliveryZone == null)
            {
                // Hide if no valid zone
                if (_textMesh != null)
                    _textMesh.text = "";
                return;
            }

            // Check if zone is active (enabled)
            _isZoneActive = deliveryZone.enabled && deliveryZone.gameObject.activeInHierarchy;

            if (hideWhenInactive && !_isZoneActive)
            {
                _textMesh.text = "";
                return;
            }

            // Get current values
            int currentValue = deliveryZone.TotalValueInZone;
            int quota = deliveryZone.IndividualQuota;
            bool quotaMet = deliveryZone.IsQuotaMet;

            // Build display text
            string displayText = "";

            if (showZoneName && _zoneDefinition != null)
            {
                displayText += $"<b>{_zoneDefinition.ZoneName}</b>\n";
            }

            displayText += $"${quota} / ${currentValue}";

            if (showQuotaMet && quota > 0)
            {
                displayText += quotaMet ? " ?" : "";
            }

            _textMesh.text = displayText;

            // Update color based on quota status
            if (quota > 0)
            {
                if (quotaMet)
                {
                    _textMesh.color = quotaMetColor;
                }
                else if (currentValue > 0)
                {
                    // Partial progress - interpolate between default and failed color
                    float progress = Mathf.Clamp01((float)currentValue / quota);
                    _textMesh.color = Color.Lerp(quotaFailedColor, defaultColor, progress);
                }
                else
                {
                    _textMesh.color = defaultColor;
                }
            }
            else
            {
                _textMesh.color = defaultColor;
            }
        }

        /// <summary>
        /// Force an immediate update of the display (useful when quota changes).
        /// </summary>
        public void ForceUpdate()
        {
            _lastUpdateTime = 0f;
            UpdateDisplay();
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            // Draw a line from the zone to the text position
            if (deliveryZone != null)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(deliveryZone.transform.position, transform.position);
                Gizmos.DrawWireSphere(transform.position, 0.5f);
            }
        }
#endif
    }
}