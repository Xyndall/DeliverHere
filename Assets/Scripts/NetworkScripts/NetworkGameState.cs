using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public enum GameState
{
    MainMenu,
    Lobby,
    Loading,
    InGame,
    Paused,
    GameOver
}

public class NetworkGameState : NetworkBehaviour
{
    public static NetworkGameState Instance { get; private set; }

    [Header("References")]
    [SerializeField] private GameManager gameManager;
    [SerializeField] private MoneyTargetManager moneyTargetManager;
    [SerializeField] private GameUIController uiController;
    [SerializeField] private LevelFlowController levelFlow;
    [SerializeField] private UIStateManager uiState;

    public event Action<bool> OnGameStartedChangedEvent;

    private readonly NetworkVariable<bool> gameStarted = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<int> nvCurrentMoney = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> nvBankedMoney = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> nvTargetMoney = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> nvCurrentDay = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<float> nvProgress = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<float> nvTimerProgress = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Client-ready handshake (server only)
    private HashSet<ulong> _clientsAwaiting;
    private Coroutine _clientReadyCoroutine;
    private Action _onAllClientsReadyCallback;

    public bool GameStarted => gameStarted.Value;
    public int CurrentDay => nvCurrentDay.Value;
    public float TimerProgress => nvTimerProgress.Value;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {

        // Bind UI updates on clients
        nvCurrentMoney.OnValueChanged += (_, v) => uiController.SetDailyEarnings(v, nvTargetMoney.Value, nvProgress.Value);
        nvBankedMoney.OnValueChanged += (_, v) => uiController.SetBankedMoney(v);
        nvTargetMoney.OnValueChanged += (_, v) =>
        {
            uiController.SetTarget(v);
            uiController.SetDailyEarnings(nvCurrentMoney.Value, v, nvProgress.Value);
        };
        nvCurrentDay.OnValueChanged += (_, v) =>
        {
            uiController.SetDay(v);
            uiController.SetDailyEarnings(nvCurrentMoney.Value, nvTargetMoney.Value, nvProgress.Value);
        };
        nvProgress.OnValueChanged += (_, v) =>
        {
            uiController.SetDailyEarnings(nvCurrentMoney.Value, nvTargetMoney.Value, v);
        };

        // NEW: toggle HUD locally on clients when gameStarted changes
        gameStarted.OnValueChanged += (_, started) =>
        {
            OnGameStartedChangedEvent?.Invoke(started);
            if (!IsServer)
            {
                if (started)
                {
                    GameManager.Instance?.StartGame();    // shows HUD on clients
                }
                else
                {
                    GameManager.Instance?.EndGame();      // hides HUD on clients
                }
            }
        };

        // Push initial snapshot
        if (IsServer)
        {
            ServerPushSnapshot();
        }
        else
        {
            ApplyAllToClientUI();
            // Ensure correct HUD visibility for late-joiners
            if (gameStarted.Value)
                GameManager.Instance?.StartGame();
            else
                GameManager.Instance?.EndGame();
        }
    }


    private void Update()
    {
        if (!IsServer) return;

        // Keep server-side state synced to clients
        if (moneyTargetManager == null)
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();

        nvCurrentMoney.Value = moneyTargetManager != null ? moneyTargetManager.CurrentMoney : nvCurrentMoney.Value;
        nvBankedMoney.Value = moneyTargetManager != null ? moneyTargetManager.BankedMoney : nvBankedMoney.Value;
        nvTargetMoney.Value = moneyTargetManager != null ? moneyTargetManager.TargetMoney : nvTargetMoney.Value;
        nvCurrentDay.Value = moneyTargetManager != null ? moneyTargetManager.CurrentDay : nvCurrentDay.Value;
        nvProgress.Value = moneyTargetManager != null ? moneyTargetManager.Progress : nvProgress.Value;

        // Timer progress from GameTimer
        float timerNorm = 0f;
        var timer = FindFirstObjectByType<GameTimer>();
        if (timer != null) timerNorm = timer.Normalized;
        nvTimerProgress.Value = timerNorm;
    }

    // Client -> Server: scene-loaded handshake
    [ServerRpc(RequireOwnership = false)]
    public void ClientSceneLoadedServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;
        var clientId = rpcParams.Receive.SenderClientId;
        if (_clientsAwaiting == null) return;

        if (_clientsAwaiting.Remove(clientId))
        {
            Debug.Log($"[NetworkGameState] Client {clientId} reported scene loaded. Remaining: {_clientsAwaiting.Count}");
            if (_clientsAwaiting.Count == 0)
                CompleteClientReadyHandshake(allClientsReported: true);
        }
    }

    public void BeginClientReadyHandshake(Action onAllReadyCallback, float timeoutSeconds)
    {
        if (!IsServer) return;

        if (_clientReadyCoroutine != null)
        {
            StopCoroutine(_clientReadyCoroutine);
            _clientReadyCoroutine = null;
        }

        _clientsAwaiting = new HashSet<ulong>();
        foreach (var id in NetworkManager.Singleton.ConnectedClientsIds)
        {
            if (id == NetworkManager.ServerClientId) continue;
            _clientsAwaiting.Add(id);
        }

        _onAllClientsReadyCallback = onAllReadyCallback;

        if (_clientsAwaiting.Count == 0)
        {
            Debug.Log("[NetworkGameState] No remote clients to wait for; completing handshake immediately.");
            CompleteClientReadyHandshake(allClientsReported: true);
            return;
        }

        _clientReadyCoroutine = StartCoroutine(ClientReadyTimeoutCoroutine(timeoutSeconds));
        Debug.Log($"[NetworkGameState] Waiting for {_clientsAwaiting.Count} clients to report ready (timeout {timeoutSeconds}s).");
    }

    private IEnumerator ClientReadyTimeoutCoroutine(float timeoutSeconds)
    {
        float deadline = Time.realtimeSinceStartup + Mathf.Max(0.01f, timeoutSeconds);
        while (Time.realtimeSinceStartup < deadline && _clientsAwaiting != null && _clientsAwaiting.Count > 0)
            yield return null;

        if (_clientsAwaiting != null && _clientsAwaiting.Count > 0)
        {
            Debug.LogWarning($"[NetworkGameState] Client-ready handshake timed out. Missing: {string.Join(", ", _clientsAwaiting)}");
        }

        CompleteClientReadyHandshake(allClientsReported: _clientsAwaiting == null || _clientsAwaiting.Count == 0);
    }

    private void CompleteClientReadyHandshake(bool allClientsReported)
    {
        if (_clientReadyCoroutine != null)
        {
            StopCoroutine(_clientReadyCoroutine);
            _clientReadyCoroutine = null;
        }

        _clientsAwaiting = null;

        var callback = _onAllClientsReadyCallback;
        _onAllClientsReadyCallback = null;

        try
        {
            callback.Invoke();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }

        Debug.Log($"[NetworkGameState] Client-ready handshake completed. allClientsReported={allClientsReported}");
    }

    // Start/End game requests
    [ServerRpc(RequireOwnership = false)]
    public void RequestStartGameServerRpc()
    {
        if (gameStarted.Value) return;

        // Tell UIStateManager we are entering the Loading state (server/host)
        if (uiState != null)
        {
            uiState.SetGameState(GameState.Loading);
        }

        gameStarted.Value = true;

        levelFlow.StartLoadNextLevel();

        ServerPushSnapshot();
        HideDayEndSummaryClientRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestEndGameServerRpc()
    {
        if (!gameStarted.Value) return;
        gameStarted.Value = false;
        gameManager?.EndGame();
        ServerPushSnapshot();
        HideDayEndSummaryClientRpc();
    }

    // Snapshot helpers
    private void ServerPushSnapshot()
    {
        if (moneyTargetManager == null)
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();

        nvCurrentMoney.Value = moneyTargetManager != null ? moneyTargetManager.CurrentMoney : 0;
        nvBankedMoney.Value = moneyTargetManager != null ? moneyTargetManager.BankedMoney : 0;
        nvTargetMoney.Value = moneyTargetManager != null ? moneyTargetManager.TargetMoney : 0;
        nvCurrentDay.Value = moneyTargetManager != null ? moneyTargetManager.CurrentDay : 0;
        nvProgress.Value = moneyTargetManager != null ? moneyTargetManager.Progress : 0f;

        float timerNorm = 0f;
        var timer = FindFirstObjectByType<GameTimer>();
        if (timer != null) timerNorm = timer.Normalized;
        nvTimerProgress.Value = timerNorm;
    }


    private void ApplyAllToClientUI()
    {
        uiController.SetDay(nvCurrentDay.Value);
        uiController.SetTarget(nvTargetMoney.Value);
        uiController.SetDailyEarnings(nvCurrentMoney.Value, nvTargetMoney.Value, nvProgress.Value);
        uiController.SetBankedMoney(nvBankedMoney.Value);
        // Keep timer visible; GameTimer will drive seconds on clients
        uiController.SetTimerVisible(true);
    }

    // Optional: UI summary RPCs (kept for compatibility)
    [ClientRpc]
    public void ShowDayEndSummaryClientRpc(int earnedToday, int newBankedTotal, int packagesDelivered, bool metQuota)
    {
        bool hostHasControl = NetworkManager.Singleton == null || IsServer;
        uiController.ShowDayEndSummary(earnedToday, newBankedTotal, packagesDelivered, metQuota, hostHasControl);
    }

    [ClientRpc]
    public void HideDayEndSummaryClientRpc()
    {
        uiController.HideDayEndSummary();
    }

    [ClientRpc]
    private void ApplyPlayerTeleportClientRpc(ulong networkObjectId, Vector3 position, Quaternion rotation, ClientRpcParams clientRpcParams = default)
    {
        NetworkObject netObj = null;

        var spawnMgr = NetworkManager.Singleton?.SpawnManager;
        if (spawnMgr != null)
        {
            // Current NGO exposes a dictionary of spawned objects keyed by NetworkObjectId
            spawnMgr.SpawnedObjects.TryGetValue(networkObjectId, out netObj);
        }

        if (netObj == null)
        {
            // Fallback: slow, but safe if called rarely
            foreach (var candidate in FindObjectsByType<NetworkObject>(FindObjectsSortMode.None))
            {
                if (candidate.NetworkObjectId == networkObjectId)
                {
                    netObj = candidate;
                    break;
                }
            }
        }

        if (netObj == null) return;

        var pm = netObj.GetComponent<PlayerMovement>();
        if (pm == null) return;

        var cc = pm.GetComponent<CharacterController>();
        var rb = pm.GetComponent<Rigidbody>();

        if (cc != null) cc.enabled = false;
        pm.transform.SetPositionAndRotation(position, rotation);
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        if (cc != null) cc.enabled = true;
    }

    // SERVER: request the owning client to apply the move locally (owner-authoritative NetworkTransform).
    public void ServerRequestOwnerTeleport(NetworkObject playerNetObj, Vector3 position, Quaternion rotation)
    {
        if (!IsServer || playerNetObj == null || !playerNetObj.IsSpawned) return;

        var ownerId = playerNetObj.OwnerClientId;
        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { ownerId }
            }
        };

        ApplyPlayerTeleportClientRpc(playerNetObj.NetworkObjectId, position, rotation, rpcParams);
    }
}