using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;

public class LevelLoader : NetworkBehaviour
{
    public static LevelLoader Instance;
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

    [Header("Levels")]
    [Tooltip("Add scene names here. Use LoadNextLevel() to advance.")]
    public string[] Levels;

    private int currentLevelIndex = -1;
    private string currentLevel;

    /// <summary>
    /// Fired when the requested level has finished loading locally (standalone) or
    /// when Netcode reports the load completed (server). Subscribe for server-side setup.
    /// </summary>
    public event Action<string> OnLevelLoaded;

    public void LoadNextLevel()
    {
        if (Levels == null || Levels.Length == 0)
        {
            Debug.LogWarning("[LevelLoader] No levels configured in Levels array.");
            return;
        }

        int nextIndex = GetRandomLevelIndexDifferentFromCurrent();
        LoadLevel(Levels[nextIndex]);
        currentLevelIndex = nextIndex;
    }

    private int GetRandomLevelIndexDifferentFromCurrent()
    {
        // If only one level, return it.
        if (Levels.Length == 1)
            return 0;

        // Try to pick a different index than the current one.
        int idx;
        do
        {
            idx = UnityEngine.Random.Range(0, Levels.Length);
        } while (idx == currentLevelIndex);

        return idx;
    }

    public void LoadLevel(string levelName)
    {
        bool isStandalone = NetworkManager.Singleton == null;
        if (!isStandalone && !IsServer)
        {
            Debug.LogWarning("[LevelLoader] Only the server can load levels in multiplayer.");
            return;
        }

        // Unload previous level if any
        if (!string.IsNullOrEmpty(currentLevel))
        {
            var netScenes = NetworkManager.Singleton?.SceneManager;
            if (!isStandalone && netScenes != null && IsServer)
            {
                var s = SceneManager.GetSceneByName(currentLevel);
                if (s.IsValid() && s.isLoaded)
                    netScenes.UnloadScene(s);
            }
            else
            {
                var s = SceneManager.GetSceneByName(currentLevel);
                if (s.IsValid() && s.isLoaded)
                    SceneManager.UnloadSceneAsync(s);
            }
        }

        currentLevel = levelName;

        // Load via Netcode on server or locally when standalone
        var netSceneMgr = NetworkManager.Singleton?.SceneManager;
        if (!isStandalone && netSceneMgr != null && IsServer)
        {
            // Subscribe to Netcode scene events (server-side)
            netSceneMgr.OnLoadEventCompleted -= OnNetcodeLoadCompleted; // ensure single subscription
            netSceneMgr.OnLoadEventCompleted += OnNetcodeLoadCompleted;

            netSceneMgr.LoadScene(levelName, LoadSceneMode.Additive);
            Debug.Log($"[LevelLoader] Scene '{levelName}' loading via Netcode (server).");
        }
        else
        {
            // Standalone/offline
            SceneManager.sceneLoaded -= SceneLoadedHandler; // ensure single subscription
            SceneManager.sceneLoaded += SceneLoadedHandler;
            SceneManager.LoadSceneAsync(levelName, LoadSceneMode.Additive);
        }
    }

    private void SceneLoadedHandler(Scene scene, LoadSceneMode mode)
    {
        if (!string.Equals(scene.name, currentLevel, StringComparison.Ordinal))
            return;

        // Unsubscribe immediately
        SceneManager.sceneLoaded -= SceneLoadedHandler;

        Debug.Log($"[LevelLoader] Scene '{scene.name}' loaded locally (standalone).");
        TryInvokeLevelLoaded(scene.name);
    }

    // Netcode: fired on server when a scene load operation completes across server and clients
    private void OnNetcodeLoadCompleted(string sceneName, LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (!string.Equals(sceneName, currentLevel, StringComparison.Ordinal))
            return;

        // Unsubscribe immediately
        var netSceneMgr = NetworkManager.Singleton?.SceneManager;
        if (netSceneMgr != null)
            netSceneMgr.OnLoadEventCompleted -= OnNetcodeLoadCompleted;

        if (clientsTimedOut != null && clientsTimedOut.Count > 0)
        {
            Debug.LogWarning($"[LevelLoader] Netcode load completed for '{sceneName}' with timeouts: {string.Join(", ", clientsTimedOut)}");
        }
        else
        {
            Debug.Log($"[LevelLoader] Netcode load completed for scene '{sceneName}' (clients: {clientsCompleted?.Count ?? 0}).");
        }

        TryInvokeLevelLoaded(sceneName);
    }

    private void TryInvokeLevelLoaded(string sceneName)
    {
        try
        {
            OnLevelLoaded?.Invoke(sceneName);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }
}