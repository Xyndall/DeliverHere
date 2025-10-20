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

    private void Awake()
    {
        CacheRefs();
    }

    private void OnEnable()
    {
        CacheRefs();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // Ensure refs exist when spawned on remote clients too
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

                // Desired direction in parent space (so we can build a local rotation)
                Vector3 desiredDirLocal = headBone.parent.InverseTransformDirection(cameraTransform.forward);

                // Convert desired direction into the head's rest-space to measure yaw/pitch from the reference pose
                Vector3 dirRest = Quaternion.Inverse(_restLocalRot) * desiredDirLocal;

                // Yaw around +Y, Pitch positive = down (so up is negative)
                yawDeg = Mathf.Atan2(dirRest.x, dirRest.z) * Mathf.Rad2Deg;
                pitchDeg = -Mathf.Asin(Mathf.Clamp(dirRest.y, -1f, 1f)) * Mathf.Rad2Deg;

                // Clamp
                yawDeg = Mathf.Clamp(yawDeg, -maxYaw, maxYaw);
                pitchDeg = Mathf.Clamp(pitchDeg, -maxPitchUp, maxPitchDown);

                // Replicate to others
                _repYawPitch.Value = new Vector2(yawDeg, pitchDeg);
            }
            else
            {
                // Non-owners: consume replicated angles
                Vector2 a = _repYawPitch.Value;
                yawDeg = Mathf.Clamp(a.x, -maxYaw, maxYaw);
                pitchDeg = Mathf.Clamp(a.y, -maxPitchUp, maxPitchDown);
            }
        }
        else
        {
            // Everyone tries to use their local camera (typically not what you want for multiplayer).
            if (cameraTransform == null) return;

            Vector3 desiredDirLocal = headBone.parent.InverseTransformDirection(cameraTransform.forward);
            Vector3 dirRest = Quaternion.Inverse(_restLocalRot) * desiredDirLocal;

            yawDeg = Mathf.Atan2(dirRest.x, dirRest.z) * Mathf.Rad2Deg;
            pitchDeg = -Mathf.Asin(Mathf.Clamp(dirRest.y, -1f, 1f)) * Mathf.Rad2Deg;

            yawDeg = Mathf.Clamp(yawDeg, -maxYaw, maxYaw);
            pitchDeg = Mathf.Clamp(pitchDeg, -maxPitchUp, maxPitchDown);
        }

        // Reconstruct clamped direction in rest space
        float cy = Mathf.Cos(yawDeg * Mathf.Deg2Rad);
        float sy = Mathf.Sin(yawDeg * Mathf.Deg2Rad);
        float cp = Mathf.Cos(pitchDeg * Mathf.Deg2Rad);
        float sp = Mathf.Sin(pitchDeg * Mathf.Deg2Rad);

        // Rest-space forward rotated by yaw, then by pitch (positive pitch looks down, so y = -sp)
        Vector3 clampedRestDir = new Vector3(sy * cp, -sp, cy * cp);

        // Back to parent local space
        Vector3 clampedLocalDir = _restLocalRot * clampedRestDir;

        // Build rotation that takes rest-forward to the clamped direction
        Quaternion delta = Quaternion.FromToRotation(_restForwardLocal, clampedLocalDir);
        Quaternion targetLocal = _restLocalRot * delta;

        // Smooth follow
        Quaternion current = headBone.localRotation;
        Quaternion followed = Quaternion.Slerp(current, targetLocal, 1f - Mathf.Exp(-followLerp * Time.deltaTime));

        // Blend with animation using weight
        if (weight < 1f)
            followed = Quaternion.Slerp(current, followed, weight);

        headBone.localRotation = followed;
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