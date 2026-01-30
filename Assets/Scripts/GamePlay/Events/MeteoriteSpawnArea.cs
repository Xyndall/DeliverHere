using UnityEngine;

/// <summary>
/// Place this on a GameObject with a BoxCollider to mark a meteorite spawn area in a level.
/// The BoxCollider's center/size defines the area.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class MeteoriteSpawnArea : MonoBehaviour
{
    public BoxCollider AreaCollider => GetComponent<BoxCollider>();

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        var col = GetComponent<BoxCollider>();
        if (col == null) return;
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.25f);
        var m = transform.localToWorldMatrix;
        Gizmos.matrix = m;
        Gizmos.DrawCube(col.center, col.size);
        Gizmos.color = new Color(1f, 0.5f, 0f, 1f);
        Gizmos.DrawWireCube(col.center, col.size);
        Gizmos.matrix = Matrix4x4.identity;
    }
#endif
}