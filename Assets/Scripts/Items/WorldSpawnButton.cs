using UnityEngine;
using Unity.Netcode;
using DeliverHere.GamePlay;

namespace DeliverHere.GamePlay
{
    [DisallowMultipleComponent]
    public class WorldSpawnButton : NetworkBehaviour
    {
        [Header("Spawn Settings")]
        [SerializeField, Min(0)] private int spawnCount = 1;
        [SerializeField, Min(0f)] private float spawnForce = 10f;
        [SerializeField, Min(0f)] private float spawnInterval = 0.1f;


        private PackageSpawner cachedSpawner;
        private void Start()
        {
            EnsureSpawner();
        }

        /// <summary>
        /// Called locally by the player (client or server). Will request server activation if needed.
        /// </summary>
        public void ActivateLocal()
        {
            // If running standalone or this instance is the server, run spawn directly.
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
            {
                cachedSpawner?.SpawnPackages(spawnCount, spawnForce, spawnInterval);
            }
            else
            {
                // Request the server to activate this button (any client can request)
                RequestActivateServerRpc();
            }
        }

        // Server RPC using the project's Rpc attribute style (allow everyone to invoke)
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestActivateServerRpc()
        {
            if (!IsServer) return;

            EnsureSpawner();
            cachedSpawner?.SpawnPackages(spawnCount, spawnForce, spawnInterval);
        }

        private void EnsureSpawner()
        {
            if (cachedSpawner != null) return;
            cachedSpawner = FindFirstObjectByType<PackageSpawner>();
            
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            spawnCount = Mathf.Max(0, spawnCount);
            spawnForce = Mathf.Max(0f, spawnForce);
            spawnInterval = Mathf.Max(0f, spawnInterval);
        }
#endif
    }
}