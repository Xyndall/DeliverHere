using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class PlayerUpgradableStats : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float baseWalkSpeed = 4.5f;
    [SerializeField] private float baseRunSpeed = 7.5f;
    [SerializeField] private float baseJumpHeight = 1.4f;

    [Header("Health")]
    [SerializeField] private int baseMaxHealth = 100;

    [Header("Strength")]
    [Tooltip("How much held mass (kg) the player can handle at near-full effectiveness.")]
    [SerializeField] private float baseArmStrengthKg = 15f;

    [Header("Initial Upgrade Multipliers (used as defaults)")]
    [SerializeField] private float initialMoveSpeedMultiplier = 1f;
    [SerializeField] private float initialJumpHeightMultiplier = 1f;
    [SerializeField] private float initialMaxHealthMultiplier = 1f;
    [SerializeField] private float initialArmStrengthMultiplier = 1f;

    [Header("Initial Stamina Multipliers (simplified)")]
    [SerializeField] private float initialStaminaMaxMultiplier = 1f;
    [Tooltip(">1 = sprint drains slower AND stamina regenerates faster.")]
    [SerializeField] private float initialStaminaEfficiencyMultiplier = 1f;

    [Header("Initial Combined Multipliers")]
    [Tooltip(">1 means stronger carry capacity + higher weight limits.")]
    [SerializeField] private float initialCarryCapacityMultiplier = 1f;
    [Tooltip(">1 means better holding and throwing.")]
    [SerializeField] private float initialHoldAndThrowMultiplier = 1f;

    [Header("Upgrade Bounds")]
    [SerializeField] private float minMultiplier = 0.25f;
    [SerializeField] private float maxMultiplier = 5.0f;

    private readonly NetworkVariable<float> nvMoveSpeedMultiplier =
        new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> nvJumpHeightMultiplier =
        new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> nvMaxHealthMultiplier =
        new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> nvArmStrengthMultiplier =
        new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> nvStaminaMaxMultiplier =
        new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> nvStaminaEfficiencyMultiplier =
        new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> nvCarryCapacityMultiplier =
        new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> nvHoldAndThrowMultiplier =
        new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public event Action<PlayerUpgradableStats> OnStatsChanged;

    public float MoveSpeedMultiplier => nvMoveSpeedMultiplier.Value;
    public float JumpHeightMultiplier => nvJumpHeightMultiplier.Value;
    public float MaxHealthMultiplier => nvMaxHealthMultiplier.Value;
    public float ArmStrengthMultiplier => nvArmStrengthMultiplier.Value;

    public float StaminaMaxMultiplier => nvStaminaMaxMultiplier.Value;
    public float StaminaEfficiencyMultiplier => nvStaminaEfficiencyMultiplier.Value;

    public float CarryCapacityMultiplier => nvCarryCapacityMultiplier.Value;
    public float HoldAndThrowMultiplier => nvHoldAndThrowMultiplier.Value;

    public float WalkSpeed => Mathf.Max(0f, baseWalkSpeed * MoveSpeedMultiplier);
    public float RunSpeed => Mathf.Max(0f, baseRunSpeed * MoveSpeedMultiplier);
    public float JumpHeight => Mathf.Max(0.1f, baseJumpHeight * JumpHeightMultiplier);
    public int MaxHealth => Mathf.Max(1, Mathf.RoundToInt(baseMaxHealth * MaxHealthMultiplier));

    public float ArmStrengthKg =>
        Mathf.Max(0.01f, baseArmStrengthKg * ArmStrengthMultiplier * CarryCapacityMultiplier);

    public enum UpgradeType : byte
    {
        MoveSpeed = 0,
        JumpHeight = 1,
        MaxHealth = 2,
        ArmStrength = 3,

        // Simplified stamina
        StaminaMax = 10,
        StaminaEfficiency = 11,

        // Combined
        CarryCapacity = 20,
        HoldAndThrow = 21
    }

    private static readonly Dictionary<ulong, StatsSnapshot> ServerSnapshotsByClientId = new();

    public struct StatsSnapshot : INetworkSerializable
    {
        public float MoveSpeedMultiplier;
        public float JumpHeightMultiplier;
        public float MaxHealthMultiplier;
        public float ArmStrengthMultiplier;

        public float StaminaMaxMultiplier;
        public float StaminaEfficiencyMultiplier;

        public float CarryCapacityMultiplier;
        public float HoldAndThrowMultiplier;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref MoveSpeedMultiplier);
            serializer.SerializeValue(ref JumpHeightMultiplier);
            serializer.SerializeValue(ref MaxHealthMultiplier);
            serializer.SerializeValue(ref ArmStrengthMultiplier);

            serializer.SerializeValue(ref StaminaMaxMultiplier);
            serializer.SerializeValue(ref StaminaEfficiencyMultiplier);

            serializer.SerializeValue(ref CarryCapacityMultiplier);
            serializer.SerializeValue(ref HoldAndThrowMultiplier);
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        nvMoveSpeedMultiplier.OnValueChanged += OnAnyMultiplierChanged;
        nvJumpHeightMultiplier.OnValueChanged += OnAnyMultiplierChanged;
        nvMaxHealthMultiplier.OnValueChanged += OnAnyMultiplierChanged;
        nvArmStrengthMultiplier.OnValueChanged += OnAnyMultiplierChanged;

        nvStaminaMaxMultiplier.OnValueChanged += OnAnyMultiplierChanged;
        nvStaminaEfficiencyMultiplier.OnValueChanged += OnAnyMultiplierChanged;

        nvCarryCapacityMultiplier.OnValueChanged += OnAnyMultiplierChanged;
        nvHoldAndThrowMultiplier.OnValueChanged += OnAnyMultiplierChanged;

        if (IsServer)
        {
            if (!ServerTryRestoreFor(OwnerClientId))
                ServerResetToDefaults();
        }

        OnStatsChanged?.Invoke(this);
    }

    public override void OnNetworkDespawn()
    {
        try
        {
            if (IsServer)
                ServerSaveFor(OwnerClientId);
        }
        finally
        {
            base.OnNetworkDespawn();
        }
    }

    public override void OnDestroy()
    {
        try
        {
            nvMoveSpeedMultiplier.OnValueChanged -= OnAnyMultiplierChanged;
            nvJumpHeightMultiplier.OnValueChanged -= OnAnyMultiplierChanged;
            nvMaxHealthMultiplier.OnValueChanged -= OnAnyMultiplierChanged;
            nvArmStrengthMultiplier.OnValueChanged -= OnAnyMultiplierChanged;

            nvStaminaMaxMultiplier.OnValueChanged -= OnAnyMultiplierChanged;
            nvStaminaEfficiencyMultiplier.OnValueChanged -= OnAnyMultiplierChanged;

            nvCarryCapacityMultiplier.OnValueChanged -= OnAnyMultiplierChanged;
            nvHoldAndThrowMultiplier.OnValueChanged -= OnAnyMultiplierChanged;
        }
        finally
        {
            base.OnDestroy();
        }
    }

    private void OnAnyMultiplierChanged(float previous, float current) => OnStatsChanged?.Invoke(this);

    private float ClampMultiplier(float v) => Mathf.Clamp(v, minMultiplier, maxMultiplier);

    public void RequestApplyUpgrade(UpgradeType type, float addDeltaMultiplier)
    {
        if (IsServer)
        {
            ServerApplyUpgradeInternal(type, addDeltaMultiplier);
            return;
        }

        if (!IsOwner) return;

        ApplyUpgradeServerRpc(type, addDeltaMultiplier);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void ApplyUpgradeServerRpc(UpgradeType type, float addDeltaMultiplier)
    {
        ServerApplyUpgradeInternal(type, addDeltaMultiplier);
    }

    private void ServerApplyUpgradeInternal(UpgradeType type, float addDeltaMultiplier)
    {
        if (!IsServer) return;

        float delta = Mathf.Clamp(addDeltaMultiplier, -2f, 2f);

        switch (type)
        {
            case UpgradeType.MoveSpeed:
                nvMoveSpeedMultiplier.Value = ClampMultiplier(nvMoveSpeedMultiplier.Value + delta);
                break;
            case UpgradeType.JumpHeight:
                nvJumpHeightMultiplier.Value = ClampMultiplier(nvJumpHeightMultiplier.Value + delta);
                break;
            case UpgradeType.MaxHealth:
                nvMaxHealthMultiplier.Value = ClampMultiplier(nvMaxHealthMultiplier.Value + delta);
                break;
            case UpgradeType.ArmStrength:
                nvArmStrengthMultiplier.Value = ClampMultiplier(nvArmStrengthMultiplier.Value + delta);
                break;

            case UpgradeType.StaminaMax:
                nvStaminaMaxMultiplier.Value = ClampMultiplier(nvStaminaMaxMultiplier.Value + delta);
                break;
            case UpgradeType.StaminaEfficiency:
                nvStaminaEfficiencyMultiplier.Value = ClampMultiplier(nvStaminaEfficiencyMultiplier.Value + delta);
                break;

            case UpgradeType.CarryCapacity:
                nvCarryCapacityMultiplier.Value = ClampMultiplier(nvCarryCapacityMultiplier.Value + delta);
                break;

            case UpgradeType.HoldAndThrow:
                nvHoldAndThrowMultiplier.Value = ClampMultiplier(nvHoldAndThrowMultiplier.Value + delta);
                break;
        }

        ServerSaveFor(OwnerClientId);
    }

    public StatsSnapshot GetSnapshot()
    {
        return new StatsSnapshot
        {
            MoveSpeedMultiplier = MoveSpeedMultiplier,
            JumpHeightMultiplier = JumpHeightMultiplier,
            MaxHealthMultiplier = MaxHealthMultiplier,
            ArmStrengthMultiplier = ArmStrengthMultiplier,

            StaminaMaxMultiplier = StaminaMaxMultiplier,
            StaminaEfficiencyMultiplier = StaminaEfficiencyMultiplier,

            CarryCapacityMultiplier = CarryCapacityMultiplier,
            HoldAndThrowMultiplier = HoldAndThrowMultiplier
        };
    }

    public void ServerApplySnapshot(StatsSnapshot snapshot)
    {
        if (!IsServer) return;

        nvMoveSpeedMultiplier.Value = ClampMultiplier(snapshot.MoveSpeedMultiplier);
        nvJumpHeightMultiplier.Value = ClampMultiplier(snapshot.JumpHeightMultiplier);
        nvMaxHealthMultiplier.Value = ClampMultiplier(snapshot.MaxHealthMultiplier);
        nvArmStrengthMultiplier.Value = ClampMultiplier(snapshot.ArmStrengthMultiplier);

        nvStaminaMaxMultiplier.Value = ClampMultiplier(snapshot.StaminaMaxMultiplier);
        nvStaminaEfficiencyMultiplier.Value = ClampMultiplier(snapshot.StaminaEfficiencyMultiplier);

        nvCarryCapacityMultiplier.Value = ClampMultiplier(snapshot.CarryCapacityMultiplier);
        nvHoldAndThrowMultiplier.Value = ClampMultiplier(snapshot.HoldAndThrowMultiplier);

        ServerSaveFor(OwnerClientId);
    }

    public void ServerResetToDefaults()
    {
        if (!IsServer) return;

        nvMoveSpeedMultiplier.Value = ClampMultiplier(initialMoveSpeedMultiplier);
        nvJumpHeightMultiplier.Value = ClampMultiplier(initialJumpHeightMultiplier);
        nvMaxHealthMultiplier.Value = ClampMultiplier(initialMaxHealthMultiplier);
        nvArmStrengthMultiplier.Value = ClampMultiplier(initialArmStrengthMultiplier);

        nvStaminaMaxMultiplier.Value = ClampMultiplier(initialStaminaMaxMultiplier);
        nvStaminaEfficiencyMultiplier.Value = ClampMultiplier(initialStaminaEfficiencyMultiplier);

        nvCarryCapacityMultiplier.Value = ClampMultiplier(initialCarryCapacityMultiplier);
        nvHoldAndThrowMultiplier.Value = ClampMultiplier(initialHoldAndThrowMultiplier);

        ServerSaveFor(OwnerClientId);
    }

    private void ServerSaveFor(ulong clientId)
    {
        if (!IsServer) return;
        ServerSnapshotsByClientId[clientId] = GetSnapshot();
    }

    private bool ServerTryRestoreFor(ulong clientId)
    {
        if (!IsServer) return false;

        if (!ServerSnapshotsByClientId.TryGetValue(clientId, out var snapshot))
            return false;

        ServerApplySnapshot(snapshot);
        return true;
    }

    public static void ServerClearAllSnapshots() => ServerSnapshotsByClientId.Clear();
    public static void ServerClearSnapshotForClient(ulong clientId) => ServerSnapshotsByClientId.Remove(clientId);

#if UNITY_EDITOR
    private void OnValidate()
    {
        baseWalkSpeed = Mathf.Max(0f, baseWalkSpeed);
        baseRunSpeed = Mathf.Max(baseWalkSpeed, baseRunSpeed);
        baseJumpHeight = Mathf.Max(0.1f, baseJumpHeight);
        baseMaxHealth = Mathf.Max(1, baseMaxHealth);
        baseArmStrengthKg = Mathf.Max(0.01f, baseArmStrengthKg);

        minMultiplier = Mathf.Max(0f, minMultiplier);
        maxMultiplier = Mathf.Max(minMultiplier, maxMultiplier);

        initialMoveSpeedMultiplier = Mathf.Clamp(initialMoveSpeedMultiplier, minMultiplier, maxMultiplier);
        initialJumpHeightMultiplier = Mathf.Clamp(initialJumpHeightMultiplier, minMultiplier, maxMultiplier);
        initialMaxHealthMultiplier = Mathf.Clamp(initialMaxHealthMultiplier, minMultiplier, maxMultiplier);
        initialArmStrengthMultiplier = Mathf.Clamp(initialArmStrengthMultiplier, minMultiplier, maxMultiplier);

        initialStaminaMaxMultiplier = Mathf.Clamp(initialStaminaMaxMultiplier, minMultiplier, maxMultiplier);
        initialStaminaEfficiencyMultiplier = Mathf.Clamp(initialStaminaEfficiencyMultiplier, minMultiplier, maxMultiplier);

        initialCarryCapacityMultiplier = Mathf.Clamp(initialCarryCapacityMultiplier, minMultiplier, maxMultiplier);
        initialHoldAndThrowMultiplier = Mathf.Clamp(initialHoldAndThrowMultiplier, minMultiplier, maxMultiplier);
    }
#endif
}
