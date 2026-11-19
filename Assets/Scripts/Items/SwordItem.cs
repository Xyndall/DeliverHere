using System.Collections.Generic;
using UnityEngine;

public class SwordItem : EquippableItem
{
    [Header("Swing Detection")]
    [Tooltip("Radius of the capsule used to detect hits along the camera's forward direction.")]
    [SerializeField] private float castRadius = 0.4f;
    [Tooltip("How far forward (along the camera's forward direction) the cast reaches.")]
    [SerializeField] private float castRange = 3f;
    [Tooltip("Layers that can be hit by the swing.")]
    [SerializeField] private LayerMask hitMask = ~0;

    [Header("Force")]
    [Tooltip("Force applied to rigidbodies hit by the swing.")]
    [SerializeField] private float pushForce = 15f;
    [Tooltip("Upward bias added to the push direction (0 = pure forward push, 1 = equal mix of forward and up).")]
    [Range(0f, 1f)]
    [SerializeField] private float upwardBias = 0.25f;
    [SerializeField] private ForceMode forceMode = ForceMode.Impulse;

    [Header("Debug")]
    [SerializeField] private bool enableDebugLogs = true;
    [SerializeField] private bool drawGizmo = true;

    // Avoid pushing the same rigidbody twice if multiple colliders belong to it.
    private readonly HashSet<Rigidbody> _hitBuffer = new HashSet<Rigidbody>();

    // Re-grabbed every time the sword is equipped, since only Camera.main is ever used in this project.
    private Camera _wielderCamera;

    // Cached for gizmo drawing (only meaningful right after a swing).
    private Vector3 _lastCastOrigin;
    private Vector3 _lastCastEnd;
    private bool _hasLastCast;

    protected override void OnEquipped(EquipmentManager holder)
    {
        base.OnEquipped(holder);
        _wielderCamera = Camera.main;

        if (_wielderCamera == null && enableDebugLogs)
            Debug.LogWarning($"[SwordItem:{name}] OnEquipped: Camera.main was null, swing detection will be disabled until a main camera is found.", this);
    }

    protected override void OnUnequipped(EquipmentManager previousHolder)
    {
        base.OnUnequipped(previousHolder);
        _wielderCamera = null;
    }

    public override void OnUse(EquipmentManager holder)
    {
        base.OnUse(holder);

        if (!IsServer)
        {
            // Safety: force application must be server-authoritative.
            return;
        }

        Swing(holder);
    }

    private void Swing(EquipmentManager holder)
    {
        if (_wielderCamera == null)
        {
            // Re-grabbed defensively in case equip order/timing meant Camera.main wasn't ready yet.
            _wielderCamera = Camera.main;
            if (_wielderCamera == null)
            {
                if (enableDebugLogs)
                    Debug.LogWarning($"[SwordItem:{name}] Swing aborted: no main camera available.", this);
                return;
            }
        }

        Vector3 aimOrigin = _wielderCamera.transform.position;
        Vector3 aimDirection = _wielderCamera.transform.forward;

        // A capsule cast needs two points; shrink the capsule's "line" segment inward
        // by its own radius so the total reach still matches castRange.
        float segmentLength = Mathf.Max(0f, castRange - castRadius * 2f);
        Vector3 point1 = aimOrigin + aimDirection * castRadius;
        Vector3 point2 = point1 + aimDirection * segmentLength;

        _lastCastOrigin = point1;
        _lastCastEnd = point2;
        _hasLastCast = true;

        _hitBuffer.Clear();

        RaycastHit[] hits = Physics.CapsuleCastAll(point1, point2, castRadius, aimDirection, 0f, hitMask, QueryTriggerInteraction.Ignore);

        foreach (var hit in hits)
        {
            var col = hit.collider;
            if (col == null) continue;

            var rb = col.attachedRigidbody;
            if (rb == null) continue;

            // Don't hit the wielder's own rigidbody (e.g. their character controller rb).
            if (holder != null && rb.transform.IsChildOf(holder.transform)) continue;

            if (!_hitBuffer.Add(rb)) continue;

            Vector3 pushDir = (aimDirection + Vector3.up * upwardBias).normalized;
            rb.AddForce(pushDir * pushForce, forceMode);

            if (enableDebugLogs)
                Debug.Log($"[SwordItem:{name}] Swing hit '{rb.name}', applied {pushForce} force ({forceMode}).", this);
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmo || !_hasLastCast) return;

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(_lastCastOrigin, castRadius);
        Gizmos.DrawWireSphere(_lastCastEnd, castRadius);
        Gizmos.DrawLine(_lastCastOrigin, _lastCastEnd);
    }
}