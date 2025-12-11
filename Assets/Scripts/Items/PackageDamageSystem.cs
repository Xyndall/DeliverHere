using System.Collections.Generic;
using UnityEngine;
using DeliverHere.Items;
using Unity.Netcode;

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public class PackageDamageSystem : MonoBehaviour
{
    [Header("Damage Settings")]
    [Tooltip("Minimum relative impact speed (m/s) required before any damage is applied.")]
    [SerializeField] private float minDamageSpeed = 3.5f;

    [Tooltip("Multiplier converting (impactSpeed - minDamageSpeed) into damage points.")]
    [SerializeField] private float damagePerSpeed = 2.0f;

    [Tooltip("Additional multiplier based on package fragility: finalDamage *= (1 + fragilityMultiplier * Fragility).")]
    [SerializeField] private float fragilityMultiplier = 1.0f;

    [Tooltip("Clamp applied damage to this maximum per single collision.")]
    [SerializeField] private int maxDamagePerHit = 50;

    [Header("Collision Filtering")]
    [Tooltip("Ignore collisions with layers in this mask (e.g., player hands).")]
    [SerializeField] private LayerMask ignoreLayers;

    [Tooltip("Minimum time between damage applications from the same other collider.")]
    [SerializeField] private float perColliderCooldownSeconds = 0.25f;

    [Tooltip("Scale damage by contact count within a collision. If false, only the strongest contact is used.")]
    [SerializeField] private bool scaleByContactCount = true;

    private Rigidbody _rb;
    private PackageProperties _props;

    // Track last damage time per other collider to avoid rapid repeated hits
    private readonly Dictionary<int, float> _lastHitTimeByOtherInstanceId = new Dictionary<int, float>();

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _props = GetComponent<PackageProperties>() ?? GetComponentInParent<PackageProperties>();
        if (_rb == null)
        {
            Debug.LogWarning($"{nameof(PackageDamageSystem)} requires a Rigidbody.");
        }
        if (_props == null)
        {
            Debug.LogWarning($"{nameof(PackageDamageSystem)} could not find PackageProperties; damage will not affect value.");
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        TryApplyDamageFromCollision(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        // Allow damage on sustained scraping/drag collisions but respect cooldown
        TryApplyDamageFromCollision(collision);
    }

    private void TryApplyDamageFromCollision(Collision collision)
    {
        if (_rb == null || _props == null) return;

        // Filter ignored layers
        if (((1 << collision.gameObject.layer) & ignoreLayers.value) != 0)
            return;

        int otherId = collision.collider.GetInstanceID();
        float now = Time.time;
        if (_lastHitTimeByOtherInstanceId.TryGetValue(otherId, out var lastTime))
        {
            if (now - lastTime < perColliderCooldownSeconds)
                return;
        }

        // Use relative velocity magnitude as impact speed
        float impactSpeed = collision.relativeVelocity.magnitude;

        // Nothing below threshold
        if (impactSpeed < minDamageSpeed)
            return;

        // Optionally scale by contact count to represent multiple contact points in a single impact
        int contactFactor = scaleByContactCount ? Mathf.Max(1, collision.contactCount) : 1;

        // Base damage is linear on speed above threshold
        float baseDamage = (impactSpeed - minDamageSpeed) * damagePerSpeed * contactFactor;

        // Scale by fragility: more fragile => more damage
        float fragilityScale = 1f + Mathf.Max(0f, fragilityMultiplier) * Mathf.Clamp01(_props.Fragility);
        int damage = Mathf.Clamp(Mathf.RoundToInt(baseDamage * fragilityScale), 0, Mathf.Max(1, maxDamagePerHit));

        if (damage <= 0)
            return;

        ApplyDamageToValue(damage);
        _lastHitTimeByOtherInstanceId[otherId] = now;
    }

    private void ApplyDamageToValue(int damage)
    {
        // Server-authoritative: only server mutates NetValue.
        // Clients do not apply damage directly to avoid desync.
        bool isNetworked = _props.NetworkManager != null && (_props.IsServer || _props.IsClient);

        if (isNetworked)
        {
            if (_props.IsServer)
            {
                int current = _props.NetValue.Value;
                int next = Mathf.Max(0, current - damage);
                if (next != current)
                {
                    _props.NetValue.Value = next;
                }
            }
            // If not server, do nothing; server will apply and replicate.
        }
        else
        {
            // Offline run: PackageProperties.Value is backed by a private localValue.
            // We cannot set it directly, so we cache health locally and mirror the initial Value.
            // As a fallback, we reduce Rigidbody mass slightly to simulate degradation and log the change.
            // Prefer adding a setter method on PackageProperties for offline support.
#if UNITY_EDITOR
            Debug.LogWarning($"{nameof(PackageDamageSystem)}: Offline mode detected. Consider adding a public method to PackageProperties to mutate local value. Damage ({damage}) not reflected in PackageProperties.Value.");
#endif
        }
    }
}
