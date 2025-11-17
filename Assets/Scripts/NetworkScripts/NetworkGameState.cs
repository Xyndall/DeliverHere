using System;
using UnityEngine;
using Unity.Netcode;

public class NetworkGameState : NetworkBehaviour
{
    public static NetworkGameState Instance { get; private set; }

    [Header("References")]
    [SerializeField] private GameManager gameManager;
    [SerializeField] private MoneyTargetManager moneyTargetManager;
    [SerializeField] private DayNightCycle dayNightCycle;
    [SerializeField] private GameUIController uiController;
    [SerializeField] private bool autoFindReferences = true;

    // Expose a simple C# event for non-NetworkBehaviours (e.g., MenuUI)
    public event Action<bool> OnGameStartedChangedEvent;

    // Networked state (server writes, everyone reads)
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

    private readonly NetworkVariable<float> nvDayNightProgress = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public bool GameStarted => gameStarted.Value;

    // NEW: expose read-only mirrors for clients/others to sample
    public int CurrentDay => nvCurrentDay.Value;
    public float DayNightProgress => nvDayNightProgress.Value;

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

    public override void OnNetworkSpawn()
    {
        if (autoFindReferences)
        {
            if (gameManager == null) gameManager = FindFirstObjectByType<GameManager>();
            if (moneyTargetManager == null) moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();
            if (dayNightCycle == null) dayNightCycle = FindFirstObjectByType<DayNightCycle>();
            if (uiController == null) uiController = FindFirstObjectByType<GameUIController>();
        }

        gameStarted.OnValueChanged += OnGameStartedChanged;
        nvCurrentMoney.OnValueChanged += (_, v) => uiController?.SetDailyEarnings(v, nvTargetMoney.Value, nvProgress.Value);
        nvBankedMoney.OnValueChanged += (_, v) => uiController?.SetBankedMoney(v);
        nvTargetMoney.OnValueChanged += (_, v) =>
        {
            uiController?.SetTarget(v);
            uiController?.SetDailyEarnings(nvCurrentMoney.Value, v, nvProgress.Value);
        };
        nvCurrentDay.OnValueChanged += (_, v) =>
        {
            uiController?.SetDay(v);
            uiController?.SetDailyEarnings(nvCurrentMoney.Value, nvTargetMoney.Value, nvProgress.Value);
        };
        nvProgress.OnValueChanged += (_, v) =>
        {
            uiController?.SetDailyEarnings(nvCurrentMoney.Value, nvTargetMoney.Value, v);
        };
        nvDayNightProgress.OnValueChanged += (_, v) =>
        {
            uiController?.SetDayNightVisible(true);
            uiController?.SetDayNightProgress(Mathf.Clamp01(v));
        };

        if (IsServer)
        {
            SubscribeServerToMoneyEvents();
            PushAllFromServer();
        }
        else
        {
            // Client: apply a snapshot immediately
            ApplyAllToClientUI();
            if (gameStarted.Value)
            {
                gameManager?.StartGame();
            }
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            UnsubscribeServerFromMoneyEvents();
        }
    }

    private void Update()
    {
        // Server polls time-of-day and mirrors to NV
        if (IsServer && dayNightCycle != null)
        {
            nvDayNightProgress.Value = Mathf.Clamp01(dayNightCycle.Normalized);
        }
    }

    // Requests from UI (host-only in practice, but allow from anyone; server validates)
    [ServerRpc(RequireOwnership = false)]
    public void RequestStartGameServerRpc()
    {
        if (gameStarted.Value) return;
        gameStarted.Value = true;
        gameManager?.StartGame();
        PushAllFromServer();
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestEndGameServerRpc()
    {
        if (!gameStarted.Value) return;
        gameStarted.Value = false;
        gameManager?.EndGame();
        PushAllFromServer();
    }

    private void OnGameStartedChanged(bool _, bool started)
    {
        OnGameStartedChangedEvent?.Invoke(started);
        if (started) gameManager?.StartGame();
        else gameManager?.EndGame();
    }

    // Server-only: mirror MoneyTargetManager into NVs
    private void SubscribeServerToMoneyEvents()
    {
        if (moneyTargetManager == null) return;

        moneyTargetManager.OnMoneyChanged += Server_OnMoneyChanged;
        moneyTargetManager.OnBankedMoneyChanged += Server_OnBankedMoneyChanged;
        moneyTargetManager.OnDailyEarningsBanked += Server_OnDailyEarningsBanked;
        moneyTargetManager.OnTargetChanged += Server_OnTargetChanged;
        moneyTargetManager.OnTargetIncreased += Server_OnTargetIncreased;
        moneyTargetManager.OnDayAdvanced += Server_OnDayAdvanced;
    }

    private void UnsubscribeServerFromMoneyEvents()
    {
        if (moneyTargetManager == null) return;

        moneyTargetManager.OnMoneyChanged -= Server_OnMoneyChanged;
        moneyTargetManager.OnBankedMoneyChanged -= Server_OnBankedMoneyChanged;
        moneyTargetManager.OnDailyEarningsBanked -= Server_OnDailyEarningsBanked;
        moneyTargetManager.OnTargetChanged -= Server_OnTargetChanged;
        moneyTargetManager.OnTargetIncreased -= Server_OnTargetIncreased;
        moneyTargetManager.OnDayAdvanced -= Server_OnDayAdvanced;
    }

    private void Server_OnMoneyChanged(int v)
    {
        if (!IsServer) return;
        nvCurrentMoney.Value = v;
        nvProgress.Value = moneyTargetManager.Progress;
    }

    private void Server_OnBankedMoneyChanged(int v)
    {
        if (!IsServer) return;
        nvBankedMoney.Value = v;
    }

    private void Server_OnDailyEarningsBanked(int newBanked, int _)
    {
        if (!IsServer) return;
        nvBankedMoney.Value = newBanked;
    }

    private void Server_OnTargetChanged(int v)
    {
        if (!IsServer) return;
        nvTargetMoney.Value = v;
        nvProgress.Value = moneyTargetManager.Progress;
    }

    private void Server_OnTargetIncreased(int newTarget, int _)
    {
        if (!IsServer) return;
        nvTargetMoney.Value = newTarget;
        nvProgress.Value = moneyTargetManager.Progress;
    }

    private void Server_OnDayAdvanced(int v)
    {
        if (!IsServer) return;
        nvCurrentDay.Value = v;
        nvCurrentMoney.Value = moneyTargetManager.CurrentMoney;
        nvProgress.Value = moneyTargetManager.Progress;
    }

    private void PushAllFromServer()
    {
        if (!IsServer || moneyTargetManager == null) return;

        nvCurrentMoney.Value = moneyTargetManager.CurrentMoney;
        nvBankedMoney.Value = moneyTargetManager.BankedMoney;
        nvTargetMoney.Value = moneyTargetManager.TargetMoney;
        nvCurrentDay.Value = moneyTargetManager.CurrentDay;
        nvProgress.Value = moneyTargetManager.Progress;
        if (dayNightCycle != null)
            nvDayNightProgress.Value = Mathf.Clamp01(dayNightCycle.Normalized);
    }

    private void ApplyAllToClientUI()
    {
        uiController?.SetDay(nvCurrentDay.Value);
        uiController?.SetTarget(nvTargetMoney.Value);
        uiController?.SetDailyEarnings(nvCurrentMoney.Value, nvTargetMoney.Value, nvProgress.Value);
        uiController?.SetBankedMoney(nvBankedMoney.Value);
        uiController?.SetDayNightVisible(true);
        uiController?.SetDayNightProgress(Mathf.Clamp01(nvDayNightProgress.Value));
    }
}