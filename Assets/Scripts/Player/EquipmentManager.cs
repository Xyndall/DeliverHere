using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class EquipmentManager : NetworkBehaviour
{
    [Header("Attachment")]
    [Tooltip("The hand bone/socket transform items get parented to.")]
    [SerializeField] private Transform handBone;

    [Header("Input")]
    [SerializeField] private PlayerInputController inputController;

    [Header("Drop")]
    [Tooltip("Forward offset (meters) applied to a dropped item's position, from the hand bone.")]
    [SerializeField] private float dropForwardOffset = 0.5f;
    [Tooltip("Impulse applied to a dropped item's rigidbody, if present.")]
    [SerializeField] private float dropForwardImpulse = 1.5f;

    [Header("Debug")]
    [SerializeField] private bool enableDebugLogs = true;

    // Server-authoritative reference to what's equipped.
    private readonly NetworkVariable<NetworkObjectReference> _equippedItemRef =
        new NetworkVariable<NetworkObjectReference>(default,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public EquippableItem EquippedItem { get; private set; }
    public bool HasItemEquipped => EquippedItem != null;

    private void Log(string message)
    {
        if (enableDebugLogs) Debug.Log($"[EquipmentManager:{name}] {message}", this);
    }

    private void LogWarning(string message)
    {
        if (enableDebugLogs) Debug.LogWarning($"[EquipmentManager:{name}] {message}", this);
    }

    private void Awake()
    {
        if (handBone == null)
            LogWarning("handBone is not assigned. Equipped items will parent to nothing useful, and EquipServer will keep item at its NetworkObject's current world position.");

        if (inputController == null)
        {
            inputController = GetComponent<PlayerInputController>();
            if (inputController == null)
                LogWarning("No PlayerInputController assigned or found via GetComponent. Attack/Drop input won't be read.");
        }
    }

    public override void OnNetworkSpawn()
    {
        _equippedItemRef.OnValueChanged += OnEquippedRefChanged;

        // Late-joiners / already-equipped state sync
        if (_equippedItemRef.Value.TryGet(out var netObj))
        {
            Log($"OnNetworkSpawn: syncing already-equipped item '{netObj.name}'.");
            var item = netObj.GetComponent<EquippableItem>();
            if (item == null)
                LogWarning($"OnNetworkSpawn: equipped NetworkObject '{netObj.name}' has no EquippableItem component.");
            AttachLocally(item);
        }
    }

    public override void OnNetworkDespawn()
    {
        _equippedItemRef.OnValueChanged -= OnEquippedRefChanged;
    }

    private void Update()
    {
        if (!IsOwner || inputController == null) return;
        if (!HasItemEquipped) return;

        if (inputController.AttackPressedThisFrame)
        {
            Log("Attack pressed - requesting use.");
            RequestUseServerRpc();
        }

        if (inputController.DropPressedThisFrame)
        {
            Log("Drop pressed - requesting unequip.");
            RequestUnequipServerRpc();
        }
    }

    // ---------- Server API ----------

    [ServerRpc]
    public void RequestEquipServerRpc(NetworkObjectReference itemRef, ServerRpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;

        if (!itemRef.TryGet(out var netObj))
        {
            LogWarning($"RequestEquipServerRpc from client {senderId}: NetworkObjectReference could not be resolved (item may have been destroyed/despawned).");
            return;
        }

        var item = netObj.GetComponent<EquippableItem>();
        if (item == null)
        {
            LogWarning($"RequestEquipServerRpc from client {senderId}: '{netObj.name}' has no EquippableItem component.");
            return;
        }

        if (item.IsHeld)
        {
            LogWarning($"RequestEquipServerRpc from client {senderId}: '{item.DisplayName}' is already held by another EquipmentManager. Request ignored.");
            return;
        }

        // Unequip current item first (drop it) if any.
        if (_equippedItemRef.Value.TryGet(out var currentObj))
        {
            Log($"RequestEquipServerRpc: swapping out currently held '{currentObj.name}' for '{netObj.name}'.");
            UnequipServer(currentObj.GetComponent<EquippableItem>());
        }

        Log($"RequestEquipServerRpc from client {senderId}: equipping '{item.DisplayName}'.");
        EquipServer(item);
    }

    [ServerRpc]
    public void RequestUnequipServerRpc()
    {
        if (!_equippedItemRef.Value.TryGet(out var currentObj))
        {
            LogWarning("RequestUnequipServerRpc: nothing is currently equipped (NetworkVariable had no resolvable value).");
            return;
        }

        var item = currentObj.GetComponent<EquippableItem>();
        if (item == null)
        {
            LogWarning($"RequestUnequipServerRpc: equipped object '{currentObj.name}' has no EquippableItem component.");
            return;
        }

        Log($"RequestUnequipServerRpc: dropping '{item.DisplayName}'.");
        UnequipServer(item);
    }

    [ServerRpc]
    private void RequestUseServerRpc()
    {
        if (!IsServer) return;

        if (!_equippedItemRef.Value.TryGet(out var currentObj))
        {
            LogWarning("RequestUseServerRpc: no item currently equipped.");
            return;
        }

        var item = currentObj.GetComponent<EquippableItem>();
        if (item == null)
        {
            LogWarning($"RequestUseServerRpc: equipped object '{currentObj.name}' has no EquippableItem component.");
            return;
        }

        Log($"RequestUseServerRpc: invoking OnUse on '{item.DisplayName}'.");
        item.OnUse(this);
    }

    private void EquipServer(EquippableItem item)
    {
        var netObj = item.NetworkObject;

        // NOTE: We intentionally do NOT use NetworkObject.TrySetParent here.
        _equippedItemRef.Value = netObj;
    }

    private void UnequipServer(EquippableItem item)
    {
        if (item == null)
        {
            LogWarning("UnequipServer called with a null EquippableItem.");
            return;
        }

        var netObj = item.NetworkObject;
        var dropPosition = handBone != null
            ? handBone.position + handBone.forward * dropForwardOffset
            : netObj.transform.position;

        _equippedItemRef.Value = default;

        // HandleUnequipped (triggered via OnEquippedRefChanged on all clients) will have
        // already unparented the item locally by the time this runs on the server, since
        // NetworkVariable callbacks fire synchronously on Value set for the local instance.
        netObj.transform.position = dropPosition;

        // Give it a little toss so it doesn't just appear underfoot.
        var rb = netObj.GetComponent<Rigidbody>();
        if (rb == null)
        {
            LogWarning($"UnequipServer: '{item.DisplayName}' has no Rigidbody, drop impulse skipped.");
        }
        else if (handBone != null)
        {
            rb.AddForce(handBone.forward * dropForwardImpulse, ForceMode.VelocityChange);
        }
    }

    // ---------- Client-side reaction (runs on all clients incl. server) ----------

    private void OnEquippedRefChanged(NetworkObjectReference previous, NetworkObjectReference current)
    {
        if (previous.TryGet(out var prevObj))
        {
            var prevItem = prevObj.GetComponent<EquippableItem>();
            if (prevItem == null)
                LogWarning($"OnEquippedRefChanged: previous object '{prevObj.name}' has no EquippableItem component.");
            prevItem?.HandleUnequipped();
        }

        if (current.TryGet(out var curObj))
        {
            var item = curObj.GetComponent<EquippableItem>();
            if (item == null)
                LogWarning($"OnEquippedRefChanged: new object '{curObj.name}' has no EquippableItem component.");
            AttachLocally(item);
        }
        else
        {
            EquippedItem = null;
        }
    }

    private void AttachLocally(EquippableItem item)
    {
        if (item == null) return;
        EquippedItem = item;
        Log($"AttachLocally: '{item.DisplayName}' equipped locally (IsOwner={IsOwner}, IsServer={IsServer}).");
        item.HandleEquipped(this, handBone);
    }

    /// <summary>
    /// Server-only, forces the currently equipped item to be dropped immediately.
    /// Call this BEFORE enabling ragdoll physics so the item isn't left parented
    /// to a hand bone that's about to be unparented/driven by physics.
    /// </summary>
    public void ForceDropEquippedItem()
    {
        if (!IsServer)
        {
            LogWarning("ForceDropEquippedItem called on a non-server instance; ignored.");
            return;
        }

        if (!_equippedItemRef.Value.TryGet(out var currentObj))
            return; // nothing equipped

        var item = currentObj.GetComponent<EquippableItem>();
        if (item == null)
        {
            LogWarning($"ForceDropEquippedItem: equipped object '{currentObj.name}' has no EquippableItem component.");
            return;
        }

        Log($"ForceDropEquippedItem: force-dropping '{item.DisplayName}' before ragdoll.");
        UnequipServer(item);
    }
}