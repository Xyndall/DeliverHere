using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Place this on the warning UI object in your canvas (disabled by default).
/// Call Show() to activate it; it will disable itself after the lifetime expires.
/// </summary>
public class EventWarningUI : MonoBehaviour
{
    [SerializeField] private TMP_Text messageText;

    [Header("Audio")]
    [Tooltip("AudioSource used to play the warning sound.")]
    [SerializeField] private AudioSource audioSource;

    [Tooltip("Sound to play when the warning appears. Leave empty to skip.")]
    [SerializeField] private AudioClip warningSfx;  

    [Tooltip("How long the warning stays visible before hiding itself.")]
    [SerializeField] private float lifetime = 3f;

    private Coroutine _hideCoroutine;

    /// <summary>
    /// Sets the warning message text, activates the object, plays the SFX,
    /// and starts the countdown to hide itself.
    /// </summary>
    public void Show(string message)
    {
        // Stop any in-progress hide so overlapping calls restart cleanly
        if (_hideCoroutine != null)
        {
            StopCoroutine(_hideCoroutine);
            _hideCoroutine = null;
        }

        if (messageText != null)
            messageText.text = message;

        gameObject.SetActive(true);

        if (audioSource != null && warningSfx != null)
            audioSource.PlayOneShot(warningSfx);

        _hideCoroutine = StartCoroutine(HideAfterLifetime());
    }

    private IEnumerator HideAfterLifetime()
    {
        yield return new WaitForSeconds(lifetime);
        gameObject.SetActive(false);
        _hideCoroutine = null;
    }
}