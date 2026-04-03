using UnityEngine;
using Unity.Netcode;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// In-world button that allows the host to start the game after level load.
    /// Triggers warehouse assignment and delivery zone discovery.
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldStartGameButton : NetworkBehaviour
    {
        [Header("Visual")]
        [SerializeField] private GameObject buttonVisuals;
        [Tooltip("Optional: Hide entire GameObject when game starts (recommended).")]
        [SerializeField] private bool hideGameObjectOnStart = true;

        [Header("Auto-Hide After Use")]
        [SerializeField] private bool hideAfterActivation = true;

        [Header("Debug")]
        [SerializeField] private bool enableLogs = true;

        private bool _hasBeenActivated = false;

        private void Start()
        {
            // Ensure button is visible initially
            if (buttonVisuals != null)
                buttonVisuals.SetActive(true);
        }

        /// <summary>
        /// Called locally by the player interacting with the button.
        /// </summary>
        public void ActivateLocal()
        {
            if (_hasBeenActivated)
            {
                if (enableLogs)
                    Debug.Log("[WorldStartGameButton] Already activated, ignoring.");
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

            // Optional: check if requester is host/server
            // For now, allow any client to start (change as needed)
            
            ServerStartGame();
        }

        private void ServerStartGame()
        {
            if (_hasBeenActivated)
                return;

            _hasBeenActivated = true;

            if (enableLogs)
                Debug.Log("[WorldStartGameButton] Starting game setup...");

            // 1. Find and setup delivery zone manager
            var zoneManager = FindFirstObjectByType<DailyDeliveryZoneManager>();
            if (zoneManager != null)
            {
                // Force warehouse discovery
                zoneManager.ServerSetupAfterLevelLoad();
            }

            // 2. Trigger GameManager to start the game
            var gm = GameManager.Instance;
            if (gm != null)
            {
                gm.StartGame();
            }
            else
            {
                Debug.LogWarning("[WorldStartGameButton] GameManager not found!");
            }

            // 3. Hide button across all clients
            if (hideAfterActivation)
            {
                HideButtonClientRpc();
            }
        }

        [ClientRpc]
        private void HideButtonClientRpc()
        {
            HideButton();
        }

        private void HideButton()
        {
            if (hideGameObjectOnStart)
            {
                gameObject.SetActive(false);
            }
            else if (buttonVisuals != null)
            {
                buttonVisuals.SetActive(false);
            }

            if (enableLogs)
                Debug.Log("[WorldStartGameButton] Button hidden.");
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

            ActivateLocal();
        }
#endif
    }
}