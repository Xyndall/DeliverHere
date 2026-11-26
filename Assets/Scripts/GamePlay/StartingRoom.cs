using UnityEngine;

// Inherits from Room so it automatically has doorwaySockets and boundsColliders
public class StartingRoom : Room
{
    // Singleton-style static reference makes cross-scene lookup instant and zero-cost
    public static StartingRoom Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }
}
