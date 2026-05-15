using System;
using System.Collections.Generic;
using UnityEngine;

public class UIStateManager : MonoBehaviour
{
    [Serializable]
    public class UIGroup
    {
        public string name;
        public GameState state;          // When this group should be active
        public GameObject[] objects;     // Canvases / panels / roots
    }

    [Header("UI Groups bound to game states")]
    [SerializeField] private UIGroup[] uiGroups;

    [Header("Special Groups")]
    [SerializeField] private GameObject[] pauseOnlyUI;   // E.g. pause menu
    [SerializeField] private GameObject[] gameplayHUD;   // E.g. health, timer

    private GameState _currentState;
    private bool _isPaused;

    private void Awake()
    {
        ApplyState(GameState.MainMenu, false);
    }

    public void SetGameState(GameState newState)
    {
        _currentState = newState;
        ApplyState(_currentState, _isPaused);
    }

    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        
        // Only toggle pause menu visibility, don't change state
        foreach (var go in pauseOnlyUI)
        {
            if (go != null)
                go.SetActive(_isPaused);
        }
    }

    private void ApplyState(GameState state, bool paused)
    {
        // Base visibility by state
        foreach (var group in uiGroups)
        {
            bool active = group.state == state;
            foreach (var go in group.objects)
            {
                if (go != null)
                    go.SetActive(active);
            }
        }

        // Keep gameplay HUD visible during InGame and Paused states
        bool showGameplayHUD = (state == GameState.InGame || state == GameState.Paused);

        foreach (var go in gameplayHUD)
        {
            if (go != null)
                go.SetActive(showGameplayHUD);
        }

        // Apply pause menu visibility based on current pause state
        foreach (var go in pauseOnlyUI)
        {
            if (go != null)
                go.SetActive(paused);
        }
    }
}