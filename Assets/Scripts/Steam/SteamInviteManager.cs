using UnityEngine;
using Steamworks;

namespace DeliverHere.Steam
{
    /// <summary>
    /// Simplified Steam invite system that uses Steam's native overlay UI.
    /// Handles invites and join requests through Steam Rich Presence.
    /// </summary>
    public class SteamInviteManager : MonoBehaviour
    {
        public static SteamInviteManager Instance { get; private set; }

        // Event when someone accepts an invite (passes the join code)
        public System.Action<string> OnInviteAccepted;

        private Callback<GameRichPresenceJoinRequested_t> _joinRequestedCallback;
        private string _currentJoinCode;

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
            // UPDATED: Use the built-in SteamManager.Initialized property
            if (!SteamManager.Initialized)
            {
                Debug.LogError("SteamInviteManager: Steam is not initialized!");
                return;
            }

            Debug.Log("SteamInviteManager: Steam is initialized! Ready for invites.");

            // Register callback for when someone clicks "Join Game" from Steam
            _joinRequestedCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameJoinRequested);
        }

        /// <summary>
        /// Opens Steam's native invite dialog.
        /// Call this when the player clicks "Invite Friends" button.
        /// </summary>
        public void OpenInviteDialog()
        {
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

            // UPDATED: Use ActivateGameOverlay with "friends" to show all friends (including offline)
            // Alternative methods:
            
            // Option 1: General friends overlay (shows all friends, can filter)
            SteamFriends.ActivateGameOverlay("friends");
            
            // Option 2: If you prefer the specific invite dialog (only shows online friends in game)
            // Uncomment this and comment out the line above if you prefer:
            // SteamFriends.ActivateGameOverlayInviteDialog(CSteamID.Nil);

            Debug.Log("Opened Steam overlay");
        }

        /// <summary>
        /// Call this when you create/host a game to set the join code.
        /// This allows friends to see "Join Game" on your profile.
        /// </summary>
        public void SetJoinCode(string joinCode)
        {
            if (!SteamManager.Initialized)
            {
                Debug.LogWarning("Cannot set join code - Steam not initialized.");
                return;
            }

            _currentJoinCode = joinCode;

            // Set rich presence so friends can join
            SteamFriends.SetRichPresence("connect", joinCode);
            SteamFriends.SetRichPresence("steam_display", "#StatusWithConnect");
            SteamFriends.SetRichPresence("status", "In Lobby");

            Debug.Log($"Steam Rich Presence set with join code: {joinCode}");
        }

        /// <summary>
        /// Call this when you join someone else's game.
        /// </summary>
        public void SetInGame(string joinCode)
        {
            if (!SteamManager.Initialized) return;

            _currentJoinCode = joinCode;
            SteamFriends.SetRichPresence("connect", joinCode);
            SteamFriends.SetRichPresence("steam_display", "#StatusInGame");
            SteamFriends.SetRichPresence("status", "Playing");
        }

        /// <summary>
        /// Call this when you leave the game to clear rich presence.
        /// </summary>
        public void ClearSession()
        {
            if (!SteamManager.Initialized) return;

            _currentJoinCode = null;
            SteamFriends.ClearRichPresence();
            Debug.Log("Cleared Steam Rich Presence");
        }

        /// <summary>
        /// Get the current join code (if hosting).
        /// </summary>
        public string GetCurrentJoinCode() => _currentJoinCode;

        /// <summary>
        /// Check if we're in a joinable session.
        /// </summary>
        public bool IsInSession() => !string.IsNullOrEmpty(_currentJoinCode);

        // Callback when someone clicks "Join Game" from Steam overlay/friends list
        private void OnGameJoinRequested(GameRichPresenceJoinRequested_t callback)
        {
            string joinCode = callback.m_rgchConnect;
            CSteamID friendId = callback.m_steamIDFriend;
            string friendName = SteamFriends.GetFriendPersonaName(friendId);

            Debug.Log($"Friend {friendName} wants to join! Join code: {joinCode}");

            // Trigger event so your networking code can handle the join
            OnInviteAccepted?.Invoke(joinCode);
        }

        private void OnDestroy()
        {
            ClearSession();
        }
    }
}