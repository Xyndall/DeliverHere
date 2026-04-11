using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;

public class PlayerAnimation : NetworkBehaviour
{
    [Header("Parameters")]
    [SerializeField] private string locomotionParam = "MoveSpeed";   // 0 = idle, >0 = moving
    [SerializeField] private string groundedParam = "IsGrounded";
    [SerializeField] private string jumpTrigger = "Jump";
    [SerializeField] private string sprintBool = "IsSprinting";

    [Header("Smoothing")]
    [SerializeField] private float speedLerp = 10f;
    [Tooltip("Speed below this threshold snaps to zero (prevents negative values).")]
    [SerializeField] private float speedSnapThreshold = 0.01f;

    [Header("Grounded Check")]
    [Tooltip("Additional raycast distance below controller for more reliable grounded detection.")]
    [SerializeField] private float groundCheckDistance = 0.2f;
    [Tooltip("Layer mask for ground detection.")]
    [SerializeField] private LayerMask groundLayer = ~0;

    [Header("References")]
    [Tooltip("Animator that drives the player model (usually on a child).")]
    [SerializeField] private Animator _animator;

    private CharacterController _controller;
    private PlayerMovement _movement;
    private NetworkAnimator _netAnimator;
    private NetworkObject _netObject;

    private float _displaySpeed;
    private bool _wasGrounded = true;
    private bool _initialized;

    private void Awake()
    {
        _controller   = GetComponent<CharacterController>();
        _movement     = GetComponent<PlayerMovement>();
        _netAnimator  = GetComponent<NetworkAnimator>();
        _netObject    = GetComponent<NetworkObject>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _initialized = (_animator != null && _animator.runtimeAnimatorController != null);
        if (!_initialized)
        {
            Debug.LogWarning($"{nameof(PlayerAnimation)}: Animator or Controller missing on {name}.");
        }
    }

    private void Update()
    {
        // Only the owner should drive animation parameters
        if (!IsOwner) return;
        if (!_initialized || _animator == null || _controller == null) return;

        float dt = Time.deltaTime;

        // Enhanced grounded check: combines controller.isGrounded with a raycast
        bool grounded = IsGroundedEnhanced();
        _animator.SetBool(groundedParam, grounded);

        // MoveSpeed: magnitude of horizontal velocity, smoothed and clamped to prevent negatives
        Vector3 vel = _controller.velocity;
        vel.y = 0f;
        float rawSpeed = vel.magnitude;
        
        // Smoothly lerp toward target speed
        _displaySpeed = Mathf.Lerp(_displaySpeed, rawSpeed, speedLerp * dt);
        
        // Snap to zero if below threshold to prevent negative drift and small jitter
        if (_displaySpeed < speedSnapThreshold)
        {
            _displaySpeed = 0f;
        }
        
        // Ensure speed never goes negative
        _displaySpeed = Mathf.Max(0f, _displaySpeed);
        
        _animator.SetFloat(locomotionParam, _displaySpeed);

        // Jump trigger on takeoff
        if (!grounded && _wasGrounded)
        {
            _animator.ResetTrigger(jumpTrigger);
            _animator.SetTrigger(jumpTrigger);

            // NetworkAnimator on the owner will replicate this trigger
            if (_netAnimator != null)
            {
                _netAnimator.SetTrigger(jumpTrigger);
            }
        }

        _wasGrounded = grounded;

        // Sprint / walk state via bool
        bool isSprinting = _movement != null && _movement.IsSprinting;
        _animator.SetBool(sprintBool, isSprinting);
        // NetworkAnimator will automatically replicate parameter changes from this Animator
    }

    private bool IsGroundedEnhanced()
    {
        // Primary check: CharacterController's built-in detection
        if (_controller.isGrounded)
            return true;

        // Secondary check: raycast slightly below the controller
        // This helps catch edge cases where isGrounded might be false but we're still effectively grounded
        Vector3 origin = transform.position + _controller.center;
        float radius = _controller.radius * 0.9f; // Slightly smaller to avoid wall hits
        float distance = (_controller.height * 0.5f) - _controller.radius + groundCheckDistance;

        // Use SphereCast for more forgiving detection
        return Physics.SphereCast(origin, radius, Vector3.down, out _, distance, groundLayer, QueryTriggerInteraction.Ignore);
    }
}