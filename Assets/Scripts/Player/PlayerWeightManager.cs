using UnityEngine;
using Unity.Netcode;

[DisallowMultipleComponent]
public class PlayerWeightManager : NetworkBehaviour
{
    [Header("Player Weight")]
    [Tooltip("Approximate base mass of the player character (kg).")]
    [SerializeField] private float baseBodyMassKg = 75f;
    [Tooltip("Equipment/clothing carried by default (kg).")]
    [SerializeField] private float baseEquipmentMassKg = 5f;

    [Header("Arm Strength")]
    [Tooltip("How much held mass (kg) the player can handle at near-full effectiveness.")]
    [SerializeField] private float armStrengthKg = 15f;

    [Header("Held Mass")]
    [Tooltip("Smoothing for changes in held mass to avoid sudden spikes.")]
    [SerializeField] private float heldMassSmoothTime = 0.12f;

    [Header("Movement Scaling")]
    [Tooltip("Speed multiplier at 0 extra weight (should be 1).")]
    [SerializeField] private float speedMultiplierAtMin = 1f;

    [Tooltip("Minimum speed multiplier when extremely overloaded (0..1).")]
    [Range(0.0f, 1f)]
    [SerializeField] private float speedMultiplierWhenOverloaded = 0.05f;

    [Header("Turn Scaling")]
    [Tooltip("Turn responsiveness multiplier when extremely overloaded (0..1).")]
    [Range(0.1f, 1f)]
    [SerializeField] private float rotateLerpMultiplierWhenOverloaded = 0.25f;

    [Header("Movement Limits")]
    [Tooltip("Total mass (player + equipment + held) above which jumping is disabled.")]
    [SerializeField] private float maxMassForJumpKg = 95f;
    [Tooltip("Total mass above which sprinting is disabled.")]
    [SerializeField] private float maxMassForSprintKg = 110f;

    [Header("Lift Tuning")]
    [Tooltip("Load ratio where lifting begins to noticeably fail. 1 = held mass equals arm strength.")]
    [SerializeField] private float liftStartLoadRatio = 0.85f;

    [Tooltip("Load ratio where lifting is near impossible.")]
    [SerializeField] private float liftEndLoadRatio = 1.50f;

    [Tooltip("Smoothing time (seconds) for lift effect, prevents sudden changes.")]
    [SerializeField] private float liftEffectSmoothTime = 0.10f;

    public NetworkVariable<float> TotalMassKg { get; private set; }
        = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public float BaseMassKg => Mathf.Max(0f, baseBodyMassKg + baseEquipmentMassKg);

    private float _heldMassTargetKg;
    private float _heldMassSmoothedKg;
    private float _heldMassVel;

    private float _liftEffectSmoothed01 = 1f;
    private float _liftEffectVel;

    private void Awake()
    {
        _heldMassTargetKg = 0f;
        _heldMassSmoothedKg = 0f;
        _heldMassVel = 0f;
        _liftEffectSmoothed01 = 1f;
        _liftEffectVel = 0f;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        enabled = IsOwner;

        if (IsOwner)
        {
            TotalMassKg.Value = BaseMassKg;
        }
    }

    private void Update()
    {
        if (!IsOwner) return;

        // Smooth held mass first
        _heldMassSmoothedKg = Mathf.SmoothDamp(_heldMassSmoothedKg, _heldMassTargetKg, ref _heldMassVel, heldMassSmoothTime);

        // Now compute/smooth lift effect using the updated held mass
        float targetLift = ComputeLiftEffect01Raw();
        _liftEffectSmoothed01 = Mathf.SmoothDamp(_liftEffectSmoothed01, targetLift, ref _liftEffectVel, liftEffectSmoothTime);

        float total = BaseMassKg + Mathf.Max(0f, _heldMassSmoothedKg);
        TotalMassKg.Value = total;
    }

    public void SetHeldMass(float? heldMassKg)
    {
        _heldMassTargetKg = Mathf.Max(0f, heldMassKg ?? 0f);
    }

    /// <summary>
    /// 0..1 based on how overloaded the player is relative to armStrengthKg.
    /// 1 = easy/light, ~0 = extremely heavy.
    /// </summary>
    public float GetArmEffectiveness01()
    {
        float strength = Mathf.Max(0.01f, armStrengthKg);
        float held = Mathf.Max(0f, _heldMassSmoothedKg);

        float load = held / strength; // 1 == "at strength"
        // Smooth curve: 1 at load=0, 0.5 at load=1, ~0.2 at load=2, ~0.06 at load=4
        float effectiveness = 1f / (1f + load * load);
        return Mathf.Clamp01(effectiveness);
    }

    public float GetSpeedMultiplier() => 1f;
    public float GetRotateLerpMultiplier() => 1f;

    public bool CanJump()
    {
        return TotalMassKg.Value <= maxMassForJumpKg + 0.0001f;
    }

    public bool CanSprint()
    {
        return TotalMassKg.Value <= maxMassForSprintKg + 0.0001f;
    }

    public float ArmStrengthKg => Mathf.Max(0.01f, armStrengthKg);

    public float GetLoadRatio()
    {
        float strength = ArmStrengthKg;
        // IMPORTANT: use the *smoothed* held mass so it doesn't flicker between modes.
        float held = Mathf.Max(0f, _heldMassSmoothedKg);
        return held / strength;
    }
    public float GetLiftEffect01()
    {
        return Mathf.Clamp01(_liftEffectSmoothed01);
    }

    private float ComputeLiftEffect01Raw()
    {
        float load = GetLoadRatio();

        // If liftStart==liftEnd it becomes a step; prevent that.
        float a = Mathf.Min(liftStartLoadRatio, liftEndLoadRatio - 0.0001f);
        float b = Mathf.Max(liftEndLoadRatio, a + 0.0001f);

        // t=0 when load<=a (easy lift), t=1 when load>=b (no lift)
        float t = Mathf.InverseLerp(a, b, load);

        // Smooth falloff instead of sudden cliff
        float smooth = t * t * (3f - 2f * t); // SmoothStep

        // effect=1 at easy, 0 at impossible
        return 1f - smooth;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        baseBodyMassKg = Mathf.Max(0f, baseBodyMassKg);
        baseEquipmentMassKg = Mathf.Max(0f, baseEquipmentMassKg);
        armStrengthKg = Mathf.Max(0.01f, armStrengthKg);

        heldMassSmoothTime = Mathf.Max(0f, heldMassSmoothTime);

        speedMultiplierAtMin = Mathf.Clamp(speedMultiplierAtMin, 0f, 2f);
        speedMultiplierWhenOverloaded = Mathf.Clamp01(speedMultiplierWhenOverloaded);
        rotateLerpMultiplierWhenOverloaded = Mathf.Clamp(rotateLerpMultiplierWhenOverloaded, 0.1f, 1f);

        maxMassForJumpKg = Mathf.Max(0f, maxMassForJumpKg);
        maxMassForSprintKg = Mathf.Max(maxMassForJumpKg, maxMassForSprintKg);
    }
#endif
}