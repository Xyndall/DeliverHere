using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using DeliverHere.GamePlay;

public class GameManager : NetworkBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("References")]
    [SerializeField] private MoneyTargetManager moneyTargetManager;
    [SerializeField] private GameUIController uiController;
    [SerializeField] private GameManager gameManager;
    [SerializeField] private GameTimer gameTimer;
    [SerializeField] private UIStateManager uiStateManager;

    [SerializeField] private LevelLoader levelLoader;
    [SerializeField] private LevelFlowController levelFlow;

    [Header("Player Spawning")]
    [SerializeField] private bool autoFindSpawnPoints = true;
    [SerializeField] private List<Transform> playerSpawnPoints = new List<Transform>();

    public event Action OnWinCondition;
    private Action<bool> _onDayEndedEvaluatedHandler;
    private Action _onDayTimerAboutToExpireHandler;
    public bool IsGameplayActive { get; private set; }

    // NEW: if true, we ended the day with failure and are waiting for host to close/restart.
    private bool _pendingLossReturnToLobby;

    public void SetGameplayActive(bool active)
    {
        IsGameplayActive = active;
    }

    public IReadOnlyList<Transform> PlayerSpawnPoints => playerSpawnPoints;

    private int lastSpawnedDay = -1;
    private int dailyPackagesDelivered = 0;
    private int currentDayTargetMoney = 0;
    private bool endOfDayPopupShown = false;

    private NetworkGameState _netState;
    private bool IsServerOrStandalone =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    // -------------------- Client -> Server money routing --------------------

    public void AddMoney(int amount)
    {
        if (moneyTargetManager == null) return;

        if (IsServerOrStandalone)
        {
            moneyTargetManager.AddMoney(amount);
            return;
        }

        RequestAddMoneyServerRpc(amount);
    }

    public void RemoveMoney(int amount)
    {
        if (moneyTargetManager == null) return;

        if (IsServerOrStandalone)
        {
            moneyTargetManager.RemoveMoney(amount);
            return;
        }

        RequestRemoveMoneyServerRpc(amount);
    }

    public bool SpendBanked(int amount)
    {
        if (moneyTargetManager == null) return false;

        if (IsServerOrStandalone)
        {
            return moneyTargetManager.SpendBanked(amount);
        }

        // Client can't know the result synchronously; treat as "requested".
        RequestSpendBankedServerRpc(amount);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestAddMoneyServerRpc(int amount)
    {
        if (moneyTargetManager == null) return;
        moneyTargetManager.AddMoney(amount);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestRemoveMoneyServerRpc(int amount)
    {
        if (moneyTargetManager == null) return;
        moneyTargetManager.RemoveMoney(amount);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestSpendBankedServerRpc(int amount)
    {
        if (moneyTargetManager == null) return;
        moneyTargetManager.SpendBanked(amount);
    }

    // ----------------------------------------------------------------------

    private void OnEnable()
    {
        SubscribeToMoneyTarget();
        FindTimerAndNetState();
        SyncAllUI();

        IsGameplayActive = false;

        uiController?.HideHUD();
        uiController?.ConfigureDayEndButtons(HostAdvanceToNextDay, HostRestartRun);
    }

    private void OnDisable()
    {
        UnsubscribeFromMoneyTarget();
        UnsubscribeTimer();
    }

    private void FindTimerAndNetState()
    {
        _netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
        var timer = gameTimer != null ? gameTimer : FindFirstObjectByType<GameTimer>();
        if (timer != null)
        {
            UnsubscribeTimer();

            _onDayTimerAboutToExpireHandler = () => { /* optional */ };
            _onDayEndedEvaluatedHandler = (metQuota) =>
            {
                int earnedToday = GetEarnedToday();
                int bankedNow = GetBankedMoney();
                bool hostHasControl = NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

                IsGameplayActive = false;

                // We are showing the end-of-day overlay; if it's a loss, we wait for host action
                _pendingLossReturnToLobby = !metQuota;

                // Put UI into a non-gameplay state so HUD/pause logic won't fight the panel
                if (IsServerOrStandalone && _netState != null)
                {
                    _netState.ServerSetGameState(GameState.GameOver, paused: false);
                }

                // Show summary for BOTH success and failure, for ALL clients.
                if (IsServerOrStandalone && _netState != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                {
                    _netState.ShowDayEndSummaryClientRpc(earnedToday, bankedNow, dailyPackagesDelivered, metQuota);
                }
                else
                {
                    uiController?.ShowDayEndSummary(earnedToday, bankedNow, dailyPackagesDelivered, metQuota, hostHasControl);
                }

                endOfDayPopupShown = true;
            };

            timer.OnDayTimerAboutToExpire += _onDayTimerAboutToExpireHandler;
            timer.OnDayEndedEvaluated += _onDayEndedEvaluatedHandler;
        }

        SyncAllUI();
    }

    private void UnsubscribeTimer()
    {
        var timer = gameTimer != null ? gameTimer : FindFirstObjectByType<GameTimer>();
        if (timer != null)
        {
            if (_onDayEndedEvaluatedHandler != null)
                timer.OnDayEndedEvaluated -= _onDayEndedEvaluatedHandler;
            if (_onDayTimerAboutToExpireHandler != null)
                timer.OnDayTimerAboutToExpire -= _onDayTimerAboutToExpireHandler;
            _onDayEndedEvaluatedHandler = null;
            _onDayTimerAboutToExpireHandler = null;
        }
    }

    private void EnsureSpawnPointReferences()
    {
        if (!autoFindSpawnPoints) return;
        playerSpawnPoints.Clear();

        try
        {
            var tagged = GameObject.FindGameObjectsWithTag("PlayerSpawn");
            foreach (var go in tagged)
            {
                if (go != null && go.activeInHierarchy)
                    playerSpawnPoints.Add(go.transform);
            }
        }
        catch { }

        if (playerSpawnPoints.Count == 0)
        {
            var allTransforms = FindObjectsByType<Transform>(FindObjectsSortMode.None);
            foreach (var t in allTransforms)
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                if (t.name.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("SpawnPoint", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    playerSpawnPoints.Add(t);
                }
            }
        }

        if (playerSpawnPoints.Count == 0)
            playerSpawnPoints.Add(this.transform);
    }

    /// <summary>
    /// Returns a random active player spawn point; falls back to GameManager transform if none.
    /// </summary>
    public Transform GetRandomPlayerSpawn()
    {
        EnsureSpawnPointReferences();
        if (playerSpawnPoints == null || playerSpawnPoints.Count == 0)
            return this.transform;

        // Choose a random active one if possible
        var active = new List<Transform>();
        foreach (var t in playerSpawnPoints)
        {
            if (t != null && t.gameObject.activeInHierarchy)
                active.Add(t);
        }

        var source = active.Count > 0 ? active : playerSpawnPoints;
        int idx = UnityEngine.Random.Range(0, source.Count);
        return source[idx] ?? this.transform;
    }

    private void SubscribeToMoneyTarget()
    {
        if (moneyTargetManager == null) return;
        moneyTargetManager.OnTargetChanged += HandleTargetChanged;
        moneyTargetManager.OnDayAdvanced += HandleDayAdvanced;
        moneyTargetManager.OnTargetReached += HandleTargetReached;
        currentDayTargetMoney = moneyTargetManager.TargetMoney;
    }

    private void UnsubscribeFromMoneyTarget()
    {
        if (moneyTargetManager == null) return;
        moneyTargetManager.OnTargetChanged -= HandleTargetChanged;
        moneyTargetManager.OnDayAdvanced -= HandleDayAdvanced;
        moneyTargetManager.OnTargetReached -= HandleTargetReached;
    }

    private void SyncAllUI()
    {
        if (uiController == null || moneyTargetManager == null) return;
        uiController.SetDay(GetCurrentDay());
        uiController.SetBankedMoney(GetBankedMoney());
        
        // Initial delivery zone value (will be 0 at start)
        OnDeliveryZoneValueChanged(0, GetTargetMoney()); // FIXED: Added quota parameter
    }

    public void StartGame()
    {
        EnsureSpawnPointReferences();
        FindTimerAndNetState();

        IsGameplayActive = true;

        if (IsServerOrStandalone)
        {
            if (_netState != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                // NEW: end Loading for everyone when we actually start gameplay
                _netState.ServerSetGameState(GameState.InGame);

                _netState.BeginClientReadyHandshake(() =>
                {
                    PositionPlayersToSpawnPoints();
                }, timeoutSeconds: 5f);
            }
            else
            {
                PositionPlayersToSpawnPoints();
            }
        }

        // CHANGED: Use StartNewDay instead of AdvanceDay for proper tracking
        if (moneyTargetManager != null && moneyTargetManager.CurrentDay <= 0 && IsServerOrStandalone)
            moneyTargetManager.StartNewDay();

        dailyPackagesDelivered = 0;
        currentDayTargetMoney = GetTargetMoney();
        endOfDayPopupShown = false;

        SyncAllUI();

        // Leaving Loading -> entering InGame
        if (uiStateManager != null)
            uiStateManager.SetGameState(GameState.InGame);

        uiController.HideWinPanel();
        uiController.HideDayEndSummary();
        uiController.ShowHUD();
    }

    public void PositionPlayersToSpawnPoints()
    {
        EnsureSpawnPointReferences();
        if (playerSpawnPoints == null || playerSpawnPoints.Count == 0) return;

        var players = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
        if (players == null || players.Length == 0) return;

        int spawnIndex = 0;
        foreach (var pm in players)
        {
            if (pm == null) continue;

            var netObj = pm.GetComponent<NetworkObject>();
            if (netObj == null || !netObj.IsSpawned) continue;

            var spawn = playerSpawnPoints[spawnIndex % playerSpawnPoints.Count];
            spawnIndex++;

            var cc = pm.GetComponent<CharacterController>();
            var rb = pm.GetComponent<Rigidbody>();

            if (cc != null) cc.enabled = false;
            pm.transform.SetPositionAndRotation(spawn.position, spawn.rotation);

            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            // Reset vertical velocity to prevent drift
            pm.ResetMovementState();

            if (cc != null) cc.enabled = true;

            _netState?.ServerRequestOwnerTeleport(netObj, spawn.position, spawn.rotation);
        }

        Debug.Log($"[GameManager] Positioned {players.Length} players to spawn points.");
    }

    public void EndGame()
    {
        IsGameplayActive = false;
        lastSpawnedDay = -1;
        ResetRun();
        dailyPackagesDelivered = 0;
        endOfDayPopupShown = false;

        // High-level UI state: game is over / not in active gameplay
        if (uiStateManager != null)
            uiStateManager.SetGameState(GameState.Lobby);

        // HUD details
        uiController.HideWinPanel();
        uiController.HideDayEndSummary();
        uiController.HideHUD();
    }

    public void RegisterPackagesDelivered(int count)
    {
        if (count <= 0) return;
        dailyPackagesDelivered += count;
    }

    public void HostAdvanceToNextDay()
    {
        if (!IsServerOrStandalone) return;

        // If the day ended in failure, "Next Day" should not do anything.
        if (_pendingLossReturnToLobby)
            return;

        uiController.HideDayEndSummary();
        endOfDayPopupShown = false;

        // Use StartNewDay explicitly instead of AdvanceDay
        moneyTargetManager?.StartNewDay();
    }

    public void HostRestartRun()
    {
        if (!IsServerOrStandalone) return;

        // If we lost, Restart should return everyone to lobby (end game).
        if (_pendingLossReturnToLobby)
        {
            _pendingLossReturnToLobby = false;
            TriggerLoseCondition();
            return;
        }

        // Otherwise it's a normal "restart run" behavior on success.
        uiController.HideDayEndSummary();
        ResetRun();
        dailyPackagesDelivered = 0;
        lastSpawnedDay = -1;
        endOfDayPopupShown = false;
        moneyTargetManager?.StartNewDay();
    }

    public void AdvanceDay() => moneyTargetManager?.StartNewDay();
    public void AdvanceDays(int days) => moneyTargetManager?.AdvanceDays(days);

    public void ResetRun()
    {
        moneyTargetManager?.ResetProgress();
        currentDayTargetMoney = GetTargetMoney();
    }

    public int GetBankedMoney() => moneyTargetManager != null ? moneyTargetManager.BankedMoney : 0;
    public int GetTargetMoney() => moneyTargetManager != null ? moneyTargetManager.TargetMoney : 0;
    public int GetCurrentDay() => moneyTargetManager != null ? moneyTargetManager.CurrentDay : 0;
    public int GetEarnedToday() => moneyTargetManager != null ? moneyTargetManager.EarnedToday : 0;
    public float GetProgress() => moneyTargetManager != null ? moneyTargetManager.Progress : 0f;

    private void HandleTargetChanged(int newTarget)
    {
        currentDayTargetMoney = newTarget;
        // Update the delivery zone display with new target
        OnDeliveryZoneValueChanged(0, newTarget); // FIXED: Added quota parameter
    }

    private void HandleDayAdvanced(int newDayIndex)
    {
        uiController?.SetDay(newDayIndex);
        currentDayTargetMoney = GetTargetMoney();
        dailyPackagesDelivered = 0;
        endOfDayPopupShown = false;
        _pendingLossReturnToLobby = false;

        IsGameplayActive = true;

        // New day = back to InGame state
        if (uiStateManager != null)
            uiStateManager.SetGameState(GameState.InGame);

        uiController?.HideDayEndSummary();
        uiController?.ShowHUD();
        
        // Reset delivery zone value display
        OnDeliveryZoneValueChanged(0, GetTargetMoney()); // FIXED: Added quota parameter
    }

    private void HandleTargetReached()
    {
        TriggerWinCondition();
    }

    private void TriggerWinCondition()
    {
        OnWinCondition?.Invoke();
        uiController?.ShowWinPanel();
        Debug.Log("[GameManager] Win condition met.");
    }

    private void TriggerLoseCondition()
    {
        Debug.Log("[GameManager] Lose condition triggered – quota not met. Performing full reset and returning players to hub.");

        IsGameplayActive = false;
        endOfDayPopupShown = false;
        dailyPackagesDelivered = 0;
        lastSpawnedDay = -1;
        _pendingLossReturnToLobby = false;

        if (moneyTargetManager != null)
        {
            moneyTargetManager.ResetProgress();
            currentDayTargetMoney = GetTargetMoney();

            uiController?.SetDay(GetCurrentDay());
            uiController?.SetBankedMoney(GetBankedMoney());
            OnDeliveryZoneValueChanged(0, GetTargetMoney()); // FIXED: Added quota parameter
        }

        if (_netState == null)
            _netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();

        if (IsServerOrStandalone && _netState != null)
        {
            try
            {
                _netState.RequestEndGameServerRpc();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        uiController?.HideHUD();
        uiController?.HideDayEndSummary();
        uiController?.HideWinPanel();

        if (!IsServerOrStandalone) return;

        if (levelFlow != null)
        {
            levelFlow.ReturnPlayersToHub();
            return;
        }

        if (levelLoader != null)
        {
            void OnUnloaded(string sceneName)
            {
                levelLoader.OnLevelUnloaded -= OnUnloaded;
                PositionPlayersToSpawnPoints();
            }
            levelLoader.OnLevelUnloaded += OnUnloaded;
            levelLoader.UnloadCurrentLevel();
        }
        else
        {
            PositionPlayersToSpawnPoints();
        }
    }

    public void SetLevelLoader(LevelLoader loader) => levelLoader = loader;
    public void SetLevelFlow(LevelFlowController flow) => levelFlow = flow;

    public void SetUIController(GameUIController controller)
    {
        uiController = controller;
        uiController?.ConfigureDayEndButtons(HostAdvanceToNextDay, HostRestartRun);
        SyncAllUI();
    }

    public void SetMoneyTargetManager(MoneyTargetManager manager)
    {
        if (moneyTargetManager == manager) return;
        UnsubscribeFromMoneyTarget();
        moneyTargetManager = manager;
        SubscribeToMoneyTarget();
        SyncAllUI();
    }

    public void OnDeliveryZoneValueChanged(int totalValue, int quota)
    {
        if (uiController != null)
        {
            uiController.SetDeliveryZoneValue(totalValue, quota);
        }
    }
}