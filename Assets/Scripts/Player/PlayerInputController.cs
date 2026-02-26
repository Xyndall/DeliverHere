using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
public class PlayerInputController : NetworkBehaviour
{
    private InputSystem_Actions _actions;

    // NEW: input lock (e.g., during Loading)
    private bool _inputLocked;
    private NetworkGameState _netState;

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
            var ui = FindAnyObjectByType<GameUIController>();
            if (ui != null)
            {
                ui.SetLocalPlayerInput(this);
            }

            BindToNetworkGameState();
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

        var ui = FindAnyObjectByType<GameUIController>();
        if (ui != null)
        {
            ui.SetLocalPlayerInput(this);
        }

        BindToNetworkGameState();
        ApplyInputLock(); // ensure current lock state is applied immediately
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        UnbindFromNetworkGameState();

        enabled = false;
        _actions?.Disable();
    }

    public override void OnDestroy()
    {
        try
        {
            UnbindFromNetworkGameState();

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

    // NEW: public API (optional) if you want to force lock input from elsewhere too
    public void SetInputLocked(bool locked)
    {
        if (_inputLocked == locked) return;
        _inputLocked = locked;
        ApplyInputLock();
        ClearFrameInputs();
    }

    private void BindToNetworkGameState()
    {
        if (!IsOwner) return;

        _netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
        if (_netState == null) return;

        _netState.OnLocalGameStateChanged -= OnLocalGameStateChanged;
        _netState.OnLocalGameStateChanged += OnLocalGameStateChanged;

        // Apply immediately to whatever state we joined into (late join / host)
        OnLocalGameStateChanged(_netState.LocalGameState);
    }

    private void UnbindFromNetworkGameState()
    {
        if (_netState == null) return;
        _netState.OnLocalGameStateChanged -= OnLocalGameStateChanged;
        _netState = null;
    }

    private void OnLocalGameStateChanged(GameState state)
    {
        // Lock input during Loading (and optionally GameOver/MainMenu if you want)
        SetInputLocked(state == GameState.Loading);
    }

    private void ApplyInputLock()
    {
        if (!IsOwner || _actions == null) return;

        if (_inputLocked)
            _actions.Disable();
        else
            _actions.Enable();
    }

    private void ClearFrameInputs()
    {
        Move = Vector2.zero;
        SprintHeld = false;
        ExtendInput = 0f;

        JumpPressedThisFrame = false;
        InteractPressedThisFrame = false;
        PausePressedThisFrame = false;
    }

    private void Update()
    {
        if (!IsOwner || _actions == null || _inputLocked)
        {
            ClearFrameInputs();
            return;
        }

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