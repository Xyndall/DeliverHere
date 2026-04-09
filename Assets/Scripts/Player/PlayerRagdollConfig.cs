using UnityEngine;

namespace DeliverHere.Player
{
    /// <summary>
    /// Scriptable Object configuration for player ragdoll stun settings.
    /// Allows easy tweaking and sharing of settings across multiple player prefabs.
    /// </summary>
    [CreateAssetMenu(fileName = "PlayerRagdollConfig", menuName = "DeliverHere/Player/Ragdoll Stun Config")]
    public class PlayerRagdollConfig : ScriptableObject
    {
        [Header("Stun Thresholds")]
        [Tooltip("Minimum impact speed (m/s) required to trigger ragdoll stun.")]
        [SerializeField] private float minImpactSpeed = 5f;
        
        [Tooltip("Ignore impacts from objects below this mass (kg).")]
        [SerializeField] private float minImpactMass = 1f;

        [Header("Stun Duration")]
        [Tooltip("Duration of ragdoll stun in seconds.")]
        [SerializeField] private float stunDuration = 2f;
        
        [Tooltip("Minimum time between stuns (cooldown in seconds).")]
        [SerializeField] private float stunCooldown = 1f;

        [Header("Force Application")]
        [Tooltip("Multiplier for the impact force applied to the ragdoll.")]
        [SerializeField] private float impactForceMultiplier = 1.5f;
        
        [Tooltip("Maximum force that can be applied to the ragdoll.")]
        [SerializeField] private float maxImpactForce = 50f;
        
        [Tooltip("Upward force component added to the impact (helps with believable tumbling).")]
        [SerializeField] private float upwardForceBoost = 2f;

        [Header("Recovery")]
        [Tooltip("Time to blend from ragdoll back to animated state (seconds).")]
        [SerializeField] private float recoveryBlendTime = 0.3f;

        // Public properties
        public float MinImpactSpeed => minImpactSpeed;
        public float MinImpactMass => minImpactMass;
        public float StunDuration => stunDuration;
        public float StunCooldown => stunCooldown;
        public float ImpactForceMultiplier => impactForceMultiplier;
        public float MaxImpactForce => maxImpactForce;
        public float UpwardForceBoost => upwardForceBoost;
        public float RecoveryBlendTime => recoveryBlendTime;

#if UNITY_EDITOR
        private void OnValidate()
        {
            minImpactSpeed = Mathf.Max(0.1f, minImpactSpeed);
            minImpactMass = Mathf.Max(0f, minImpactMass);
            stunDuration = Mathf.Max(0.1f, stunDuration);
            stunCooldown = Mathf.Max(0f, stunCooldown);
            impactForceMultiplier = Mathf.Max(0.1f, impactForceMultiplier);
            maxImpactForce = Mathf.Max(1f, maxImpactForce);
            upwardForceBoost = Mathf.Max(0f, upwardForceBoost);
            recoveryBlendTime = Mathf.Max(0.1f, recoveryBlendTime);
        }
#endif
    }
}