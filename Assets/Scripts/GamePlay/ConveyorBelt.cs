using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Moves objects (like packages) along a conveyor belt path.
    /// Uses constant velocity for reliable sliding behavior.
    /// Server-authoritative with client visual smoothing.
    /// </summary>
    [DisallowMultipleComponent]
    public class ConveyorBelt : NetworkBehaviour
    {
        [Header("Belt Configuration")]
        [Tooltip("Speed of the conveyor belt in meters per second.")]
        [SerializeField, Min(0f)] private float beltSpeed = 2f;

        [Tooltip("Direction the belt moves objects (in local space).")]
        [SerializeField] private Vector3 beltDirection = Vector3.forward;

        [Header("Movement Method")]
        [Tooltip("Direct Velocity: Sets velocity directly (most reliable). Force-Based: Uses physics forces (more natural but less consistent).")]
        [SerializeField] private MovementMethod movementMethod = MovementMethod.DirectVelocity;

        [Tooltip("How strongly to apply the belt velocity (0-1). Lower values allow more physics interaction.")]
        [SerializeField, Range(0f, 1f)] private float velocityStrength = 0.9f;

        [Header("Height Control")]
        [Tooltip("Maintain objects at a consistent height above the belt surface.")]
        [SerializeField] private bool maintainHeight = true;

        [Tooltip("Target height above belt surface (in local Y).")]
        [SerializeField, Min(0f)] private float targetHeight = 0.15f;

        [Tooltip("How strongly to pull objects to target height (higher = snappier).")]
        [SerializeField, Range(0.1f, 50f)] private float heightCorrectionStrength = 20f;

        [Tooltip("Use the belt collider's top surface as reference height.")]
        [SerializeField] private bool useBeltSurfaceAsReference = true;

        [Header("Friction Settings")]
        [Tooltip("Apply friction to perpendicular movement (prevents sliding sideways off belt).")]
        [SerializeField] private bool applyPerpendicularFriction = true;

        [Tooltip("How much to dampen movement perpendicular to belt direction (0-1).")]
        [SerializeField, Range(0f, 1f)] private float perpendicularDamping = 0.95f;

        [Header("Rotation Control")]
        [Tooltip("Prevent objects from rotating while on belt.")]
        [SerializeField] private bool preventRotation = true;

        [Tooltip("How strongly to dampen rotation (0-1). 1 = instant stop, 0 = no damping.")]
        [SerializeField, Range(0f, 1f)] private float rotationDamping = 0.98f;

        [Tooltip("Align objects to belt rotation (keeps them upright).")]
        [SerializeField] private bool alignToSurface = true;

        [Tooltip("Speed of rotation alignment.")]
        [SerializeField, Range(0.1f, 20f)] private float alignmentSpeed = 10f;

        [Header("Physics Material")]
        [Tooltip("Optional: Physics material to apply to belt surface (should have low friction).")]
        [SerializeField] private PhysicsMaterial beltPhysicsMaterial;

        [Header("Detection Method")]
        [Tooltip("Use triggers for detection (recommended for height-locked movement).")]
        [SerializeField] private DetectionMethod detectionMethod = DetectionMethod.Trigger;

        [Header("Layer Filtering")]
        [Tooltip("Only objects on these layers will be affected by the belt.")]
        [SerializeField] private LayerMask affectedLayers = ~0;

        [Tooltip("Optional tag filter. Leave empty to affect all objects.")]
        [SerializeField] private string requiredTag = "";

        [Header("Collision Detection")]
        [Tooltip("Trigger collider for detection (should be positioned above the belt surface).")]
        [SerializeField] private Collider beltTrigger;

        [Tooltip("Optional: Surface collider for physical support (non-trigger).")]
        [SerializeField] private Collider beltSurfaceCollider;

        [Header("Visual Feedback")]
        [Tooltip("Optional renderer to animate belt surface (UV scrolling).")]
        [SerializeField] private Renderer beltRenderer;

        [Tooltip("Material property name for UV offset (usually '_MainTex' or '_BaseMap').")]
        [SerializeField] private string uvOffsetPropertyName = "_MainTex";

        [Tooltip("UV scroll speed multiplier.")]
        [SerializeField, Min(0f)] private float uvScrollSpeed = 1f;

        [Header("Debug")]
        [SerializeField] private bool showDebugGizmos = true;
        [SerializeField] private bool logObjectsOnBelt = false;
        [SerializeField] private bool logCollisionEvents = false;
        [SerializeField] private bool showHeightDebug = false;

        public enum MovementMethod
        {
            DirectVelocity,
            ForceBased
        }

        public enum DetectionMethod
        {
            Trigger,
            Collision
        }

        // Track objects currently on the belt
        private readonly HashSet<Rigidbody> _objectsOnBelt = new HashSet<Rigidbody>();

        // Store original physics properties for restoration
        private readonly Dictionary<Rigidbody, PhysicsState> _originalStates = new Dictionary<Rigidbody, PhysicsState>();

        private Vector3 _worldBeltDirection;
        private Vector2 _uvOffset;
        private Material _beltMaterial;
        private Rigidbody _beltRigidbody;
        private float _beltSurfaceHeight;

        private struct PhysicsState
        {
            public float Drag;
            public float AngularDrag;
            public RigidbodyConstraints Constraints;
            public PhysicsMaterial Material;
        }

        private void Awake()
        {
            // Get trigger collider if not assigned
            if (beltTrigger == null)
            {
                beltTrigger = GetComponent<Collider>();
                if (beltTrigger == null)
                {
                    Debug.LogError("[ConveyorBelt] No collider found! Please add a trigger collider to the belt GameObject.");
                    return;
                }
            }

            // Setup based on detection method
            if (detectionMethod == DetectionMethod.Trigger)
            {
                // Trigger mode - trigger collider must be a trigger
                if (!beltTrigger.isTrigger)
                {
                    Debug.LogWarning("[ConveyorBelt] Detection method is Trigger but collider is not a trigger. Enabling trigger mode.");
                    beltTrigger.isTrigger = true;
                }
            }
            else
            {
                // Collision mode - belt needs a Rigidbody and collider should NOT be trigger
                if (beltTrigger.isTrigger)
                {
                    Debug.LogWarning("[ConveyorBelt] Detection method is Collision but collider is a trigger. Disabling trigger mode.");
                    beltTrigger.isTrigger = false;
                }

                // Ensure belt has a Rigidbody for collision detection
                _beltRigidbody = GetComponent<Rigidbody>();
                if (_beltRigidbody == null)
                {
                    Debug.LogWarning("[ConveyorBelt] Collision detection requires a Rigidbody. Adding kinematic Rigidbody.");
                    _beltRigidbody = gameObject.AddComponent<Rigidbody>();
                    _beltRigidbody.isKinematic = true;
                    _beltRigidbody.useGravity = false;
                }
                else if (!_beltRigidbody.isKinematic)
                {
                    Debug.LogWarning("[ConveyorBelt] Belt Rigidbody should be kinematic. Setting to kinematic.");
                    _beltRigidbody.isKinematic = true;
                    _beltRigidbody.useGravity = false;
                }
            }

            // Apply physics material if assigned
            if (beltPhysicsMaterial != null && beltSurfaceCollider != null)
            {
                beltSurfaceCollider.material = beltPhysicsMaterial;
            }

            // Setup material for UV animation
            if (beltRenderer != null)
            {
                _beltMaterial = beltRenderer.material; // Creates instance
            }

            // Normalize belt direction
            if (beltDirection.sqrMagnitude > 0.001f)
            {
                beltDirection.Normalize();
            }
            else
            {
                beltDirection = Vector3.forward;
                Debug.LogWarning("[ConveyorBelt] Belt direction was zero, defaulting to forward.");
            }

            // Calculate belt surface height
            CalculateBeltSurfaceHeight();

            Debug.Log($"[ConveyorBelt] Initialized with detection method: {detectionMethod}, trigger: {beltTrigger.isTrigger}, surface height: {_beltSurfaceHeight}");
        }

        private void CalculateBeltSurfaceHeight()
        {
            if (useBeltSurfaceAsReference && beltSurfaceCollider != null)
            {
                // Use the top of the surface collider
                _beltSurfaceHeight = beltSurfaceCollider.bounds.max.y;
            }
            else if (beltTrigger != null)
            {
                // Use the bottom of the trigger collider as reference
                _beltSurfaceHeight = beltTrigger.bounds.min.y;
            }
            else
            {
                // Fallback to transform position
                _beltSurfaceHeight = transform.position.y;
            }
        }

        private void OnEnable()
        {
            UpdateWorldDirection();
            CalculateBeltSurfaceHeight();
        }

        private void Update()
        {
            UpdateWorldDirection();

            // Animate belt surface (visual only, runs on all clients)
            if (_beltMaterial != null && uvScrollSpeed > 0f)
            {
                float scrollDistance = beltSpeed * uvScrollSpeed * Time.deltaTime;
                _uvOffset.y += scrollDistance;

                // Wrap UV to prevent floating point precision issues
                if (_uvOffset.y > 10f)
                {
                    _uvOffset.y -= 10f;
                }

                _beltMaterial.SetTextureOffset(uvOffsetPropertyName, _uvOffset);
            }

            // Debug info
            if (logObjectsOnBelt && Time.frameCount % 120 == 0) // Log every 2 seconds
            {
                Debug.Log($"[ConveyorBelt] Objects on belt: {_objectsOnBelt.Count}");
            }
        }

        private void FixedUpdate()
        {
            // Only server applies physics
            if (!IsServer && NetworkManager.Singleton != null)
                return;

            // Recalculate surface height each frame (in case belt moves)
            CalculateBeltSurfaceHeight();

            // Apply conveyor movement to all objects on belt
            foreach (var rb in _objectsOnBelt)
            {
                if (rb == null)
                    continue;

                ApplyConveyorMovement(rb);
            }
        }

        private void UpdateWorldDirection()
        {
            _worldBeltDirection = transform.TransformDirection(beltDirection);
            _worldBeltDirection.Normalize();
        }

        // Trigger-based detection
        private void OnTriggerEnter(Collider other)
        {
            if (detectionMethod != DetectionMethod.Trigger) return;

            if (logCollisionEvents)
            {
                Debug.Log($"[ConveyorBelt] OnTriggerEnter: {other.name}, Layer: {LayerMask.LayerToName(other.gameObject.layer)}");
            }

            TryAddObject(other, null);
        }

        private void OnTriggerStay(Collider other)
        {
            if (detectionMethod != DetectionMethod.Trigger) return;

            // Keep trying to add in case object entered before server authority
            TryAddObject(other, null);
        }

        private void OnTriggerExit(Collider other)
        {
            if (detectionMethod != DetectionMethod.Trigger) return;

            if (logCollisionEvents)
            {
                Debug.Log($"[ConveyorBelt] OnTriggerExit: {other.name}");
            }

            TryRemoveObject(other);
        }

        // Collision-based detection
        private void OnCollisionEnter(Collision collision)
        {
            if (detectionMethod != DetectionMethod.Collision) return;

            if (logCollisionEvents)
            {
                Debug.Log($"[ConveyorBelt] OnCollisionEnter: {collision.collider.name}, Layer: {LayerMask.LayerToName(collision.collider.gameObject.layer)}");
            }

            TryAddObject(collision.collider, collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            if (detectionMethod != DetectionMethod.Collision) return;

            // Keep trying to add in case object entered before server authority
            TryAddObject(collision.collider, collision);
        }

        private void OnCollisionExit(Collision collision)
        {
            if (detectionMethod != DetectionMethod.Collision) return;

            if (logCollisionEvents)
            {
                Debug.Log($"[ConveyorBelt] OnCollisionExit: {collision.collider.name}");
            }

            TryRemoveObject(collision.collider);
        }

        private void TryAddObject(Collider other, Collision collision = null)
        {
            if (other == null)
            {
                Debug.LogWarning("[ConveyorBelt] TryAddObject: other is null");
                return;
            }

            // Layer check
            int layer = other.gameObject.layer;
            bool layerMatch = ((1 << layer) & affectedLayers.value) != 0;

            if (!layerMatch)
            {
                if (logCollisionEvents)
                {
                    Debug.Log($"[ConveyorBelt] Layer mismatch: {other.name} on layer {LayerMask.LayerToName(layer)}");
                }
                return;
            }

            // Tag check
            if (!string.IsNullOrEmpty(requiredTag) && !other.CompareTag(requiredTag))
            {
                if (logCollisionEvents)
                {
                    Debug.Log($"[ConveyorBelt] Tag mismatch: {other.name} has tag '{other.tag}' (required: '{requiredTag}')");
                }
                return;
            }

            // Get rigidbody
            Rigidbody rb = other.attachedRigidbody;
            if (rb == null)
            {
                if (logCollisionEvents)
                {
                    Debug.Log($"[ConveyorBelt] No Rigidbody: {other.name}");
                }
                return;
            }

            if (rb.isKinematic)
            {
                if (logCollisionEvents)
                {
                    Debug.Log($"[ConveyorBelt] Rigidbody is kinematic: {other.name}");
                }
                return;
            }

            // Check if object is actually on top of the belt (for collision method)
            if (detectionMethod == DetectionMethod.Collision && collision != null)
            {
                bool isOnTop = false;
                foreach (ContactPoint contact in collision.contacts)
                {
                    // Check if contact normal points upward (object is on top)
                    if (Vector3.Dot(contact.normal, Vector3.up) > 0.3f)
                    {
                        isOnTop = true;
                        break;
                    }
                }

                if (!isOnTop)
                {
                    if (logCollisionEvents)
                    {
                        Debug.Log($"[ConveyorBelt] Not on top: {other.name}");
                    }
                    return;
                }
            }

            // Add to tracking
            if (_objectsOnBelt.Add(rb))
            {
                // Store original physics state
                if (!_originalStates.ContainsKey(rb))
                {
                    Collider objCollider = other;
                    if (objCollider == null)
                        objCollider = rb.GetComponent<Collider>();

                    _originalStates[rb] = new PhysicsState
                    {
                        Drag = rb.linearDamping,
                        AngularDrag = rb.angularDamping,
                        Constraints = rb.constraints,
                        Material = objCollider != null ? objCollider.material : null
                    };
                }

                // Disable gravity while on belt (height control will handle vertical position)
                if (maintainHeight)
                {
                    rb.useGravity = false;
                }

                if (logObjectsOnBelt)
                {
                    Debug.Log($"[ConveyorBelt] ✓ Object ADDED to belt: {other.name} (Total on belt: {_objectsOnBelt.Count})");
                }
            }
        }

        private void TryRemoveObject(Collider other)
        {
            if (other == null)
                return;

            Rigidbody rb = other.attachedRigidbody;
            if (rb == null)
                return;

            if (_objectsOnBelt.Remove(rb))
            {
                // Restore original physics state
                if (_originalStates.TryGetValue(rb, out PhysicsState state))
                {
                    rb.linearDamping = state.Drag;
                    rb.angularDamping = state.AngularDrag;
                    rb.constraints = state.Constraints;
                    rb.useGravity = true; // Re-enable gravity when leaving belt

                    // Restore material if we have one
                    if (state.Material != null)
                    {
                        Collider objCollider = rb.GetComponent<Collider>();
                        if (objCollider != null)
                        {
                            objCollider.material = state.Material;
                        }
                    }

                    _originalStates.Remove(rb);
                }

                if (logObjectsOnBelt)
                {
                    Debug.Log($"[ConveyorBelt] ✗ Object REMOVED from belt: {other.name} (Total on belt: {_objectsOnBelt.Count})");
                }
            }
        }

        private void ApplyConveyorMovement(Rigidbody rb)
        {
            if (rb == null || rb.isKinematic)
                return;

            // Get current velocity
            Vector3 currentVelocity = rb.linearVelocity;

            // Calculate belt velocity
            Vector3 beltVelocity = _worldBeltDirection * beltSpeed;

            // === HEIGHT CONTROL ===
            if (maintainHeight)
            {
                float currentHeight = rb.position.y;
                float desiredHeight = _beltSurfaceHeight + targetHeight;
                float heightError = desiredHeight - currentHeight;

                // Apply vertical correction
                float verticalVelocity = heightError * heightCorrectionStrength;
                currentVelocity.y = verticalVelocity;

                if (showHeightDebug)
                {
                    Debug.DrawLine(rb.position, new Vector3(rb.position.x, desiredHeight, rb.position.z), Color.cyan);
                }
            }

            // === HORIZONTAL MOVEMENT ===
            if (movementMethod == MovementMethod.DirectVelocity)
            {
                // Direct velocity method (most reliable)

                // Get horizontal velocity components
                Vector3 horizontalVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z);
                float velocityAlongBelt = Vector3.Dot(horizontalVelocity, _worldBeltDirection);
                Vector3 perpendicularVelocity = horizontalVelocity - (_worldBeltDirection * velocityAlongBelt);

                // Build new horizontal velocity
                Vector3 newHorizontalVelocity = beltVelocity * velocityStrength + horizontalVelocity * (1f - velocityStrength);

                // Apply perpendicular friction
                if (applyPerpendicularFriction)
                {
                    newHorizontalVelocity += perpendicularVelocity * (1f - perpendicularDamping);
                }

                // Combine horizontal and vertical velocity
                currentVelocity.x = newHorizontalVelocity.x;
                currentVelocity.z = newHorizontalVelocity.z;

                // Set velocity
                rb.linearVelocity = currentVelocity;
            }
            else
            {
                // Force-based method (more natural physics)
                Vector3 horizontalVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z);
                float velocityAlongBelt = Vector3.Dot(horizontalVelocity, _worldBeltDirection);
                float velocityDelta = beltSpeed - velocityAlongBelt;
                Vector3 force = _worldBeltDirection * (velocityDelta * rb.mass * 10f);
                rb.AddForce(force, ForceMode.Force);

                // Apply perpendicular friction
                if (applyPerpendicularFriction)
                {
                    Vector3 perpVelocity = horizontalVelocity - (_worldBeltDirection * velocityAlongBelt);
                    rb.AddForce(-perpVelocity * perpendicularDamping * rb.mass, ForceMode.Force);
                }
            }

            // === ROTATION CONTROL ===
            if (preventRotation)
            {
                if (rotationDamping >= 0.99f)
                {
                    // Complete stop
                    rb.angularVelocity = Vector3.zero;
                }
                else
                {
                    // Gradual damping
                    rb.angularVelocity *= (1f - rotationDamping);
                }
            }

            // === ALIGNMENT TO SURFACE ===
            if (alignToSurface)
            {
                // Calculate target rotation (aligned with belt, upright)
                Quaternion targetRotation = Quaternion.LookRotation(_worldBeltDirection, Vector3.up);

                // Smoothly rotate towards target
                Quaternion newRotation = Quaternion.Slerp(rb.rotation, targetRotation, alignmentSpeed * Time.fixedDeltaTime);
                rb.MoveRotation(newRotation);
            }
        }

        private void OnDestroy()
        {
            // Restore all objects on belt
            foreach (var rb in _objectsOnBelt)
            {
                if (rb != null && _originalStates.TryGetValue(rb, out PhysicsState state))
                {
                    rb.linearDamping = state.Drag;
                    rb.angularDamping = state.AngularDrag;
                    rb.constraints = state.Constraints;
                    rb.useGravity = true;

                    if (state.Material != null)
                    {
                        Collider objCollider = rb.GetComponent<Collider>();
                        if (objCollider != null)
                        {
                            objCollider.material = state.Material;
                        }
                    }
                }
            }

            _objectsOnBelt.Clear();
            _originalStates.Clear();

            // Cleanup material instance
            if (_beltMaterial != null)
            {
                Destroy(_beltMaterial);
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            beltSpeed = Mathf.Max(0f, beltSpeed);
            targetHeight = Mathf.Max(0f, targetHeight);
            heightCorrectionStrength = Mathf.Max(0.1f, heightCorrectionStrength);
            uvScrollSpeed = Mathf.Max(0f, uvScrollSpeed);
            velocityStrength = Mathf.Clamp01(velocityStrength);
            perpendicularDamping = Mathf.Clamp01(perpendicularDamping);
            rotationDamping = Mathf.Clamp01(rotationDamping);
            alignmentSpeed = Mathf.Max(0.1f, alignmentSpeed);

            if (beltTrigger == null)
            {
                beltTrigger = GetComponent<Collider>();
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!showDebugGizmos)
                return;

            UpdateWorldDirection();
            CalculateBeltSurfaceHeight();

            // Draw belt direction
            Gizmos.color = Color.green;
            Vector3 center = beltTrigger != null ? beltTrigger.bounds.center : transform.position;
            Vector3 endPoint = center + _worldBeltDirection * 2f;

            Gizmos.DrawLine(center, endPoint);

            // Draw arrowhead
            Vector3 right = Vector3.Cross(_worldBeltDirection, Vector3.up).normalized * 0.2f;
            Vector3 arrowTip = endPoint;
            Gizmos.DrawLine(arrowTip, arrowTip - _worldBeltDirection * 0.3f + right);
            Gizmos.DrawLine(arrowTip, arrowTip - _worldBeltDirection * 0.3f - right);

            // Draw trigger bounds
            if (beltTrigger != null)
            {
                Gizmos.color = new Color(0.2f, 1f, 0.2f, 0.3f);
                Gizmos.DrawCube(beltTrigger.bounds.center, beltTrigger.bounds.size);
            }

            // Draw surface collider bounds
            if (beltSurfaceCollider != null)
            {
                Gizmos.color = new Color(0.2f, 0.2f, 1f, 0.3f);
                Gizmos.DrawCube(beltSurfaceCollider.bounds.center, beltSurfaceCollider.bounds.size);
            }

            // Draw target height plane
            if (maintainHeight && beltTrigger != null)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.5f);
                Vector3 heightCenter = new Vector3(center.x, _beltSurfaceHeight + targetHeight, center.z);
                Vector3 size = beltTrigger.bounds.size;
                size.y = 0.02f;
                Gizmos.DrawCube(heightCenter, size);
            }

            // Draw speed indicator text
#if UNITY_EDITOR
            string detectionInfo = detectionMethod == DetectionMethod.Trigger ? "TRIGGER" : "COLLISION";
            string heightInfo = maintainHeight ? $"\nHeight: {targetHeight:F2}m" : "";
            UnityEditor.Handles.Label(center + Vector3.up * 0.5f,
                $"Belt Speed: {beltSpeed:F1} m/s\nMethod: {movementMethod}\nDetection: {detectionInfo}{heightInfo}\nObjects: {_objectsOnBelt.Count}");
#endif
        }
#endif
    }
}