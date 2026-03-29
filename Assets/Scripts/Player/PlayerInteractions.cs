using UnityEngine;
using Unity.Netcode;
using DeliverHere.GamePlay;

[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerInputController))]
public class PlayerInteractions : NetworkBehaviour
{
    public enum CastMode { Ray, Sphere, Capsule }

    [Header("Aiming / Detection")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private LayerMask interactLayer = ~0; // set to your "Interactable" layer in inspector
    [SerializeField, Min(0.1f)] private float range = 3f;
    [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore;

    [Header("Feedback (optional)")]
    [SerializeField] private bool drawGizmos = true;

    [Header("UI (reuses upgrade prompt)")]
    [SerializeField] private bool showUiPrompt = true;

    private PlayerInputController _input;

    // Cached result of the continuous raycast
    private WorldSpawnButton _lookedAtButton;
    private float _lastLookDistance;

    // UI controller (reused from PlayerUpgradeInteractor)
    private GameUIController _ui;

    // constant prompt text this component owns
    private const string SpawnPromptText = "Interact to spawn packages";

    private void Awake()
    {
        _input = GetComponent<PlayerInputController>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // Only owner should act on local input
        enabled = IsOwner;

        if (IsOwner && cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (IsOwner)
            _ui = FindAnyObjectByType<GameUIController>();
    }

    private void OnDisable()
    {
        if (IsOwner && _ui != null)
            _ui.ClearUpgradePromptIfEquals(SpawnPromptText);
    }

    private void Update()
    {
        if (!IsOwner) return;
        if (_input == null) return;

        // Always update what we're looking at every frame
        DetectLookedAt();

        // React only when the interact button is pressed this frame
        if (!_input.InteractPressedThisFrame) return;

        TryInteract();
    }

    // Continuously performs the raycast and caches the looked-at interactable (if any)
    private void DetectLookedAt()
    {
        if (cameraTransform == null)
        {
            _lookedAtButton = null;
            _lastLookDistance = 0f;
            if (showUiPrompt && _ui != null) _ui.ClearUpgradePromptIfEquals(SpawnPromptText);
            return;
        }

        RaycastHit hit;
        bool gotHit = Physics.Raycast(cameraTransform.position, cameraTransform.forward, out hit, range, interactLayer, triggerInteraction);

        if (drawGizmos)
            Debug.DrawLine(cameraTransform.position, cameraTransform.position + cameraTransform.forward * (gotHit ? hit.distance : range), Color.red);

        if (!gotHit || hit.collider == null)
        {
            _lookedAtButton = null;
            _lastLookDistance = gotHit ? hit.distance : range;
            if (showUiPrompt && _ui != null) _ui.ClearUpgradePromptIfEquals(SpawnPromptText);
            return;
        }

        // Cache the WorldSpawnButton if present on the hit object or its parents
        _lookedAtButton = hit.collider.GetComponentInParent<WorldSpawnButton>();
        _lastLookDistance = hit.distance;

        // Show a basic prompt (reusing the upgrade prompt UI)
        if (!showUiPrompt || _ui == null)
            return;

        if (_lookedAtButton != null)
        {
            // Basic text — change if you want something more descriptive
            _ui.SetUpgradePrompt(SpawnPromptText);
        }
        else
        {
            _ui.ClearUpgradePromptIfEquals(SpawnPromptText);
        }
    }

    private void TryInteract()
    {
        // Use the cached looked-at interactable
        if (_lookedAtButton != null)
        {
            // ActivateLocal will handle server/standalone logic (it issues RPCs when needed)
            _lookedAtButton.ActivateLocal();
            return;
        }

        // No cached interactable — nothing to do.
    }

}