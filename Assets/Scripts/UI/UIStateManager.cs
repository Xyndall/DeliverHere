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

    [Header("Main UI Groups bound to game states (forced, server-authoritative)")]
    [SerializeField] private UIGroup[] uiGroups;

    [Header("Special Groups")]
    [SerializeField] private GameObject[] gameplayHUD;   // E.g. health, timer

    private GameState _currentState;

    private void Awake()
    {
        ApplyState(GameState.MainMenu);
    }

    /// <summary>
    /// Called ONLY by NetworkGameState in response to replicated GameState changes.
    /// Do not call this from anywhere else (e.g. join/connect handlers, menu buttons)
    /// or you will race with the replicated state and desync the UI between host and clients.
    /// Client-local menus (settings, upgrades, etc.) should NOT go through this class at all -
    /// toggle their GameObjects directly from their own controller.
    /// </summary>
    public void SetGameState(GameState newState)
    {
        _currentState = newState;
        ApplyState(_currentState);
    }

    private void ApplyState(GameState state)
    {
        Debug.Log($"[UIStateManager] Applying state: {state}");

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

        // Keep gameplay HUD visible during InGame and ReadyToStart states
        bool showGameplayHUD = (state == GameState.InGame || state == GameState.ReadyToStart);

        foreach (var go in gameplayHUD)
        {
            if (go != null)
            {
                go.SetActive(showGameplayHUD);
                Debug.Log($"[UIStateManager] Set gameplay HUD '{go.name}' active: {showGameplayHUD}");
            }
        }
    }
}