using UnityEngine;
using Unity.Netcode;

namespace DeliverHere.Player
{
    /// <summary>
    /// Drives grapple behavior for the player, but only while a GrappleGunItem is the
    /// currently equipped item (see EquipmentManager). Aims straight down the camera's
    /// forward (screen center) -- no reticle. Pulls the player toward a layermasked hit
    /// point using PlayerMovement's grapple API, carries momentum on release, auto-cancels
    /// on arrival or re-pressing Attack, and retracts harmlessly on a miss.
    /// </summary>
    [RequireComponent(typeof(PlayerMovement))]
    [DisallowMultipleComponent]
    public class PlayerGrapple : NetworkBehaviour
    {
        private enum GrappleState { Idle, Shooting, Pulling, Retracting }

        [Header("References")]
        [Tooltip("Camera / aim source. If null, Camera.main is used.")]
        [SerializeField] private Transform cameraTransform;
        [SerializeField] private PlayerMovement playerMovement;
        [SerializeField] private PlayerInputController inputController;
        [SerializeField] private EquipmentManager equipmentManager;

        private GrappleState _state = GrappleState.Idle;
        private float _nextFireTime;
        private Vector3 _targetPoint;
        private Vector3 _shootOrigin;
        private float _shootTraveled;
        private Vector3 _currentVelocity;
        private Vector3 _retractVisualPoint;
        private bool _missedShot;

        private GrappleGunItem _activeGun;

        private void Awake()
        {
            if (playerMovement == null) playerMovement = GetComponent<PlayerMovement>();
            if (inputController == null) inputController = GetComponent<PlayerInputController>();
            if (equipmentManager == null) equipmentManager = GetComponent<EquipmentManager>();
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            enabled = IsOwner;
        }

        public override void OnGainedOwnership()
        {
            base.OnGainedOwnership();
            enabled = true;
        }

        public override void OnLostOwnership()
        {
            base.OnLostOwnership();
            CancelImmediate();
            enabled = false;
        }

        private void Update()
        {
            if (!IsOwner || inputController == null || equipmentManager == null || cameraTransform == null) return;

            var equippedGun = equipmentManager.EquippedItem as GrappleGunItem;

            // Equipped item changed/unequipped mid-grapple -> hard cancel.
            if (equippedGun != _activeGun)
            {
                CancelImmediate();
                _activeGun = equippedGun;
            }

            if (_activeGun == null)
            {
                return; // No grapple gun equipped: do nothing.
            }

            bool attackPressed = inputController.AttackPressedThisFrame;

            switch (_state)
            {
                case GrappleState.Idle:
                    if (attackPressed && Time.time >= _nextFireTime)
                    {
                        TryFire();
                    }
                    break;

                case GrappleState.Shooting:
                    if (attackPressed)
                    {
                        CancelImmediate();
                        break;
                    }
                    TickShooting(Time.deltaTime);
                    break;

                case GrappleState.Pulling:
                    if (attackPressed)
                    {
                        EndPull();
                        break;
                    }
                    TickPulling(Time.deltaTime);
                    break;

                case GrappleState.Retracting:
                    TickRetracting(Time.deltaTime);
                    break;
            }

            UpdateRopeVisual();
        }

        private void TryFire()
        {
            Vector3 origin = cameraTransform.position;
            Vector3 direction = cameraTransform.forward;

            _nextFireTime = Time.time + Mathf.Max(0.01f, _activeGun.FireRate);

            if (Physics.Raycast(origin, direction, out RaycastHit hit, _activeGun.MaxRange, _activeGun.GrappleLayerMask, QueryTriggerInteraction.Ignore))
            {
                _targetPoint = hit.point;
                _missedShot = false;
            }
            else
            {
                _targetPoint = origin + direction * _activeGun.MaxRange;
                _missedShot = true;
            }

            _shootOrigin = origin;
            _shootTraveled = 0f;
            _state = GrappleState.Shooting;

            if (_activeGun.RopeLine != null) _activeGun.RopeLine.enabled = true;
        }

        private void TickShooting(float dt)
        {
            _shootTraveled += _activeGun.ShootSpeed * dt;
            float fullDist = Vector3.Distance(_shootOrigin, _targetPoint);

            if (_shootTraveled >= fullDist)
            {
                if (_missedShot) StartRetract();
                else BeginPull();
            }
        }

        private void BeginPull()
        {
            _state = GrappleState.Pulling;
            _currentVelocity = Vector3.zero;

            Vector3 toTarget = _targetPoint - transform.position;
            Vector3 dir = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;
            playerMovement.BeginGrapple(dir * _activeGun.PullSpeed);
        }

        private void TickPulling(float dt)
        {
            Vector3 toTarget = _targetPoint - transform.position;
            float dist = toTarget.magnitude;

            if (dist <= _activeGun.ArrivalDistance)
            {
                EndPull();
                return;
            }

            Vector3 desiredVelocity = toTarget.normalized * _activeGun.PullSpeed;
            _currentVelocity = Vector3.MoveTowards(_currentVelocity, desiredVelocity, _activeGun.PullAcceleration * dt);
            playerMovement.SetGrappleVelocity(_currentVelocity);
        }

        private void EndPull()
        {
            playerMovement.EndGrapple(_currentVelocity);
            _state = GrappleState.Idle;
            if (_activeGun != null && _activeGun.RopeLine != null) _activeGun.RopeLine.enabled = false;
            _missedShot = false;
        }

        private void StartRetract()
        {
            _state = GrappleState.Retracting;
            _retractVisualPoint = _targetPoint;
        }

        private void TickRetracting(float dt)
        {
            Vector3 muzzlePos = _activeGun.Muzzle != null ? _activeGun.Muzzle.position : cameraTransform.position;
            _retractVisualPoint = Vector3.MoveTowards(_retractVisualPoint, muzzlePos, _activeGun.ShootSpeed * dt);

            if (Vector3.Distance(_retractVisualPoint, muzzlePos) < 0.1f)
            {
                _state = GrappleState.Idle;
                _missedShot = false;
                if (_activeGun.RopeLine != null) _activeGun.RopeLine.enabled = false;
            }
        }

        private void CancelImmediate()
        {
            if (_state == GrappleState.Pulling)
            {
                playerMovement.EndGrapple(_currentVelocity);
            }

            if (_activeGun != null && _activeGun.RopeLine != null)
            {
                _activeGun.RopeLine.enabled = false;
            }

            _state = GrappleState.Idle;
            _missedShot = false;
        }

        private void UpdateRopeVisual()
        {
            if (_activeGun == null || _activeGun.RopeLine == null) return;

            Vector3 start = _activeGun.Muzzle != null ? _activeGun.Muzzle.position : cameraTransform.position;
            Vector3 end;

            switch (_state)
            {
                case GrappleState.Shooting:
                    end = Vector3.MoveTowards(_shootOrigin, _targetPoint, _shootTraveled);
                    break;
                case GrappleState.Pulling:
                    end = _targetPoint;
                    break;
                case GrappleState.Retracting:
                    end = _retractVisualPoint;
                    break;
                default:
                    return;
            }

            var line = _activeGun.RopeLine;
            line.positionCount = 2;
            line.SetPosition(0, start);
            line.SetPosition(1, end);
        }
    }
}