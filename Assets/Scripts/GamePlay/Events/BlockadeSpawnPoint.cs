using UnityEngine;

/// <summary>
/// Marks a location where a blockade can spawn during the Blockade event.
/// Place these at strategic points (intersections, roads, choke points).
/// </summary>
public class BlockadeSpawnPoint : MonoBehaviour
{
    [Header("Visual Debug")]
    [SerializeField] private bool showGizmo = true;
    [SerializeField] private Color gizmoColor = Color.red;
    [SerializeField] private float gizmoSize = 1f;

    private void OnDrawGizmos()
    {
        if (!showGizmo) return;

        Gizmos.color = gizmoColor;
        Gizmos.DrawWireCube(transform.position, Vector3.one * gizmoSize);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * gizmoSize);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(transform.position, Vector3.one * (gizmoSize * 1.5f));
    }
}