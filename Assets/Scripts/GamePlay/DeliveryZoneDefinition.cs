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
        [Tooltip("List of visual markers (can include particle systems, lights, etc.).")]
        [SerializeField] private GameObject[] visualMarkers = new GameObject[0];
        [SerializeField] private Color markerColor = Color.green;
        [Tooltip("If true, visual markers start hidden and only show when zone is active.")]
        [SerializeField] private bool hideWhenInactive = true;

        [Header("Animation")]
        [Tooltip("Animator component to trigger animations when zone activates.")]
        [SerializeField] private Animator zoneAnimator;
        [Tooltip("If true, auto-find Animator on this GameObject or visual markers.")]
        [SerializeField] private bool autoFindAnimator = true;
        [Tooltip("Animation trigger parameter name (e.g., 'Activate', 'Show').")]
        [SerializeField] private string activationTriggerName = "Activate";
        [Tooltip("Optional: Animation trigger for deactivation (e.g., 'Deactivate', 'Hide').")]
        [SerializeField] private string deactivationTriggerName = "";
        [Tooltip("If true, play animation on first activation (when zone is first selected).")]
        [SerializeField] private bool playAnimationOnFirstActivation = true;

        private PackageDeliveryZone _deliveryZone;
        private ParticleSystem[] _particleSystems;
        private bool _isActive = false;
        private bool _hasBeenActivatedBefore = false; // Track if zone was ever activated (to prevent animation during scene setup)

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

        private void Awake()
        {
            // Auto-find animator if enabled and not assigned
            if (autoFindAnimator && zoneAnimator == null)
            {
                FindAnimator();
            }

            // Cache all particle systems from visual markers
            CacheParticleSystems();

            // Initialize zone to inactive state (no animation, just set initial state)
            InitializeToInactiveState();
        }

        /// <summary>
        /// Initializes the zone to an inactive state WITHOUT triggering animations.
        /// This is called during Awake to set up the initial visual state.
        /// </summary>
        private void InitializeToInactiveState()
        {
            _isActive = false;
            _hasBeenActivatedBefore = false; // Zone has never been activated yet

            // Silently hide visual markers without triggering animations
            if (hideWhenInactive)
            {
                SetVisualMarkersActive(false);
            }

            // Ensure delivery zone is disabled initially
            if (DeliveryZone != null)
                DeliveryZone.enabled = false;
        }

        /// <summary>
        /// Attempts to find an Animator component on this GameObject or visual markers.
        /// </summary>
        private void FindAnimator()
        {
            // First, try this GameObject
            zoneAnimator = GetComponent<Animator>();

            // If not found, search visual markers
            if (zoneAnimator == null && visualMarkers != null)
            {
                foreach (var marker in visualMarkers)
                {
                    if (marker == null) continue;

                    var anim = marker.GetComponentInChildren<Animator>();
                    if (anim != null)
                    {
                        zoneAnimator = anim;
                        break;
                    }
                }
            }

            // Last resort: search children
            if (zoneAnimator == null)
            {
                zoneAnimator = GetComponentInChildren<Animator>();
            }
        }

        /// <summary>
        /// Finds and caches all particle systems in visual markers.
        /// </summary>
        private void CacheParticleSystems()
        {
            if (visualMarkers == null || visualMarkers.Length == 0)
            {
                _particleSystems = new ParticleSystem[0];
                return;
            }

            var particleList = new System.Collections.Generic.List<ParticleSystem>();

            foreach (var marker in visualMarkers)
            {
                if (marker == null) continue;

                // Get particle systems from this marker and its children
                var particles = marker.GetComponentsInChildren<ParticleSystem>(true);
                if (particles != null && particles.Length > 0)
                {
                    particleList.AddRange(particles);
                }
            }

            _particleSystems = particleList.ToArray();
        }

        /// <summary>
        /// Activates this zone for the current day (enables visuals and functionality).
        /// Plays activation animation based on settings.
        /// </summary>
        public void ActivateZone()
        {
            _isActive = true;

            // Show visual markers and play particles
            SetVisualMarkersActive(true);

            // Play activation animation:
            // - Always play if zone was previously activated (subsequent activations)
            // - On first activation, only play if playAnimationOnFirstActivation is true
            bool shouldPlayAnimation = _hasBeenActivatedBefore || playAnimationOnFirstActivation;
            
            if (shouldPlayAnimation)
            {
                PlayActivationAnimation();
            }

            // Mark that this zone has been activated at least once
            _hasBeenActivatedBefore = true;

            // Enable delivery zone functionality
            if (DeliveryZone != null)
                DeliveryZone.enabled = true;

            gameObject.SetActive(true);
        }

        /// <summary>
        /// Deactivates this zone (hides visuals and disables functionality).
        /// Plays deactivation animation ONLY if zone was previously active.
        /// </summary>
        public void DeactivateZone()
        {
            bool wasActive = _isActive;
            _isActive = false;

            // Only play deactivation animation if:
            // 1. We were actually active
            // 2. We have a deactivation trigger configured
            // 3. Zone has been activated before (not initial setup)
            if (wasActive && _hasBeenActivatedBefore && !string.IsNullOrEmpty(deactivationTriggerName))
            {
                PlayDeactivationAnimation();
            }

            // Hide visual markers
            if (hideWhenInactive)
            {
                SetVisualMarkersActive(false);
            }

            // Disable delivery zone functionality
            if (DeliveryZone != null)
                DeliveryZone.enabled = false;
        }

        /// <summary>
        /// Plays the activation animation using the configured trigger.
        /// </summary>
        private void PlayActivationAnimation()
        {
            if (zoneAnimator == null) return;

            // Trigger activation animation
            if (!string.IsNullOrEmpty(activationTriggerName))
            {
                zoneAnimator.SetTrigger(activationTriggerName);
            }
        }

        /// <summary>
        /// Plays the deactivation animation using the configured trigger.
        /// </summary>
        private void PlayDeactivationAnimation()
        {
            if (zoneAnimator == null) return;

            // Trigger deactivation animation if specified
            if (!string.IsNullOrEmpty(deactivationTriggerName))
            {
                zoneAnimator.SetTrigger(deactivationTriggerName);
            }
        }

        /// <summary>
        /// Controls all visual markers and their particle systems.
        /// </summary>
        private void SetVisualMarkersActive(bool active)
        {
            if (visualMarkers == null || visualMarkers.Length == 0) return;

            if (active)
            {
                // Activate all visual markers
                foreach (var marker in visualMarkers)
                {
                    if (marker != null)
                        marker.SetActive(true);
                }

                // Start all particle systems
                if (_particleSystems != null)
                {
                    foreach (var ps in _particleSystems)
                    {
                        if (ps != null)
                            ps.Play();
                    }
                }
            }
            else
            {
                // Stop and clear all particle systems first
                if (_particleSystems != null)
                {
                    foreach (var ps in _particleSystems)
                    {
                        if (ps != null)
                            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    }
                }

                // Deactivate all visual markers
                foreach (var marker in visualMarkers)
                {
                    if (marker != null)
                        marker.SetActive(false);
                }
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            // Only draw gizmos if zone is active or in edit mode
            if (!Application.isPlaying || _isActive)
            {
                Gizmos.color = new Color(markerColor.r, markerColor.g, markerColor.b, 0.3f);
                Gizmos.DrawWireSphere(WorldPosition, 2f);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(markerColor.r, markerColor.g, markerColor.b, 0.5f);
            Gizmos.DrawSphere(WorldPosition, 2f);
            
            // Draw name label in editor
            UnityEditor.Handles.Label(WorldPosition + Vector3.up * 3f, zoneName);

            // Draw lines to visual markers
            if (visualMarkers != null)
            {
                Gizmos.color = Color.cyan;
                foreach (var marker in visualMarkers)
                {
                    if (marker != null)
                    {
                        Gizmos.DrawLine(WorldPosition, marker.transform.position);
                        Gizmos.DrawWireCube(marker.transform.position, Vector3.one * 0.5f);
                    }
                }
            }
        }

        private void OnValidate()
        {
            // In edit mode, ensure visual markers state matches hideWhenInactive setting
            if (!Application.isPlaying && visualMarkers != null && hideWhenInactive)
            {
                foreach (var marker in visualMarkers)
                {
                    if (marker != null)
                        marker.SetActive(false);
                }
            }

            // Auto-find animator if the option is enabled
            if (autoFindAnimator && zoneAnimator == null)
            {
                FindAnimator();
            }
        }

        [ContextMenu("Debug/Test Activate")]
        private void Debug_TestActivate()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Test Activate only works in Play mode.");
                return;
            }
            
            ActivateZone();
            Debug.Log($"[{zoneName}] Activated. HasBeenActivatedBefore: {_hasBeenActivatedBefore}");
        }

        [ContextMenu("Debug/Test Deactivate")]
        private void Debug_TestDeactivate()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Test Deactivate only works in Play mode.");
                return;
            }
            DeactivateZone();
            Debug.Log($"[{zoneName}] Deactivated.");
        }

        [ContextMenu("Debug/Cache Particle Systems")]
        private void Debug_CacheParticles()
        {
            CacheParticleSystems();
            Debug.Log($"[{zoneName}] Cached {_particleSystems?.Length ?? 0} particle system(s).");
        }

        [ContextMenu("Debug/Find Animator")]
        private void Debug_FindAnimator()
        {
            FindAnimator();
            Debug.Log($"[{zoneName}] Animator: {(zoneAnimator != null ? zoneAnimator.name : "Not Found")}");
        }
#endif
    }
}