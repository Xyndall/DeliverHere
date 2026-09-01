using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

namespace DeliverHere.GamePlay
{

    [DisallowMultipleComponent]
    public class ConveyorBelt : NetworkBehaviour
    {
        [Header("Belt Configuration")]
        [Tooltip("Speed of the conveyor belt in meters per second.")]
        [SerializeField, Min(0f)] private float beltSpeed = 2f;

        [Tooltip("Direction the belt moves objects (in local space).")]
        [SerializeField] private Vector3 beltDirection = Vector3.forward;

        [Header("Contact Detection")]
        [Tooltip("Fallback check: minimum upward alignment (dot with belt's local up) a contact normal must have to count as \"resting on top\", used only if height-based detection is disabled.")]
        [SerializeField, Range(0f, 1f)] private float minUpwardContactDot = 0.3f;

        [Tooltip("Use the contact point's height relative to the belt's top surface instead of the contact normal. More reliable for tumbling/rotated objects.")]
        [SerializeField] private bool useHeightBasedDetection = true;

        [Tooltip("How far below/above the belt's top surface (meters) a contact point can be and still count as \"on top\".")]
        [SerializeField, Min(0f)] private float topSurfaceTolerance = 0.15f;

        [Header("Friction Settings")]
        [Tooltip("Apply friction to velocity perpendicular to the belt direction (prevents sliding sideways off the belt).")]
        [SerializeField] private bool applyPerpendicularFriction = true;

        [Tooltip("How much to dampen horizontal velocity perpendicular to the belt direction per physics step (0-1).")]
        [SerializeField, Range(0f, 1f)] private float perpendicularDamping = 0.2f;

        [Header("Anti-Tumble (Sliding Behavior)")]
        [Tooltip("Freeze rotation while objects rest on the belt so they slide flat instead of rolling/tumbling, like a real conveyor. Rotation is restored the instant they leave the belt.")]
        [SerializeField] private bool freezeRotationOnBelt = true;

        [Header("Layer Filtering")]
        [Tooltip("Only objects on these layers will be affected by the belt.")]
        [SerializeField] private LayerMask affectedLayers = ~0;

        [Tooltip("Optional tag filter. Leave empty to affect all objects.")]
        [SerializeField] private string requiredTag = "";

        [Header("Collision")]
        [Tooltip("Solid (non-trigger) collider representing the belt surface. Objects rest on this via normal physics.")]
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
        [SerializeField] private bool logCollisionEvents = false;

        private Vector3 _worldBeltDirection;
        private Vector2 _uvOffset;
        private Material _beltMaterial;

        // Tracks rigidbodies currently frozen by this belt, along with their original
        // constraints so they can be restored exactly when the object leaves the belt.
        private readonly Dictionary<Rigidbody, RigidbodyConstraints> _frozenRigidbodies = new Dictionary<Rigidbody, RigidbodyConstraints>();

        private void Awake()
        {
            if (beltSurfaceCollider == null)
            {
                beltSurfaceCollider = GetComponent<Collider>();
            }

            if (beltSurfaceCollider != null && beltSurfaceCollider.isTrigger)
            {
                Debug.LogWarning("[ConveyorBelt] Belt surface collider was set as a trigger. Disabling trigger so objects can physically rest on it.");
                beltSurfaceCollider.isTrigger = false;
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
        }

        private void OnEnable()
        {
            UpdateWorldDirection();
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
        }

        private void FixedUpdate()
        {
            // Only the server tracks/restores frozen rigidbodies.
            if (!IsServer && NetworkManager.Singleton != null)
                return;

            if (_frozenRigidbodies.Count == 0)
                return;

            // Safety net: if a frozen object was destroyed or pooled (SetActive(false)) while
            // still on the belt, OnCollisionExit never fires for it. Clean those up here so we
            // never leak a frozen rotation state onto a reused/pooled object.
            List<Rigidbody> toRestore = null;
            foreach (var kvp in _frozenRigidbodies)
            {
                var rb = kvp.Key;
                if (rb == null || !rb.gameObject.activeInHierarchy)
                {
                    (toRestore ??= new List<Rigidbody>()).Add(rb);
                }
            }

            if (toRestore != null)
            {
                foreach (var rb in toRestore)
                {
                    if (rb != null)
                    {
                        rb.constraints = _frozenRigidbodies[rb];
                    }
                    _frozenRigidbodies.Remove(rb);
                }
            }
        }

        private void UpdateWorldDirection()
        {
            _worldBeltDirection = transform.TransformDirection(beltDirection);
            _worldBeltDirection.Normalize();
        }

        private void OnCollisionEnter(Collision collision)
        {
            TryApplyConveyor(collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            TryApplyConveyor(collision);
        }

        private void OnCollisionExit(Collision collision)
        {
            Rigidbody rb = collision.rigidbody;
            RestoreRotation(rb);
        }

        private void TryApplyConveyor(Collision collision)
        {
            // Only the server pushes objects; clients receive the resulting motion via replication.
            if (!IsServer && NetworkManager.Singleton != null)
                return;

            var other = collision.collider;
            if (other == null) return;

            // Layer check
            int layer = other.gameObject.layer;
            if (((1 << layer) & affectedLayers.value) == 0)
            {
                if (logCollisionEvents)
                    Debug.Log($"[ConveyorBelt] Layer mismatch: {other.name}");
                return;
            }

            // Tag check
            if (!string.IsNullOrEmpty(requiredTag) && !other.CompareTag(requiredTag))
            {
                if (logCollisionEvents)
                    Debug.Log($"[ConveyorBelt] Tag mismatch: {other.name}");
                return;
            }

            Rigidbody rb = collision.rigidbody;
            if (rb == null || rb.isKinematic)
                return;

            // Only push objects that are actually resting on top of the belt (not hitting its side/underside).
            bool isOnTop = false;

            if (useHeightBasedDetection && beltSurfaceCollider != null)
            {
                float topSurfaceY = beltSurfaceCollider.bounds.max.y;

                foreach (ContactPoint contact in collision.contacts)
                {
                    // A contact point at (or just above) the belt's top surface height means the
                    // object is resting on top, regardless of the object's own rotation/shape,
                    // which makes this robust for tumbling or randomly-rotated packages.
                    float heightDelta = contact.point.y - topSurfaceY;
                    if (heightDelta >= -topSurfaceTolerance && heightDelta <= topSurfaceTolerance)
                    {
                        isOnTop = true;
                        break;
                    }
                }

                if (logCollisionEvents && !isOnTop && collision.contacts.Length > 0)
                {
                    float sampleDelta = collision.contacts[0].point.y - topSurfaceY;
                    Debug.Log($"[ConveyorBelt] {other.name} contact height delta from top surface: {sampleDelta:F3}m (tolerance: {topSurfaceTolerance:F3}m)");
                }
            }
            else
            {
                Vector3 beltUp = transform.up;
                foreach (ContactPoint contact in collision.contacts)
                {
                    // Compare against the belt's own local up so tilted belts are handled correctly,
                    // and check both possible normal directions since Unity doesn't guarantee which
                    // collider's surface the normal is reported relative to.
                    float dot = Mathf.Abs(Vector3.Dot(contact.normal, beltUp));
                    if (dot >= minUpwardContactDot)
                    {
                        isOnTop = true;
                        break;
                    }
                }

                if (logCollisionEvents && !isOnTop && collision.contacts.Length > 0)
                {
                    float sampleDot = Mathf.Abs(Vector3.Dot(collision.contacts[0].normal, beltUp));
                    Debug.Log($"[ConveyorBelt] {other.name} contact normal dot: {sampleDot:F3} (threshold: {minUpwardContactDot:F3})");
                }
            }

            if (!isOnTop)
            {
                if (logCollisionEvents)
                    Debug.Log($"[ConveyorBelt] {other.name} touching belt but not resting on top.");

                // Not resting on top (e.g. glanced off the side): make sure it isn't left frozen.
                RestoreRotation(rb);
                return;
            }

            ApplyConveyorMovement(rb);
        }

        /// <summary>
        /// Adds belt velocity along the belt direction, leaving vertical velocity (gravity)
        /// and any other physics interactions completely untouched. Rotation is frozen while
        /// resting on the belt so objects slide flat instead of rolling/tumbling, matching how
        /// a box behaves on a real conveyor belt.
        /// </summary>
        private void ApplyConveyorMovement(Rigidbody rb)
        {
            Vector3 velocity = rb.linearVelocity;

            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            float velocityAlongBelt = Vector3.Dot(horizontalVelocity, _worldBeltDirection);
            Vector3 perpendicularVelocity = horizontalVelocity - (_worldBeltDirection * velocityAlongBelt);

            // Push directly toward belt speed along the belt direction.
            Vector3 newHorizontalVelocity = _worldBeltDirection * beltSpeed;

            // Retain a damped amount of perpendicular velocity instead of wiping it out,
            // so side-on collisions/pushes still feel natural.
            if (applyPerpendicularFriction)
            {
                newHorizontalVelocity += perpendicularVelocity * (1f - perpendicularDamping);
            }
            else
            {
                newHorizontalVelocity += perpendicularVelocity;
            }

            velocity.x = newHorizontalVelocity.x;
            velocity.z = newHorizontalVelocity.z;
            // velocity.y is left untouched -- gravity keeps working normally.

            rb.linearVelocity = velocity;

            if (freezeRotationOnBelt)
            {
                FreezeRotation(rb);
            }
        }

        private void FreezeRotation(Rigidbody rb)
        {
            if (rb == null) return;

            if (!_frozenRigidbodies.ContainsKey(rb))
            {
                _frozenRigidbodies[rb] = rb.constraints;
            }

            rb.angularVelocity = Vector3.zero;
            rb.constraints = _frozenRigidbodies[rb] | RigidbodyConstraints.FreezeRotation;
        }

        private void RestoreRotation(Rigidbody rb)
        {
            if (rb == null) return;

            if (_frozenRigidbodies.TryGetValue(rb, out var originalConstraints))
            {
                rb.constraints = originalConstraints;
                _frozenRigidbodies.Remove(rb);
            }
        }

        public override void OnNetworkDespawn()
        {
            RestoreAllFrozenRigidbodies();
            base.OnNetworkDespawn();
        }

        public override void OnDestroy()
        {
            RestoreAllFrozenRigidbodies();

            // Cleanup material instance
            if (_beltMaterial != null)
            {
                Destroy(_beltMaterial);
            }
        }

        private void RestoreAllFrozenRigidbodies()
        {
            foreach (var kvp in _frozenRigidbodies)
            {
                if (kvp.Key != null)
                {
                    kvp.Key.constraints = kvp.Value;
                }
            }
            _frozenRigidbodies.Clear();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            beltSpeed = Mathf.Max(0f, beltSpeed);
            uvScrollSpeed = Mathf.Max(0f, uvScrollSpeed);
            perpendicularDamping = Mathf.Clamp01(perpendicularDamping);
            minUpwardContactDot = Mathf.Clamp01(minUpwardContactDot);
            topSurfaceTolerance = Mathf.Max(0f, topSurfaceTolerance);

            if (beltSurfaceCollider == null)
            {
                beltSurfaceCollider = GetComponent<Collider>();
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!showDebugGizmos)
                return;

            UpdateWorldDirection();

            // Draw belt direction
            Gizmos.color = Color.green;
            Vector3 center = beltSurfaceCollider != null ? beltSurfaceCollider.bounds.center : transform.position;
            Vector3 endPoint = center + _worldBeltDirection * 2f;

            Gizmos.DrawLine(center, endPoint);

            // Draw arrowhead
            Vector3 right = Vector3.Cross(_worldBeltDirection, Vector3.up).normalized * 0.2f;
            Vector3 arrowTip = endPoint;
            Gizmos.DrawLine(arrowTip, arrowTip - _worldBeltDirection * 0.3f + right);
            Gizmos.DrawLine(arrowTip, arrowTip - _worldBeltDirection * 0.3f - right);

            // Draw surface collider bounds
            if (beltSurfaceCollider != null)
            {
                Gizmos.color = new Color(0.2f, 0.2f, 1f, 0.3f);
                Gizmos.DrawCube(beltSurfaceCollider.bounds.center, beltSurfaceCollider.bounds.size);
            }

            // Draw top-surface detection tolerance band
            if (useHeightBasedDetection && beltSurfaceCollider != null)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.4f);
                float topY = beltSurfaceCollider.bounds.max.y;
                Vector3 size = beltSurfaceCollider.bounds.size;
                size.y = topSurfaceTolerance * 2f;
                Gizmos.DrawCube(new Vector3(center.x, topY, center.z), size);
            }
        }
#endif
    }
}