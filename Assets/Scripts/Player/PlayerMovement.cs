using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float walkSpeed = 4.5f;
    [SerializeField] private float runSpeed = 7.5f;
    [SerializeField] private float gravity = -20f;          // Stronger gravity for a snappier feel
    [SerializeField] private float jumpHeight = 1.4f;       // Jump apex height in meters
    [SerializeField] private bool rotateToMove = true;
    [SerializeField] private float rotateLerp = 15f;

    [Header("Stamina")]
    [SerializeField] private float maxStamina = 100f;
    [SerializeField] private float sprintCostPerSecond = 22f;
    [SerializeField] private float regenPerSecond = 15f;
    [SerializeField] private float regenDelay = 0.75f;      // Delay after sprint stops before regen

    [Header("References")]
    [Tooltip("Optional. If null, will use Camera.main.")]
    [SerializeField] private Transform cameraTransform;

    // NEW: Arms reference (optional) and penalties
    [Header("Arm Reach/Load Penalty")]
    [Tooltip("Optional. If present, movement will be penalized when playerHold are extended or when holding weight.")]
    [SerializeField] private PlayerHold playerHold;
    [Tooltip("At max penalty, movement speed is multiplied by this.")]
    [Range(0.2f, 1f)] [SerializeField] private float speedMultiplierAtMaxPenalty = 0.55f;
    [Tooltip("At max penalty, rotateLerp is multiplied by this (lower = harder to turn).")]
    [Range(0.1f, 1f)] [SerializeField] private float rotateLerpMultiplierAtMaxPenalty = 0.4f;

    // NEW: Centralized weight manager
    [Header("Weight Manager")]
    [Tooltip("If not assigned, will try to GetComponent on the same GameObject.")]
    [SerializeField] private PlayerWeightManager weightManager;

    public NetworkVariable<float> Stamina { get; private set; }
        = new NetworkVariable<float>(100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // Expose sprinting state so camera effects can be accurate (respects stamina and movement)
    public bool IsSprinting { get; private set; }

    private CharacterController _controller;
    private Vector3 _verticalVel; // only Y is used
    private float _regenCooldown;

    // Generated Input Actions wrapper (replace 'PlayerInputActions' and map/action names if yours differ)
    private InputSystem_Actions _input;
    private InputAction _moveAction;
    private InputAction _sprintAction;
    private InputAction _jumpAction;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        if (playerHold == null) playerHold = GetComponent<PlayerHold>();
        if (weightManager == null) weightManager = GetComponent<PlayerWeightManager>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsOwner)
        {
            InitInputIfNeeded();

            Stamina.Value = Mathf.Clamp(Stamina.Value, 0f, maxStamina);
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
        }

        // Only owner simulates input/movement (use ClientNetworkTransform on the prefab to sync)
        enabled = IsOwner;

        if (enabled) EnableInput();
        else DisableInput();
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        enabled = true;
        InitInputIfNeeded();
        EnableInput();
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
            // Generated wrapper implements IDisposable
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
        if (_controller == null) return;

        float dt = Time.deltaTime;

        // Feed held mass into weight manager (centralized source of truth)
        if (weightManager != null)
        {
            // PlayerHold.HeldMass is nullable; null indicates no held object
            weightManager.SetHeldMass(playerHold != null ? playerHold.HeldMass : null);
        }

        bool grounded = _controller.isGrounded;
        if (grounded && _verticalVel.y < 0f)
            _verticalVel.y = -2f; // keep grounded

        Vector2 move = _moveAction != null ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
        bool hasMove = move.sqrMagnitude > 0.0001f;

        bool staminaAllowsSprint = Stamina.Value > 0.01f;
        bool weightAllowsSprint = weightManager == null || weightManager.CanSprint();
        bool wantsSprintInput = (_sprintAction != null && _sprintAction.IsPressed());
        bool wantsSprint = wantsSprintInput && hasMove && staminaAllowsSprint && weightAllowsSprint;

        float baseSpeed = wantsSprint ? runSpeed : walkSpeed;
        float speed = baseSpeed;

        // APPLY WEIGHT PENALTY (speed and turning) via PlayerWeightManager
        float rotateLerpCurrent = rotateLerp;
        if (weightManager != null)
        {
            float speedMult = weightManager.GetSpeedMultiplier();
            speed *= speedMult;

            float rotateMult = weightManager.GetRotateLerpMultiplier();
            rotateLerpCurrent = rotateLerp * rotateMult;
        }
        else
        {
            // Fallback to existing arms penalty if no weight manager present
            float penalty01 = playerHold != null ? playerHold.ControlPenalty01 : 0f;
            float speedMult = Mathf.Lerp(1f, speedMultiplierAtMaxPenalty, penalty01);
            float rotateMult = Mathf.Lerp(1f, rotateLerpMultiplierAtMaxPenalty, penalty01);
            speed *= speedMult;
            rotateLerpCurrent = rotateLerp * rotateMult;
        }

        // Camera-relative planar movement
        Vector3 forward = transform.forward;
        if (cameraTransform != null)
        {
            forward = cameraTransform.forward;
            forward.y = 0f;
            forward.Normalize();
        }
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        Vector3 moveDir = (forward * move.y + right * move.x).normalized;
        Vector3 horizontal = moveDir * speed;

        // Prevent walking the held item into walls: clamp forward component along the hold direction
        if (playerHold != null && playerHold.IsHolding)
        {
            Vector3 holdFwd = playerHold.HoldForwardFlat;
            Vector3 sideDir = Vector3.Cross(Vector3.up, holdFwd);

            float forwardComp = Vector3.Dot(horizontal, holdFwd);
            float sideComp = Vector3.Dot(horizontal, sideDir);

            float desiredForwardDelta = forwardComp * dt;
            float clampedForwardDelta = playerHold.ClampForwardMovement(desiredForwardDelta);

            float clampedForwardComp = (dt > 0f) ? (clampedForwardDelta / dt) : forwardComp;
            horizontal = holdFwd * clampedForwardComp + sideDir * sideComp;
        }

        // Rotate character towards movement direction (optional)
        if (rotateToMove && hasMove && horizontal.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(horizontal.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotateLerpCurrent * dt);
        }

        // Jump (blocked if overweight)
        bool canJumpByWeight = weightManager == null || weightManager.CanJump();
        if (grounded && canJumpByWeight && _jumpAction != null && _jumpAction.WasPressedThisFrame())
        {
            _verticalVel.y = Mathf.Sqrt(jumpHeight * -2f * gravity); // v = sqrt(2gh)
        }

        // Gravity
        _verticalVel.y += gravity * dt;

        // Compose final motion
        Vector3 motion = horizontal;
        motion.y = _verticalVel.y;

        _controller.Move(motion * dt);

        // Stamina use/regen
        if (wantsSprint)
        {
            Stamina.Value = Mathf.Max(0f, Stamina.Value - sprintCostPerSecond * dt);
            _regenCooldown = regenDelay;
        }
        else
        {
            if (_regenCooldown > 0f) _regenCooldown -= dt;
            else Stamina.Value = Mathf.Min(maxStamina, Stamina.Value + regenPerSecond * dt);
        }

        // Update exposed sprint state after stamina and movement logic
        IsSprinting = wantsSprint;
    }

    private void InitInputIfNeeded()
    {
        if (_input != null) return;

        // Instantiate your generated input wrapper
        _input = new InputSystem_Actions();

        // Cache actions from your map. Commonly the map is named "Player" or "Gameplay".
        // If your map/actions are named differently, change the lines below to match.
        // Example assumes: Map = "Player", Actions = "Move", "Sprint", "Jump".
        _moveAction = _input.Player.Move;
        _sprintAction = _input.Player.Sprint;
        _jumpAction = _input.Player.Jump;
    }

    private void EnableInput()
    {
        _input?.Enable();
        // Actions are enabled with the asset; explicit calls are okay but not required:
        _moveAction?.Enable();
        _sprintAction?.Enable();
        _jumpAction?.Enable();
    }

    private void DisableInput()
    {
        _jumpAction?.Disable();
        _sprintAction?.Disable();
        _moveAction?.Disable();
        _input?.Disable();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        walkSpeed = Mathf.Max(0f, walkSpeed);
        runSpeed = Mathf.Max(walkSpeed, runSpeed);
        maxStamina = Mathf.Max(1f, maxStamina);
        sprintCostPerSecond = Mathf.Max(0f, sprintCostPerSecond);
        regenPerSecond = Mathf.Max(0f, regenPerSecond);
        regenDelay = Mathf.Max(0f, regenDelay);
        jumpHeight = Mathf.Max(0.1f, jumpHeight);
        gravity = Mathf.Min(-0.1f, gravity); // keep negative

        speedMultiplierAtMaxPenalty = Mathf.Clamp(speedMultiplierAtMaxPenalty, 0.2f, 1f);
        rotateLerpMultiplierAtMaxPenalty = Mathf.Clamp(rotateLerpMultiplierAtMaxPenalty, 0.1f, 1f);
    }
#endif
}