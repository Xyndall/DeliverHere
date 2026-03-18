using UnityEngine;
using System;

[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public class PlayerArms : MonoBehaviour
{
    [Header("References")]
    [Tooltip("If null, will search up the hierarchy (child model -> root player object).")]
    [SerializeField] private PlayerHold playerHold;

    [Header("Grip Marker Names (on package prefab)")]
    [SerializeField] private string gripCenterName = "GripCenter";
    [SerializeField] private string leftGripName = "LeftGrip";
    [SerializeField] private string rightGripName = "RightGrip";

    [Header("Fallback (when markers are missing)")]
    [Tooltip("Offset applied in the package's LOCAL space when only a center point is available.")]
    [SerializeField] private Vector3 defaultLocalOffset = new Vector3(0f, -0.15f, 0f);
    [Tooltip("Horizontal spacing between hands when only a center point is available.")]
    [SerializeField] private float handHorizontalSeparation = 0.25f;
    [Tooltip("Extra vertical offset added to computed bounds center (meters).")]
    [SerializeField] private float verticalOffset = 0.0f;

    [Header("IK Weights")]
    [Range(0f, 1f)]
    [SerializeField] private float holdIKWeight = 1f;
    [SerializeField] private float blendSpeed = 8f;

    [Header("Rotation")]
    [Tooltip("Rotate hands to match grip orientation/center orientation.")]
    [SerializeField] private bool applyRotation = true;

    [Header("Debug")]
    [SerializeField] private bool logMissingAnimatorIKSupport = false;

    private Animator _anim;

    private float _currentWeight;
    private Rigidbody _currentHeld;

    // Grip points (prefer explicit)
    private Transform _gripCenter;
    private Transform _leftGrip;
    private Transform _rightGrip;

    // Runtime fallback helper (if no GripCenter exists)
    private Transform _runtimeGripCenter;

    private void Awake()
    {
        _anim = GetComponent<Animator>();

        if (playerHold == null)
            playerHold = GetComponentInParent<PlayerHold>(true);

        if (logMissingAnimatorIKSupport && _anim != null && !_anim.isHuman)
        {
            Debug.LogWarning($"{nameof(PlayerArms)} expects a Humanoid rig for IK. '{name}' Animator is not humanoid.");
        }
    }

    private void OnEnable()
    {
        Bind();
    }

    private void OnDisable()
    {
        Unbind();
        CleanupRuntimeGripCenter();
    }

    private void Bind()
    {
        if (playerHold == null)
            playerHold = GetComponentInParent<PlayerHold>(true);

        if (playerHold == null)
            return;

        playerHold.PickedUp += OnPickedUp;
        playerHold.Dropped += OnDropped;
    }

    private void Unbind()
    {
        if (playerHold == null)
            return;

        playerHold.PickedUp -= OnPickedUp;
        playerHold.Dropped -= OnDropped;
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
        CleanupRuntimeGripCenter();
    }

    private void CleanupRuntimeGripCenter()
    {
        if (_runtimeGripCenter == null)
            return;

        Destroy(_runtimeGripCenter.gameObject);
        _runtimeGripCenter = null;
    }

    private void ResolveGripPoints(Transform package)
    {
        _gripCenter = _leftGrip = _rightGrip = null;
        CleanupRuntimeGripCenter();

        if (package == null)
            return;

        // Look for markers by name (case-insensitive).
        foreach (Transform t in package.GetComponentsInChildren<Transform>(true))
        {
            if (t == null) continue;

            if (string.Equals(t.name, gripCenterName, StringComparison.OrdinalIgnoreCase))
                _gripCenter = t;
            else if (string.Equals(t.name, leftGripName, StringComparison.OrdinalIgnoreCase))
                _leftGrip = t;
            else if (string.Equals(t.name, rightGripName, StringComparison.OrdinalIgnoreCase))
                _rightGrip = t;
        }

        if (_gripCenter == null)
        {
            // Fallback: bounds center -> runtime transform so we have stable position+rotation.
            Vector3 center = package.position;
            Collider col = package.GetComponent<Collider>() ?? package.GetComponentInChildren<Collider>();
            if (col != null)
                center = col.bounds.center;

            GameObject temp = new GameObject("RuntimeGripCenter");
            temp.hideFlags = HideFlags.HideAndDontSave;
            temp.transform.SetPositionAndRotation(center + Vector3.up * verticalOffset, package.rotation);

            _runtimeGripCenter = temp.transform;
            _gripCenter = _runtimeGripCenter;
        }
    }

    private void Update()
    {
        float target = (_currentHeld != null) ? Mathf.Clamp01(holdIKWeight) : 0f;
        _currentWeight = Mathf.MoveTowards(_currentWeight, target, blendSpeed * Time.deltaTime);
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (_anim == null)
            return;

        float w = _currentWeight;

        _anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, w);
        _anim.SetIKPositionWeight(AvatarIKGoal.RightHand, w);

        float rw = applyRotation ? w : 0f;
        _anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, rw);
        _anim.SetIKRotationWeight(AvatarIKGoal.RightHand, rw);

        if (w <= 0.0001f || _currentHeld == null)
            return;

        Vector3 leftPos, rightPos;
        Quaternion targetRot;

        if (_leftGrip != null && _rightGrip != null)
        {
            // Best: per-hand markers.
            leftPos = _leftGrip.position;
            rightPos = _rightGrip.position;

            // Use center's rotation if available, otherwise average-ish by taking package rotation.
            targetRot = _gripCenter != null ? _gripCenter.rotation : _currentHeld.rotation;
        }
        else
        {
            // OK: compute two hand points around a single center.
            Transform center = _gripCenter != null ? _gripCenter : _currentHeld.transform;
            targetRot = center.rotation;

            Vector3 rightAxis = targetRot * Vector3.right;

            // IMPORTANT: offset in package LOCAL space (not world).
            Vector3 basePoint = center.TransformPoint(defaultLocalOffset);

            leftPos = basePoint - rightAxis * (handHorizontalSeparation * 0.5f);
            rightPos = basePoint + rightAxis * (handHorizontalSeparation * 0.5f);
        }

        _anim.SetIKPosition(AvatarIKGoal.LeftHand, leftPos);
        _anim.SetIKPosition(AvatarIKGoal.RightHand, rightPos);

        if (applyRotation)
        {
            _anim.SetIKRotation(AvatarIKGoal.LeftHand, targetRot);
            _anim.SetIKRotation(AvatarIKGoal.RightHand, targetRot);
        }
    }
}