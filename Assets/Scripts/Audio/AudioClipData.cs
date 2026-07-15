using UnityEngine;
using UnityEngine.Audio;

namespace DeliverHere.Audio
{
    /// <summary>
    /// Defines a single audio clip with its playback settings.
    /// </summary>
    [CreateAssetMenu(fileName = "New Audio Clip Data", menuName = "DeliverHere/Audio/Audio Clip Data")]
    public class AudioClipData : ScriptableObject
    {
        [Header("Audio Clip")]
        public AudioClip clip;
        
        [Header("Playback Settings")]
        [Range(0f, 1f)] public float volume = 1f;
        [Range(0.1f, 3f)] public float pitch = 1f;
        [Range(0f, 1f)] public float spatialBlend = 0f; // 0 = 2D, 1 = 3D
        
        [Header("Randomization")]
        public bool randomizePitch = false;
        [Range(0f, 0.5f)] public float pitchVariation = 0.1f;
        
        [Header("Audio Mixer")]
        [Tooltip("Which mixer group to play through (SFX, Music, etc.)")]
        public AudioMixerGroup mixerGroup;
        
        [Header("Looping")]
        public bool loop = false;
        
        [Header("Cooldown")]
        [Tooltip("Minimum time between plays to prevent audio spam")]
        public float cooldown = 0.1f;
    }
}