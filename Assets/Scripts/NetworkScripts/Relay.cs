using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using DeliverHere.Steam;

public class Relay : MonoBehaviour
{
    
    public static Relay Instance { get; private set; }

    public int MaxPlayers = 3;
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

        // Subscribe to Steam invite events
        if (SteamInviteManager.Instance != null)
        {
            SteamInviteManager.Instance.OnInviteAccepted += HandleSteamInvite;
        }
    }

    // Handle when someone accepts a Steam invite
    private void HandleSteamInvite(string joinCode)
    {
        Debug.Log($"Handling Steam invite with join code: {joinCode}");
        JoinRelay(joinCode);
    }

    public async Task<string> CreateRelay()
    {
        try
        {
            // Treat MaxPlayers as TOTAL (host + clients)
            int clientSlots = Mathf.Max(1, MaxPlayers - 1); // Relay requires at least 1
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(clientSlots);
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            Debug.Log("Relay allocation created. Join code: " + joinCode);

            var connectionType = "dtls";
            var relayServerData = allocation.ToRelayServerData(connectionType);
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(relayServerData);

            NetworkManager.Singleton.StartHost();

            // Set Steam Rich Presence with join code
            if (SteamInviteManager.Instance != null)
            {
                SteamInviteManager.Instance.SetJoinCode(joinCode);
            }

            return joinCode;
        }
        catch (RelayServiceException e)
        {
            Debug.LogError("Failed to create Relay allocation: " + e);
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

            // Update Steam Rich Presence
            if (SteamInviteManager.Instance != null)
            {
                SteamInviteManager.Instance.SetInGame(joinCode);
            }
        }
        catch (RelayServiceException e)
        {
            Debug.LogError("Failed to join Relay allocation: " + e);
        }
    }

    // Clear Steam presence when leaving
    private void OnDestroy()
    {
        if (SteamInviteManager.Instance != null)
        {
            SteamInviteManager.Instance.OnInviteAccepted -= HandleSteamInvite;
            SteamInviteManager.Instance.ClearSession();
        }
    }
}
