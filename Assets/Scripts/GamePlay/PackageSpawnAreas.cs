using System.Collections.Generic;
using UnityEngine;

namespace DeliverHere.GamePlay
{
    [DisallowMultipleComponent]
    public class PackageSpawnAreas : MonoBehaviour
    {
        [Header("Points")]
        [SerializeField] private List<Transform> spawnPoints = new List<Transform>();

        [Header("Areas")]
        [SerializeField] private List<BoxCollider> spawnAreas = new List<BoxCollider>();

        // Expose serialized lists via read-only properties (NOT public fields)
        public IReadOnlyList<Transform> Points => spawnPoints;
        public IReadOnlyList<BoxCollider> Areas => spawnAreas;
    }
}