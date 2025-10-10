using UnityEngine;
using Unity.Netcode;
using Unity.Cinemachine;

[DisallowMultipleComponent]
public class PlayerCameraAssigner : NetworkBehaviour
{
    [Header("Targets")]
    [SerializeField] private Transform followTarget; // Player root or a head/anchor

    [Header("Cinemachine")]
    [Tooltip("Optional explicit reference. If null, will try to find one in the scene.")]
    [SerializeField] private CinemachineCamera virtualCamera;
    [Tooltip("If true, will search the scene for a suitable Cinemachine camera if none are assigned.")]
    [SerializeField] private bool autoFindCameraInScene = true;

    [Header("Control")]
    [SerializeField] private PlayerFirstPersonLook cameraController;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        enabled = IsOwner;
        if (!enabled) return;

        AssignCameraToPlayer();
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        enabled = true;
        AssignCameraToPlayer();
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        enabled = false;

        // Optional: unparent if we were the owner and had parented it
        if (virtualCamera != null && followTarget != null && virtualCamera.transform.parent == followTarget)
        {
            virtualCamera.transform.SetParent(null, true); // keep world pose on unparent
        }
    }

    private void AssignCameraToPlayer()
    {
        if (followTarget == null) followTarget = transform;

        if (virtualCamera == null && autoFindCameraInScene)
            virtualCamera = FindFirstObjectByType<CinemachineCamera>();

        if (virtualCamera == null)
        {
            Debug.LogWarning("[PlayerCameraAssigner] No CinemachineCamera found to assign.", this);
            return;
        }

        // Parent the camera to the follow target and snap to zero local offset/rotation
        var camTransform = virtualCamera.transform;
        camTransform.SetParent(followTarget, false); // do not preserve world pose
        camTransform.localPosition = Vector3.zero;
        camTransform.localRotation = Quaternion.identity;
        camTransform.localScale = Vector3.one;

        // Inform the FP look controller
        if (cameraController != null)
        {
            cameraController.playerCamera = virtualCamera;
            cameraController.CameraIsAssigned();
        }
    }
}