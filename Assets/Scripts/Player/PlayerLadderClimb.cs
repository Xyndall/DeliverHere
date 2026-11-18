using UnityEngine;

namespace DeliverHere.Player
{
    /// <summary>
    /// Detects ladder trigger volumes and forwards enter/exit events to PlayerMovement.
    /// Requires a trigger collider on this GameObject (separate from the CharacterController).
    /// </summary>
    [RequireComponent(typeof(PlayerMovement))]
    public class PlayerLadderClimb : MonoBehaviour
    {
        private PlayerMovement _movement;

        private void Awake()
        {
            _movement = GetComponent<PlayerMovement>();
        }

        private void OnTriggerEnter(Collider other)
        {
            Ladder ladder = other.GetComponentInParent<Ladder>();
            if (ladder != null)
                _movement.EnterLadder(ladder);
        }

        private void OnTriggerExit(Collider other)
        {
            Ladder ladder = other.GetComponentInParent<Ladder>();
            if (ladder != null)
                _movement.ExitLadder(ladder);
        }
    }
}