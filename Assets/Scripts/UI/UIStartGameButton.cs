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

        private bool _hasBeenActivated = false;

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
            if (NetworkGameState.Instance != null)
            {
                NetworkGameState.Instance.OnLocalGameStateChanged -= OnGameStateChanged;
            }
            
            base.OnNetworkDespawn();
        }

        private void OnGameStateChanged(GameState newState)
        {
            // Show button panel only in ReadyToStart state
            bool shouldShow = newState == GameState.ReadyToStart && !_hasBeenActivated;
            
            if (panelToHide != null)
            {
                panelToHide.SetActive(shouldShow);
            }
            else if (startButton != null)
            {
                startButton.gameObject.SetActive(shouldShow);
            }
            
            if (enableLogs)
                Debug.Log($"[UIStartGameButton] State changed to {newState}, button visible: {shouldShow}");
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
            startButton.interactable = isHost && !_hasBeenActivated;

            // Update button text if available
            if (buttonText != null)
            {
                if (_hasBeenActivated)
                {
                    buttonText.text = "Game Started";
                }
                else
                {
                    buttonText.text = isHost ? defaultText : waitingText;
                }
            }
        }

        /// <summary>
        /// Called when the UI button is clicked.
        /// </summary>
        private void OnButtonClicked()
        {
            if (_hasBeenActivated)
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
            if (_hasBeenActivated)
                return;

            _hasBeenActivated = true;

            if (enableLogs)
                Debug.Log("[UIStartGameButton] Starting game setup...");

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