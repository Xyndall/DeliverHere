using UnityEngine;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Represents a single delivery zone location with metadata.
    /// Attach this to delivery zone GameObjects or define as ScriptableObject.
    /// </summary>
    [DisallowMultipleComponent]
    public class DeliveryZoneDefinition : MonoBehaviour
    {
        [Header("Zone Identity")]
        [SerializeField] private string zoneName = "Delivery Point";
        [SerializeField, TextArea(2, 4)] private string zoneDescription = "Deliver packages here.";

        [Header("Location")]
        [Tooltip("Optional custom position override. If null, uses transform.position.")]
        [SerializeField] private Transform locationOverride;

        [Header("Visual/Audio Feedback")]
        [SerializeField] private GameObject visualMarker;
        [SerializeField] private Color markerColor = Color.green;

        private PackageDeliveryZone _deliveryZone;

        public string ZoneName => zoneName;
        public string Description => zoneDescription;
        public Vector3 WorldPosition => locationOverride != null ? locationOverride.position : transform.position;
        public PackageDeliveryZone DeliveryZone
        {
            get
            {
                if (_deliveryZone == null)
                    _deliveryZone = GetComponent<PackageDeliveryZone>();
                return _deliveryZone;
            }
        }

        /// <summary>
        /// Activates this zone for the current day (enables visuals and functionality).
        /// </summary>
        public void ActivateZone()
        {
            if (visualMarker != null)
                visualMarker.SetActive(true);

            if (DeliveryZone != null)
                DeliveryZone.enabled = true;

            gameObject.SetActive(true);
        }

        /// <summary>
        /// Deactivates this zone (hides visuals and disables functionality).
        /// </summary>
        public void DeactivateZone()
        {
            if (visualMarker != null)
                visualMarker.SetActive(false);

            if (DeliveryZone != null)
                DeliveryZone.enabled = false;

            // Optional: fully disable GameObject or just hide components
            // gameObject.SetActive(false);
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            Gizmos.color = markerColor;
            Gizmos.DrawWireSphere(WorldPosition, 2f);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(markerColor.r, markerColor.g, markerColor.b, 0.3f);
            Gizmos.DrawSphere(WorldPosition, 2f);
            
            // Draw name label in editor
            UnityEditor.Handles.Label(WorldPosition + Vector3.up * 3f, zoneName);
        }
#endif
    }
}