using UnityEngine;
using Unity.Netcode;

namespace DeliverHere.Player
{
    /// <summary>
    /// Handles players sticking to moving platforms by applying platform velocity compensation.
    /// Works with both Rigidbody and animated platforms in a networked environment.
    /// Does NOT use parenting (which only works on server).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public class PlayerPlatformStick : NetworkBehaviour
    {
        [Header("Platform Detection")]
        [Tooltip("Layers considered as platforms (e.g., MovingPlatform layer).")]
        [SerializeField] private LayerMask platformLayers = ~0;

        [Tooltip("Distance below feet to check for platforms.")]
        [SerializeField] private float groundCheckDistance = 0.2f;

        [Tooltip("Radius of sphere cast for ground detection.")]
        [SerializeField] private float groundCheckRadius = 0.3f;

        [Tooltip("If true, only stick to objects tagged as 'MovingPlatform'.")]
        [SerializeField] private bool requirePlatformTag = false;

        [Tooltip("Tag required for platforms (if requirePlatformTag is true).")]
        [SerializeField] private string platformTag = "MovingPlatform";

        [Header("Movement Compensation")]
        [Tooltip("Smoothing factor for velocity compensation (0-1, higher = smoother but less responsive).")]
        [Range(0f, 1f)]
        [SerializeField] private float velocitySmoothing = 0.15f;

        [Tooltip("Multiplier for platform velocity application (1.0 = exact match).")]
        [Range(0.5f, 1.5f)]
        [SerializeField] private float velocityMultiplier = 1.0f;

        [Header("Advanced Settings")]
        [Tooltip("Minimum platform velocity (m/s) to trigger sticking behavior.")]
        [SerializeField] private float minPlatformVelocity = 0.01f;

        [Tooltip("Maximum platform velocity (m/s) to accept (prevents extreme values).")]
        [SerializeField] private float maxPlatformVelocity = 50f;

        [Tooltip("If true, only stick when grounded according to CharacterController.")]
        [SerializeField] private bool requireGrounded = true;

        [Tooltip("Delay before unsticking from platform after leaving (prevents jitter).")]
        [SerializeField] private float unstickDelay = 0.1f;

        [Tooltip("Additional downward force to help stick to platform (prevents floating).")]
        [SerializeField] private float stickingForce = 2f;

        [Header("Rotation Compensation")]
        [Tooltip("If true, rotate player with platform rotation.")]
        [SerializeField] private bool compensateRotation = true;

        [Tooltip("How fast to rotate with platform (lower = smoother).")]
        [Range(0f, 1f)]
        [SerializeField] private float rotationSmoothing = 0.2f;

        [Header("References")]
        [SerializeField] private CharacterController characterController;
        [SerializeField] private PlayerMovement playerMovement;

        [Header("Debug")]
        [SerializeField] private bool showDebugLogs = false;
        [SerializeField] private bool showDebugGizmos = true;

        // Current platform state
        private Transform _currentPlatform;
        private Rigidbody _currentPlatformRb;
        private GamePlay.SplineVehicleVelocityTracker _currentPlatformTracker;
        private Vector3 _platformVelocity;
        private Vector3 _smoothedPlatformVelocity;

        // Platform tracking for rotation
        private Vector3 _lastPlatformPosition;
        private Quaternion _lastPlatformRotation;
        private Vector3 _localPositionOnPlatform;
        private float _lastRotationAngle;

        // Unstick timing
        private float _lastPlatformContactTime;
        private bool _wasOnPlatform;

        // Frame tracking
        private Vector3 _lastPlayerPosition;

        // Public properties
        public bool IsOnPlatform => _currentPlatform != null;
        public Vector3 PlatformVelocity => _platformVelocity;
        public Transform CurrentPlatform => _currentPlatform;

        private void Awake()
        {
            if (characterController == null)
                characterController = GetComponent<CharacterController>();

            if (playerMovement == null)
                playerMovement = GetComponent<PlayerMovement>();

            _lastPlayerPosition = transform.position;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Only run on owner (prevents conflicts with server physics)
            enabled = IsOwner;
        }

        public override void OnGainedOwnership()
        {
            base.OnGainedOwnership();
            enabled = true;
        }

        public override void OnLostOwnership()
        {
            base.OnLostOwnership();
            enabled = false;
            DetachFromPlatform();
        }

        private void Update()
        {
            if (!IsOwner) return;
            if (characterController == null) return;

            // Check if we should be on a platform
            Transform detectedPlatform = DetectPlatform();

            // Handle platform attachment/detachment
            if (detectedPlatform != null)
            {
                _lastPlatformContactTime = Time.time;

                if (_currentPlatform != detectedPlatform)
                {
                    // New platform detected
                    AttachToPlatform(detectedPlatform);
                }
                else
                {
                    // Already on this platform, update velocity and apply compensation
                    UpdatePlatformVelocity();
                    ApplyPlatformCompensation();
                }

                _wasOnPlatform = true;
            }
            else
            {
                // No platform detected
                bool shouldDetach = _wasOnPlatform && (Time.time - _lastPlatformContactTime) >= unstickDelay;

                if (shouldDetach)
                {
                    DetachFromPlatform();
                    _wasOnPlatform = false;
                }
            }

            _lastPlayerPosition = transform.position;
        }

        private Transform DetectPlatform()
        {
            // Optionally require grounded state
            if (requireGrounded && characterController != null && !characterController.isGrounded)
                return null;

            // Sphere cast down from player feet
            Vector3 origin = transform.position + Vector3.up * groundCheckRadius;
            RaycastHit hit;

            bool detected = Physics.SphereCast(
                origin,
                groundCheckRadius,
                Vector3.down,
                out hit,
                groundCheckDistance + groundCheckRadius,
                platformLayers,
                QueryTriggerInteraction.Ignore
            );

            if (!detected)
                return null;

            // Check tag requirement
            if (requirePlatformTag && !hit.collider.CompareTag(platformTag))
                return null;

            // Get the root transform (could be nested)
            Transform platformTransform = hit.collider.transform;

            // Try to find Rigidbody or SplineVehicleVelocityTracker in parent hierarchy
            Rigidbody rb = platformTransform.GetComponentInParent<Rigidbody>();
            if (rb != null)
                platformTransform = rb.transform;

            return platformTransform;
        }

        private void AttachToPlatform(Transform platform)
        {
            if (showDebugLogs)
                Debug.Log($"[PlayerPlatformStick] Attaching to platform: {platform.name}");

            _currentPlatform = platform;
            _currentPlatformRb = platform.GetComponent<Rigidbody>();
            _currentPlatformTracker = platform.GetComponent<GamePlay.SplineVehicleVelocityTracker>();

            // Store initial platform state
            _lastPlatformPosition = platform.position;
            _lastPlatformRotation = platform.rotation;

            // Calculate local position on platform for rotation compensation
            _localPositionOnPlatform = platform.InverseTransformPoint(transform.position);
            _lastRotationAngle = platform.eulerAngles.y;

            // Initialize velocity
            UpdatePlatformVelocity();
            _smoothedPlatformVelocity = _platformVelocity;
        }

        private void DetachFromPlatform()
        {
            if (_currentPlatform == null)
                return;

            if (showDebugLogs)
                Debug.Log($"[PlayerPlatformStick] Detaching from platform: {_currentPlatform.name}");

            _currentPlatform = null;
            _currentPlatformRb = null;
            _currentPlatformTracker = null;
            _platformVelocity = Vector3.zero;
            _smoothedPlatformVelocity = Vector3.zero;
        }

        private void UpdatePlatformVelocity()
        {
            if (_currentPlatform == null)
            {
                _platformVelocity = Vector3.zero;
                return;
            }

            Vector3 velocity = Vector3.zero;

            // Priority 1: Check for SplineVehicleVelocityTracker (for animated platforms)
            if (_currentPlatformTracker != null)
            {
                velocity = _currentPlatformTracker.GetTrackedVelocity();

                if (showDebugLogs && velocity.magnitude > minPlatformVelocity)
                    Debug.Log($"[PlayerPlatformStick] Using tracked velocity from SplineVehicleVelocityTracker: {velocity.magnitude:F2} m/s");
            }
            // Priority 2: Check Rigidbody velocity
            else if (_currentPlatformRb != null && !_currentPlatformRb.isKinematic)
            {
                velocity = _currentPlatformRb.linearVelocity;

                if (showDebugLogs && velocity.magnitude > minPlatformVelocity)
                    Debug.Log($"[PlayerPlatformStick] Using Rigidbody velocity: {velocity.magnitude:F2} m/s");
            }
            // Priority 3: Calculate from position delta
            else
            {
                float deltaTime = Time.deltaTime;
                if (deltaTime > 0.0001f)
                {
                    Vector3 positionDelta = _currentPlatform.position - _lastPlatformPosition;
                    velocity = positionDelta / deltaTime;

                    if (showDebugLogs && velocity.magnitude > minPlatformVelocity)
                        Debug.Log($"[PlayerPlatformStick] Calculated velocity from position delta: {velocity.magnitude:F2} m/s");
                }
            }

            // Clamp velocity to reasonable limits
            if (velocity.magnitude > maxPlatformVelocity)
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[PlayerPlatformStick] Platform velocity {velocity.magnitude:F2} exceeds max {maxPlatformVelocity}, clamping.");

                velocity = velocity.normalized * maxPlatformVelocity;
            }

            _platformVelocity = velocity;

            // Smooth velocity for compensation
            if (velocitySmoothing > 0f)
            {
                _smoothedPlatformVelocity = Vector3.Lerp(_smoothedPlatformVelocity, _platformVelocity, 1f - velocitySmoothing);
            }
            else
            {
                _smoothedPlatformVelocity = _platformVelocity;
            }

            // Update last platform state
            _lastPlatformPosition = _currentPlatform.position;
            _lastPlatformRotation = _currentPlatform.rotation;
        }

        private void ApplyPlatformCompensation()
        {
            if (characterController == null || _currentPlatform == null)
                return;

            // Apply linear velocity compensation
            if (_smoothedPlatformVelocity.magnitude >= minPlatformVelocity)
            {
                Vector3 compensation = _smoothedPlatformVelocity * velocityMultiplier * Time.deltaTime;

                // Add extra downward force to stick better
                if (stickingForce > 0f)
                {
                    compensation.y -= stickingForce * Time.deltaTime;
                }

                characterController.Move(compensation);

                if (showDebugLogs)
                    Debug.Log($"[PlayerPlatformStick] Applied velocity compensation: {compensation.magnitude:F3} m this frame");
            }

            // Apply rotation compensation
            if (compensateRotation)
            {
                ApplyRotationCompensation();
            }
        }

        private void ApplyRotationCompensation()
        {
            if (_currentPlatform == null)
                return;

            // Get the current platform rotation angle
            float currentAngle = _currentPlatform.eulerAngles.y;
            float angleDelta = Mathf.DeltaAngle(_lastRotationAngle, currentAngle);

            if (Mathf.Abs(angleDelta) > 0.01f)
            {
                // Calculate where the player should be based on platform rotation
                Vector3 worldPosOnPlatform = _currentPlatform.TransformPoint(_localPositionOnPlatform);
                Vector3 offset = worldPosOnPlatform - transform.position;

                // Only apply horizontal offset (Y is handled by regular movement)
                offset.y = 0f;

                if (offset.magnitude > 0.001f)
                {
                    // Smoothly move to the correct position
                    float smoothing = 1f - rotationSmoothing;
                    Vector3 moveAmount = offset * smoothing;
                    characterController.Move(moveAmount);

                    if (showDebugLogs)
                        Debug.Log($"[PlayerPlatformStick] Applied rotation compensation: {moveAmount.magnitude:F3} m, angle delta: {angleDelta:F2}°");
                }

                // Rotate the player to match platform rotation
                float rotationAmount = angleDelta * (1f - rotationSmoothing);
                transform.Rotate(Vector3.up, rotationAmount, Space.World);

                // Update local position on platform
                _localPositionOnPlatform = _currentPlatform.InverseTransformPoint(transform.position);
            }

            _lastRotationAngle = currentAngle;
        }

        /// <summary>
        /// Public method to get the velocity to add to player movement.
        /// Call this from PlayerMovement if you want manual control.
        /// </summary>
        public Vector3 GetPlatformVelocityCompensation()
        {
            if (_currentPlatform == null)
                return Vector3.zero;

            return _smoothedPlatformVelocity;
        }

        /// <summary>
        /// Force detach from current platform.
        /// </summary>
        public void ForceDetach()
        {
            DetachFromPlatform();
            _wasOnPlatform = false;
        }

        private void OnDisable()
        {
            // Clean up when disabled
            if (_currentPlatform != null)
            {
                DetachFromPlatform();
            }
        }

        public override void OnDestroy()
        {
            // Clean up when destroyed
            if (_currentPlatform != null)
            {
                DetachFromPlatform();
            }

            base.OnDestroy();
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!showDebugGizmos || !Application.isPlaying)
                return;

            // Draw ground check sphere
            Vector3 origin = transform.position + Vector3.up * groundCheckRadius;
            Gizmos.color = IsOnPlatform ? Color.green : Color.yellow;
            Gizmos.DrawWireSphere(origin, groundCheckRadius);
            Gizmos.DrawLine(origin, origin + Vector3.down * (groundCheckDistance + groundCheckRadius));

            // Draw platform velocity
            if (IsOnPlatform && _platformVelocity.magnitude > minPlatformVelocity)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(transform.position, transform.position + _platformVelocity);

                // Draw smoothed velocity in different color
                Gizmos.color = Color.blue;
                Gizmos.DrawLine(transform.position + Vector3.up * 0.1f, transform.position + Vector3.up * 0.1f + _smoothedPlatformVelocity);
            }

            // Draw connection to platform
            if (IsOnPlatform && _currentPlatform != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(transform.position, _currentPlatform.position);

                // Draw expected position based on platform rotation
                if (compensateRotation)
                {
                    Vector3 expectedPos = _currentPlatform.TransformPoint(_localPositionOnPlatform);
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawWireSphere(expectedPos, 0.2f);
                }
            }
        }

        private void OnValidate()
        {
            groundCheckDistance = Mathf.Max(0.01f, groundCheckDistance);
            groundCheckRadius = Mathf.Max(0.01f, groundCheckRadius);
            minPlatformVelocity = Mathf.Max(0f, minPlatformVelocity);
            maxPlatformVelocity = Mathf.Max(1f, maxPlatformVelocity);
            unstickDelay = Mathf.Max(0f, unstickDelay);
            velocitySmoothing = Mathf.Clamp01(velocitySmoothing);
            rotationSmoothing = Mathf.Clamp01(rotationSmoothing);
            stickingForce = Mathf.Max(0f, stickingForce);
        }
#endif
    }
}