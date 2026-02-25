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
        // If not wired, try to auto-find on children
        if (_animator == null)
        {
            _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>(true);
        }

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

        // Grounded
        bool grounded = _controller.isGrounded;
        _animator.SetBool(groundedParam, grounded);

        // MoveSpeed: magnitude of horizontal velocity, smoothed
        Vector3 vel = _controller.velocity;
        vel.y = 0f;
        float rawSpeed = vel.magnitude;
        _displaySpeed = Mathf.Lerp(_displaySpeed, rawSpeed, speedLerp * dt);
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
}