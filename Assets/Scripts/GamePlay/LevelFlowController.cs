using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;

public class LevelFlowController : MonoBehaviour
{
    [Tooltip("Seconds to wait for level load before timing out.")]
    [SerializeField] private float loadTimeoutSeconds = 10f;
    [SerializeField] private LevelLoader levelLoader;

    private void OnEnable()
    {
        // Clients report their local scene loads to server
        SceneManager.sceneLoaded += ClientSceneLoadedHandler;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= ClientSceneLoadedHandler;
    }

    private void Awake()
    {
        if (levelLoader == null)
            levelLoader = LevelLoader.Instance ?? FindFirstObjectByType<LevelLoader>();
    }

    // Public entry point you can call from UI, NetworkGameState, etc.
    public void StartLoadNextLevel()
    {
        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
        {
            Debug.LogWarning("[LevelFlowController] StartLoadNextLevel must be called on the server/host.");
            return;
        }
        StartCoroutine(LoadNextAndThenStartCoroutine());
    }

    private IEnumerator LoadNextAndThenStartCoroutine()
    {
        if (levelLoader == null)
        {
            Debug.LogError("[LevelFlowController] levelLoader is null. Assign in inspector or via LevelLoader.Instance.");
            yield break;
        }

        bool serverLoaded = false;
        void OnLoaded(string sceneName)
        {
            Debug.Log($"[LevelFlowController] OnLevelLoaded received on server for '{sceneName}'.");
            serverLoaded = true;
        }

        levelLoader.OnLevelLoaded += OnLoaded;
        Debug.Log("[LevelFlowController] Requesting next level load on server...");
        levelLoader.LoadNextLevel();

        float deadline = Time.time + loadTimeoutSeconds;
        while (!serverLoaded && Time.time < deadline)
            yield return null;

        levelLoader.OnLevelLoaded -= OnLoaded;

        if (!serverLoaded)
            Debug.LogWarning("[LevelFlowController] Server scene load timed out or didn't match expected name.");

        // Optional small wait to allow a frame for spawned objects / NetworkObjects to settle
        yield return null;

        // Server/host: begin client-ready handshake, then start the game
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
        {
            var netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
            if (netState != null)
            {
                netState.BeginClientReadyHandshake(() =>
                {
                    Debug.Log("[LevelFlowController] All clients ready (or timeout) — starting game on server.");
                    if (GameManager.Instance != null)
                        GameManager.Instance.StartGame();
                }, loadTimeoutSeconds);
            }
            else
            {
                if (GameManager.Instance != null)
                    GameManager.Instance.StartGame();
            }
        }
    }

    // CLIENT: when any scene finishes loading locally, notify server that this client is ready.
    private void ClientSceneLoadedHandler(Scene scene, LoadSceneMode mode)
    {
        // Only notify server when this instance is a client (not the headless server).
        if (NetworkManager.Singleton == null) return;
        if (NetworkManager.Singleton.IsServer) return; // server doesn't report to itself

        // Call the ServerRpc on NetworkGameState to report readiness
        if (NetworkGameState.Instance != null)
        {
            try
            {
                NetworkGameState.Instance.ClientSceneLoadedServerRpc();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}