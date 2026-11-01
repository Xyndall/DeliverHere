using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[DisallowMultipleComponent]
public class EquippableItem : NetworkBehaviour
{
    [Header("Identity")]
    [SerializeField] private string itemId;
    [SerializeField] private string displayName;

    [Header("Hand Attachment")]
    [Tooltip("Local position offset applied when attached to the hand bone.")]
    [SerializeField] private Vector3 handPositionOffset;
    [Tooltip("Local euler rotation offset applied when attached to the hand bone.")]
    [SerializeField] private Vector3 handRotationOffset;

    [Header("Physics")]
    [Tooltip("Rigidbody to disable/enable physics on while equipped.")]
    [SerializeField] private Rigidbody rb;
    [Tooltip("Colliders to disable while equipped (avoid hitting the player).")]
    [SerializeField] private Collider[] collidersToDisableWhileHeld;

    [Header("Networking")]
    [Tooltip("Optional. If present, disabled while held so it doesn't fight the local hand-bone attachment, and re-enabled on drop so the server can resync position.")]
    [SerializeField] private NetworkTransform networkTransform;

    public string ItemId => itemId;
    public string DisplayName => displayName;
    public Vector3 HandPositionOffset => handPositionOffset;
    public Vector3 HandRotationOffset => handRotationOffset;

    /// <summary>Currently equipping EquipmentManager, null if not held.</summary>
    public EquipmentManager CurrentHolder { get; private set; }

    public bool IsHeld => CurrentHolder != null;

    private void Reset()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
        if (networkTransform == null) networkTransform = GetComponent<NetworkTransform>();
    }

    /// <summary>
    /// Called on all clients (via EquipmentManager) once this item has been locally
    /// parented to a hand bone. Override OnEquipped/OnUnequipped in a derived class
    /// for item-specific behavior, or just use these hooks directly for simple items.
    /// </summary>
    public void HandleEquipped(EquipmentManager holder, Transform handBone)
    {
        CurrentHolder = holder;

        if (rb != null)
        {
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }

        // NetworkTransform can't be network-parented to a bone with no NetworkObject,
        // so we disable it while held and drive the visual attachment purely locally
        // (identical on every client, since every client has the same rig).
        if (networkTransform != null)
        {
            networkTransform.enabled = false;
        }

        if (handBone != null)
        {
            // NetworkObject patches Transform.SetParent and throws InvalidParentException
            // if AutoObjectParentSync is on and the new parent has no NetworkObject.
            // We're intentionally doing a local-only, non-networked parent here (every
            // client parents to its own copy of the same bone), so disable the sync
            // around the call, then restore it so unrelated NGO behavior isn't affected.
            bool previousAutoSync = NetworkObject.AutoObjectParentSync;
            NetworkObject.AutoObjectParentSync = false;
            transform.SetParent(handBone, worldPositionStays: false);
            NetworkObject.AutoObjectParentSync = previousAutoSync;

            transform.localPosition = handPositionOffset;
            transform.localRotation = Quaternion.Euler(handRotationOffset);
        }

        SetHeldColliders(false);
        OnEquipped(holder);
    }

    public void HandleUnequipped()
    {
        var previousHolder = CurrentHolder;
        CurrentHolder = null;

        bool previousAutoSync = NetworkObject.AutoObjectParentSync;
        NetworkObject.AutoObjectParentSync = false;
        transform.SetParent(null, worldPositionStays: true);
        NetworkObject.AutoObjectParentSync = previousAutoSync;

        if (networkTransform != null)
        {
            networkTransform.enabled = true;
        }

        if (rb != null)
        {
            rb.isKinematic = false;
            rb.detectCollisions = true;
        }

        SetHeldColliders(true);
        OnUnequipped(previousHolder);
    }

    /// <summary>
    /// Called on the server (and forwarded to clients if you want VFX/SFX) when the
    /// holder "uses" the item (e.g., swing sword, toggle flashlight).
    /// </summary>
    public virtual void OnUse(EquipmentManager holder)
    {
        // Default: no-op. Override per item, or attach a companion component
        // implementing IUsable and forward the call from here.
    }

    protected virtual void OnEquipped(EquipmentManager holder) { }
    protected virtual void OnUnequipped(EquipmentManager previousHolder) { }

    private void SetHeldColliders(bool enabledState)
    {
        if (collidersToDisableWhileHeld == null) return;
        foreach (var c in collidersToDisableWhileHeld)
        {
            if (c != null) c.enabled = enabledState;
        }
    }
}