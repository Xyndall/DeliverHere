using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace DeliverHere.Network
{
    [DisallowMultipleComponent]
    public class PlayerReconnectHandler : NetworkBehaviour
    {
        private GameManager _gm;
        private NetworkGameState _netState;

        [Header("Join Teleport")]
        [Tooltip("Seconds to wait for the player's NetworkObject to spawn after connect.")]
        [SerializeField] private float waitForPlayerSpawnTimeoutSeconds = 5f;

        private readonly Dictionary<ulong, Coroutine> _pending = new();

        private void Awake()
        {
            _gm = GameManager.Instance ?? FindFirstObjectByType<GameManager>();
            _netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        public override void OnDestroy()
        {
            if (NetworkManager != null)
            {
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            foreach (var kvp in _pending)
            {
                if (kvp.Value != null)
                    StopCoroutine(kvp.Value);
            }
            _pending.Clear();

            base.OnDestroy();
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_pending.TryGetValue(clientId, out var co) && co != null)
                StopCoroutine(co);

            _pending.Remove(clientId);
        }

        private void OnClientConnected(ulong clientId)
        {
            if (!IsServer) return;

            if (_gm == null) _gm = GameManager.Instance ?? FindFirstObjectByType<GameManager>();
            if (_netState == null) _netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
            if (_gm == null || _netState == null)
            {
                Debug.LogWarning("[PlayerReconnectHandler] Missing GameManager or NetworkGameState; cannot sync reconnect.");
                return;
            }

            if (_pending.TryGetValue(clientId, out var existing) && existing != null)
                StopCoroutine(existing);

            _pending[clientId] = StartCoroutine(WaitForPlayerThenTeleportAndSync(clientId));
        }

        private IEnumerator WaitForPlayerThenTeleportAndSync(ulong clientId)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, waitForPlayerSpawnTimeoutSeconds);

            NetworkObject playerNetObj = null;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (NetworkManager != null &&
                    NetworkManager.ConnectedClients != null &&
                    NetworkManager.ConnectedClients.TryGetValue(clientId, out var cc) &&
                    cc != null &&
                    cc.PlayerObject != null)
                {
                    playerNetObj = cc.PlayerObject;
                    break;
                }

                yield return null;
            }

            _pending.Remove(clientId);

            if (playerNetObj == null)
            {
                Debug.LogWarning($"[PlayerReconnectHandler] Timed out waiting for player object for client {clientId}; cannot teleport.");
                yield break;
            }

            // If gameplay is already active, DO NOT hub-teleport here.
            if (_netState != null && _netState.LocalGameState == GameState.InGame)
            {
                SetHudVisibilityClientRpc(_gm.IsGameplayActive, new ClientRpcParams
                {
                    Send = new ClientRpcSendParams
                    {
                        TargetClientIds = new[] { clientId }
                    }
                });

                yield break;
            }

            // Teleport after spawn to hub/default spawn (Lobby/Loading/etc.)
            var spawn = _netState.DefaultSpawnPoint != null ? _netState.DefaultSpawnPoint : _netState.transform;
            try
            {
                _netState.ServerRequestOwnerTeleport(playerNetObj, spawn.position, spawn.rotation);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            // Money/target/day UI is now driven from MoneyTargetManager NetworkVariables.
            // We only ensure HUD visibility state for the reconnecting client.
            SetHudVisibilityClientRpc(_gm.IsGameplayActive, new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { clientId }
                }
            });
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
            var uiField = typeof(GameManager).GetField("uiController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var ui = uiField?.GetValue(gm) as GameUIController;
            if (ui == null) ui = FindFirstObjectByType<GameUIController>();
            return ui;
        }
    }
}