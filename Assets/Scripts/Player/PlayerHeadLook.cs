using UnityEngine;
using Unity.Netcode;

[DisallowMultipleComponent]
public class PlayerHeadLook : NetworkBehaviour
{
    [Header("References")]
    [Tooltip("Head bone transform in your rig (e.g., Armature/Hips/Spine/Neck/Head).")]
    [SerializeField] private Transform headBone;

    [Tooltip("Camera used for aiming the head. If null, tries PlayerFirstPersonLook.playerCamera or Camera.main.")]
    [SerializeField] private Transform cameraTransform;

    [Tooltip("Optional: used to find camera if not provided.")]
    [SerializeField] private PlayerFirstPersonLook firstPersonLook;

    [Tooltip("Reference to the Animator component (required for OnAnimatorIK).")]
    [SerializeField] private Animator animator;

    [Header("Limits")]
    [Tooltip("Max left/right rotation from rest (degrees).")]
    [SerializeField] private float maxYaw = 75f;
    [Tooltip("Max up rotation from rest (degrees).")]
    [SerializeField] private float maxPitchUp = 60f;
    [Tooltip("Max down rotation from rest (degrees).")]
    [SerializeField] private float maxPitchDown = 80f;

    [Header("Weight & Smoothing")]
    [Tooltip("0 = animation only, 1 = full head follow.")]
    [Range(0f, 1f)]
    [SerializeField] private float weight = 1f;

    [Tooltip("How quickly the head follows the camera (bigger = snappier).")]
    [SerializeField] private float followLerp = 16f;

    [Header("Networking")]
    [Tooltip("Owner computes from their camera; others read replicated angles so they see it.")]
    [SerializeField] private bool ownerOnly = true;

    // yaw (x), pitch (y) in degrees, relative to rest pose (already clamped on write)
    private NetworkVariable<Vector2> _repYawPitch =
        new NetworkVariable<Vector2>(Vector2.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private Quaternion _restLocalRot;
    private Vector3 _restForwardLocal;
    private bool _initialized;
    private Quaternion _targetRotation;

    private void Awake()
    {
        CacheRefs();
        
        // Find animator if not assigned
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
    }

    private void OnEnable()
    {
        CacheRefs();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        CacheRefs();
    }

    private void CacheRefs()
    {
        if (headBone != null)
        {
            _restLocalRot = headBone.localRotation;
            _restForwardLocal = _restLocalRot * Vector3.forward;
            _initialized = true;
        }

        if (cameraTransform == null)
        {
            if (firstPersonLook == null)
                firstPersonLook = GetComponentInParent<PlayerFirstPersonLook>();

            if (firstPersonLook != null && firstPersonLook.playerCamera != null)
                cameraTransform = firstPersonLook.playerCamera.transform;
            else if (Camera.main != null)
                cameraTransform = Camera.main.transform;
        }
    }

    private void LateUpdate()
    {
        if (!_initialized || headBone == null || headBone.parent == null)
            return;

        float yawDeg, pitchDeg;

        if (ownerOnly)
        {
            if (IsOwner)
            {
                if (cameraTransform == null) return;

                Vector3 desiredDirLocal = headBone.parent.InverseTransformDirection(cameraTransform.forward);
                Vector3 dirRest = Quaternion.Inverse(_restLocalRot) * desiredDirLocal;

                yawDeg = Mathf.Atan2(dirRest.x, dirRest.z) * Mathf.Rad2Deg;
                pitchDeg = -Mathf.Asin(Mathf.Clamp(dirRest.y, -1f, 1f)) * Mathf.Rad2Deg;

                yawDeg = Mathf.Clamp(yawDeg, -maxYaw, maxYaw);
                pitchDeg = Mathf.Clamp(pitchDeg, -maxPitchUp, maxPitchDown);

                _repYawPitch.Value = new Vector2(yawDeg, pitchDeg);
            }
            else
            {
                Vector2 a = _repYawPitch.Value;
                yawDeg = Mathf.Clamp(a.x, -maxYaw, maxYaw);
                pitchDeg = Mathf.Clamp(a.y, -maxPitchUp, maxPitchDown);
            }
        }
        else
        {
            if (cameraTransform == null) return;

            Vector3 desiredDirLocal = headBone.parent.InverseTransformDirection(cameraTransform.forward);
            Vector3 dirRest = Quaternion.Inverse(_restLocalRot) * desiredDirLocal;

            yawDeg = Mathf.Atan2(dirRest.x, dirRest.z) * Mathf.Rad2Deg;
            pitchDeg = -Mathf.Asin(Mathf.Clamp(dirRest.y, -1f, 1f)) * Mathf.Rad2Deg;

            yawDeg = Mathf.Clamp(yawDeg, -maxYaw, maxYaw);
            pitchDeg = Mathf.Clamp(pitchDeg, -maxPitchUp, maxPitchDown);
        }

        // Calculate target rotation
        float cy = Mathf.Cos(yawDeg * Mathf.Deg2Rad);
        float sy = Mathf.Sin(yawDeg * Mathf.Deg2Rad);
        float cp = Mathf.Cos(pitchDeg * Mathf.Deg2Rad);
        float sp = Mathf.Sin(pitchDeg * Mathf.Deg2Rad);

        Vector3 clampedRestDir = new Vector3(sy * cp, -sp, cy * cp);
        Vector3 clampedLocalDir = _restLocalRot * clampedRestDir;

        Quaternion delta = Quaternion.FromToRotation(_restForwardLocal, clampedLocalDir);
        Quaternion targetLocal = _restLocalRot * delta;

        Quaternion current = headBone.localRotation;
        Quaternion followed = Quaternion.Slerp(current, targetLocal, 1f - Mathf.Exp(-followLerp * Time.deltaTime));

        if (weight < 1f)
            followed = Quaternion.Slerp(current, followed, weight);

        // Store for OnAnimatorIK
        _targetRotation = followed;
    }

    // This runs AFTER animation updates, ensuring head rotation isn't overridden
    private void OnAnimatorIK(int layerIndex)
    {
        if (!_initialized || headBone == null || weight <= 0f)
            return;

        headBone.localRotation = _targetRotation;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        maxYaw = Mathf.Max(0f, maxYaw);
        maxPitchUp = Mathf.Max(0f, maxPitchUp);
        maxPitchDown = Mathf.Max(0f, maxPitchDown);
        followLerp = Mathf.Max(0.01f, followLerp);
        weight = Mathf.Clamp01(weight);
    }
#endif

    public void SetWeight(float w)
    {
        weight = Mathf.Clamp01(w);
    }

    public void SetHeadRestPoseFromCurrent()
    {
        if (headBone == null) return;
        _restLocalRot = headBone.localRotation;
        _restForwardLocal = _restLocalRot * Vector3.forward;
        _initialized = true;
    }
}