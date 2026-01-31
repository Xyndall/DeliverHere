using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;
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

        [Header("Spawn Positions")]
        [SerializeField] private SpawnMode spawnMode = SpawnMode.Mixed;

        [Header("Areas Provider")]
        [SerializeField] private bool autoFindSpawnAreasProvider = true;
        [SerializeField] private PackageSpawnAreas spawnAreasProvider;

        [Header("Validation")]
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private LayerMask obstacleMask = ~0;
        [SerializeField] private float surfaceOffset = 0.05f;
        [SerializeField] private float overlapRadius = 0.4f;
        [SerializeField] private int maxAttemptsPerPackage = 25;

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
        [SerializeField, Min(0)] private int basePackagesFirstDay = 15;
        [SerializeField, Min(0)] private int packagesIncrementPerDay = 1;
        [SerializeField, Min(0)] private int maxDailyPackagesCap = 0;

        private readonly HashSet<int> usedPointIndices = new HashSet<int>();
        private bool dailyOverrideActive;
        private int dailyOverrideCount;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            if (autoFindSpawnAreasProvider && spawnAreasProvider == null)
                ResolveAreasProvider();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (autoFindSpawnAreasProvider && spawnAreasProvider == null)
                ResolveAreasProvider();

            if (autoSpawnOnNetworkSpawn)
            {
                SpawnAll();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!autoFindSpawnAreasProvider) return;
            ResolveAreasProvider(preferScene: scene);
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (spawnAreasProvider != null)
            {
                var providerScene = spawnAreasProvider.gameObject.scene;
                if (providerScene == scene)
                {
                    spawnAreasProvider = null;
                }
            }

            if (autoFindSpawnAreasProvider && spawnAreasProvider == null)
            {
                ResolveAreasProvider();
            }
        }

        private void ResolveAreasProvider(Scene? preferScene = null)
        {
            if (spawnAreasProvider != null && spawnAreasProvider.isActiveAndEnabled)
                return;

            PackageSpawnAreas found = null;

            if (preferScene.HasValue)
            {
                var s = preferScene.Value;
                if (s.isLoaded)
                {
                    var rootObjects = s.GetRootGameObjects();
                    foreach (var ro in rootObjects)
                    {
                        if (ro == null || !ro.activeInHierarchy) continue;
                        var candidate = ro.GetComponentInChildren<PackageSpawnAreas>(true);
                        if (candidate != null && candidate.isActiveAndEnabled)
                        {
                            found = candidate;
                            break;
                        }
                    }
                }
            }

            if (found == null)
            {
                var providers = FindObjectsByType<PackageSpawnAreas>(FindObjectsSortMode.None);
                foreach (var p in providers)
                {
                    if (p != null && p.isActiveAndEnabled)
                    {
                        found = p;
                        if (p.gameObject.scene.IsValid() && p.gameObject.scene != gameObject.scene)
                            break;
                    }
                }
            }

            spawnAreasProvider = found;
        }

        [ContextMenu("Server Spawn All (Play Mode)")]
        public void SpawnAll()
        {
            if (NetworkManager.Singleton != null && !IsServer)
            {
                return;
            }

            if (autoFindSpawnAreasProvider && spawnAreasProvider == null)
                ResolveAreasProvider();

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

            // Pass 1: points/areas with normal clearance
            totalSpawned += SpawnPass(desired - totalSpawned, clearanceRadius: overlapRadius);

            // Pass 2: points/areas with relaxed clearance
            if (totalSpawned < desired)
                totalSpawned += SpawnPass(desired - totalSpawned, clearanceRadius: Mathf.Max(0.1f, overlapRadius * 0.75f));
        }

        public void ApplyDayIndex(int dayIndex)
        {
            int desired = ComputeDesiredForDay(dayIndex);
            dailyOverrideActive = true;
            dailyOverrideCount = Mathf.Max(0, desired);
        }

        public void SetUseDailyIncreasingCount(bool enabled) => useDailyIncreasingCount = enabled;

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

        private int GetCurrentDayIndexSafe() => 1;

        private int SpawnPass(int need, float clearanceRadius)
        {
            if (need <= 0) return 0;
            if (NetworkManager.Singleton != null && !IsServer) return 0;
            if (!HasValidPackageOption())
            {
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
                {
                    continue;
                }

                if (!IsPositionClearWithRadius(position, clearanceRadius))
                {
                    continue;
                }

                var chosen = PickWeightedPackage();
                if (chosen == null || chosen.prefab == null)
                {
                    continue;
                }

                var instance = Instantiate(chosen.prefab, position, rotation);
                SpawnAndParent(instance);

                spawned++;
            }

            return spawned;
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

        private IReadOnlyList<Transform> GetPoints()
        {
            if (spawnAreasProvider != null && spawnAreasProvider.Points != null && spawnAreasProvider.Points.Count > 0)
                return spawnAreasProvider.Points;

            return System.Array.Empty<Transform>();
        }

        private IReadOnlyList<BoxCollider> GetAreas()
        {
            if (spawnAreasProvider != null && spawnAreasProvider.Areas != null && spawnAreasProvider.Areas.Count > 0)
                return spawnAreasProvider.Areas;

            return System.Array.Empty<BoxCollider>();
        }

        private bool TryFromPoints(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;

            var points = GetPoints();
            if (points == null || points.Count == 0) return false;

            for (int attempt = 0; attempt < Mathf.Min(maxAttemptsPerPackage, points.Count); attempt++)
            {
                int idx = Random.Range(0, points.Count);
                if (usedPointIndices.Contains(idx) && usedPointIndices.Count < points.Count) continue;

                var t = points[idx];
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

            var areas = GetAreas();
            if (areas == null || areas.Count == 0) return false;

            for (int attempt = 0; attempt < maxAttemptsPerPackage; attempt++)
            {
                var area = areas[Random.Range(0, areas.Count)];
                if (area == null) continue;

                var bounds = area.bounds;
                var origin = new Vector3(
                    Random.Range(bounds.min.x, bounds.max.x),
                    bounds.max.y + sampleAbove,
                    Random.Range(bounds.min.z, bounds.max.z));

                if (!TryProjectToGround(origin, out var hitPoint, out var hitNormal)) continue;
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

        private bool IsPositionClear(Vector3 position) => IsPositionClearWithRadius(position, overlapRadius);

        private bool IsPositionClearWithRadius(Vector3 position, float radius)
        {
            var center = position + Vector3.up * (radius + surfaceOffset + 0.01f);
            return !Physics.CheckSphere(center, radius, obstacleMask, QueryTriggerInteraction.Ignore);
        }

        private (int points, int areas) ProviderCounts()
        {
            int p = 0, a = 0;
            if (spawnAreasProvider != null)
            {
                p = spawnAreasProvider.Points != null ? spawnAreasProvider.Points.Count : 0;
                a = spawnAreasProvider.Areas != null ? spawnAreasProvider.Areas.Count : 0;
            }
            return (p, a);
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