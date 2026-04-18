using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Manages daily delivery zone selection with expanding radius and multi-zone quota splitting.
    /// Server-authoritative: selects zones each day and broadcasts to clients.
    /// </summary>
    [DisallowMultipleComponent]
    public class DailyDeliveryZoneManager : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private MoneyTargetManager moneyTargetManager;
        [SerializeField] private Transform warehouseSpawnPoint;
        [SerializeField] private bool autoFindWarehouse = true;

        [Header("Radius Settings")]
        [SerializeField, Min(0f)] private float initialRadius = 50f;
        [SerializeField, Min(0f)] private float radiusIncreasePerDay = 25f;
        [SerializeField, Min(0f)] private float maxRadius = 500f;

        [Header("Zone Selection")]
        [SerializeField, Min(1)] private int zonesPerDay = 1;
        [SerializeField] private bool allowDuplicateZones = false;
        [SerializeField] private bool autoDiscoverZones = true;
        [Tooltip("If true, prevents selecting the same zone as the previous day.")]
        [SerializeField] private bool avoidLastDayZone = true;

        [Header("Multi-Zone Quota Settings")]
        [Tooltip("How to split quota among multiple zones.")]
        [SerializeField] private QuotaSplitMode quotaSplitMode = QuotaSplitMode.EqualSplit;
        [Tooltip("If using WeightedByDistance, zones further from warehouse get higher quotas.")]
        [SerializeField, Range(0.5f, 2f)] private float distanceQuotaMultiplier = 1.2f;

        [Header("Progressive Zone Unlocking")]
        [Tooltip("Enable to gradually unlock more zones as days progress.")]
        [SerializeField] private bool enableProgressiveUnlock = true;
        [Tooltip("Day number to unlock second zone.")]
        [SerializeField, Min(1)] private int unlockSecondZoneDay = 5;
        [Tooltip("Additional zones to unlock every X days after second zone.")]
        [SerializeField, Min(1)] private int additionalZoneEveryXDays = 3;
        [Tooltip("Maximum number of simultaneous zones.")]
        [SerializeField, Min(1)] private int maxSimultaneousZones = 4;

        [Header("Zones (Manual Assignment)")]
        [SerializeField] private List<DeliveryZoneDefinition> allDeliveryZones = new List<DeliveryZoneDefinition>();

        [Header("Setup Timing")]
        [Tooltip("If true, setup happens immediately on network spawn. If false, requires manual call.")]
        [SerializeField] private bool autoSetupOnSpawn = false;

        [Header("Debug")]
        [SerializeField] private bool showDebugGizmos = true;
        [SerializeField] private bool enableServerLogs = true;
        [SerializeField] private bool enableClientLogs = false;

        public enum QuotaSplitMode
        {
            EqualSplit,              // Each zone gets quota / zoneCount
            WeightedByDistance,      // Farther zones get higher quotas
            PrimarySecondary         // First zone gets 70%, others share 30%
        }

        // Runtime state
        private List<DeliveryZoneDefinition> _activeZonesThisDay = new List<DeliveryZoneDefinition>();
        private Dictionary<DeliveryZoneDefinition, int> _zoneQuotas = new Dictionary<DeliveryZoneDefinition, int>();
        private HashSet<int> _usedZoneIndices = new HashSet<int>();
        private int _currentDay = 0;
        private float _currentRadius = 0f;
        private bool _hasBeenSetup = false;

        // Track previous day's zone(s) to avoid repeating
        private List<int> _previousDayZoneIndices = new List<int>();

        // Network variables for client synchronization
        private readonly NetworkVariable<int> nvCurrentDay = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Network list to replicate active zone indices to clients
        private NetworkList<int> nvActiveZoneIndices;
        private NetworkList<int> nvZoneQuotas; // Parallel list to nvActiveZoneIndices

        // Public events
        public event Action<List<DeliveryZoneDefinition>> OnZonesSelectedForDay;

        // Public accessors
        public IReadOnlyList<DeliveryZoneDefinition> ActiveZones => _activeZonesThisDay;
        public float CurrentRadius => _currentRadius;
        public Vector3 WarehousePosition => warehouseSpawnPoint != null ? warehouseSpawnPoint.position : transform.position;

        private void Awake()
        {
            nvActiveZoneIndices = new NetworkList<int>();
            nvZoneQuotas = new NetworkList<int>();

            if (moneyTargetManager == null)
                moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (moneyTargetManager != null)
                moneyTargetManager.OnDayAdvanced += OnDayAdvanced;

            // Subscribe to network variable changes
            nvCurrentDay.OnValueChanged += OnNetworkDayChanged;
            nvActiveZoneIndices.OnListChanged += OnNetworkActiveZonesChanged;
            nvZoneQuotas.OnListChanged += OnNetworkQuotasChanged;

            // Clients need to discover zones locally too
            if (!IsServer)
            {
                if (enableClientLogs)
                    Debug.Log("[DailyDeliveryZoneManager] Client discovering zones...");

                if (autoDiscoverZones)
                {
                    DiscoverAllZones();
                }

                _currentDay = nvCurrentDay.Value;
                _currentRadius = Mathf.Min(initialRadius + (radiusIncreasePerDay * (_currentDay - 1)), maxRadius);

                SyncActiveZonesFromNetwork();

                if (enableClientLogs)
                    Debug.Log($"[DailyDeliveryZoneManager] Client synced: Day {_currentDay}, {_activeZonesThisDay.Count} active zones");
            }

            if (autoSetupOnSpawn && IsServer)
            {
                ServerSetupAfterLevelLoad();
            }
        }

        public override void OnNetworkDespawn()
        {
            if (moneyTargetManager != null)
                moneyTargetManager.OnDayAdvanced -= OnDayAdvanced;

            nvCurrentDay.OnValueChanged -= OnNetworkDayChanged;
            nvActiveZoneIndices.OnListChanged -= OnNetworkActiveZonesChanged;
            nvZoneQuotas.OnListChanged -= OnNetworkQuotasChanged;

            base.OnNetworkDespawn();
        }

        public void ServerSetupAfterLevelLoad()
        {
            if (!IsServer)
            {
                Debug.LogWarning("[DailyDeliveryZoneManager] ServerSetupAfterLevelLoad can only be called on server.");
                return;
            }

            if (_hasBeenSetup)
            {
                if (enableServerLogs)
                    Debug.Log("[DailyDeliveryZoneManager] Already setup, skipping.");
                return;
            }

            _hasBeenSetup = true;

            if (enableServerLogs)
                Debug.Log("[DailyDeliveryZoneManager] Running level load setup...");

            if (autoFindWarehouse && warehouseSpawnPoint == null)
            {
                FindWarehouseSpawnPoint();
            }

            if (autoDiscoverZones)
            {
                DiscoverAllZones();
            }

            if (moneyTargetManager != null && moneyTargetManager.CurrentDay > 0)
            {
                SelectZonesForDay(moneyTargetManager.CurrentDay);
            }

            if (enableServerLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Setup complete. Warehouse: {warehouseSpawnPoint?.name ?? "null"}, Zones discovered: {allDeliveryZones.Count}");
        }

        public void ClientDiscoverAndSync()
        {
            if (IsServer) return;

            if (enableClientLogs)
                Debug.Log("[DailyDeliveryZoneManager] Client manual discover and sync...");

            if (autoDiscoverZones)
            {
                DiscoverAllZones();
            }

            _currentDay = nvCurrentDay.Value;
            _currentRadius = Mathf.Min(initialRadius + (radiusIncreasePerDay * (_currentDay - 1)), maxRadius);
            SyncActiveZonesFromNetwork();

            if (enableClientLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Client sync complete: {_activeZonesThisDay.Count} active zones");
        }

        private void OnDayAdvanced(int newDayIndex)
        {
            if (!IsServer) return;

            _currentDay = newDayIndex;
            nvCurrentDay.Value = newDayIndex;

            SelectZonesForDay(newDayIndex);
        }

        private void FindWarehouseSpawnPoint()
        {
            var gm = GameManager.Instance;
            if (gm != null && gm.PlayerSpawnPoints != null && gm.PlayerSpawnPoints.Count > 0)
            {
                warehouseSpawnPoint = gm.PlayerSpawnPoints[0];

                if (enableServerLogs)
                    Debug.Log($"[DailyDeliveryZoneManager] Found warehouse from GameManager: {warehouseSpawnPoint.name}");
                return;
            }

            var spawns = GameObject.FindGameObjectsWithTag("PlayerSpawn");
            if (spawns != null && spawns.Length > 0)
            {
                warehouseSpawnPoint = spawns[0].transform;

                if (enableServerLogs)
                    Debug.Log($"[DailyDeliveryZoneManager] Found warehouse by tag: {warehouseSpawnPoint.name}");
                return;
            }

            warehouseSpawnPoint = transform;

            if (enableServerLogs)
                Debug.LogWarning("[DailyDeliveryZoneManager] No warehouse spawn found, using manager position as fallback.");
        }

        private void DiscoverAllZones()
        {
            allDeliveryZones.Clear();
            var found = FindObjectsByType<DeliveryZoneDefinition>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var zone in found)
            {
                if (zone != null)
                    allDeliveryZones.Add(zone);
            }

            // Sort deterministically
            allDeliveryZones = allDeliveryZones
                .OrderBy(z => z.gameObject.GetInstanceID())
                .ToList();

            bool isClient = !IsServer;
            if ((enableServerLogs && IsServer) || (enableClientLogs && isClient))
            {
                string peer = IsServer ? "Server" : "Client";
                Debug.Log($"[DailyDeliveryZoneManager] {peer} discovered {allDeliveryZones.Count} delivery zones (sorted by instance ID).");
            }
        }

        private void SelectZonesForDay(int dayIndex)
        {
            if (!IsServer) return;

            // Calculate how many zones to activate based on day
            int targetZoneCount = CalculateTargetZoneCount(dayIndex);

            // Calculate radius for this day
            _currentRadius = Mathf.Min(initialRadius + (radiusIncreasePerDay * (dayIndex - 1)), maxRadius);

            // Deactivate all zones first
            DeactivateAllZones();

            // Get zones within radius
            var eligibleZones = GetZonesWithinRadius(_currentRadius);

            if (eligibleZones.Count == 0)
            {
                Debug.LogWarning($"[DailyDeliveryZoneManager] No zones found within radius {_currentRadius:F1}m for day {dayIndex}.");
                return;
            }

            // Filter out zones from previous day if enabled
            var candidateZones = new List<DeliveryZoneDefinition>(eligibleZones);

            if (avoidLastDayZone && _previousDayZoneIndices.Count > 0)
            {
                for (int i = candidateZones.Count - 1; i >= 0; i--)
                {
                    int globalIndex = allDeliveryZones.IndexOf(candidateZones[i]);
                    if (_previousDayZoneIndices.Contains(globalIndex))
                    {
                        candidateZones.RemoveAt(i);
                    }
                }

                if (candidateZones.Count == 0)
                {
                    if (enableServerLogs)
                        Debug.LogWarning($"[DailyDeliveryZoneManager] All zones were used last day. Using full eligible pool.");
                    candidateZones = new List<DeliveryZoneDefinition>(eligibleZones);
                }
            }

            // Select random zones
            _activeZonesThisDay.Clear();
            _zoneQuotas.Clear();
            nvActiveZoneIndices.Clear();
            nvZoneQuotas.Clear();

            int actualZoneCount = Mathf.Min(targetZoneCount, candidateZones.Count);
            var newlySelectedIndices = new List<int>();
            var selectedZones = new List<DeliveryZoneDefinition>();

            if (!allowDuplicateZones)
            {
                // Unique selection
                var indices = new List<int>();
                for (int i = 0; i < candidateZones.Count; i++)
                    indices.Add(i);

                for (int i = 0; i < actualZoneCount; i++)
                {
                    int randIndex = UnityEngine.Random.Range(0, indices.Count);
                    int selectedIndex = indices[randIndex];
                    indices.RemoveAt(randIndex);

                    var zone = candidateZones[selectedIndex];
                    selectedZones.Add(zone);

                    int globalIndex = allDeliveryZones.IndexOf(zone);
                    if (globalIndex >= 0)
                    {
                        newlySelectedIndices.Add(globalIndex);
                    }
                }
            }
            else
            {
                for (int i = 0; i < actualZoneCount; i++)
                {
                    var zone = candidateZones[UnityEngine.Random.Range(0, candidateZones.Count)];
                    selectedZones.Add(zone);

                    int globalIndex = allDeliveryZones.IndexOf(zone);
                    if (globalIndex >= 0 && !newlySelectedIndices.Contains(globalIndex))
                    {
                        newlySelectedIndices.Add(globalIndex);
                    }
                }
            }

            // Calculate and assign quotas
            CalculateAndAssignQuotas(selectedZones, dayIndex);

            // Activate zones with their quotas
            for (int i = 0; i < selectedZones.Count; i++)
            {
                var zone = selectedZones[i];
                int globalIndex = allDeliveryZones.IndexOf(zone);
                int quota = _zoneQuotas[zone];

                _activeZonesThisDay.Add(zone);
                nvActiveZoneIndices.Add(globalIndex);
                nvZoneQuotas.Add(quota);

                zone.ActivateZone();

                if (zone.DeliveryZone != null)
                {
                    zone.DeliveryZone.SetIndividualQuota(quota);
                }
            }

            // Store current selection for next day's exclusion
            _previousDayZoneIndices.Clear();
            _previousDayZoneIndices.AddRange(newlySelectedIndices);

            // Set primary zone for UI reporting
            SetPrimaryZoneForUI();

            if (enableServerLogs)
            {
                Debug.Log($"[DailyDeliveryZoneManager] Day {dayIndex}: Selected {_activeZonesThisDay.Count} zones within radius {_currentRadius:F1}m");
                foreach (var z in _activeZonesThisDay)
                {
                    int idx = allDeliveryZones.IndexOf(z);
                    int quota = _zoneQuotas[z];
                    bool isPrimary = z.DeliveryZone != null && z.DeliveryZone.IsPrimaryZone;
                    Debug.Log($"  - [{idx}] {z.ZoneName} at {z.WorldPosition}, Quota: ${quota}{(isPrimary ? " [PRIMARY]" : "")}");
                }
            }

            OnZonesSelectedForDay?.Invoke(_activeZonesThisDay);
        }

        private int CalculateTargetZoneCount(int dayIndex)
        {
            if (!enableProgressiveUnlock)
                return zonesPerDay;

            // Day 1-4: 1 zone
            if (dayIndex < unlockSecondZoneDay)
                return 1;

            // Day 5+: progressively unlock more zones
            int daysAfterUnlock = dayIndex - unlockSecondZoneDay;
            int additionalZones = 1 + (daysAfterUnlock / additionalZoneEveryXDays);

            return Mathf.Min(additionalZones, maxSimultaneousZones);
        }

        private void CalculateAndAssignQuotas(List<DeliveryZoneDefinition> zones, int dayIndex)
        {
            if (moneyTargetManager == null || zones.Count == 0)
                return;

            int totalQuota = moneyTargetManager.TargetMoney;

            switch (quotaSplitMode)
            {
                case QuotaSplitMode.EqualSplit:
                    AssignEqualQuotas(zones, totalQuota);
                    break;

                case QuotaSplitMode.WeightedByDistance:
                    AssignWeightedQuotas(zones, totalQuota);
                    break;

                case QuotaSplitMode.PrimarySecondary:
                    AssignPrimarySecondaryQuotas(zones, totalQuota);
                    break;
            }
        }

        private void AssignEqualQuotas(List<DeliveryZoneDefinition> zones, int totalQuota)
        {
            int quotaPerZone = Mathf.CeilToInt(totalQuota / (float)zones.Count);

            foreach (var zone in zones)
            {
                _zoneQuotas[zone] = quotaPerZone;
            }
        }

        private void AssignWeightedQuotas(List<DeliveryZoneDefinition> zones, int totalQuota)
        {
            Vector3 warehousePos = WarehousePosition;

            // Calculate distances and weights
            var distanceWeights = new Dictionary<DeliveryZoneDefinition, float>();
            float totalWeight = 0f;

            foreach (var zone in zones)
            {
                float distance = Vector3.Distance(warehousePos, zone.WorldPosition);
                float weight = Mathf.Pow(distance / 100f, distanceQuotaMultiplier); // Normalize by 100m
                distanceWeights[zone] = weight;
                totalWeight += weight;
            }

            // Assign quotas based on weights
            foreach (var zone in zones)
            {
                float normalizedWeight = distanceWeights[zone] / totalWeight;
                int quota = Mathf.CeilToInt(totalQuota * normalizedWeight);
                _zoneQuotas[zone] = quota;
            }
        }

        private void AssignPrimarySecondaryQuotas(List<DeliveryZoneDefinition> zones, int totalQuota)
        {
            if (zones.Count == 1)
            {
                _zoneQuotas[zones[0]] = totalQuota;
                return;
            }

            // Primary zone gets 70% of quota
            int primaryQuota = Mathf.CeilToInt(totalQuota * 0.7f);
            _zoneQuotas[zones[0]] = primaryQuota;

            // Remaining zones share 30%
            int remainingQuota = totalQuota - primaryQuota;
            int secondaryCount = zones.Count - 1;
            int quotaPerSecondary = Mathf.CeilToInt(remainingQuota / (float)secondaryCount);

            for (int i = 1; i < zones.Count; i++)
            {
                _zoneQuotas[zones[i]] = quotaPerSecondary;
            }
        }

        private List<DeliveryZoneDefinition> GetZonesWithinRadius(float radius)
        {
            var result = new List<DeliveryZoneDefinition>();
            Vector3 warehousePos = WarehousePosition;

            foreach (var zone in allDeliveryZones)
            {
                if (zone == null) continue;

                float distance = Vector3.Distance(warehousePos, zone.WorldPosition);
                if (distance <= radius)
                    result.Add(zone);
            }

            return result;
        }

        private void DeactivateAllZones()
        {
            foreach (var zone in allDeliveryZones)
            {
                if (zone != null)
                {
                    zone.DeactivateZone();

                    if (zone.DeliveryZone != null)
                    {
                        zone.DeliveryZone.ResetForNewDay();
                    }
                }
            }

            _activeZonesThisDay.Clear();
            _zoneQuotas.Clear();
        }

        private void OnNetworkDayChanged(int previousDay, int newDay)
        {
            _currentDay = newDay;
            _currentRadius = Mathf.Min(initialRadius + (radiusIncreasePerDay * (newDay - 1)), maxRadius);

            if (!IsServer && enableClientLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Client day changed: {previousDay} -> {newDay}, radius: {_currentRadius:F1}m");
        }

        private void OnNetworkActiveZonesChanged(NetworkListEvent<int> changeEvent)
        {
            if (IsServer) return;

            if (enableClientLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Client received zone list change, syncing...");

            SyncActiveZonesFromNetwork();
        }

        private void OnNetworkQuotasChanged(NetworkListEvent<int> changeEvent)
        {
            if (IsServer) return;

            if (enableClientLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Client received quota list change, syncing...");

            SyncActiveZonesFromNetwork();
        }

        private void SyncActiveZonesFromNetwork()
        {
            if (allDeliveryZones.Count == 0 && autoDiscoverZones)
            {
                DiscoverAllZones();
            }

            DeactivateAllZones();
            _activeZonesThisDay.Clear();
            _zoneQuotas.Clear();

            int syncedCount = 0;
            for (int i = 0; i < nvActiveZoneIndices.Count; i++)
            {
                int zoneIndex = nvActiveZoneIndices[i];
                int quota = i < nvZoneQuotas.Count ? nvZoneQuotas[i] : 0;

                if (zoneIndex >= 0 && zoneIndex < allDeliveryZones.Count)
                {
                    var zone = allDeliveryZones[zoneIndex];
                    if (zone != null)
                    {
                        _activeZonesThisDay.Add(zone);
                        _zoneQuotas[zone] = quota;
                        zone.ActivateZone();

                        if (zone.DeliveryZone != null)
                        {
                            zone.DeliveryZone.SetIndividualQuota(quota);
                        }

                        syncedCount++;

                        if (enableClientLogs)
                            Debug.Log($"[DailyDeliveryZoneManager] Client activated zone [{zoneIndex}] {zone.ZoneName}, Quota: ${quota}");
                    }
                }
            }

            if (!IsServer && enableClientLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Client synced {syncedCount} active zones");

            OnZonesSelectedForDay?.Invoke(_activeZonesThisDay);
        }

        private void SetPrimaryZoneForUI()
        {
            if (!IsServer) return;

            // Clear primary status from all zones
            foreach (var zoneDef in allDeliveryZones)
            {
                if (zoneDef != null && zoneDef.DeliveryZone != null)
                {
                    zoneDef.DeliveryZone.SetPrimaryZone(false);
                }
            }

            // Set first active zone as primary
            if (_activeZonesThisDay.Count > 0)
            {
                var firstZone = _activeZonesThisDay[0];
                if (firstZone != null && firstZone.DeliveryZone != null)
                {
                    firstZone.DeliveryZone.SetPrimaryZone(true);

                    if (enableServerLogs)
                        Debug.Log($"[DailyDeliveryZoneManager] Set '{firstZone.ZoneName}' as primary UI zone.");
                }
            }
        }

        // ========== PUBLIC API METHODS ==========

        public int GetZoneQuota(DeliveryZoneDefinition zoneDef)
        {
            if (_zoneQuotas.TryGetValue(zoneDef, out int quota))
                return quota;

            return moneyTargetManager != null ? moneyTargetManager.TargetMoney : 0;
        }

        public bool AreAllZoneQuotasMet()
        {
            foreach (var zoneDef in _activeZonesThisDay)
            {
                if (zoneDef.DeliveryZone != null)
                {
                    if (!zoneDef.DeliveryZone.IsQuotaMet)
                        return false;
                }
            }

            return _activeZonesThisDay.Count > 0;
        }

        public Dictionary<string, (int current, int quota, bool met)> GetZoneQuotaSummary()
        {
            var summary = new Dictionary<string, (int, int, bool)>();

            foreach (var zoneDef in _activeZonesThisDay)
            {
                if (zoneDef != null && zoneDef.DeliveryZone != null)
                {
                    int quota = GetZoneQuota(zoneDef);
                    int current = zoneDef.DeliveryZone.TotalValueInZone;
                    bool met = zoneDef.DeliveryZone.IsQuotaMet;
                    summary[zoneDef.ZoneName] = (current, quota, met);
                }
            }

            return summary;
        }

        public string GetActiveZonesDisplayText()
        {
            if (_activeZonesThisDay.Count == 0)
                return "No active delivery zones";

            if (_activeZonesThisDay.Count == 1)
                return $"Deliver to: {_activeZonesThisDay[0].ZoneName}";

            string result = "Deliver to:\n";
            for (int i = 0; i < _activeZonesThisDay.Count; i++)
            {
                var zone = _activeZonesThisDay[i];
                int quota = GetZoneQuota(zone);
                result += $"  • {zone.ZoneName} (${quota})\n";
            }
            return result.TrimEnd();
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!showDebugGizmos) return;

            Vector3 center = warehouseSpawnPoint != null ? warehouseSpawnPoint.position : transform.position;

            // Draw current radius
            Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
            UnityEditor.Handles.color = new Color(0f, 1f, 1f, 0.5f);
            UnityEditor.Handles.DrawWireDisc(center, Vector3.up, _currentRadius > 0 ? _currentRadius : initialRadius);

            // Draw max radius
            Gizmos.color = new Color(1f, 0f, 0f, 0.2f);
            UnityEditor.Handles.color = new Color(1f, 0f, 0f, 0.3f);
            UnityEditor.Handles.DrawWireDisc(center, Vector3.up, maxRadius);

            // Draw warehouse position
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(center, Vector3.one * 5f);

            // Draw previous day's zones in orange
            if (_previousDayZoneIndices != null && _previousDayZoneIndices.Count > 0)
            {
                Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
                foreach (int idx in _previousDayZoneIndices)
                {
                    if (idx >= 0 && idx < allDeliveryZones.Count)
                    {
                        var zone = allDeliveryZones[idx];
                        if (zone != null)
                        {
                            Gizmos.DrawWireSphere(zone.WorldPosition, 3f);
                        }
                    }
                }
            }

            // Draw active zones with quota labels
            if (Application.isPlaying && _activeZonesThisDay != null)
            {
                foreach (var zone in _activeZonesThisDay)
                {
                    if (zone == null) continue;

                    Gizmos.color = Color.green;
                    Gizmos.DrawWireSphere(zone.WorldPosition, 5f);

                    if (_zoneQuotas.TryGetValue(zone, out int quota))
                    {
                        UnityEditor.Handles.Label(zone.WorldPosition + Vector3.up * 7f, $"${quota}");
                    }
                }
            }
        }
#endif

        [ContextMenu("Debug/Select Zones for Current Day")]
        private void Debug_SelectZonesNow()
        {
            if (!Application.isPlaying || !IsServer) return;
            SelectZonesForDay(_currentDay > 0 ? _currentDay : 1);
        }

        [ContextMenu("Debug/Log Zone Quota Summary")]
        private void Debug_LogQuotaSummary()
        {
            if (!Application.isPlaying) return;

            var summary = GetZoneQuotaSummary();
            Debug.Log($"=== Zone Quota Summary (Day {_currentDay}) ===");
            foreach (var kvp in summary)
            {
                string status = kvp.Value.met ? "✓ MET" : "✗ NOT MET";
                Debug.Log($"  {kvp.Key}: ${kvp.Value.current} / ${kvp.Value.quota} {status}");
            }
            Debug.Log($"All Quotas Met: {AreAllZoneQuotasMet()}");
        }
    }
}