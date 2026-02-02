using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using DeliverHere.GamePlay;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Lifecycle")]
    [SerializeField] private bool dontDestroyOnLoad = true;

    [Header("References")]
    [SerializeField] private MoneyTargetManager moneyTargetManager;
    [SerializeField] private bool autoFindMoneyTargetManager = true;
    [SerializeField] private GameUIController uiController;
    [SerializeField] private bool autoFindUIController = true;
    [SerializeField] private GameManager gameManager;
    [SerializeField] private GameTimer gameTimer;

    [Header("Package Spawning")]
    [SerializeField] private List<PackageSpawner> packageSpawners = new List<PackageSpawner>();

    [Header("Spawn Safety")]
    [SerializeField] private bool preventDuplicateSpawnsPerDay = true;

    [Header("Player Spawning")]
    [SerializeField] private bool autoFindSpawnPoints = true;
    [SerializeField] private List<Transform> playerSpawnPoints = new List<Transform>();

    public event Action OnWinCondition;
    public bool IsGameplayActive { get; private set; }

    // Expose spawn points read-only to other components.
    public IReadOnlyList<Transform> PlayerSpawnPoints => playerSpawnPoints;

    private InputSystem_Actions _input;
    private InputAction _pauseAction;

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
        if (dontDestroyOnLoad)
            DontDestroyOnLoad(gameObject);

        EnsureMoneyTargetReference();
        EnsureUIReference();
        EnsureSpawnPointReferences();
    }

    private void OnEnable()
    {
        SubscribeToMoneyTarget();
        FindTimerAndNetState();
        SyncAllUI();

        IsGameplayActive = false;

        if (_input == null)
            _input = new InputSystem_Actions();

        _pauseAction = _input.Player.Pause;
        _pauseAction.performed += OnPausePerformed;

        _input.Enable();
        _pauseAction.Enable();

        uiController?.HideHUD();
        uiController?.ConfigureDayEndButtons(HostAdvanceToNextDay, HostRestartRun);
    }

    private void OnDisable()
    {
        UnsubscribeFromMoneyTarget();
        UnsubscribeTimer();

        if (_pauseAction != null)
            _pauseAction.performed -= OnPausePerformed;

        _input?.Disable();
        _input?.Dispose();
        _input = null;
        _pauseAction = null;
    }

    // Find timer and net state
    private void FindTimerAndNetState()
    {
        _netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
        var timer = FindFirstObjectByType<GameTimer>();

        if (timer != null)
        {
            // Prevent duplicate subscription
            UnsubscribeTimer();

            timer.OnDayTimerAboutToExpire += () =>
            {
                // Pre-expiration hook if needed
            };

            timer.OnDayEndedEvaluated += (metQuota) =>
            {
                // Show summary popup locally
                int earnedToday = GetCurrentMoney();
                int newBanked = GetBankedMoney();
                bool hostHasControl = NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;
                uiController?.ShowDayEndSummary(earnedToday, newBanked, dailyPackagesDelivered, metQuota, hostHasControl);

                endOfDayPopupShown = true;
                IsGameplayActive = false;
            };
        }

        SyncAllUI();
    }

    private void UnsubscribeTimer()
    {
        var timer = FindFirstObjectByType<GameTimer>();
        if (timer != null)
        {
            // Note: to safely unsubscribe, store delegates if you add named handlers.
            timer.OnDayEndedEvaluated -= (bool _) => { };
            timer.OnDayTimerAboutToExpire -= () => { };
        }
    }

    private void EnsureMoneyTargetReference()
    {
        if (moneyTargetManager == null && autoFindMoneyTargetManager)
            moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();
    }

    private void EnsureUIReference()
    {
        if (uiController == null && autoFindUIController)
            uiController = FindFirstObjectByType<GameUIController>();
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
        moneyTargetManager.OnMoneyChanged += HandleMoneyChanged;
        moneyTargetManager.OnBankedMoneyChanged += HandleBankedMoneyChanged;
        moneyTargetManager.OnDailyEarningsBanked += HandleDailyEarningsBanked;
        moneyTargetManager.OnTargetChanged += HandleTargetChanged;
        moneyTargetManager.OnDayAdvanced += HandleDayAdvanced;
        moneyTargetManager.OnTargetReached += HandleTargetReached;
        currentDayTargetMoney = moneyTargetManager.TargetMoney;
    }

    private void UnsubscribeFromMoneyTarget()
    {
        if (moneyTargetManager == null) return;
        moneyTargetManager.OnMoneyChanged -= HandleMoneyChanged;
        moneyTargetManager.OnBankedMoneyChanged -= HandleBankedMoneyChanged;
        moneyTargetManager.OnDailyEarningsBanked -= HandleDailyEarningsBanked;
        moneyTargetManager.OnTargetChanged -= HandleTargetChanged;
        moneyTargetManager.OnDayAdvanced -= HandleDayAdvanced;
        moneyTargetManager.OnTargetReached -= HandleTargetReached;
    }

    private void SyncAllUI()
    {
        if (uiController == null || moneyTargetManager == null) return;
        uiController.SetDay(GetCurrentDay());
        uiController.SetTarget(GetTargetMoney());
        uiController.SetDailyEarnings(GetCurrentMoney(), GetTargetMoney(), GetProgress());
        uiController.SetBankedMoney(GetBankedMoney());
    }

    public void StartGame()
    {
        EnsureMoneyTargetReference();
        EnsureUIReference();
        EnsureSpawnPointReferences();
        FindTimerAndNetState();

        IsGameplayActive = true;

        if (IsServerOrStandalone)
        {
            // Defer player positioning until clients report ready to avoid moving
            // only the host’s player when clients aren’t fully spawned yet.
            if (_netState != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                // Small timeout (e.g., 5s) to avoid hanging if a client never reports.
                _netState.BeginClientReadyHandshake(() =>
                {
                    PositionPlayersToSpawnPoints();
                }, timeoutSeconds: 5f);
            }
            else
            {
                // Standalone (no networking) – teleport immediately.
                PositionPlayersToSpawnPoints();
            }
        }

        if (moneyTargetManager != null && moneyTargetManager.CurrentDay <= 0 && IsServerOrStandalone)
            moneyTargetManager.AdvanceDay();
        else if (moneyTargetManager != null && moneyTargetManager.CurrentDay > 0 && IsServerOrStandalone)
            TrySpawnPackagesForDay(moneyTargetManager.CurrentDay);

        dailyPackagesDelivered = 0;
        currentDayTargetMoney = GetTargetMoney();
        endOfDayPopupShown = false;

        SyncAllUI();
        uiController.HideWinPanel();
        uiController.HideDayEndSummary();
        uiController.ShowHUD();
    }

    /// <summary>
    /// Find player NetworkObjects (PlayerMovement components) and teleport them to spawn points.
    /// Uses the inspector-assigned spawn points or auto-found ones. Safe with CharacterController.
    /// Runs on the server/host so positions will be authoritative and replicate to clients.
    /// </summary>
    private void PositionPlayersToSpawnPoints()
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

            // Host (server) can update transform immediately for its own view.
            var cc = pm.GetComponent<CharacterController>();
            var rb = pm.GetComponent<Rigidbody>();
            if (cc != null) cc.enabled = false;
            pm.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            if (cc != null) cc.enabled = true;

            // Let the owner client (for non-host players) apply the move locally (owner-authoritative).
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

        // Hide any stale summaries locally
        uiController.HideDayEndSummary();
        endOfDayPopupShown = false;

        moneyTargetManager?.AdvanceDay();
    }

    public void HostRestartRun()
    {
        if (!IsServerOrStandalone) return;

        uiController.HideDayEndSummary();
        ResetRun();
        dailyPackagesDelivered = 0;
        lastSpawnedDay = -1;
        endOfDayPopupShown = false;
        moneyTargetManager?.AdvanceDay();
    }

    private void OnPausePerformed(InputAction.CallbackContext ctx) => ToggleCursorVisibility();

    public void ToggleCursorVisibility()
    {
        bool show = !Cursor.visible;
        Cursor.visible = show;
        Cursor.lockState = show ? CursorLockMode.None : CursorLockMode.Locked;
    }

    public void AddMoney(int amount) => moneyTargetManager.AddMoney(amount);
    public void RemoveMoney(int amount) => moneyTargetManager.RemoveMoney(amount);
    public bool SpendBanked(int amount) => moneyTargetManager != null && moneyTargetManager.SpendBanked(amount);
    public void AdvanceDay() => moneyTargetManager.AdvanceDay();
    public void AdvanceDays(int days) => moneyTargetManager?.AdvanceDays(days);

    public void ResetRun()
    {
        moneyTargetManager.ResetProgress();
        currentDayTargetMoney = GetTargetMoney();
    }

    public void SetTargetMoney(int value)
    {
        if (moneyTargetManager == null) return;
        moneyTargetManager.TargetMoney = value;
        uiController?.SetTarget(value);
        uiController?.SetDailyEarnings(GetCurrentMoney(), value, GetProgress());
        currentDayTargetMoney = value;
    }

    public int PreviewIncreaseForNextDay() => moneyTargetManager != null ? moneyTargetManager.PreviewIncreaseForNextDay() : 0;

    public int GetCurrentMoney() => moneyTargetManager != null ? moneyTargetManager.CurrentMoney : 0;
    public int GetBankedMoney() => moneyTargetManager != null ? moneyTargetManager.BankedMoney : 0;
    public int GetTargetMoney() => moneyTargetManager != null ? moneyTargetManager.TargetMoney : 0;
    public int GetCurrentDay() => moneyTargetManager != null ? moneyTargetManager.CurrentDay : 0;
    public float GetProgress() => moneyTargetManager != null ? moneyTargetManager.Progress : 0f;

    private void HandleMoneyChanged(int newAmount)
    {
        uiController?.SetDailyEarnings(newAmount, GetTargetMoney(), GetProgress());
    }

    private void HandleBankedMoneyChanged(int newBanked)
    {
        uiController?.SetBankedMoney(newBanked);
    }

    private void HandleDailyEarningsBanked(int newBanked, int amountAdded)
    {
        if (endOfDayPopupShown) return;
        bool metQuota = amountAdded >= currentDayTargetMoney;

        // Show local summary; if you need network broadcast, add a ClientRpc in NetworkGameState
        uiController?.ShowDayEndSummary(amountAdded, newBanked, dailyPackagesDelivered, metQuota, IsServerOrStandalone);

        dailyPackagesDelivered = 0;
        endOfDayPopupShown = true;
    }

    private void HandleTargetChanged(int newTarget)
    {
        uiController.SetTarget(newTarget);
        uiController.SetDailyEarnings(GetCurrentMoney(), newTarget, GetProgress());
        currentDayTargetMoney = newTarget;
    }

    private void HandleDayAdvanced(int newDayIndex)
    {
        uiController.SetDay(newDayIndex);
        uiController.SetDailyEarnings(GetCurrentMoney(), GetTargetMoney(), GetProgress());
        currentDayTargetMoney = GetTargetMoney();
        dailyPackagesDelivered = 0;
        endOfDayPopupShown = false;

        IsGameplayActive = true;
        uiController.HideDayEndSummary();
        uiController.ShowHUD();

        if (IsServerOrStandalone)
            TrySpawnPackagesForDay(newDayIndex);
    }

    private void HandleTargetReached()
    {
        TriggerWinCondition();
    }

    private void TriggerWinCondition()
    {
        OnWinCondition?.Invoke();
        uiController.ShowWinPanel();
        Debug.Log("[GameManager] Win condition met.");
    }

    public void SetUIController(GameUIController controller)
    {
        uiController = controller;
        uiController.ConfigureDayEndButtons(HostAdvanceToNextDay, HostRestartRun);
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

    private void TrySpawnPackagesForDay(int dayIndex)
    {
        if (preventDuplicateSpawnsPerDay && lastSpawnedDay == dayIndex)
            return;

        lastSpawnedDay = dayIndex;
        SpawnPackagesForDay(dayIndex);
    }

    private void SpawnPackagesForDay(int dayIndex)
    {
        if (packageSpawners == null) return;

        var active = new List<PackageSpawner>();
        foreach (var s in packageSpawners)
        {
            if (s != null && s.isActiveAndEnabled)
                active.Add(s);
        }
        if (active.Count == 0) return;

        foreach (var spawner in active)
        {
            spawner.SetUseDailyIncreasingCount(false);
            spawner.ApplyDayIndex(dayIndex);
            spawner.SpawnAll();
        }
    }
}