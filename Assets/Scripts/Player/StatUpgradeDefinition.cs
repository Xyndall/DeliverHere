using UnityEngine;

[CreateAssetMenu(menuName = "DeliverHere/Upgrades/Stat Upgrade Definition", fileName = "StatUpgradeDefinition")]
public sealed class StatUpgradeDefinition : ScriptableObject
{
    [Header("Upgrade")]
    [SerializeField] private PlayerUpgradableStats.UpgradeType upgradeType = PlayerUpgradableStats.UpgradeType.MoveSpeed;

    [Tooltip("Adds directly to the stat multiplier. Example: +0.10 means +10% to that stat.")]
    [SerializeField] private float addDeltaMultiplier = 0.10f;

    [Header("Description")]
    [TextArea(1, 4)]
    [SerializeField] private string description;

    [Header("Cost")]
    [Min(0)]
    [SerializeField] private int cost = 0;

    public PlayerUpgradableStats.UpgradeType UpgradeType => upgradeType;
    public float AddDeltaMultiplier => addDeltaMultiplier;
    public string Description => description;
    public int Cost => cost;

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Keep deltas within the same clamp window enforced by ServerApplyUpgradeInternal.
        addDeltaMultiplier = Mathf.Clamp(addDeltaMultiplier, -2f, 2f);
        cost = Mathf.Max(0, cost);

        description ??= string.Empty;
    }
#endif
}