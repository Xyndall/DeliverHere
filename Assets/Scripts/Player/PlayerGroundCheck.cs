using UnityEngine;

namespace DeliverHere.Player
{
    /// <summary>
    /// Centralized, manual ground check used by PlayerMovement, PlayerPlatformStick,
    /// and any other script that needs to know if the player is standing on something.
    /// Uses a SphereCast instead of relying on CharacterController.isGrounded, which can
    /// be unreliable on certain colliders (mesh seams, steep-but-walkable slopes, etc.).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public class PlayerGroundCheck : MonoBehaviour
    {
        [Header("Ground Check Settings")]
        [Tooltip("Layers considered as ground/platforms.")]
        [SerializeField] private LayerMask groundLayers = ~0;

        [Tooltip("Radius of the sphere cast used to detect ground.")]
        [SerializeField] private float checkRadius = 0.3f;

        [Tooltip("Extra distance below the controller's bottom to check for ground.")]
        [SerializeField] private float checkDistance = 0.2f;

        [Tooltip("Max surface angle (degrees) still considered 'grounded'. Should usually match CharacterController.slopeLimit.")]
        [SerializeField] private float maxGroundAngle = 60f;

        [Header("Debug")]
        [SerializeField] private bool showDebugGizmos = true;

        private CharacterController _controller;

        /// <summary>True if the last check found valid ground within range and angle.</summary>
        public bool IsGrounded { get; private set; }

        /// <summary>Normal of the ground hit (Vector3.up if not grounded).</summary>
        public Vector3 GroundNormal { get; private set; } = Vector3.up;

        /// <summary>Collider of the ground hit, if any.</summary>
        public Collider GroundCollider { get; private set; }

        /// <summary>The full RaycastHit info from the last check (valid only if IsGrounded).</summary>
        public RaycastHit LastHit { get; private set; }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        private void FixedUpdate()
        {
            CheckGround();
        }

        /// <summary>
        /// Forces an immediate re-check (useful if you need up-to-date info mid-frame,
        /// e.g. right before deciding whether a jump input is valid).
        /// </summary>
        public bool CheckGround()
        {
            if (_controller == null)
                _controller = GetComponent<CharacterController>();

            // Origin at the bottom of the capsule, raised by radius so the sphere starts inside the controller.
            Vector3 bottom = transform.position + _controller.center + Vector3.down * (_controller.height * 0.5f - _controller.radius);
            Vector3 origin = bottom + Vector3.up * checkRadius;

            bool hitSomething = Physics.SphereCast(
                origin,
                checkRadius,
                Vector3.down,
                out RaycastHit hit,
                checkDistance + checkRadius,
                groundLayers,
                QueryTriggerInteraction.Ignore
            );

            if (hitSomething)
            {
                float angle = Vector3.Angle(hit.normal, Vector3.up);
                if (angle <= maxGroundAngle)
                {
                    IsGrounded = true;
                    GroundNormal = hit.normal;
                    GroundCollider = hit.collider;
                    LastHit = hit;
                    return true;
                }
            }

            IsGrounded = false;
            GroundNormal = Vector3.up;
            GroundCollider = null;
            return false;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!showDebugGizmos) return;

            if (_controller == null)
                _controller = GetComponent<CharacterController>();
            if (_controller == null) return;

            Vector3 bottom = transform.position + _controller.center + Vector3.down * (_controller.height * 0.5f - _controller.radius);
            Vector3 origin = bottom + Vector3.up * checkRadius;

            Gizmos.color = IsGrounded ? Color.green : Color.red;
            Gizmos.DrawWireSphere(origin + Vector3.down * (checkDistance + checkRadius), checkRadius);
        }
#endif
    }
}