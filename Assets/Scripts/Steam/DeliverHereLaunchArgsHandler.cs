using UnityEngine;
using Steamworks;

namespace DeliverHere.Steam
{
    /// <summary>
    /// Handles Steam launch arguments when the game is started via a Steam invite
    /// (either Rich Presence "connect" or Lobby invite) while the game was NOT already running.
    /// This should be on the SAME GameObject as SteamManager, and Start() should run AFTER Steam Init.
    /// </summary>
    public class DeliverHereLaunchArgsHandler : MonoBehaviour
    {
        public static DeliverHereLaunchArgsHandler Instance { get; private set; }

        // Stores a pending join code found in launch args, to be consumed once
        // your networking system (Relay) is ready.
        public string PendingJoinCode { get; private set; }
        public bool HasPendingJoin => !string.IsNullOrEmpty(PendingJoinCode);

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
            // IMPORTANT: This must run AFTER SteamAPI.Init() has completed.
            if (!SteamManager.Initialized)
            {
                Debug.LogWarning("[DeliverHereLaunchArgsHandler] Steam not initialized yet, cannot check launch args.");
                return;
            }

            CheckLaunchArguments();
        }

        private void CheckLaunchArguments()
        {
            // --- Method 1: Check raw command line args (works for +connect_lobby, -connect, etc.) ---
            string[] args = System.Environment.GetCommandLineArgs();
            Debug.Log($"[DeliverHereLaunchArgsHandler] Launch args: {string.Join(" ", args)}");

            for (int i = 0; i < args.Length; i++)
            {
                // Rich Presence "connect" invites typically show up as "+connect <joincode>"
                if (args[i] == "+connect" && i + 1 < args.Length)
                {
                    string joinCode = args[i + 1];
                    Debug.Log($"[DeliverHereLaunchArgsHandler] Found +connect launch arg: {joinCode}");
                    PendingJoinCode = joinCode;
                    return;
                }

                // Lobby invites show up as "+connect_lobby <lobbyId>"
                if (args[i] == "+connect_lobby" && i + 1 < args.Length)
                {
                    string lobbyIdStr = args[i + 1];
                    Debug.Log($"[DeliverHereLaunchArgsHandler] Found +connect_lobby launch arg: {lobbyIdStr}");

                    if (ulong.TryParse(lobbyIdStr, out ulong lobbyIdRaw))
                    {
                        JoinLobbyFromLaunchArg(new CSteamID(lobbyIdRaw));
                    }
                    return;
                }
            }

            // --- Method 2: Fallback using SteamApps GetLaunchCommandLine ---
            // NOTE: Signature is GetLaunchCommandLine(out string, int), not StringBuilder.
            int length = SteamApps.GetLaunchCommandLine(out string cmd, 1024);
            if (length > 0 && !string.IsNullOrEmpty(cmd))
            {
                Debug.Log($"[DeliverHereLaunchArgsHandler] SteamApps launch command line: {cmd}");

                if (cmd.Contains("+connect_lobby"))
                {
                    var parts = cmd.Split(' ');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i] == "+connect_lobby" && i + 1 < parts.Length &&
                            ulong.TryParse(parts[i + 1], out ulong lobbyIdRaw))
                        {
                            JoinLobbyFromLaunchArg(new CSteamID(lobbyIdRaw));
                            return;
                        }
                    }
                }
                else if (cmd.Contains("+connect"))
                {
                    var parts = cmd.Split(' ');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i] == "+connect" && i + 1 < parts.Length)
                        {
                            PendingJoinCode = parts[i + 1];
                            Debug.Log($"[DeliverHereLaunchArgsHandler] Found +connect via SteamApps: {PendingJoinCode}");
                            return;
                        }
                    }
                }
            }

            Debug.Log("[DeliverHereLaunchArgsHandler] No pending Steam invite launch args found.");
        }

        private void JoinLobbyFromLaunchArg(CSteamID lobbyId)
        {
            Debug.Log($"[DeliverHereLaunchArgsHandler] Attempting to join lobby from launch args: {lobbyId}");

            var joinLobbyCall = SteamMatchmaking.JoinLobby(lobbyId);
            var joinLobbyCallback = CallResult<LobbyEnter_t>.Create((result, failure) =>
            {
                if (failure || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                {
                    Debug.LogError($"[DeliverHereLaunchArgsHandler] Failed to join lobby from launch args: {result.m_EChatRoomEnterResponse}");
                    return;
                }

                CSteamID joinedLobbyId = new CSteamID(result.m_ulSteamIDLobby);
                string joinCode = SteamMatchmaking.GetLobbyData(joinedLobbyId, "JoinCode");

                if (!string.IsNullOrEmpty(joinCode))
                {
                    Debug.Log($"[DeliverHereLaunchArgsHandler] Retrieved join code from lobby: {joinCode}");
                    PendingJoinCode = joinCode;
                }
                else
                {
                    Debug.LogError("[DeliverHereLaunchArgsHandler] Lobby joined but JoinCode metadata not found!");
                }
            });

            joinLobbyCallback.Set(joinLobbyCall);
        }

        /// <summary>
        /// Call this once your networking (Relay) is ready to consume the pending join code.
        /// Clears the pending state after retrieval.
        /// </summary>
        public string ConsumePendingJoinCode()
        {
            string code = PendingJoinCode;
            PendingJoinCode = null;
            return code;
        }
    }
}