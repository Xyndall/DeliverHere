using UnityEngine;
using UnityEngine.UI;
using DeliverHere.Steam;

namespace DeliverHere.UI
{
    /// <summary>
    /// Simple button that opens Steam's invite overlay.
    /// Just attach this to a button in your UI.
    /// </summary>
    public class SteamInviteButton : MonoBehaviour
    {
        private Button _button;

        private void Awake()
        {
            _button = GetComponent<Button>();
            if (_button != null)
            {
                _button.onClick.AddListener(OnInviteButtonClicked);
            }
        }

        private void Update()
        {
            // Enable button only if we're in a session
            if (_button != null && SteamInviteManager.Instance != null)
            {
                _button.interactable = SteamInviteManager.Instance.IsInSession();
            }
        }

        private void OnInviteButtonClicked()
        {
            if (SteamInviteManager.Instance != null)
            {
                SteamInviteManager.Instance.OpenInviteDialog();
            }
            else
            {
                Debug.LogWarning("SteamInviteManager not found!");
            }
        }

        private void OnDestroy()
        {
            if (_button != null)
            {
                _button.onClick.RemoveListener(OnInviteButtonClicked);
            }
        }
    }
}