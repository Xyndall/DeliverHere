using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using DeliverHere.Settings;

namespace DeliverHere.UI
{
    /// <summary>
    /// Controls a single row in the controls settings list.
    /// Shows the action name and either an icon or a binding text label.
    /// </summary>
    public class ControlBindingRowUI : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TMP_Text actionNameText;
        [SerializeField] private Image bindingIconImage;
        [SerializeField] private TMP_Text bindingText;

        /// <summary>
        /// Populates this row with the given control entry and input scheme.
        /// </summary>
        public void Setup(ControlEntry entry, bool isGamepad)
        {
            if (actionNameText != null)
                actionNameText.text = entry.displayName;

            Sprite icon = isGamepad ? entry.controllerIcon : entry.keyboardMouseIcon;

            if (icon != null)
            {
                // Show icon, hide text
                if (bindingIconImage != null)
                {
                    bindingIconImage.sprite = icon;
                    bindingIconImage.gameObject.SetActive(true);
                }

                if (bindingText != null)
                    bindingText.gameObject.SetActive(false);
            }
            else
            {
                // No icon assigned — fall back to reading the binding string from the Input System
                if (bindingIconImage != null)
                    bindingIconImage.gameObject.SetActive(false);

                if (bindingText != null)
                {
                    bindingText.text = GetBindingDisplayString(entry, isGamepad);
                    bindingText.gameObject.SetActive(true);
                }
            }
        }

        /// <summary>
        /// Reads the human-readable binding string for the relevant control scheme from the Input System.
        /// </summary>
        private string GetBindingDisplayString(ControlEntry entry, bool isGamepad)
        {
            if (entry.actionReference == null || entry.actionReference.action == null)
                return "?";

            InputAction action = entry.actionReference.action;
            string group = isGamepad ? entry.gamepadGroup : entry.keyboardGroup;

            foreach (InputBinding binding in action.bindings)
            {
                // Skip composite parent entries (e.g., "WASD")
                if (binding.isComposite) continue;

                if (!string.IsNullOrEmpty(binding.groups) && binding.groups.Contains(group))
                {
                    return InputControlPath.ToHumanReadableString(
                        binding.effectivePath,
                        InputControlPath.HumanReadableStringOptions.OmitDevice);
                }
            }

            // Fallback: return whatever the action returns by default
            return action.GetBindingDisplayString();
        }
    }
}