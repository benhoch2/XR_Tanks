using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Base class for player-flown suicide weapons (Drone, Plane). Handles the parts that don't
/// vary between flight models: stick input, suspending/restoring tank scripts on bind/explode,
/// collision-triggered detonation, kill-anything-it-hits, and the safety lifetime. Concrete
/// subclasses implement only the per-frame flight kinematics in <see cref="UpdateFlight"/>.
/// </summary>
public abstract class FlyingProjectile : MonoBehaviour
{
    [Header("Lifetime")]
    [Tooltip("Self-destructs after this many seconds even with no collision so a runaway weapon can't strand the player.")]
    [SerializeField] protected float maxLifetime = 30f;

    [Header("Explosion")]
    [Tooltip("Optional VFX prefab spawned at this object's position on detonation.")]
    [SerializeField] protected GameObject explosionPrefab;
    [Tooltip("How long the spawned VFX lingers before being destroyed.")]
    [SerializeField] protected float explosionLifetime = 2f;

    protected InputAction _moveAction;
    protected InputAction _aimAction;
    protected Rigidbody _rb;
    protected float _spawnTime;
    protected bool _exploded;

    // Tank scripts suspended at Bind time, captured so detonation can re-enable them.
    protected TowerController _suspendedTower;
    protected ShootingControls _suspendedShooting;
    protected TankFlip _suspendedFlip;

    protected virtual void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _spawnTime = Time.time;
    }

    protected virtual void OnEnable()
    {
        // Direct XR controller bindings, same approach TowerController and ShootingControls use.
        // No shared input asset to thread through.
        _moveAction = new InputAction("FlyingProjectileMove", InputActionType.Value, "<XRController>{LeftHand}/thumbstick");
        _aimAction = new InputAction("FlyingProjectileAim", InputActionType.Value, "<XRController>{RightHand}/thumbstick");
        _moveAction.Enable();
        _aimAction.Enable();
    }

    protected virtual void OnDisable()
    {
        _moveAction?.Disable();
        _aimAction?.Disable();
        _moveAction?.Dispose();
        _aimAction?.Dispose();
        _moveAction = null;
        _aimAction = null;
    }

    /// <summary>
    /// Suspends drive/aim/shoot/flip on the launcher tank and remembers them so detonation can
    /// re-enable. Also adds a Physics.IgnoreCollision pass against the tank's colliders so this
    /// projectile doesn't collide with its own launcher on release.
    /// </summary>
    public void Bind(GameObject playerTank)
    {
        if (playerTank == null) return;

        _suspendedTower = playerTank.GetComponent<TowerController>();
        _suspendedShooting = playerTank.GetComponent<ShootingControls>();
        _suspendedFlip = playerTank.GetComponent<TankFlip>();

        if (_suspendedTower != null) _suspendedTower.enabled = false;
        if (_suspendedShooting != null) _suspendedShooting.enabled = false;
        if (_suspendedFlip != null) _suspendedFlip.enabled = false;

        Collider self = GetComponent<Collider>();
        if (self != null)
        {
            foreach (var tankCol in playerTank.GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(self, tankCol);
        }
    }

    protected virtual void FixedUpdate()
    {
        if (_exploded) return;

        if (Time.time - _spawnTime > maxLifetime)
        {
            Explode();
            return;
        }

        Vector2 leftStick = _moveAction?.ReadValue<Vector2>() ?? Vector2.zero;
        Vector2 rightStick = _aimAction?.ReadValue<Vector2>() ?? Vector2.zero;

        UpdateFlight(leftStick, rightStick, Time.fixedDeltaTime);
    }

    /// <summary>
    /// Per-physics-step flight integration. Subclasses translate stick input into rotation
    /// and velocity changes appropriate to their flight model (hover quad vs. forever-forward plane).
    /// </summary>
    protected abstract void UpdateFlight(Vector2 leftStick, Vector2 rightStick, float dt);

    private void OnCollisionEnter(Collision collision)
    {
        if (_exploded) return;
        TryKill(collision.gameObject);
        Explode();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (_exploded) return;
        TryKill(other.gameObject);
        Explode();
    }

    private void TryKill(GameObject hit)
    {
        if (hit == null) return;
        Target target = hit.GetComponent<Target>();
        if (target == null) target = hit.GetComponentInParent<Target>();
        if (target == null) return;

        // Don't friendly-fire the launching tank if Physics.IgnoreCollision missed.
        if (_suspendedShooting != null && target.gameObject == _suspendedShooting.gameObject)
            return;

        target.Kill();
    }

    protected void Explode()
    {
        if (_exploded) return;
        _exploded = true;

        if (explosionPrefab != null)
        {
            GameObject fx = Instantiate(explosionPrefab, transform.position, Quaternion.identity);
            Destroy(fx, explosionLifetime);
        }

        RestoreTankControls();
        Destroy(gameObject);
    }

    protected virtual void OnDestroy()
    {
        // Defensive: scene reload, editor stop, exception during flight — re-enable tank scripts
        // unconditionally so the player is never stuck "in flight mode" without a flying weapon.
        RestoreTankControls();
    }

    private void RestoreTankControls()
    {
        if (_suspendedTower != null) _suspendedTower.enabled = true;
        if (_suspendedShooting != null) _suspendedShooting.enabled = true;
        if (_suspendedFlip != null) _suspendedFlip.enabled = true;
    }
}
