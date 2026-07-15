using UnityEngine;
using UnityEngine.Audio;
using Unity.Netcode;
using System.Collections.Generic;

namespace DeliverHere.Audio
{
    /// <summary>
    /// Centralized audio management system.
    /// Handles all sound effect and music playback throughout the game.
    /// Uses object pooling for efficient AudioSource management.
    /// </summary>
    public class AudioManager : NetworkBehaviour // Changed from MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        [Header("References")]
        [SerializeField] private AudioLibrary audioLibrary;
        [SerializeField] private AudioMixer audioMixer;
        
        [Header("Audio Sources")]
        [SerializeField] private AudioSource musicSource;
        
        [Header("Pooling Settings")]
        [SerializeField] private int initialPoolSize = 10;
        [SerializeField] private int maxPoolSize = 30;
        
        [Header("Debug")]
        [SerializeField] private bool enableLogs = false;

        // SFX AudioSource pool
        private List<AudioSource> audioSourcePool = new List<AudioSource>();
        private Dictionary<string, float> lastPlayedTimes = new Dictionary<string, float>();
        
        // Currently playing music
        private AudioClipData currentMusic;

        private void Awake()
        {
            // Singleton pattern
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            InitializeAudioSources();
        }

        private void InitializeAudioSources()
        {
            // Create music source if not assigned
            if (musicSource == null)
            {
                GameObject musicObj = new GameObject("Music Source");
                musicObj.transform.SetParent(transform);
                musicSource = musicObj.AddComponent<AudioSource>();
                musicSource.loop = true;
                musicSource.playOnAwake = false;
            }

            // Create initial pool of AudioSources for SFX
            for (int i = 0; i < initialPoolSize; i++)
            {
                CreatePooledAudioSource();
            }

            if (enableLogs)
                Debug.Log($"[AudioManager] Initialized with {audioSourcePool.Count} pooled AudioSources.");
        }

        private AudioSource CreatePooledAudioSource()
        {
            GameObject sfxObj = new GameObject($"SFX Source {audioSourcePool.Count}");
            sfxObj.transform.SetParent(transform);
            AudioSource source = sfxObj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            audioSourcePool.Add(source);
            return source;
        }

        private AudioSource GetAvailableAudioSource()
        {
            // Find an available source
            foreach (var source in audioSourcePool)
            {
                if (!source.isPlaying)
                    return source;
            }

            // Create new source if under max pool size
            if (audioSourcePool.Count < maxPoolSize)
            {
                if (enableLogs)
                    Debug.Log($"[AudioManager] Expanding pool to {audioSourcePool.Count + 1} sources.");
                return CreatePooledAudioSource();
            }

            // Force reuse oldest source
            if (enableLogs)
                Debug.LogWarning("[AudioManager] All AudioSources busy. Reusing oldest.");
            return audioSourcePool[0];
        }

        /// <summary>
        /// Plays a sound effect locally on this client.
        /// </summary>
        public void PlaySFX(AudioClipData clipData, Vector3 position = default, Transform parent = null)
        {
            if (clipData == null || clipData.clip == null)
            {
                if (enableLogs)
                    Debug.LogWarning("[AudioManager] Attempted to play null AudioClipData.");
                return;
            }

            // Check cooldown
            string clipName = clipData.clip.name;
            if (lastPlayedTimes.TryGetValue(clipName, out float lastTime))
            {
                if (Time.time - lastTime < clipData.cooldown)
                {
                    if (enableLogs)
                        Debug.Log($"[AudioManager] Skipped '{clipName}' due to cooldown.");
                    return;
                }
            }
            lastPlayedTimes[clipName] = Time.time;

            // Get available AudioSource
            AudioSource source = GetAvailableAudioSource();
            
            // Configure AudioSource
            source.clip = clipData.clip;
            source.volume = clipData.volume;
            source.pitch = clipData.randomizePitch 
                ? clipData.pitch + Random.Range(-clipData.pitchVariation, clipData.pitchVariation)
                : clipData.pitch;
            source.spatialBlend = clipData.spatialBlend;
            source.loop = clipData.loop;
            source.outputAudioMixerGroup = clipData.mixerGroup;

            // Position and play
            if (clipData.spatialBlend > 0f)
            {
                source.transform.position = position;
                if (parent != null)
                    source.transform.SetParent(parent);
                else
                    source.transform.SetParent(transform);
            }

            source.Play();

            if (enableLogs)
                Debug.Log($"[AudioManager] Played SFX: {clipName}");
        }

        /// <summary>
        /// NEW: Plays a sound effect across the network for all clients.
        /// Call this from server to sync audio playback.
        /// </summary>
        public void PlaySFXNetworked(string clipName, Vector3 position = default)
        {
            if (NetworkManager.Singleton == null)
            {
                // Not in multiplayer, play locally
                PlaySFXByName(clipName, position);
                return;
            }

            if (IsServer)
            {
                // Server plays locally and tells clients to play
                PlaySFXByName(clipName, position);
                PlaySFXClientRpc(clipName, position);
            }
            else
            {
                // Client requests server to broadcast
                RequestPlaySFXServerRpc(clipName, position);
            }
        }

        [Rpc(SendTo.ClientsAndHost)]
        private void PlaySFXClientRpc(string clipName, Vector3 position)
        {
            PlaySFXByName(clipName, position);
        }

        [Rpc(SendTo.Server)]
        private void RequestPlaySFXServerRpc(string clipName, Vector3 position)
        {
            PlaySFXClientRpc(clipName, position);
        }

        /// <summary>
        /// Plays a sound effect by name from the library.
        /// </summary>
        public void PlaySFXByName(string clipName, Vector3 position = default, Transform parent = null)
        {
            if (audioLibrary == null)
            {
                Debug.LogError("[AudioManager] AudioLibrary is not assigned!");
                return;
            }

            AudioClipData clipData = audioLibrary.GetClipByName(clipName);
            if (clipData != null)
            {
                PlaySFX(clipData, position, parent);
            }
        }

        /// <summary>
        /// Plays background music. Fades out previous music if any.
        /// </summary>
        public void PlayMusic(AudioClipData musicData, bool fadeIn = true, float fadeDuration = 1f)
        {
            if (musicData == null || musicData.clip == null)
            {
                Debug.LogWarning("[AudioManager] Attempted to play null music.");
                return;
            }

            // Stop current music
            if (currentMusic != null && fadeIn)
            {
                StopMusic(fadeDuration);
            }
            else
            {
                musicSource.Stop();
            }

            currentMusic = musicData;
            musicSource.clip = musicData.clip;
            musicSource.volume = fadeIn ? 0f : musicData.volume;
            musicSource.pitch = musicData.pitch;
            musicSource.loop = musicData.loop;
            musicSource.outputAudioMixerGroup = musicData.mixerGroup;
            musicSource.Play();

            if (fadeIn)
            {
                StartCoroutine(FadeMusic(musicSource, musicData.volume, fadeDuration));
            }

            if (enableLogs)
                Debug.Log($"[AudioManager] Playing music: {musicData.clip.name}");
        }

        /// <summary>
        /// Stops currently playing music.
        /// </summary>
        public void StopMusic(float fadeDuration = 1f)
        {
            if (musicSource.isPlaying)
            {
                if (fadeDuration > 0f)
                {
                    StartCoroutine(FadeMusic(musicSource, 0f, fadeDuration, () => musicSource.Stop()));
                }
                else
                {
                    musicSource.Stop();
                }
            }
            currentMusic = null;
        }

        private System.Collections.IEnumerator FadeMusic(AudioSource source, float targetVolume, float duration, System.Action onComplete = null)
        {
            float startVolume = source.volume;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                source.volume = Mathf.Lerp(startVolume, targetVolume, elapsed / duration);
                yield return null;
            }

            source.volume = targetVolume;
            onComplete?.Invoke();
        }

        /// <summary>
        /// Stops all currently playing sound effects.
        /// </summary>
        public void StopAllSFX()
        {
            foreach (var source in audioSourcePool)
            {
                if (source.isPlaying && !source.loop)
                    source.Stop();
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Debug/Test Button Click Sound")]
        private void TestButtonClick()
        {
            if (audioLibrary?.buttonClick != null)
                PlaySFX(audioLibrary.buttonClick);
        }
#endif
    }
}