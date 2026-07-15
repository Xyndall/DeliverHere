using UnityEngine;
using Unity.Netcode;
using DeliverHere.Items;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Destroys packages (and optionally other objects) when they enter the trigger zone.
    /// Typically placed at the end of a conveyor belt.
    /// Server-authoritative to ensure consistent state across network.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public class PackageDestroyer : NetworkBehaviour
    {
        [Header("Filtering")]
        [Tooltip("Only objects on these layers will be destroyed.")]
        [SerializeField] private LayerMask destroyLayers = ~0;

        [Tooltip("If enabled, only destroy objects with PackageProperties component.")]
        [SerializeField] private bool onlyDestroyPackages = true;

        [Tooltip("Optional required tag. Leave empty to destroy all matching objects.")]
        [SerializeField] private string requiredTag = "";

        [Header("Cooldown")]
        [Tooltip("Minimum time between processing the same object (prevents double-destruction).")]
        [SerializeField, Min(0f)] private float reprocessCooldown = 0.1f;

        [Header("Debug")]
        [SerializeField] private bool logDestructions = true;

        private Collider _triggerCollider;
        
        // Track recently processed objects to prevent double-destruction
        private readonly System.Collections.Generic.Dictionary<int, float> _processedInstanceIds = 
            new System.Collections.Generic.Dictionary<int, float>();

        private void Awake()
        {
            _triggerCollider = GetComponent<Collider>();
            
            if (_triggerCollider != null && !_triggerCollider.isTrigger)
            {
                Debug.LogWarning("[PackageDestroyer] Collider wasn't trigger. Enabling trigger mode.");
                _triggerCollider.isTrigger = true;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            // Only server processes destruction (authoritative)
            if (!IsServer && NetworkManager.Singleton != null)
                return;

            TryDestroyObject(other);
        }

        private void TryDestroyObject(Collider other)
        {
            if (other == null)
                return;

            // Layer check
            if (((1 << other.gameObject.layer) & destroyLayers.value) == 0)
                return;

            // Tag check
            if (!string.IsNullOrEmpty(requiredTag) && !other.CompareTag(requiredTag))
                return;

            GameObject target = other.gameObject;
            
            // Try to find root NetworkObject
            NetworkObject netObj = target.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                netObj = target.GetComponentInParent<NetworkObject>();
            }

            // If we have a NetworkObject, use that as the target
            if (netObj != null)
            {
                target = netObj.gameObject;
            }

            // Package check
            if (onlyDestroyPackages)
            {
                PackageProperties package = target.GetComponent<PackageProperties>();
                if (package == null)
                {
                    package = target.GetComponentInParent<PackageProperties>();
                }

                if (package == null)
                    return;
            }

            // Cooldown check (prevent double-destruction)
            int instanceId = target.GetInstanceID();
            if (_processedInstanceIds.TryGetValue(instanceId, out float lastProcessedTime))
            {
                if (Time.time - lastProcessedTime < reprocessCooldown)
                    return;
            }

            _processedInstanceIds[instanceId] = Time.time;

            // Log destruction
            if (logDestructions)
            {
                Debug.Log($"[PackageDestroyer] Destroying object: {target.name} at position {target.transform.position}");
            }



            // Destroy the object (despawn if networked, otherwise destroy)
            if (netObj != null && netObj.IsSpawned)
            {
                netObj.Despawn(destroy: true);
            }
            else
            {
                Destroy(target);
            }
        }


        // Cleanup old entries from processed dictionary
        private void Update()
        {
            if (!IsServer && NetworkManager.Singleton != null)
                return;

            // Clean up old entries every second
            if (Time.frameCount % 60 == 0)
            {
                float currentTime = Time.time;
                var keysToRemove = new System.Collections.Generic.List<int>();

                foreach (var kvp in _processedInstanceIds)
                {
                    if (currentTime - kvp.Value > reprocessCooldown * 10f)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _processedInstanceIds.Remove(key);
                }
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_triggerCollider == null)
            {
                _triggerCollider = GetComponent<Collider>();
            }

            if (_triggerCollider != null && !_triggerCollider.isTrigger)
            {
                _triggerCollider.isTrigger = true;
            }

            reprocessCooldown = Mathf.Max(0f, reprocessCooldown);
        }

        private void OnDrawGizmosSelected()
        {
            if (_triggerCollider == null)
            {
                _triggerCollider = GetComponent<Collider>();
            }

            if (_triggerCollider == null)
                return;

            // Draw destroyer zone in red
            Gizmos.color = new Color(1f, 0f, 0f, 0.4f);
            
            BoxCollider box = _triggerCollider as BoxCollider;
            SphereCollider sphere = _triggerCollider as SphereCollider;
            CapsuleCollider capsule = _triggerCollider as CapsuleCollider;

            if (box != null)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }
            else if (sphere != null)
            {
                Gizmos.DrawSphere(sphere.bounds.center, sphere.radius);
            }
            else if (capsule != null)
            {
                Gizmos.DrawSphere(capsule.bounds.center, capsule.radius);
            }
        }
#endif
    }
}