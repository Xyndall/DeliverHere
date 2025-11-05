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

    // Input wrapper (same pattern as PlayerMovement)
    private InputSystem_Actions _input;
    private InputAction _interactAction; // Player.Interact (Button)
    private InputAction _extendAction;   // Player.Extend (1D axis)

    // Exposed to PlayerMovement
    public bool IsHolding => _heldBody != null;
    public float ControlPenalty01
    {
        get
        {
            if (_heldBody == null || massForMaxSlowdown <= 0.01f) return 0f;
            return Mathf.Clamp01(_heldBody.mass / massForMaxSlowdown);
        }
    }

    // Provide the yaw-forward used by holding so movement can align/clamp
    public Vector3 HoldForwardFlat => GetYawRotation() * Vector3.forward;

    // Internal state
    private float _holdDistance;         // Current distance forward from handsRoot
    private Rigidbody _heldBody;

    // PD target history (for damping with moving targets)
    private Vector3 _prevTargetPos;
    private Quaternion _prevTargetRot = Quaternion.identity;
    private bool _havePrevTarget;

    // Restore info (no longer restores isKinematic; it stays off)
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
        else
        {
            DisableInput();
        }

        enabled = IsOwner;
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        InitInputIfNeeded();
        EnableInput();
        enabled = true;
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        DisableInput();
        enabled = false;
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
            if (IsHolding) DropHeld();
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

        // Update hold distance by Extend axis
        float axis = _extendAction != null ? _extendAction.ReadValue<float>() : 0f;
        if (Mathf.Abs(axis) > 0.0001f)
        {
            float delta = distanceChangeSpeed * Time.deltaTime;
            _holdDistance = Mathf.Clamp(_holdDistance + axis * delta, minHoldDistance, maxHoldDistance);
        }

        // Toggle pickup/drop
        if (_interactAction != null && _interactAction.WasPressedThisFrame())
        {
            if (IsHolding) DropHeld();
            else TryPickup();
        }
    }

    private void FixedUpdate()
    {
        if (!IsOwner || !IsHolding || handsRoot == null) return;

        // Compute target pose in physics step
        Quaternion yawRot = GetYawRotation();
        Vector3 local = new Vector3(localHoldOffset.x, localHoldOffset.y, _holdDistance);
        Vector3 targetPos = handsRoot.position + yawRot * local;
        Quaternion targetRot = yawRot;

        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        // Initialize previous target snapshot on first frame / after pickup
        if (!_havePrevTarget)
        {
            _prevTargetPos = targetPos;
            _prevTargetRot = targetRot;
            _havePrevTarget = true;
        }

        // Estimate target linear and angular velocity (for damping with moving target)
        Vector3 targetVel = (targetPos - _prevTargetPos) / dt;

        Quaternion qTgtDelta = targetRot * Quaternion.Inverse(_prevTargetRot);
        qTgtDelta.ToAngleAxis(out float tgtAngleDeg, out Vector3 tgtAxis);
        if (float.IsNaN(tgtAxis.x) || tgtAxis.sqrMagnitude < 1e-8f) tgtAxis = Vector3.up;
        float tgtAngleRad = Mathf.Deg2Rad * tgtAngleDeg;
        Vector3 targetOmega = tgtAxis * (tgtAngleRad / dt);

        // Linear PD (AddForce with Acceleration -> mass independent)
        Vector3 posError = (targetPos - _heldBody.position);
        Vector3 velError = (targetVel - _heldBody.linearVelocity);

        float wPos = Mathf.Max(0f, holdPosFrequency) * 2f * Mathf.PI;
        float cPos = 2f * Mathf.Clamp01(holdPosDamping) * wPos;
        Vector3 accelCmd = (wPos * wPos) * posError + cPos * velError;

        // Clamp linear acceleration
        float maxAcc = Mathf.Max(0f, maxLinearAcceleration);
        if (maxAcc > 0f && accelCmd.sqrMagnitude > maxAcc * maxAcc)
            accelCmd = accelCmd.normalized * maxAcc;

        _heldBody.AddForce(accelCmd, ForceMode.Acceleration);

        // Angular PD (world space). Uses shortest-arc axis-angle error.
        Quaternion qErr = targetRot * Quaternion.Inverse(_heldBody.rotation);
        if (qErr.w < 0f) { qErr.x = -qErr.x; qErr.y = -qErr.y; qErr.z = -qErr.z; qErr.w = -qErr.w; } // shortest path
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

        // Update target history
        _prevTargetPos = targetPos;
        _prevTargetRot = targetRot;
    }

    // Yaw-only rotation from either camera or handsRoot
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

    // Kept to avoid breaking callers; no longer clamps movement.
    public float ClampForwardMovement(float desiredForwardDelta) => desiredForwardDelta;

    private void TryPickup()
    {
        // Aim ray: camera if available/allowed, else body yaw
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

        if (!Physics.SphereCast(origin, pickupCastRadius, dir, out RaycastHit hit, pickupRange, pickupMask, QueryTriggerInteraction.Ignore))
            return;

        var rb = hit.rigidbody;
        if (rb == null) return;

        // Cache previous state (no longer restoring isKinematic)
        _restore = new HeldRestore
        {
            UseGravity = rb.useGravity,
            Drag = rb.linearDamping,
            AngularDrag = rb.angularDamping,
            Interp = rb.interpolation,
            CollisionMode = rb.collisionDetectionMode
        };

        // Always dynamic follow; never make kinematic; no parenting.
        rb.isKinematic = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;


        // Allow fast spins if needed when snapping rotation
        rb.maxAngularVelocity = 100f;

        _heldBody = rb;

        // Reset PD target history to avoid a large initial impulse
        _havePrevTarget = false;
    }

    public void DropHeld()
    {
        if (_heldBody == null) return;

        // Restore relevant physics state except isKinematic.
        _heldBody.isKinematic = false;
        _heldBody.useGravity = _restore.UseGravity;
        _heldBody.linearDamping = _restore.Drag;
        _heldBody.angularDamping = _restore.AngularDrag;
        _heldBody.interpolation = _restore.Interp;
        _heldBody.collisionDetectionMode = _restore.CollisionMode;

        _heldBody = null;
        _havePrevTarget = false;
    }

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