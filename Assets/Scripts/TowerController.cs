using UnityEngine;
using UnityEngine.InputSystem;

public class TowerController : MonoBehaviour
{
    [Header("Tower Rotation Settings")]
    [SerializeField] GameObject towerBase;
    public float rotationSpeed = 100f;
    public float tiltSpeed = 50f;
    public float minTilt = -10f;
    public float maxTilt = 30f;

    [Header("Tank Movement Settings")]
    public float moveSpeed = 5f;

    [Tooltip("If the tank's up axis deviates from world up by more than this many degrees, " +
             "drive input is ignored — the tank is on its side or back and can't drive. " +
             "Turret rotation/tilt is unaffected so the player can still look around while " +
             "they hold B to flip back upright.")]
    [SerializeField] private float maxDriveTiltDegrees = 60f;

    [Header("Knockback")]
    [Tooltip("Effective mass for hit-impact knockback. Higher = less slide from the same hit.")]
    [SerializeField] private float mass = 1f;

    [Tooltip("How quickly knockback velocity decays back to zero (per second). Higher = stops sooner.")]
    [SerializeField] private float knockbackDamping = 6f;

    private InputAction moveAction;
    private InputAction aimAction;
    private float currentTilt = 0f;
    private Vector3 _knockbackVelocity;

    void OnEnable()
    {
        moveAction = new InputAction("Move", InputActionType.Value, "<XRController>{LeftHand}/thumbstick");
        aimAction = new InputAction("Aim", InputActionType.Value, "<XRController>{RightHand}/thumbstick");
        moveAction.Enable();
        aimAction.Enable();
    }

    void OnDisable()
    {
        moveAction?.Disable();
        aimAction?.Disable();
        moveAction?.Dispose();
        aimAction?.Dispose();
        moveAction = null;
        aimAction = null;
    }

    void Update()
    {
        Vector2 leftStick = moveAction?.ReadValue<Vector2>() ?? Vector2.zero;
        Vector2 rightStick = aimAction?.ReadValue<Vector2>() ?? Vector2.zero;

        float moveInput = leftStick.y;
        float turnInput = leftStick.x;
        float rotationInput = rightStick.x;
        float tiltInput = rightStick.y;

        MoveTank(moveInput, turnInput);
        RotateTower(rotationInput);
        TiltTower(tiltInput);

        ApplyKnockbackStep();
    }

    /// <summary>
    /// Adds a knockback impulse (world-space). Horizontal-only: the player tank slides on
    /// the floor instead of launching upward. Mass divides the impulse so a heavier player
    /// would slide less from the same hit — currently only relevant if the field is tuned.
    /// </summary>
    public void ApplyKnockback(Vector3 worldImpulse)
    {
        Vector3 horizImpulse = new Vector3(worldImpulse.x, 0f, worldImpulse.z);
        if (horizImpulse.sqrMagnitude < 0.0001f)
            return;

        float effectiveMass = Mathf.Max(0.001f, mass);
        _knockbackVelocity += horizImpulse / effectiveMass;
    }

    private void ApplyKnockbackStep()
    {
        if (_knockbackVelocity.sqrMagnitude < 0.0001f)
        {
            _knockbackVelocity = Vector3.zero;
            return;
        }

        transform.position += _knockbackVelocity * Time.deltaTime;
        _knockbackVelocity = Vector3.Lerp(
            _knockbackVelocity,
            Vector3.zero,
            Mathf.Clamp01(knockbackDamping * Time.deltaTime));
    }

    void MoveTank(float moveInput, float turnInput)
    {
        if (IsTippedOver())
            return;

        if (Mathf.Abs(turnInput) > 0.01f)
            transform.Rotate(Vector3.up, turnInput * rotationSpeed * Time.deltaTime);
        if (Mathf.Abs(moveInput) > 0.01f)
            transform.position += transform.forward * moveInput * moveSpeed * Time.deltaTime;
    }

    private bool IsTippedOver()
    {
        return Vector3.Angle(transform.up, Vector3.up) > maxDriveTiltDegrees;
    }

    void RotateTower(float input)
    {
        if (Mathf.Abs(input) > 0.01f)
        {
            // Rotate around the parent's (tank's) local Y so the turret stays level
            // with the tank body if the tank pitches or rolls (driving over an obstacle,
            // mid-flip, etc.).
            Vector3 localEuler = towerBase.transform.localEulerAngles;
            localEuler.y += input * rotationSpeed * Time.deltaTime;
            towerBase.transform.localEulerAngles = localEuler;
        }
    }

    void TiltTower(float input)
    {
        if (Mathf.Abs(input) > 0.01f)
        {
            currentTilt = Mathf.Clamp(currentTilt + input * tiltSpeed * Time.deltaTime, minTilt, maxTilt);
            Vector3 localEuler = towerBase.transform.localEulerAngles;
            localEuler.x = currentTilt;
            towerBase.transform.localEulerAngles = localEuler;
        }
    }
}
