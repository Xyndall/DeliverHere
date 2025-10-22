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

    [Header("Hold Mode")]
    [Tooltip("Disable gravity while held (dynamic follow).")]
    [SerializeField] private bool disableGravityWhileHeld = true;
    [Tooltip("Position follow speed (m/s).")]
    [SerializeField] private float followPositionSpeed = 20f;
    [Tooltip("Rotation follow speed (deg/s).")]
    [SerializeField] private float followRotationSpeedDeg = 720f;

    [Header("Obstacle Interaction")]
    [Tooltip("Layers considered obstacles for the held item.")]
    [SerializeField] private LayerMask obstacleMask = ~0;
    [Tooltip("Approx radius of the held item for obstacle checks.")]
    [SerializeField] private float holdCollisionRadius = 0.25f;
    [Tooltip("Small padding to keep the held item off walls.")]
    [SerializeField] private float holdSkin = 0.03f;
    [Tooltip("Clamp hold distance against obstacles so the item doesn't clip.")]
    [SerializeField] private bool clampHoldAgainstObstacles = true;

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
    private Transform _holdPoint;        // Follows yaw basis
    private float _holdDistance;         // Current distance forward from handsRoot
    private Rigidbody _heldBody;

    // Restore info (no longer restores isKinematic; it stays off)
    private struct HeldRestore
    {
        public Transform Parent;
        public bool UseGravity;
        public float Drag;
        public float AngularDrag;
        public RigidbodyInterpolation Interp;
        public CollisionDetectionMode CollisionMode;
        public bool ParentedToHold;
    }
    private HeldRestore _restore;

    private void Awake()
    {
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (handsRoot == null) handsRoot = transform;

        _holdDistance = Mathf.Clamp((_holdDistance <= 0f ? (minHoldDistance + maxHoldDistance) * 0.5f : _holdDistance),
                                    minHoldDistance, maxHoldDistance);

        if (_holdPoint == null)
        {
            var go = new GameObject("HoldPoint");
            _holdPoint = go.transform;
            _holdPoint.SetParent(transform, false);
        }
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

        // Drive hold point using yaw-only basis (forward is horizontal)
        if (_holdPoint != null && handsRoot != null)
        {
            Quaternion yawRot = GetYawRotation();

            // Optionally clamp desired hold distance against obstacles so we don't clip
            if (clampHoldAgainstObstacles)
            {
                _holdDistance = Mathf.Min(_holdDistance, GetMaxClearHoldDistance(yawRot, _holdDistance));
            }

            Vector3 local = new Vector3(localHoldOffset.x, localHoldOffset.y, _holdDistance);
            _holdPoint.position = handsRoot.position + yawRot * local;
            _holdPoint.rotation = yawRot;
        }

        // No kinematic snapping; we always use dynamic follow in FixedUpdate.
    }

    private void FixedUpdate()
    {
        // Dynamic follow only
        if (!IsOwner || !IsHolding || _holdPoint == null) return;

        Vector3 targetPos = _holdPoint.position;
        Quaternion targetRot = _holdPoint.rotation;

        Vector3 nextPos = Vector3.MoveTowards(_heldBody.position, targetPos, followPositionSpeed * Time.fixedDeltaTime);
        Quaternion nextRot = Quaternion.RotateTowards(_heldBody.rotation, targetRot, followRotationSpeedDeg * Time.fixedDeltaTime);

        _heldBody.MovePosition(nextPos);
        _heldBody.MoveRotation(nextRot);
    }

    // Compute a yaw-only rotation from either camera or handsRoot
    private Quaternion GetYawRotation()
    {
        Vector3 srcFwd;
        if (useCameraYawForHold && cameraTransform != null)
            srcFwd = cameraTransform.forward;
        else
            srcFwd = handsRoot != null ? handsRoot.forward : transform.forward;

        // Project to XZ to strip pitch
        Vector3 yawFwd = Vector3.ProjectOnPlane(srcFwd, Vector3.up);
        if (yawFwd.sqrMagnitude < 1e-6f) yawFwd = new Vector3(srcFwd.x, 0f, srcFwd.z);
        if (yawFwd.sqrMagnitude < 1e-6f) yawFwd = Vector3.forward; // final fallback
        yawFwd.Normalize();
        return Quaternion.LookRotation(yawFwd, Vector3.up);
    }

    // Compute how far forward we can place the held item before an obstacle
    private float GetMaxClearHoldDistance(Quaternion yawRot, float desired)
    {
        if (handsRoot == null) return desired;

        Vector3 anchor = handsRoot.position + yawRot * new Vector3(localHoldOffset.x, localHoldOffset.y, 0f);
        Vector3 dir = yawRot * Vector3.forward;

        float castDist = Mathf.Max(0f, desired);
        if (castDist <= 0f) return desired;

        var hits = Physics.SphereCastAll(anchor, holdCollisionRadius, dir, castDist, obstacleMask, QueryTriggerInteraction.Ignore);
        float minHit = float.PositiveInfinity;
        for (int i = 0; i < hits.Length; ++i)
        {
            var h = hits[i];
            if (!h.collider) continue;
            // Ignore our own held rigidbody (and any colliders attached to it)
            if (_heldBody != null && h.rigidbody == _heldBody) continue;
            if (h.distance < minHit) minHit = h.distance;
        }

        if (float.IsInfinity(minHit)) return desired;
        return Mathf.Max(0f, minHit - holdSkin);
    }

    // Clamp forward movement along hold direction so the held item won't penetrate obstacles
    public float ClampForwardMovement(float desiredForwardDelta)
    {
        if (!IsHolding || desiredForwardDelta <= 0f || handsRoot == null) return desiredForwardDelta;

        Quaternion yawRot = GetYawRotation();
        Vector3 anchor = handsRoot.position + yawRot * new Vector3(localHoldOffset.x, localHoldOffset.y, 0f);
        Vector3 dir = yawRot * Vector3.forward;

        float castDist = Mathf.Max(0f, _holdDistance + desiredForwardDelta + holdSkin);
        if (castDist <= 0f) return desiredForwardDelta;

        var hits = Physics.SphereCastAll(anchor, holdCollisionRadius, dir, castDist, obstacleMask, QueryTriggerInteraction.Ignore);
        float minHit = float.PositiveInfinity;
        for (int i = 0; i < hits.Length; ++i)
        {
            var h = hits[i];
            if (!h.collider) continue;
            if (_heldBody != null && h.rigidbody == _heldBody) continue;
            if (h.distance < minHit) minHit = h.distance;
        }

        if (float.IsInfinity(minHit)) return desiredForwardDelta;

        float maxAllowedAdvance = Mathf.Max(0f, minHit - _holdDistance - holdSkin);
        return Mathf.Min(desiredForwardDelta, maxAllowedAdvance);
    }

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
            Parent = rb.transform.parent,
            UseGravity = rb.useGravity,
            Drag = rb.linearDamping,
            AngularDrag = rb.angularDamping,
            Interp = rb.interpolation,
            CollisionMode = rb.collisionDetectionMode,
            ParentedToHold = false
        };

        if (_holdPoint == null)
        {
            var go = new GameObject("HoldPoint");
            _holdPoint = go.transform;
            _holdPoint.SetParent(transform, false);
        }

        // Always dynamic follow; never make kinematic; no parenting.
        rb.isKinematic = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        if (disableGravityWhileHeld) rb.useGravity = false;

        _heldBody = rb;
    }

    public void DropHeld()
    {
        if (_heldBody == null) return;

        // No unparenting (we never parented). Restore relevant physics state except isKinematic.
        _heldBody.isKinematic = false; // keep it off, as requested
        _heldBody.useGravity = _restore.UseGravity;
        _heldBody.linearDamping = _restore.Drag;
        _heldBody.angularDamping = _restore.AngularDrag;
        _heldBody.interpolation = _restore.Interp;
        _heldBody.collisionDetectionMode = _restore.CollisionMode;

        _heldBody = null;
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

        followPositionSpeed = Mathf.Max(0.01f, followPositionSpeed);
        followRotationSpeedDeg = Mathf.Max(1f, followRotationSpeedDeg);

        holdCollisionRadius = Mathf.Max(0.01f, holdCollisionRadius);
        holdSkin = Mathf.Clamp(holdSkin, 0.001f, 0.1f);
    }
#endif
}