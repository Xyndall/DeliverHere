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

    [Header("UI Prompts")]
    [SerializeField] private bool showUiPrompt = true;

    private PlayerInputController _input;

    // Cached results of the continuous raycast
    private WorldSpawnButton _lookedAtSpawnButton;
    private float _lastLookDistance;

    // UI controller
    private GameUIController _ui;

    // Prompt texts
    private const string SpawnPromptText = "Press [E] to Spawn Packages";

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
        {
            _ui.ClearUpgradePromptIfEquals(SpawnPromptText);
        }
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
            ClearLookedAt();
            return;
        }

        RaycastHit hit;
        bool gotHit = Physics.Raycast(cameraTransform.position, cameraTransform.forward, out hit, range, interactLayer, triggerInteraction);

        if (drawGizmos)
            Debug.DrawLine(cameraTransform.position, cameraTransform.position + cameraTransform.forward * (gotHit ? hit.distance : range), Color.red);

        if (!gotHit || hit.collider == null)
        {
            ClearLookedAt();
            _lastLookDistance = gotHit ? hit.distance : range;
            return;
        }

        // Cache the interactable components if present on the hit object or its parents
        _lookedAtSpawnButton = hit.collider.GetComponentInParent<WorldSpawnButton>();
        _lastLookDistance = hit.distance;

        // Show appropriate prompt based on what we're looking at
        UpdatePromptUI();
    }

    private void ClearLookedAt()
    {
        _lookedAtSpawnButton = null;
        
        if (showUiPrompt && _ui != null)
        {
            _ui.ClearUpgradePromptIfEquals(SpawnPromptText);
        }
    }

    private void UpdatePromptUI()
    {
        if (!showUiPrompt || _ui == null)
            return;

        // Priority: Start button takes precedence over spawn button
        if (_lookedAtSpawnButton != null)
        {
            _ui.SetUpgradePrompt(SpawnPromptText);
        }
        else
        {
            _ui.ClearUpgradePromptIfEquals(SpawnPromptText);
        }
    }

    private void TryInteract()
    {

        if (_lookedAtSpawnButton != null)
        {
            _lookedAtSpawnButton.ActivateLocal();
            return;
        }

        // No cached interactable — nothing to do.
    }
}