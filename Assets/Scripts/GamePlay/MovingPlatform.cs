using UnityEngine;
using Unity.Netcode;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Simple back-and-forth (or looping) moving platform. Uses a kinematic Rigidbody
    /// so PlayerPlatformStick can read Rigidbody.linearVelocity directly for accurate
    /// velocity compensation and jump inertia.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class MovingPlatform : NetworkBehaviour
    {
        private enum LoopMode
        {
            PingPong,   // Moves A -> B -> A ...
            Loop        // Moves A -> B -> A (teleport) -> B ...
        }

        [Header("Waypoints")]
        [Tooltip("Local-space offsets (relative to starting position) the platform will move between. Needs at least 2 points.")]
        [SerializeField] private Vector3[] waypoints = new Vector3[] { Vector3.zero, new Vector3(0f, 0f, 5f) };

        [Header("Motion")]
        [Tooltip("Speed the platform travels between waypoints, in meters/second.")]
        [SerializeField, Min(0.01f)] private float moveSpeed = 2f;

        [Tooltip("Seconds to pause at each waypoint before continuing.")]
        [SerializeField, Min(0f)] private float waitTimeAtWaypoint = 0f;

        [SerializeField] private LoopMode loopMode = LoopMode.PingPong;

        [Tooltip("Ease in/out near each waypoint instead of moving at constant speed.")]
        [SerializeField] private bool smoothMotion = true;

        [Header("Debug")]
        [SerializeField] private bool showDebugGizmos = true;

        private Rigidbody _rb;
        private Vector3 _startPosition;
        private int _currentIndex;
        private int _direction = 1;
        private float _waitTimer;
        private bool _waiting;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.isKinematic = true;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;

            _startPosition = transform.position;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Only the server drives the platform's motion; movement replicates via NetworkTransform.
            enabled = !NetworkManager.Singleton || IsServer;
        }

        private void FixedUpdate()
        {
            if (waypoints == null || waypoints.Length < 2)
                return;

            if (_waiting)
            {
                _waitTimer -= Time.fixedDeltaTime;
                if (_waitTimer <= 0f)
                    _waiting = false;
                else
                    return;
            }

            Vector3 targetLocal = waypoints[_currentIndex];
            Vector3 targetWorld = _startPosition + targetLocal;

            Vector3 currentPos = _rb.position;
            Vector3 toTarget = targetWorld - currentPos;
            float distance = toTarget.magnitude;

            float step = moveSpeed * Time.fixedDeltaTime;

            if (distance <= step || distance < 0.0001f)
            {
                _rb.MovePosition(targetWorld);
                AdvanceWaypoint();
                return;
            }

            Vector3 direction = toTarget / distance;

            if (smoothMotion)
            {
                // Ease speed down as we approach the waypoint.
                float easedStep = Mathf.Min(step, Mathf.Max(step * 0.25f, distance * 0.5f));
                _rb.MovePosition(currentPos + direction * easedStep);
            }
            else
            {
                _rb.MovePosition(currentPos + direction * step);
            }
        }

        private void AdvanceWaypoint()
        {
            if (waitTimeAtWaypoint > 0f)
            {
                _waiting = true;
                _waitTimer = waitTimeAtWaypoint;
            }

            if (loopMode == LoopMode.PingPong)
            {
                if (_currentIndex + _direction >= waypoints.Length || _currentIndex + _direction < 0)
                {
                    _direction *= -1;
                }
                _currentIndex += _direction;
            }
            else // Loop
            {
                _currentIndex = (_currentIndex + 1) % waypoints.Length;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            moveSpeed = Mathf.Max(0.01f, moveSpeed);
            waitTimeAtWaypoint = Mathf.Max(0f, waitTimeAtWaypoint);
        }

        private void OnDrawGizmosSelected()
        {
            if (!showDebugGizmos || waypoints == null || waypoints.Length == 0)
                return;

            Vector3 basePos = Application.isPlaying ? _startPosition : transform.position;

            Gizmos.color = Color.cyan;
            for (int i = 0; i < waypoints.Length; i++)
            {
                Vector3 point = basePos + waypoints[i];
                Gizmos.DrawWireSphere(point, 0.3f);

                int nextIndex = (i + 1) % waypoints.Length;
                if (nextIndex != 0 || loopMode == LoopMode.Loop)
                {
                    Vector3 nextPoint = basePos + waypoints[nextIndex];
                    Gizmos.DrawLine(point, nextPoint);
                }
            }
        }
#endif
    }
}
