using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Manages daily delivery zone selection with expanding radius.
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

        [Header("Zones (Manual Assignment)")]
        [SerializeField] private List<DeliveryZoneDefinition> allDeliveryZones = new List<DeliveryZoneDefinition>();

        [Header("Setup Timing")]
        [Tooltip("If true, setup happens immediately on network spawn. If false, requires manual call (e.g., from start button).")]
        [SerializeField] private bool autoSetupOnSpawn = false;

        [Header("Debug")]
        [SerializeField] private bool showDebugGizmos = true;
        [SerializeField] private bool enableServerLogs = true;
        [SerializeField] private bool enableClientLogs = false;

        // Runtime state
        private List<DeliveryZoneDefinition> _activeZonesThisDay = new List<DeliveryZoneDefinition>();
        private HashSet<int> _usedZoneIndices = new HashSet<int>();
        private int _currentDay = 0;
        private float _currentRadius = 0f;
        private bool _hasBeenSetup = false;

        // NEW: Track previous day's zone(s) to avoid repeating
        private List<int> _previousDayZoneIndices = new List<int>();

        // Network variables for client synchronization
        private readonly NetworkVariable<int> nvCurrentDay = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Network list to replicate active zone indices to clients
        private NetworkList<int> nvActiveZoneIndices;

        // Public events
        public event Action<List<DeliveryZoneDefinition>> OnZonesSelectedForDay;

        // Public accessors
        public IReadOnlyList<DeliveryZoneDefinition> ActiveZones => _activeZonesThisDay;
        public float CurrentRadius => _currentRadius;
        public Vector3 WarehousePosition => warehouseSpawnPoint != null ? warehouseSpawnPoint.position : transform.position;

        private void Awake()
        {
            nvActiveZoneIndices = new NetworkList<int>();

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

            // NEW: Clients need to discover zones locally too!
            if (!IsServer)
            {
                if (enableClientLogs)
                    Debug.Log("[DailyDeliveryZoneManager] Client discovering zones...");

                // Clients discover zones in their local scene
                if (autoDiscoverZones)
                {
                    DiscoverAllZones();
                }

                _currentDay = nvCurrentDay.Value;
                _currentRadius = Mathf.Min(initialRadius + (radiusIncreasePerDay * (_currentDay - 1)), maxRadius);

                // Sync active zones from network
                SyncActiveZonesFromNetwork();

                if (enableClientLogs)
                    Debug.Log($"[DailyDeliveryZoneManager] Client synced: Day {_currentDay}, {_activeZonesThisDay.Count} active zones");
            }

            // Only auto-setup if configured (server only)
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

            base.OnNetworkDespawn();
        }

        /// <summary>
        /// Server: Call this after level is loaded to discover warehouse and zones.
        /// Typically called from WorldStartGameButton or LevelFlowController.
        /// </summary>
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

            // 1. Find warehouse spawn point
            if (autoFindWarehouse && warehouseSpawnPoint == null)
            {
                FindWarehouseSpawnPoint();
            }

            // 2. Discover all delivery zones in the loaded level
            if (autoDiscoverZones)
            {
                DiscoverAllZones();
            }

            // 3. If a day has already started, select zones immediately
            if (moneyTargetManager != null && moneyTargetManager.CurrentDay > 0)
            {
                SelectZonesForDay(moneyTargetManager.CurrentDay);
            }

            if (enableServerLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Setup complete. Warehouse: {warehouseSpawnPoint?.name ?? "null"}, Zones discovered: {allDeliveryZones.Count}");
        }

        /// <summary>
        /// Client: Call this when a client joins mid-game or loads the level late.
        /// Ensures client has discovered zones and synced state.
        /// </summary>
        public void ClientDiscoverAndSync()
        {
            if (IsServer) return;

            if (enableClientLogs)
                Debug.Log("[DailyDeliveryZoneManager] Client manual discover and sync...");

            // Discover zones in client's local scene
            if (autoDiscoverZones)
            {
                DiscoverAllZones();
            }

            // Sync from network state
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
            // Try to find warehouse/spawn point
            var gm = GameManager.Instance;
            if (gm != null && gm.PlayerSpawnPoints != null && gm.PlayerSpawnPoints.Count > 0)
            {
                warehouseSpawnPoint = gm.PlayerSpawnPoints[0];

                if (enableServerLogs)
                    Debug.Log($"[DailyDeliveryZoneManager] Found warehouse from GameManager: {warehouseSpawnPoint.name}");
                return;
            }

            // Fallback: search by tag
            var spawns = GameObject.FindGameObjectsWithTag("PlayerSpawn");
            if (spawns != null && spawns.Length > 0)
            {
                warehouseSpawnPoint = spawns[0].transform;

                if (enableServerLogs)
                    Debug.Log($"[DailyDeliveryZoneManager] Found warehouse by tag: {warehouseSpawnPoint.name}");
                return;
            }

            // Last resort: use this transform as fallback
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

            bool isClient = !IsServer;
            if ((enableServerLogs && IsServer) || (enableClientLogs && isClient))
            {
                string peer = IsServer ? "Server" : "Client";
                Debug.Log($"[DailyDeliveryZoneManager] {peer} discovered {allDeliveryZones.Count} delivery zones.");
            }
        }

        /// <summary>
        /// Server-only: Selects random delivery zones within the current day's radius.
        /// Avoids zones used in the previous day.
        /// </summary>
        private void SelectZonesForDay(int dayIndex)
        {
            if (!IsServer) return;

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

            // NEW: Filter out zones from previous day if enabled
            var candidateZones = new List<DeliveryZoneDefinition>(eligibleZones);

            if (avoidLastDayZone && _previousDayZoneIndices.Count > 0)
            {
                // Remove zones that were active last day
                for (int i = candidateZones.Count - 1; i >= 0; i--)
                {
                    int globalIndex = allDeliveryZones.IndexOf(candidateZones[i]);
                    if (_previousDayZoneIndices.Contains(globalIndex))
                    {
                        candidateZones.RemoveAt(i);
                    }
                }

                // If we filtered out everything, fall back to all eligible zones
                if (candidateZones.Count == 0)
                {
                    if (enableServerLogs)
                        Debug.LogWarning($"[DailyDeliveryZoneManager] All zones were used last day. Using full eligible pool.");
                    candidateZones = new List<DeliveryZoneDefinition>(eligibleZones);
                }
                else if (enableServerLogs)
                {
                    Debug.Log($"[DailyDeliveryZoneManager] Filtered out {eligibleZones.Count - candidateZones.Count} zone(s) from previous day. {candidateZones.Count} candidates remain.");
                }
            }

            // Select random zones
            _activeZonesThisDay.Clear();
            _usedZoneIndices.Clear();
            nvActiveZoneIndices.Clear();

            int targetCount = Mathf.Min(zonesPerDay, candidateZones.Count);
            var newlySelectedIndices = new List<int>();

            if (!allowDuplicateZones)
            {
                // Unique selection
                var indices = new List<int>();
                for (int i = 0; i < candidateZones.Count; i++)
                    indices.Add(i);

                for (int i = 0; i < targetCount; i++)
                {
                    int randIndex = UnityEngine.Random.Range(0, indices.Count);
                    int selectedIndex = indices[randIndex];
                    indices.RemoveAt(randIndex);

                    var zone = candidateZones[selectedIndex];
                    _activeZonesThisDay.Add(zone);

                    // Find global index for network sync
                    int globalIndex = allDeliveryZones.IndexOf(zone);
                    if (globalIndex >= 0)
                    {
                        nvActiveZoneIndices.Add(globalIndex);
                        newlySelectedIndices.Add(globalIndex);
                    }

                    zone.ActivateZone();
                }
            }
            else
            {
                // Allow duplicates
                for (int i = 0; i < targetCount; i++)
                {
                    var zone = candidateZones[UnityEngine.Random.Range(0, candidateZones.Count)];
                    _activeZonesThisDay.Add(zone);

                    int globalIndex = allDeliveryZones.IndexOf(zone);
                    if (globalIndex >= 0)
                    {
                        nvActiveZoneIndices.Add(globalIndex);
                        if (!newlySelectedIndices.Contains(globalIndex))
                            newlySelectedIndices.Add(globalIndex);
                    }

                    zone.ActivateZone();
                }
            }

            // NEW: Store current selection for next day's exclusion
            _previousDayZoneIndices.Clear();
            _previousDayZoneIndices.AddRange(newlySelectedIndices);

            if (enableServerLogs)
            {
                Debug.Log($"[DailyDeliveryZoneManager] Day {dayIndex}: Selected {_activeZonesThisDay.Count} zones within radius {_currentRadius:F1}m");
                foreach (var z in _activeZonesThisDay)
                    Debug.Log($"  - {z.ZoneName} at {z.WorldPosition}");
            }

            OnZonesSelectedForDay?.Invoke(_activeZonesThisDay);
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
                    zone.DeactivateZone();
            }

            _activeZonesThisDay.Clear();
        }

        // Network synchronization callbacks
        private void OnNetworkDayChanged(int previousDay, int newDay)
        {
            _currentDay = newDay;
            _currentRadius = Mathf.Min(initialRadius + (radiusIncreasePerDay * (newDay - 1)), maxRadius);

            if (!IsServer && enableClientLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Client day changed: {previousDay} -> {newDay}, radius: {_currentRadius:F1}m");
        }

        private void OnNetworkActiveZonesChanged(NetworkListEvent<int> changeEvent)
        {
            if (IsServer) return; // Server handles this locally

            if (enableClientLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Client received zone list change, syncing...");

            SyncActiveZonesFromNetwork();
        }

        private void SyncActiveZonesFromNetwork()
        {
            // Ensure zones are discovered before trying to sync
            if (allDeliveryZones.Count == 0 && autoDiscoverZones)
            {
                if (enableClientLogs || enableServerLogs)
                    Debug.Log("[DailyDeliveryZoneManager] No zones discovered yet, discovering now...");
                DiscoverAllZones();
            }

            DeactivateAllZones();
            _activeZonesThisDay.Clear();

            int syncedCount = 0;
            foreach (int zoneIndex in nvActiveZoneIndices)
            {
                if (zoneIndex >= 0 && zoneIndex < allDeliveryZones.Count)
                {
                    var zone = allDeliveryZones[zoneIndex];
                    if (zone != null)
                    {
                        _activeZonesThisDay.Add(zone);
                        zone.ActivateZone();
                        syncedCount++;
                    }
                }
                else if (!IsServer && enableClientLogs)
                {
                    Debug.LogWarning($"[DailyDeliveryZoneManager] Client: Invalid zone index {zoneIndex} (have {allDeliveryZones.Count} zones)");
                }
            }

            if (!IsServer && enableClientLogs)
                Debug.Log($"[DailyDeliveryZoneManager] Client synced {syncedCount} active zones");

            OnZonesSelectedForDay?.Invoke(_activeZonesThisDay);
        }

        /// <summary>
        /// Gets user-friendly string describing active zones for UI display.
        /// </summary>
        public string GetActiveZonesDisplayText()
        {
            if (_activeZonesThisDay.Count == 0)
                return "No active delivery zones";

            if (_activeZonesThisDay.Count == 1)
                return $"Deliver to: {_activeZonesThisDay[0].ZoneName}";

            string result = "Deliver to:\n";
            for (int i = 0; i < _activeZonesThisDay.Count; i++)
            {
                result += $"  • {_activeZonesThisDay[i].ZoneName}\n";
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

            // NEW: Draw previous day's zones in orange
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
        }
#endif

        [ContextMenu("Debug/Select Zones for Current Day")]
        private void Debug_SelectZonesNow()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Only available in Play mode.");
                return;
            }

            if (!IsServer)
            {
                Debug.LogWarning("Only server can select zones.");
                return;
            }

            SelectZonesForDay(_currentDay > 0 ? _currentDay : 1);
        }

        [ContextMenu("Debug/Discover All Zones")]
        private void Debug_DiscoverZones()
        {
            DiscoverAllZones();
            Debug.Log($"Discovered {allDeliveryZones.Count} zones.");
        }

        [ContextMenu("Debug/Server Setup After Level Load")]
        private void Debug_ServerSetup()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Only available in Play mode.");
                return;
            }

            _hasBeenSetup = false; // Reset to allow re-setup
            ServerSetupAfterLevelLoad();
        }

        [ContextMenu("Debug/Client Discover and Sync")]
        private void Debug_ClientSync()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Only available in Play mode.");
                return;
            }

            ClientDiscoverAndSync();
        }
    }
}