using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;

public class MenuUIController : MonoBehaviour
{
    [Header("Menu UI")]
    [SerializeField] private GameObject menuRoot; // Optional: assign to hide/show menu
    [SerializeField] private Button startGameButton;
    [SerializeField] private Button endGameButton;

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
            HandleGameStartedChanged(NetworkGameState.Instance.GameStarted);
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
        }
    }

    private void OnDestroy()
    {
        if (startGameButton != null)
            startGameButton.onClick.RemoveListener(OnStartGameClicked);

        if (endGameButton != null)
            endGameButton.onClick.RemoveListener(OnEndGameClicked);
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
        bool isHost = hasNM && NetworkManager.Singleton.IsHost;

        if (startGameButton != null) startGameButton.gameObject.SetActive(isHost);
        if (endGameButton != null) endGameButton.gameObject.SetActive(isHost);

        if (logVisibilityDebug)
        {
            string reason = hasNM
                ? $"IsServer={NetworkManager.Singleton.IsServer}, IsClient={NetworkManager.Singleton.IsClient}, IsHost={NetworkManager.Singleton.IsHost}, IsListening={NetworkManager.Singleton.IsListening}"
                : "NetworkManager.Singleton == null";
            Debug.Log($"[MenuUIController] EvaluateVisibility => host={isHost}. Reason: {reason}");
        }
    }

    private void HandleGameStartedChanged(bool started)
    {
        if (menuRoot != null) menuRoot.SetActive(!started);
    }

    private void OnStartGameClicked()
    {
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
