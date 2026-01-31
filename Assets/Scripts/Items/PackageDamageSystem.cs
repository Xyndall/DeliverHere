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

    [Tooltip("Minimum applied damage required to count as damage (e.g., 5). Below this, damage is ignored.")]
    [SerializeField] private int minDamageAmount = 5;

    [Header("Collision Filtering")]
    [Tooltip("Ignore collisions with layers in this mask (e.g., player hands).")]
    [SerializeField] private LayerMask ignoreLayers;

    [Tooltip("Minimum time between any damage applications to this package (global cooldown, independent of collider).")]
    [SerializeField] private float packageCooldownSeconds = 0.25f;

    [Tooltip("Scale damage by contact count within a collision. If false, only the strongest contact is used.")]
    [SerializeField] private bool scaleByContactCount = true;

    [Header("Feedback")]
    [Tooltip("Optional broadcaster used to display floating damage text.")]
    [SerializeField] private DamageFeedbackBroadcaster feedbackBroadcaster;

    [Tooltip("Local offset for spawned text relative to the package origin.")]
    [SerializeField] private Vector3 textLocalOffset = new Vector3(0f, 0.25f, 0f);

    private Rigidbody _rb;
    private PackageProperties _props;

    // Global cooldown: last time the package took damage (any collider)
    private float _lastPackageDamageTime = -999f;

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
        if (feedbackBroadcaster == null)
        {
            feedbackBroadcaster = GetComponent<DamageFeedbackBroadcaster>() ?? GetComponentInParent<DamageFeedbackBroadcaster>();
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

        float now = Time.time;

        // Global package cooldown first (independent of which collider hit us)
        if (now - _lastPackageDamageTime < packageCooldownSeconds)
            return;

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

        // Enforce minimum applied damage amount
        if (damage < minDamageAmount)
            return;

        // Prefer package origin with a small upward local offset so it’s visible above geometry
        Vector3 localOffset = textLocalOffset;

        ApplyDamageToValue(damage, localOffset);

        // Record global cooldown
        _lastPackageDamageTime = now;
    }

    private void ApplyDamageToValue(int damage, Vector3 localOffset)
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

                    // Broadcast to all clients to show floating damage (spawned as child of the broadcaster/package)
                    if (feedbackBroadcaster != null && feedbackBroadcaster.IsServer)
                    {
                        feedbackBroadcaster.ShowDamageClientRpc(damage, localOffset);
                    }
                }
            }
            // If not server, do nothing; server will apply and replicate and clients get the RPC.
        }
        else
        {
            // Offline run: show local feedback even though value does not change
#if UNITY_EDITOR
            Debug.LogWarning($"{nameof(PackageDamageSystem)}: Offline mode detected. Consider adding a public method to PackageProperties to mutate local value. Damage ({damage}) not reflected in PackageProperties.Value.");
#endif
            if (feedbackBroadcaster != null)
            {
                feedbackBroadcaster.ShowLocal(damage, localOffset);
            }
        }
    }
}
