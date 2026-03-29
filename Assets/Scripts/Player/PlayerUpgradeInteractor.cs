using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerInputController))]
[RequireComponent(typeof(PlayerUpgradableStats))]
public sealed class PlayerUpgradeInteractor : NetworkBehaviour
{
    [Header("Camera (used for aiming interact ray)")]
    [SerializeField] private Transform cameraTransform;

    [Header("Detection")]
    [SerializeField] private LayerMask pickupMask = ~0;
    [SerializeField] private float pickupCastRadius = 0.15f;
    [SerializeField] private float pickupRange = 3f;

    [Header("UI")]
    [SerializeField] private bool showUiPrompt = true;

    // NEW: server-side tolerance (meters) so range checks don't fail due to pivot differences
    [SerializeField] private float serverRangeBuffer = 0.5f;

    private PlayerInputController _input;
    private PlayerUpgradableStats _stats;
    private GameUIController _ui;

    private NetworkObjectReference _lookedAtRef;
    private StatUpgradePickup _lookedAtPickupLocal;

    private void Awake()
    {
        _input = GetComponent<PlayerInputController>();
        _stats = GetComponent<PlayerUpgradableStats>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsOwner && cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (IsOwner)
            _ui = FindAnyObjectByType<GameUIController>();
    }

    private void OnDisable()
    {
        if (IsOwner && _ui != null)
            _ui.ClearUpgradePrompt();
    }

    private void Update()
    {
        if (!IsOwner) return;
        if (_input == null) return;

        UpdateLookTargetAndPrompt();

        if (!_input.InteractPressedThisFrame)
            return;

        if (!_lookedAtRef.TryGet(out _))
            return;

        TryConsumePickupServerRpc(_lookedAtRef);
    }

    private void UpdateLookTargetAndPrompt()
    {
        // Lazy-find the UI in case it wasn't present during OnNetworkSpawn
        if (IsOwner && _ui == null)
            _ui = FindAnyObjectByType<GameUIController>();

        _lookedAtPickupLocal = null;
        _lookedAtRef = default;

        if (cameraTransform == null)
        {
            if (showUiPrompt && _ui != null) _ui.ClearUpgradePrompt();
            return;
        }

        if (!Physics.SphereCast(
                cameraTransform.position,
                pickupCastRadius,
                cameraTransform.forward,
                out RaycastHit hit,
                pickupRange,
                pickupMask,
                QueryTriggerInteraction.Ignore))
        {
            if (showUiPrompt && _ui != null) _ui.ClearUpgradePrompt();
            return;
        }

        var pickup = hit.collider != null ? hit.collider.GetComponentInParent<StatUpgradePickup>() : null;
        if (pickup == null)
        {
            if (showUiPrompt && _ui != null) _ui.ClearUpgradePrompt();
            return;
        }

        var pickupNo = pickup.GetComponent<NetworkObject>();
        if (pickupNo == null || !pickupNo.IsSpawned)
        {
            if (showUiPrompt && _ui != null) _ui.ClearUpgradePrompt();
            return;
        }

        _lookedAtPickupLocal = pickup;
        _lookedAtRef = new NetworkObjectReference(pickupNo);

        if (!showUiPrompt || _ui == null)
            return;

        var def = pickup.Definition;
        if (def == null)
        {
            _ui.ClearUpgradePrompt();
            return;
        }

        int cost = def.Cost;
        float pct = def.AddDeltaMultiplier * 100f;

        string statName = def.UpgradeType.ToString();
        string sign = pct >= 0f ? "+" : "";
        string costText = cost > 0 ? $"${cost}" : "FREE";

        // Example: "MoveSpeed +10%  |  Cost: $50  |  Press Interact"
        string prompt = $"{statName} {sign}{pct:0.#}%  |  Cost: {costText}  |  Press Interact";
        _ui.SetUpgradePrompt(prompt);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void TryConsumePickupServerRpc(NetworkObjectReference pickupRef)
    {
        if (!IsServer) return;

        if (!pickupRef.TryGet(out NetworkObject pickupNo)) return;

        var pickup = pickupNo.GetComponent<StatUpgradePickup>();
        if (pickup == null) return;

        // Server-side distance/security check: always use player root (no camera dependency on server)
        float effectiveRange = pickupRange + Mathf.Max(0f, serverRangeBuffer);
        float maxSqr = effectiveRange * effectiveRange;

        float sqr = (pickup.transform.position - transform.position).sqrMagnitude;
        if (sqr > maxSqr)
            return;

        pickup.ServerConsumeBy(_stats);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        pickupCastRadius = Mathf.Max(0.01f, pickupCastRadius);
        pickupRange = Mathf.Max(0.2f, pickupRange);
        serverRangeBuffer = Mathf.Max(0f, serverRangeBuffer);
    }
#endif
}