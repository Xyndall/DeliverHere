using UnityEngine;
using Steamworks;

namespace DeliverHere.Steam
{

    public class SteamInviteManager : MonoBehaviour
    {
        public static SteamInviteManager Instance { get; private set; }

        // Event when someone accepts an invite (passes the join code)
        public System.Action<string> OnInviteAccepted;

        private Callback<GameRichPresenceJoinRequested_t> _joinRequestedCallback;
        private Callback<GameLobbyJoinRequested_t> _lobbyJoinRequestedCallback;
        
        private string _currentJoinCode;
        private CSteamID _currentLobbyId;
        private bool _isHost = false;
        private bool _isQuitting;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (!SteamManager.Initialized)
            {
                Debug.LogError("SteamInviteManager: Steam is not initialized!");
                return;
            }

            Debug.Log("SteamInviteManager: Steam is initialized! Ready for invites.");

            // Register callbacks
            _joinRequestedCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameJoinRequested);
            _lobbyJoinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
        }

        /// <summary>
        /// Opens Steam's invite dialog.
        /// </summary>
        public void OpenInviteDialog()
        {
            Debug.Log("[SteamInviteManager] OpenInviteDialog() called!");

            if (!SteamManager.Initialized)
            {
                Debug.LogWarning("Cannot open invite dialog - Steam not initialized.");
                return;
            }

            if (string.IsNullOrEmpty(_currentJoinCode))
            {
                Debug.LogWarning("Cannot invite friends - no active game session with join code.");
                return;
            }

            // If we have a lobby, use the lobby invite dialog (shows all friends)
            if (_currentLobbyId.IsValid())
            {
                Debug.Log("[SteamInviteManager] Opening lobby invite dialog");
                SteamFriends.ActivateGameOverlayInviteDialog(_currentLobbyId);
            }
            else
            {
                // Fallback to friends overlay
                Debug.Log("[SteamInviteManager] Opening friends overlay");
                SteamFriends.ActivateGameOverlay("friends");
            }
        }

        /// <summary>
        /// HOST ONLY: Call this when you create/host a game to set the join code.
        /// Creates a Steam lobby for better invite support.
        /// </summary>
        public void SetJoinCode(string joinCode)
        {
            Debug.Log($"[SteamInviteManager] SetJoinCode() called with code: {joinCode}");

            if (!SteamManager.Initialized)
            {
                Debug.LogWarning("Cannot set join code - Steam not initialized.");
                return;
            }

            _currentJoinCode = joinCode;
            _isHost = true;

            // Create a Steam lobby for better invite dialog
            CreateSteamLobby(joinCode);

            // Set rich presence
            SteamFriends.SetRichPresence("connect", joinCode);
            SteamFriends.SetRichPresence("steam_display", "#StatusWithConnect");
            SteamFriends.SetRichPresence("status", "Hosting");

            Debug.Log($"[SteamInviteManager] HOST: Steam Rich Presence set with join code: {joinCode}");
        }

        /// <summary>
        /// CLIENT ONLY: Call this when you join someone else's game.
        /// This syncs the join code from the host so clients can also invite.
        /// </summary>
        public void SetJoinCodeAsClient(string joinCode)
        {
            Debug.Log($"[SteamInviteManager] SetJoinCodeAsClient() called with code: {joinCode}");

            if (!SteamManager.Initialized)
            {
                Debug.LogWarning("Cannot set join code - Steam not initialized.");
                return;
            }

            _currentJoinCode = joinCode;
            _isHost = false;

            // Set rich presence so others can join through this client too
            SteamFriends.SetRichPresence("connect", joinCode);
            SteamFriends.SetRichPresence("steam_display", "#StatusInGame");
            SteamFriends.SetRichPresence("status", "Playing");

            Debug.Log($"[SteamInviteManager] CLIENT: Steam Rich Presence set with join code: {joinCode}");
        }

        /// <summary>
        /// DEPRECATED: Use SetJoinCodeAsClient for clients.
        /// </summary>
        [System.Obsolete("Use SetJoinCodeAsClient() for clients instead")]
        public void SetInGame(string joinCode)
        {
            SetJoinCodeAsClient(joinCode);
        }

        /// <summary>
        /// Creates a Steam lobby with the join code as metadata.
        /// This enables the proper invite dialog that shows all friends.
        /// </summary>
        private void CreateSteamLobby(string joinCode)
        {
            // Create a friends-only lobby (invisible to public)
            var createLobbyCall = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 10);
            
            var createLobbyCallback = CallResult<LobbyCreated_t>.Create((result, failure) =>
            {
                if (failure || result.m_eResult != EResult.k_EResultOK)
                {
                    Debug.LogError($"[SteamInviteManager] Failed to create Steam lobby: {result.m_eResult}");
                    return;
                }

                _currentLobbyId = new CSteamID(result.m_ulSteamIDLobby);
                Debug.Log($"[SteamInviteManager] Steam lobby created: {_currentLobbyId}");

                // Store the join code in the lobby metadata
                SteamMatchmaking.SetLobbyData(_currentLobbyId, "JoinCode", joinCode);
                SteamMatchmaking.SetLobbyJoinable(_currentLobbyId, true);
            });

            createLobbyCallback.Set(createLobbyCall);
        }

        /// <summary>
        /// Call this when you leave the game to clear rich presence and lobby.
        /// </summary>
        public void ClearSession()
        {
            if (!SteamManager.Exists || !SteamManager.Initialized) return;

            // Leave the Steam lobby if we're in one (host only)
            if (_currentLobbyId.IsValid() && _isHost)
            {
                SteamMatchmaking.LeaveLobby(_currentLobbyId);
                Debug.Log($"[SteamInviteManager] Left Steam lobby: {_currentLobbyId}");
                _currentLobbyId = CSteamID.Nil;
            }

            _currentJoinCode = null;
            _isHost = false;
            SteamFriends.ClearRichPresence();
            Debug.Log("[SteamInviteManager] Cleared Steam Rich Presence");
        }

        /// <summary>
        /// Get the current join code.
        /// </summary>
        public string GetCurrentJoinCode() => _currentJoinCode;

        /// <summary>
        /// Check if we're in a joinable session.
        /// </summary>
        public bool IsInSession() => !string.IsNullOrEmpty(_currentJoinCode);

        // Callback when someone clicks "Join Game" from Rich Presence
        private void OnGameJoinRequested(GameRichPresenceJoinRequested_t callback)
        {
            string joinCode = callback.m_rgchConnect;
            CSteamID friendId = callback.m_steamIDFriend;
            string friendName = SteamFriends.GetFriendPersonaName(friendId);

            Debug.Log($"[SteamInviteManager] Friend {friendName} wants to join via Rich Presence! Join code: {joinCode}");

            // Trigger event so your networking code can handle the join
            OnInviteAccepted?.Invoke(joinCode);
        }

        // Callback when someone joins via Steam lobby invite
        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            CSteamID lobbyId = callback.m_steamIDLobby;
            CSteamID friendId = callback.m_steamIDFriend;
            string friendName = SteamFriends.GetFriendPersonaName(friendId);

            Debug.Log($"[SteamInviteManager] Friend {friendName} wants to join lobby: {lobbyId}");

            // Join the lobby to get the join code
            var joinLobbyCall = SteamMatchmaking.JoinLobby(lobbyId);
            
            var joinLobbyCallback = CallResult<LobbyEnter_t>.Create((result, failure) =>
            {
                if (failure || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                {
                    Debug.LogError($"[SteamInviteManager] Failed to join lobby: {result.m_EChatRoomEnterResponse}");
                    return;
                }

                CSteamID joinedLobbyId = new CSteamID(result.m_ulSteamIDLobby);
                string joinCode = SteamMatchmaking.GetLobbyData(joinedLobbyId, "JoinCode");

                Debug.Log($"[SteamInviteManager] Joined lobby, retrieved join code: {joinCode}");

                if (!string.IsNullOrEmpty(joinCode))
                {
                    OnInviteAccepted?.Invoke(joinCode);
                }
                else
                {
                    Debug.LogError("[SteamInviteManager] Join code not found in lobby data!");
                }
            });

            joinLobbyCallback.Set(joinLobbyCall);
        }

        private void OnApplicationQuit()
        {
            _isQuitting = true;
        }

        private void OnDisable()
        {
            if (_isQuitting) return; // Avoid touching Steamworks APIs during app shutdown teardown ordering issues
            ClearSession();
        }
    }
}