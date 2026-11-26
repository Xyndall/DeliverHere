using System.Collections;
using UnityEngine;

[RequireComponent(typeof(DeliverHere.Player.Ladder))]
[RequireComponent(typeof(Rigidbody))]
public class LadderItem : EquippableItem
{
    [Header("Ladder Placement")]
    [Tooltip("Distance in front of the holder (along their flat forward) the ladder is placed.")]
    [SerializeField] private float placeDistance = 1f;
    [Tooltip("Height above the holder's position the ladder is released from, so it doesn't clip into the ground before falling.")]
    [SerializeField] private float dropHeight = 0.5f;

    [Header("Fall Timing")]
    [Tooltip("Layers considered 'ground' - only a collision with one of these triggers the forward tip.")]
    [SerializeField] private LayerMask groundMask = ~0;
    [Tooltip("Small delay after touching the ground before it starts to tip, purely for a natural 'thud, then topple' feel. 0 = instant.")]
    [SerializeField] private float tipDelayAfterLanding = 0.1f;
    [Tooltip("Small forward torque nudge given when it starts falling so it reliably tips forward instead of balancing indefinitely.")]
    [SerializeField] private float forwardTipTorque = 4f;

    [Header("Physics")]
    [Tooltip("Rigidbody used for the fall (optional, falls back to GetComponent).")]
    [SerializeField] private Rigidbody placedRigidbody;
    [Tooltip("Local-space position of the ladder's base (the pivot point it tips around). If the model's pivot is already at the base, leave this at (0,0,0). If the pivot is at the center, set this to roughly (0, -halfLength, 0).")]
    [SerializeField] private Vector3 baseLocalOffset = Vector3.zero;

    private bool _waitingForLanding;
    private bool _hasTipped;
    private Vector3 _tipForward;
    private DeliverHere.Player.Ladder _ladder;
    private HingeJoint _hinge;

    private void Reset()
    {
        if (placedRigidbody == null) placedRigidbody = GetComponent<Rigidbody>();
    }

    private void Awake()
    {
        if (placedRigidbody == null) placedRigidbody = GetComponent<Rigidbody>();
        if (_ladder == null) _ladder = GetComponent<DeliverHere.Player.Ladder>();
    }

    public override void OnUse(EquipmentManager holder)
    {
        if (!IsServer || holder == null) return;
        holder.UnequipCurrentServer();
    }

    protected override void OnEquipped(EquipmentManager holder)
    {
        base.OnEquipped(holder);

        // Being picked up mid-climb: kick any player currently climbing this ladder
        // back to normal movement immediately, instead of leaving them stuck
        // referencing a ladder that's now in someone's hand.
        if (_ladder != null)
            _ladder.ForceExitAnyClimbers();

        // Picked back up before it finished falling/tipping: clean up the hinge
        // and any in-progress fall state so re-dropping starts fresh.
        if (_hinge != null)
        {
            Destroy(_hinge);
            _hinge = null;
        }
        _waitingForLanding = false;
        _hasTipped = false;
    }

    protected override void OnUnequipped(EquipmentManager previousHolder)
    {
        base.OnUnequipped(previousHolder);

        if (!IsServer || previousHolder == null) return;

        StartCoroutine(PlaceAndFall(previousHolder.transform));
    }

    private IEnumerator PlaceAndFall(Transform holderTransform)
    {
        yield return null; // let EquipmentManager's own drop positioning run first

        if (holderTransform == null) yield break;

        Vector3 flatForward = holderTransform.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.0001f) flatForward = Vector3.forward;
        flatForward.Normalize();

        Vector3 targetPos = holderTransform.position + flatForward * placeDistance + Vector3.up * dropHeight;
        Quaternion targetRot = Quaternion.LookRotation(flatForward, Vector3.up);

        transform.SetPositionAndRotation(targetPos, targetRot);

        _tipForward = flatForward;
        _hasTipped = false;
        _waitingForLanding = true;

        if (placedRigidbody != null)
        {
            placedRigidbody.isKinematic = false;
            placedRigidbody.linearVelocity = Vector3.zero;
            placedRigidbody.angularVelocity = Vector3.zero;

            // Fall straight down under gravity, but stay locked vertical (no toppling)
            // until it actually touches the ground.
            placedRigidbody.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer || !_waitingForLanding || _hasTipped) return;

        // Only ground layers count as "landed" - ignore the holder's own collider,
        // other players, props, etc. on the way down.
        if (((1 << collision.gameObject.layer) & groundMask) == 0) return;

        _waitingForLanding = false;
        _hasTipped = true;

        if (tipDelayAfterLanding > 0f)
            StartCoroutine(TipAfterDelay(tipDelayAfterLanding));
        else
            BeginTip();
    }

    private IEnumerator TipAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        BeginTip();
    }

    private void BeginTip()
    {
        if (placedRigidbody == null) return;

        placedRigidbody.centerOfMass = baseLocalOffset;

        // Release the lock and nudge it off-balance - after this, it's plain
        // physics: leans on whatever's there, or falls flat.
        placedRigidbody.constraints = RigidbodyConstraints.None;

        Vector3 rightAxis = Vector3.Cross(Vector3.up, _tipForward);
        placedRigidbody.AddTorque(rightAxis * forwardTipTorque, ForceMode.VelocityChange);
    }
}