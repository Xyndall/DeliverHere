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
        [Tooltip("Automatically deliver all packages in zone when the day timer expires.")]
        [SerializeField] private bool autoDeliverOnDayEnd = true;

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

        private DayNightCycle _dayNight;

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

        private void OnEnable()
        {
            FindDayNightAndSubscribe();
        }

        private void OnDisable()
        {
            UnsubscribeDayNight();
        }

        private void FindDayNightAndSubscribe()
        {
            if (!autoDeliverOnDayEnd) return;
            if (_dayNight == null)
                _dayNight = FindFirstObjectByType<DayNightCycle>();

            if (_dayNight != null)
                _dayNight.OnDayTimerAboutToExpire += HandleDayTimerAboutToExpire;
        }

        private void UnsubscribeDayNight()
        {
            if (_dayNight != null)
                _dayNight.OnDayTimerAboutToExpire -= HandleDayTimerAboutToExpire;
        }

        private void HandleDayTimerAboutToExpire()
        {
            if (!autoDeliverOnDayEnd) return;

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
                Debug.Log($"[PackageDeliveryZone] Cleared {cleared} undelivered package(s) at day end.");
            }

            // Also clear local tracking sets to avoid stale references
            _pendingPackages.RemoveWhere(p => p == null || IsAlreadyProcessed(p));
            _processedNetworkIds.Clear();
            _processedInstanceIdUntil.Clear();
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

            _pendingPackages.Add(props);
        }

        private void TryUnregister(Collider other)
        {
            if (other == null) return;
            var props = other.GetComponentInParent<PackageProperties>();
            if (props == null) return;
            _pendingPackages.Remove(props);
        }

        public void ForceDeliverNow()
        {
            DeliverAllPending();
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

            Gizmos.color = new Color(0.2f, 1f, 0.2f, 0.35f);
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