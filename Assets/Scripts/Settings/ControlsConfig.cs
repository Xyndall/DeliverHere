using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DeliverHere.Settings
{
    /// <summary>
    /// ScriptableObject that defines all game controls to display in the controls settings UI.
    /// Create via: Assets > Create > DeliverHere > Settings > Controls Config
    /// </summary>
    [CreateAssetMenu(fileName = "ControlsConfig", menuName = "DeliverHere/Settings/Controls Config", order = 2)]
    public class ControlsConfig : ScriptableObject
    {
        [Tooltip("List of all controls to display in the controls settings panel.")]
        public List<ControlEntry> controls = new List<ControlEntry>();
    }

    [Serializable]
    public class ControlEntry
    {
        [Tooltip("The display name shown next to the binding, e.g. \"Jump\" or \"Sprint\".")]
        public string displayName;

        [Tooltip("Reference to the Input Action (from your Input Actions asset).")]
        public InputActionReference actionReference;

        [Tooltip("Icon displayed when using Keyboard & Mouse. Leave empty to show binding text instead.")]
        public Sprite keyboardMouseIcon;

        [Tooltip("Icon displayed when using a Gamepad/Controller. Leave empty to show binding text instead.")]
        public Sprite controllerIcon;

        [Tooltip("Input binding group name for Keyboard & Mouse scheme (must match your Input Actions asset).")]
        public string keyboardGroup = "Keyboard&Mouse";

        [Tooltip("Input binding group name for Gamepad scheme (must match your Input Actions asset).")]
        public string gamepadGroup = "Gamepad";
    }
}