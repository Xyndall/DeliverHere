using UnityEngine;
using Unity.Netcode;

namespace DeliverHere.Player
{
    /// <summary>
    /// Enables ragdoll physics on the player when hit by fast-moving objects.
    /// Temporarily disables character controller and enables rigidbody physics.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class PlayerRagdollStun : NetworkBehaviour
    {
        [Header("Stun Configuration")]
        [Tooltip("Use a config asset for shared settings across players, or configure inline below.")]
        [SerializeField] private PlayerRagdollConfig config;

        [Header("Inline Settings (used if no config assigned)")]
        [Tooltip("Minimum impact speed (m/s) required to trigger ragdoll stun.")]
        [SerializeField] private float minImpactSpeed = 5f;

        [Tooltip("Duration of ragdoll stun in seconds.")]
        [SerializeField] private float stunDuration = 2f;

        [Tooltip("Multiplier for the impact force applied to the ragdoll.")]
        [SerializeField] private float impactForceMultiplier = 1.5f;

        [Tooltip("Maximum force that can be applied to the ragdoll.")]
        [SerializeField] private float maxImpactForce = 50f;

        [Tooltip("Upward force component added to the impact (helps with believable tumbling).")]
        [SerializeField] private float upwardForceBoost = 2f;

        [Tooltip("Minimum time between stuns (cooldown in seconds).")]
        [SerializeField] private float stunCooldown = 1f;

        [Header("Impact Filtering")]
        [Tooltip("Layers that can trigger ragdoll stun (e.g., physics objects, packages).")]
        [SerializeField] private LayerMask impactLayers = ~0;

        [Tooltip("Ignore impacts from objects below this mass (kg).")]
        [SerializeField] private float minImpactMass = 1f;

        [Header("Ragdoll Physics Settings")]
        [Tooltip("Minimum collision detection mode for ragdoll bodies (prevents clipping).")]
        [SerializeField] private CollisionDetectionMode ragdollCollisionMode = CollisionDetectionMode.ContinuousDynamic;

        [Tooltip("Drag applied to ragdoll bodies (helps prevent sliding/clipping).")]
        [SerializeField] private float ragdollDrag = 0.5f;

        [Tooltip("Angular drag applied to ragdoll bodies (reduces spinning).")]
        [SerializeField] private float ragdollAngularDrag = 0.5f;

        [Tooltip("Maximum ragdoll velocity to prevent extreme speeds that cause clipping.")]
        [SerializeField] private float maxRagdollVelocity = 15f;

        [Tooltip("Solver iterations for ragdoll physics (higher = more stable, more expensive).")]
        [SerializeField] private int ragdollSolverIterations = 10;

        [Tooltip("Height offset added when spawning ragdoll to prevent initial ground penetration.")]
        [SerializeField] private float ragdollSpawnHeightOffset = 0.2f;

        [Header("Ragdoll Mass Distribution")]
        [Tooltip("Total mass of the entire ragdoll (kg). This will be distributed across body parts.")]
        [SerializeField] private float totalRagdollMass = 75f;

        [Tooltip("If true, automatically distribute mass across ragdoll bodies based on realistic proportions.")]
        [SerializeField] private bool autoDistributeMass = true;

        [Tooltip("Mass distribution percentages for different body parts (must sum to 1.0).")]
        [SerializeField] private RagdollMassDistribution massDistribution = new RagdollMassDistribution();

        [Header("Ground Detection for Recovery")]
        [Tooltip("If true, player can only recover after ragdoll touches the ground.")]
        [SerializeField] private bool requireGroundContactForRecovery = true;

        [Tooltip("Layers considered as ground for recovery detection.")]
        [SerializeField] private LayerMask groundLayers = ~0;

        [Tooltip("Minimum time ragdoll must be grounded before recovery is allowed (seconds).")]
        [SerializeField] private float minGroundedTimeForRecovery = 0.3f;

        [Tooltip("Minimum velocity threshold to consider ragdoll as settled (m/s).")]
        [SerializeField] private float settledVelocityThreshold = 0.5f;

        [Header("References")]
        [SerializeField] private CharacterController characterController;
        [SerializeField] private Rigidbody ragdollRigidbody;
        [SerializeField] private PlayerMovement playerMovement;
        [SerializeField] private PlayerInputController inputController;
        [SerializeField] private Animator animator;
        [SerializeField] private PlayerFirstPersonLook firstPersonLook;
        [SerializeField] private PlayerCameraAssigner cameraAssigner;

        [Header("Ragdoll Body Parts")]
        [Tooltip("Assign all rigidbody body parts of the ragdoll (limbs, torso, head, etc.).")]
        [SerializeField] private Rigidbody[] ragdollBodies = new Rigidbody[0];

        [Tooltip("Assign all colliders that are part of the ragdoll.")]
        [SerializeField] private Collider[] ragdollColliders = new Collider[0];

        [Header("Ragdoll Parent Handling")]
        [Tooltip("The transform that contains the ragdoll (usually the character model). Will be unparented during ragdoll.")]
        [SerializeField] private Transform ragdollRoot;

        [Tooltip("If true, automatically find ragdoll root from animator.")]
        [SerializeField] private bool autoFindRagdollRoot = true;

        [Header("Camera Following")]
        [Tooltip("Bone to track for camera position during ragdoll (usually head bone). If null, uses pelvis.")]
        [SerializeField] private Transform cameraFollowBone;

        [Tooltip("If true, auto-find head bone for camera tracking.")]
        [SerializeField] private bool autoFindCameraFollowBone = true;

        [Tooltip("Camera smoothing during ragdoll follow.")]
        [SerializeField] private float cameraFollowSmoothing = 8f;

        [Tooltip("Camera offset from follow bone in local space.")]
        [SerializeField] private Vector3 cameraFollowOffset = new Vector3(0f, 0.15f, 0f);

        [Tooltip("If true, only move player root on owning client (prevents server physics interference).")]
        [SerializeField] private bool onlyFollowCameraOnClient = true;

        [Header("Recovery")]
        [Tooltip("If true, player auto-recovers after stun duration. If false, requires input.")]
        [SerializeField] private bool autoRecover = true;

        [Tooltip("Time to blend from ragdoll back to animated state (seconds).")]
        [SerializeField] private float recoveryBlendTime = 0.3f;

        [Header("Visual Feedback")]
        [Tooltip("Optional particle effect to spawn on impact.")]
        [SerializeField] private ParticleSystem impactVFX;

        [Tooltip("Optional audio source for impact sound.")]
        [SerializeField] private AudioSource impactAudioSource;

        [Tooltip("Impact sound clip.")]
        [SerializeField] private AudioClip impactSFX;

        [Header("Debug")]
        [SerializeField] private bool logImpacts = false;
        [SerializeField] private bool logGroundContact = false;

        // Network variable to replicate stun state
        private readonly NetworkVariable<bool> _isStunned = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private bool _isInRagdoll;
        private float _stunEndTime;
        private float _lastStunTime = -999f;
        private Vector3 _ragdollVelocity;

        // Ground contact tracking
        private bool _hasHitGround;
        private float _groundContactStartTime;
        private int _groundContactCount;

        // Store original states for each ragdoll body
        private struct RagdollBodyState
        {
            public float drag;
            public float angularDrag;
            public CollisionDetectionMode collisionMode;
            public int solverIterations;
            public float mass;
            public bool useGravity;
        }
        private RagdollBodyState[] _ragdollBodyStates;

        // Store original states
        private bool _originalUseGravity;
        private bool _originalIsKinematic;
        private RigidbodyConstraints _originalConstraints;

        // Store ragdoll parent state
        private Transform _originalParent;
        private Vector3 _originalLocalPosition;
        private Quaternion _originalLocalRotation;

        // Camera following state
        private Transform _originalCameraParent;
        private Transform _originalCameraParentBeforeRagdoll;
        private Vector3 _originalCameraLocalPos;
        private Quaternion _originalCameraLocalRot;
        private Vector3 _cameraTargetPosition;

        // Store last frame velocity for CharacterController impact detection
        private Vector3 _lastPosition;
        private Vector3 _currentVelocity;

        public bool IsStunned => _isStunned.Value;
        public bool IsInRagdoll => _isInRagdoll;
        public bool HasHitGround => _hasHitGround;

        #region Configuration Properties
        private float MinImpactSpeed => config != null ? config.MinImpactSpeed : minImpactSpeed;
        private float StunDuration => config != null ? config.StunDuration : stunDuration;
        private float ImpactForceMultiplier => config != null ? config.ImpactForceMultiplier : impactForceMultiplier;
        private float MaxImpactForce => config != null ? config.MaxImpactForce : maxImpactForce;
        private float UpwardForceBoost => config != null ? config.UpwardForceBoost : upwardForceBoost;
        private float StunCooldown => config != null ? config.StunCooldown : stunCooldown;
        private float MinImpactMass => config != null ? config.MinImpactMass : minImpactMass;
        #endregion

        [System.Serializable]
        public class RagdollMassDistribution
        {
            [Tooltip("Pelvis/hips mass percentage (default: 25%)")]
            [Range(0f, 1f)] public float pelvis = 0.25f;

            [Tooltip("Spine/torso mass percentage (default: 20%)")]
            [Range(0f, 1f)] public float spine = 0.20f;

            [Tooltip("Head mass percentage (default: 8%)")]
            [Range(0f, 1f)] public float head = 0.08f;

            [Tooltip("Each upper leg mass percentage (default: 10% each)")]
            [Range(0f, 1f)] public float upperLeg = 0.10f;

            [Tooltip("Each lower leg mass percentage (default: 5% each)")]
            [Range(0f, 1f)] public float lowerLeg = 0.05f;

            [Tooltip("Each upper arm mass percentage (default: 4% each)")]
            [Range(0f, 1f)] public float upperArm = 0.04f;

            [Tooltip("Each lower arm mass percentage (default: 2% each)")]
            [Range(0f, 1f)] public float lowerArm = 0.02f;
        }

        private void Awake()
        {
            // Auto-find components if not assigned
            if (characterController == null)
                characterController = GetComponent<CharacterController>();

            if (ragdollRigidbody == null)
                ragdollRigidbody = GetComponent<Rigidbody>();

            if (playerMovement == null)
                playerMovement = GetComponent<PlayerMovement>();

            if (inputController == null)
                inputController = GetComponent<PlayerInputController>();

            if (animator == null)
                animator = GetComponentInChildren<Animator>();

            if (firstPersonLook == null)
                firstPersonLook = GetComponentInChildren<PlayerFirstPersonLook>();

            if (cameraAssigner == null)
                cameraAssigner = GetComponent<PlayerCameraAssigner>();

            // Auto-find ragdoll root
            if (autoFindRagdollRoot && ragdollRoot == null && animator != null)
            {
                ragdollRoot = animator.transform;
            }

            // Auto-find camera follow bone (head)
            if (autoFindCameraFollowBone && cameraFollowBone == null && animator != null && animator.isHuman)
            {
                cameraFollowBone = animator.GetBoneTransform(HumanBodyBones.Head);
            }

            // Store original parent relationship
            if (ragdollRoot != null)
            {
                _originalParent = ragdollRoot.parent;
                _originalLocalPosition = ragdollRoot.localPosition;
                _originalLocalRotation = ragdollRoot.localRotation;
            }

            // Auto-populate ragdoll bodies if empty
            if (ragdollBodies.Length == 0 && animator != null)
            {
                ragdollBodies = animator.GetComponentsInChildren<Rigidbody>();
            }

            // Auto-populate ragdoll colliders if empty
            if (ragdollColliders.Length == 0 && animator != null)
            {
                ragdollColliders = animator.GetComponentsInChildren<Collider>();
            }

            // Apply mass distribution
            if (autoDistributeMass)
            {
                ApplyMassDistribution();
            }

            // Store original rigidbody settings for ragdoll bodies
            StoreRagdollBodyStates();

            // Store original rigidbody settings
            if (ragdollRigidbody != null)
            {
                _originalUseGravity = ragdollRigidbody.useGravity;
                _originalIsKinematic = ragdollRigidbody.isKinematic;
                _originalConstraints = ragdollRigidbody.constraints;
            }

            // Initialize ragdoll to disabled state
            SetRagdollEnabled(false);

            // Initialize velocity tracking
            _lastPosition = transform.position;
        }

        private void ApplyMassDistribution()
        {
            if (ragdollBodies == null || ragdollBodies.Length == 0)
                return;

            foreach (var rb in ragdollBodies)
            {
                if (rb == null) continue;

                string boneName = rb.name.ToLower();
                float massPercent = 0.01f; // Default fallback

                // Determine mass based on bone name
                if (boneName.Contains("pelvis") || boneName.Contains("hip"))
                {
                    massPercent = massDistribution.pelvis;
                }
                else if (boneName.Contains("spine") || boneName.Contains("chest"))
                {
                    massPercent = massDistribution.spine;
                }
                else if (boneName.Contains("head"))
                {
                    massPercent = massDistribution.head;
                }
                else if (boneName.Contains("thigh") || boneName.Contains("upperleg"))
                {
                    massPercent = massDistribution.upperLeg;
                }
                else if (boneName.Contains("calf") || boneName.Contains("shin") || boneName.Contains("lowerleg"))
                {
                    massPercent = massDistribution.lowerLeg;
                }
                else if (boneName.Contains("upperarm") || boneName.Contains("shoulder"))
                {
                    massPercent = massDistribution.upperArm;
                }
                else if (boneName.Contains("forearm") || boneName.Contains("lowerarm") || boneName.Contains("hand"))
                {
                    massPercent = massDistribution.lowerArm;
                }

                rb.mass = totalRagdollMass * massPercent;
            }

            if (logImpacts)
            {
                Debug.Log($"[PlayerRagdollStun] Applied mass distribution. Total mass: {totalRagdollMass} kg");
            }
        }

        private void StoreRagdollBodyStates()
        {
            if (ragdollBodies == null || ragdollBodies.Length == 0)
                return;

            _ragdollBodyStates = new RagdollBodyState[ragdollBodies.Length];
            for (int i = 0; i < ragdollBodies.Length; i++)
            {
                if (ragdollBodies[i] == null) continue;

                _ragdollBodyStates[i] = new RagdollBodyState
                {
                    drag = ragdollBodies[i].linearDamping,
                    angularDrag = ragdollBodies[i].angularDamping,
                    collisionMode = ragdollBodies[i].collisionDetectionMode,
                    solverIterations = ragdollBodies[i].solverIterations,
                    mass = ragdollBodies[i].mass,
                    useGravity = ragdollBodies[i].useGravity
                };
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            _isStunned.OnValueChanged += OnStunStateChanged;

            // Sync initial state
            if (_isStunned.Value && !_isInRagdoll)
            {
                EnableRagdoll(Vector3.zero);
            }
        }

        public override void OnNetworkDespawn()
        {
            _isStunned.OnValueChanged -= OnStunStateChanged;
            base.OnNetworkDespawn();
        }

        private void Update()
        {
            // Track velocity for CharacterController collision detection
            if (!_isInRagdoll)
            {
                float dt = Time.deltaTime;
                if (dt > 0.0001f)
                {
                    _currentVelocity = (transform.position - _lastPosition) / dt;
                    _lastPosition = transform.position;
                }
            }

            if (!IsServer) return;
            if (!_isInRagdoll) return;

            // Check for recovery conditions
            if (Time.time >= _stunEndTime && autoRecover)
            {
                if (CanRecover())
                {
                    RecoverFromRagdoll();
                }
            }
        }

        private void FixedUpdate()
        {
            if (!IsServer) return;

            // Clamp ragdoll velocities to prevent extreme speeds (and clipping)
            if (_isInRagdoll && maxRagdollVelocity > 0f)
            {
                foreach (var rb in ragdollBodies)
                {
                    if (rb == null || rb.isKinematic) continue;

                    // Clamp linear velocity
                    if (rb.linearVelocity.magnitude > maxRagdollVelocity)
                    {
                        rb.linearVelocity = rb.linearVelocity.normalized * maxRagdollVelocity;
                    }

                    // Clamp angular velocity
                    float maxAngularVel = maxRagdollVelocity * 2f; // Allow more rotation
                    if (rb.angularVelocity.magnitude > maxAngularVel)
                    {
                        rb.angularVelocity = rb.angularVelocity.normalized * maxAngularVel;
                    }
                }
            }

            // Track ground contact
            if (_isInRagdoll && requireGroundContactForRecovery)
            {
                UpdateGroundContact();
            }
        }

        private void UpdateGroundContact()
        {
            bool isGrounded = false;
            bool isSettled = true;

            // Check if any ragdoll body is touching ground
            foreach (var rb in ragdollBodies)
            {
                if (rb == null || rb.isKinematic) continue;

                // Check if velocity is low enough (settled)
                if (rb.linearVelocity.magnitude > settledVelocityThreshold)
                {
                    isSettled = false;
                }

                // Cast down from each body to detect ground
                if (Physics.Raycast(rb.position, Vector3.down, 0.2f, groundLayers, QueryTriggerInteraction.Ignore))
                {
                    isGrounded = true;
                }
            }

            // Update ground contact state
            if (isGrounded && isSettled)
            {
                if (_groundContactCount == 0)
                {
                    _groundContactStartTime = Time.time;
                    if (logGroundContact)
                    {
                        Debug.Log("[PlayerRagdollStun] Ground contact started");
                    }
                }
                _groundContactCount++;

                // Mark as hit ground if sustained contact
                float contactDuration = Time.time - _groundContactStartTime;
                if (!_hasHitGround && contactDuration >= minGroundedTimeForRecovery)
                {
                    _hasHitGround = true;
                    if (logGroundContact)
                    {
                        Debug.Log("[PlayerRagdollStun] Ragdoll has settled on ground - recovery allowed");
                    }
                }
            }
            else
            {
                _groundContactCount = 0;
            }
        }

        private bool CanRecover()
        {
            if (!requireGroundContactForRecovery)
                return true;

            return _hasHitGround;
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            // Only process on server
            if (!IsServer) return;

            // Check if already stunned or in cooldown
            if (Time.time - _lastStunTime < StunCooldown)
                return;

            if (_isInRagdoll)
                return;

            // Check layer mask
            if (((1 << hit.gameObject.layer) & impactLayers.value) == 0)
                return;

            // Get the rigidbody of the object we hit
            Rigidbody hitRb = hit.rigidbody;
            if (hitRb == null)
                return;

            // Check minimum mass
            if (hitRb.mass < MinImpactMass)
                return;

            // Calculate relative velocity (object velocity - player velocity)
            Vector3 relativeVelocity = hitRb.linearVelocity - _currentVelocity;
            float impactSpeed = relativeVelocity.magnitude;

            if (logImpacts)
            {
                Debug.Log($"[PlayerRagdollStun] ControllerColliderHit detected: speed={impactSpeed:F2} m/s, mass={hitRb.mass:F2} kg, threshold={MinImpactSpeed:F2} m/s, object={hit.gameObject.name}");
            }

            // Check if impact is strong enough
            if (impactSpeed < MinImpactSpeed)
                return;

            // Calculate impact force direction and magnitude
            Vector3 impactDirection = relativeVelocity.normalized;
            Vector3 impactPoint = hit.point;

            // Calculate force magnitude based on speed and mass
            float forceMagnitude = impactSpeed * hitRb.mass * ImpactForceMultiplier;
            forceMagnitude = Mathf.Min(forceMagnitude, MaxImpactForce);

            // Add upward component for more dramatic effect
            Vector3 force = impactDirection * forceMagnitude;
            force.y += UpwardForceBoost;

            if (logImpacts)
            {
                Debug.Log($"[PlayerRagdollStun] Applying stun from controller hit! Force={forceMagnitude:F2}N, Direction={impactDirection}");
            }

            // Trigger ragdoll stun
            TriggerRagdollStun(force, impactPoint);
        }

        private void OnCollisionEnter(Collision collision)
        {
            // Only process on server
            if (!IsServer) return;

            // Check cooldown
            if (Time.time - _lastStunTime < StunCooldown)
                return;

            // Already stunned
            if (_isInRagdoll)
                return;

            // Check layer mask
            if (((1 << collision.gameObject.layer) & impactLayers.value) == 0)
                return;

            // Get impact rigidbody
            Rigidbody impactRb = collision.rigidbody;
            if (impactRb == null)
                return;

            // Check minimum mass
            if (impactRb.mass < MinImpactMass)
                return;

            // Calculate impact speed
            float impactSpeed = collision.relativeVelocity.magnitude;

            if (logImpacts)
            {
                Debug.Log($"[PlayerRagdollStun] OnCollisionEnter detected: speed={impactSpeed:F2} m/s, mass={impactRb.mass:F2} kg, threshold={MinImpactSpeed:F2} m/s, object={collision.gameObject.name}");
            }

            // Check if impact is strong enough
            if (impactSpeed < MinImpactSpeed)
                return;

            // Calculate impact force direction and magnitude
            Vector3 impactDirection = collision.relativeVelocity.normalized;

            // Find the contact point closest to center of mass for better force application
            Vector3 impactPoint = collision.contacts[0].point;
            if (collision.contactCount > 1)
            {
                Vector3 avgPoint = Vector3.zero;
                foreach (var contact in collision.contacts)
                {
                    avgPoint += contact.point;
                }
                impactPoint = avgPoint / collision.contactCount;
            }

            // Calculate force magnitude based on speed and mass
            float forceMagnitude = impactSpeed * impactRb.mass * ImpactForceMultiplier;
            forceMagnitude = Mathf.Min(forceMagnitude, MaxImpactForce);

            // Add upward component for more dramatic effect
            Vector3 force = impactDirection * forceMagnitude;
            force.y += UpwardForceBoost;

            if (logImpacts)
            {
                Debug.Log($"[PlayerRagdollStun] Applying stun from collision! Force={forceMagnitude:F2}N, Direction={impactDirection}");
            }

            // Trigger ragdoll stun
            TriggerRagdollStun(force, impactPoint);
        }

        private void TriggerRagdollStun(Vector3 force, Vector3 impactPoint)
        {
            if (!IsServer) return;

            _lastStunTime = Time.time;
            _stunEndTime = Time.time + StunDuration;

            // Reset ground contact tracking
            _hasHitGround = false;
            _groundContactCount = 0;
            _groundContactStartTime = 0f;

            // Set network variable to replicate to clients
            _isStunned.Value = true;

            // Enable ragdoll on server
            EnableRagdoll(force, impactPoint);

            // Play feedback
            PlayImpactFeedbackClientRpc(impactPoint);
        }

        private void EnableRagdoll(Vector3 force, Vector3 impactPoint = default)
        {
            _isInRagdoll = true;

            // Disable character controller
            if (characterController != null)
                characterController.enabled = false;

            // Disable player movement script
            if (playerMovement != null)
                playerMovement.enabled = false;

            // Disable camera look (prevents jitter from sway)
            if (firstPersonLook != null)
                firstPersonLook.enabled = false;

            // Disable animator
            if (animator != null)
                animator.enabled = false;

            // Unparent ragdoll root to allow independent physics FIRST
            if (ragdollRoot != null && ragdollRoot.parent != null)
            {
                // Store world position/rotation before unparenting
                Vector3 worldPos = ragdollRoot.position;
                Quaternion worldRot = ragdollRoot.rotation;

                ragdollRoot.SetParent(null);

                // Lift slightly above ground to prevent initial clipping
                worldPos.y += ragdollSpawnHeightOffset;

                // Restore world position/rotation after unparenting
                ragdollRoot.position = worldPos;
                ragdollRoot.rotation = worldRot;
            }

            // FIXED: Reparent camera to the HEAD BONE (which has physics) instead of ragdoll root
            if (IsOwner && firstPersonLook != null && firstPersonLook.playerCamera != null && cameraFollowBone != null)
            {
                Transform camTransform = firstPersonLook.playerCamera.transform;
                _originalCameraParentBeforeRagdoll = camTransform.parent;
                _originalCameraLocalPos = camTransform.localPosition;
                _originalCameraLocalRot = camTransform.localRotation;

                // Parent camera directly to the head bone so it follows the physics
                camTransform.SetParent(cameraFollowBone, true); // preserve world position
                
                // Apply the offset relative to head bone
                camTransform.localPosition = cameraFollowOffset;
                camTransform.localRotation = Quaternion.identity;
            }

            // Initialize camera follow position (no longer needed but kept for fallback)
            if (cameraFollowBone != null)
            {
                _cameraTargetPosition = cameraFollowBone.position + cameraFollowBone.TransformDirection(cameraFollowOffset);
            }
            else if (ragdollBodies.Length > 0)
            {
                // Fallback to pelvis/first body
                _cameraTargetPosition = ragdollBodies[0].position;
            }

            // Enable ragdoll physics with anti-clipping settings
            SetRagdollEnabled(true);

            // Apply force to ragdoll (server-only)
            if (IsServer && force.sqrMagnitude > 0.01f)
            {
                // Apply force to the main rigidbody or distribute to body parts
                if (ragdollRigidbody != null && ragdollBodies.Length == 0)
                {
                    if (impactPoint != default)
                        ragdollRigidbody.AddForceAtPosition(force, impactPoint, ForceMode.Impulse);
                    else
                        ragdollRigidbody.AddForce(force, ForceMode.Impulse);
                }
                else
                {
                    // Distribute force across ragdoll bodies
                    foreach (var rb in ragdollBodies)
                    {
                        if (rb == null || rb.isKinematic) continue;

                        float distance = Vector3.Distance(rb.position, impactPoint);
                        float falloff = Mathf.Clamp01(1f - (distance / 2f)); // Force falls off with distance

                        rb.AddForce(force * falloff, ForceMode.Impulse);
                    }
                }
            }
        }

        private void RecoverFromRagdoll()
        {
            if (!IsServer) return;

            _isStunned.Value = false;
            DisableRagdoll();
        }

        private void DisableRagdoll()
        {
            _isInRagdoll = false;

            // Find ragdoll body to snap player to (prefer pelvis/hips)
            Vector3 recoveryPosition = transform.position;
            Quaternion recoveryRotation = transform.rotation;

            if (ragdollBodies.Length > 0)
            {
                // Try to find pelvis/hip bone for best recovery position
                Rigidbody pelvis = null;
                foreach (var rb in ragdollBodies)
                {
                    if (rb == null) continue;
                    string boneName = rb.name.ToLower();
                    if (boneName.Contains("pelvis") || boneName.Contains("hip") || boneName.Contains("spine"))
                    {
                        pelvis = rb;
                        break;
                    }
                }

                // Use pelvis position or first ragdoll body
                Rigidbody referenceBody = pelvis ?? ragdollBodies[0];
                if (referenceBody != null)
                {
                    recoveryPosition = referenceBody.position;
                    // Keep only Y rotation for recovery
                    Vector3 forward = referenceBody.transform.forward;
                    forward.y = 0;
                    if (forward.sqrMagnitude > 0.01f)
                    {
                        recoveryRotation = Quaternion.LookRotation(forward.normalized);
                    }
                }
            }

            // Disable ragdoll physics BEFORE reparenting
            SetRagdollEnabled(false);

            // ADDED: Restore camera parent (owner only)
            if (IsOwner && firstPersonLook != null && firstPersonLook.playerCamera != null && _originalCameraParentBeforeRagdoll != null)
            {
                Transform camTransform = firstPersonLook.playerCamera.transform;
                camTransform.SetParent(_originalCameraParentBeforeRagdoll, true); // preserve world position
                camTransform.localPosition = _originalCameraLocalPos;
                camTransform.localRotation = _originalCameraLocalRot;
            }

            // Reparent ragdoll root back to player
            if (ragdollRoot != null && ragdollRoot.parent != _originalParent)
            {
                ragdollRoot.SetParent(_originalParent);
                ragdollRoot.localPosition = _originalLocalPosition;
                ragdollRoot.localRotation = _originalLocalRotation;
            }

            // Move player root to recovery position
            if (characterController != null)
            {
                characterController.enabled = false; // Disable first to allow teleport
            }

            // Snap to ground with raycast
            RaycastHit hit;
            Vector3 rayStart = recoveryPosition + Vector3.up * 2f;
            if (Physics.Raycast(rayStart, Vector3.down, out hit, 10f, ~0, QueryTriggerInteraction.Ignore))
            {
                // Add small offset above ground
                transform.position = hit.point + Vector3.up * 0.1f;
            }
            else
            {
                // Fallback: just use recovery position with offset
                transform.position = recoveryPosition + Vector3.up * 0.1f;
            }

            transform.rotation = recoveryRotation;

            // Re-enable character controller
            if (characterController != null)
            {
                characterController.enabled = true;
            }

            // Re-enable player movement
            if (playerMovement != null)
                playerMovement.enabled = true;

            // Re-enable camera look
            if (firstPersonLook != null)
                firstPersonLook.enabled = true;

            // Re-enable animator
            if (animator != null)
            {
                animator.enabled = true;
            }

            // Reset velocity if not kinematic
            if (ragdollRigidbody != null && !ragdollRigidbody.isKinematic)
            {
                ragdollRigidbody.linearVelocity = Vector3.zero;
                ragdollRigidbody.angularVelocity = Vector3.zero;
            }

            // Reset velocity tracking
            _lastPosition = transform.position;
            _currentVelocity = Vector3.zero;

            // Reset ground contact tracking
            _hasHitGround = false;
            _groundContactCount = 0;
        }

        private void SetRagdollEnabled(bool enabled)
        {
            // Enable/disable ragdoll body parts
            for (int i = 0; i < ragdollBodies.Length; i++)
            {
                var rb = ragdollBodies[i];
                if (rb == null) continue;

                rb.isKinematic = !enabled;
                rb.detectCollisions = enabled;

                if (enabled)
                {
                    // Apply anti-clipping physics settings AND ensure gravity is enabled
                    rb.collisionDetectionMode = ragdollCollisionMode;
                    rb.linearDamping = ragdollDrag;
                    rb.angularDamping = ragdollAngularDrag;
                    rb.solverIterations = ragdollSolverIterations;
                    rb.maxAngularVelocity = 50f;
                    rb.useGravity = true; // Force gravity ON during ragdoll
                }
                else
                {
                    // Restore original settings
                    if (_ragdollBodyStates != null && i < _ragdollBodyStates.Length)
                    {
                        rb.collisionDetectionMode = _ragdollBodyStates[i].collisionMode;
                        rb.linearDamping = _ragdollBodyStates[i].drag;
                        rb.angularDamping = _ragdollBodyStates[i].angularDrag;
                        rb.solverIterations = _ragdollBodyStates[i].solverIterations;
                        rb.mass = _ragdollBodyStates[i].mass;
                        rb.useGravity = _ragdollBodyStates[i].useGravity;
                    }

                    // Only reset velocity when not kinematic
                    if (!rb.isKinematic)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                }
            }

            foreach (var col in ragdollColliders)
            {
                if (col == null) continue;
                col.enabled = enabled;
            }

            // Configure main rigidbody
            if (ragdollRigidbody != null)
            {
                if (enabled)
                {
                    ragdollRigidbody.isKinematic = false;
                    ragdollRigidbody.useGravity = true;
                    ragdollRigidbody.constraints = RigidbodyConstraints.None;
                }
                else
                {
                    ragdollRigidbody.isKinematic = _originalIsKinematic;
                    ragdollRigidbody.useGravity = _originalUseGravity;
                    ragdollRigidbody.constraints = _originalConstraints;

                    // Only reset velocity if not kinematic
                    if (!ragdollRigidbody.isKinematic)
                    {
                        ragdollRigidbody.linearVelocity = Vector3.zero;
                        ragdollRigidbody.angularVelocity = Vector3.zero;
                    }
                }
            }
        }

        private void OnStunStateChanged(bool wasStunned, bool isStunned)
        {
            if (isStunned && !_isInRagdoll)
            {
                EnableRagdoll(Vector3.zero);
            }
            else if (!isStunned && _isInRagdoll)
            {
                DisableRagdoll();
            }
        }

        [ClientRpc]
        private void PlayImpactFeedbackClientRpc(Vector3 impactPoint)
        {
            // Play VFX
            if (impactVFX != null)
            {
                impactVFX.transform.position = impactPoint;
                impactVFX.Play();
            }

            // Play SFX
            if (impactAudioSource != null && impactSFX != null)
            {
                impactAudioSource.PlayOneShot(impactSFX);
            }
        }

        /// <summary>
        /// Public method to manually trigger ragdoll stun (e.g., from explosions, abilities).
        /// </summary>
        public void TriggerStunManually(Vector3 force, Vector3 impactPoint)
        {
            if (!IsServer)
            {
                Debug.LogWarning("[PlayerRagdollStun] TriggerStunManually can only be called on server.");
                return;
            }

            TriggerRagdollStun(force, impactPoint);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            minImpactSpeed = Mathf.Max(0.1f, minImpactSpeed);
            stunDuration = Mathf.Max(0.1f, stunDuration);
            impactForceMultiplier = Mathf.Max(0.1f, impactForceMultiplier);
            maxImpactForce = Mathf.Max(1f, maxImpactForce);
            upwardForceBoost = Mathf.Max(0f, upwardForceBoost);
            stunCooldown = Mathf.Max(0f, stunCooldown);
            minImpactMass = Mathf.Max(0f, minImpactMass);
            recoveryBlendTime = Mathf.Max(0.1f, recoveryBlendTime);
            cameraFollowSmoothing = Mathf.Max(0.1f, cameraFollowSmoothing);

            ragdollDrag = Mathf.Max(0f, ragdollDrag);
            ragdollAngularDrag = Mathf.Max(0f, ragdollAngularDrag);
            maxRagdollVelocity = Mathf.Max(1f, maxRagdollVelocity);
            ragdollSolverIterations = Mathf.Clamp(ragdollSolverIterations, 1, 255);
            ragdollSpawnHeightOffset = Mathf.Max(0f, ragdollSpawnHeightOffset);

            totalRagdollMass = Mathf.Max(1f, totalRagdollMass);
            minGroundedTimeForRecovery = Mathf.Max(0f, minGroundedTimeForRecovery);
            settledVelocityThreshold = Mathf.Max(0f, settledVelocityThreshold);

            // Auto-find ragdoll root in editor
            if (autoFindRagdollRoot && ragdollRoot == null)
            {
                var anim = GetComponentInChildren<Animator>();
                if (anim != null)
                {
                    ragdollRoot = anim.transform;
                }
            }

            // Auto-find camera follow bone in editor
            if (autoFindCameraFollowBone && cameraFollowBone == null)
            {
                var anim = GetComponentInChildren<Animator>();
                if (anim != null && anim.isHuman)
                {
                    cameraFollowBone = anim.GetBoneTransform(HumanBodyBones.Head);
                }
            }
        }

        [ContextMenu("Debug/Test Ragdoll Stun")]
        private void Debug_TestRagdollStun()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Test Ragdoll Stun only works in Play mode.");
                return;
            }

            if (!IsServer)
            {
                Debug.LogWarning("Test Ragdoll Stun only works on server/host.");
                return;
            }

            Vector3 testForce = transform.forward * 10f + Vector3.up * 5f;
            TriggerRagdollStun(testForce, transform.position);
            Debug.Log("[PlayerRagdollStun] Test stun triggered!");
        }

        [ContextMenu("Debug/Force Recovery")]
        private void Debug_ForceRecovery()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Force Recovery only works in Play mode.");
                return;
            }

            if (!IsServer)
            {
                Debug.LogWarning("Force Recovery only works on server/host.");
                return;
            }

            RecoverFromRagdoll();
            Debug.Log("[PlayerRagdollStun] Recovery forced!");
        }

        [ContextMenu("Debug/Apply Mass Distribution")]
        private void Debug_ApplyMassDistribution()
        {
            ApplyMassDistribution();
            Debug.Log($"[PlayerRagdollStun] Mass distribution applied. Total: {totalRagdollMass} kg");

            foreach (var rb in ragdollBodies)
            {
                if (rb != null)
                {
                    Debug.Log($"  - {rb.name}: {rb.mass:F2} kg");
                }
            }
        }
#endif
    }
}