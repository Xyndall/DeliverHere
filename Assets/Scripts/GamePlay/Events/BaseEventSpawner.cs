using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Base class for all event spawners. Handles common functionality like:
/// - Server authority
/// - Day-end cleanup
/// - Duration management
/// </summary>
public abstract class BaseEventSpawner : MonoBehaviour
{
    [Header("Event Duration")]
    [Tooltip("How long this event lasts in seconds. Set to 0 or negative to last until day ends.")]
    [SerializeField] protected float durationSeconds = 20f;

    [Header("Day Integration")]
    [SerializeField] protected bool autoFindGameTimer = true;
    [SerializeField] protected GameTimer gameTimer;

    [Header("Authority")]
    [SerializeField] protected bool serverAuthoritative = true;

    protected bool IsServerOrStandalone =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

    protected bool isEventActive = false;
    protected bool lastUntilDayEnd = false;

    protected virtual void Awake()
    {
        // Find game timer for day end detection
        if (gameTimer == null && autoFindGameTimer)
        {
            gameTimer = FindFirstObjectByType<GameTimer>();
        }

        if (gameTimer != null)
        {
            gameTimer.OnDayEndedEvaluated += HandleDayEnded;
        }
    }

    protected virtual void Start()
    {
        // Check server authority
        if (serverAuthoritative && !IsServerOrStandalone)
        {
            Destroy(gameObject);
            return;
        }

        // Determine if event should last until day end
        lastUntilDayEnd = durationSeconds <= 0f;
        isEventActive = true;

        // Let derived classes validate and initialize
        if (!ValidateAndInitialize())
        {
            Destroy(gameObject);
            return;
        }

        // Start the event
        OnEventStart();
    }

    /// <summary>
    /// Override this to validate required references and initialize the event.
    /// Return false to abort the event and destroy the spawner.
    /// </summary>
    protected abstract bool ValidateAndInitialize();

    /// <summary>
    /// Called when the event starts (after validation).
    /// </summary>
    protected abstract void OnEventStart();

    /// <summary>
    /// Called when the event should end (either by duration or day end).
    /// Perform cleanup here.
    /// </summary>
    protected abstract void OnEventEnd();

    /// <summary>
    /// Call this from your event coroutine to check if the event should continue.
    /// </summary>
    protected bool ShouldContinueEvent(float elapsed)
    {
        if (!isEventActive)
            return false;

        // If lasting until day end, continue indefinitely
        if (lastUntilDayEnd)
            return true;

        // Otherwise, check duration
        return elapsed < durationSeconds;
    }

    /// <summary>
    /// Call this to end the event manually (e.g., when duration expires).
    /// </summary>
    protected void EndEvent()
    {
        if (!isEventActive)
            return;

        isEventActive = false;
        OnEventEnd();
        Destroy(gameObject);
    }

    private void HandleDayEnded(bool success)
    {
        // Force event to end when day ends
        if (isEventActive)
        {
            EndEvent();
        }
    }

    protected virtual void OnDestroy()
    {
        // Unsubscribe from events
        if (gameTimer != null)
        {
            gameTimer.OnDayEndedEvaluated -= HandleDayEnded;
        }

        // Ensure cleanup happens
        if (isEventActive)
        {
            isEventActive = false;
            OnEventEnd();
        }
    }
}