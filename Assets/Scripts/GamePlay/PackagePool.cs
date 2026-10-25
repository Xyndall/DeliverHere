using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

namespace DeliverHere.GamePlay
{
    /// <summary>
    /// Static object pool for networked package instances, mirroring the pattern used by
    /// FloatingWorldText. Pools are keyed per-prefab so multiple package prefabs can share
    /// this pool safely. Server-authoritative: pooling/reuse decisions only matter where
    /// NetworkObject.Spawn()/Despawn() are called (i.e. on the server).
    /// </summary>
    public static class PackagePool
    {
        private const int MaxPoolSize = 200;
        private const string PoolSceneName = "PackagePool_Pool";

        private static Scene _poolScene;
        private static bool _poolSceneCreated;

        private static readonly Dictionary<NetworkObject, Stack<NetworkObject>> _pools = new();

        // Tracks which prefab an instance came from, so callers can Release() without
        // needing to keep their own prefab reference around.
        private static readonly Dictionary<NetworkObject, NetworkObject> _instanceToPrefab = new();

        private static Scene PoolScene
        {
            get
            {
                if (!_poolSceneCreated || !_poolScene.IsValid())
                {
                    _poolScene = SceneManager.CreateScene(PoolSceneName,
                        new CreateSceneParameters(LocalPhysicsMode.None));
                    _poolSceneCreated = true;
                }
                return _poolScene;
            }
        }

        private static Stack<NetworkObject> GetPoolFor(NetworkObject prefab)
        {
            if (!_pools.TryGetValue(prefab, out var stack))
            {
                stack = new Stack<NetworkObject>();
                _pools[prefab] = stack;
            }
            return stack;
        }

        /// <summary>
        /// Gets a package instance for the given prefab, either reused from the pool or freshly instantiated.
        /// The returned instance is active and positioned at the given transform; caller is responsible
        /// for calling NetworkObject.Spawn() on it.
        /// </summary>
        public static NetworkObject Get(NetworkObject prefab, Vector3 position, Quaternion rotation)
        {
            if (prefab == null) return null;

            var pool = GetPoolFor(prefab);

            NetworkObject instance = null;
            while (pool.Count > 0 && instance == null)
            {
                instance = pool.Pop();
            }

            if (instance == null)
            {
                instance = Object.Instantiate(prefab, position, rotation);
            }
            else
            {
                // Move back into the active scene before reuse/spawn.
                SceneManager.MoveGameObjectToScene(instance.gameObject, SceneManager.GetActiveScene());
                instance.transform.SetPositionAndRotation(position, rotation);
                instance.gameObject.SetActive(true);
            }

            _instanceToPrefab[instance] = prefab;
            return instance;
        }

        /// <summary>
        /// Releases a package instance back to its originating prefab's pool (despawning it from the
        /// network layer without destroying the underlying GameObject), or destroys it if the pool is
        /// full or the instance's originating prefab is unknown.
        /// Server-only; call sites should guard with IsServer/authority checks before invoking this.
        /// </summary>
        public static void Release(NetworkObject instance)
        {
            if (instance == null) return;

            if (!_instanceToPrefab.TryGetValue(instance, out var prefab) || prefab == null)
            {
                DespawnAndDestroy(instance);
                return;
            }

            Release(prefab, instance);
        }

        /// <summary>
        /// Releases a package instance back to its prefab's pool (despawning it from the network layer
        /// without destroying the underlying GameObject), or destroys it if the pool is full.
        /// </summary>
        public static void Release(NetworkObject prefab, NetworkObject instance)
        {
            if (instance == null) return;

            if (prefab == null)
            {
                DespawnAndDestroy(instance);
                return;
            }

            var pool = GetPoolFor(prefab);

            if (pool.Count >= MaxPoolSize)
            {
                DespawnAndDestroy(instance);
                _instanceToPrefab.Remove(instance);
                return;
            }

            // Reset any package-specific state before pooling so the next reuse starts fresh.
            var props = instance.GetComponent<DeliverHere.Items.PackageProperties>()
                         ?? instance.GetComponentInChildren<DeliverHere.Items.PackageProperties>(true);
            props?.ResetForPool();

            // Detach to scene root while still spawned (NetworkObject only allows reparenting
            // while spawned, and only to null or another spawned NetworkObject's transform).
            if (instance.transform.parent != null)
            {
                instance.transform.SetParent(null, false);
            }

            if (instance.IsSpawned)
            {
                // Keep the GameObject alive (destroy: false) so it can be reused.
                instance.Despawn(false);
            }

            instance.gameObject.SetActive(false);

            // Group pooled instances into their own scene for Hierarchy organization. This does
            // NOT count as Transform reparenting as far as NetworkObject is concerned.
            SceneManager.MoveGameObjectToScene(instance.gameObject, PoolScene);

            pool.Push(instance);
        }

        private static void DespawnAndDestroy(NetworkObject instance)
        {
            _instanceToPrefab.Remove(instance);

            if (instance.IsSpawned)
            {
                instance.Despawn(true);
            }
            else
            {
                Object.Destroy(instance.gameObject);
            }
        }
    }
}