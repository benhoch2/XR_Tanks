using UnityEngine;

/// <summary>
/// Plane-style suicide drone. Always moves forward — sticks centered = keep flying straight at
/// cruise. Cannot hover, cannot reverse. Plumbing (input setup, bind, explode-on-impact,
/// lifetime) lives in <see cref="FlyingProjectile"/>.
///
/// Stick map (real-aircraft convention — pull back to climb):
///   Left Y  = throttle (centered = cruise, up = boost to maxSpeed, down = slow to minSpeed)
///   Left X  = rudder yaw (world up)
///   Right Y = pitch (down on stick / pull back = climb, up on stick / push forward = dive)
///   Right X = roll (visual banking)
/// </summary>
public class PlaneDrone : FlyingProjectile
{
    [Header("Speed (m/s)")]
    [Tooltip("Slowest the plane can fly — left stick Y all the way down. Stays positive so the plane never hovers.")]
    [SerializeField] private float minSpeed = 0.4f;
    [Tooltip("Fastest the plane can fly — left stick Y all the way up.")]
    [SerializeField] private float maxSpeed = 1.0f;
    [Tooltip("How quickly throttle changes affect actual speed.")]
    [SerializeField] private float speedAccel = 1.0f;

    [Header("Rotation rates (deg / sec)")]
    [Tooltip("How fast the plane pitches (climbs/dives) when the right stick Y is fully pushed.")]
    [SerializeField] private float pitchDegPerSec = 60f;
    [Tooltip("How fast the plane rolls (banks left/right) when the right stick X is fully pushed.")]
    [SerializeField] private float rollDegPerSec = 90f;
    [Tooltip("Rudder yaw rate when the left stick X is fully pushed.")]
    [SerializeField] private float yawDegPerSec = 30f;
    [Tooltip("Bank-induced yaw: how aggressively a banked plane turns into the bank, in deg/sec per degree of roll. 0 disables for a pure-rudder feel; ~0.5 gives natural banked turns.")]
    [SerializeField] private float bankYawCoupling = 0.5f;

    private float _currentSpeed;

    protected override void Awake()
    {
        base.Awake();
        // Start at cruise so the plane visibly accelerates out of the cannon rather than crawling.
        _currentSpeed = (minSpeed + maxSpeed) * 0.5f;
    }

    protected override void UpdateFlight(Vector2 leftStick, Vector2 rightStick, float dt)
    {
        float throttleInput = leftStick.y;
        float rudderInput = leftStick.x;
        float pitchInput = rightStick.y;
        float rollInput = rightStick.x;

        // Throttle — stick Y in [-1,1] maps to [minSpeed, maxSpeed]. Centered = cruise.
        float throttle01 = (throttleInput + 1f) * 0.5f;
        float targetSpeed = Mathf.Lerp(minSpeed, maxSpeed, throttle01);
        _currentSpeed = Mathf.MoveTowards(_currentSpeed, targetSpeed, speedAccel * dt);

        // Pitch — rotate about local +X. Real planes pull back on the stick (negative thumbstick
        // Y) to climb, so positive stick Y (push forward) maps to nose-down (positive local X
        // rotation in Unity). No sign flip needed — both conventions match here.
        if (Mathf.Abs(pitchInput) > 0.001f)
            transform.Rotate(Vector3.right, pitchInput * pitchDegPerSec * dt, Space.Self);

        // Roll — rotate about local +Z. Negate so stick-right = bank right (right wing dips).
        if (Mathf.Abs(rollInput) > 0.001f)
            transform.Rotate(Vector3.forward, -rollInput * rollDegPerSec * dt, Space.Self);

        // Yaw — rudder (explicit) plus bank-induced yaw (current roll angle, signed) so a
        // banked plane turns into its bank without the player having to also push rudder.
        float currentRollDeg = SignedRollDegrees();
        float bankYaw = -currentRollDeg * bankYawCoupling;  // bank-right (negative roll) => +yaw
        float yawRate = rudderInput * yawDegPerSec + bankYaw;
        if (Mathf.Abs(yawRate) > 0.001f)
            transform.Rotate(Vector3.up, yawRate * dt, Space.World);

        // Velocity — always forward at the current speed. No drift, no hover.
        Vector3 forwardVel = transform.forward * _currentSpeed;
        if (_rb != null)
        {
            _rb.linearVelocity = forwardVel;
        }
        else
        {
            transform.position += forwardVel * dt;
        }
    }

    /// <summary>
    /// Roll angle of the plane around its forward axis, signed [-180,180]. Positive = banked
    /// left (right wing up); negative = banked right (right wing down). Used to drive the
    /// banked-turn yaw coupling.
    /// </summary>
    private float SignedRollDegrees()
    {
        // Project the plane's local right axis onto the world horizontal plane to find how
        // much it has rolled. atan2 of (right.y, projected length on the horizon) is more
        // stable across orientations than reading eulerAngles.z directly.
        Vector3 right = transform.right;
        Vector3 horizRight = right;
        horizRight.y = 0f;
        float horiz = horizRight.magnitude;
        return Mathf.Atan2(right.y, horiz) * Mathf.Rad2Deg;
    }
}
