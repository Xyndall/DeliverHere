using System;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Services.Core;
using Unity.Services.Authentication;
using DeliverHere.NetworkScripts;

public class NetworkManagerUI : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private Button hostButton;
    [SerializeField] private Button joinButton;
    [SerializeField] private TMP_InputField joinCodeInput;
    [SerializeField] private TMP_Text joinCodeText;
    [SerializeField] private TMP_Text playerNameText;

    public UIStateManager uiStateManager; // assign via inspector

    private bool _subscribedToRelay = false;

    private void OnEnable()
    {
        if (hostButton != null) hostButton.onClick.AddListener(OnClickHost);
        if (joinButton != null) joinButton.onClick.AddListener(OnClickJoin);
        if (joinCodeInput != null) joinCodeInput.onValueChanged.AddListener(OnJoinCodeValueChanged);

        // FIXED: Relay.Instance might not exist yet (Awake ordering / scene load timing).
        // Try immediately, and if it's not ready, keep retrying via coroutine until it is.
        TrySubscribeToRelay();
        if (!_subscribedToRelay)
        {
            StartCoroutine(SubscribeToRelayWhenReady());
        }
    }

    private void TrySubscribeToRelay()
    {
        if (_subscribedToRelay) return;

        if (Relay.Instance != null)
        {
            Relay.Instance.OnClientJoined += HandleRelayJoined;
            Relay.Instance.OnClientJoinFailed += HandleRelayJoinFailed;
            _subscribedToRelay = true;
            Debug.Log("[NetworkManagerUI] Subscribed to Relay events.");
        }
    }

    private IEnumerator SubscribeToRelayWhenReady()
    {
        float timeout = 5f;
        float elapsed = 0f;

        while (!_subscribedToRelay && elapsed < timeout)
        {
            yield return new WaitForSeconds(0.1f);
            elapsed += 0.1f;
            TrySubscribeToRelay();
        }

        if (!_subscribedToRelay)
        {
            Debug.LogWarning("[NetworkManagerUI] Could not find Relay.Instance to subscribe to after timeout!");
        }
    }

    private void OnDisable()
    {
        if (hostButton != null) hostButton.onClick.RemoveListener(OnClickHost);
        if (joinButton != null) joinButton.onClick.RemoveListener(OnClickJoin);
        if (joinCodeInput != null) joinCodeInput.onValueChanged.RemoveListener(OnJoinCodeValueChanged);

        if (Relay.Instance != null && _subscribedToRelay)
        {
            Relay.Instance.OnClientJoined -= HandleRelayJoined;
            Relay.Instance.OnClientJoinFailed -= HandleRelayJoinFailed;
        }
        _subscribedToRelay = false;
    }

    // Central handler that closes the main menu / moves to Lobby state,
    // regardless of what triggered the join (manual, Steam invite, cold launch).
    private void HandleRelayJoined()
    {
        Debug.Log("[NetworkManagerUI] HandleRelayJoined fired.");

        // Do NOT touch UIStateManager here. NetworkGameState owns the
        // forced/main UI state and will apply it via nvGameState replication
        // (see NetworkGameState.ApplyUiStateToLocal). Setting it here races
        // with that replicated state and can leave stale menus open/closed.
        SetInteractable(true);
    }

    private void HandleRelayJoinFailed(string error)
    {
        Debug.LogError("Relay join/host failed: " + error);
        SetJoinCodeText("Failed");
        SetInteractable(true);
    }

    public async void OnClickHost()
    {
        if (Relay.Instance == null)
        {
            Debug.LogError("Relay component not found in scene. Add the Relay script to a GameObject.");
            return;
        }

        // FIXED: Ensure subscription exists before hosting, in case OnEnable's retry hadn't caught it yet.
        TrySubscribeToRelay();

        SetInteractable(false);
        try
        {
            string code = await Relay.Instance.CreateRelay();
            if (!string.IsNullOrEmpty(code))
            {
                SetJoinCodeText(code);

                // ADDED: Wait for NetworkUISync to spawn, then set join code on it
                await WaitForNetworkUISyncAndSetCode(code);

            }
            else
            {
                Debug.LogError("Host failed: Join code was null/empty.");
                SetJoinCodeText("Host failed");
                SetInteractable(true);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Exception while hosting: " + ex);
            SetJoinCodeText("Host failed");
            SetInteractable(true);
        }
    }

    // ADDED: Wait for NetworkUISync to be ready and set join code
    private async Task WaitForNetworkUISyncAndSetCode(string code)
    {
        // Wait up to 5 seconds for NetworkUISync to spawn
        float timeout = 5f;
        float elapsed = 0f;

        while (NetworkUISync.Instance == null && elapsed < timeout)
        {
            await Task.Delay(100);
            elapsed += 0.1f;
        }

        if (NetworkUISync.Instance != null)
        {
            NetworkUISync.Instance.ServerSetJoinCode(code);
            Debug.Log($"[NetworkManagerUI] Set join code on NetworkUISync: {code}");
        }
        else
        {
            Debug.LogWarning("[NetworkManagerUI] NetworkUISync not found after timeout. Join code won't sync to clients.");
        }
    }

    public void OnClickJoin()
    {
        if (Relay.Instance == null)
        {
            Debug.LogError("Relay component not found in scene. Add the Relay script to a GameObject.");
            return;
        }

        TrySubscribeToRelay();

        string code = (joinCodeInput != null ? joinCodeInput.text : string.Empty)?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            Debug.LogWarning("Join code is empty.");
            return;
        }

        // Normalize code (Relay codes are typically uppercase)
        code = code.ToUpperInvariant();

        SetInteractable(false);
        try
        {
            Relay.Instance.JoinRelay(code);

            // NOTE: UI state transition to Lobby happens via HandleRelayJoined(),
            // triggered by Relay.OnClientJoined, once the join actually succeeds.
        }
        catch (Exception ex)
        {
            Debug.LogError("Exception while joining: " + ex);
            SetInteractable(true);
        }
    }

    public void OnJoinCodeValueChanged(string value)
    {
        // Optional: keep the input uppercase as user types
        if (joinCodeInput != null && value != null)
        {
            int caret = joinCodeInput.caretPosition;
            string upper = value.ToUpperInvariant();
            if (!string.Equals(joinCodeInput.text, upper, StringComparison.Ordinal))
            {
                joinCodeInput.text = upper;
                joinCodeInput.caretPosition = Mathf.Clamp(caret, 0, joinCodeInput.text.Length);
            }
        }
    }

    private void SetJoinCodeText(string code)
    {
        if (joinCodeText != null)
        {
            joinCodeText.text = string.IsNullOrEmpty(code) ? string.Empty : $"CODE: {code.ToUpperInvariant()}";
        }
    }

    private void SetInteractable(bool interactable)
    {
        if (hostButton != null) hostButton.interactable = interactable;
        if (joinButton != null) joinButton.interactable = interactable;
        if (joinCodeInput != null) joinCodeInput.interactable = interactable;
    }
}