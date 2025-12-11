using UnityEngine;
using Unity.Netcode;

[DisallowMultipleComponent]
public class HoldableHoldingState : NetworkBehaviour
{
    // Number of players currently holding this object (server authoritative).
    public NetworkVariable<int> HoldersCount { get; private set; }
        = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public int CurrentCount => Mathf.Max(0, HoldersCount.Value);

    // Server-side helpers
    public void ServerAddHolder()
    {
        if (!IsServer) return;
        HoldersCount.Value = Mathf.Max(0, HoldersCount.Value + 1);
    }

    public void ServerRemoveHolder()
    {
        if (!IsServer) return;
        HoldersCount.Value = Mathf.Max(0, HoldersCount.Value - 1);
    }
}