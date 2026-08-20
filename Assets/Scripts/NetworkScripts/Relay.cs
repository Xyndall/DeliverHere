using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using DeliverHere.Steam;
using System.Collections;
using System.Threading.Tasks;

public class Relay : MonoBehaviour
{
    
    public static Relay Instance { get; private set; }

    public int MaxPlayers = 3;

    // NEW: Fired whenever a client successfully joins a relay session, regardless of trigger source
    // (manual UI join, Steam invite while running, or cold-launch Steam invite).
    public event System.Action OnClientJoined;

    // NEW: Fired if a client join attempt fails, so UI can react (e.g. show an error, re-enable buttons).
    public event System.Action<string> OnClientJoinFailed;
    
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        
    }


    private async void Start()
    {
        await UnityServices.InitializeAsync();

        AuthenticationService.Instance.SignedIn += () =>
        {
            Debug.Log("Signed in! Player ID: " + AuthenticationService.Instance.PlayerId);
        };
        await AuthenticationService.Instance.SignInAnonymouslyAsync();

        // Subscribe to Steam invite events (game already running case)
        if (SteamInviteManager.Instance != null)
        {
            SteamInviteManager.Instance.OnInviteAccepted += HandleSteamInvite;
        }

        // Handle cold-launch invites (game was NOT running when invite accepted)
        StartCoroutine(CheckForColdLaunchInvite());
    }

    // Waits briefly for DeliverHereLaunchArgsHandler to resolve any pending join code from launch args
    private IEnumerator CheckForColdLaunchInvite()
    {
        float timeout = 5f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            if (DeliverHereLaunchArgsHandler.Instance != null && DeliverHereLaunchArgsHandler.Instance.HasPendingJoin)
            {
                string joinCode = DeliverHereLaunchArgsHandler.Instance.ConsumePendingJoinCode();
                Debug.Log($"[Relay] Cold-launch Steam invite detected! Joining with code: {joinCode}");
                JoinRelay(joinCode);
                yield break;
            }

            yield return new WaitForSeconds(0.25f);
            elapsed += 0.25f;
        }

        Debug.Log("[Relay] No cold-launch Steam invite found.");
    }

    // Handle when someone accepts a Steam invite while game IS already running
    private void HandleSteamInvite(string joinCode)
    {
        Debug.Log($"Handling Steam invite with join code: {joinCode}");
        JoinRelay(joinCode);
    }

    public async Task<string> CreateRelay()
    {
        try
        {
            int clientSlots = Mathf.Max(1, MaxPlayers - 1);
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(clientSlots);
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            Debug.Log("Relay allocation created. Join code: " + joinCode);

            var connectionType = "dtls";
            var relayServerData = allocation.ToRelayServerData(connectionType);
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(relayServerData);

            NetworkManager.Singleton.StartHost();

            if (SteamInviteManager.Instance != null)
            {
                SteamInviteManager.Instance.SetJoinCode(joinCode);
            }

            // Transition the replicated game state to Lobby now that hosting has started.
            // This is the single source of truth for UI state - NetworkGameState.nvGameState
            // drives UIStateManager on host and all clients via replication.
            if (NetworkGameState.Instance != null)
            {
                NetworkGameState.Instance.ServerSetGameState(GameState.Lobby);
            }

            // Hosting also counts as "joined" for UI state purposes
            OnClientJoined?.Invoke();

            return joinCode;
        }
        catch (RelayServiceException e)
        {
            Debug.LogError("Failed to create Relay allocation: " + e);
            OnClientJoinFailed?.Invoke(e.Message);
            return null;
        }

    }

    public async void JoinRelay(string joinCode)
    {
        try
        {
            Debug.Log("Joining Relay with " + joinCode);
            JoinAllocation joinAllocation =  await RelayService.Instance.JoinAllocationAsync(joinCode);

            var connectionType = "dtls";
            var relayServerData = joinAllocation.ToRelayServerData(connectionType);
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(relayServerData);
            NetworkManager.Singleton.StartClient();

            if (SteamInviteManager.Instance != null)
            {
                SteamInviteManager.Instance.SetJoinCodeAsClient(joinCode);
            }

            // NEW: Notify listeners (e.g. NetworkManagerUI) that we successfully joined,
            // regardless of whether this was triggered by manual UI input or a Steam invite.
            OnClientJoined?.Invoke();
        }
        catch (RelayServiceException e)
        {
            Debug.LogError("Failed to join Relay allocation: " + e);
            OnClientJoinFailed?.Invoke(e.Message);
        }
    }

    private void OnDestroy()
    {
        if (SteamInviteManager.Instance != null)
        {
            SteamInviteManager.Instance.OnInviteAccepted -= HandleSteamInvite;
            SteamInviteManager.Instance.ClearSession();
        }
    }
}
