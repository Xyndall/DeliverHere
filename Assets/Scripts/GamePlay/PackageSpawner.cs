using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.AI;
using DeliverHere.Items;

namespace DeliverHere.GamePlay
{
    public enum SpawnMode
    {
        PointsOnly,
        AreasOnly,
        Mixed
    }

    [System.Serializable]
    public class WeightedPackage
    {
        public NetworkObject prefab;
        [Min(0f)] public float weight = 1f;
    }

    [DisallowMultipleComponent]
    public class PackageSpawner : NetworkBehaviour
    {
        [Header("Package Prefabs (must have NetworkObject + PackageProperties)")]
        [SerializeField] private List<WeightedPackage> packageOptions = new List<WeightedPackage>();

        [Header("Count (local, used by SpawnAll)")]
        [SerializeField] private int minPackages = 5;
        [SerializeField] private int maxPackages = 12;

        [Header("Spawn Positions")]
        [SerializeField] private SpawnMode spawnMode = SpawnMode.Mixed;
        [SerializeField] private List<Transform> spawnPoints = new List<Transform>();
        [SerializeField] private List<BoxCollider> spawnAreas = new List<BoxCollider>();

        [Header("Validation")]
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private LayerMask obstacleMask = ~0;
        [SerializeField] private float surfaceOffset = 0.05f;
        [SerializeField] private float overlapRadius = 0.4f;
        [SerializeField] private int maxAttemptsPerPackage = 25;

        [Header("NavMesh (optional)")]
        [SerializeField] private bool useNavMeshValidation = true;
        [SerializeField] private float navMeshSampleMaxDistance = 2f;

        [Header("Raycast Settings")]
        [SerializeField] private float sampleAbove = 2f;
        [SerializeField] private float groundRayLength = 50f;

        [Header("Lifecycle")]
        [SerializeField] private bool autoSpawnOnNetworkSpawn = false;

        [Header("Parenting")]
        [SerializeField] private Transform spawnParent;
        [SerializeField] private bool useNetworkParenting = false;

        private readonly HashSet<int> usedPointIndices = new HashSet<int>();

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (autoSpawnOnNetworkSpawn)
            {
                SpawnAll();
            }
        }

        [ContextMenu("Server Spawn All (Play Mode)")]
        public void SpawnAll()
        {
            if (NetworkManager.Singleton != null && !IsServer)
            {
                return;
            }
            int count = Random.Range(Mathf.Min(minPackages, maxPackages), Mathf.Max(minPackages, maxPackages) + 1);
            SpawnCount(count);
        }

        public int SpawnCount(int desiredCount)
        {
            if (desiredCount <= 0) return 0;
            if (NetworkManager.Singleton != null && !IsServer) return 0;
            if (!HasValidPackageOption())
            {
                Debug.LogError("[PackageSpawner] No valid package options or all weights are zero.");
                return 0;
            }

            usedPointIndices.Clear();

            int spawned = 0;
            int safety = 0;
            int maxSafety = Mathf.Max(1, desiredCount) * Mathf.Max(1, maxAttemptsPerPackage);

            while (spawned < desiredCount && safety < maxSafety)
            {
                safety++;

                if (!TryGetSpawnTransform(out var position, out var rotation))
                    continue;

                var chosen = PickWeightedPackage();
                if (chosen == null || chosen.prefab == null)
                    continue;

                var instance = Instantiate(chosen.prefab, position, rotation);

                if (NetworkManager.Singleton != null)
                {
                    instance.Spawn(true);

                    // Parent after spawn (NGO requirement)
                    if (spawnParent != null)
                    {
                        if (useNetworkParenting)
                        {
                            var parentNO = spawnParent.GetComponent<NetworkObject>();
                            if (parentNO != null && parentNO.IsSpawned)
                            {
                                instance.TrySetParent(parentNO, true);
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
                }
                else
                {
                    if (spawnParent != null)
                    {
                        instance.transform.SetParent(spawnParent, true);
                    }

                    // Offline: force per-instance randomization immediately
                    var propsOffline = instance.GetComponentInChildren<PackageProperties>(true);
                    propsOffline?.ServerRandomize();
                }

                spawned++;
            }

            if (spawned < desiredCount)
            {
                Debug.LogWarning($"[PackageSpawner] Requested {desiredCount}, spawned {spawned}.");
            }

            return spawned;
        }

        private bool TryGetSpawnTransform(out Vector3 position, out Quaternion rotation)
        {
            bool usePoint = spawnMode == SpawnMode.PointsOnly || (spawnMode == SpawnMode.Mixed && Random.value < 0.5f);

            if (usePoint && TryFromPoints(out position, out rotation)) return true;
            if (TryFromAreas(out position, out rotation)) return true;
            if (!usePoint && spawnMode != SpawnMode.AreasOnly) return TryFromPoints(out position, out rotation);

            position = default;
            rotation = default;
            return false;
        }

        private bool TryFromPoints(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;

            if (spawnPoints == null || spawnPoints.Count == 0) return false;

            for (int attempt = 0; attempt < Mathf.Min(maxAttemptsPerPackage, spawnPoints.Count); attempt++)
            {
                int idx = Random.Range(0, spawnPoints.Count);
                if (usedPointIndices.Contains(idx) && usedPointIndices.Count < spawnPoints.Count) continue;

                var t = spawnPoints[idx];
                if (t == null) continue;

                var origin = t.position + Vector3.up * sampleAbove;
                if (TryProjectToGround(origin, out var hitPoint, out var hitNormal))
                {
                    if (IsPositionClear(hitPoint))
                    {
                        usedPointIndices.Add(idx);
                        position = hitPoint + hitNormal * surfaceOffset;
                        rotation = Quaternion.FromToRotation(Vector3.up, hitNormal) * Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                        return true;
                    }
                }
                else
                {
                    if (IsPositionClear(t.position))
                    {
                        usedPointIndices.Add(idx);
                        position = t.position + Vector3.up * surfaceOffset;
                        rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                        return true;
                    }
                }
            }
            return false;
        }

        private bool TryFromAreas(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;

            if (spawnAreas == null || spawnAreas.Count == 0) return false;

            for (int attempt = 0; attempt < maxAttemptsPerPackage; attempt++)
            {
                var area = spawnAreas[Random.Range(0, spawnAreas.Count)];
                if (area == null) continue;

                var bounds = area.bounds;
                var randomXZ = new Vector3(
                    Random.Range(bounds.min.x, bounds.max.x),
                    bounds.max.y + sampleAbove,
                    Random.Range(bounds.min.z, bounds.max.z));

                if (!TryProjectToGround(randomXZ, out var hitPoint, out var hitNormal)) continue;
                if (useNavMeshValidation && !OnNavMesh(hitPoint)) continue;
                if (!IsPositionClear(hitPoint)) continue;

                position = hitPoint + hitNormal * surfaceOffset;
                rotation = Quaternion.FromToRotation(Vector3.up, hitNormal) * Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                return true;
            }
            return false;
        }

        private bool TryProjectToGround(Vector3 rayOrigin, out Vector3 hitPoint, out Vector3 hitNormal)
        {
            hitPoint = default;
            hitNormal = Vector3.up;

            if (Physics.Raycast(rayOrigin, Vector3.down, out var hit, groundRayLength, groundMask, QueryTriggerInteraction.Ignore))
            {
                hitPoint = hit.point;
                hitNormal = hit.normal;
                return true;
            }
            return false;
        }

        private bool OnNavMesh(Vector3 position)
        {
            if (!useNavMeshValidation) return true;
            NavMeshHit navHit;
            return NavMesh.SamplePosition(position, out navHit, navMeshSampleMaxDistance, NavMesh.AllAreas);
        }

        private bool IsPositionClear(Vector3 position)
        {
            var center = position + Vector3.up * (overlapRadius + surfaceOffset + 0.01f);
            return !Physics.CheckSphere(center, overlapRadius, obstacleMask, QueryTriggerInteraction.Ignore);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.25f);
            if (spawnAreas != null)
            {
                foreach (var col in spawnAreas)
                {
                    if (col == null) continue;
                    Gizmos.DrawCube(col.bounds.center, col.bounds.size);
                }
            }

            Gizmos.color = Color.yellow;
            if (spawnPoints != null)
            {
                foreach (var t in spawnPoints)
                {
                    if (t == null) continue;
                    Gizmos.DrawSphere(t.position, 0.15f);
                }
            }
        }
#endif

        // ---------- Public API for daily changes ----------
        public void SetDailySpawnCount(int min, int max)
        {
            minPackages = Mathf.Max(0, Mathf.Min(min, max));
            maxPackages = Mathf.Max(minPackages, max);
        }

        public void ApplyDailyWeights(IList<float> weights)
        {
            if (packageOptions == null || weights == null) return;
            int n = Mathf.Min(packageOptions.Count, weights.Count);
            for (int i = 0; i < n; i++)
            {
                packageOptions[i].weight = Mathf.Max(0f, weights[i]);
            }
        }

        private bool HasValidPackageOption()
        {
            if (packageOptions == null || packageOptions.Count == 0) return false;
            foreach (var p in packageOptions)
            {
                if (p != null && p.prefab != null && p.weight > 0f) return true;
            }
            return false;
        }

        private WeightedPackage PickWeightedPackage()
        {
            float total = 0f;
            WeightedPackage lastValid = null;

            for (int i = 0; i < packageOptions.Count; i++)
            {
                var p = packageOptions[i];
                if (p == null || p.prefab == null || p.weight <= 0f) continue;
                total += p.weight;
                lastValid = p;
            }
            if (total <= 0f)
            {
                for (int i = 0; i < packageOptions.Count; i++)
                {
                    var p = packageOptions[i];
                    if (p != null && p.prefab != null) return p;
                }
                return null;
            }

            float r = Random.value * total;
            float acc = 0f;
            for (int i = 0; i < packageOptions.Count; i++)
            {
                var p = packageOptions[i];
                if (p == null || p.prefab == null || p.weight <= 0f) continue;
                acc += p.weight;
                if (r <= acc) return p;
            }
            return lastValid;
        }
    }
}