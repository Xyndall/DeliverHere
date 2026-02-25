using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
public class PlayerInputController : NetworkBehaviour
{
    private InputSystem_Actions _actions;

    // Public, read-only input state for other scripts
    public Vector2 Move { get; private set; }
    public bool SprintHeld { get; private set; }
    public bool JumpPressedThisFrame { get; private set; }
    public bool InteractPressedThisFrame { get; private set; }
    public float ExtendInput { get; private set; }

    // Pause input (one-frame)
    public bool PausePressedThisFrame { get; private set; }

    // Alias so PlayerHold can use ExtendAxis if needed
    public float ExtendAxis => ExtendInput;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // Only the owner should read local input
        enabled = IsOwner;
        if (enabled)
        {
            if (_actions == null)
            {
                _actions = new InputSystem_Actions();
            }

            _actions.Enable();

            // Register with the local GameUIController
            var ui = FindObjectOfType<GameUIController>();
            if (ui != null)
            {
                ui.SetLocalPlayerInput(this);
            }
        }
        else
        {
            _actions?.Disable();
        }
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        enabled = true;
        if (_actions == null)
        {
            _actions = new InputSystem_Actions();
        }
        _actions.Enable();

        var ui = FindObjectOfType<GameUIController>();
        if (ui != null)
        {
            ui.SetLocalPlayerInput(this);
        }
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        enabled = false;
        _actions?.Disable();
    }

    public override void OnDestroy()
    {
        try
        {
            if (_actions != null)
            {
                _actions.Dispose();
                _actions = null;
            }
        }
        finally
        {
            base.OnDestroy();
        }
    }

    private void Update()
    {
        if (!IsOwner || _actions == null)
            return;

        // Continuous inputs
        Move = _actions.Player.Move.ReadValue<Vector2>();
        SprintHeld = _actions.Player.Sprint.IsPressed();
        ExtendInput = _actions.Player.Extend.ReadValue<float>();

        // One-frame button inputs (polled directly)
        JumpPressedThisFrame = _actions.Player.Jump.WasPressedThisFrame();
        InteractPressedThisFrame = _actions.Player.Interact.WasPressedThisFrame();

        // Pause input (one-frame)
        PausePressedThisFrame = _actions.Player.Pause.WasPressedThisFrame();
    }
}