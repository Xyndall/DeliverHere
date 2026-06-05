using System;
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

    private void OnEnable()
    {
        if (hostButton != null) hostButton.onClick.AddListener(OnClickHost);
        if (joinButton != null) joinButton.onClick.AddListener(OnClickJoin);
        if (joinCodeInput != null) joinCodeInput.onValueChanged.AddListener(OnJoinCodeValueChanged);
    }

    private void OnDisable()
    {
        if (hostButton != null) hostButton.onClick.RemoveListener(OnClickHost);
        if (joinButton != null) joinButton.onClick.RemoveListener(OnClickJoin);
        if (joinCodeInput != null) joinCodeInput.onValueChanged.RemoveListener(OnJoinCodeValueChanged);
    }

    public async void OnClickHost()
    {
        if (Relay.Instance == null)
        {
            Debug.LogError("Relay component not found in scene. Add the Relay script to a GameObject.");
            return;
        }

        SetInteractable(false);
        try
        {
            string code = await Relay.Instance.CreateRelay();
            if (!string.IsNullOrEmpty(code))
            {
                SetJoinCodeText(code);

                // ADDED: Wait for NetworkUISync to spawn, then set join code on it
                await WaitForNetworkUISyncAndSetCode(code);

                // Move to Lobby or InGame, depending on your flow
                if (uiStateManager != null)
                {
                    uiStateManager.SetGameState(GameState.Lobby);
                }
            }
            else
            {
                Debug.LogError("Host failed: Join code was null/empty.");
                SetJoinCodeText("Host failed");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Exception while hosting: " + ex);
            SetJoinCodeText("Host failed");
        }
        finally
        {
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

            if (uiStateManager != null)
            {
                uiStateManager.SetGameState(GameState.Lobby);
            }
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