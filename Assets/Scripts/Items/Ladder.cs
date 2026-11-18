using UnityEngine;

namespace DeliverHere.Player
{
    /// <summary>
    /// Marks a climbable ladder. Requires a trigger collider on the same GameObject
    /// covering the climbable region. The player's PlayerLadderClimb detector calls
    /// into PlayerMovement when entering/exiting this trigger.
    /// </summary>
    [DisallowMultipleComponent]
    public class Ladder : MonoBehaviour
    {
        /// <summary>World-space direction the player climbs "up" along.</summary>
        public Vector3 Up => transform.up;

        /// <summary>Direction pointing away from the rungs (used to push the player off when jumping).</summary>
        public Vector3 Forward => transform.forward;

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(transform.position, transform.position + transform.up * 2f);
            Gizmos.DrawRay(transform.position, transform.forward * 1f);
        }
#endif
    }
}