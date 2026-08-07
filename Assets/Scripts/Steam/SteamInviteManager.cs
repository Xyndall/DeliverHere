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
            if (SteamManager.Instance == null || !SteamManager.Instance.IsSteamInitialized)
            {
                Debug.LogError("SteamInviteManager: Steam is not initialized!");
                return;
            }

            // Register callback for when someone clicks "Join Game" from Steam
            _joinRequestedCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameJoinRequested);
        }

        /// <summary>
        /// Opens Steam's native invite dialog.
        /// Call this when the player clicks "Invite Friends" button.
        /// </summary>
        public void OpenInviteDialog()
        {
            if (!SteamManager.Instance.IsSteamInitialized)
            {
                Debug.LogWarning("Cannot open invite dialog - Steam not initialized.");
                return;
            }

            if (string.IsNullOrEmpty(_currentJoinCode))
            {
                Debug.LogWarning("Cannot invite friends - no active game session with join code.");
                return;
            }

            // This opens Steam's overlay with the friends list to invite
            SteamFriends.ActivateGameOverlayInviteDialog(CSteamID.Nil);
            Debug.Log("Opened Steam invite dialog");
        }

        /// <summary>
        /// Call this when you create/host a game to set the join code.
        /// This allows friends to see "Join Game" on your profile.
        /// </summary>
        public void SetJoinCode(string joinCode)
        {
            if (!SteamManager.Instance.IsSteamInitialized)
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
            if (!SteamManager.Instance.IsSteamInitialized) return;

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
            if (!SteamManager.Instance.IsSteamInitialized) return;

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