using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Centralized weight logic for players. Tracks body weight and held package mass,
/// and provides movement constraints/multipliers based on total weight.
/// Other systems (movement, stamina, etc.) should query this manager.
/// </summary>
[DisallowMultipleComponent]
public class PlayerWeightManager : NetworkBehaviour
{
    [Header("Player Weight")]
    [Tooltip("Approximate base mass of the player character (kg).")]
    [SerializeField] private float baseBodyMassKg = 75f;
    [Tooltip("Equipment/clothing carried by default (kg).")]
    [SerializeField] private float baseEquipmentMassKg = 5f;

    [Header("Held Mass")]
    [Tooltip("Smoothing for changes in held mass to avoid sudden spikes.")]
    [SerializeField] private float heldMassSmoothTime = 0.12f;

    [Header("Movement Scaling")]
    [Tooltip("Speed multiplier at 0 extra weight (should be 1).")]
    [SerializeField] private float speedMultiplierAtMin = 1f;
    [Tooltip("Speed multiplier at or above maxSlowdownMassKg extra weight.")]
    [Range(0.2f, 1f)] [SerializeField] private float speedMultiplierAtMax = 0.55f;
    [Tooltip("Extra mass (kg) needed to reach max slowdown from min. E.g., 10kg -> max slowdown at +10kg.")]
    [SerializeField] private float maxSlowdownMassKg = 10f;

    [Header("Turn Scaling")]
    [Tooltip("Turn responsiveness multiplier at or above maxSlowdownMassKg.")]
    [Range(0.1f, 1f)] [SerializeField] private float rotateLerpMultiplierAtMax = 0.4f;

    [Header("Movement Limits")]
    [Tooltip("Total mass (player + equipment + held) above which jumping is disabled.")]
    [SerializeField] private float maxMassForJumpKg = 95f;
    [Tooltip("Total mass above which sprinting is disabled.")]
    [SerializeField] private float maxMassForSprintKg = 110f;

    // Networked exposure if needed by UI/remote effects
    public NetworkVariable<float> TotalMassKg { get; private set; }
        = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public float BaseMassKg => Mathf.Max(0f, baseBodyMassKg + baseEquipmentMassKg);

    // Held mass tracked/smoothed locally by the owning client
    private float _heldMassTargetKg;
    private float _heldMassSmoothedKg;
    private float _heldMassVel;

    private void Awake()
    {
        // Initialize total mass to base
        TotalMassKg.Value = BaseMassKg;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // Owner controls their own weight; others just read the network variable
        enabled = IsOwner;
        if (enabled)
        {
            // Ensure sane initial value
            TotalMassKg.Value = BaseMassKg;
        }
    }

    private void Update()
    {
        if (!IsOwner) return;

        // Smooth held mass for nicer feel
        _heldMassSmoothedKg = Mathf.SmoothDamp(_heldMassSmoothedKg, _heldMassTargetKg, ref _heldMassVel, heldMassSmoothTime);

        // Update TotalMass
        float total = BaseMassKg + Mathf.Max(0f, _heldMassSmoothedKg);
        TotalMassKg.Value = total;
    }

    /// <summary>
    /// Provide the current held mass (kg). Pass null or negative to indicate no held mass.
    /// </summary>
    public void SetHeldMass(float? heldMassKg)
    {
        _heldMassTargetKg = Mathf.Max(0f, heldMassKg ?? 0f);
    }

    /// <summary>
    /// 0..1 weight fraction relative to slowdown range. 0 at 0 extra mass, 1 at or above maxSlowdownMassKg extra mass.
    /// </summary>
    private float GetPenalty01()
    {
        float extraMass = Mathf.Max(0f, _heldMassSmoothedKg);
        if (maxSlowdownMassKg <= 0.0001f) return 1f;
        return Mathf.Clamp01(extraMass / maxSlowdownMassKg);
    }

    /// <summary>
    /// Speed multiplier based on current held mass only (does not alter base speeds directly).
    /// </summary>
    public float GetSpeedMultiplier()
    {
        float t = GetPenalty01();
        return Mathf.Lerp(speedMultiplierAtMin, speedMultiplierAtMax, t);
    }

    /// <summary>
    /// Rotate responsiveness multiplier based on held mass only.
    /// </summary>
    public float GetRotateLerpMultiplier()
    {
        float t = GetPenalty01();
        return Mathf.Lerp(1f, rotateLerpMultiplierAtMax, t);
    }

    /// <summary>
    /// Whether the player can jump at current total mass.
    /// </summary>
    public bool CanJump()
    {
        return TotalMassKg.Value <= maxMassForJumpKg + 0.0001f;
    }

    /// <summary>
    /// Whether the player can sprint at current total mass.
    /// </summary>
    public bool CanSprint()
    {
        return TotalMassKg.Value <= maxMassForSprintKg + 0.0001f;
    }
}