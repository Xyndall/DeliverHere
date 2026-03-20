using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class StatUpgradePickup : NetworkBehaviour
{
    [Header("Catalog (shared asset on all clients)")]
    [SerializeField] private StatUpgradeCatalog catalog;

    [Header("Resolved (do not manually set at runtime)")]
    [SerializeField] private StatUpgradeDefinition definition;

    [Header("Fallback (server)")]
    [Tooltip("If true, the server will pick a random definition index from the catalog when none is set/valid.")]
    [SerializeField] private bool serverPickRandomIfUnset = true;

    [Tooltip("Delay (seconds) before auto-picking, to allow spawners to set the index first.")]
    [SerializeField] private float serverRandomPickDelaySeconds = 0.05f;

    [Header("Debug")]
    [SerializeField] private bool enableServerLogs = false;

    private readonly NetworkVariable<int> nvDefinitionIndex =
        new NetworkVariable<int>(-1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public StatUpgradeDefinition Definition => definition;
    public int DefinitionIndex => nvDefinitionIndex.Value;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        nvDefinitionIndex.OnValueChanged += OnDefinitionIndexChanged;
        ResolveDefinitionFromIndex(nvDefinitionIndex.Value);

        // Backup: if spawner didn't set a valid index, server chooses one.
        if (IsServer && serverPickRandomIfUnset)
        {
            CancelInvoke(nameof(ServerEnsureDefinitionAssigned));
            Invoke(nameof(ServerEnsureDefinitionAssigned), Mathf.Max(0f, serverRandomPickDelaySeconds));
        }
    }

    public override void OnNetworkDespawn()
    {
        nvDefinitionIndex.OnValueChanged -= OnDefinitionIndexChanged;
        CancelInvoke(nameof(ServerEnsureDefinitionAssigned));
        base.OnNetworkDespawn();
    }

    private void OnDefinitionIndexChanged(int previous, int current)
    {
        ResolveDefinitionFromIndex(current);
    }

    private void ResolveDefinitionFromIndex(int index)
    {
        if (catalog == null)
        {
            definition = null;
            return;
        }

        if (!catalog.TryGet(index, out var def))
        {
            definition = null;
            return;
        }

        definition = def;
    }

    // Server-side setup API for spawners
    public void SetDefinitionIndexServer(int index)
    {
        if (!IsServer) return;

        nvDefinitionIndex.Value = index;
        ResolveDefinitionFromIndex(index);

        if (enableServerLogs)
            Debug.Log($"[UpgradePickup][SET] Pickup={name} idx={index} def={(definition != null ? definition.name : "null")}");
    }

    private void ServerEnsureDefinitionAssigned()
    {
        if (!IsServer) return;

        // If already valid, do nothing
        if (definition != null && nvDefinitionIndex.Value >= 0)
            return;

        if (catalog == null || catalog.Definitions == null || catalog.Definitions.Count == 0)
        {
            if (enableServerLogs)
                Debug.LogWarning($"[UpgradePickup][FAIL:CATALOG_EMPTY] Pickup={name}");
            return;
        }

        // Pick a random NON-null definition index
        int picked = -1;
        for (int tries = 0; tries < 50; tries++)
        {
            int idx = Random.Range(0, catalog.Definitions.Count);
            if (catalog.Definitions[idx] != null)
            {
                picked = idx;
                break;
            }
        }

        if (picked < 0)
        {
            // fallback scan
            for (int i = 0; i < catalog.Definitions.Count; i++)
            {
                if (catalog.Definitions[i] != null)
                {
                    picked = i;
                    break;
                }
            }
        }

        if (picked < 0)
        {
            if (enableServerLogs)
                Debug.LogWarning($"[UpgradePickup][FAIL:NO_VALID_DEFS] Pickup={name}");
            return;
        }

        nvDefinitionIndex.Value = picked;
        ResolveDefinitionFromIndex(picked);

        if (enableServerLogs)
            Debug.Log($"[UpgradePickup][AUTO_PICK] Pickup={name} idx={picked} def={definition.name}");
    }

    public bool CanAfford(GameManager gm)
    {
        if (gm == null || definition == null) return false;
        return gm.GetBankedMoney() >= definition.Cost;
    }

    public void ServerConsumeBy(PlayerUpgradableStats playerStats)
    {
        if (!IsServer)
        {
            if (enableServerLogs)
                Debug.LogWarning($"[UpgradePickup][ServerConsumeBy] Called on non-server. Pickup={name}");
            return;
        }

        var pickupNo = GetComponent<NetworkObject>();
        ulong pickupNetId = pickupNo != null ? pickupNo.NetworkObjectId : 0;

        if (definition == null)
        {
            if (enableServerLogs)
                Debug.LogWarning($"[UpgradePickup][FAIL:DEF_NULL] Pickup={name} netId={pickupNetId} idx={nvDefinitionIndex.Value}");
            return;
        }

        if (playerStats == null)
        {
            if (enableServerLogs)
                Debug.LogWarning($"[UpgradePickup][FAIL:PLAYER_NULL] Pickup={name} netId={pickupNetId}");
            return;
        }

        var gm = GameManager.Instance;
        if (gm == null)
        {
            if (enableServerLogs)
                Debug.LogWarning($"[UpgradePickup][FAIL:GM_NULL] Pickup={name} netId={pickupNetId} playerOwner={playerStats.OwnerClientId}");
            return;
        }

        int cost = Mathf.Max(0, definition.Cost);
        int bankedBefore = gm.GetBankedMoney();

        if (enableServerLogs)
        {
            Debug.Log(
                $"[UpgradePickup][TRY] Pickup={name} netId={pickupNetId} " +
                $"playerOwner={playerStats.OwnerClientId} idx={nvDefinitionIndex.Value} type={definition.UpgradeType} " +
                $"delta={definition.AddDeltaMultiplier:+0.###;-0.###;0.###} cost={cost} bankedBefore={bankedBefore}");
        }

        if (cost > 0)
        {
            bool paid = gm.SpendBanked(cost);
            if (!paid)
            {
                if (enableServerLogs)
                {
                    int bankedAfterFail = gm.GetBankedMoney();
                    Debug.LogWarning(
                        $"[UpgradePickup][FAIL:CANT_AFFORD] Pickup={name} netId={pickupNetId} " +
                        $"playerOwner={playerStats.OwnerClientId} cost={cost} bankedBefore={bankedBefore} bankedAfter={bankedAfterFail}");
                }
                return;
            }
        }

        playerStats.RequestApplyUpgrade(definition.UpgradeType, definition.AddDeltaMultiplier);

        if (enableServerLogs)
        {
            int bankedAfter = gm.GetBankedMoney();
            Debug.Log(
                $"[UpgradePickup][SUCCESS] Pickup={name} netId={pickupNetId} " +
                $"playerOwner={playerStats.OwnerClientId} idx={nvDefinitionIndex.Value} type={definition.UpgradeType} " +
                $"delta={definition.AddDeltaMultiplier:+0.###;-0.###;0.###} bankedAfter={bankedAfter}");
        }

        if (pickupNo != null && pickupNo.IsSpawned)
            pickupNo.Despawn(true);
        else
            Destroy(gameObject);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        serverRandomPickDelaySeconds = Mathf.Max(0f, serverRandomPickDelaySeconds);
    }
#endif
}