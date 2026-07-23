using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Represents a blockade obstacle that blocks player movement.
/// Can have collision, health, and visual effects.
/// </summary>
[RequireComponent(typeof(Collider))]
public class Blockade : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("If true, blockade is indestructible during the event.")]
    [SerializeField] private bool isIndestructible = true;
    [Tooltip("If not indestructible, amount of health before destruction.")]
    [SerializeField] private float health = 100f;

    [Header("Visual Feedback")]
    [Tooltip("Optional: Visual effect when blockade spawns.")]
    [SerializeField] private GameObject spawnEffect;
    [Tooltip("Optional: Visual effect when blockade is destroyed.")]
    [SerializeField] private GameObject destroyEffect;
    [Tooltip("Optional: Animator for blockade animations.")]
    [SerializeField] private Animator animator;

    private float currentHealth;
    private bool isDestroyed = false;

    private void Start()
    {
        currentHealth = health;

        // Play spawn effect
        if (spawnEffect != null)
        {
            GameObject effect = Instantiate(spawnEffect, transform.position, Quaternion.identity);
            Destroy(effect, 3f);
        }

        // Trigger spawn animation
        if (animator != null)
        {
            animator.SetTrigger("Spawn");
        }
    }

    /// <summary>
    /// Apply damage to the blockade (if not indestructible).
    /// </summary>
    public void TakeDamage(float damage)
    {
        if (isIndestructible || isDestroyed)
            return;

        currentHealth -= damage;

        if (currentHealth <= 0f)
        {
            DestroyBlockade();
        }
    }

    private void DestroyBlockade()
    {
        if (isDestroyed)
            return;

        isDestroyed = true;

        // Play destroy effect
        if (destroyEffect != null)
        {
            GameObject effect = Instantiate(destroyEffect, transform.position, Quaternion.identity);
            Destroy(effect, 3f);
        }

        // Network despawn
        var netObj = GetComponent<NetworkObject>();
        if (netObj != null && netObj.IsSpawned)
        {
            netObj.Despawn(true);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Called by the event spawner when the event ends.
    /// </summary>
    public void RemoveBlockade()
    {
        // Could add a despawn animation here
        if (animator != null)
        {
            animator.SetTrigger("Despawn");
        }

        // Wait a moment for animation, then destroy
        Destroy(gameObject, 0.5f);
    }
}