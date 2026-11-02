using UnityEngine;

/// <summary>
/// Equippable grapple gun. Holds no grapple logic itself -- just the item's
/// identity/visual bits (muzzle point, rope line renderer) and tunable stats.
/// The actual grapple behavior lives on the player (see PlayerGrapple), which
/// reads these values whenever this item is the one currently equipped.
/// </summary>
[DisallowMultipleComponent]
public class GrappleGunItem : EquippableItem
{
    [Header("Grapple Visual")]
    [Tooltip("Tip of the gun model the rope is drawn from.")]
    [SerializeField] private Transform muzzle;
    [Tooltip("LineRenderer used to draw the rope while shooting/pulling/retracting.")]
    [SerializeField] private LineRenderer ropeLine;

    [Header("Grapple Range / Speed")]
    [SerializeField] private float maxRange = 30f;
    [Tooltip("Speed the hook travels out at before it hits something (visual only).")]
    [SerializeField] private float shootSpeed = 80f;
    [Tooltip("Speed the player is pulled toward the hit point.")]
    [SerializeField] private float pullSpeed = 20f;
    [Tooltip("Distance from the hit point at which the grapple auto-cancels (reached destination).")]
    [SerializeField] private float arrivalDistance = 1.25f;
    [Tooltip("How quickly the pull velocity ramps up/down (higher = snappier).")]
    [SerializeField] private float pullAcceleration = 60f;

    [Header("Grapple Fire Rate")]
    [Tooltip("Minimum seconds between grapple shots.")]
    [SerializeField] private float fireRate = 1f;

    [Header("Grapple Targeting")]
    [SerializeField] private LayerMask grappleLayerMask = ~0;

    public Transform Muzzle => muzzle;
    public LineRenderer RopeLine => ropeLine;
    public float MaxRange => maxRange;
    public float ShootSpeed => shootSpeed;
    public float PullSpeed => pullSpeed;
    public float ArrivalDistance => arrivalDistance;
    public float PullAcceleration => pullAcceleration;
    public float FireRate => fireRate;
    public LayerMask GrappleLayerMask => grappleLayerMask;

    protected override void OnEquipped(EquipmentManager holder)
    {
        base.OnEquipped(holder);
        if (ropeLine != null) ropeLine.enabled = false;
    }

    protected override void OnUnequipped(EquipmentManager previousHolder)
    {
        base.OnUnequipped(previousHolder);
        if (ropeLine != null) ropeLine.enabled = false;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        maxRange = Mathf.Max(1f, maxRange);
        shootSpeed = Mathf.Max(1f, shootSpeed);
        pullSpeed = Mathf.Max(1f, pullSpeed);
        arrivalDistance = Mathf.Max(0.1f, arrivalDistance);
        pullAcceleration = Mathf.Max(1f, pullAcceleration);
        fireRate = Mathf.Max(0.01f, fireRate);
    }
#endif
}