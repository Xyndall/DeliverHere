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

        [Header("Desired Count")]
        [SerializeField, Min(0)] private int desiredPackages = 15;

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

        [Header("Daily Mode (optional)")]
        [SerializeField] private bool useDailyIncreasingCount = true;
        [SerializeField, Min(0)] private int basePackagesFirstDay = 15;      // Day 1 default
        [SerializeField, Min(0)] private int packagesIncrementPerDay = 1;    // +1 per day
        [SerializeField, Min(0)] private int maxDailyPackagesCap = 0;        // 0 = no cap

        private readonly HashSet<int> usedPointIndices = new HashSet<int>();
        private bool dailyOverrideActive;
        private int dailyOverrideCount;

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

            int desired;
            if (dailyOverrideActive)
            {
                desired = dailyOverrideCount;
            }
            else if (useDailyIncreasingCount)
            {
                desired = ComputeDesiredForDay(GetCurrentDayIndexSafe());
            }
            else
            {
                desired = Mathf.Max(0, basePackagesFirstDay);
            }

            int totalSpawned = 0;

            // Pass 1: normal rules
            totalSpawned += SpawnPass(desired - totalSpawned, validateNavMesh: useNavMeshValidation, clearanceRadius: overlapRadius);

            // Pass 2: soft fallback (disable NavMesh, reduce clearance)
            if (totalSpawned < desired)
            {
                totalSpawned += SpawnPass(desired - totalSpawned, validateNavMesh: false, clearanceRadius: Mathf.Max(0.1f, overlapRadius * 0.75f));
            }

            // Pass 3: hard guarantee (ground-only placement)
            if (totalSpawned < desired)
            {
                totalSpawned += HardGuaranteeSpawn(desired - totalSpawned);
            }

            // Pass 4: final force placement (stack on first valid ground hit)
            if (totalSpawned < desired)
            {
                totalSpawned += ForcePlaceRemaining(desired - totalSpawned);
            }

            if (totalSpawned < desired)
            {
                Debug.LogWarning($"[PackageSpawner] Target {desired} not met. Spawned {totalSpawned}.");
            }
        }

        // Public API to set desired exact count
        public void SetDesiredPackages(int count)
        {
            desiredPackages = Mathf.Max(0, count);
        }

        // Public API to set daily count from external systems (DayNightCycle/GameManager)
        public void ApplyDayIndex(int dayIndex)
        {
            int desired = ComputeDesiredForDay(dayIndex);
            // lock exact count for SpawnAll
            dailyOverrideActive = true;
            dailyOverrideCount = Mathf.Max(0, desired);
        }

        // Optional explicit toggle if you want to disable auto daily mode when using ApplyDayIndex
        public void SetUseDailyIncreasingCount(bool enabled)
        {
            useDailyIncreasingCount = enabled;
        }

        private int ComputeDesiredForDay(int dayIndex)
        {
            int dayOneBased = Mathf.Max(1, dayIndex);
            long desired = (long)basePackagesFirstDay + (long)(packagesIncrementPerDay) * (long)(dayOneBased - 1);
            if (maxDailyPackagesCap > 0)
            {
                desired = Mathf.Min((int)desired, maxDailyPackagesCap);
            }
            return Mathf.Max(0, (int)desired);
        }

        private int GetCurrentDayIndexSafe()
        {
            // Hook to your DayNightCycle or GameManager if available.
            return 1;
        }

        // Normal/soft pass using TryGetSpawnTransform and validations
        private int SpawnPass(int need, bool validateNavMesh, float clearanceRadius)
        {
            if (need <= 0) return 0;
            if (NetworkManager.Singleton != null && !IsServer) return 0;
            if (!HasValidPackageOption())
            {
                Debug.LogError("[PackageSpawner] No valid package options or all weights are zero.");
                return 0;
            }

            usedPointIndices.Clear();

            int spawned = 0;
            int safety = 0;
            int maxSafety = Mathf.Max(1, need) * Mathf.Max(1, maxAttemptsPerPackage);

            while (spawned < need && safety < maxSafety)
            {
                safety++;

                if (!TryGetSpawnTransform(out var position, out var rotation))
                    continue;

                // Validate clearance and NavMesh per pass configuration
                if (!IsPositionClearWithRadius(position, clearanceRadius))
                    continue;

                if (validateNavMesh && !OnNavMesh(position))
                    continue;

                var chosen = PickWeightedPackage();
                if (chosen == null || chosen.prefab == null)
                    continue;

                var instance = Instantiate(chosen.prefab, position, rotation);
                SpawnAndParent(instance);

                spawned++;
            }

            return spawned;
        }

        // Hard guarantee: place on any ground hit inside areas/points without clearance or NavMesh check.
        private int HardGuaranteeSpawn(int need)
        {
            if (need <= 0) return 0;
            int placed = 0;

            var origins = new List<Vector3>(spawnPoints.Count + spawnAreas.Count * 4);

            for (int i = 0; i < spawnPoints.Count; i++)
            {
                var t = spawnPoints[i];
                if (t == null) continue;
                origins.Add(t.position + Vector3.up * sampleAbove);
            }

            for (int i = 0; i < spawnAreas.Count; i++)
            {
                var area = spawnAreas[i];
                if (area == null) continue;

                var b = area.bounds;
                int samples = Mathf.Max(4, need * 10);
                for (int s = 0; s < samples; s++)
                {
                    var origin = new Vector3(
                        Random.Range(b.min.x, b.max.x),
                        b.max.y + sampleAbove,
                        Random.Range(b.min.z, b.max.z)
                    );
                    origins.Add(origin);
                }
            }

            if (origins.Count == 0)
            {
                int samples = Mathf.Max(10, need * 10);
                var center = transform.position;
                float radius = 10f;
                for (int s = 0; s < samples; s++)
                {
                    var offset = new Vector2(Random.Range(-radius, radius), Random.Range(-radius, radius));
                    var origin = new Vector3(center.x + offset.x, center.y + sampleAbove, center.z + offset.y);
                    origins.Add(origin);
                }
            }

            // Shuffle some
            for (int i = 0; i < origins.Count; i++)
            {
                int j = Random.Range(i, origins.Count);
                (origins[i], origins[j]) = (origins[j], origins[i]);
            }

            int attempts = 0;
            int maxAttempts = Mathf.Max(need * 50, need * maxAttemptsPerPackage);

            while (placed < need && attempts < maxAttempts)
            {
                attempts++;
                var origin = origins[Random.Range(0, origins.Count)];

                if (!Physics.Raycast(origin, Vector3.down, out var hit, groundRayLength, groundMask, QueryTriggerInteraction.Ignore))
                    continue;

                var pos = hit.point + hit.normal * surfaceOffset;
                var rot = Quaternion.FromToRotation(Vector3.up, hit.normal) * Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

                var chosen = PickWeightedPackage();
                if (chosen == null || chosen.prefab == null) continue;

                var instance = Instantiate(chosen.prefab, pos, rot);
                SpawnAndParent(instance);

                placed++;
            }

            return placed;
        }

        // Absolute last resort: stack remaining packages at the first valid ground hit so the count is exact.
        private int ForcePlaceRemaining(int need)
        {
            if (need <= 0) return 0;

            // Find a single valid ground position near the spawner (or first spawn point/area) and stack upwards
            Vector3? basePos = null;
            Vector3 baseNormal = Vector3.up;

            // Try points
            foreach (var t in spawnPoints)
            {
                if (t == null) continue;
                var origin = t.position + Vector3.up * sampleAbove;
                if (Physics.Raycast(origin, Vector3.down, out var hit, groundRayLength, groundMask, QueryTriggerInteraction.Ignore))
                {
                    basePos = hit.point + hit.normal * surfaceOffset;
                    baseNormal = hit.normal;
                    break;
                }
            }

            // Try areas if needed
            if (!basePos.HasValue)
            {
                foreach (var area in spawnAreas)
                {
                    if (area == null) continue;
                    var b = area.bounds;
                    var origin = new Vector3(
                        (b.min.x + b.max.x) * 0.5f,
                        b.max.y + sampleAbove,
                        (b.min.z + b.max.z) * 0.5f
                    );
                    if (Physics.Raycast(origin, Vector3.down, out var hit, groundRayLength, groundMask, QueryTriggerInteraction.Ignore))
                    {
                        basePos = hit.point + hit.normal * surfaceOffset;
                        baseNormal = hit.normal;
                        break;
                    }
                }
            }

            // Fallback to spawner position
            if (!basePos.HasValue)
            {
                var origin = transform.position + Vector3.up * sampleAbove;
                if (Physics.Raycast(origin, Vector3.down, out var hit, groundRayLength, groundMask, QueryTriggerInteraction.Ignore))
                {
                    basePos = hit.point + hit.normal * surfaceOffset;
                    baseNormal = hit.normal;
                }
            }

            if (!basePos.HasValue)
            {
                Debug.LogError("[PackageSpawner] Force placement failed: no ground found.");
                return 0;
            }

            int placed = 0;
            var rot = Quaternion.FromToRotation(Vector3.up, baseNormal);

            // Stack vertically with small offsets to avoid immediate overlaps
            float verticalStep = Mathf.Max(0.1f, overlapRadius * 1.5f);
            Vector3 pos = basePos.Value;

            while (placed < need)
            {
                var chosen = PickWeightedPackage();
                if (chosen == null || chosen.prefab == null) break;

                var instance = Instantiate(chosen.prefab, pos, rot * Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
                SpawnAndParent(instance);

                placed++;
                pos += Vector3.up * verticalStep;
            }

            Debug.LogWarning($"[PackageSpawner] Forced placement stacked {placed} packages to meet target.");
            return placed;
        }

        private void SpawnAndParent(NetworkObject instance)
        {
            if (NetworkManager.Singleton != null)
            {
                instance.Spawn(true);

                if (spawnParent != null)
                {
                    if (useNetworkParenting)
                    {
                        var parentNO = spawnParent.GetComponent<NetworkObject>();
                        if (parentNO != null && parentNO.IsSpawned)
                            instance.TrySetParent(parentNO, true);
                        else
                            instance.transform.SetParent(spawnParent, true);
                    }
                    else
                    {
                        instance.transform.SetParent(spawnParent, true);
                    }
                }
            }
            else
            {
                if (spawnParent != null) instance.transform.SetParent(spawnParent, true);
                var propsOffline = instance.GetComponentInChildren<PackageProperties>(true);
                propsOffline?.ServerRandomize();
            }
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
                var origin = new Vector3(
                    Random.Range(bounds.min.x, bounds.max.x),
                    bounds.max.y + sampleAbove,
                    Random.Range(bounds.min.z, bounds.max.z));

                if (!TryProjectToGround(origin, out var hitPoint, out var hitNormal)) continue;
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
            return IsPositionClearWithRadius(position, overlapRadius);
        }

        private bool IsPositionClearWithRadius(Vector3 position, float radius)
        {
            var center = position + Vector3.up * (radius + surfaceOffset + 0.01f);
            return !Physics.CheckSphere(center, radius, obstacleMask, QueryTriggerInteraction.Ignore);
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