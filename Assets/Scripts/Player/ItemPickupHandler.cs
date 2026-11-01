using DeliverHere.Settings;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

[DisallowMultipleComponent]
public class ItemPickupHandler : NetworkBehaviour
{
    [Header("References")]
    [Tooltip("Optional. If null, will try to use PlayerHold's camera or fall back to Camera.main.")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private EquipmentManager equipmentManager;
    [SerializeField] private PlayerInputController inputController;
    [Tooltip("Optional. Used to source the camera transform if cameraTransform isn't assigned.")]
    [SerializeField] private PlayerHold playerHold;

    [Header("Look Detection")]
    [Tooltip("Max distance of the sphere cast used to detect pickup-able items.")]
    [SerializeField] private float lookRange = 3f;
    [Tooltip("Radius of the sphere cast, gives the player some leeway when aiming at items.")]
    [SerializeField] private float lookCastRadius = 0.25f;
    [SerializeField] private LayerMask pickupMask = ~0;

    [Header("UI Prompts")]
    [SerializeField] private bool showUiPrompt = true;

    [Header("Controls Config")]
    [Tooltip("Reference to the ControlsConfig ScriptableObject used for binding display")]
    [SerializeField] private ControlsConfig controlsConfig;

    [Header("Gizmos")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private Color gizmoColor = Color.yellow;
    [SerializeField] private Color gizmoHitColor = Color.green;

    [Header("Debug")]
    [SerializeField] private bool enableDebugLogs = true;

    public EquippableItem CurrentLookTarget { get; private set; }

    // UI controller
    private GameUIController _ui;

    // Prompt text
    private const string PickupItemPromptText = "Press {0} to Pick Up {1}";

    // Tracks the last prompt we set, so it can be cleared precisely.
    private string _lastPromptText;

    // Cached control entry for the Interact action
    private ControlEntry _interactControlEntry;
    private bool _isGamepad = false;

    // Set from the InputSystem thread; consumed safely on the main thread in Update.
    private volatile bool _pendingSchemeChange = false;
    private volatile bool _pendingIsGamepad = false;

    private void Log(string message)
    {
        if (enableDebugLogs) Debug.Log($"[ItemPickupHandler:{name}] {message}", this);
    }

    private void LogWarning(string message)
    {
        if (enableDebugLogs) Debug.LogWarning($"[ItemPickupHandler:{name}] {message}", this);
    }

    private void Awake()
    {
        if (playerHold == null) playerHold = GetComponent<PlayerHold>();

        if (equipmentManager == null)
            LogWarning("equipmentManager is not assigned. Pickup requests will silently be ignored.");

        if (inputController == null)
            LogWarning("inputController is not assigned. Interact presses won't be read.");

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

        if (IsOwner && cameraTransform == null)
        {
            if (playerHold != null && playerHold.CameraTransform != null)
            {
                cameraTransform = playerHold.CameraTransform;
                Log("OnNetworkSpawn: resolved cameraTransform from PlayerHold.");
            }
            else if (Camera.main != null)
            {
                cameraTransform = Camera.main.transform;
                Log("OnNetworkSpawn: resolved cameraTransform from Camera.main.");
            }
            else
            {
                LogWarning("OnNetworkSpawn: could not resolve a camera from PlayerHold or Camera.main. Pickup detection will not function.");
            }
        }

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
                _ui.ClearUpgradePromptIfEquals(_lastPromptText);
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
    /// Only sets a flag - never touches Unity objects here.
    /// </summary>
    private void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
    {
        bool deviceIsGamepad = device is Gamepad;

        if (deviceIsGamepad == _isGamepad && !_pendingSchemeChange) return;
        if (deviceIsGamepad == _pendingIsGamepad && _pendingSchemeChange) return;

        _pendingIsGamepad = deviceIsGamepad;
        _pendingSchemeChange = true;
    }

    private void Update()
    {
        if (!IsOwner) return;

        if (_pendingSchemeChange)
        {
            _pendingSchemeChange = false;
            _isGamepad = _pendingIsGamepad;
        }

        UpdateLookTarget();

        // Interact now only ever picks up unheld items that are being looked at.
        if (inputController != null && inputController.InteractPressedThisFrame
            && CurrentLookTarget != null && !CurrentLookTarget.IsHeld)
        {
            RequestEquip(CurrentLookTarget);
        }
    }

    private void UpdateLookTarget()
    {
        CurrentLookTarget = null;
        if (cameraTransform == null)
        {
            ClearPromptUI();
            return;
        }

        if (Physics.SphereCast(cameraTransform.position, lookCastRadius, cameraTransform.forward,
                out var hit, lookRange, pickupMask, QueryTriggerInteraction.Ignore))
        {
            var item = hit.collider.GetComponentInParent<EquippableItem>();
            if (item == null)
            {
                // Hit something, but it's not a pickup-able item - not a failure, just noise, so no log here.
                ClearPromptUI();
                return;
            }

            if (item.IsHeld)
            {
                ClearPromptUI();
                return;
            }

            CurrentLookTarget = item;
            UpdatePromptUI(item);
        }
        else
        {
            ClearPromptUI();
        }
    }

    private void UpdatePromptUI(EquippableItem item)
    {
        if (!showUiPrompt || _ui == null) return;

        string prompt = string.Format(PickupItemPromptText, GetInteractBindingDisplay(), item.DisplayName);

        _lastPromptText = prompt;
        _ui.SetUpgradePrompt(prompt);
    }

    private void ClearPromptUI()
    {
        if (!showUiPrompt || _ui == null) return;

        _ui.ClearUpgradePromptIfEquals(_lastPromptText);
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

    private void RequestEquip(EquippableItem item)
    {
        if (equipmentManager == null)
        {
            LogWarning($"RequestEquip: cannot pick up '{item.DisplayName}', equipmentManager reference is null.");
            return;
        }

        var netObj = item.GetComponent<NetworkObject>();
        if (netObj == null)
        {
            LogWarning($"RequestEquip: '{item.DisplayName}' has no NetworkObject component, cannot send pickup request.");
            return;
        }

        if (!netObj.IsSpawned)
        {
            LogWarning($"RequestEquip: '{item.DisplayName}' NetworkObject is not spawned, cannot send pickup request.");
            return;
        }

        Log($"RequestEquip: requesting pickup of '{item.DisplayName}'.");
        equipmentManager.RequestEquipServerRpc(netObj);
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        var cam = cameraTransform != null ? cameraTransform
            : (playerHold != null ? playerHold.CameraTransform : null);
        if (cam == null) return;

        bool hasHit = Physics.SphereCast(cam.position, lookCastRadius, cam.forward,
            out var hit, lookRange, pickupMask, QueryTriggerInteraction.Ignore);

        Gizmos.color = hasHit ? gizmoHitColor : gizmoColor;

        float castDistance = hasHit ? hit.distance : lookRange;
        Vector3 startCenter = cam.position;
        Vector3 endCenter = cam.position + cam.forward * castDistance;

        Gizmos.DrawWireSphere(startCenter, lookCastRadius);
        Gizmos.DrawWireSphere(endCenter, lookCastRadius);
        Gizmos.DrawLine(startCenter, endCenter);

        if (hasHit)
        {
            Gizmos.DrawLine(startCenter + Vector3.up * lookCastRadius, hit.point);
        }
    }
}