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

        bool grounded = _controller.isGrounded;
        if (grounded && _verticalVel.y < 0f)
            _verticalVel.y = -2f; // keep grounded

        Vector2 move = _moveAction != null ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
        bool hasMove = move.sqrMagnitude > 0.0001f;

        bool wantsSprint = (_sprintAction != null && _sprintAction.IsPressed()) && hasMove && Stamina.Value > 0.01f;
        float speed = wantsSprint ? runSpeed : walkSpeed;

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

        // Rotate character towards movement direction (optional)
        if (rotateToMove && hasMove && horizontal.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(moveDir, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotateLerp * dt);
        }

        // Jump
        if (grounded && _jumpAction != null && _jumpAction.WasPressedThisFrame())
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
    }
#endif
}