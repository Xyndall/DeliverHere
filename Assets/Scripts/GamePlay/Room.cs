using System.Collections.Generic;
using UnityEngine;

public class Room : MonoBehaviour
{
    [Tooltip("Empty transforms placed at the bottom-center of each doorway, with local Z (blue arrow) pointing OUT of the room.")]
    public List<Transform> doorwaySockets = new List<Transform>();

    [Tooltip("Trigger BoxColliders defining this room's physical footprint. Use multiple for L-shaped rooms.")]
    public List<BoxCollider> boundsColliders = new List<BoxCollider>();

    [System.NonSerialized] public Room sourcePrefab;

#if UNITY_EDITOR
    [Header("Gizmo Debug")]
    [SerializeField] private float gizmoArrowLength = 1.5f;

    private void OnDrawGizmos()
    {
        if (doorwaySockets == null) return;

        foreach (var socket in doorwaySockets)
        {
            if (socket == null) continue;

            // Forward (should point OUT of the room) - RED
            Gizmos.color = Color.red;
            Gizmos.DrawLine(socket.position, socket.position + socket.forward * gizmoArrowLength);

            // Up - GREEN (helps spot accidental roll/tilt)
            Gizmos.color = Color.green;
            Gizmos.DrawLine(socket.position, socket.position + socket.up * gizmoArrowLength * 0.5f);

            // Socket origin - YELLOW sphere
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(socket.position, 0.1f);

            // Warn if lossyScale isn't uniform
            Vector3 s = socket.lossyScale;
            if (Mathf.Abs(s.x - s.y) > 0.001f || Mathf.Abs(s.y - s.z) > 0.001f)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireCube(socket.position, Vector3.one * 0.3f);
            }
        }
    }
#endif
}
