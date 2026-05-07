using UnityEngine;

/// <summary>
/// Quadcopter-style suicide drone. Hovers in place when sticks are released. Translates in
/// three axes via stick input, yaws about world up, and visually pitches/rolls toward whatever
/// direction the player is commanding (just for the look — actual movement uses a yaw-only
/// level frame so tilt doesn't redirect velocity).
///
/// Stick map matches the standard "Mode 2" RC-quadcopter / DJI / sim convention:
///   Left Y  = throttle (up = ascend, down = descend)
///   Left X  = yaw (left = rotate CCW, right = rotate CW)
///   Right Y = pitch — forward/back travel (up = forward, down = back)
///   Right X = roll — left/right strafe (left = strafe left, right = strafe right)
///
/// Plumbing (input setup, bind, explode-on-impact, lifetime) lives in <see cref="FlyingProjectile"/>.
/// </summary>
public class Drone : FlyingProjectile
{
    [Header("Flight (m/s)")]
    [Tooltip("Top forward / reverse speed when the right stick Y is fully pushed.")]
    [SerializeField] private float maxForwardSpeed = 0.6f;
    [Tooltip("Top sideways speed when the right stick X is fully pushed.")]
    [SerializeField] private float maxStrafeSpeed = 0.4f;
    [Tooltip("Top vertical speed when the left stick Y is fully pushed.")]
    [SerializeField] private float maxVerticalSpeed = 0.5f;
    [Tooltip("Top yaw rate (degrees / second) when the left stick X is fully pushed.")]
    [SerializeField] private float maxYawDegPerSec = 60f;
    [Tooltip("How quickly the drone reaches its target velocity. Higher = snappier, less floaty.")]
    [SerializeField] private float linearAccel = 2f;
    [Tooltip("How quickly the drone reaches its target yaw rate.")]
    [SerializeField] private float yawAccel = 240f;

    [Header("Visual tilt")]
    [Tooltip("Maximum pitch / roll the body tilts at full stick deflection (degrees). Purely cosmetic — does not affect velocity.")]
    [SerializeField] private float maxTiltDegrees = 20f;
    [Tooltip("How quickly tilt eases toward the target angle. Higher = snappier, less floaty.")]
    [SerializeField] private float tiltSmoothing = 8f;

    private float _yawVelocity;
    private float _yaw;
    private float _pitch;
    private float _roll;

    protected override void Awake()
    {
        base.Awake();
        // Inherit launch yaw so the drone keeps facing wherever the cannon was pointing on
        // release. Pitch/roll start at zero — the drone "levels off" as it leaves the muzzle.
        Vector3 e = transform.eulerAngles;
        _yaw = e.y;
        _pitch = 0f;
        _roll = 0f;
    }

    protected override void UpdateFlight(Vector2 leftStick, Vector2 rightStick, float dt)
    {
        // Mode 2 mapping.
        float verticalInput = leftStick.y;
        float yawInput = leftStick.x;
        float forwardInput = rightStick.y;
        float strafeInput = rightStick.x;

        // Yaw — accumulated angle about world up. _yawVelocity ramps up to the target rate
        // so the body doesn't snap from rest to full-rate yaw.
        float targetYawRate = yawInput * maxYawDegPerSec;
        _yawVelocity = Mathf.MoveTowards(_yawVelocity, targetYawRate, yawAccel * dt);
        _yaw += _yawVelocity * dt;

        // Visual tilt — proportional to input, eased over time.
        // Sign convention (Unity left-handed): positive local X rot = nose-down, positive
        // local Z rot = right-wing-up. We want forward stick → nose dips forward (+pitch),
        // and right stick → right wing dips down (-roll).
        float targetPitch = forwardInput * maxTiltDegrees;
        float targetRoll = -strafeInput * maxTiltDegrees;
        float k = Mathf.Clamp01(tiltSmoothing * dt);
        _pitch = Mathf.Lerp(_pitch, targetPitch, k);
        _roll = Mathf.Lerp(_roll, targetRoll, k);

        transform.rotation = Quaternion.Euler(_pitch, _yaw, _roll);

        // Movement runs in the yaw-only level frame so the visual tilt doesn't redirect
        // velocity downward. A real quadcopter accelerates by tilting; we cheat for arcade
        // feel by treating tilt as cosmetic and driving velocity from input alone.
        Quaternion levelRot = Quaternion.Euler(0f, _yaw, 0f);
        Vector3 levelForward = levelRot * Vector3.forward;
        Vector3 levelRight = levelRot * Vector3.right;

        Vector3 targetVelocity = levelForward * forwardInput * maxForwardSpeed
                               + levelRight * strafeInput * maxStrafeSpeed
                               + Vector3.up * verticalInput * maxVerticalSpeed;

        if (_rb != null)
        {
            _rb.linearVelocity = Vector3.MoveTowards(_rb.linearVelocity, targetVelocity, linearAccel * dt);
        }
        else
        {
            transform.position += targetVelocity * dt;
        }
    }
}
