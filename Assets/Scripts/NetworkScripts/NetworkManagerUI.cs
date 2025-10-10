using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Services.Core;
using Unity.Services.Authentication;

public class NetworkManagerUI : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private Button hostButton;
    [SerializeField] private Button joinButton;
    [SerializeField] private TMP_InputField joinCodeInput;
    [SerializeField] private TMP_Text joinCodeText;
    [SerializeField] private TMP_Text playerNameText;

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