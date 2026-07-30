using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using DeliverHere.UI;

public class MenuUIController : MonoBehaviour
{
    [Header("Menu UI")]
    [SerializeField] private GameObject menuRoot; // Optional: assign to hide/show menu
    [SerializeField] private Button startGameButton;
    [SerializeField] private Button endGameButton;

    [Header("UI References")]
    [Tooltip("Reference to the in-game start button that needs to be reset when starting from menu")]
    [SerializeField] private UIStartGameButton uiStartGameButton;

    [Header("Debug")]
    [SerializeField] private bool logVisibilityDebug = false;

    private bool subscribed;
    private Action serverStartedHandler;
    private Action<ulong> clientConnectedHandler;
    private Action<ulong> clientDisconnectedHandler;
    private Coroutine waitAndSubscribeRoutine;

    private void Awake()
    {
        if (startGameButton != null)
            startGameButton.onClick.AddListener(OnStartGameClicked);

        if (endGameButton != null)
            endGameButton.onClick.AddListener(OnEndGameClicked);
    }

    private void OnEnable()
    {
        // Immediate attempt
        TrySetupNetworkSubscriptionsOrQueue();

        // React to replicated game state to show/hide menu everywhere
        if (NetworkGameState.Instance != null)
        {
            NetworkGameState.Instance.OnGameStartedChangedEvent += HandleGameStartedChanged;
            NetworkGameState.Instance.OnLocalGameStateChanged += HandleGameStateChanged;
            
            // Apply initial state
            HandleGameStartedChanged(NetworkGameState.Instance.GameStarted);
            HandleGameStateChanged(NetworkGameState.Instance.LocalGameState);
        }
        else
        {
            // If NetworkGameState doesn't exist yet, start coroutine to wait for it
            StartCoroutine(WaitForNetworkGameStateAndSubscribe());
        }

        // Set a sane initial state
        EvaluateVisibility();
    }

    private void OnDisable()
    {
        TeardownNetworkSubscriptions();

        if (NetworkGameState.Instance != null)
        {
            NetworkGameState.Instance.OnGameStartedChangedEvent -= HandleGameStartedChanged;
            NetworkGameState.Instance.OnLocalGameStateChanged -= HandleGameStateChanged;
        }
    }

    private void OnDestroy()
    {
        if (startGameButton != null)
            startGameButton.onClick.RemoveListener(OnStartGameClicked);

        if (endGameButton != null)
            endGameButton.onClick.RemoveListener(OnEndGameClicked);
    }

    private IEnumerator WaitForNetworkGameStateAndSubscribe()
    {
        // Wait for NetworkGameState to exist
        float timeout = 5f;
        float elapsed = 0f;
        
        while (NetworkGameState.Instance == null && elapsed < timeout)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (NetworkGameState.Instance != null)
        {
            NetworkGameState.Instance.OnGameStartedChangedEvent += HandleGameStartedChanged;
            NetworkGameState.Instance.OnLocalGameStateChanged += HandleGameStateChanged;
            
            // Apply initial state
            HandleGameStartedChanged(NetworkGameState.Instance.GameStarted);
            HandleGameStateChanged(NetworkGameState.Instance.LocalGameState);
            
            if (logVisibilityDebug)
                Debug.Log($"[MenuUIController] Subscribed to NetworkGameState. GameStarted={NetworkGameState.Instance.GameStarted}, State={NetworkGameState.Instance.LocalGameState}");
        }
        else if (logVisibilityDebug)
        {
            Debug.LogWarning("[MenuUIController] NetworkGameState not found within timeout.");
        }
    }

    private void TrySetupNetworkSubscriptionsOrQueue()
    {
        if (NetworkManager.Singleton != null)
        {
            SetupNetworkSubscriptions();
            EvaluateVisibility(); // evaluate with current role
            return;
        }

        // Defer until NetworkManager exists (e.g., if spawned at runtime)
        if (waitAndSubscribeRoutine == null)
            waitAndSubscribeRoutine = StartCoroutine(WaitForNetworkManagerThenSubscribe());
    }

    private IEnumerator WaitForNetworkManagerThenSubscribe()
    {
        const float timeout = 5f;
        float t = 0f;
        while (NetworkManager.Singleton == null && t < timeout)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }

        waitAndSubscribeRoutine = null;

        if (NetworkManager.Singleton != null)
        {
            SetupNetworkSubscriptions();
            EvaluateVisibility();
        }
        else if (logVisibilityDebug)
        {
            Debug.LogWarning("[MenuUIController] NetworkManager not found within timeout; buttons will remain hidden unless running offline.");
        }
    }

    private void SetupNetworkSubscriptions()
    {
        if (subscribed) return;

        serverStartedHandler = EvaluateVisibility; // no params
        clientConnectedHandler = _ => EvaluateVisibility();
        clientDisconnectedHandler = _ => EvaluateVisibility();

        NetworkManager.Singleton.OnServerStarted += serverStartedHandler;
        NetworkManager.Singleton.OnClientConnectedCallback += clientConnectedHandler;
        NetworkManager.Singleton.OnClientDisconnectCallback += clientDisconnectedHandler;

        subscribed = true;
    }

    private void TeardownNetworkSubscriptions()
    {
        if (waitAndSubscribeRoutine != null)
        {
            StopCoroutine(waitAndSubscribeRoutine);
            waitAndSubscribeRoutine = null;
        }

        if (!subscribed || NetworkManager.Singleton == null) return;

        if (serverStartedHandler != null)
            NetworkManager.Singleton.OnServerStarted -= serverStartedHandler;
        if (clientConnectedHandler != null)
            NetworkManager.Singleton.OnClientConnectedCallback -= clientConnectedHandler;
        if (clientDisconnectedHandler != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= clientDisconnectedHandler;

        serverStartedHandler = null;
        clientConnectedHandler = null;
        clientDisconnectedHandler = null;
        subscribed = false;
    }

    private void EvaluateVisibility()
    {
        bool hasNM = NetworkManager.Singleton != null;
        bool isConnected = hasNM && NetworkManager.Singleton.IsListening;

        // Show buttons if connected (either as host or client)
        if (startGameButton != null) startGameButton.gameObject.SetActive(isConnected);
        if (endGameButton != null) endGameButton.gameObject.SetActive(isConnected);

        // Only host can actually interact with the buttons
        bool isHost = hasNM && NetworkManager.Singleton.IsHost;
        if (startGameButton != null) startGameButton.interactable = isHost;
        if (endGameButton != null) endGameButton.interactable = isHost;

        if (logVisibilityDebug)
        {
            string reason = hasNM
                ? $"IsServer={NetworkManager.Singleton.IsServer}, IsClient={NetworkManager.Singleton.IsClient}, IsHost={NetworkManager.Singleton.IsHost}, IsListening={NetworkManager.Singleton.IsListening}"
                : "NetworkManager.Singleton == null";
            Debug.Log($"[MenuUIController] EvaluateVisibility => visible={isConnected}, interactable={isHost}. Reason: {reason}");
        }
    }

    private void HandleGameStartedChanged(bool started)
    {
        UpdateMenuVisibility();
    }

    private void HandleGameStateChanged(GameState state)
    {
        UpdateMenuVisibility();
        
        if (logVisibilityDebug)
            Debug.Log($"[MenuUIController] HandleGameStateChanged: state={state}");
    }

    private void UpdateMenuVisibility()
    {
        if (menuRoot == null) return;

        // Hide main menu if:
        // 1. Game has started (gameStarted = true)
        // 2. OR we're in Lobby state (connected to a game session)
        // 3. OR we're in Loading/ReadyToStart/InGame/GameOver states
        
        bool shouldHideMenu = false;
        
        if (NetworkGameState.Instance != null)
        {
            GameState currentState = NetworkGameState.Instance.LocalGameState;
            bool gameStarted = NetworkGameState.Instance.GameStarted;
            
            // Show menu only in MainMenu state when game hasn't started
            shouldHideMenu = gameStarted || currentState != GameState.MainMenu;
        }
        
        menuRoot.SetActive(!shouldHideMenu);
        
        if (logVisibilityDebug)
        {
            string state = NetworkGameState.Instance != null ? NetworkGameState.Instance.LocalGameState.ToString() : "Unknown";
            bool started = NetworkGameState.Instance != null ? NetworkGameState.Instance.GameStarted : false;
            Debug.Log($"[MenuUIController] UpdateMenuVisibility: state={state}, started={started}, menuVisible={!shouldHideMenu}");
        }
    }

    private void OnStartGameClicked()
    {
        // Reset the UIStartGameButton activation state before starting the game
        // This ensures it can be clicked again after level loads
        if (uiStartGameButton != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            uiStartGameButton.ServerResetButton();
            
            if (logVisibilityDebug)
                Debug.Log("[MenuUIController] Reset UIStartGameButton activation state before starting game.");
        }
        else if (uiStartGameButton == null)
        {
            Debug.LogWarning("[MenuUIController] UIStartGameButton reference is null! Please assign it in the inspector.");
        }

        NetworkGameState.Instance.RequestStartGameServerRpc();
    }

    private void OnEndGameClicked()
    {
        if (NetworkGameState.Instance != null)
        {
            NetworkGameState.Instance.RequestEndGameServerRpc();
        }
        else
        {
            GameManager.Instance?.EndGame();
            if (menuRoot != null) menuRoot.SetActive(true);
        }
    }
}
