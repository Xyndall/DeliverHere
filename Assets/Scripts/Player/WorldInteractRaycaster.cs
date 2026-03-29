using UnityEngine;

namespace DeliverHere.Interaction
{
    [DisallowMultipleComponent]
    public class WorldInteractRaycaster : MonoBehaviour
    {
        [Header("Camera (used for aiming interact ray)")]
        [SerializeField] private Transform cameraTransform;

        [Header("Detection")]
        [SerializeField] private LayerMask interactMask = ~0;
        [SerializeField, Min(0.01f)] private float castRadius = 0.15f;
        [SerializeField, Min(0.1f)] private float range = 3f;
        [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore;

        // Last hit (optional consumer can inspect if needed)
        public GameObject LastHitObject { get; private set; }

        private void Awake()
        {
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
        }

        private void OnValidate()
        {
            castRadius = Mathf.Max(0.01f, castRadius);
            range = Mathf.Max(0.1f, range);
        }

        /// <summary>
        /// Try to find the first WorldSpawnButton in front of the camera according to the configured mask.
        /// Returns true and sets 'button' if found.
        /// </summary>
        public bool TryGetLookedAtButton(out DeliverHere.GamePlay.WorldSpawnButton button)
        {
            button = null;
            LastHitObject = null;

            if (cameraTransform == null) return false;

            // SphereCast for a forgiving aim
            if (!Physics.SphereCast(
                cameraTransform.position,
                castRadius,
                cameraTransform.forward,
                out var hit,
                range,
                interactMask,
                triggerInteraction))
            {
                return false;
            }

            if (hit.collider == null) return false;

            LastHitObject = hit.collider.gameObject;
            button = hit.collider.GetComponentInParent<DeliverHere.GamePlay.WorldSpawnButton>();
            return button != null;
        }

        // Public setter so other scripts (player prefabs) can assign a camera if needed
        public void SetCameraTransform(Transform cam) => cameraTransform = cam;
    }
}