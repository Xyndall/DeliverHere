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
    [SerializeField] private float gravity = -20f;
    [SerializeField] private float jumpHeight = 1.4f;
    [SerializeField] private bool rotateToMove = true;
    [SerializeField] private float rotateLerp = 15f;


    [Header("Stamina")]
    [SerializeField] private float maxStamina = 100f;
    [SerializeField] private float sprintCostPerSecond = 22f;
    [SerializeField] private float regenPerSecond = 15f;
    [SerializeField] private float regenDelay = 0.75f;

    [Header("References")]
    [Tooltip("Optional. If null, will use Camera.main.")]
    [SerializeField] private Transform cameraTransform;

    [Header("Arm Reach/Load Penalty")]
    [Tooltip("Optional. If present, movement will be penalized when playerHold are extended or when holding weight.")]
    [SerializeField] private PlayerHold playerHold;
    [Tooltip("At max penalty, movement speed is multiplied by this.")]
    [Range(0.2f, 1f)] [SerializeField] private float speedMultiplierAtMaxPenalty = 0.55f;
    [Tooltip("At max penalty, rotateLerp is multiplied by this (lower = harder to turn).")]
    [Range(0.1f, 1f)] [SerializeField] private float rotateLerpMultiplierAtMaxPenalty = 0.4f;

    [Header("Weight Manager")]
    [Tooltip("If not assigned, will try to GetComponent on the same GameObject.")]
    [SerializeField] private PlayerWeightManager weightManager;

    [Header("Input")]
    [Tooltip("Centralized input controller on this player.")]
    [SerializeField] private PlayerInputController inputController;

    public NetworkVariable<float> Stamina { get; private set; }
        = new NetworkVariable<float>(100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public bool IsSprinting { get; private set; }

    private CharacterController _controller;
    private Vector3 _verticalVel;
    private float _regenCooldown;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        if (playerHold == null) playerHold = GetComponent<PlayerHold>();
        if (weightManager == null) weightManager = GetComponent<PlayerWeightManager>();
        if (inputController == null) inputController = GetComponent<PlayerInputController>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsOwner)
        {
            Stamina.Value = Mathf.Clamp(Stamina.Value, 0f, maxStamina);
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
        }

        enabled = IsOwner;
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        enabled = true;
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        enabled = false;
    }

    private void Update()
    {
        if (!IsOwner) return;
        if (_controller == null) return;
        if (inputController == null) return;

        float dt = Time.deltaTime;

        if (weightManager != null)
        {
            weightManager.SetHeldMass(playerHold != null ? playerHold.HeldMass : (float?)null);
        }

        bool grounded = _controller.isGrounded;
        if (grounded && _verticalVel.y < 0f)
            _verticalVel.y = -2f;

        Vector2 move = inputController.Move;
        bool hasMove = move.sqrMagnitude > 0.0001f;

        bool staminaAllowsSprint = Stamina.Value > 0.01f;
        bool weightAllowsSprint = weightManager == null || weightManager.CanSprint();
        bool wantsSprint = inputController.SprintHeld && hasMove && staminaAllowsSprint && weightAllowsSprint;

        float baseSpeed = wantsSprint ? runSpeed : walkSpeed;
        float speed = baseSpeed;

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
            float penalty01 = playerHold != null ? playerHold.ControlPenalty01 : 0f;
            float speedMult = Mathf.Lerp(1f, speedMultiplierAtMaxPenalty, penalty01);
            float rotateMult = Mathf.Lerp(1f, rotateLerpMultiplierAtMaxPenalty, penalty01);
            speed *= speedMult;
            rotateLerpCurrent = rotateLerp * rotateMult;
        }

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

        if (rotateToMove && hasMove && horizontal.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(horizontal.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotateLerpCurrent * dt);
        }

        bool canJumpByWeight = weightManager == null || weightManager.CanJump();
        if (grounded && canJumpByWeight && inputController.JumpPressedThisFrame)
        {
            _verticalVel.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        _verticalVel.y += gravity * dt;

        Vector3 motion = horizontal;
        motion.y = _verticalVel.y;

        _controller.Move(motion * dt);

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

        IsSprinting = wantsSprint;
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
        gravity = Mathf.Min(-0.1f, gravity);

        speedMultiplierAtMaxPenalty = Mathf.Clamp(speedMultiplierAtMaxPenalty, 0.2f, 1f);
        rotateLerpMultiplierAtMaxPenalty = Mathf.Clamp(rotateLerpMultiplierAtMaxPenalty, 0.1f, 1f);
    }
#endif
}