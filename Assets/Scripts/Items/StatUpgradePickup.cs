using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class StatUpgradePickup : NetworkBehaviour
{
    [SerializeField] private StatUpgradeDefinition definition;

    [Header("Debug")]
    [SerializeField] private bool enableServerLogs = false;

    public StatUpgradeDefinition Definition => definition;

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
                Debug.LogWarning($"[UpgradePickup][FAIL:DEF_NULL] Pickup={name} netId={pickupNetId}");
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
                $"playerOwner={playerStats.OwnerClientId} type={definition.UpgradeType} " +
                $"delta={definition.AddDeltaMultiplier:+0.###;-0.###;0.###} cost={cost} bankedBefore={bankedBefore}");
        }

        // Spend currency (authoritative)
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

        // Apply upgrade
        playerStats.RequestApplyUpgrade(definition.UpgradeType, definition.AddDeltaMultiplier);

        if (enableServerLogs)
        {
            int bankedAfter = gm.GetBankedMoney();
            Debug.Log(
                $"[UpgradePickup][SUCCESS] Pickup={name} netId={pickupNetId} " +
                $"playerOwner={playerStats.OwnerClientId} type={definition.UpgradeType} " +
                $"delta={definition.AddDeltaMultiplier:+0.###;-0.###;0.###} bankedAfter={bankedAfter}");
        }

        // Remove pickup from the world for everyone
        if (pickupNo != null && pickupNo.IsSpawned)
            pickupNo.Despawn(true);
        else
            Destroy(gameObject);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (definition == null) return;
    }
#endif
}