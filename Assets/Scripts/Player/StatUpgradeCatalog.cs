using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "DeliverHere/Upgrades/Stat Upgrade Catalog", fileName = "StatUpgradeCatalog")]
public sealed class StatUpgradeCatalog : ScriptableObject
{
    [SerializeField] private List<StatUpgradeDefinition> definitions = new List<StatUpgradeDefinition>();

    public IReadOnlyList<StatUpgradeDefinition> Definitions => definitions;

    public bool TryGet(int index, out StatUpgradeDefinition def)
    {
        if (index >= 0 && definitions != null && index < definitions.Count && definitions[index] != null)
        {
            def = definitions[index];
            return true;
        }

        def = null;
        return false;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Keep list non-null and avoid null entries silently if you want.
        definitions ??= new List<StatUpgradeDefinition>();
    }
#endif
}