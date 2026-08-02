using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

[RequireComponent(typeof(SplineAnimate))]
public class SplineRandomSwitcher : MonoBehaviour
{
    [Header("Spline Switching Settings")]
    [Tooltip("List of Spline Containers to randomly switch between.")]
    public List<SplineContainer> splinesToSwitch = new List<SplineContainer>();

    [Tooltip("Minimum time in seconds before the object can switch splines again.")]
    public float minSwitchInterval = 3f;

    [Tooltip("Maximum time in seconds before the object can switch splines again.")]
    public float maxSwitchInterval = 8f;

    [Tooltip("If true, the object will loop on its current spline before switching.")]
    public bool waitForLoopEnd = true;

    private SplineAnimate splineAnimate;
    private float switchTimer;
    private bool isReadyToSwitch = true;

    void Start()
    {
        splineAnimate = GetComponent<SplineAnimate>();

        if (splinesToSwitch.Count == 0)
        {
            Debug.LogError("SplineRandomSwitcher: No splines assigned in the 'splinesToSwitch' list.");
            enabled = false;
            return;
        }

        // Set the initial random spline
        SwitchToRandomSpline();
        SetRandomSwitchTimer();
    }

    void Update()
    {
        if (splinesToSwitch.Count == 0) return;

        // Timer to determine when to switch
        switchTimer -= Time.deltaTime;

        if (switchTimer <= 0 && isReadyToSwitch)
        {
            if (waitForLoopEnd)
            {
                // Check if the spline animation has finished one loop
                // This uses a normalized time check. It's a common way to detect a loop event [citation:10].
                if (splineAnimate.NormalizedTime >= 0.99f)
                {
                    SwitchToRandomSpline();
                    SetRandomSwitchTimer();
                }
            }
            else
            {
                // If we don't wait for loop end, we just switch immediately.
                // To make this look smoother, it can be helpful to snap the object to the start of the new spline.
                SwitchToRandomSpline(true);
                SetRandomSwitchTimer();
            }
        }
    }

    /// <summary>
    /// Switches the SplineAnimate component to a random spline from the list.
    /// </summary>
    /// <param name="resetToStart">If true, resets the animation to the beginning of the new spline.</param>
    void SwitchToRandomSpline(bool resetToStart = false)
    {
        if (splinesToSwitch.Count == 0) return;

        // Choose a random spline from the list
        int randomIndex = Random.Range(0, splinesToSwitch.Count);
        SplineContainer targetSpline = splinesToSwitch[randomIndex];

        // Assign the new spline to the SplineAnimate component
        splineAnimate.Container = targetSpline;

        if (resetToStart)
        {
            splineAnimate.NormalizedTime = 0f; // Jump to the start of the new path
        }
        else
        {
            // When switching without resetting, you can try to find the closest point on the new spline
            // to maintain a smooth transition. This is a simple version that just starts from the beginning.
            splineAnimate.NormalizedTime = 0f;
            Debug.Log($"Switched to spline: {targetSpline.name}");
        }

        // Allow a brief pause before the next switch can be considered [citation:12]
        isReadyToSwitch = false;
        StartCoroutine(EnableSwitchAfterFrame());
    }

    IEnumerator EnableSwitchAfterFrame()
    {
        yield return null; // Wait one frame
        isReadyToSwitch = true;
    }

    void SetRandomSwitchTimer()
    {
        switchTimer = Random.Range(minSwitchInterval, maxSwitchInterval);
    }
}