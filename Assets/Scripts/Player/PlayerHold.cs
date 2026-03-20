using UnityEngine;
using Unity.Netcode;
using System;
using System.Collections.Generic;

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

    [Header("Upgradable Stats")]
    [SerializeField] private PlayerUpgradableStats upgradableStats;

    [Header("Pickup")]
    [SerializeField] private LayerMask pickupMask = ~0;
    [SerializeField] private float pickupCastRadius = 0.15f;
    [SerializeField] private float pickupRange = 3f;

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

    [Header("Hold Strength vs Mass")]
    [Tooltip("Mass (kg) where the joint reaches its minimum strength scaling.")]
    [SerializeField] private float jointStrengthMaxMassKg = 30f;
    [Tooltip("At or above jointStrengthMaxMassKg, joint drive strengths are multiplied by this.")]
    [Range(0.05f, 1f)]
    [SerializeField] private float jointStrengthScaleAtMaxMass = 0.25f;
    [Tooltip("Never allow joint strength scaling to go below this (stability).")]
    [Range(0.02f, 1f)]
    [SerializeField] private float minJointStrengthScale = 0.08f;

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

    [Header("Held Stability")]
    [Tooltip("Apply extra drag while held for stability. Disable to allow better throws.")]
    [SerializeField] private bool boostDragWhileHeld = false;
    [Tooltip("Minimum drag while held if boosting is enabled.")]
    [SerializeField] private float heldDrag = 2.5f;
    [Tooltip("Minimum angular drag while held if boosting is enabled.")]
    [SerializeField] private float heldAngularDrag = 3.0f;

    [Header("Drop")]
    [Tooltip("Force gravity ON after drop regardless of original state.")]
    [SerializeField] private bool alwaysEnableGravityOnDrop = true;

    [Header("Throw Tuning")]
    [Tooltip("Fraction of anchor velocity added at release (before mass scaling).")]
    [Range(0f, 2f)]
    [SerializeField] private float releaseAnchorVelScale = 1.0f;
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
    [Range(0f, 1f)]
    [SerializeField] private float forwardBoostMultiplierAtMaxMass = 0.3f;
    [Tooltip("Anchor velocity multiplier at or above massForMinThrowBoost (0-1).")]
    [Range(0f, 1f)]
    [SerializeField] private float anchorVelMultiplierAtMaxMass = 0.5f;

    [Header("Auto-Drop (Distance)")]
    [Tooltip("Automatically drop if object drifts too far from player.")]
    [SerializeField] private bool enableAutoDropOnDistance = true;
    [Tooltip("Distance threshold (meters) from anchor/body before drop logic starts.")]
    [SerializeField] private float maxHoldSeparation = 3.0f;
    [Tooltip("Continuous time (seconds) beyond threshold required to trigger drop.")]
    [SerializeField] private float autoDropDelay = 0.25f;
    [Tooltip("Grace time after pickup before separation checks begin.")]
    [SerializeField] private float postPickupGraceTime = 0.5f;

    [Header("Dynamic Lift Response (Gravity Always On)")]
    [Tooltip("How far down the hold target can sag when extremely heavy (meters).")]
    [SerializeField] private float maxSagMeters = 1.1f;
    [Tooltip("How quickly the hold height responds to weight/strength changes.")]
    [SerializeField] private float sagLerpSpeed = 8f;
    [Tooltip("Extra downward force applied while held when heavy (helps it feel like gravity wins).")]
    [SerializeField] private float extraDownForceAtZeroLift = 120f;

    [Header("Network Anchor Pose (Owner -> Server)")]
    [Tooltip("How often the owner sends the desired anchor pose to the server (Hz). Higher = more responsive, more bandwidth.")]
    [SerializeField] private float anchorPoseSendRateHz = 30f;
    [Tooltip("Minimum distance change before sending another pose (meters).")]
    [SerializeField] private float anchorPoseMinPosDelta = 0.0025f;
    [Tooltip("Minimum rotation change before sending another pose (degrees).")]
    [SerializeField] private float anchorPoseMinRotDeltaDeg = 0.5f;

    [Header("Input")]
    [SerializeField] private PlayerInputController inputController;

    // -------------------- NEW (layer swap only) --------------------
    [Header("Held Item Layer Swap")]
    [Tooltip("Layer name applied to the held object hierarchy while held.")]
    [SerializeField] private string heldItemLayerName = "HeldItem";

    private int _heldItemLayer = -1;
    private readonly Dictionary<Transform, int> _heldOriginalLayers = new Dictionary<Transform, int>(64);
    // ---------------------------------------------------------------

    private readonly NetworkVariable<NetworkObjectReference> _heldRef =
        new NetworkVariable<NetworkObjectReference>(default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

    public bool IsHolding => _heldRef.Value.TryGet(out _) && _heldBody != null;
    public float? HeldMass => _heldBody != null ? _heldBody.mass : null;
    public Rigidbody HeldBody => _heldBody;
    public Vector3 HoldForwardFlat => GetYawRotation() * Vector3.forward;

    public float CurrentSeparation => _heldBody != null && _anchorRb != null
        ? Vector3.Distance(_heldBody.position, _anchorRb.position)
        : 0f;

    [SerializeField] private float massForMaxSlowdown = 10f;
    public float ControlPenalty01
    {
        get
        {
            if (_heldBody == null || massForMaxSlowdown <= 0.01f) return 0f;
            return Mathf.Clamp01(_heldBody.mass / massForMaxSlowdown);
        }
    }

    private float _holdDistance;
    private Rigidbody _heldBody;

    private Rigidbody _anchorRb;
    private ConfigurableJoint _joint;
    private float _anchorRampElapsed;

    private Vector3 _prevAnchorPos;
    private Vector3 _anchorVel;

    private float _pickupTimestamp;
    private float _exceededSeparationTime;

    private float _sag01;

    private struct HeldRestore
    {
        public bool UseGravity;
        public bool IsKinematic;
        public float Drag;
        public float AngularDrag;
        public RigidbodyInterpolation Interp;
        public CollisionDetectionMode CollisionMode;
    }

    private HeldRestore _restore;
    private bool _restoreCaptured;

    private HoldableHoldingState _holdingState;
    private PlayerWeightManager _weightManager;

    public event Action<Rigidbody> PickedUp;
    public event Action<Rigidbody> Dropped;

    // Owner->Server anchor pose state
    private double _nextAnchorPoseSendTime;
    private Vector3 _lastSentAnchorPos;
    private Quaternion _lastSentAnchorRot;

    // Server uses latest received pose
    private Vector3 _serverDesiredAnchorPos;
    private Quaternion _serverDesiredAnchorRot = Quaternion.identity;

    private void Awake()
    {
        if (handsRoot == null) handsRoot = transform;

        _holdDistance = Mathf.Clamp((_holdDistance <= 0f
                ? (minHoldDistance + maxHoldDistance) * 0.5f
                : _holdDistance),
            minHoldDistance, maxHoldDistance);

        _weightManager = GetComponent<PlayerWeightManager>();
        if (inputController == null) inputController = GetComponent<PlayerInputController>();
        if (upgradableStats == null) upgradableStats = GetComponent<PlayerUpgradableStats>();

        // NEW: cache layer index (safe if not created yet; re-checked on use)
        _heldItemLayer = LayerMask.NameToLayer(heldItemLayerName);
    }

    private float HoldThrowMult => upgradableStats != null ? Mathf.Max(0.01f, upgradableStats.HoldAndThrowMultiplier) : 1f;

    private float ResolvedMaxHoldDistance => maxHoldDistance * HoldThrowMult;
    private float ResolvedDistanceChangeSpeed => distanceChangeSpeed * HoldThrowMult;

    private float ResolvedReleaseForwardBoost => releaseForwardBoost * HoldThrowMult;
    private float ResolvedMaxReleaseSpeed => maxReleaseSpeed * HoldThrowMult;
    private float ResolvedReleaseAnchorVelScale => releaseAnchorVelScale * Mathf.Lerp(1f, 1.35f, Mathf.Clamp01(HoldThrowMult - 1f));

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsOwner && cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        enabled = true;

        EnsureAnchorExists();

        _heldRef.OnValueChanged += OnHeldRefChanged;
        OnHeldRefChanged(default, _heldRef.Value);
    }

    public override void OnDestroy()
    {
        try
        {
            _heldRef.OnValueChanged -= OnHeldRefChanged;

            if (IsServer && IsHolding)
                ServerDropHeldInternal();

            if (_anchorRb != null)
                Destroy(_anchorRb.gameObject);
        }
        finally { base.OnDestroy(); }
    }

    private void Update()
    {
        if (!IsOwner) return;
        if (inputController == null) return;

        float axis = inputController.ExtendInput;
        if (Mathf.Abs(axis) > 0.0001f)
        {
            float delta = ResolvedDistanceChangeSpeed * Time.deltaTime;
            _holdDistance = Mathf.Clamp(_holdDistance + axis * delta, minHoldDistance, ResolvedMaxHoldDistance);
        }

        if (!inputController.InteractPressedThisFrame)
            return;

        if (IsHolding)
        {
            RequestDropServerRpc();
            return;
        }

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

    private void FixedUpdate()
    {
        float liftEffect01 = 1f;
        if (_weightManager != null)
        {
            if (IsOwner)
                liftEffect01 = _weightManager.GetLiftEffect01();
            else
                liftEffect01 = _weightManager.LiftEffect01.Value;
        }

        EnsureAnchorExists();

        // Compute desired anchor pose (always)
        Quaternion yawRot = GetYawRotation();

        float targetDist = _holdDistance;
        if (IsHolding && anchorRampTime > 0f && _anchorRampElapsed < anchorRampTime)
        {
            _anchorRampElapsed += Time.fixedDeltaTime;
            float t = Mathf.Clamp01(_anchorRampElapsed / anchorRampTime);
            targetDist = Mathf.Lerp(0f, _holdDistance, t);
        }

        float sagTarget01 = 1f - Mathf.Clamp01(liftEffect01);
        _sag01 = Mathf.MoveTowards(_sag01, sagTarget01, sagLerpSpeed * Time.fixedDeltaTime);

        float sagMeters = _sag01 * maxSagMeters;
        Vector3 local = new Vector3(localHoldOffset.x, localHoldOffset.y - sagMeters, targetDist);

        Vector3 desiredAnchorPos = handsRoot.position + yawRot * local;
        Quaternion desiredAnchorRot = yawRot;

        // Update anchor velocity (used for release)
        _anchorVel = (desiredAnchorPos - _prevAnchorPos) / Mathf.Max(1e-5f, Time.fixedDeltaTime);
        _prevAnchorPos = desiredAnchorPos;

        // Owner: send desired pose to server (throttled)
        if (IsOwner && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !IsServer)
        {
            TrySendAnchorPoseToServer(desiredAnchorPos, desiredAnchorRot);
        }

        // Server: apply latest received desired pose (or if host, use local)
        if (IsServer)
        {
            if (IsOwner)
            {
                // Host: no RPC needed, just use local computed pose.
                _serverDesiredAnchorPos = desiredAnchorPos;
                _serverDesiredAnchorRot = desiredAnchorRot;
            }

            _anchorRb.position = _serverDesiredAnchorPos;
            _anchorRb.rotation = _serverDesiredAnchorRot;
        }
        else
        {
            // Clients: keep local anchor in sync for local computations (doesn't affect server physics).
            _anchorRb.position = desiredAnchorPos;
            _anchorRb.rotation = desiredAnchorRot;
        }

        if (!IsHolding || _heldBody == null)
            return;

        // Server-only: mutate physics / apply forces
        if (!IsServer)
            return;

        if (_joint != null)
            ApplyJointStrengthForMass(_heldBody.mass);

        if (!_heldBody.useGravity)
            _heldBody.useGravity = true;

        if (extraDownForceAtZeroLift > 0f)
        {
            float extra = (1f - Mathf.Clamp01(liftEffect01)) * extraDownForceAtZeroLift;
            if (extra > 0f)
                _heldBody.AddForce(Vector3.down * extra, ForceMode.Force);
        }

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
                        ServerDropHeldInternal();
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
    }

    private void TrySendAnchorPoseToServer(Vector3 pos, Quaternion rot)
    {
        float hz = Mathf.Max(1f, anchorPoseSendRateHz);
        double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalTime.Time : Time.timeAsDouble;

        if (now < _nextAnchorPoseSendTime)
            return;

        float posDelta = Vector3.Distance(pos, _lastSentAnchorPos);
        float rotDelta = Quaternion.Angle(rot, _lastSentAnchorRot);

        if (posDelta < anchorPoseMinPosDelta && rotDelta < anchorPoseMinRotDeltaDeg)
            return;

        _nextAnchorPoseSendTime = now + (1.0 / hz);
        _lastSentAnchorPos = pos;
        _lastSentAnchorRot = rot;

        SubmitAnchorPoseServerRpc(pos, rot);
    }

    [ServerRpc]
    private void SubmitAnchorPoseServerRpc(Vector3 pos, Quaternion rot)
    {
        _serverDesiredAnchorPos = pos;
        _serverDesiredAnchorRot = rot;
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
        _serverDesiredAnchorPos = _anchorRb.position;
        _serverDesiredAnchorRot = _anchorRb.rotation;

        _lastSentAnchorPos = _anchorRb.position;
        _lastSentAnchorRot = _anchorRb.rotation;
    }

    private void OnHeldRefChanged(NetworkObjectReference previous, NetworkObjectReference current)
    {
        EnsureAnchorExists();

        if (!current.TryGet(out NetworkObject netObj))
        {
            ClearHeldLocalState();
            return;
        }

        _heldBody = netObj.GetComponent<Rigidbody>();
        _holdingState = netObj.GetComponent<HoldableHoldingState>();

        _anchorRampElapsed = 0f;
        _prevAnchorPos = _anchorRb.position;
        _sag01 = 0f;

        _nextAnchorPoseSendTime = 0;
        _lastSentAnchorPos = _anchorRb.position;
        _lastSentAnchorRot = _anchorRb.rotation;

        if (_heldBody != null && !_restoreCaptured)
        {
            _restore = new HeldRestore
            {
                UseGravity = _heldBody.useGravity,
                IsKinematic = _heldBody.isKinematic,
                Drag = _heldBody.linearDamping,
                AngularDrag = _heldBody.angularDamping,
                Interp = _heldBody.interpolation,
                CollisionMode = _heldBody.collisionDetectionMode
            };
            _restoreCaptured = true;
        }

        if (_heldBody != null)
        {
            // NEW: change layer to HeldItem while held (no other behavior changes)
            ApplyHeldItemLayer(_heldBody.transform);

            ApplyHeldPhysicsLocal();
        }

        if (IsServer && _heldBody != null)
        {
            CreateOrConfigureJoint();
            _holdingState ??= netObj.gameObject.AddComponent<HoldableHoldingState>();
        }

        if (_heldBody != null)
        {
            if (_holdingState != null)
                _holdingState.HoldersCount.OnValueChanged += OnHoldersCountChanged;

            UpdateHeldMassForPlayer();
            PickedUp?.Invoke(_heldBody);
        }
    }

    private void ApplyHeldPhysicsLocal()
    {
        if (_heldBody == null) return;

        bool isNetworked = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        bool isServer = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        _heldBody.useGravity = true;

        if (boostDragWhileHeld)
        {
            _heldBody.linearDamping = Mathf.Max(_heldBody.linearDamping, heldDrag);
            _heldBody.angularDamping = Mathf.Max(_heldBody.angularDamping, heldAngularDrag);
        }

        _heldBody.maxAngularVelocity = Mathf.Max(_heldBody.maxAngularVelocity, 100f);
        _heldBody.interpolation = RigidbodyInterpolation.Interpolate;
        _heldBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // On non-server clients, keep kinematic to avoid fighting server snapshots
        _heldBody.isKinematic = isNetworked && !isServer ? true : false;
    }

    private void RestoreHeldPhysicsLocalIfCaptured()
    {
        if (_heldBody == null || !_restoreCaptured) return;

        _heldBody.isKinematic = _restore.IsKinematic;
        _heldBody.useGravity = _restore.UseGravity;
        _heldBody.linearDamping = _restore.Drag;
        _heldBody.angularDamping = _restore.AngularDrag;
        _heldBody.interpolation = _restore.Interp;
        _heldBody.collisionDetectionMode = _restore.CollisionMode;
    }

    private void OnHoldersCountChanged(int previous, int current)
    {
        UpdateHeldMassForPlayer();

        if (IsServer && _heldBody != null && _joint != null)
            ApplyJointStrengthForMass(_heldBody.mass);
    }

    private void UpdateHeldMassForPlayer()
    {
        if (_weightManager == null) return;

        if (_heldBody == null)
        {
            _weightManager.SetHeldMass(null);
            return;
        }

        _weightManager.SetHeldMass(Mathf.Max(0f, _heldBody.mass));
    }

    private void ClearHeldLocalState()
    {
        if (_holdingState != null)
            _holdingState.HoldersCount.OnValueChanged -= OnHoldersCountChanged;

        _holdingState = null;

        // NEW: restore original layers on release (no other behavior changes)
        RestoreHeldLayersIfAny();

        RestoreHeldPhysicsLocalIfCaptured();

        _heldBody = null;
        DestroyJoint();

        _restoreCaptured = false;
        _exceededSeparationTime = 0f;
        _sag01 = 0f;

        _weightManager?.SetHeldMass(null);
    }

    // -------------------- NEW (layer swap only) --------------------

    private void ApplyHeldItemLayer(Transform heldRoot)
    {
        if (heldRoot == null) return;

        int layer = _heldItemLayer;
        if (layer < 0)
        {
            layer = LayerMask.NameToLayer(heldItemLayerName);
            _heldItemLayer = layer;
        }

        if (layer < 0)
        {
            Debug.LogWarning($"[{nameof(PlayerHold)}] Layer '{heldItemLayerName}' not found. Create it in Project Settings > Tags and Layers.");
            return;
        }

        _heldOriginalLayers.Clear();

        var stack = new Stack<Transform>();
        stack.Push(heldRoot);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (t == null) continue;

            _heldOriginalLayers[t] = t.gameObject.layer;
            t.gameObject.layer = layer;

            for (int i = 0; i < t.childCount; i++)
                stack.Push(t.GetChild(i));
        }
    }

    private void RestoreHeldLayersIfAny()
    {
        if (_heldOriginalLayers.Count == 0)
            return;

        foreach (var kvp in _heldOriginalLayers)
        {
            if (kvp.Key == null) continue;
            kvp.Key.gameObject.layer = kvp.Value;
        }

        _heldOriginalLayers.Clear();
    }

    // -------------------------------------------------------------

    private void CreateOrConfigureJoint()
    {
        DestroyJoint();
        if (_heldBody == null || _anchorRb == null) return;

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

        cj.rotationDriveMode = RotationDriveMode.Slerp;

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

        ApplyJointStrengthForMass(_heldBody.mass);

        if (boostDragWhileHeld)
        {
            _heldBody.linearDamping = Mathf.Max(_heldBody.linearDamping, heldDrag);
            _heldBody.angularDamping = Mathf.Max(_heldBody.angularDamping, heldAngularDrag);
        }

        _heldBody.maxAngularVelocity = Mathf.Max(_heldBody.maxAngularVelocity, 100f);
        _heldBody.interpolation = RigidbodyInterpolation.Interpolate;
        _heldBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _heldBody.isKinematic = false;

        UpdateHeldMassForPlayer();

        _pickupTimestamp = Time.time;
        _exceededSeparationTime = 0f;
    }

    private void ApplyJointStrengthForMass(float massKg)
    {
        if (_joint == null) return;

        float strengthScale = GetJointStrengthScaleForMass(massKg);

        float armScale = 1f;
        if (_weightManager != null)
        {
            armScale = IsOwner
                ? Mathf.Clamp01(_weightManager.GetArmEffectiveness01())
                : Mathf.Clamp01(_weightManager.ArmEffectiveness01.Value);
        }

        armScale = Mathf.Clamp(armScale, minJointStrengthScale, 1f);

        float combinedScale = strengthScale * armScale;

        float mass = Mathf.Max(0.001f, massKg);
        float linearSpring = (linearSpringBase + linearSpringPerKg * mass) * combinedScale;

        var drive = new JointDrive
        {
            positionSpring = Mathf.Max(0f, linearSpring),
            positionDamper = Mathf.Max(0f, linearDamper * combinedScale),
            maximumForce = (linearMaxForce <= 0f) ? float.PositiveInfinity : linearMaxForce * combinedScale
        };

        _joint.xDrive = drive;
        _joint.yDrive = drive;
        _joint.zDrive = drive;

        var slerp = new JointDrive
        {
            positionSpring = Mathf.Max(0f, angularSpring * combinedScale),
            positionDamper = Mathf.Max(0f, angularDamper * combinedScale),
            maximumForce = (angularMaxForce <= 0f) ? float.PositiveInfinity : angularMaxForce * combinedScale
        };

        _joint.slerpDrive = slerp;
    }

    private float GetJointStrengthScaleForMass(float massKg)
    {
        float mass = Mathf.Max(0f, massKg);
        if (jointStrengthMaxMassKg <= 0.01f) return 1f;

        float t = Mathf.Clamp01(mass / jointStrengthMaxMassKg);
        float scale = Mathf.Lerp(1f, jointStrengthScaleAtMaxMass, t);
        return Mathf.Clamp(scale, minJointStrengthScale, 1f);
    }

    private void DestroyJoint()
    {
        if (_joint == null) return;

        if (IsServer)
            Destroy(_joint);

        _joint = null;
    }

    [ServerRpc]
    private void RequestPickupServerRpc(Vector3 origin, Vector3 dir, float requestedHoldDistance)
    {
        if (_heldRef.Value.TryGet(out _))
            return;

        _holdDistance = Mathf.Clamp(requestedHoldDistance, minHoldDistance, maxHoldDistance);

        if (!Physics.SphereCast(origin, pickupCastRadius, dir, out RaycastHit hit, pickupRange, pickupMask,
                QueryTriggerInteraction.Ignore))
            return;

        var rb = hit.rigidbody;
        if (rb == null) return;

        var netObj = rb.GetComponent<NetworkObject>();
        if (netObj == null) return;

        _heldBody = rb;
        _heldRef.Value = new NetworkObjectReference(netObj);

        _anchorRampElapsed = 0f;
        _restoreCaptured = false;
        _pickupTimestamp = Time.time;
        _exceededSeparationTime = 0f;

        _holdingState = netObj.GetComponent<HoldableHoldingState>();
        if (_holdingState == null) _holdingState = netObj.gameObject.AddComponent<HoldableHoldingState>();
        _holdingState.ServerAddHolder();

        CreateOrConfigureJoint();
    }

    [ServerRpc]
    private void RequestDropServerRpc()
    {
        if (!_heldRef.Value.TryGet(out _) || _heldBody == null)
            return;

        Vector3 releaseVel = _heldBody.linearVelocity;
        Vector3 forward = GetYawRotation() * Vector3.forward;

        float mass = _heldBody.mass;
        float t = scaleThrowByMass && massForMinThrowBoost > 0f
            ? Mathf.Clamp01(mass / massForMinThrowBoost)
            : 0f;

        float anchorScale = Mathf.Lerp(1f, anchorVelMultiplierAtMaxMass, t);
        float forwardScale = Mathf.Lerp(1f, forwardBoostMultiplierAtMaxMass, t);

        // Apply combined upgrade multipliers (simple)
        releaseVel += _anchorVel * ResolvedReleaseAnchorVelScale * anchorScale;
        releaseVel += forward * ResolvedReleaseForwardBoost * forwardScale;

        float clamp = ResolvedMaxReleaseSpeed;
        if (clamp > 0f && releaseVel.sqrMagnitude > clamp * clamp)
            releaseVel = releaseVel.normalized * clamp;

        ServerDropHeldInternal(releaseVel);
    }

    private void ServerDropHeldInternal(Vector3? overrideVelocity = null)
    {
        if (_heldBody == null) return;

        if (_holdingState != null) _holdingState.ServerRemoveHolder();

        DestroyJoint();

        _heldBody.isKinematic = _restore.IsKinematic;
        _heldBody.useGravity = alwaysEnableGravityOnDrop ? true : _restore.UseGravity;
        _heldBody.linearDamping = _restore.Drag;
        _heldBody.angularDamping = _restore.AngularDrag;
        _heldBody.interpolation = _restore.Interp;
        _heldBody.collisionDetectionMode = _restore.CollisionMode;

        if (overrideVelocity.HasValue)
            _heldBody.linearVelocity = overrideVelocity.Value;

        var droppedBody = _heldBody;

        _weightManager?.SetHeldMass(null);

        if (_holdingState != null)
            _holdingState.HoldersCount.OnValueChanged -= OnHoldersCountChanged;

        _holdingState = null;

        _heldBody = null;
        _heldRef.Value = default;
        _restoreCaptured = false;
        _exceededSeparationTime = 0f;
        _sag01 = 0f;

        Dropped?.Invoke(droppedBody);
    }

    public float ClampForwardMovement(float desiredForwardDelta) => desiredForwardDelta;

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

#if UNITY_EDITOR
    private void OnValidate()
    {
        minHoldDistance = Mathf.Max(0.2f, minHoldDistance);
        maxHoldDistance = Mathf.Max(minHoldDistance, maxHoldDistance);
        distanceChangeSpeed = Mathf.Max(0.01f, distanceChangeSpeed);
        pickupCastRadius = Mathf.Max(0.01f, pickupCastRadius);
        pickupRange = Mathf.Max(0.2f, pickupRange);
        massForMaxSlowdown = Mathf.Max(0.01f, massForMaxSlowdown);

        jointStrengthMaxMassKg = Mathf.Max(0.01f, jointStrengthMaxMassKg);
        jointStrengthScaleAtMaxMass = Mathf.Clamp(jointStrengthScaleAtMaxMass, 0.05f, 1f);
        minJointStrengthScale = Mathf.Clamp(minJointStrengthScale, 0.02f, 1f);

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

        maxSagMeters = Mathf.Max(0f, maxSagMeters);
        sagLerpSpeed = Mathf.Max(0.01f, sagLerpSpeed);
        extraDownForceAtZeroLift = Mathf.Max(0f, extraDownForceAtZeroLift);

        anchorPoseSendRateHz = Mathf.Clamp(anchorPoseSendRateHz, 1f, 60f);
        anchorPoseMinPosDelta = Mathf.Max(0f, anchorPoseMinPosDelta);
        anchorPoseMinRotDeltaDeg = Mathf.Max(0f, anchorPoseMinRotDeltaDeg);
    }
#endif
}