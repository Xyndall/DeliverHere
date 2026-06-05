using UnityEngine;
using Unity.Netcode;
using DeliverHere.GamePlay;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using DeliverHere.Settings;

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
    
    [Header("Controls Config")]
    [Tooltip("Reference to the ControlsConfig ScriptableObject used for binding display")]
    [SerializeField] private ControlsConfig controlsConfig;

    private PlayerInputController _input;

    // Cached results of the continuous raycast
    private WorldSpawnButton _lookedAtSpawnButton;
    private float _lastLookDistance;

    // UI controller
    private GameUIController _ui;

    // Prompt texts
    private const string SpawnPromptText = "Press {0} to Spawn Packages";
    private const string DefaultInteractPromptText = "Press {0} to Interact";

    // Cached control entry for the Interact action
    private ControlEntry _interactControlEntry;
    private bool _isGamepad = false;

    // Set from the InputSystem thread; consumed safely on the main thread in Update.
    private volatile bool _pendingSchemeChange = false;
    private volatile bool _pendingIsGamepad = false;

    private void Awake()
    {
        _input = GetComponent<PlayerInputController>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        // Find the Interact control entry from the config
        if (controlsConfig != null)
        {
            _interactControlEntry = controlsConfig.controls.Find(entry => 
                entry.actionReference != null && 
                entry.actionReference.action != null &&
                entry.actionReference.action.name == "Interact");
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // Only owner should act on local input
        enabled = IsOwner;

        if (IsOwner && cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (IsOwner)
        {
            _ui = FindAnyObjectByType<GameUIController>();
            DetectCurrentDevice();
        }
    }

    private void OnEnable()
    {
        if (IsOwner)
        {
            InputSystem.onEvent += OnInputEvent;
        }
    }

    private void OnDisable()
    {
        if (IsOwner)
        {
            InputSystem.onEvent -= OnInputEvent;

            if (_ui != null)
            {
                _ui.ClearUpgradePromptIfEquals(GetFormattedPrompt(SpawnPromptText));
                _ui.ClearUpgradePromptIfEquals(GetFormattedPrompt(DefaultInteractPromptText));
            }
        }
    }

    /// <summary>
    /// Detects the current device type (keyboard/mouse vs gamepad)
    /// </summary>
    private void DetectCurrentDevice()
    {
        bool gamepadConnected = Gamepad.current != null;
        bool keyboardConnected = Keyboard.current != null;

        // Default to keyboard if both (or neither) are present
        _isGamepad = gamepadConnected && !keyboardConnected;
    }

    /// <summary>
    /// Called on the InputSystem thread for every raw input event.
    /// Only sets a flag — never touches Unity objects here.
    /// </summary>
    private void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
    {
        bool deviceIsGamepad = device is Gamepad;

        // Early-out: no change, or a pending change of the same type already queued.
        if (deviceIsGamepad == _isGamepad && !_pendingSchemeChange) return;
        if (deviceIsGamepad == _pendingIsGamepad && _pendingSchemeChange) return;

        _pendingIsGamepad = deviceIsGamepad;
        _pendingSchemeChange = true;
    }

    private void Update()
    {
        if (!IsOwner) return;
        if (_input == null) return;

        // Apply any pending scheme change on the main thread where it is safe to touch Unity objects.
        if (_pendingSchemeChange)
        {
            _pendingSchemeChange = false;
            _isGamepad = _pendingIsGamepad;
        }

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
            _ui.ClearUpgradePromptIfEquals(GetFormattedPrompt(SpawnPromptText));
            _ui.ClearUpgradePromptIfEquals(GetFormattedPrompt(DefaultInteractPromptText));
        }
    }

    private void UpdatePromptUI()
    {
        if (!showUiPrompt || _ui == null)
            return;

        // Priority: Start button takes precedence over spawn button
        if (_lookedAtSpawnButton != null)
        {
            _ui.SetUpgradePrompt(GetFormattedPrompt(SpawnPromptText));
        }
        else
        {
            // Show default interact prompt when looking at any interactable layer
            _ui.SetUpgradePrompt(GetFormattedPrompt(DefaultInteractPromptText));
        }
    }

    /// <summary>
    /// Gets the current interact key binding display string based on active controller
    /// </summary>
    private string GetInteractBindingDisplay()
    {
        if (_interactControlEntry == null || _interactControlEntry.actionReference == null)
            return "[E]";

        InputAction action = _interactControlEntry.actionReference.action;
        if (action == null)
            return "[E]";

        string group = _isGamepad ? _interactControlEntry.gamepadGroup : _interactControlEntry.keyboardGroup;

        foreach (InputBinding binding in action.bindings)
        {
            // Skip composite parent entries
            if (binding.isComposite) continue;

            if (!string.IsNullOrEmpty(binding.groups) && binding.groups.Contains(group))
            {
                string displayString = InputControlPath.ToHumanReadableString(
                    binding.effectivePath,
                    InputControlPath.HumanReadableStringOptions.OmitDevice);
                
                return string.IsNullOrEmpty(displayString) ? "[E]" : $"[{displayString}]";
            }
        }

        // Fallback: return whatever the action returns by default
        string fallback = action.GetBindingDisplayString();
        return string.IsNullOrEmpty(fallback) ? "[E]" : $"[{fallback}]";
    }

    /// <summary>
    /// Formats a prompt string with the current interact binding
    /// </summary>
    private string GetFormattedPrompt(string promptTemplate)
    {
        return string.Format(promptTemplate, GetInteractBindingDisplay());
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