using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Spawns blockades at random spawn points for the event duration.
/// Randomly adds and removes blockades during the event.
/// </summary>
public class BlockadeEventSpawner : BaseEventSpawner
{
    [Header("Spawn Points")]
    [SerializeField] private List<Transform> spawnPoints = new List<Transform>();
    [SerializeField] private bool autoFindPointsIfEmpty = true;

    [Header("Blockade Settings")]
    [SerializeField] private GameObject blockadePrefab;
    [Tooltip("Minimum number of blockades to spawn initially.")]
    [SerializeField] private int minInitialBlockades = 1;
    [Tooltip("Maximum number of blockades to spawn initially.")]
    [SerializeField] private int maxInitialBlockades = 3;
    [SerializeField] private Transform blockadeParent;

    [Header("Event Dynamics")]
    [Tooltip("Minimum seconds between adding/removing blockades.")]
    [SerializeField] private float minChangeInterval = 3f;
    [Tooltip("Maximum seconds between adding/removing blockades.")]
    [SerializeField] private float maxChangeInterval = 8f;
    [Tooltip("Chance (0-1) to add a blockade vs remove one during dynamic changes.")]
    [Range(0f, 1f)]
    [SerializeField] private float addChance = 0.5f;

    // Track which points currently have blockades
    private Dictionary<Transform, GameObject> activeBlockades = new Dictionary<Transform, GameObject>();

    protected override bool ValidateAndInitialize()
    {
        if (blockadePrefab == null)
        {
            Debug.LogError("BlockadeEventSpawner: No blockade prefab assigned!");
            return false;
        }

        if (blockadeParent == null)
        {
            var container = GameObject.Find("RuntimeEvents") ?? new GameObject("RuntimeEvents");
            blockadeParent = container.transform;
            if (!blockadeParent.gameObject.activeSelf)
                blockadeParent.gameObject.SetActive(true);
        }

        // Discover spawn points
        if ((spawnPoints == null || spawnPoints.Count == 0) && autoFindPointsIfEmpty)
        {
            var markers = FindObjectsByType<BlockadeSpawnPoint>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var marker in markers)
            {
                if (marker.transform != null)
                    spawnPoints.Add(marker.transform);
            }
        }

        if (spawnPoints == null || spawnPoints.Count == 0)
        {
            Debug.LogWarning("BlockadeEventSpawner: No spawn points found!");
            return false;
        }

        // Clamp settings
        minInitialBlockades = Mathf.Clamp(minInitialBlockades, 0, spawnPoints.Count);
        maxInitialBlockades = Mathf.Clamp(maxInitialBlockades, minInitialBlockades, spawnPoints.Count);

        return true;
    }

    protected override void OnEventStart()
    {
        StartCoroutine(RunEventCoroutine());
    }

    protected override void OnEventEnd()
    {
        CleanupAllBlockades();
    }

    private IEnumerator RunEventCoroutine()
    {
        // Spawn initial blockades
        int initialCount = Random.Range(minInitialBlockades, maxInitialBlockades + 1);
        SpawnInitialBlockades(initialCount);

        float elapsed = 0f;
        float nextChangeTime = Random.Range(minChangeInterval, maxChangeInterval);

        // Dynamic changes during event
        while (ShouldContinueEvent(elapsed))
        {
            yield return null;
            elapsed += Time.deltaTime;

            if (elapsed >= nextChangeTime)
            {
                PerformRandomChange();
                nextChangeTime = elapsed + Random.Range(minChangeInterval, maxChangeInterval);
            }
        }

        // End the event (cleanup happens in OnEventEnd)
        EndEvent();
    }

    private void SpawnInitialBlockades(int count)
    {
        // Create a shuffled list of available points
        List<Transform> availablePoints = new List<Transform>(spawnPoints);
        ShuffleList(availablePoints);

        int spawned = 0;
        for (int i = 0; i < availablePoints.Count && spawned < count; i++)
        {
            Transform point = availablePoints[i];
            if (TrySpawnBlockadeAt(point))
            {
                spawned++;
            }
        }
    }

    private void PerformRandomChange()
    {
        bool shouldAdd = Random.value < addChance;

        if (shouldAdd)
        {
            TryAddRandomBlockade();
        }
        else
        {
            TryRemoveRandomBlockade();
        }
    }

    private void TryAddRandomBlockade()
    {
        // Get available spawn points (not currently occupied)
        List<Transform> availablePoints = new List<Transform>();
        foreach (Transform point in spawnPoints)
        {
            if (!activeBlockades.ContainsKey(point))
                availablePoints.Add(point);
        }

        if (availablePoints.Count == 0)
            return; // All points occupied

        // Pick a random available point
        Transform chosen = availablePoints[Random.Range(0, availablePoints.Count)];
        TrySpawnBlockadeAt(chosen);
    }

    private void TryRemoveRandomBlockade()
    {
        if (activeBlockades.Count == 0)
            return; // No blockades to remove

        // Pick a random active blockade
        List<Transform> occupiedPoints = new List<Transform>(activeBlockades.Keys);
        Transform chosen = occupiedPoints[Random.Range(0, occupiedPoints.Count)];

        RemoveBlockadeAt(chosen);
    }

    private bool TrySpawnBlockadeAt(Transform point)
    {
        if (point == null || activeBlockades.ContainsKey(point))
            return false;

        Vector3 spawnPos = point.position;
        Quaternion spawnRot = point.rotation;

        GameObject go = Instantiate(blockadePrefab, spawnPos, spawnRot, blockadeParent);

        // Network spawning
        var netObj = go.GetComponent<NetworkObject>();
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && netObj != null && !netObj.IsSpawned)
        {
            netObj.Spawn(true);
        }

        // Ensure visible
        if (!go.activeSelf) go.SetActive(true);

        // Track it
        activeBlockades[point] = go;

        return true;
    }

    private void RemoveBlockadeAt(Transform point)
    {
        if (!activeBlockades.ContainsKey(point))
            return;

        GameObject blockade = activeBlockades[point];
        activeBlockades.Remove(point);

        if (blockade != null)
        {
            // Despawn if networked
            var netObj = blockade.GetComponent<NetworkObject>();
            if (netObj != null && netObj.IsSpawned)
            {
                netObj.Despawn(true);
            }
            else
            {
                Destroy(blockade);
            }
        }
    }

    private void CleanupAllBlockades()
    {
        List<Transform> points = new List<Transform>(activeBlockades.Keys);
        foreach (Transform point in points)
        {
            RemoveBlockadeAt(point);
        }
        activeBlockades.Clear();
    }

    private void ShuffleList<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            T temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }
    }
}