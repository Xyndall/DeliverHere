using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;
using System;

[DisallowMultipleComponent]
public class PlayerHold : NetworkBehaviour
{
    [Header("Camera (used only for aiming pickup)")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private bool useCameraForPickupRay = true;

    [Header("Hold Relative To Body")]
    [SerializeField] private Transform handsRoot;
    [SerializeField] private bool useCameraYawForHold = true;
    [SerializeField] private Vector3 localHoldOffset = new Vector3(0f, 1f, 0f);
    [SerializeField] private float minHoldDistance = 0.8f;
    [SerializeField] private float maxHoldDistance = 2.2f;
    [SerializeField] private float distanceChangeSpeed = 2f;

    [Header("Pickup")]
    [SerializeField] private LayerMask pickupMask = ~0;
    [SerializeField] private float pickupCastRadius = 0.15f;
    [SerializeField] private float pickupRange = 3f;

    [Header("Movement Slowdown")]
    [SerializeField] private float massForMaxSlowdown = 10f;

    [Header("ConfigurableJoint (Position Drive)")]
    [Tooltip("Base linear drive spring (N/m) + per-kg scale produces final spring.")]
    [SerializeField] private float linearSpringBase = 450f;
    [SerializeField] private float linearSpringPerKg = 40f;
    [Tooltip("Linear drive damper (N·s/m).")]
    [SerializeField] private float linearDamper = 55f;
    [Tooltip("Max force linear drives can apply (0 = unlimited).")]
    [SerializeField] private float linearMaxForce = 0f;

    [Header("ConfigurableJoint (Rotation Drive)")]
    [Tooltip("Angular drive spring (N·m/rad). Increase for tighter rotation.")]
    [SerializeField] private float angularSpring = 1200f;
    [Tooltip("Angular drive damper (N·m·s/rad). Increase to reduce spin/overshoot.")]
    [SerializeField] private float angularDamper = 120f;
    [Tooltip("Max torque angular drive can apply (0 = unlimited).")]
    [SerializeField] private float angularMaxForce = 0f;

    [Header("Joint Stabilization")]
    [Tooltip("Projection keeps the bodies together if solver can't fully satisfy constraints.")]
    [SerializeField] private bool enableProjection = true;
    [SerializeField] private float projectionDistance = 0.03f;
    [SerializeField] private float projectionAngle = 5f;
    [Tooltip("Optional clamp of linear speed (m/s) while held.")]
    [SerializeField] private float maxLinearSpeed = 5f;
    [Tooltip("Optional clamp of angular speed (deg/s) while held.")]
    [SerializeField] private float maxAngularSpeedDeg = 360f;

    [Header("Anchor Ramp")]
    [Tooltip("Seconds to smoothly extend from 0 to hold distance after pickup (0 disables).")]
    [SerializeField] private float anchorRampTime = 0.18f;

    [Header("Misc")]
    [SerializeField] private bool disableGravityWhileHeld = true;

    [Tooltip("Apply extra drag while held for stability. Disable to allow better throws.")]
    [SerializeField] private bool boostDragWhileHeld = false;
    [Tooltip("Minimum drag while held if boosting is enabled.")]
    [SerializeField] private float heldDrag = 2.5f;
    [Tooltip("Minimum angular drag while held if boosting is enabled.")]
    [SerializeField] private float heldAngularDrag = 3.0f;

    [Tooltip("Force gravity ON after drop regardless of original state.")]
    [SerializeField] private bool alwaysEnableGravityOnDrop = true;

    [Header("Throw Tuning")]
    [Tooltip("Fraction of anchor velocity added at release (before mass scaling).")]
    [Range(0f, 2f)] [SerializeField] private float releaseAnchorVelScale = 1.0f;
    [Tooltip("Extra forward speed added at release (m/s) before mass scaling.")]
    [SerializeField] private float releaseForwardBoost = 1.5f;
    [Tooltip("Clamp for the final release speed (m/s). 0 = no clamp.")]
    [SerializeField] private float maxReleaseSpeed = 12f;

    [Header("Throw Mass Scaling")]
    [Tooltip("Apply mass-based reduction to throw boost.")]
    [SerializeField] private bool scaleThrowByMass = true;
    [Tooltip("Mass (kg) at which boost reaches its minimum scaling.")]
    [SerializeField] private float massForMinThrowBoost = 15f;
    [Tooltip("Forward boost multiplier at or above massForMinThrowBoost (0-1).")]
    [Range(0f,1f)][SerializeField] private float forwardBoostMultiplierAtMaxMass = 0.3f;
    [Tooltip("Anchor velocity multiplier at or above massForMinThrowBoost (0-1).")]
    [Range(0f,1f)][SerializeField] private float anchorVelMultiplierAtMaxMass = 0.5f;

    [Header("Auto-Drop (Distance)")]
    [Tooltip("Automatically drop if object drifts too far from player.")]
    [SerializeField] private bool enableAutoDropOnDistance = true;
    [Tooltip("Distance threshold (meters) from anchor/body before drop logic starts.")]
    [SerializeField] private float maxHoldSeparation = 3.0f;
    [Tooltip("Continuous time (seconds) beyond threshold required to trigger drop.")]
    [SerializeField] private float autoDropDelay = 0.25f;
    [Tooltip("Grace time after pickup before separation checks begin.")]
    [SerializeField] private float postPickupGraceTime = 0.5f;

    // Input
    private InputSystem_Actions _input;
    private InputAction _interactAction;
    private InputAction _extendAction;

    // Network sync (server authoritative)
    private NetworkVariable<NetworkObjectReference> _heldRef =
        new NetworkVariable<NetworkObjectReference>(default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

    public bool IsHolding => _heldBody != null;
    public float ControlPenalty01
    {
        get
        {
            if (_heldBody == null || massForMaxSlowdown <= 0.01f) return 0f;
            return Mathf.Clamp01(_heldBody.mass / massForMaxSlowdown);
        }
    }
    public float? HeldMass => _heldBody != null ? (float?)_heldBody.mass : null;

    // Added: expose the currently held Rigidbody so UI can query components (e.g., PackageProperties)
    public Rigidbody HeldBody => _heldBody;

    public Vector3 HoldForwardFlat => GetYawRotation() * Vector3.forward;

    public float CurrentSeparation => _heldBody != null && _anchorRb != null
        ? Vector3.Distance(_heldBody.position, _anchorRb.position)
        : 0f;

    public bool IsSeparationExceeded => enableAutoDropOnDistance && IsHolding && CurrentSeparation > maxHoldSeparation;

    private float _holdDistance;
    private Rigidbody _heldBody;

    // Anchor (kinematic)
    private Rigidbody _anchorRb;
    private ConfigurableJoint _joint;
    private float _anchorRampElapsed;

    // Anchor velocity (for release)
    private Vector3 _prevAnchorPos;
    private Vector3 _anchorVel;

    // Auto-drop timers
    private float _pickupTimestamp;
    private float _exceededSeparationTime;

    private struct HeldRestore
    {
        public bool UseGravity;
        public float Drag;
        public float AngularDrag;
        public RigidbodyInterpolation Interp;
        public CollisionDetectionMode CollisionMode;
        public CollisionDetectionMode CollisionDetectionMode => CollisionMode;
    }
    private HeldRestore _restore;
    private bool _restoreCaptured;

    private void Awake()
    {
        if (handsRoot == null) handsRoot = transform;
        _holdDistance = Mathf.Clamp((_holdDistance <= 0f
                ? (minHoldDistance + maxHoldDistance) * 0.5f
                : _holdDistance),
            minHoldDistance, maxHoldDistance);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsOwner && cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (IsOwner)
        {
            InitInputIfNeeded();
            EnableInput();
        }

        enabled = IsServer || IsOwner;
        EnsureAnchorExists();
        _heldRef.OnValueChanged += OnHeldRefChanged;
        OnHeldRefChanged(default, _heldRef.Value);
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
        InitInputIfNeeded();
        EnableInput();
        enabled = IsServer || IsOwner;
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        if (!IsServer) DisableInput();
        enabled = IsServer || IsOwner;
    }

    private void OnEnable()
    {
        if (IsOwner)
        {
            InitInputIfNeeded();
            EnableInput();
        }
        EnsureAnchorExists();
    }

    private void OnDisable()
    {
        if (IsOwner) DisableInput();
    }

    public override void OnDestroy()
    {
        try
        {
            _heldRef.OnValueChanged -= OnHeldRefChanged;
            if (IsServer && IsHolding) ServerDropHeldInternal();
            if (_anchorRb != null) Destroy(_anchorRb.gameObject);
            _input?.Dispose();
        }
        finally { base.OnDestroy(); }
    }

    private void Update()
    {
        if (!IsOwner) return;

        float axis = _extendAction != null ? _extendAction.ReadValue<float>() : 0f;
        if (Mathf.Abs(axis) > 0.0001f)
        {
            float delta = distanceChangeSpeed * Time.deltaTime;
            _holdDistance = Mathf.Clamp(_holdDistance + axis * delta, minHoldDistance, maxHoldDistance);
        }

        if (_interactAction != null && _interactAction.WasPressedThisFrame())
        {
            if (IsHolding) RequestDropServerRpc();
            else
            {
                Vector3 origin, dir;
                if (useCameraForPickupRay && cameraTransform != null)
                {
                    origin = cameraTransform.position;
                    dir = cameraTransform.forward;
                }
                else
                {
                    Quaternion yawRot = GetYawRotation();
                    origin = handsRoot.position + Vector3.up * Mathf.Max(0.1f, localHoldOffset.y);
                    dir = yawRot * Vector3.forward;
                }
                RequestPickupServerRpc(origin, dir, _holdDistance);
            }
        }
    }

    private void FixedUpdate()
    {
        if (_anchorRb != null)
        {
            Quaternion yawRot = GetYawRotation();
            float targetDist = _holdDistance;
            if (IsHolding && anchorRampTime > 0f && _anchorRampElapsed < anchorRampTime)
            {
                _anchorRampElapsed += Time.fixedDeltaTime;
                float t = Mathf.Clamp01(_anchorRampElapsed / anchorRampTime);
                targetDist = Mathf.Lerp(0f, _holdDistance, t);
            }
            Vector3 local = new Vector3(localHoldOffset.x, localHoldOffset.y, targetDist);

            // Update anchor pose and velocity (server)
            Vector3 newAnchorPos = handsRoot.position + yawRot * local;
            _anchorVel = (newAnchorPos - _prevAnchorPos) / Mathf.Max(1e-5f, Time.fixedDeltaTime);
            _prevAnchorPos = newAnchorPos;

            _anchorRb.position = newAnchorPos;
            _anchorRb.rotation = yawRot;
        }

        if (!IsServer || !IsHolding || _heldBody == null) return;

        // Distance-based auto drop
        if (enableAutoDropOnDistance)
        {
            float sincePickup = Time.time - _pickupTimestamp;
            if (sincePickup >= postPickupGraceTime)
            {
                if (CurrentSeparation > maxHoldSeparation)
                {
                    _exceededSeparationTime += Time.fixedDeltaTime;
                    if (_exceededSeparationTime >= autoDropDelay)
                    {
                        ServerDropHeldInternal(); // no throw boost, just drop due to distance
                        return;
                    }
                }
                else
                {
                    _exceededSeparationTime = 0f;
                }
            }
        }

        if (maxLinearSpeed > 0f && _heldBody.linearVelocity.magnitude > maxLinearSpeed)
            _heldBody.linearVelocity = _heldBody.linearVelocity.normalized * maxLinearSpeed;

        float maxOmegaRad = Mathf.Deg2Rad * Mathf.Max(30f, maxAngularSpeedDeg);
        if (_heldBody.angularVelocity.magnitude > maxOmegaRad)
            _heldBody.angularVelocity = _heldBody.angularVelocity.normalized * maxOmegaRad;

        if (disableGravityWhileHeld && _heldBody.useGravity)
            _heldBody.useGravity = false;
    }

    private void EnsureAnchorExists()
    {
        if (_anchorRb != null) return;
        GameObject anchorGO = new GameObject("HoldAnchor");
        anchorGO.hideFlags = HideFlags.HideInHierarchy;
        _anchorRb = anchorGO.AddComponent<Rigidbody>();
        _anchorRb.isKinematic = true;
        _anchorRb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        _anchorRb.interpolation = RigidbodyInterpolation.None;
        _prevAnchorPos = _anchorRb.position;
    }

    private void OnHeldRefChanged(NetworkObjectReference previous, NetworkObjectReference current)
    {
        if (!current.TryGet(out NetworkObject netObj))
        {
            _heldBody = null;
            DestroyJoint();
            _restoreCaptured = false;
            _exceededSeparationTime = 0f;
            return;
        }

        _heldBody = netObj.GetComponent<Rigidbody>();
        _anchorRampElapsed = 0f;
        _prevAnchorPos = _anchorRb.position;

        if (IsServer && _heldBody != null) CreateOrConfigureJoint();

        if (_heldBody != null)
        {
            PickedUp?.Invoke(_heldBody);
        }
    }

    private void CreateOrConfigureJoint()
    {
        DestroyJoint();
        if (_heldBody == null || _anchorRb == null) return;

        if (!_restoreCaptured)
        {
            _restore = new HeldRestore
            {
                UseGravity = _heldBody.useGravity,
                Drag = _heldBody.linearDamping,
                AngularDrag = _heldBody.angularDamping,
                Interp = _heldBody.interpolation,
                CollisionMode = _heldBody.collisionDetectionMode
            };
            _restoreCaptured = true;
        }

        var cj = _heldBody.gameObject.AddComponent<ConfigurableJoint>();
        cj.autoConfigureConnectedAnchor = false;
        cj.connectedBody = _anchorRb;
        cj.anchor = Vector3.zero;
        cj.connectedAnchor = Vector3.zero;
        cj.configuredInWorldSpace = true;

        cj.xMotion = ConfigurableJointMotion.Free;
        cj.yMotion = ConfigurableJointMotion.Free;
        cj.zMotion = ConfigurableJointMotion.Free;
        cj.angularXMotion = ConfigurableJointMotion.Free;
        cj.angularYMotion = ConfigurableJointMotion.Free;
        cj.angularZMotion = ConfigurableJointMotion.Free;

        float mass = Mathf.Max(0.001f, _heldBody.mass);
        float linearSpring = linearSpringBase + linearSpringPerKg * mass;
        var drive = new JointDrive
        {
            positionSpring = Mathf.Max(0f, linearSpring),
            positionDamper = Mathf.Max(0f, linearDamper),
            maximumForce = (linearMaxForce <= 0f) ? float.PositiveInfinity : linearMaxForce
        };
        cj.xDrive = drive;
        cj.yDrive = drive;
        cj.zDrive = drive;

        cj.rotationDriveMode = RotationDriveMode.Slerp;
        var slerp = new JointDrive
        {
            positionSpring = Mathf.Max(0f, angularSpring),
            positionDamper = Mathf.Max(0f, angularDamper),
            maximumForce = (angularMaxForce <= 0f) ? float.PositiveInfinity : angularMaxForce
        };
        cj.slerpDrive = slerp;

        cj.targetPosition = Vector3.zero;
        cj.targetVelocity = Vector3.zero;
        cj.targetRotation = Quaternion.identity;
        cj.targetAngularVelocity = Vector3.zero;

        if (enableProjection)
        {
            cj.projectionMode = JointProjectionMode.PositionAndRotation;
            cj.projectionDistance = Mathf.Max(0.001f, projectionDistance);
            cj.projectionAngle = Mathf.Clamp(projectionAngle, 0.1f, 180f);
        }
        else
        {
            cj.projectionMode = JointProjectionMode.None;
        }

        _joint = cj;

        if (boostDragWhileHeld)
        {
            _heldBody.linearDamping = Mathf.Max(_heldBody.linearDamping, heldDrag);
            _heldBody.angularDamping = Mathf.Max(_heldBody.angularDamping, heldAngularDrag);
        }

        if (disableGravityWhileHeld) _heldBody.useGravity = false;
        _heldBody.maxAngularVelocity = Mathf.Max(_heldBody.maxAngularVelocity, 100f);
        _heldBody.interpolation = RigidbodyInterpolation.Interpolate;
        _heldBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _heldBody.isKinematic = false;

        // Separation timers
        _pickupTimestamp = Time.time;
        _exceededSeparationTime = 0f;
    }

    private void DestroyJoint()
    {
        if (_joint != null)
        {
            if (IsServer) Destroy(_joint);
            _joint = null;
        }
    }

    [ServerRpc]
    private void RequestPickupServerRpc(Vector3 origin, Vector3 dir, float requestedHoldDistance)
    {
        if (_heldBody != null) return;

        _holdDistance = Mathf.Clamp(requestedHoldDistance, minHoldDistance, maxHoldDistance);

        if (!Physics.SphereCast(origin, pickupCastRadius, dir, out RaycastHit hit, pickupRange, pickupMask, QueryTriggerInteraction.Ignore))
            return;

        var rb = hit.rigidbody;
        if (rb == null) return;

        var netObj = rb.GetComponent<NetworkObject>();
        if (netObj == null) return;

        if (!netObj.IsOwner)
            netObj.ChangeOwnership(OwnerClientId);

        _heldBody = rb;
        _heldRef.Value = new NetworkObjectReference(netObj);
        _anchorRampElapsed = 0f;
        _restoreCaptured = false;
        _pickupTimestamp = Time.time;
        _exceededSeparationTime = 0f;

        CreateOrConfigureJoint();
    }

    [ServerRpc]
    private void RequestDropServerRpc()
    {
        if (_heldBody == null) return;

        Vector3 releaseVel = _heldBody.linearVelocity;
        Vector3 forward = GetYawRotation() * Vector3.forward;

        float mass = _heldBody.mass;
        float t = scaleThrowByMass && massForMinThrowBoost > 0f
            ? Mathf.Clamp01(mass / massForMinThrowBoost)
            : 0f;

        float anchorScale = Mathf.Lerp(1f, anchorVelMultiplierAtMaxMass, t);
        float forwardScale = Mathf.Lerp(1f, forwardBoostMultiplierAtMaxMass, t);

        releaseVel += _anchorVel * releaseAnchorVelScale * anchorScale;
        releaseVel += forward * releaseForwardBoost * forwardScale;

        if (maxReleaseSpeed > 0f && releaseVel.sqrMagnitude > maxReleaseSpeed * maxReleaseSpeed)
            releaseVel = releaseVel.normalized * maxReleaseSpeed;

        ServerDropHeldInternal(releaseVel);
    }

    private void ServerDropHeldInternal(Vector3? overrideVelocity = null)
    {
        if (_heldBody == null) return;

        DestroyJoint();

        _heldBody.isKinematic = false;
        _heldBody.useGravity = alwaysEnableGravityOnDrop ? true : _restore.UseGravity;
        _heldBody.linearDamping = _restore.Drag;
        _heldBody.angularDamping = _restore.AngularDrag;
        _heldBody.interpolation = _restore.Interp;
        _heldBody.collisionDetectionMode = _restore.CollisionMode;

        if (overrideVelocity.HasValue)
            _heldBody.linearVelocity = overrideVelocity.Value;

        var netObj = _heldBody.GetComponent<NetworkObject>();
        if (netObj != null && netObj.OwnerClientId == OwnerClientId)
        {
            // Optionally reclaim ownership
        }

        var droppedBody = _heldBody;

        _heldBody = null;
        _heldRef.Value = default;
        _restoreCaptured = false;
        _exceededSeparationTime = 0f;

        Dropped?.Invoke(droppedBody);
    }

    private Quaternion GetYawRotation()
    {
        bool canUseCamera = useCameraYawForHold && cameraTransform != null && (IsOwner || !IsServer);
        Vector3 srcFwd = canUseCamera
            ? cameraTransform.forward
            : (handsRoot != null ? handsRoot.forward : transform.forward);

        Vector3 yawFwd = Vector3.ProjectOnPlane(srcFwd, Vector3.up);
        if (yawFwd.sqrMagnitude < 1e-6f) yawFwd = new Vector3(srcFwd.x, 0f, srcFwd.z);
        if (yawFwd.sqrMagnitude < 1e-6f) yawFwd = Vector3.forward;
        yawFwd.Normalize();
        return Quaternion.LookRotation(yawFwd, Vector3.up);
    }

    public float ClampForwardMovement(float desiredForwardDelta) => desiredForwardDelta;

    private void InitInputIfNeeded()
    {
        if (_input != null) return;
        _input = new InputSystem_Actions();
        _interactAction = _input.Player.Interact;
        _extendAction = _input.Player.Extend;
    }

    private void EnableInput()
    {
        _input?.Enable();
        _interactAction?.Enable();
        _extendAction?.Enable();
    }

    private void DisableInput()
    {
        _extendAction?.Disable();
        _interactAction?.Disable();
        _input?.Disable();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        minHoldDistance = Mathf.Max(0.2f, minHoldDistance);
        maxHoldDistance = Mathf.Max(minHoldDistance, maxHoldDistance);
        distanceChangeSpeed = Mathf.Max(0.01f, distanceChangeSpeed);
        pickupCastRadius = Mathf.Max(0.01f, pickupCastRadius);
        pickupRange = Mathf.Max(0.2f, pickupRange);
        massForMaxSlowdown = Mathf.Max(0.01f, massForMaxSlowdown);

        linearSpringBase = Mathf.Max(1f, linearSpringBase);
        linearSpringPerKg = Mathf.Max(0f, linearSpringPerKg);
        linearDamper = Mathf.Max(0f, linearDamper);
        anchorRampTime = Mathf.Clamp(anchorRampTime, 0f, 1f);

        maxLinearSpeed = Mathf.Max(0f, maxLinearSpeed);
        maxAngularSpeedDeg = Mathf.Max(30f, maxAngularSpeedDeg);

        angularSpring = Mathf.Max(0f, angularSpring);
        angularDamper = Mathf.Max(0f, angularDamper);
        projectionDistance = Mathf.Clamp(projectionDistance, 0.001f, 0.5f);
        projectionAngle = Mathf.Clamp(projectionAngle, 0.1f, 180f);

        heldDrag = Mathf.Max(0f, heldDrag);
        heldAngularDrag = Mathf.Max(0f, heldAngularDrag);

        releaseAnchorVelScale = Mathf.Clamp(releaseAnchorVelScale, 0f, 2f);
        releaseForwardBoost = Mathf.Max(0f, releaseForwardBoost);
        maxReleaseSpeed = Mathf.Max(0f, maxReleaseSpeed);

        massForMinThrowBoost = Mathf.Max(0.01f, massForMinThrowBoost);
        forwardBoostMultiplierAtMaxMass = Mathf.Clamp01(forwardBoostMultiplierAtMaxMass);
        anchorVelMultiplierAtMaxMass = Mathf.Clamp01(anchorVelMultiplierAtMaxMass);

        maxHoldSeparation = Mathf.Max(0.1f, maxHoldSeparation);
        autoDropDelay = Mathf.Clamp(autoDropDelay, 0f, 5f);
        postPickupGraceTime = Mathf.Clamp(postPickupGraceTime, 0f, 5f);
    }
#endif

    public Transform AnchorTransform => _anchorRb != null ? _anchorRb.transform : null;

    // Notify external systems (e.g., PlayerArms) when pickup/drop occur.
    public event Action<Rigidbody> PickedUp;
    public event Action<Rigidbody> Dropped;
}