using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Spawns meteorites over time for a set duration. One-shot payload invoked by RandomEventSystem.
/// </summary>
public class MeteoriteEventSpawner : MonoBehaviour
{
    [Header("Spawn Areas")]
    [SerializeField] private List<BoxCollider> spawnAreas = new List<BoxCollider>();
    [SerializeField] private bool autoFindAreasIfEmpty = true;
    [SerializeField] private Vector3 fallbackCenter = Vector3.zero;
    [SerializeField] private Vector3 fallbackSize = new Vector3(50f, 0f, 50f);

    [Header("Meteorite Settings")]
    [SerializeField] private GameObject meteoritePrefab;
    [Tooltip("Total meteorites to emit over the event duration.")]
    [SerializeField] private int totalCount = 30;
    [Tooltip("Height above ground to drop meteorites from.")]
    [SerializeField] private float dropHeight = 40f;
    [Tooltip("Padding from area bounds to avoid edge spawns.")]
    [SerializeField] private float horizontalPadding = 2f;
    [SerializeField] private Transform meteoriteParent;

    [Header("Event Duration")]
    [Tooltip("How long this event lasts in seconds.")]
    [SerializeField] private float durationSeconds = 20f;
    [Tooltip("If true, spawn a small burst at start, then stream the rest.")]
    [SerializeField] private int burstAtStart = 0;

    [Header("Authority")]
    [SerializeField] private bool serverAuthoritative = true;

    private bool IsServerOrStandalone =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

    private void Start()
    {
        if (serverAuthoritative && !IsServerOrStandalone)
        {
            Destroy(gameObject);
            return;
        }

        if (meteoritePrefab == null)
        {
            Destroy(gameObject);
            return;
        }

        if (meteoriteParent == null)
        {
            var container = GameObject.Find("RuntimeEvents") ?? new GameObject("RuntimeEvents");
            meteoriteParent = container.transform;
            if (!meteoriteParent.gameObject.activeSelf)
                meteoriteParent.gameObject.SetActive(true);
        }

        // Discover areas
        if ((spawnAreas == null || spawnAreas.Count == 0) && autoFindAreasIfEmpty)
        {
            var markers = FindObjectsByType<MeteoriteSpawnArea>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var m in markers)
            {
                var col = m.AreaCollider;
                if (col != null && col.enabled) spawnAreas.Add(col);
            }
        }

        // Clamp inputs
        totalCount = Mathf.Max(0, totalCount);
        durationSeconds = Mathf.Max(0.1f, durationSeconds);
        burstAtStart = Mathf.Clamp(burstAtStart, 0, totalCount);

        // Run spawns over time
        StartCoroutine(SpawnOverTimeCoroutine());
    }

    private IEnumerator SpawnOverTimeCoroutine()
    {
        int spawned = 0;

        // Optional burst at start
        if (burstAtStart > 0)
        {
            for (int i = 0; i < burstAtStart; i++)
            {
                if (TrySpawnOneNetworked(out _))
                {
                    spawned++;
                }
            }
        }

        int remaining = totalCount - burstAtStart;
        if (remaining <= 0)
        {
            Destroy(gameObject);
            yield break;
        }

        // Emit remaining evenly over duration
        float streamDuration = durationSeconds;
        float interval = streamDuration / Mathf.Max(1, remaining); // seconds per meteorite
        float nextTime = Time.time;

        while (remaining > 0)
        {
            while (Time.time < nextTime)
                yield return null;

            if (TrySpawnOneNetworked(out _))
            {
                spawned++;
                remaining--;
            }
            else
            {
                // Try again shortly without consuming quota
                nextTime = Time.time + 0.05f;
                continue;
            }

            nextTime += interval;
        }

        Destroy(gameObject);
    }

    private bool TrySpawnOneNetworked(out Vector3 spawnPosOut)
    {
        spawnPosOut = Vector3.zero;
        if (!TryGetSpawnPosition(out var basePos))
            return false;

        var spawnPos = basePos + Vector3.up * Mathf.Max(1f, dropHeight);
        var rot = Quaternion.LookRotation(Vector3.down, Vector3.forward);

        // Instantiate
        var go = Instantiate(meteoritePrefab, spawnPos, rot, meteoriteParent);

        // If this prefab has a NetworkObject, spawn it so clients see it
        var netObj = go.GetComponent<NetworkObject>();
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && netObj != null && !netObj.IsSpawned)
        {
            netObj.Spawn(true);
        }
        // If running standalone, no network needed.

        // Initialize Meteorite behavior
        var meteor = go.GetComponent<Meteorite>();
        if (meteor != null)
        {
            meteor.SetFallParams(new Meteorite.FallParams
            {
                gravity = 30f,
                terminalVelocity = 60f,
                landDestroyDelay = 0f,
                impactRadius = 2.5f,
                impactDamage = 25,
                knockbackForce = 12f
            });
        }

        // Ensure visible
        if (!go.activeSelf) go.SetActive(true);
        if (go.transform.localScale == Vector3.zero)
            go.transform.localScale = Vector3.one;

        var scaleJitter = Random.Range(0.95f, 1.08f);
        go.transform.localScale *= scaleJitter;

        spawnPosOut = spawnPos;
        return true;
    }

    private bool TryGetSpawnPosition(out Vector3 position)
    {
        position = Vector3.zero;

        if (spawnAreas != null && spawnAreas.Count > 0)
        {
            var area = spawnAreas[Random.Range(0, spawnAreas.Count)];
            if (area == null) return false;

            var center = area.transform.TransformPoint(area.center);
            var size = Vector3.Scale(area.transform.lossyScale, area.size);
            var half = size * 0.5f - new Vector3(horizontalPadding, 0f, horizontalPadding);

            var offset = new Vector3(
                Random.Range(-half.x, half.x),
                0f,
                Random.Range(-half.z, half.z)
            );

            position = new Vector3(center.x + offset.x, center.y, center.z + offset.z);
            return true;
        }

        var halfFallback = fallbackSize * 0.5f - new Vector3(horizontalPadding, 0f, horizontalPadding);
        var off = new Vector3(
            Random.Range(-halfFallback.x, halfFallback.x),
            0f,
            Random.Range(-halfFallback.z, halfFallback.z)
        );
        position = fallbackCenter + off;
        return true;
    }
}