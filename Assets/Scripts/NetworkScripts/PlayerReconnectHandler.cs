using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace DeliverHere.Network
{
    /// <summary>
    /// Server-authoritative reconnect handler:
    /// - Tracks client disconnects / connects.
    /// - On (re)connect, teleports player to a spawn point and pushes current game UI snapshot.
    /// - Keeps HUD visibility consistent with GameManager.IsGameplayActive.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerReconnectHandler : NetworkBehaviour
    {
        private GameManager _gm;
        private NetworkGameState _netState;

        // Cache last known spawn index assignment for clients (optional simple round-robin)
        private int _nextSpawnIndex = 0;
        private readonly Dictionary<ulong, int> _clientSpawnIndex = new();

        private void Awake()
        {
            _gm = GameManager.Instance ?? FindFirstObjectByType<GameManager>();
            _netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            // Subscribe to Netcode client lifecycle
            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        public override void OnDestroy()
        {
            // Unsubscribe safely and call base
            if (NetworkManager != null)
            {
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            base.OnDestroy();
        }

        private void OnClientDisconnected(ulong clientId)
        {
            // Cleanup any cached mapping if desired
            _clientSpawnIndex.Remove(clientId);
        }

        private void OnClientConnected(ulong clientId)
        {
            if (!IsServer) return;

            // 1) Ensure references
            if (_gm == null) _gm = GameManager.Instance ?? FindFirstObjectByType<GameManager>();
            if (_netState == null) _netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
            if (_gm == null || _netState == null)
            {
                Debug.LogWarning("[PlayerReconnectHandler] Missing GameManager or NetworkGameState; cannot sync reconnect.");
                return;
            }

            // 2) Find the player's NetworkObject (owner-authoritative PlayerMovement)
            var players = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
            NetworkObject ownedPlayerNetObj = null;
            foreach (var pm in players)
            {
                if (pm == null) continue;
                var netObj = pm.GetComponent<NetworkObject>();
                if (netObj != null && netObj.IsSpawned && netObj.OwnerClientId == clientId)
                {
                    ownedPlayerNetObj = netObj;
                    break;
                }
            }

            // 3) Determine a spawn transform
            var spawnPoints = _gm.PlayerSpawnPoints;
            Transform spawn = null;
            if (spawnPoints != null && spawnPoints.Count > 0)
            {
                int index;
                if (!_clientSpawnIndex.TryGetValue(clientId, out index))
                {
                    index = _nextSpawnIndex % spawnPoints.Count;
                    _clientSpawnIndex[clientId] = index;
                    _nextSpawnIndex++;
                }

                // Prefer active spawn; fallback to index rotation
                var active = new List<Transform>();
                foreach (var t in spawnPoints)
                {
                    if (t != null && t.gameObject.activeInHierarchy) active.Add(t);
                }
                var source = active.Count > 0 ? active : spawnPoints;
                spawn = source[index % source.Count];
            }
            if (spawn == null) spawn = _gm.transform;

            // 4) Teleport the player (if we found them)
            if (ownedPlayerNetObj != null)
            {
                try
                {
                    _netState.ServerRequestOwnerTeleport(ownedPlayerNetObj, spawn.position, spawn.rotation);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            // 5) Push gameplay/UI snapshot to the reconnecting client
            PushSnapshotToClient(clientId);

            // 6) Ensure HUD visibility matches current gameplay state for this client
            SetHudVisibilityClientRpc(_gm.IsGameplayActive, new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { clientId }
                }
            });
        }

        private void PushSnapshotToClient(ulong clientId)
        {
            // Gather current game values
            var currentMoney = _gm.GetCurrentMoney();
            var targetMoney = _gm.GetTargetMoney();
            var bankedMoney = _gm.GetBankedMoney();
            var day = _gm.GetCurrentDay();
            var progress = _gm.GetProgress();

            ApplyUiSnapshotClientRpc(currentMoney, targetMoney, bankedMoney, day, progress, new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { clientId }
                }
            });
        }

        [ClientRpc]
        private void ApplyUiSnapshotClientRpc(int currentMoney, int targetMoney, int bankedMoney, int day, float progress, ClientRpcParams clientRpcParams = default)
        {
            // Client-side UI sync. This runs on the reconnecting client.
            var gm = GameManager.Instance ?? FindFirstObjectByType<GameManager>();
            var ui = gm != null ? GetUi(gm) : null;
            if (gm == null || ui == null) return;

            ui.SetDay(day);
            ui.SetTarget(targetMoney);
            ui.SetDailyEarnings(currentMoney, targetMoney, progress);
            ui.SetBankedMoney(bankedMoney);
        }

        [ClientRpc]
        private void SetHudVisibilityClientRpc(bool show, ClientRpcParams clientRpcParams = default)
        {
            var gm = GameManager.Instance ?? FindFirstObjectByType<GameManager>();
            var ui = gm != null ? GetUi(gm) : null;
            if (gm == null || ui == null) return;

            if (show)
            {
                ui.HideWinPanel();
                ui.HideDayEndSummary();
                ui.ShowHUD();
            }
            else
            {
                ui.HideHUD();
                ui.HideDayEndSummary();
                ui.HideWinPanel();
            }
        }

        private GameUIController GetUi(GameManager gm)
        {
            // GameManager already maintains the UIController; use it if present
            var uiField = typeof(GameManager).GetField("uiController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var ui = uiField?.GetValue(gm) as GameUIController;
            if (ui == null) ui = FindFirstObjectByType<GameUIController>();
            return ui;
        }
    }
}