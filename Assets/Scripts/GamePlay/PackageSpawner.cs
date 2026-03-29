using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;

namespace DeliverHere.GamePlay
{
    [DisallowMultipleComponent]
    public class PackageSpawner : NetworkBehaviour
    {
        [Header("Package Prefab (must have NetworkObject)")]
        [SerializeField] private NetworkObject packagePrefab;

        [Header("Pipe / Spawn Point")]
        [SerializeField] private Transform pipeExit;

        [Header("Auto-Find Pipe (level-provided)")]
        [SerializeField] private bool autoFindPipe = true;
        [SerializeField] private string pipeNameKeyword = "pipe"; // search keyword (case-insensitive) used when auto-finding

        [Header("Parenting")]
        [SerializeField] private Transform spawnParent;
        [SerializeField] private bool useNetworkParenting = false;

        [Header("Spawn Defaults")]
        [SerializeField, Min(0)] private int initialSpawnCount = 10;
        [SerializeField, Min(0f)] private float defaultSpawnForce = 10f;
        [SerializeField, Min(0f)] private float defaultSpawnInterval = 0.1f;
        [SerializeField] private bool autoSpawnOnNetworkSpawn = false;

        private Coroutine spawnCoroutine;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            if (autoFindPipe && pipeExit == null)
                ResolvePipe();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (autoFindPipe && pipeExit == null)
                ResolvePipe();

            if (autoSpawnOnNetworkSpawn && IsServer)
            {
                SpawnPackages(initialSpawnCount, defaultSpawnForce, defaultSpawnInterval);
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!autoFindPipe) return;
            ResolvePipe(preferScene: scene);
        }

        private void OnSceneUnloaded(Scene scene)
        {
            // If the pipe belonged to the unloaded scene, clear reference so it can be re-resolved
            if (pipeExit != null)
            {
                var pScene = pipeExit.gameObject.scene;
                if (pScene == scene)
                {
                    pipeExit = null;
                }
            }

            if (autoFindPipe && pipeExit == null)
                ResolvePipe();
        }

        private void ResolvePipe(Scene? preferScene = null)
        {
            // If already assigned and active, nothing to do
            if (pipeExit != null && pipeExit.gameObject != null && pipeExit.gameObject.activeInHierarchy)
                return;

            Transform found = null;

            // Preferred scene first (useful when level is loaded additively)
            if (preferScene.HasValue)
            {
                var s = preferScene.Value;
                if (s.isLoaded)
                {
                    var rootObjects = s.GetRootGameObjects();
                    foreach (var ro in rootObjects)
                    {
                        if (ro == null) continue;
                        // search children for matching name
                        var candidates = ro.GetComponentsInChildren<Transform>(true);
                        foreach (var t in candidates)
                        {
                            if (t == null) continue;
                            if (NameLooksLikePipe(t.name))
                            {
                                found = t;
                                break;
                            }
                        }
                        if (found != null) break;
                    }
                }
            }

            // Fallback: search all Transforms in loaded scenes
            if (found == null)
            {
                var all = FindObjectsByType<Transform>(FindObjectsSortMode.None);
                foreach (var t in all)
                {
                    if (t == null) continue;
                    if (!t.gameObject.scene.IsValid()) continue;
                    if (NameLooksLikePipe(t.name))
                    {
                        found = t;
                        break;
                    }
                }
            }

            if (found != null)
            {
                pipeExit = found;
            }
        }

        private bool NameLooksLikePipe(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf(pipeNameKeyword, StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("pipeexit", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("pipe_exit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Server-only: spawn 'count' packages in sequence, applying impulse force from the pipe exit.
        /// If called while a spawn sequence is running, the existing sequence is stopped and replaced.
        /// </summary>
        public void SpawnPackages(int count, float force, float interval = 0.1f)
        {
            if (count <= 0) return;

            // If networked, only allow the server to perform authoritative spawns.
            if (NetworkManager.Singleton != null && !IsServer)
                return;

            if (packagePrefab == null || pipeExit == null)
                return;

            if (spawnCoroutine != null)
                StopCoroutine(spawnCoroutine);

            spawnCoroutine = StartCoroutine(SpawnSequenceCoroutine(count, force, interval));
        }

        /// <summary>
        /// Clients/players may call this to request the server spawn packages.
        /// Ownership is not required so any client can request; add checks if needed.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestSpawnPackagesServerRpc(int count, float force, float interval = 0)
        {
            // Provide a sane default if zero or negative interval passed from client
            float useInterval = interval > 0f ? interval : defaultSpawnInterval;
            SpawnPackages(count, force, useInterval);
        }

        private IEnumerator SpawnSequenceCoroutine(int count, float force, float interval)
        {
            int spawned = 0;
            while (spawned < count)
            {
                SpawnOne(force);
                spawned++;

                if (interval > 0f)
                    yield return new WaitForSeconds(interval);
                else
                    yield return null;
            }

            spawnCoroutine = null;
        }

        private void SpawnOne(float force)
        {
            if (packagePrefab == null || pipeExit == null) return;

            var instance = Instantiate(packagePrefab, pipeExit.position, pipeExit.rotation);

            if (NetworkManager.Singleton != null)
            {
                instance.Spawn(true);

                if (spawnParent != null)
                {
                    if (useNetworkParenting)
                    {
                        var parentNO = spawnParent.GetComponent<NetworkObject>();
                        if (parentNO != null && parentNO.IsSpawned)
                            instance.TrySetParent(parentNO, true);
                        else
                            instance.transform.SetParent(spawnParent, true);
                    }
                    else
                    {
                        instance.transform.SetParent(spawnParent, true);
                    }
                }
            }
            else
            {
                if (spawnParent != null) instance.transform.SetParent(spawnParent, true);
            }

            // Apply impulse force to the first Rigidbody found on the spawned object or its children.
            var rb = instance.GetComponentInChildren<Rigidbody>();
            if (rb != null)
            {
                rb.AddForce(pipeExit.forward * force, ForceMode.Impulse);
            }
        }
    }
}