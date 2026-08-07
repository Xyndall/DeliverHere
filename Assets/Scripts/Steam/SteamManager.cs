using UnityEngine;
using Steamworks;

namespace DeliverHere.Steam
{
    /// <summary>
    /// Handles Steam API initialization and callbacks.
    /// Add this to a persistent GameObject in your first scene.
    /// </summary>
    public class SteamManager : MonoBehaviour
    {
        public static SteamManager Instance { get; private set; }

        private bool _steamInitialized = false;
        public bool IsSteamInitialized => _steamInitialized;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            InitializeSteam();
        }

        private void InitializeSteam()
        {
            try
            {
                // Initialize Steam API
                if (SteamAPI.RestartAppIfNecessary(AppId_t.Invalid))
                {
                    Debug.Log("Steam: Restarting app through Steam...");
                    Application.Quit();
                    return;
                }

                _steamInitialized = SteamAPI.Init();

                if (_steamInitialized)
                {
                    string personaName = SteamFriends.GetPersonaName();
                    CSteamID steamId = SteamUser.GetSteamID();
                    Debug.Log($"Steam initialized successfully! User: {personaName} (ID: {steamId})");
                }
                else
                {
                    Debug.LogError("Steam API initialization failed. Make sure Steam is running and steam_appid.txt is in the project root.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Steam initialization exception: {e.Message}");
                _steamInitialized = false;
            }
        }

        private void Update()
        {
            if (_steamInitialized)
            {
                // Run Steam callbacks
                SteamAPI.RunCallbacks();
            }
        }

        private void OnDestroy()
        {
            if (_steamInitialized)
            {
                SteamAPI.Shutdown();
                Debug.Log("Steam API shutdown.");
            }
        }

        private void OnApplicationQuit()
        {
            if (_steamInitialized)
            {
                SteamAPI.Shutdown();
            }
        }
    }
}