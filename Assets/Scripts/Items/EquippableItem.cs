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

    private Vector3 _originalLocalScale;
    private bool _hasCachedOriginalScale;

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

    private void Awake()
    {
        _originalLocalScale = transform.localScale;
        _hasCachedOriginalScale = true;
    }

    /// <summary>
    /// Called on all clients (via EquipmentManager) once this item has been locally
    /// parented to a hand bone. Override OnEquipped/OnUnequipped in a derived class
    /// for item-specific behavior, or just use these hooks directly for simple items.
    /// </summary>
    public void HandleEquipped(EquipmentManager holder, Transform handBone)
    {
        CurrentHolder = holder;

        if (!_hasCachedOriginalScale)
        {
            _originalLocalScale = transform.localScale;
            _hasCachedOriginalScale = true;
        }

        if (rb != null)
        {
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }

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
            transform.localScale = _originalLocalScale; // keep visual scale stable while held
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
        // Use worldPositionStays: false and restore the cached scale ourselves,
        // instead of letting Unity recompute localScale from the parent's lossyScale
        // (which compounds shrinkage/growth every equip/drop cycle).
        transform.SetParent(null, worldPositionStays: false);
        NetworkObject.AutoObjectParentSync = previousAutoSync;

        transform.localScale = _originalLocalScale;

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