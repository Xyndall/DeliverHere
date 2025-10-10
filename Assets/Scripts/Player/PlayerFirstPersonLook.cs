using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

[DisallowMultipleComponent]
public class PlayerFirstPersonLook : NetworkBehaviour
{
    [Header("References")]
    [Tooltip("Camera used for the local player. If null, will try to find a child Camera or Camera.main.")]
    [SerializeField] public CinemachineCamera playerCamera;
    [Tooltip("Optional pivot for vertical rotation (pitch). If null, will use the camera transform.")]
    [SerializeField] private Transform pitchPivot;

    [Header("Look")]
    [SerializeField] private float mouseSensitivity = 0.12f;   // deg per pixel
    [SerializeField] private float stickSensitivity = 180f;    // deg per second
    [SerializeField] private float minPitch = -89f;
    [SerializeField] private float maxPitch = 89f;
    [SerializeField] private bool invertY = false;
    [SerializeField] private bool lockCursor = true;

    [Header("Camera Effects")]
    [SerializeField] private bool enableHeadBob = true;
    [SerializeField] private float walkBobAmplitude = 0.03f;      // meters
    [SerializeField] private float walkBobFrequency = 10f;        // Hz-like
    [SerializeField] private float runBobAmplitude = 0.06f;       // meters
    [SerializeField] private float runBobFrequency = 14f;         // Hz-like
    [SerializeField] private float bobReturnLerp = 12f;           // how quickly camera returns to rest

    [SerializeField] private bool enableSprintFovKick = true;
    [SerializeField] private float sprintFovAdd = 10f;            // added onto base FOV when sprinting
    [SerializeField] private float fovLerpSpeed = 8f;             // FOV change smoothing

    [Header("Camera Sway")]
    [SerializeField] private bool enableCameraSway = true;
    [Tooltip("How much look delta contributes to yaw/pitch sway (deg sway per deg look).")]
    [SerializeField] private float lookSwayAmount = 0.02f;
    [Tooltip("Clamp for yaw/pitch sway in degrees.")]
    [SerializeField] private float lookSwayMax = 2f;
    [Tooltip("Additional roll from look delta (deg per deg look).")]
    [SerializeField] private float lookRollAmount = 0.04f;
    [Tooltip("Max roll from movement (degrees).")]
    [SerializeField] private float moveRollAmount = 3f;
    [Tooltip("Lateral speed (m/s) at which movement roll reaches max.")]
    [SerializeField] private float rollMaxSpeed = 8f;
    [Tooltip("Overall smoothing for sway response and return.")]
    [SerializeField] private float swayLerp = 14f;

    private float _yaw;
    private float _pitch;

    // Input System
    private InputSystem_Actions _input;
    private InputAction _lookAction;

    // Effects state
    private Vector3 _camDefaultLocalPos;
    private float _bobPhase;
    private float _baseFov;

    // Sway state
    private Vector3 _swayEuler; // smoothed current yaw/pitch/roll offsets
    private float _lastDx;      // look delta (deg) used for sway
    private float _lastDy;

    // External refs for effect state
    private CharacterController _cc;
    private PlayerMovement _movement;

    public  void CameraIsAssigned()
    {
        base.OnNetworkSpawn();

        // Only the owning client should control and render its camera
        enabled = IsOwner;

        if (IsOwner)
        {
            InitInputIfNeeded();
            EnableInput();

            // Initialize from current transforms
            _yaw = transform.eulerAngles.y;
            _pitch = NormalizePitch((pitchPivot != null ? pitchPivot.localEulerAngles.x : 0f));

            CacheExternalRefsAndDefaults();

            SetCursorLocked(lockCursor);
        }
        else
        {
            // Ensure non-owners do not render or listen
            if (playerCamera != null) playerCamera.enabled = false;
            var listener = playerCamera != null ? playerCamera.GetComponent<AudioListener>() : null;
            if (listener != null) listener.enabled = false;
        }
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        enabled = true;
        InitInputIfNeeded();
        EnableInput();
        CacheExternalRefsAndDefaults();
        SetCursorLocked(lockCursor);
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        DisableInput();
        enabled = false;
        SetCursorLocked(false);
    }

    private void OnEnable()
    {
        if (IsOwner)
        {
            InitInputIfNeeded();
            EnableInput();
            CacheExternalRefsAndDefaults();
        }
    }

    private void OnDisable()
    {
        if (IsOwner)
        {
            DisableInput();
            SetCursorLocked(false);
        }
    }

    public override void OnDestroy()
    {
        try
        {
            _input?.Dispose();
        }
        finally
        {
            base.OnDestroy();
        }
    }

    private void Update()
    {
        if (!IsOwner || playerCamera == null) return;

        Vector2 look = (_lookAction != null ? _lookAction.ReadValue<Vector2>() : Vector2.zero);

        float dx = 0f, dy = 0f;
        InputDevice device = _lookAction?.activeControl != null ? _lookAction.activeControl.device : null;
        bool isMouse = device is Mouse;

        if (isMouse)
        {
            dx = look.x * mouseSensitivity;
            dy = look.y * mouseSensitivity * (invertY ? 1f : -1f);
        }
        else
        {
            dx = look.x * stickSensitivity * Time.unscaledDeltaTime;
            dy = look.y * stickSensitivity * Time.unscaledDeltaTime * (invertY ? 1f : -1f);
        }

        _yaw += dx;
        _pitch = Mathf.Clamp(_pitch + dy, minPitch, maxPitch);

        // Apply rotations: yaw on player body, pitch on camera pivot
        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        if (pitchPivot != null)
            pitchPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        else
            playerCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

        // Store deltas for sway
        _lastDx = dx;
        _lastDy = dy;

        // Camera effects after orientation
        ApplyCameraEffects();
        ApplyCameraSway();
    }

    private void ApplyCameraEffects()
    {
        if (playerCamera == null) return;

        // Determine motion state
        bool grounded = _cc != null && _cc.isGrounded;
        float planarSpeed = 0f;
        if (_cc != null)
        {
            Vector3 v = _cc.velocity;
            v.y = 0f;
            planarSpeed = v.magnitude;
        }
        bool isMoving = planarSpeed > 0.05f;
        bool isSprinting = _movement != null && _movement.IsSprinting;

        // Head bob
        if (enableHeadBob && grounded && isMoving)
        {
            float freq = isSprinting ? runBobFrequency : walkBobFrequency;
            float amp  = isSprinting ? runBobAmplitude  : walkBobAmplitude;

            // Advance phase in radians
            _bobPhase += (freq * 2f * Mathf.PI) * Time.deltaTime;
            if (_bobPhase > Mathf.PI * 2f) _bobPhase -= Mathf.PI * 2f;

            float offsetY = Mathf.Sin(_bobPhase) * amp;
            float offsetX = Mathf.Cos(_bobPhase * 0.5f) * (amp * 0.5f); // subtle lateral sway

            Vector3 target = _camDefaultLocalPos + new Vector3(offsetX, offsetY, 0f);
            playerCamera.transform.localPosition = Vector3.Lerp(
                playerCamera.transform.localPosition,
                target,
                1f - Mathf.Exp(-bobReturnLerp * Time.deltaTime)
            );
        }
        else
        {
            // Return to rest
            _bobPhase = 0f;
            playerCamera.transform.localPosition = Vector3.Lerp(
                playerCamera.transform.localPosition,
                _camDefaultLocalPos,
                1f - Mathf.Exp(-bobReturnLerp * Time.deltaTime)
            );
        }

        // FOV kick (base + N while sprinting)
        if (enableSprintFovKick)
        {
            var lens = playerCamera.Lens;
            float targetFov = _baseFov + (isSprinting ? sprintFovAdd : 0f);
            float newFov = Mathf.Lerp(lens.FieldOfView, targetFov, 1f - Mathf.Exp(-fovLerpSpeed * Time.deltaTime));
            lens.FieldOfView = newFov;
            playerCamera.Lens = lens;
        }
    }

    private void ApplyCameraSway()
    {
        if (playerCamera == null)
            return;

        float dt = Time.deltaTime;

        if (enableCameraSway)
        {
            // Look-based sway: slight inertial counter motion
            float yawSway   = Mathf.Clamp(-_lastDx * lookSwayAmount, -lookSwayMax, lookSwayMax);
            float pitchSway = Mathf.Clamp( _lastDy * lookSwayAmount, -lookSwayMax, lookSwayMax);
            float rollFromLook = Mathf.Clamp(-_lastDx * lookRollAmount, -lookSwayMax, lookSwayMax);

            // Movement-based roll from lateral velocity
            float rollFromMove = 0f;
            if (_cc != null)
            {
                Vector3 vel = _cc.velocity;
                float lateral = Vector3.Dot(vel, transform.right); // +right, -left
                float t = Mathf.Clamp(lateral / Mathf.Max(0.01f, rollMaxSpeed), -1f, 1f);
                rollFromMove = -t * moveRollAmount; // lean into the turn
            }

            float targetRoll = Mathf.Clamp(rollFromLook + rollFromMove, -(moveRollAmount + lookSwayMax), (moveRollAmount + lookSwayMax));

            Vector3 targetEuler = new Vector3(pitchSway, yawSway, targetRoll);
            _swayEuler = Vector3.Lerp(_swayEuler, targetEuler, 1f - Mathf.Exp(-swayLerp * dt));

            Quaternion swayRot = Quaternion.Euler(_swayEuler);

            // Apply on top of base pitch
            if (pitchPivot != null)
            {
                playerCamera.transform.localRotation = swayRot;
            }
            else
            {
                playerCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f) * swayRot;
            }
        }
        else
        {
            // Smoothly return to neutral when disabled
            _swayEuler = Vector3.Lerp(_swayEuler, Vector3.zero, 1f - Mathf.Exp(-swayLerp * dt));
            Quaternion swayRot = Quaternion.Euler(_swayEuler);

            if (pitchPivot != null)
                playerCamera.transform.localRotation = swayRot; // will go to identity
            else
                playerCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f) * swayRot;
        }
    }

    private void CacheExternalRefsAndDefaults()
    {
        _movement = _movement ?? GetComponentInParent<PlayerMovement>();
        _cc = _cc ?? GetComponentInParent<CharacterController>();

        if (playerCamera != null)
        {
            _camDefaultLocalPos = playerCamera.transform.localPosition;
            var lens = playerCamera.Lens;
            _baseFov = lens.FieldOfView;
        }
    }

    private void InitInputIfNeeded()
    {
        if (_input != null) return;

        _input = new InputSystem_Actions();
        // Ensure your Input Actions asset has: Map = "Player", Action = "Look" (Vector2)
        _lookAction = _input.Player.Look;
    }

    private void EnableInput()
    {
        _input?.Enable();
        _lookAction?.Enable();
    }

    private void DisableInput()
    {
        _lookAction?.Disable();
        _input?.Disable();
    }

    private static float NormalizePitch(float eulerX)
    {
        float x = eulerX;
        if (x > 180f) x -= 360f;
        return x;
    }

    private static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        mouseSensitivity = Mathf.Max(0.001f, mouseSensitivity);
        stickSensitivity = Mathf.Max(1f, stickSensitivity);
        minPitch = Mathf.Clamp(minPitch, -89f, 89f);
        maxPitch = Mathf.Clamp(maxPitch, -89f, 89f);
        if (maxPitch < minPitch) maxPitch = minPitch;

        // Effects validation
        walkBobAmplitude = Mathf.Max(0f, walkBobAmplitude);
        runBobAmplitude  = Mathf.Max(0f, runBobAmplitude);
        walkBobFrequency = Mathf.Max(0f, walkBobFrequency);
        runBobFrequency  = Mathf.Max(0f, runBobFrequency);
        bobReturnLerp    = Mathf.Max(0.01f, bobReturnLerp);

        sprintFovAdd     = Mathf.Max(0f, sprintFovAdd);
        fovLerpSpeed     = Mathf.Max(0.01f, fovLerpSpeed);

        // Sway validation
        lookSwayAmount   = Mathf.Max(0f, lookSwayAmount);
        lookSwayMax      = Mathf.Max(0f, lookSwayMax);
        lookRollAmount   = Mathf.Max(0f, lookRollAmount);
        moveRollAmount   = Mathf.Max(0f, moveRollAmount);
        rollMaxSpeed     = Mathf.Max(0.01f, rollMaxSpeed);
        swayLerp         = Mathf.Max(0.01f, swayLerp);
    }
#endif
}