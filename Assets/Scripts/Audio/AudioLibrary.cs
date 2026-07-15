using UnityEngine;
using System.Collections.Generic;

namespace DeliverHere.Audio
{
    /// <summary>
    /// Organizes all game audio clips into categories.
    /// Create one ScriptableObject asset and populate it in the Inspector.
    /// </summary>
    [CreateAssetMenu(fileName = "Audio Library", menuName = "DeliverHere/Audio/Audio Library")]
    public class AudioLibrary : ScriptableObject
    {
        [Header("UI Sounds")]
        public AudioClipData buttonClick;
        public AudioClipData buttonHover;
        public AudioClipData panelOpen;
        public AudioClipData panelClose;
        
        [Header("Gameplay Sounds")]
        public AudioClipData packagePickup;
        public AudioClipData packageDrop;
        public AudioClipData packageCollision; // NEW: Collision/impact sound
        public AudioClipData packageDamage; // NEW: Damage/money loss sound
        public AudioClipData packageEnterZone; // NEW: Entering delivery zone
        public AudioClipData deliverySuccess;
        public AudioClipData deliveryFail;
        
        [Header("Player Sounds")]
        public AudioClipData footstep;
        public AudioClipData jump;
        public AudioClipData land;
        public AudioClipData hurt;
        
        [Header("Environment")]
        public AudioClipData conveyorBelt;
        public AudioClipData meteoriteImpact;
        
        [Header("Music")]
        public AudioClipData menuMusic;
        public AudioClipData gameplayMusic;
        public AudioClipData winMusic;
        
        // Helper method to get clip by name (optional)
        public AudioClipData GetClipByName(string clipName)
        {
            var field = GetType().GetField(clipName);
            if (field != null && field.FieldType == typeof(AudioClipData))
            {
                return field.GetValue(this) as AudioClipData;
            }
            Debug.LogWarning($"[AudioLibrary] Clip '{clipName}' not found.");
            return null;
        }
    }
}