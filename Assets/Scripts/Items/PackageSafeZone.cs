using System.Collections.Generic;
using UnityEngine;

namespace DeliverHere.Items
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public sealed class PackageSafeZone : MonoBehaviour
    {
        private static readonly HashSet<PackageProperties> PackagesInSafeZones = new HashSet<PackageProperties>();

        public static bool IsPackageSafe(PackageProperties packageProperties)
        {
            return packageProperties != null && PackagesInSafeZones.Contains(packageProperties);
        }

        private void Awake()
        {
            var col = GetComponent<Collider>();
            if (col != null && !col.isTrigger)
            {
                col.isTrigger = true;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            var props = other.GetComponentInParent<PackageProperties>();
            if (props != null)
            {
                PackagesInSafeZones.Add(props);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            var props = other.GetComponentInParent<PackageProperties>();
            if (props != null)
            {
                PackagesInSafeZones.Remove(props);
            }
        }
    }
}