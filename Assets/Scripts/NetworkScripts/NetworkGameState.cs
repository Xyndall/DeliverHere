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

    [Header("Default / Hub Spawn (NOT level spawns)")]
    [Tooltip("Where players are positioned when returning to lobby / on loss. Server-authoritative.")]
    [SerializeField] private Transform defaultSpawnPoint;

    public Transform DefaultSpawnPoint => defaultSpawnPoint;

    public event Action<bool> OnGameStartedChangedEvent;

    // Local state exposure for client-side systems (input, cameras, etc.)
    public event Action<GameState> OnLocalGameStateChanged;
    public GameState LocalGameState => nvGameState.Value;

    private readonly NetworkVariable<bool> gameStarted = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Replicate high-level UI/game flow state to all clients
    private readonly NetworkVariable<GameState> nvGameState = new NetworkVariable<GameState>(
        GameState.MainMenu, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Replicate pause flag
    private readonly NetworkVariable<bool> nvPaused = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Keep day + timer replicated here (money is replicated by MoneyTargetManager)
    private readonly NetworkVariable<int> nvCurrentDay = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

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

    /// <summary>
    /// Server-authoritative: teleports all spawned players to the configured default spawn point.
    /// Intended for "return to lobby" / loss flow. Does not use GameManager spawn points.
    /// </summary>
    public void ServerPositionAllPlayersToDefaultSpawn()
    {
        if (!IsServer) return;

        if (defaultSpawnPoint == null)
        {
            Debug.LogWarning("[NetworkGameState] DefaultSpawnPoint is not assigned; cannot reposition players.");
            return;
        }

        var players = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
        foreach (var pm in players)
        {
            if (pm == null) continue;

            var netObj = pm.GetComponent<NetworkObject>();
            if (netObj == null || !netObj.IsSpawned) continue;

            ServerRequestOwnerTeleport(netObj, defaultSpawnPoint.position, defaultSpawnPoint.rotation);
        }

        Debug.Log($"[NetworkGameState] Teleported players to default spawn '{defaultSpawnPoint.name}'.");
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // Money UI is driven by MoneyTargetManager NetworkVariables + events (Option B)
        BindMoneyUi();

        // Day UI
        nvCurrentDay.OnValueChanged += (_, v) =>
        {
            uiController?.SetDay(v);

            if (moneyTargetManager != null && uiController != null)
            {
                uiController.SetDailyEarnings(
                    moneyTargetManager.BankedMoney,
                    moneyTargetManager.TargetMoney,
                    moneyTargetManager.Progress);
            }
        };

        // Drive UIStateManager from replicated state
        nvGameState.OnValueChanged += (_, s) =>
        {
            ApplyUiStateToLocal(s, nvPaused.Value);
            OnLocalGameStateChanged?.Invoke(s);
        };
        nvPaused.OnValueChanged += (_, p) => ApplyUiStateToLocal(nvGameState.Value, p);

        // Toggle HUD locally on clients when gameStarted changes
        gameStarted.OnValueChanged += (_, started) =>
        {
            OnGameStartedChangedEvent?.Invoke(started);
            if (!IsServer)
            {
                if (started)
                    GameManager.Instance?.StartGame();
                else
                    GameManager.Instance?.EndGame();
            }
        };

        // Push initial local state event so systems see current state on spawn
        OnLocalGameStateChanged?.Invoke(nvGameState.Value);

        // Client UI initial push
        if (!IsServer)
        {
            ApplyAllToClientUI();
            ApplyUiStateToLocal(nvGameState.Value, nvPaused.Value);
        }
        else
        {
            ServerPushSnapshot();
        }
    }

    public override void OnNetworkDespawn()
    {
        try
        {
            if (moneyTargetManager != null)
            {
                moneyTargetManager.OnBankedMoneyChanged -= OnBankedMoneyChangedUi;
                moneyTargetManager.OnTargetChanged -= OnTargetChangedUi;
            }
        }
        finally
        {
            base.OnNetworkDespawn();
        }
    }

    private void BindMoneyUi()
    {
        if (uiController == null)
            uiController = FindFirstObjectByType<GameUIController>();

        if (moneyTargetManager == null)
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();

        if (uiController == null || moneyTargetManager == null)
            return;

        // Avoid duplicate subscription
        moneyTargetManager.OnBankedMoneyChanged -= OnBankedMoneyChangedUi;
        moneyTargetManager.OnTargetChanged -= OnTargetChangedUi;

        moneyTargetManager.OnBankedMoneyChanged += OnBankedMoneyChangedUi;
        moneyTargetManager.OnTargetChanged += OnTargetChangedUi;

        // Initial push
        uiController.SetDay(moneyTargetManager.CurrentDay);
        uiController.SetTarget(moneyTargetManager.TargetMoney);
        uiController.SetDailyEarnings(moneyTargetManager.BankedMoney, moneyTargetManager.TargetMoney, moneyTargetManager.Progress);
        uiController.SetBankedMoney(moneyTargetManager.BankedMoney);
    }

    private void OnBankedMoneyChangedUi(int banked)
    {
        if (uiController == null || moneyTargetManager == null) return;
        uiController.SetBankedMoney(banked);
        uiController.SetDailyEarnings(banked, moneyTargetManager.TargetMoney, moneyTargetManager.Progress);
    }

    private void OnTargetChangedUi(int target)
    {
        if (uiController == null || moneyTargetManager == null) return;
        uiController.SetTarget(target);
        uiController.SetDailyEarnings(moneyTargetManager.BankedMoney, target, moneyTargetManager.Progress);
    }

    private void ApplyUiStateToLocal(GameState state, bool paused)
    {
        if (uiState == null)
            return;

        uiState.SetGameState(state);
        uiState.SetPaused(paused);
    }

    private void Update()
    {
        if (!IsServer) return;

        if (moneyTargetManager == null)
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();

        nvCurrentDay.Value = moneyTargetManager != null ? moneyTargetManager.CurrentDay : nvCurrentDay.Value;

        float timerNorm = 0f;
        var timer = FindFirstObjectByType<GameTimer>();
        if (timer != null) timerNorm = timer.Normalized;
        nvTimerProgress.Value = timerNorm;
    }

    private void ServerPushSnapshot()
    {
        if (!IsServer) return;

        if (moneyTargetManager == null)
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();

        nvCurrentDay.Value = moneyTargetManager != null ? moneyTargetManager.CurrentDay : 0;

        float timerNorm = 0f;
        var timer = FindFirstObjectByType<GameTimer>();
        if (timer != null) timerNorm = timer.Normalized;
        nvTimerProgress.Value = timerNorm;
    }

    private void ApplyAllToClientUI()
    {
        BindMoneyUi();
        uiController?.SetDay(nvCurrentDay.Value);
        uiController?.SetTimerVisible(true);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void ClientSceneLoadedServerRpc(RpcParams rpcParams = default)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return;

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
            callback?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }

        Debug.Log($"[NetworkGameState] Client-ready handshake completed. allClientsReported={allClientsReported}");
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void RequestStartGameServerRpc()
    {
        if (gameStarted.Value) return;

        nvPaused.Value = false;
        nvGameState.Value = GameState.Loading;

        gameStarted.Value = true;

        levelFlow.StartLoadNextLevel();

        ServerPushSnapshot();
        HideDayEndSummaryClientRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void RequestEndGameServerRpc()
    {
        if (!gameStarted.Value) return;

        gameStarted.Value = false;

        nvPaused.Value = false;
        nvGameState.Value = GameState.Lobby;

        var players = FindObjectsByType<PlayerUpgradableStats>(FindObjectsSortMode.None);
        foreach (var stats in players)
        {
            if (stats == null) continue;
            if (!stats.IsSpawned) continue;
            stats.ServerResetToDefaults();
        }

        PlayerUpgradableStats.ServerClearAllSnapshots();

        gameManager?.EndGame();
        ServerPushSnapshot();
        HideDayEndSummaryClientRpc();
        ServerPositionAllPlayersToDefaultSpawn();
    }

    [ClientRpc]
    public void ShowDayEndSummaryClientRpc(int earnedToday, int newBankedTotal, int packagesDelivered, bool metQuota)
    {
        bool hostHasControl = NetworkManager.Singleton == null || IsServer;
        uiController.ShowDayEndSummary(earnedToday, newBankedTotal, packagesDelivered, metQuota, hostHasControl);
    }

    [ClientRpc]
    public void HideDayEndSummaryClientRpc()
    {
        uiController?.HideDayEndSummary();
    }

    [ClientRpc]
    private void ApplyPlayerTeleportClientRpc(ulong networkObjectId, Vector3 position, Quaternion rotation, ClientRpcParams clientRpcParams = default)
    {
        NetworkObject netObj = null;

        var spawnMgr = NetworkManager.Singleton?.SpawnManager;
        if (spawnMgr != null)
        {
            spawnMgr.SpawnedObjects.TryGetValue(networkObjectId, out netObj);
        }

        if (netObj == null)
        {
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

    public void ServerSetGameState(GameState state, bool paused = false)
    {
        if (!IsServer) return;
        nvPaused.Value = paused;
        nvGameState.Value = state;
    }
}