using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class UpgradeShopSpawner : NetworkBehaviour
{
    [Header("Catalog (shared asset on all clients)")]
    [SerializeField] private StatUpgradeCatalog catalog;

    [Header("Spawn Points")]
    [SerializeField] private List<Transform> spawnPoints = new List<Transform>();

    [Header("What can spawn")]
    [SerializeField] private List<StatUpgradeDefinition> possibleUpgrades = new List<StatUpgradeDefinition>();

    [Header("Prefab")]
    [SerializeField] private StatUpgradePickup pickupPrefab;

    [Header("Counts")]
    [SerializeField, Min(0)] private int upgradesPerDay = 3;
    [SerializeField] private bool uniqueWithinDay = true;

    [Header("Lifecycle")]
    [SerializeField] private bool clearOnShopDisabled = true;

    [Header("Debug")]
    [SerializeField] private bool enableServerLogs = false;

    private readonly List<NetworkObject> _spawned = new List<NetworkObject>();

    public override void OnNetworkDespawn()
    {
        if (IsServer && clearOnShopDisabled)
            ServerClearSpawned();

        base.OnNetworkDespawn();
    }

    /// <summary>Server: replaces the shop stock for the given day.</summary>
    public void ServerRefreshForDay(int dayIndex)
    {
        if (!IsServer) return;

        ServerClearSpawned();
        ServerSpawnStock(dayIndex);
    }

    private void ServerSpawnStock(int dayIndex)
    {
        if (pickupPrefab == null) return;
        if (spawnPoints == null || spawnPoints.Count == 0) return;
        if (possibleUpgrades == null || possibleUpgrades.Count == 0) return;
        if (catalog == null || catalog.Definitions == null) return;

        int count = Mathf.Min(upgradesPerDay, spawnPoints.Count);
        if (count <= 0) return;

        var used = uniqueWithinDay ? new HashSet<int>() : null;

        for (int i = 0; i < count; i++)
        {
            var point = spawnPoints[i];
            if (point == null) continue;

            int possibleIndex = PickDefinitionIndex(used);
            if (possibleIndex < 0) break;

            var def = possibleUpgrades[possibleIndex];
            if (def == null) continue;

            int catalogIndex = FindCatalogIndex(def);
            if (catalogIndex < 0)
            {
                Debug.LogWarning($"[UpgradeShopSpawner] Definition '{def.name}' is not present in catalog '{catalog.name}'. Shop='{name}'");
                continue;
            }

            var inst = Instantiate(pickupPrefab, point.position, point.rotation);

            // Ensure the pickup can resolve indices (prefab should also have this assigned)
            if (enableServerLogs && inst.Definition == null && inst.DefinitionIndex < 0)
            {
                // not necessarily an error yet; index will be set below
            }

            inst.SetDefinitionIndexServer(catalogIndex);

            var no = inst.GetComponent<NetworkObject>();
            if (no == null)
            {
                Debug.LogWarning($"[UpgradeShopSpawner] Pickup prefab '{pickupPrefab.name}' is missing NetworkObject. Shop='{name}'");
                Destroy(inst.gameObject);
                continue;
            }

            no.Spawn(true);
            _spawned.Add(no);

            if (enableServerLogs)
                Debug.Log($"[UpgradeShopSpawner] Spawned '{inst.name}' at '{point.name}' idx={catalogIndex} def='{def.name}' shop='{name}'");
        }
    }

    private int FindCatalogIndex(StatUpgradeDefinition def)
    {
        var list = catalog.Definitions;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == def)
                return i;
        }
        return -1;
    }

    private int PickDefinitionIndex(HashSet<int> used)
    {
        if (possibleUpgrades == null || possibleUpgrades.Count == 0) return -1;

        if (used == null)
            return Random.Range(0, possibleUpgrades.Count);

        if (used.Count >= possibleUpgrades.Count)
            used.Clear();

        for (int tries = 0; tries < 50; tries++)
        {
            int idx = Random.Range(0, possibleUpgrades.Count);
            if (used.Add(idx))
                return idx;
        }

        for (int i = 0; i < possibleUpgrades.Count; i++)
        {
            if (used.Add(i))
                return i;
        }

        return -1;
    }

    private void ServerClearSpawned()
    {
        for (int i = _spawned.Count - 1; i >= 0; i--)
        {
            var no = _spawned[i];
            _spawned.RemoveAt(i);

            if (no == null) continue;

            if (no.IsSpawned)
                no.Despawn(true);
            else
                Destroy(no.gameObject);
        }
    }
}