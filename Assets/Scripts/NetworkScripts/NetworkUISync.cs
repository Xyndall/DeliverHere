using Unity.Netcode;
using UnityEngine;
using TMPro;
using Unity.Collections;
using System.Collections;

namespace DeliverHere.NetworkScripts
{
    /// <summary>
    /// Centralized network UI synchronization system.
    /// Ensures ALL critical UI elements are properly replicated to clients.
    /// </summary>
    public class NetworkUISync : NetworkBehaviour
    {
        public static NetworkUISync Instance { get; private set; }

        [Header("UI References")]
        [SerializeField] private GameUIController gameUIController;
        [SerializeField] private TMP_Text joinCodeText;
        [SerializeField] private bool autoFindReferences = true;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = true;

        // Network variables for ALL UI state
        private NetworkVariable<FixedString128Bytes> nvJoinCode = new NetworkVariable<FixedString128Bytes>(
            "", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<int> nvCurrentDay = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<int> nvBankedMoney = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<int> nvCurrentValueInZone = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<int> nvTargetMoney = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<float> nvTimerSeconds = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<bool> nvHudVisible = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<bool> nvTimerVisible = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Events for UI updates
        public event System.Action<string> OnJoinCodeChanged;
        public event System.Action OnUIStateChanged;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (autoFindReferences)
            {
                StartCoroutine(FindReferencesDelayed());
            }
        }

        private IEnumerator FindReferencesDelayed()
        {
            // Wait a frame for everything to initialize
            yield return null;

            if (gameUIController == null)
            {
                gameUIController = FindFirstObjectByType<GameUIController>();
                if (gameUIController != null && enableDebugLogs)
                    Debug.Log("[NetworkUISync] Found GameUIController");
            }

            if (joinCodeText == null)
            {
                var networkManagerUI = FindFirstObjectByType<NetworkManagerUI>();
                if (networkManagerUI != null)
                {
                    // Try to get the join code text from NetworkManagerUI via reflection
                    var field = typeof(NetworkManagerUI).GetField("joinCodeText", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null)
                    {
                        joinCodeText = field.GetValue(networkManagerUI) as TMP_Text;
                        if (joinCodeText != null && enableDebugLogs)
                            Debug.Log("[NetworkUISync] Found join code text via NetworkManagerUI");
                    }
                }
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Subscribe to all network variable changes
            nvJoinCode.OnValueChanged += OnJoinCodeNetworkChanged;
            nvCurrentDay.OnValueChanged += OnDayChanged;
            nvBankedMoney.OnValueChanged += OnBankedMoneyChanged;
            nvCurrentValueInZone.OnValueChanged += OnZoneValueChanged;
            nvTargetMoney.OnValueChanged += OnTargetMoneyChanged;
            nvTimerSeconds.OnValueChanged += OnTimerChanged;
            nvHudVisible.OnValueChanged += OnHudVisibilityChanged;
            nvTimerVisible.OnValueChanged += OnTimerVisibilityChanged;

            if (!IsServer)
            {
                // Clients: Apply all initial values
                StartCoroutine(ApplyInitialUIStateDelayed());
            }
            else if (enableDebugLogs)
            {
                Debug.Log("[NetworkUISync] Server spawned, ready to sync UI");
            }
        }

        private IEnumerator ApplyInitialUIStateDelayed()
        {
            // Wait for UI controller to be ready
            float timeout = 5f;
            float elapsed = 0f;

            while (gameUIController == null && elapsed < timeout)
            {
                gameUIController = FindFirstObjectByType<GameUIController>();
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }

            if (gameUIController == null)
            {
                Debug.LogWarning("[NetworkUISync] Client: GameUIController not found after timeout!");
                yield break;
            }

            // Apply all current values
            ApplyAllUIState();

            if (enableDebugLogs)
                Debug.Log("[NetworkUISync] Client: Applied initial UI state");
        }

        public override void OnNetworkDespawn()
        {
            nvJoinCode.OnValueChanged -= OnJoinCodeNetworkChanged;
            nvCurrentDay.OnValueChanged -= OnDayChanged;
            nvBankedMoney.OnValueChanged -= OnBankedMoneyChanged;
            nvCurrentValueInZone.OnValueChanged -= OnZoneValueChanged;
            nvTargetMoney.OnValueChanged -= OnTargetMoneyChanged;
            nvTimerSeconds.OnValueChanged -= OnTimerChanged;
            nvHudVisible.OnValueChanged -= OnHudVisibilityChanged;
            nvTimerVisible.OnValueChanged -= OnTimerVisibilityChanged;

            base.OnNetworkDespawn();
        }

        // ==================== SERVER SETTERS ====================

        /// <summary>
        /// Server sets the join code and replicates to all clients.
        /// </summary>
        public void ServerSetJoinCode(string code)
        {
            if (!IsServer)
            {
                Debug.LogWarning("[NetworkUISync] ServerSetJoinCode can only be called on server!");
                return;
            }

            nvJoinCode.Value = code ?? "";
            ApplyJoinCodeUI(code);

            if (enableDebugLogs)
                Debug.Log($"[NetworkUISync] Server set join code: {code}");
        }

        /// <summary>
        /// Server updates the current day display.
        /// </summary>
        public void ServerSetDay(int day)
        {
            if (!IsServer) return;
            nvCurrentDay.Value = day;
        }

        /// <summary>
        /// Server updates the banked money display.
        /// </summary>
        public void ServerSetBankedMoney(int amount)
        {
            if (!IsServer) return;
            nvBankedMoney.Value = amount;
        }

        /// <summary>
        /// Server updates the delivery zone quota display.
        /// </summary>
        public void ServerSetDeliveryZoneValue(int currentValue, int target)
        {
            if (!IsServer) return;
            nvCurrentValueInZone.Value = currentValue;
            nvTargetMoney.Value = target;
        }

        /// <summary>
        /// Server updates the timer display.
        /// </summary>
        public void ServerSetTimer(float secondsRemaining)
        {
            if (!IsServer) return;
            nvTimerSeconds.Value = secondsRemaining;
        }

        /// <summary>
        /// Server controls HUD visibility.
        /// </summary>
        public void ServerSetHudVisible(bool visible)
        {
            if (!IsServer) return;
            nvHudVisible.Value = visible;
        }

        /// <summary>
        /// Server controls timer visibility.
        /// </summary>
        public void ServerSetTimerVisible(bool visible)
        {
            if (!IsServer) return;
            nvTimerVisible.Value = visible;
        }

        /// <summary>
        /// Server updates all UI state at once. Call this when major state changes occur.
        /// </summary>
        public void ServerRefreshAllUI(int day, int bankedMoney, int currentValueInZone, int targetMoney, 
            float timerSeconds, bool hudVisible, bool timerVisible)
        {
            if (!IsServer) return;

            nvCurrentDay.Value = day;
            nvBankedMoney.Value = bankedMoney;
            nvCurrentValueInZone.Value = currentValueInZone;
            nvTargetMoney.Value = targetMoney;
            nvTimerSeconds.Value = timerSeconds;
            nvHudVisible.Value = hudVisible;
            nvTimerVisible.Value = timerVisible;

            if (enableDebugLogs)
                Debug.Log($"[NetworkUISync] Server refreshed all UI: Day={day}, Money=${bankedMoney}, Quota=${currentValueInZone}/{targetMoney}, Timer={timerSeconds:F1}s");
        }

        // ==================== NETWORK CALLBACKS ====================

        private void OnJoinCodeNetworkChanged(FixedString128Bytes previousValue, FixedString128Bytes newValue)
        {
            string code = newValue.ToString();
            ApplyJoinCodeUI(code);
            OnJoinCodeChanged?.Invoke(code);

            if (enableDebugLogs)
                Debug.Log($"[NetworkUISync] Join code changed: {code}");
        }

        private void OnDayChanged(int oldValue, int newValue)
        {
            if (gameUIController != null)
            {
                gameUIController.SetDay(newValue);
            }

            OnUIStateChanged?.Invoke();

            if (enableDebugLogs)
                Debug.Log($"[NetworkUISync] Day changed: {oldValue} -> {newValue}");
        }

        private void OnBankedMoneyChanged(int oldValue, int newValue)
        {
            if (gameUIController != null)
            {
                gameUIController.SetBankedMoney(newValue);
            }

            OnUIStateChanged?.Invoke();

            if (enableDebugLogs)
                Debug.Log($"[NetworkUISync] Banked money changed: ${oldValue} -> ${newValue}");
        }

        private void OnZoneValueChanged(int oldValue, int newValue)
        {
            if (gameUIController != null)
            {
                gameUIController.SetDeliveryZoneValue(newValue, nvTargetMoney.Value);
            }

            OnUIStateChanged?.Invoke();
        }

        private void OnTargetMoneyChanged(int oldValue, int newValue)
        {
            if (gameUIController != null)
            {
                gameUIController.SetDeliveryZoneValue(nvCurrentValueInZone.Value, newValue);
            }

            OnUIStateChanged?.Invoke();
        }

        private void OnTimerChanged(float oldValue, float newValue)
        {
            if (gameUIController != null)
            {
                gameUIController.SetTimerSeconds(newValue);
            }
        }

        private void OnHudVisibilityChanged(bool oldValue, bool newValue)
        {
            if (gameUIController != null)
            {
                if (newValue)
                    gameUIController.ShowHUD();
                else
                    gameUIController.HideHUD();
            }

            if (enableDebugLogs)
                Debug.Log($"[NetworkUISync] HUD visibility changed: {newValue}");
        }

        private void OnTimerVisibilityChanged(bool oldValue, bool newValue)
        {
            if (gameUIController != null)
            {
                gameUIController.SetTimerVisible(newValue);
            }
        }

        // ==================== UI APPLICATION ====================

        private void ApplyJoinCodeUI(string code)
        {
            if (joinCodeText != null)
            {
                joinCodeText.text = string.IsNullOrEmpty(code) ? "" : $"CODE: {code.ToUpperInvariant()}";
            }
        }

        private void ApplyAllUIState()
        {
            if (gameUIController == null)
            {
                Debug.LogWarning("[NetworkUISync] Cannot apply UI state - GameUIController is null");
                return;
            }

            // Apply join code
            ApplyJoinCodeUI(nvJoinCode.Value.ToString());

            // Apply all game UI
            gameUIController.SetDay(nvCurrentDay.Value);
            gameUIController.SetBankedMoney(nvBankedMoney.Value);
            gameUIController.SetDeliveryZoneValue(nvCurrentValueInZone.Value, nvTargetMoney.Value);
            gameUIController.SetTimerSeconds(nvTimerSeconds.Value);
            gameUIController.SetTimerVisible(nvTimerVisible.Value);

            if (nvHudVisible.Value)
                gameUIController.ShowHUD();
            else
                gameUIController.HideHUD();

            if (enableDebugLogs)
            {
                Debug.Log($"[NetworkUISync] Applied all UI state: Day={nvCurrentDay.Value}, " +
                         $"Money=${nvBankedMoney.Value}, Quota=${nvCurrentValueInZone.Value}/{nvTargetMoney.Value}, " +
                         $"Timer={nvTimerSeconds.Value:F1}s, HUD={nvHudVisible.Value}");
            }
        }

        /// <summary>
        /// Forces a full UI refresh from current network state.
        /// Useful when UI elements are dynamically created or when recovering from errors.
        /// </summary>
        [ContextMenu("Force UI Sync")]
        public void ForceUISync()
        {
            if (autoFindReferences)
            {
                gameUIController = FindFirstObjectByType<GameUIController>();
            }

            ApplyAllUIState();

            if (enableDebugLogs)
                Debug.Log("[NetworkUISync] Forced UI sync complete");
        }

        /// <summary>
        /// Call this from server when clients connect late to give them current state.
        /// </summary>
        public void ServerSyncNewClient(ulong clientId)
        {
            if (!IsServer) return;

            // Network variables automatically sync on connect, but we can force an RPC update
            SyncClientUIClientRpc(RpcTarget.Single(clientId, RpcTargetUse.Temp));

            if (enableDebugLogs)
                Debug.Log($"[NetworkUISync] Triggered sync for new client {clientId}");
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void SyncClientUIClientRpc(RpcParams rpcParams)
        {
            ForceUISync();
        }

        // ==================== UPDATE LOOP ====================

        private void Update()
        {
            // Server continuously pushes timer updates
            if (IsServer && gameUIController != null)
            {
                // Timer gets updated from GameTimer, but we sync it here for clients
                // This is handled by GameTimer calling ServerSetTimer
            }
        }
    }
}