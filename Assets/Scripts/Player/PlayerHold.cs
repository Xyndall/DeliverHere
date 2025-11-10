using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

[DisallowMultipleComponent]
public class PlayerHold : NetworkBehaviour
{
    [Header("Camera (used only for aiming pickup)")]
    [Tooltip("Optional. If null, will use Camera.main.")]
    [SerializeField] private Transform cameraTransform;
    [Tooltip("Use camera for pickup aim. If false, use body/hands yaw for pickup aim.")]
    [SerializeField] private bool useCameraForPickupRay = true;

    [Header("Hold Relative To Body")]
    [Tooltip("Root for hands/body yaw (e.g., player root). Defaults to this.transform if null.")]
    [SerializeField] private Transform handsRoot;
    [Tooltip("Use camera yaw for hold forward (ignores camera pitch). If false, use handsRoot yaw.")]
    [SerializeField] private bool useCameraYawForHold = true;
    [Tooltip("Local offset from handsRoot (x=side, y=height). Z is driven by hold distance.")]
    [SerializeField] private Vector3 localHoldOffset = new Vector3(0f, 1.0f, 0f);
    [Tooltip("Min distance forward from handsRoot.")]
    [SerializeField] private float minHoldDistance = 0.8f;
    [Tooltip("Max distance forward from handsRoot.")]
    [SerializeField] private float maxHoldDistance = 2.2f;
    [Tooltip("Meters per second when adjusting hold distance with Extend input.")]
    [SerializeField] private float distanceChangeSpeed = 2.0f;

    [Header("Pickup")]
    [Tooltip("Layers that can be picked up.")]
    [SerializeField] private LayerMask pickupMask = ~0;
    [Tooltip("Sphere radius for pickup test.")]
    [SerializeField] private float pickupCastRadius = 0.15f;
    [Tooltip("How far ahead to search when picking up.")]
    [SerializeField] private float pickupRange = 3.0f;

    [Header("Movement Slowdown")]
    [Tooltip("Mass (kg) that maps to max slowdown. Heavier will clamp at max.")]
    [SerializeField] private float massForMaxSlowdown = 10f;

    [Header("PD Spring Tuning")]
    [Tooltip("Natural frequency (Hz) of the linear spring. Higher = snappier.")]
    [SerializeField] private float holdPosFrequency = 8f;
    [Tooltip("Damping ratio for the linear spring. 1 = critically damped.")]
    [Range(0f, 2f), SerializeField] private float holdPosDamping = 1.0f;
    [Tooltip("Clamp for linear acceleration (m/s^2).")]
    [SerializeField] private float maxLinearAcceleration = 80f;

    [Tooltip("Natural frequency (Hz) of the angular spring. Higher = snappier.")]
    [SerializeField] private float holdRotFrequency = 8f;
    [Tooltip("Damping ratio for the angular spring. 1 = critically damped.")]
    [Range(0f, 2f), SerializeField] private float holdRotDamping = 1.0f;
    [Tooltip("Clamp for angular acceleration (deg/s^2).")]
    [SerializeField] private float maxAngularAccelerationDeg = 800f;

    // Input
    private InputSystem_Actions _input;
    private InputAction _interactAction;
    private InputAction _extendAction;

    // Network sync of held object (server authoritative)
    private NetworkVariable<NetworkObjectReference> _heldRef = new NetworkVariable<NetworkObjectReference>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Public status
    public bool IsHolding => _heldBody != null;
    public float ControlPenalty01
    {
        get
        {
            if (_heldBody == null || massForMaxSlowdown <= 0.01f) return 0f;
            return Mathf.Clamp01(_heldBody.mass / massForMaxSlowdown);
        }
    }
    public Vector3 HoldForwardFlat => GetYawRotation() * Vector3.forward;

    // Internal state
    private float _holdDistance;
    private Rigidbody _heldBody;

    private Vector3 _prevTargetPos;
    private Quaternion _prevTargetRot = Quaternion.identity;
    private bool _havePrevTarget;

    private struct HeldRestore
    {
        public bool UseGravity;
        public float Drag;
        public float AngularDrag;
        public RigidbodyInterpolation Interp;
        public CollisionDetectionMode CollisionMode;
    }
    private HeldRestore _restore;

    private void Awake()
    {
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
        if (handsRoot == null) handsRoot = transform;

        _holdDistance = Mathf.Clamp((_holdDistance <= 0f ? (minHoldDistance + maxHoldDistance) * 0.5f : _holdDistance),
                                    minHoldDistance, maxHoldDistance);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsOwner)
        {
            InitInputIfNeeded();
            EnableInput();
        }

        // Enable component on server for ALL players so server can run physics for remote owners.
        enabled = IsServer || IsOwner;

        _heldRef.OnValueChanged += OnHeldRefChanged;

        // Initialize existing value if joining late
        OnHeldRefChanged(default, _heldRef.Value);
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        InitInputIfNeeded();
        EnableInput();
        // Keep enabled state: server must stay enabled regardless.
        enabled = IsServer || IsOwner;
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        if (!IsServer) // Server never disables (needs physics authority)
            DisableInput();
        enabled = IsServer || IsOwner;
    }

    private void OnEnable()
    {
        if (IsOwner)
        {
            InitInputIfNeeded();
            EnableInput();
        }
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
            if (IsServer && IsHolding) ServerDropHeldInternal(); // ensure cleanup
            _input?.Dispose();
        }
        finally
        {
            base.OnDestroy();
        }
    }

    private void Update()
    {
        if (!IsOwner) return;

        // Adjust hold distance locally (client sends to server if changed while holding)
        float axis = _extendAction != null ? _extendAction.ReadValue<float>() : 0f;
        if (Mathf.Abs(axis) > 0.0001f)
        {
            float delta = distanceChangeSpeed * Time.deltaTime;
            _holdDistance = Mathf.Clamp(_holdDistance + axis * delta, minHoldDistance, maxHoldDistance);
        }

        if (_interactAction != null && _interactAction.WasPressedThisFrame())
        {
            if (IsHolding)
            {
                RequestDropServerRpc();
            }
            else
            {
                // Compute pickup ray client-side, send to server
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
        // Only server applies physics; clients observe via replication.
        if (!IsServer || !IsHolding || handsRoot == null) return;

        Quaternion yawRot = GetYawRotation();
        Vector3 local = new Vector3(localHoldOffset.x, localHoldOffset.y, _holdDistance);
        Vector3 targetPos = handsRoot.position + yawRot * local;
        Quaternion targetRot = yawRot;

        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        if (!_havePrevTarget)
        {
            _prevTargetPos = targetPos;
            _prevTargetRot = targetRot;
            _havePrevTarget = true;
        }

        Vector3 targetVel = (targetPos - _prevTargetPos) / dt;

        Quaternion qTgtDelta = targetRot * Quaternion.Inverse(_prevTargetRot);
        qTgtDelta.ToAngleAxis(out float tgtAngleDeg, out Vector3 tgtAxis);
        if (float.IsNaN(tgtAxis.x) || tgtAxis.sqrMagnitude < 1e-8f) tgtAxis = Vector3.up;
        float tgtAngleRad = Mathf.Deg2Rad * tgtAngleDeg;
        Vector3 targetOmega = tgtAxis * (tgtAngleRad / dt);

        Vector3 posError = (targetPos - _heldBody.position);
        Vector3 velError = (targetVel - _heldBody.linearVelocity);

        float wPos = Mathf.Max(0f, holdPosFrequency) * 2f * Mathf.PI;
        float cPos = 2f * Mathf.Clamp01(holdPosDamping) * wPos;
        Vector3 accelCmd = (wPos * wPos) * posError + cPos * velError;

        float maxAcc = Mathf.Max(0f, maxLinearAcceleration);
        if (maxAcc > 0f && accelCmd.sqrMagnitude > maxAcc * maxAcc)
            accelCmd = accelCmd.normalized * maxAcc;

        _heldBody.AddForce(accelCmd, ForceMode.Acceleration);

        Quaternion qErr = targetRot * Quaternion.Inverse(_heldBody.rotation);
        if (qErr.w < 0f) { qErr.x = -qErr.x; qErr.y = -qErr.y; qErr.z = -qErr.z; qErr.w = -qErr.w; }
        qErr.ToAngleAxis(out float errAngleDeg, out Vector3 errAxis);
        if (float.IsNaN(errAxis.x) || errAxis.sqrMagnitude < 1e-8f)
        {
            errAxis = Vector3.right;
            errAngleDeg = 0f;
        }
        float errAngleRad = Mathf.Deg2Rad * errAngleDeg;
        errAxis.Normalize();

        float wRot = Mathf.Max(0f, holdRotFrequency) * 2f * Mathf.PI;
        float cRot = 2f * Mathf.Clamp01(holdRotDamping) * wRot;

        float omegaAlong = Vector3.Dot(_heldBody.angularVelocity, errAxis);
        float omegaTgtAlong = Vector3.Dot(targetOmega, errAxis);

        float alphaAlong = (wRot * wRot) * errAngleRad + cRot * (omegaTgtAlong - omegaAlong);
        Vector3 alphaCmd = errAxis * alphaAlong;

        float maxAlphaRad = Mathf.Deg2Rad * Mathf.Max(0f, maxAngularAccelerationDeg);
        if (maxAlphaRad > 0f && alphaCmd.sqrMagnitude > maxAlphaRad * maxAlphaRad)
            alphaCmd = alphaCmd.normalized * maxAlphaRad;

        _heldBody.AddTorque(alphaCmd, ForceMode.Acceleration);

        _prevTargetPos = targetPos;
        _prevTargetRot = targetRot;
    }

    private void OnHeldRefChanged(NetworkObjectReference previous, NetworkObjectReference current)
    {
        if (!current.TryGet(out NetworkObject netObj))
        {
            _heldBody = null;
            _havePrevTarget = false;
            return;
        }

        _heldBody = netObj.GetComponent<Rigidbody>();
        _havePrevTarget = false;
    }

    // ServerRpc: client requests pickup
    [ServerRpc]
    private void RequestPickupServerRpc(Vector3 origin, Vector3 dir, float requestedHoldDistance)
    {
        if (_heldBody != null) return; // already holding

        // Use client-provided hold distance (clamped)
        _holdDistance = Mathf.Clamp(requestedHoldDistance, minHoldDistance, maxHoldDistance);

        if (!Physics.SphereCast(origin, pickupCastRadius, dir, out RaycastHit hit, pickupRange, pickupMask, QueryTriggerInteraction.Ignore))
            return;

        var rb = hit.rigidbody;
        if (rb == null) return;

        // Ensure network object reference exists
        var netObj = rb.GetComponent<NetworkObject>();
        if (netObj == null) return;

        // Optionally transfer ownership of the object to the player (so other client inputs won't conflict)
        if (!netObj.IsOwner)
        {
            netObj.ChangeOwnership(OwnerClientId);
        }

        _restore = new HeldRestore
        {
            UseGravity = rb.useGravity,
            Drag = rb.linearDamping,
            AngularDrag = rb.angularDamping,
            Interp = rb.interpolation,
            CollisionMode = rb.collisionDetectionMode
        };

        rb.isKinematic = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.maxAngularVelocity = 100f;

        _heldBody = rb;
        _heldRef.Value = new NetworkObjectReference(netObj);

        _havePrevTarget = false;
    }

    [ServerRpc]
    private void RequestDropServerRpc()
    {
        if (_heldBody == null) return;
        ServerDropHeldInternal();
    }

    private void ServerDropHeldInternal()
    {
        if (_heldBody == null) return;

        _heldBody.isKinematic = false;
        _heldBody.useGravity = _restore.UseGravity;
        _heldBody.linearDamping = _restore.Drag;
        _heldBody.angularDamping = _restore.AngularDrag;
        _heldBody.interpolation = _restore.Interp;
        _heldBody.collisionDetectionMode = _restore.CollisionMode;

        // Optionally revoke ownership back to server (host) if desired:
        var netObj = _heldBody.GetComponent<NetworkObject>();
        if (netObj != null && netObj.OwnerClientId == OwnerClientId)
        {
            // Uncomment to return to server ownership:
            // netObj.ChangeOwnership(NetworkManager.ServerClientId);
        }

        _heldBody = null;
        _heldRef.Value = default;
        _havePrevTarget = false;
    }

    private Quaternion GetYawRotation()
    {
        Vector3 srcFwd;
        if (useCameraYawForHold && cameraTransform != null)
            srcFwd = cameraTransform.forward;
        else
            srcFwd = handsRoot != null ? handsRoot.forward : transform.forward;

        Vector3 yawFwd = Vector3.ProjectOnPlane(srcFwd, Vector3.up);
        if (yawFwd.sqrMagnitude < 1e-6f) yawFwd = new Vector3(srcFwd.x, 0f, srcFwd.z);
        if (yawFwd.sqrMagnitude < 1e-6f) yawFwd = Vector3.forward;
        yawFwd.Normalize();
        return Quaternion.LookRotation(yawFwd, Vector3.up);
    }

    public float ClampForwardMovement(float desiredForwardDelta) => desiredForwardDelta;

    // Input init
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

        holdPosFrequency = Mathf.Max(0f, holdPosFrequency);
        holdPosDamping = Mathf.Clamp(holdPosDamping, 0f, 2f);
        maxLinearAcceleration = Mathf.Max(0f, maxLinearAcceleration);

        holdRotFrequency = Mathf.Max(0f, holdRotFrequency);
        holdRotDamping = Mathf.Clamp(holdRotDamping, 0f, 2f);
        maxAngularAccelerationDeg = Mathf.Max(0f, maxAngularAccelerationDeg);
    }
#endif
}