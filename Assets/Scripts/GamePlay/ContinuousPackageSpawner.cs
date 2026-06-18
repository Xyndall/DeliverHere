using System.Collections;
using UnityEngine;
using Unity.Netcode;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Continuously spawns packages at a set rate onto a conveyor belt.
    /// Server-authoritative with automatic spawning.
    /// Designed to work with ConveyorBelt system.
    /// </summary>
    [DisallowMultipleComponent]
    public class ContinuousPackageSpawner : NetworkBehaviour
    {
        [Header("Package Prefab")]
        [Tooltip("Package prefab to spawn (must have NetworkObject component).")]
        [SerializeField] private NetworkObject packagePrefab;

        [Header("Spawn Configuration")]
        [Tooltip("Spawn point transform (packages spawn at this location).")]
        [SerializeField] private Transform spawnPoint;

        [Tooltip("Time between spawns in seconds.")]
        [SerializeField, Min(0.01f)] private float spawnInterval = 2f;

        [Tooltip("Random variation in spawn interval (±seconds).")]
        [SerializeField, Min(0f)] private float spawnIntervalVariation = 0.5f;

        [Tooltip("Initial force applied to spawned packages (usually in forward direction).")]
        [SerializeField, Min(0f)] private float spawnForce = 5f;

        [Tooltip("Random variation in spawn force (±units).")]
        [SerializeField, Min(0f)] private float spawnForceVariation = 1f;

        [Tooltip("Start spawning automatically when the game starts.")]
        [SerializeField] private bool autoStart = true;

        [Tooltip("Maximum number of packages to spawn (0 = unlimited).")]
        [SerializeField, Min(0)] private int maxSpawnCount = 0;

        [Header("Parenting")]
        [Tooltip("Parent spawned packages to this transform (optional, for organization).")]
        [SerializeField] private Transform spawnParent;

        [Tooltip("Use network parenting (requires parent to be a NetworkObject).")]
        [SerializeField] private bool useNetworkParenting = false;

        [Header("Spawn Variation")]
        [Tooltip("Random position offset applied to spawn point (local space).")]
        [SerializeField] private Vector3 spawnPositionVariation = Vector3.zero;

        [Tooltip("Random rotation offset applied to spawned packages (degrees).")]
        [SerializeField] private Vector3 spawnRotationVariation = new Vector3(0f, 0f, 0f);

        [Header("Debug")]
        [SerializeField] private bool logSpawns = false;
        [SerializeField] private bool showDebugGizmos = true;

        private Coroutine _spawnCoroutine;
        private int _spawnedCount = 0;
        private bool _isSpawning = false;

        // NetworkVariable to sync spawner state
        private readonly NetworkVariable<bool> _isActiveNetworked = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public bool IsSpawning => _isSpawning;
        public int SpawnedCount => _spawnedCount;

        private void Awake()
        {
            // Default spawn point to self if not assigned
            if (spawnPoint == null)
            {
                spawnPoint = transform;
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (IsServer && autoStart)
            {
                StartSpawning();
            }

            // Subscribe to state changes
            _isActiveNetworked.OnValueChanged += OnSpawningStateChanged;
        }

        public override void OnNetworkDespawn()
        {
            _isActiveNetworked.OnValueChanged -= OnSpawningStateChanged;
            StopSpawning();
            base.OnNetworkDespawn();
        }

        private void OnSpawningStateChanged(bool wasActive, bool isActive)
        {
            // Visual/audio feedback could be added here
            if (logSpawns)
            {
                Debug.Log($"[ContinuousPackageSpawner] Spawning state changed: {isActive}");
            }
        }

        /// <summary>
        /// Starts continuous spawning (Server only).
        /// </summary>
        public void StartSpawning()
        {
            if (!IsServer)
            {
                Debug.LogWarning("[ContinuousPackageSpawner] StartSpawning can only be called on server.");
                return;
            }

            if (_isSpawning)
            {
                Debug.LogWarning("[ContinuousPackageSpawner] Already spawning.");
                return;
            }

            if (packagePrefab == null)
            {
                Debug.LogError("[ContinuousPackageSpawner] Cannot start spawning: packagePrefab is null.");
                return;
            }

            _isSpawning = true;
            _isActiveNetworked.Value = true;
            _spawnedCount = 0;

            _spawnCoroutine = StartCoroutine(SpawnCoroutine());

            if (logSpawns)
            {
                Debug.Log("[ContinuousPackageSpawner] Started continuous spawning.");
            }
        }

        /// <summary>
        /// Stops continuous spawning (Server only).
        /// </summary>
        public void StopSpawning()
        {
            if (!IsServer)
            {
                Debug.LogWarning("[ContinuousPackageSpawner] StopSpawning can only be called on server.");
                return;
            }

            if (!_isSpawning)
                return;

            _isSpawning = false;
            _isActiveNetworked.Value = false;

            if (_spawnCoroutine != null)
            {
                StopCoroutine(_spawnCoroutine);
                _spawnCoroutine = null;
            }

            if (logSpawns)
            {
                Debug.Log($"[ContinuousPackageSpawner] Stopped spawning. Total spawned: {_spawnedCount}");
            }
        }

        /// <summary>
        /// Resets the spawn counter.
        /// </summary>
        public void ResetSpawnCount()
        {
            _spawnedCount = 0;
        }

        private IEnumerator SpawnCoroutine()
        {
            while (_isSpawning)
            {
                // Check if we've reached max spawn count
                if (maxSpawnCount > 0 && _spawnedCount >= maxSpawnCount)
                {
                    if (logSpawns)
                    {
                        Debug.Log($"[ContinuousPackageSpawner] Reached max spawn count ({maxSpawnCount}). Stopping.");
                    }
                    StopSpawning();
                    yield break;
                }

                // Spawn a package
                SpawnPackage();

                // Wait for next spawn
                float interval = spawnInterval + Random.Range(-spawnIntervalVariation, spawnIntervalVariation);
                interval = Mathf.Max(0.01f, interval); // Ensure positive interval

                yield return new WaitForSeconds(interval);
            }
        }

        private void SpawnPackage()
        {
            if (packagePrefab == null || spawnPoint == null)
                return;

            // Calculate spawn position with variation
            Vector3 localOffset = new Vector3(
                Random.Range(-spawnPositionVariation.x, spawnPositionVariation.x),
                Random.Range(-spawnPositionVariation.y, spawnPositionVariation.y),
                Random.Range(-spawnPositionVariation.z, spawnPositionVariation.z)
            );

            Vector3 spawnPosition = spawnPoint.position + spawnPoint.TransformDirection(localOffset);

            // Calculate spawn rotation with variation
            Vector3 rotationOffset = new Vector3(
                Random.Range(-spawnRotationVariation.x, spawnRotationVariation.x),
                Random.Range(-spawnRotationVariation.y, spawnRotationVariation.y),
                Random.Range(-spawnRotationVariation.z, spawnRotationVariation.z)
            );

            Quaternion spawnRotation = spawnPoint.rotation * Quaternion.Euler(rotationOffset);

            // Instantiate package
            NetworkObject instance = Instantiate(packagePrefab, spawnPosition, spawnRotation);

            // Spawn on network
            instance.Spawn(true);

            // Handle parenting
            if (spawnParent != null)
            {
                if (useNetworkParenting)
                {
                    NetworkObject parentNetObj = spawnParent.GetComponent<NetworkObject>();
                    if (parentNetObj != null && parentNetObj.IsSpawned)
                    {
                        instance.TrySetParent(parentNetObj, worldPositionStays: true);
                    }
                    else
                    {
                        instance.transform.SetParent(spawnParent, true);
                    }
                }
                else
                {
                    instance.transform.SetParent(spawnParent, true);
                }
            }

            // Apply spawn force
            Rigidbody rb = instance.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = instance.GetComponentInChildren<Rigidbody>();
            }

            if (rb != null)
            {
                float force = spawnForce + Random.Range(-spawnForceVariation, spawnForceVariation);
                force = Mathf.Max(0f, force);

                Vector3 forceDirection = spawnPoint.forward;
                rb.AddForce(forceDirection * force, ForceMode.Impulse);

                if (logSpawns)
                {
                    Debug.Log($"[ContinuousPackageSpawner] Spawned package #{_spawnedCount + 1} with force {force:F2}N");
                }
            }

            _spawnedCount++;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            spawnInterval = Mathf.Max(0.01f, spawnInterval);
            spawnIntervalVariation = Mathf.Max(0f, spawnIntervalVariation);
            spawnForce = Mathf.Max(0f, spawnForce);
            spawnForceVariation = Mathf.Max(0f, spawnForceVariation);
            maxSpawnCount = Mathf.Max(0, maxSpawnCount);

            if (spawnPoint == null)
            {
                spawnPoint = transform;
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!showDebugGizmos || spawnPoint == null)
                return;

            // Draw spawn point
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(spawnPoint.position, 0.2f);

            // Draw spawn direction
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(spawnPoint.position, spawnPoint.forward * 1f);

            // Draw spawn variation bounds
            if (spawnPositionVariation.sqrMagnitude > 0.001f)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.2f);
                Vector3 size = spawnPositionVariation * 2f;
                Gizmos.matrix = spawnPoint.localToWorldMatrix;
                Gizmos.DrawCube(Vector3.zero, size);
                Gizmos.matrix = Matrix4x4.identity;
            }
        }
#endif

        [ContextMenu("Start Spawning")]
        private void ContextMenu_StartSpawning()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ContinuousPackageSpawner] Can only start spawning in Play mode.");
                return;
            }

            StartSpawning();
        }

        [ContextMenu("Stop Spawning")]
        private void ContextMenu_StopSpawning()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ContinuousPackageSpawner] Can only stop spawning in Play mode.");
                return;
            }

            StopSpawning();
        }
    }
}