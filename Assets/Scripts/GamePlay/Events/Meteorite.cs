using System.Collections;
using UnityEngine;

/// <summary>
/// Simple falling meteorite that damages players on impact radius and then destroys itself.
/// </summary>
[RequireComponent(typeof(Collider))]
public class Meteorite : MonoBehaviour
{
    [System.Serializable]
    public struct FallParams
    {
        public float gravity;
        public float terminalVelocity;
        public float landDestroyDelay;
        public float impactRadius;
        public int impactDamage;
        public float knockbackForce;
    }

    [Header("Visuals")]
    [SerializeField] private ParticleSystem trailVFX;
    [SerializeField] private ParticleSystem impactVFX;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip impactSFX;

    [Header("Collision")]
    [SerializeField] private LayerMask groundMask = ~0; // default: collide with everything
    [SerializeField] private LayerMask playerMask;

    [Header("Explosion on Land")]
    [Tooltip("If true, applies an explosion force to nearby packages when landing.")]
    [SerializeField] private bool explodeOnLand = true;
    [Tooltip("Radius used to push packages away.")]
    [SerializeField] private float packageExplosionRadius = 4f;
    [Tooltip("Impulse force applied to packages within radius.")]
    [SerializeField] private float packageExplosionForce = 15f;
    [Tooltip("Upwards modifier passed to AddExplosionForce.")]
    [SerializeField] private float packageUpwardsModifier = 0.25f;
    [Tooltip("Optional layer mask to filter which objects count as packages. Leave 0 to auto-detect via components.")]
    [SerializeField] private LayerMask packageMask;

    [SerializeField] private FallParams p;
    private float verticalSpeed;
    private bool landed;

    // Optional for pooling
    public void SetFallParams(FallParams fp)
    {
        p = fp;
    }

    private void Awake()
    {
        // Reasonable defaults if not set via SetFallParams
        if (p.gravity <= 0f) p.gravity = 30f;
        if (p.terminalVelocity <= 0f) p.terminalVelocity = 60f;
        if (p.landDestroyDelay <= 0f) p.landDestroyDelay = 0.5f;
        if (p.impactRadius <= 0f) p.impactRadius = 2.5f;
        if (p.knockbackForce <= 0f) p.knockbackForce = 10f;

        // If playerMask is not set in inspector, try a reasonable default
        if (playerMask == 0)
        {
            // Assume "Player" layer index 8 if exists; otherwise everything
            int playerLayer = LayerMask.NameToLayer("Player");
            playerMask = playerLayer >= 0 ? (1 << playerLayer) : ~0;
        }
    }

    private void Update()
    {
        if (landed) return;
        // Accelerate downward (transform-driven; collider can be trigger)
        verticalSpeed = Mathf.Max(-p.terminalVelocity, verticalSpeed - p.gravity * Time.deltaTime);
        transform.position += Vector3.up * verticalSpeed * Time.deltaTime;

        // Raycast to detect ground contact for precise landing
        if (Physics.Raycast(transform.position, Vector3.down, out var hit, 1.5f, groundMask, QueryTriggerInteraction.Ignore))
        {
            // Consider landed when moving downward and close to ground
            if (verticalSpeed < -0.1f && hit.distance < 0.75f)
            {
                OnLanded(hit.point, hit.normal);
            }
        }
    }

    private void OnLanded(Vector3 point, Vector3 normal)
    {
        if (landed) return;
        landed = true;

        // Snap to ground
        transform.position = point;

        // Play impact VFX/SFX
        if (impactVFX != null) { impactVFX.transform.position = point; impactVFX.Play(); }
        if (audioSource != null && impactSFX != null) audioSource.PlayOneShot(impactSFX);

        // Damage/knockback players in radius
        DamagePlayersInRadius(point, p.impactRadius, p.impactDamage, p.knockbackForce);

        // Push packages away (explosion)
        if (explodeOnLand)
        {
            PushPackagesInRadius(point, packageExplosionRadius, packageExplosionForce, packageUpwardsModifier);
        }

        // Stop trail
        if (trailVFX != null) trailVFX.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        // Delay, then destroy
        StartCoroutine(DestroyAfterDelay(p.landDestroyDelay));
    }

    private void DamagePlayersInRadius(Vector3 center, float radius, int damage, float knockbackForce)
    {
        var hits = Physics.OverlapSphere(center, radius, playerMask, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            // If a health interface exists, call it. Otherwise, no-op
            var damageable = h.GetComponentInParent<IPlayerDamageable>();
            if (damageable != null)
            {
                damageable.ApplyDamage(damage, center);
            }
        }
    }

    private void PushPackagesInRadius(Vector3 center, float radius, float force, float upwardsModifier)
    {
        // If a mask is provided, use it; otherwise, scan all and filter by package components.
        Collider[] hits = packageMask != 0
            ? Physics.OverlapSphere(center, radius, packageMask, QueryTriggerInteraction.Ignore)
            : Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Ignore);

        foreach (var h in hits)
        {
            // Filter to package-like objects (by component)
            var pkg = h.GetComponentInParent<PackageDamageSystem>();
            if (pkg == null)
            {
                var props = h.GetComponentInParent<DeliverHere.Items.PackageProperties>();
                if (props == null) continue;
            }

            var rb = h.attachedRigidbody ?? h.GetComponentInParent<Rigidbody>();
            if (rb == null || rb.isKinematic) continue;

            rb.AddExplosionForce(force, center, radius, upwardsModifier, ForceMode.Impulse);
        }
    }

    private IEnumerator DestroyAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        Destroy(gameObject);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, p.impactRadius > 0f ? p.impactRadius : 2.5f);

        Gizmos.color = new Color(1f, 0.5f, 0f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, packageExplosionRadius > 0f ? packageExplosionRadius : 4f);
    }
#endif
}
