using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using DeliverHere.Items;

namespace DeliverHere.GamePlay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public class PackageDeliveryZone : NetworkBehaviour
    {
        [Header("Money")]
        [Tooltip("If true, deposit directly into the bank. If false, add to today's earnings (recommended).")]
        [SerializeField] private bool depositDirectlyToBank = false;

        [Tooltip("MoneyTargetManager used when GameManager.Instance is not available.")]
        [SerializeField] private MoneyTargetManager moneyTargetManager;

        [Header("Filtering")]
        [Tooltip("Only objects on these layers will be considered. Leave default to accept all.")]
        [SerializeField] private LayerMask packageMask = ~0;

        [Tooltip("Optional required tag on incoming object or its parents.")]
        [SerializeField] private string requiredTag = "";

        [Header("Batch Delivery")]
        [Tooltip("Automatically deliver all packages in zone when the timer expires.")]
        [SerializeField] private bool autoDeliverOnTimerEnd = true;

        [Tooltip("Also clear (consume/despawn) packages after batch delivery.")]
        [SerializeField] private bool consumePackageOnDeposit = true;

        [Tooltip("Ignore repeated trigger hits from the same object (safety window).")]
        [SerializeField] private float reEntryIgnoreSeconds = 0.25f;

        private Collider _zoneCollider;

        // Tracking packages currently inside the zone (pending delivery)
        private readonly HashSet<PackageProperties> _pendingPackages = new HashSet<PackageProperties>();

        // Guards against double crediting across the batch call.
        private readonly HashSet<ulong> _processedNetworkIds = new HashSet<ulong>();
        private readonly Dictionary<int, float> _processedInstanceIdUntil = new Dictionary<int, float>();

        // For ensuring we only auto-batch once per day.
        private int _lastDeliveredDay = -1;

        // Timer reference (replaces DayNightCycle)
        private GameTimer _timer;

        // NEW: Track total value of packages in zone
        private readonly NetworkVariable<int> _totalValueInZone = 
            new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // NEW: Track this zone's individual quota (set by DailyDeliveryZoneManager)
        private readonly NetworkVariable<int> _individualQuota = 
            new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // NEW: Track if this zone met its quota
        private readonly NetworkVariable<bool> _quotaMet = 
            new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // NEW: Track if this zone should report to GameManager UI
        private bool _isPrimaryZone = false;

        public int TotalValueInZone => _totalValueInZone.Value;
        public int IndividualQuota => _individualQuota.Value;
        public bool IsQuotaMet => _quotaMet.Value;
        public bool IsPrimaryZone => _isPrimaryZone;

        private void Awake()
        {
            _zoneCollider = GetComponent<Collider>();
            if (_zoneCollider != null && !_zoneCollider.isTrigger)
            {
                Debug.LogWarning("[PackageDeliveryZone] Collider wasn't trigger. Enabling trigger.");
                _zoneCollider.isTrigger = true;
            }

            if (moneyTargetManager == null && GameManager.Instance != null)
            {
                moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Subscribe to value changes on ALL clients (not just server)
            _totalValueInZone.OnValueChanged += OnTotalValueChanged;
            _individualQuota.OnValueChanged += OnQuotaChanged;
            _quotaMet.OnValueChanged += OnQuotaMetChanged;
            
            // Push initial value to UI immediately on spawn if this is primary
            UpdateGameManagerUI(_totalValueInZone.Value);
        }

        public override void OnNetworkDespawn()
        {
            _totalValueInZone.OnValueChanged -= OnTotalValueChanged;
            _individualQuota.OnValueChanged -= OnQuotaChanged;
            _quotaMet.OnValueChanged -= OnQuotaMetChanged;
            base.OnNetworkDespawn();
        }

        private void OnTotalValueChanged(int previous, int current)
        {
            // This callback fires on ALL clients when the value changes
            // Update the UI on every client if this is the primary zone
            UpdateGameManagerUI(current);
        }

        private void OnQuotaChanged(int previous, int current)
        {
            // Update UI when quota changes
            UpdateGameManagerUI(_totalValueInZone.Value);
        }

        private void OnQuotaMetChanged(bool previous, bool current)
        {
            // Optionally trigger visual/audio feedback when quota is met
            if (current && !previous)
            {
                Debug.Log($"[PackageDeliveryZone] Quota met! ${_totalValueInZone.Value} / ${_individualQuota.Value}");
            }
        }

        private void UpdateGameManagerUI(int totalValue)
        {
            // Only update UI if this is the primary zone
            if (!_isPrimaryZone) return;

            // This runs on all clients, not just server
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnDeliveryZoneValueChanged(totalValue, _individualQuota.Value);
            }
        }

        /// <summary>
        /// Called by DailyDeliveryZoneManager to set whether this zone should report to GameManager UI.
        /// </summary>
        public void SetPrimaryZone(bool isPrimary)
        {
            bool wasChanged = _isPrimaryZone != isPrimary; // FIXED: typo
            _isPrimaryZone = isPrimary;

            // If we just became primary, push current value immediately
            if (wasChanged && _isPrimaryZone)
            {
                UpdateGameManagerUI(_totalValueInZone.Value);
            }
            // If we just lost primary status, reset UI to 0
            else if (wasChanged && !_isPrimaryZone)
            {
                if (GameManager.Instance != null)
                {
                    GameManager.Instance.OnDeliveryZoneValueChanged(0, _individualQuota.Value); // FIXED: added quota parameter
                }
            }
        }

        private void OnEnable()
        {
            FindTimerAndSubscribe();
            
            // Push current value to UI when zone becomes active (only if primary)
            if (_isPrimaryZone && GameManager.Instance != null)
            {
                GameManager.Instance.OnDeliveryZoneValueChanged(_totalValueInZone.Value, _individualQuota.Value); // FIXED: added quota parameter
            }
        }

        private void OnDisable()
        {
            UnsubscribeTimer();
            
            // Reset UI to 0 when this zone is disabled (only if primary)
            if (_isPrimaryZone && GameManager.Instance != null)
            {
                GameManager.Instance.OnDeliveryZoneValueChanged(0, _individualQuota.Value); // FIXED: added quota parameter
            }
            
            // Clear pending packages when zone is disabled
            _pendingPackages.Clear();
            
            // Reset the network value if we're the server
            if (IsProcessingAuthority() && IsSpawned)
            {
                _totalValueInZone.Value = 0;
            }
        }

        private void FindTimerAndSubscribe()
        {
            if (!autoDeliverOnTimerEnd) return;
            if (_timer == null)
                _timer = FindFirstObjectByType<GameTimer>();

            if (_timer != null)
                _timer.OnDayTimerAboutToExpire += HandleTimerAboutToExpire;
        }

        private void UnsubscribeTimer()
        {
            if (_timer != null)
                _timer.OnDayTimerAboutToExpire -= HandleTimerAboutToExpire;
        }

        private void HandleTimerAboutToExpire()
        {
            if (!autoDeliverOnTimerEnd) return;

            int currentDay = moneyTargetManager != null ? moneyTargetManager.CurrentDay :
                             (GameManager.Instance != null ? GameManager.Instance.GetCurrentDay() : -1);

            if (currentDay >= 0 && _lastDeliveredDay == currentDay)
                return; // Already delivered this day.

            // 1) Deliver anything currently in the zone.
            DeliverAllPending();

            // 2) Regardless of delivery status, delete all remaining packages in the scene.
            ClearAllScenePackages();

            _lastDeliveredDay = currentDay;
        }

        private void ClearAllScenePackages()
        {
            if (!IsProcessingAuthority()) return;

            var allPackages = FindObjectsByType<PackageProperties>(FindObjectsSortMode.None);
            int cleared = 0;

            for (int i = 0; i < allPackages.Length; i++)
            {
                var p = allPackages[i];
                if (p == null) continue;

                // Skip if already processed and despawned/destroyed
                if (IsAlreadyProcessed(p)) continue;

                // Despawn/destroy the GameObject
                DespawnOrDestroy(p.gameObject);
                cleared++;
            }

            if (cleared > 0)
            {
                Debug.Log($"[PackageDeliveryZone] Cleared {cleared} undelivered package(s) at timer end.");
            }

            // Also clear local tracking sets to avoid stale references
            _pendingPackages.RemoveWhere(p => p == null || IsAlreadyProcessed(p));
            _processedNetworkIds.Clear();
            _processedInstanceIdUntil.Clear();

            // Reset zone value
            if (IsProcessingAuthority())
            {
                _totalValueInZone.Value = 0;
            }
        }

        // Trigger accumulation only (no immediate delivery)
        private void OnTriggerEnter(Collider other)
        {
            TryRegister(other);
        }

        private void OnTriggerStay(Collider other)
        {
            // Keep trying in case object entered before server authority, etc.
            TryRegister(other);
        }

        private void OnTriggerExit(Collider other)
        {
            TryUnregister(other);
        }

        private void TryRegister(Collider other)
        {
            if (!IsProcessingAuthority()) return;
            if (other == null) return;
            if (!IsLayerAllowed(other.gameObject.layer)) return;
            if (!IsTagAllowed(other.transform)) return;

            var props = other.GetComponentInParent<PackageProperties>();
            if (props == null) return;

            // If already processed (delivered) ignore.
            if (IsAlreadyProcessed(props)) return;

            // Add to pending if not already there
            if (_pendingPackages.Add(props))
            {
                // Recalculate total value
                RecalculateZoneValue();
            }
        }

        public void ForceDeliverNow()
        {
            DeliverAllPending();
        }

        private void TryUnregister(Collider other)
        {
            if (other == null) return;
            var props = other.GetComponentInParent<PackageProperties>();
            if (props == null) return;
            
            if (_pendingPackages.Remove(props))
            {
                // Recalculate total value
                if (IsProcessingAuthority())
                {
                    RecalculateZoneValue();
                }
            }
        }

        private void RecalculateZoneValue()
        {
            if (!IsProcessingAuthority()) return;

            int totalValue = 0;
            var toRemove = new List<PackageProperties>();

            foreach (var p in _pendingPackages)
            {
                if (p == null || IsAlreadyProcessed(p))
                {
                    toRemove.Add(p);
                    continue;
                }

                totalValue += Mathf.Max(0, p.GetDeliveryReward());
            }

            // Clean up nulls
            foreach (var p in toRemove)
            {
                _pendingPackages.Remove(p);
            }

            _totalValueInZone.Value = totalValue;

            // Check if quota is met
            if (_individualQuota.Value > 0)
            {
                bool newQuotaMet = totalValue >= _individualQuota.Value;
                if (_quotaMet.Value != newQuotaMet)
                {
                    _quotaMet.Value = newQuotaMet;
                }
            }
        }

        /// <summary>
        /// Called by DailyDeliveryZoneManager to set this zone's individual quota.
        /// </summary>
        public void SetIndividualQuota(int quota)
        {
            if (!IsProcessingAuthority()) return;

            _individualQuota.Value = Mathf.Max(0, quota);
            _quotaMet.Value = false; // Reset quota met status

            Debug.Log($"[PackageDeliveryZone] Quota set to ${quota}");
        }

        /// <summary>
        /// Resets the zone for a new day (called by DailyDeliveryZoneManager).
        /// </summary>
        public void ResetForNewDay()
        {
            if (!IsProcessingAuthority()) return;

            _totalValueInZone.Value = 0;
            _quotaMet.Value = false;
            _pendingPackages.Clear();
            _processedNetworkIds.Clear();
            _processedInstanceIdUntil.Clear();
            _lastDeliveredDay = -1;

            if (_isPrimaryZone && GameManager.Instance != null)
            {
                GameManager.Instance.OnDeliveryZoneValueChanged(0, _individualQuota.Value); // This one is already correct
            }
        }

        private void DeliverAllPending()
        {
            if (!IsProcessingAuthority()) return;

            // Clean up any nulls (destroyed objects)
            var toDeliver = new List<PackageProperties>();
            foreach (var p in _pendingPackages)
            {
                if (p != null) toDeliver.Add(p);
            }

            int totalReward = 0;
            int deliveredCount = 0;
            for (int i = 0; i < toDeliver.Count; i++)
            {
                var props = toDeliver[i];
                if (props == null) continue;
                if (IsAlreadyProcessed(props)) continue;

                int reward = Mathf.Max(0, props.GetDeliveryReward());
                if (reward <= 0) continue;

                DepositReward(reward);
                MarkProcessed(props);
                totalReward += reward;
                deliveredCount++;

                if (consumePackageOnDeposit)
                {
                    DespawnOrDestroy(props.gameObject);
                }

                // Inform GameManager about each package delivered
                if (GameManager.Instance != null)
                {
                    GameManager.Instance.RegisterPackagesDelivered(1);
                }
            }

            if (deliveredCount > 0)
            {
                Debug.Log($"[PackageDeliveryZone] Batch delivered {deliveredCount} packages for total ${totalReward}.");
            }

            // Remove consumed/destroyed from pending
            _pendingPackages.RemoveWhere(p => p == null || IsAlreadyProcessed(p));

            // Reset zone value after delivery
            if (IsProcessingAuthority())
            {
                _totalValueInZone.Value = 0;
            }
        }

        private void DepositReward(int reward)
        {
            if (!depositDirectlyToBank)
            {
                if (GameManager.Instance != null)
                {
                    GameManager.Instance.AddMoney(reward);
                }
                else if (moneyTargetManager != null)
                {
                    moneyTargetManager.AddMoney(reward);
                }
            }
            else
            {
                var mgr = moneyTargetManager != null ? moneyTargetManager
                         : (GameManager.Instance != null ? FindFirstObjectByType<MoneyTargetManager>() : null);

                if (mgr != null)
                {
                    int newBank = mgr.BankedMoney + reward;
                    mgr.SetBankedMoney(newBank);
                }
                else if (GameManager.Instance != null)
                {
                    GameManager.Instance.AddMoney(reward);
                }
            }
        }

        private bool IsProcessingAuthority()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return true;
            return IsServer;
        }

        private bool IsLayerAllowed(int layer) => (packageMask.value & (1 << layer)) != 0;

        private bool IsTagAllowed(Transform t)
        {
            if (string.IsNullOrEmpty(requiredTag)) return true;
            var cur = t;
            while (cur != null)
            {
                if (cur.CompareTag(requiredTag)) return true;
                cur = cur.parent;
            }
            return false;
        }

        private bool IsAlreadyProcessed(PackageProperties props)
        {
            var no = props.GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned)
            {
                return _processedNetworkIds.Contains(no.NetworkObjectId);
            }

            int id = props.gameObject.GetInstanceID();
            if (_processedInstanceIdUntil.TryGetValue(id, out float until))
            {
                if (Time.time <= until) return true;
            }
            return false;
        }

        private void MarkProcessed(PackageProperties props)
        {
            var no = props.GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned)
            {
                _processedNetworkIds.Add(no.NetworkObjectId);
            }
            else
            {
                int id = props.gameObject.GetInstanceID();
                _processedInstanceIdUntil[id] = Time.time + Mathf.Max(0f, reEntryIgnoreSeconds);
            }
        }

        private void DespawnOrDestroy(GameObject go)
        {
            var no = go.GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned)
            {
                no.Despawn(true);
            }
            else
            {
                Destroy(go);
            }
        }

        // Inspector context menu actions (no UI needed)
        [ContextMenu("Debug/Deliver Now (This Zone)")]
        private void Debug_DeliverNow_ThisZone()
        {
            bool serverOrStandalone = NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;
            if (!serverOrStandalone)
            {
                Debug.LogWarning("[PackageDeliveryZone] Deliver Now can only be triggered by server/host.");
                return;
            }
            ForceDeliverNow();
        }

        [ContextMenu("Debug/Deliver Now (All Zones)")]
        private void Debug_DeliverNow_AllZones()
        {
            bool serverOrStandalone = NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;
            if (!serverOrStandalone)
            {
                Debug.LogWarning("[PackageDeliveryZone] Deliver Now (All Zones) can only be triggered by server/host.");
                return;
            }
            var zones = FindObjectsByType<PackageDeliveryZone>(FindObjectsSortMode.None);
            int triggered = 0;
            foreach (var z in zones)
            {
                if (z != null && z.isActiveAndEnabled)
                {
                    z.ForceDeliverNow();
                    triggered++;
                }
            }
            Debug.Log($"[PackageDeliveryZone] Deliver Now invoked across {triggered} delivery zone(s).");
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_zoneCollider == null) _zoneCollider = GetComponent<Collider>();
            if (_zoneCollider != null && !_zoneCollider.isTrigger)
                _zoneCollider.isTrigger = true;
        }

        private void OnDrawGizmosSelected()
        {
            if (_zoneCollider == null) _zoneCollider = GetComponent<Collider>();
            if (_zoneCollider == null) return;

            // Draw in special color if primary zone
            Gizmos.color = _isPrimaryZone 
                ? new Color(1f, 0.5f, 0f, 0.45f)  // Orange for primary
                : new Color(0.2f, 1f, 0.2f, 0.35f); // Green for normal

            var box = _zoneCollider as BoxCollider;
            var sphere = _zoneCollider as SphereCollider;
            var capsule = _zoneCollider as CapsuleCollider;

            if (box != null)
            {
                Gizmos.matrix = box.transform.localToWorldMatrix;
                Gizmos.DrawCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }
            else if (sphere != null)
            {
                Gizmos.DrawSphere(sphere.bounds.center, sphere.radius * Mathf.Max(
                    sphere.transform.lossyScale.x,
                    Mathf.Max(sphere.transform.lossyScale.y, sphere.transform.lossyScale.z)));
            }
            else if (capsule != null)
            {
                Gizmos.DrawSphere(capsule.bounds.center, Mathf.Max(capsule.radius, 0.1f));
            }
        }
#endif
    }
}

