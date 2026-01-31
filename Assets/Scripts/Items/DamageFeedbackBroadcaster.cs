using UnityEngine;
using Unity.Netcode;

public class DamageFeedbackBroadcaster : NetworkBehaviour
{
    [Header("Floating Text Prefab")]
    [Tooltip("World-space TextMeshPro (FloatingWorldText) prefab to spawn on damage.")]
    [SerializeField] private FloatingWorldText floatingTextPrefab;

    [Header("Text Appearance")]
    [SerializeField] private Color damageColor = new Color(1f, 0.2f, 0.2f, 1f);

    [ClientRpc]
    public void ShowDamageClientRpc(int amount, Vector3 localOffset)
    {
        if (floatingTextPrefab == null) return;
        FloatingWorldText.Spawn(floatingTextPrefab, transform, localOffset, $"-${amount}", damageColor);
    }

    public void ShowLocal(int amount, Vector3 localOffset)
    {
        if (floatingTextPrefab == null) return;
        FloatingWorldText.Spawn(floatingTextPrefab, transform, localOffset, $"-${amount}", damageColor);
    }
}