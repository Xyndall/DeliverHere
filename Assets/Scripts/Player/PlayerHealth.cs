using UnityEngine;
using Unity.Netcode;
using System;

/// <summary>
/// Optional interface your player can implement to receive damage.
/// Keeping this next to PlayerHealth for convenience.
/// </summary>
public interface IPlayerDamageable
{
    void ApplyDamage(int amount, Vector3 hitPoint);
}

public class PlayerHealth : MonoBehaviour, IPlayerDamageable
{
    [SerializeField] private int maxHealth = 100;
    [SerializeField] private int currentHealth;

    // Fired exactly once per death before respawn handling.
    public event Action<PlayerHealth> OnDied;

    private void Awake()
    {
        currentHealth = maxHealth;
    }

    public void ApplyDamage(int amount, Vector3 hitPoint)
    {
        currentHealth = Mathf.Max(0, currentHealth - amount);

        if (currentHealth <= 0)
        {
            Die();
        }
    }

    private void Die()
    {
        // Raise event for future effects (UI, VFX, score, etc.)
        OnDied?.Invoke(this);

        // For now: immediate respawn at a spawn point and restore health.
        RespawnAtSpawnPoint();
        currentHealth = maxHealth;
    }

    private void RespawnAtSpawnPoint()
    {
        // Determine a spawn point
        var spawn = FindSpawnPoint();
        if (spawn == null) return;

        // Teleport logic: handle standalone vs. netcode
        var netObj = GetComponent<NetworkObject>();
        var cc = GetComponent<CharacterController>();
        var rb = GetComponent<Rigidbody>();

        // Standalone or host can move immediately for local visuals
        if (cc != null) cc.enabled = false;
        transform.SetPositionAndRotation(spawn.position, spawn.rotation);
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        if (cc != null) cc.enabled = true;

        // If in a networked session and this object is spawned, request owner-side teleport for proper sync
        var netState = NetworkGameState.Instance ?? FindFirstObjectByType<NetworkGameState>();
        if (netState != null && netObj != null && netObj.IsSpawned && (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer))
        {
            netState.ServerRequestOwnerTeleport(netObj, spawn.position, spawn.rotation);
        }
    }

    private Transform FindSpawnPoint()
    {
        // Try tag-based spawn points first
        try
        {
            var tagged = GameObject.FindGameObjectsWithTag("PlayerSpawn");
            foreach (var go in tagged)
            {
                if (go != null && go.activeInHierarchy)
                    return go.transform;
            }
        }
        catch { /* Tag may not exist; fall back below */ }

        // Fallback: any transform in scene with name hint
        var allTransforms = FindObjectsByType<Transform>(FindObjectsSortMode.None);
        foreach (var t in allTransforms)
        {
            if (t == null || !t.gameObject.activeInHierarchy) continue;
            var name = t.name;
            if (name.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("SpawnPoint", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return t;
            }
        }

        // Last resort: current position (no-op)
        return transform;
    }
}