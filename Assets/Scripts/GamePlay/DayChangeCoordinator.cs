using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class DayChangeCoordinator : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private MoneyTargetManager moneyTargetManager;

    [Header("Shop Discovery")]
    [Tooltip("If empty, shops will be auto-discovered at runtime.")]
    [SerializeField] private List<UpgradeShopSpawner> shops = new List<UpgradeShopSpawner>();

    [Tooltip("If true, re-scan the scene for shops every time the day advances.")]
    [SerializeField] private bool rescanShopsOnDayAdvance = true;

    private void Awake()
    {
        if (moneyTargetManager == null)
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (moneyTargetManager == null)
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();

        if (moneyTargetManager != null)
            moneyTargetManager.OnDayAdvanced += OnDayAdvanced;

        // Server drives initial discovery + first refresh so shops aren't empty on join.
        if (IsServer)
        {
            DiscoverShopsIfNeeded(force: true);

            // If a day has already started, stock shops immediately.
            // If your day starts at 0 and increments on first AdvanceDay(), this is still safe.
            ServerRefreshAllShops(moneyTargetManager != null ? moneyTargetManager.CurrentDay : 0);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (moneyTargetManager != null)
            moneyTargetManager.OnDayAdvanced -= OnDayAdvanced;

        base.OnNetworkDespawn();
    }

    private void OnDayAdvanced(int newDayIndex)
    {
        if (!IsServer) return;

        if (rescanShopsOnDayAdvance)
            DiscoverShopsIfNeeded(force: true);
        else
            DiscoverShopsIfNeeded(force: false);

        ServerRefreshAllShops(newDayIndex);

        // Later: other day-change systems (random events, weather, etc.)
    }

    private void DiscoverShopsIfNeeded(bool force)
    {
        if (!force && shops != null && shops.Count > 0)
            return;

        shops ??= new List<UpgradeShopSpawner>();
        shops.Clear();

        // Include inactive so disabled shops can still be managed if needed.
        var found = FindObjectsByType<UpgradeShopSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            if (found[i] != null)
                shops.Add(found[i]);
        }
    }

    private void ServerRefreshAllShops(int dayIndex)
    {
        if (shops == null) return;

        for (int i = 0; i < shops.Count; i++)
        {
            var s = shops[i];
            if (s == null) continue;

            // Only refresh spawned shops that are actually present in the network session.
            // (If a shop is a scene NetworkObject, it should be spawned. If it isn't networked, it still works
            // because spawning is done by the shop itself on the server.)
            s.ServerRefreshForDay(dayIndex);
        }
    }
}