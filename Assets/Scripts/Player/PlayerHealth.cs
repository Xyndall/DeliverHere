using UnityEngine;
using Unity.Netcode;
using System;

public interface IPlayerDamageable
{
    void ApplyDamage(int amount, Vector3 hitPoint);
}

public class PlayerHealth : MonoBehaviour, IPlayerDamageable
{
    [SerializeField] private int maxHealth = 100;
    [SerializeField] private int currentHealth;

    [Header("Damage Cooldown")]
    [SerializeField] private float damageCooldownSeconds = 0.5f;

    [Header("Upgradable Stats")]
    [SerializeField] private PlayerUpgradableStats upgradableStats;

    // Fired exactly once per death before respawn handling.
    public event Action<PlayerHealth> OnDied;

    private float _nextDamageAllowedTime = 0f;

    private int ResolvedMaxHealth => upgradableStats != null ? upgradableStats.MaxHealth : maxHealth;

    private void Awake()
    {
        if (upgradableStats == null) upgradableStats = GetComponent<PlayerUpgradableStats>();

        currentHealth = ResolvedMaxHealth;
        _nextDamageAllowedTime = 0f;

        if (upgradableStats != null)
            upgradableStats.OnStatsChanged += OnStatsChanged;
    }

    private void OnDestroy()
    {
        if (upgradableStats != null)
            upgradableStats.OnStatsChanged -= OnStatsChanged;
    }

    private void OnStatsChanged(PlayerUpgradableStats stats)
    {
        int newMax = ResolvedMaxHealth;
        currentHealth = Mathf.Clamp(currentHealth, 0, newMax);
    }

    public void ApplyDamage(int amount, Vector3 hitPoint)
    {
        if (amount <= 0) return;

        // Short cooldown: ignore damage until allowed again
        if (Time.time < _nextDamageAllowedTime)
        {
            return;
        }

        _nextDamageAllowedTime = Time.time + damageCooldownSeconds;

        currentHealth = Mathf.Max(0, currentHealth - amount);
        Debug.Log($"{gameObject.name} took {amount} damage at {hitPoint}, current health: {currentHealth}");
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
        currentHealth = ResolvedMaxHealth;

        // Reset cooldown to allow immediate post-respawn damage if needed
        _nextDamageAllowedTime = Time.time + damageCooldownSeconds;
    }

    private void RespawnAtSpawnPoint()
    {
        // Determine a spawn point
        var spawn = GameManager.Instance.GetRandomPlayerSpawn();
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

}