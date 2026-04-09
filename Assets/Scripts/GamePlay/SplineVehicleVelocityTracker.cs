using UnityEngine;
using Unity.Netcode;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Tracks velocity for objects moved by animation/splines (not physics).
    /// The Rigidbody.velocity will be zero because Transform is being moved directly.
    /// This component manually calculates velocity from position changes.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class SplineVehicleVelocityTracker : NetworkBehaviour
    {
        [Header("Velocity Tracking")]
        [Tooltip("If true, calculate velocity on server only. If false, calculate on all machines.")]
        [SerializeField] private bool serverAuthoritative = true;
        
        [Tooltip("Smoothing frames for velocity calculation (higher = smoother but less responsive).")]
        [SerializeField] private int smoothingFrames = 3;
        
        [Header("Debug")]
        [SerializeField] private bool showDebugLog = false;
        
        private Rigidbody _rb;
        private Vector3 _lastPosition;
        private Vector3[] _velocityHistory;
        private int _historyIndex = 0;
        private Vector3 _currentVelocity;
        
        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _velocityHistory = new Vector3[smoothingFrames];
            _lastPosition = transform.position;
        }
        
        private void FixedUpdate()
        {
            // Only calculate on server if server-authoritative, otherwise calculate everywhere
            if (serverAuthoritative && !IsServer)
                return;
            
            // Calculate velocity from position change
            Vector3 currentPosition = transform.position;
            float deltaTime = Time.fixedDeltaTime;
            
            if (deltaTime > 0.0001f)
            {
                Vector3 velocity = (currentPosition - _lastPosition) / deltaTime;
                
                // Store in history for smoothing
                _velocityHistory[_historyIndex] = velocity;
                _historyIndex = (_historyIndex + 1) % smoothingFrames;
                
                // Calculate average velocity
                Vector3 avgVelocity = Vector3.zero;
                for (int i = 0; i < smoothingFrames; i++)
                {
                    avgVelocity += _velocityHistory[i];
                }
                _currentVelocity = avgVelocity / smoothingFrames;
                
                if (showDebugLog && _currentVelocity.magnitude > 0.1f)
                {
                    Debug.Log($"[SplineVehicleVelocity] {name} velocity: {_currentVelocity.magnitude:F2} m/s (Rigidbody reports: {_rb.linearVelocity.magnitude:F2})");
                }
            }
            
            _lastPosition = currentPosition;
        }
        
        /// <summary>
        /// Gets the tracked velocity (calculated from Transform movement).
        /// </summary>
        public Vector3 GetTrackedVelocity()
        {
            return _currentVelocity;
        }
        
        /// <summary>
        /// Gets the tracked speed in m/s.
        /// </summary>
        public float GetTrackedSpeed()
        {
            return _currentVelocity.magnitude;
        }
    }
}