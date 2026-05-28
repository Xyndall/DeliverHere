using System.Collections.Generic;
using DeliverHere.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace DeliverHere.UI
{
    /// <summary>
    /// Builds and manages the controls settings UI.
    /// Automatically switches between Keyboard/Mouse and Gamepad display
    /// based on the last device that sent input.
    /// </summary>
    public class ControlsSettingsUI : MonoBehaviour
    {
        [Header("Config")]
        [Tooltip("ScriptableObject listing all controls to display.")]
        [SerializeField] private ControlsConfig controlsConfig;

        [Header("UI References")]
        [Tooltip("Prefab with a ControlBindingRowUI component for each control row.")]
        [SerializeField] private GameObject controlRowPrefab;

        [Tooltip("Parent transform that rows are spawned under.")]
        [SerializeField] private Transform controlsContainer;

        [Tooltip("Optional: header/label shown when in Keyboard & Mouse mode.")]
        [SerializeField] private GameObject keyboardMouseHeader;

        [Tooltip("Optional: header/label shown when in Gamepad mode.")]
        [SerializeField] private GameObject gamepadHeader;

        [Tooltip("Optional: text label that shows the current input scheme name.")]
        [SerializeField] private TMP_Text activeSchemeLabel;

        [Header("Debug")]
        [SerializeField] private bool enableLogs = false;

        private bool _isGamepad = false;

        // Set from the InputSystem thread; consumed safely on the main thread in Update.
        private volatile bool _pendingSchemeChange = false;
        private volatile bool _pendingIsGamepad = false;

        private readonly List<ControlBindingRowUI> _rows = new List<ControlBindingRowUI>();

        private void OnEnable()
        {
            DetectCurrentDevice();
            BuildRows();

            // onEvent fires on the input system thread — only set a flag here, never touch Unity objects.
            InputSystem.onEvent += OnInputEvent;
        }

        private void OnDisable()
        {
            InputSystem.onEvent -= OnInputEvent;
        }

        private void Update()
        {
            // Apply any pending scheme change on the main thread where it is safe to touch Unity objects.
            if (_pendingSchemeChange)
            {
                _pendingSchemeChange = false;
                _isGamepad = _pendingIsGamepad;

                if (enableLogs)
                    Debug.Log($"[ControlsSettingsUI] Input scheme switched to: {(_isGamepad ? "Gamepad" : "Keyboard & Mouse")}");

                RefreshRows();
                UpdateSchemeHeaders();
            }
        }

        /// <summary>
        /// Checks connected devices to make an initial guess at the active scheme.
        /// Prefers Gamepad if one is connected and no keyboard is present.
        /// </summary>
        private void DetectCurrentDevice()
        {
            bool gamepadConnected = Gamepad.current != null;
            bool keyboardConnected = Keyboard.current != null;

            // Default to keyboard if both (or neither) are present.
            _isGamepad = gamepadConnected && !keyboardConnected;
        }

        /// <summary>
        /// Called on the InputSystem thread for every raw input event.
        /// Only sets a flag — never touches Unity objects here.
        /// </summary>
        private void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            bool deviceIsGamepad = device is Gamepad;

            // Early-out: no change, or a pending change of the same type already queued.
            if (deviceIsGamepad == _isGamepad && !_pendingSchemeChange) return;
            if (deviceIsGamepad == _pendingIsGamepad && _pendingSchemeChange) return;

            _pendingIsGamepad = deviceIsGamepad;
            _pendingSchemeChange = true;
        }

        /// <summary>
        /// Clears existing rows and spawns fresh ones from the config.
        /// </summary>
        private void BuildRows()
        {
            ClearRows();

            if (controlsConfig == null)
            {
                Debug.LogWarning("[ControlsSettingsUI] No ControlsConfig assigned.");
                return;
            }

            if (controlRowPrefab == null)
            {
                Debug.LogWarning("[ControlsSettingsUI] No controlRowPrefab assigned.");
                return;
            }

            if (controlsContainer == null)
            {
                Debug.LogWarning("[ControlsSettingsUI] No controlsContainer assigned.");
                return;
            }

            foreach (ControlEntry entry in controlsConfig.controls)
            {
                GameObject rowGO = Instantiate(controlRowPrefab, controlsContainer);
                ControlBindingRowUI row = rowGO.GetComponent<ControlBindingRowUI>();

                if (row != null)
                {
                    row.Setup(entry, _isGamepad);
                    _rows.Add(row);
                }
                else
                {
                    Debug.LogWarning("[ControlsSettingsUI] controlRowPrefab is missing a ControlBindingRowUI component.");
                }
            }

            UpdateSchemeHeaders();

            if (enableLogs)
                Debug.Log($"[ControlsSettingsUI] Built {_rows.Count} control rows.");
        }

        /// <summary>
        /// Re-runs Setup on all existing rows without rebuilding them.
        /// Called when the input scheme changes.
        /// </summary>
        private void RefreshRows()
        {
            if (controlsConfig == null) return;

            for (int i = 0; i < _rows.Count && i < controlsConfig.controls.Count; i++)
            {
                _rows[i].Setup(controlsConfig.controls[i], _isGamepad);
            }
        }

        /// <summary>
        /// Shows/hides the scheme header objects and updates the scheme label.
        /// </summary>
        private void UpdateSchemeHeaders()
        {
            if (keyboardMouseHeader != null)
                keyboardMouseHeader.SetActive(!_isGamepad);

            if (gamepadHeader != null)
                gamepadHeader.SetActive(_isGamepad);

            if (activeSchemeLabel != null)
                activeSchemeLabel.text = _isGamepad ? "Controller" : "Keyboard & Mouse";
        }

        private void ClearRows()
        {
            foreach (ControlBindingRowUI row in _rows)
            {
                if (row != null)
                    Destroy(row.gameObject);
            }

            _rows.Clear();
        }

#if UNITY_EDITOR
        [ContextMenu("Test Rebuild Rows")]
        private void TestRebuildRows() => BuildRows();

        [ContextMenu("Test Toggle Scheme")]
        private void TestToggleScheme()
        {
            _isGamepad = !_isGamepad;
            RefreshRows();
            UpdateSchemeHeaders();
        }
#endif
    }
}