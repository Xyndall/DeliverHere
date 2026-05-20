using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;

namespace DeliverHere.UI
{
    /// <summary>
    /// UI Canvas button handler that allows the host to start the game after level load.
    /// Triggers warehouse assignment and delivery zone discovery.
    /// Works with Unity UI Button component on a Canvas.
    /// </summary>
    [RequireComponent(typeof(Button))]
    [DisallowMultipleComponent]
    public class UIStartGameButton : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private Button startButton;
        [Tooltip("Optional: Parent GameObject to hide when game starts (e.g., entire start panel).")]
        [SerializeField] private GameObject panelToHide;

        [Header("Button Text (Optional)")]
        [Tooltip("Optional: Text component to update button label.")]
        [SerializeField] private TMPro.TMP_Text buttonText;
        [SerializeField] private string defaultText = "Start Game";
        [SerializeField] private string waitingText = "Waiting for Host...";

        [Header("Auto-Hide After Use")]
        [SerializeField] private bool hideAfterActivation = true;

        [Header("Debug")]
        [SerializeField] private bool enableLogs = true;

        // Network variable to sync activation state across all clients
        private NetworkVariable<bool> _hasBeenActivated = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private void Awake()
        {
            // Auto-find button if not assigned
            if (startButton == null)
            {
                startButton = GetComponent<Button>();
            }

            if (startButton != null)
            {
                startButton.onClick.AddListener(OnButtonClicked);
            }
            else
            {
                Debug.LogError("[UIStartGameButton] No Button component found!");
            }
        }

        private void Start()
        {
            UpdateButtonState();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            
            // Subscribe to activation state changes
            _hasBeenActivated.OnValueChanged += OnActivationStateChanged;
            
            // Subscribe to game state changes to show/hide button
            if (NetworkGameState.Instance != null)
            {
                NetworkGameState.Instance.OnLocalGameStateChanged += OnGameStateChanged;
                
                // Apply initial state
                OnGameStateChanged(NetworkGameState.Instance.LocalGameState);
            }
            
            UpdateButtonState();
        }

        public override void OnNetworkDespawn()
        {
            _hasBeenActivated.OnValueChanged -= OnActivationStateChanged;
            
            if (NetworkGameState.Instance != null)
            {
                NetworkGameState.Instance.OnLocalGameStateChanged -= OnGameStateChanged;
            }
            
            base.OnNetworkDespawn();
        }

        private void OnActivationStateChanged(bool previousValue, bool newValue)
        {
            if (enableLogs)
                Debug.Log($"[UIStartGameButton] Activation state changed: {previousValue} -> {newValue}");
            
            UpdateButtonState();
            
            // Update visibility based on current game state
            if (NetworkGameState.Instance != null)
            {
                OnGameStateChanged(NetworkGameState.Instance.LocalGameState);
            }
        }

        private void OnGameStateChanged(GameState newState)
        {
            // Show button panel in Lobby (after reset) or ReadyToStart (after level load)
            bool shouldShow = (newState == GameState.ReadyToStart || newState == GameState.Lobby) && !_hasBeenActivated.Value;
            
            if (panelToHide != null)
            {
                panelToHide.SetActive(shouldShow);
            }
            else if (startButton != null)
            {
                startButton.gameObject.SetActive(shouldShow);
            }
            
            if (enableLogs)
                Debug.Log($"[UIStartGameButton] State changed to {newState}, button visible: {shouldShow}, activated: {_hasBeenActivated.Value}");
        }

        /// <summary>
        /// Updates button interactability based on network role.
        /// Only host/server can press the button.
        /// </summary>
        private void UpdateButtonState()
        {
            if (startButton == null) return;

            bool isHost = NetworkManager.Singleton != null && 
                         (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost);

            // Button is only interactable if player is host and game hasn't started
            startButton.interactable = isHost && !_hasBeenActivated.Value;

            // Update button text if available
            if (buttonText != null)
            {
                if (_hasBeenActivated.Value)
                {
                    buttonText.text = "Game Started";
                }
                else
                {
                    // Show appropriate text based on whether we need to load a level first
                    bool needsLevelLoad = NetworkGameState.Instance != null && 
                                         NetworkGameState.Instance.LocalGameState == GameState.Lobby;
                    
                    if (isHost)
                    {
                        buttonText.text = needsLevelLoad ? "Load & Start Game" : defaultText;
                    }
                    else
                    {
                        buttonText.text = waitingText;
                    }
                }
            }
        }

        /// <summary>
        /// Called when the UI button is clicked.
        /// </summary>
        private void OnButtonClicked()
        {
            if (_hasBeenActivated.Value)
            {
                if (enableLogs)
                    Debug.Log("[UIStartGameButton] Already activated, ignoring.");
                return;
            }

            // If standalone or this is the server, activate directly
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
            {
                ServerStartGame();
            }
            else
            {
                // Request server to start
                RequestStartGameServerRpc();
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestStartGameServerRpc(RpcParams rpcParams = default)
        {
            if (!IsServer) return;

            // Optional: Verify requester is host/server
            // For now, allow any client to start (change as needed)
            
            ServerStartGame();
        }

        private void ServerStartGame()
        {
            if (_hasBeenActivated.Value)
            {
                if (enableLogs)
                    Debug.Log("[UIStartGameButton] Already activated on server, ignoring.");
                return;
            }

            // Set activation state on server (will replicate to all clients)
            _hasBeenActivated.Value = true;

            if (enableLogs)
                Debug.Log("[UIStartGameButton] Starting game setup...");

            // Check if we're in Lobby state and need to load a level first
            bool needsLevelLoad = NetworkGameState.Instance != null && 
                                 NetworkGameState.Instance.LocalGameState == GameState.Lobby;

            if (needsLevelLoad)
            {
                // Trigger level load flow which will eventually call back to start the game
                if (enableLogs)
                    Debug.Log("[UIStartGameButton] Triggering level load from Lobby state...");
                
                if (NetworkGameState.Instance != null)
                {
                    NetworkGameState.Instance.RequestStartGameServerRpc();
                }
                
                // Hide button during load
                if (hideAfterActivation)
                {
                    HideButtonClientRpc();
                }
                
                return;
            }

            // Otherwise, we're in ReadyToStart state after a level load
            // 1. Find and setup delivery zone manager
            var zoneManager = FindFirstObjectByType<GamePlay.DailyDeliveryZoneManager>();
            if (zoneManager != null)
            {
                // Force warehouse discovery
                zoneManager.ServerSetupAfterLevelLoad();
            }
            else
            {
                Debug.LogWarning("[UIStartGameButton] DailyDeliveryZoneManager not found!");
            }

            // 2. Trigger GameManager to start the game
            var gm = GameManager.Instance;
            if (gm != null)
            {
                gm.StartGame();
            }
            else
            {
                Debug.LogWarning("[UIStartGameButton] GameManager not found!");
            }

            // 3. Transition game state from ReadyToStart -> InGame
            if (NetworkGameState.Instance != null)
            {
                NetworkGameState.Instance.ServerBeginGameplay();
            }

            // 4. Hide button across all clients
            if (hideAfterActivation)
            {
                HideButtonClientRpc();
            }
        }

        [Rpc(SendTo.ClientsAndHost)]
        private void HideButtonClientRpc()
        {
            HideButton();
        }

        private void HideButton()
        {
            if (panelToHide != null)
            {
                panelToHide.SetActive(false);
            }
            else if (startButton != null)
            {
                // Hide just the button if no panel specified
                startButton.gameObject.SetActive(false);
            }

            if (enableLogs)
                Debug.Log("[UIStartGameButton] Button hidden.");
        }

        private void OnDestroy()
        {
            if (startButton != null)
            {
                startButton.onClick.RemoveListener(OnButtonClicked);
            }
        }

        /// <summary>
        /// Resets the button state so it can be activated again.
        /// Should be called when returning to lobby or resetting the game.
        /// Server-only method.
        /// </summary>
        public void ServerResetButton()
        {
            if (!IsServer)
            {
                Debug.LogWarning("[UIStartGameButton] ServerResetButton can only be called on server!");
                return;
            }

            if (enableLogs)
                Debug.Log($"[UIStartGameButton] ServerResetButton called. Current activation state: {_hasBeenActivated.Value}");

            // Reset the network variable (will replicate to all clients)
            _hasBeenActivated.Value = false;
            
            if (enableLogs)
                Debug.Log($"[UIStartGameButton] Activation state reset to: {_hasBeenActivated.Value}. Current game state: {NetworkGameState.Instance?.LocalGameState}");
            
            // Force show the button on all clients
            ShowButtonClientRpc();
        }

        [Rpc(SendTo.ClientsAndHost)]
        private void ShowButtonClientRpc()
        {
            // Force visibility update based on current game state
            if (NetworkGameState.Instance != null)
            {
                OnGameStateChanged(NetworkGameState.Instance.LocalGameState);
            }
            else
            {
                // Fallback: show button by default
                if (panelToHide != null)
                {
                    panelToHide.SetActive(true);
                }
                else if (startButton != null)
                {
                    startButton.gameObject.SetActive(true);
                }
            }
            
            UpdateButtonState();
            
            if (enableLogs)
                Debug.Log("[UIStartGameButton] Button shown on client via RPC.");
        }

#if UNITY_EDITOR
        [ContextMenu("Test Activate")]
        private void TestActivate()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Only works in Play mode.");
                return;
            }

            OnButtonClicked();
        }

        [ContextMenu("Test Reset (Server Only)")]
        private void TestReset()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Only works in Play mode.");
                return;
            }

            if (!IsServer)
            {
                Debug.LogWarning("Reset can only be called on server.");
                return;
            }

            ServerResetButton();
        }

        private void OnValidate()
        {
            // Auto-assign button in editor
            if (startButton == null)
            {
                startButton = GetComponent<Button>();
            }
        }
#endif
    }
}