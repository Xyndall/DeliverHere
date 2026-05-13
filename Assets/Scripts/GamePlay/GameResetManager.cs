using System.Collections;
using Unity.Netcode;
using UnityEngine;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Centralized manager for resetting the entire game state to defaults.
    /// Server-authoritative: handles resetting player stats, game progress, delivery zones, UI, and spawners.
    /// </summary>
    [DisallowMultipleComponent]
    public class GameResetManager : NetworkBehaviour
    {
        [Header("Core References")]
        [SerializeField] private GameManager gameManager;
        [SerializeField] private MoneyTargetManager moneyTargetManager;
        [SerializeField] private NetworkGameState networkGameState;
        [SerializeField] private GameUIController uiController;
        [SerializeField] private UIStateManager uiStateManager;
        [SerializeField] private GameTimer gameTimer;

        [Header("Level Systems")]
        [SerializeField] private DailyDeliveryZoneManager deliveryZoneManager;
        [SerializeField] private PackageSpawner packageSpawner;
        [SerializeField] private LevelFlowController levelFlowController;
        [SerializeField] private LevelLoader levelLoader;

        [Header("Auto-Find References")]
        [SerializeField] private bool autoFindReferences = true;

        [Header("Debug")]
        [SerializeField] private bool enableLogs = true;

        private bool _isUnloadingLevel = false;

        private void Awake()
        {
            if (autoFindReferences)
            {
                FindReferences();
            }
        }

        private void FindReferences()
        {
            if (gameManager == null)
                gameManager = GameManager.Instance ?? FindFirstObjectByType<GameManager>();

            if (moneyTargetManager == null)
                moneyTargetManager = FindFirstObjectByType<MoneyTargetManager>();

            if (networkGameState == null)
                networkGameState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();

            if (uiController == null)
                uiController = FindFirstObjectByType<GameUIController>();

            if (uiStateManager == null)
                uiStateManager = FindFirstObjectByType<UIStateManager>();

            if (gameTimer == null)
                gameTimer = FindFirstObjectByType<GameTimer>();

            if (deliveryZoneManager == null)
                deliveryZoneManager = FindFirstObjectByType<DailyDeliveryZoneManager>();

            if (packageSpawner == null)
                packageSpawner = FindFirstObjectByType<PackageSpawner>();

            if (levelFlowController == null)
                levelFlowController = FindFirstObjectByType<LevelFlowController>();

            if (levelLoader == null)
                levelLoader = LevelLoader.Instance ?? FindFirstObjectByType<LevelLoader>();
        }

        /// <summary>
        /// Performs a complete game reset. Should only be called by the server/host.
        /// Resets all player stats, game progress, delivery zones, UI, and spawned objects.
        /// </summary>
        public void PerformFullReset()
        {
            if (!NetworkManager.Singleton.IsServer)
            {
                Debug.LogWarning("[GameResetManager] PerformFullReset can only be called on server.");
                return;
            }

            if (enableLogs)
                Debug.Log("[GameResetManager] Starting full game reset...");

            StartCoroutine(FullResetSequence());
        }

        private IEnumerator FullResetSequence()
        {
            // Step 1: Reset game state and stop gameplay
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 1: Stopping gameplay and resetting game state...");

            if (gameManager != null)
            {
                gameManager.SetGameplayActive(false);
            }

            // Important: Set to Loading first to prevent button clicks during reset
            if (networkGameState != null)
            {
                networkGameState.ServerSetGameState(GameState.Loading, paused: false);
            }

            yield return null;

            // Step 2: Clear spawned packages before unloading level
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 2: Clearing spawned packages...");

            ClearSpawnedPackages();

            yield return null;

            // Step 3: Unload current level scene
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 3: Unloading current level...");

            if (levelLoader != null && levelLoader.HasCurrentLevel)
            {
                _isUnloadingLevel = true;
                
                // Subscribe to unload event
                System.Action<string> onLevelUnloaded = null;
                onLevelUnloaded = (sceneName) =>
                {
                    levelLoader.OnLevelUnloaded -= onLevelUnloaded;
                    _isUnloadingLevel = false;
                    
                    if (enableLogs)
                        Debug.Log($"[GameResetManager] Level '{sceneName}' unloaded successfully.");
                };
                
                levelLoader.OnLevelUnloaded += onLevelUnloaded;
                levelLoader.UnloadCurrentLevel();

                // Wait for unload to complete (with timeout)
                float timeout = 10f;
                float elapsed = 0f;
                while (_isUnloadingLevel && elapsed < timeout)
                {
                    elapsed += Time.deltaTime;
                    yield return null;
                }

                if (_isUnloadingLevel)
                {
                    Debug.LogWarning("[GameResetManager] Level unload timed out after 10 seconds.");
                    _isUnloadingLevel = false;
                }
            }
            else
            {
                if (enableLogs)
                    Debug.Log("[GameResetManager] No level currently loaded, skipping unload.");
            }

            yield return new WaitForSeconds(0.5f);

            // Step 4: Reset all player stats to default
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 4: Resetting player stats...");

            ResetAllPlayerStats();

            yield return null;

            // Step 5: Reset money and progress
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 5: Resetting money and quotas...");

            if (moneyTargetManager != null)
            {
                moneyTargetManager.ResetProgress();
            }

            yield return null;

            // Step 6: Reset delivery zones
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 6: Resetting delivery zones...");

            ResetDeliveryZones();

            yield return null;

            // Step 7: Timer will reset on next day start
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 7: Timer will reset on next day start...");

            yield return null;

            // Step 8: Reset UI
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 8: Resetting UI...");

            ResetUIClientRpc();

            yield return null;

            // Step 9: Teleport players to spawn
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 9: Repositioning players to hub spawn...");

            if (networkGameState != null)
            {
                networkGameState.ServerPositionAllPlayersToDefaultSpawn();
            }
            else if (gameManager != null)
            {
                gameManager.PositionPlayersToSpawnPoints();
            }

            yield return new WaitForSeconds(0.5f);

            // Step 10: Return to lobby state and ensure game can be restarted
            if (enableLogs)
                Debug.Log("[GameResetManager] Step 10: Returning to lobby state...");

            // Call RequestEndGameServerRpc to properly reset the gameStarted flag
            if (networkGameState != null)
            {
                // This will set gameStarted = false and state = Lobby
                networkGameState.RequestEndGameServerRpc();
            }
            else
            {
                // Fallback if NetworkGameState isn't available
                if (uiStateManager != null)
                {
                    uiStateManager.SetGameState(GameState.Lobby);
                }
                
                if (gameManager != null)
                {
                    gameManager.EndGame();
                }
            }

            yield return new WaitForSeconds(0.2f);

            if (enableLogs)
                Debug.Log("[GameResetManager] Full reset complete! Ready to start new game.");
        }

        /// <summary>
        /// Resets all connected player stats to their default values.
        /// Server-only operation.
        /// </summary>
        private void ResetAllPlayerStats()
        {
            var allPlayerStats = FindObjectsByType<PlayerUpgradableStats>(FindObjectsSortMode.None);

            foreach (var stats in allPlayerStats)
            {
                if (stats != null && stats.IsSpawned)
                {
                    stats.ServerResetToDefaults();

                    if (enableLogs)
                        Debug.Log($"[GameResetManager] Reset stats for player {stats.OwnerClientId}");
                }
            }

            // Clear server-side snapshot cache
            PlayerUpgradableStats.ServerClearSnapshotForClient(ulong.MaxValue); // Clear all
        }

        /// <summary>
        /// Resets all delivery zones to inactive state.
        /// </summary>
        private void ResetDeliveryZones()
        {
            // Reset the daily zone manager if present
            if (deliveryZoneManager != null)
            {
                // The zone manager will handle deactivating all zones when reset
                if (enableLogs)
                    Debug.Log("[GameResetManager] Daily delivery zone manager found and will be reset on next day start.");
            }

            // Also manually reset all delivery zone definitions
            var allZones = FindObjectsByType<DeliveryZoneDefinition>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var zone in allZones)
            {
                if (zone != null)
                {
                    zone.DeactivateZone();

                    if (zone.DeliveryZone != null)
                    {
                        zone.DeliveryZone.ResetForNewDay();
                    }
                }
            }

            // Reset all package delivery zones directly
            var deliveryZones = FindObjectsByType<PackageDeliveryZone>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var zone in deliveryZones)
            {
                if (zone != null && zone.IsSpawned)
                {
                    zone.ResetForNewDay();
                }
            }

            if (enableLogs)
                Debug.Log($"[GameResetManager] Reset {allZones.Length} delivery zone definitions and {deliveryZones.Length} package delivery zones.");
        }

        /// <summary>
        /// Clears all spawned packages from the scene.
        /// </summary>
        private void ClearSpawnedPackages()
        {
            var allHoldables = FindObjectsByType<Holdable>(FindObjectsSortMode.None);
            int clearedCount = 0;

            foreach (var holdable in allHoldables)
            {
                if (holdable != null && holdable.TryGetComponent<NetworkObject>(out var netObj))
                {
                    if (netObj.IsSpawned)
                    {
                        netObj.Despawn(true);
                        clearedCount++;
                    }
                }
            }

            if (enableLogs)
                Debug.Log($"[GameResetManager] Cleared {clearedCount} spawned packages.");
        }

        /// <summary>
        /// Resets UI state on all clients.
        /// </summary>
        [ClientRpc]
        private void ResetUIClientRpc()
        {
            if (uiController != null)
            {
                uiController.HideHUD();
                uiController.HideWinPanel();
                uiController.HideDayEndSummary();
                uiController.ClearUpgradePrompt();
                uiController.SetDay(0);
                uiController.SetBankedMoney(0);
                uiController.SetDeliveryZoneValue(0, 0);
                uiController.SetTimerVisible(false);
            }

            if (uiStateManager != null)
            {
                uiStateManager.SetGameState(GameState.Lobby);
                uiStateManager.SetPaused(false);
            }

            if (enableLogs)
                Debug.Log("[GameResetManager] UI reset on client.");
        }

        #region Context Menu Debug Methods
#if UNITY_EDITOR
        [ContextMenu("Debug/Perform Full Reset (Server Only)")]
        private void Debug_PerformFullReset()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[GameResetManager] Can only reset during play mode.");
                return;
            }

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            {
                Debug.LogWarning("[GameResetManager] Full reset can only be called on server.");
                return;
            }

            PerformFullReset();
        }

        [ContextMenu("Debug/Find All References")]
        private void Debug_FindReferences()
        {
            FindReferences();
            Debug.Log("[GameResetManager] References found and assigned.");
        }
#endif
        #endregion
    }
}