using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public class PlayerArms : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerHold playerHold;
    [Tooltip("Optional explicit grip center override when package lacks markers.")]
    [SerializeField] private Vector3 defaultLocalOffset = new Vector3(0f, -0.15f, 0f);
    [Tooltip("Horizontal spacing between hands when only a center point is available.")]
    [SerializeField] private float handHorizontalSeparation = 0.25f;
    [Tooltip("Vertical alignment offset added to package center.")]
    [SerializeField] private float verticalOffset = 0.0f;

    [Header("IK Weights")]
    [SerializeField] private float holdIKWeight = 1f;
    [SerializeField] private float blendSpeed = 6f;

    [Header("Rotation")]
    [Tooltip("Should hands rotate to match grip orientation?")]
    [SerializeField] private bool applyRotation = true;

    private Animator _anim;
    private float _currentWeight;
    private Rigidbody _currentHeld;

    // Cached grip points (if found)
    private Transform _gripCenter;
    private Transform _leftGrip;
    private Transform _rightGrip;

    private void Awake()
    {
        _anim = GetComponent<Animator>();
        if (playerHold == null)
            playerHold = GetComponentInParent<PlayerHold>();
    }

    private void OnEnable()
    {
        if (playerHold != null)
        {
            playerHold.PickedUp += OnPickedUp;
            playerHold.Dropped += OnDropped;
        }
    }

    private void OnDisable()
    {
        if (playerHold != null)
        {
            playerHold.PickedUp -= OnPickedUp;
            playerHold.Dropped -= OnDropped;
        }
    }

    private void OnPickedUp(Rigidbody rb)
    {
        _currentHeld = rb;
        ResolveGripPoints(rb != null ? rb.transform : null);
    }

    private void OnDropped(Rigidbody rb)
    {
        _currentHeld = null;
        _gripCenter = _leftGrip = _rightGrip = null;
    }

    private void ResolveGripPoints(Transform package)
    {
        _gripCenter = _leftGrip = _rightGrip = null;
        if (package == null) return;

        // Try named children
        foreach (Transform t in package.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name;
            if (n.Equals("GripCenter", System.StringComparison.OrdinalIgnoreCase)) _gripCenter = t;
            else if (n.Equals("LeftGrip", System.StringComparison.OrdinalIgnoreCase)) _leftGrip = t;
            else if (n.Equals("RightGrip", System.StringComparison.OrdinalIgnoreCase)) _rightGrip = t;
        }

        // Fallback: make a temporary center if none exists
        if (_gripCenter == null)
        {
            // Use collider bounds center
            Vector3 center = package.position;
            var col = package.GetComponent<Collider>() ?? package.GetComponentInChildren<Collider>();
            if (col != null) center = col.bounds.center;

            // Create a runtime-only helper (not parented so remote clients don’t need it)
            GameObject temp = new GameObject("RuntimeGripCenter");
            temp.hideFlags = HideFlags.HideAndDontSave;
            temp.transform.position = center + Vector3.up * verticalOffset;
            temp.transform.rotation = package.rotation;
            _gripCenter = temp.transform;
        }
    }

    private void Update()
    {
        // Smooth weight target
        float target = (_currentHeld != null) ? holdIKWeight : 0f;
        _currentWeight = Mathf.MoveTowards(_currentWeight, target, blendSpeed * Time.deltaTime);
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (_anim == null) return;

        float w = _currentWeight;
        _anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, w);
        _anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, applyRotation ? w : 0f);
        _anim.SetIKPositionWeight(AvatarIKGoal.RightHand, w);
        _anim.SetIKRotationWeight(AvatarIKGoal.RightHand, applyRotation ? w : 0f);

        if (w <= 0.0001f || _currentHeld == null)
            return;

        // Determine target positions
        Vector3 leftPos, rightPos;
        Quaternion rot;

        if (_leftGrip != null && _rightGrip != null)
        {
            leftPos = _leftGrip.position;
            rightPos = _rightGrip.position;
            rot = _leftGrip.rotation; // could average rotations
        }
        else
        {
            Transform center = _gripCenter != null ? _gripCenter : _currentHeld.transform;
            rot = center.rotation;

            // Build lateral axis for spacing
            Vector3 forward = rot * Vector3.forward;
            Vector3 up = rot * Vector3.up;
            Vector3 right = Vector3.Cross(up, forward).normalized;
            if (right.sqrMagnitude < 1e-4f) right = Vector3.right;

            Vector3 basePoint = center.position;
            leftPos = basePoint - right * (handHorizontalSeparation * 0.5f) + defaultLocalOffset;
            rightPos = basePoint + right * (handHorizontalSeparation * 0.5f) + defaultLocalOffset;
        }

        _anim.SetIKPosition(AvatarIKGoal.LeftHand, leftPos);
        _anim.SetIKRotation(AvatarIKGoal.LeftHand, rot);
        _anim.SetIKPosition(AvatarIKGoal.RightHand, rightPos);
        _anim.SetIKRotation(AvatarIKGoal.RightHand, rot);
    }
}