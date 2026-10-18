using UnityEngine;

public enum ProjectileType
{
    Standard,   // Gray: damages enemies on hit, 5s fuse after first non-enemy collision then explodes
    Explosive,  // Blue: explodes on contact with anything
    Teleport    // Red: on first contact, starts a delay timer; at expiry, teleports the shooter to wherever the ball is now
}

public class Projectile : MonoBehaviour
{
    [Tooltip("Damage dealt on hit. Set to 0 or less for instant kill.")]
    public int damage = 25;

    public ProjectileType projectileType = ProjectileType.Standard;

    [Tooltip("Effect spawned when projectile explodes on its own (timer or surface hit).")]
    public GameObject explosionEffectPrefab;

    [Tooltip("How long the explosion effect lasts.")]
    public float explosionEffectDuration = 2f;

    [Tooltip("Standard type only: seconds before self-destruct after first non-enemy collision.")]
    public float selfDestructTimer = 5f;

    [Tooltip("Teleport type only: seconds between first contact and the deferred teleport. The ball stays alive (and bouncy) during this window.")]
    public float teleportDelay = 5f;

    [HideInInspector] public Transform shooter;

    // Fired exactly once on the projectile's first "real" collision (after the elevated-floor-ignore filter).
    // Used by EnemyTankAI to observe where its shots land so it can correct aim between shots.
    [System.NonSerialized] public System.Action<Vector3> onImpact;

    /// <summary>
    /// Raised when a projectile explodes on its own (fuse or surface hit) — not on direct
    /// Target hits, which Target handles. The turn-based match digs craters and applies
    /// blast damage from it; real-time play has no subscriber.
    /// </summary>
    public static event System.Action<Projectile, Vector3> Exploded;

    /// <summary>
    /// Horizontal acceleration (m/s²) applied to every live projectile. Zero in real-time
    /// play; the turn-based match rolls a new wind each turn and resets it when it ends.
    /// </summary>
    public static Vector3 Wind;

    /// <summary>True once the projectile has had its first real collision.</summary>
    public bool HasImpacted { get; private set; }

    [Tooltip("Explosive shots ignore floor-like collisions only when they happen clearly above the gameplay floor.")]
    public float elevatedFloorIgnoreThreshold = 0.05f;

    [Tooltip("Safety net: destroyed this many seconds after spawning even if nothing resolved it, " +
             "e.g. a shot that flew off the edge of the VR map. 0 = never.")]
    public float maxLifetime = 30f;

    [Header("Physics")]
    [Tooltip("0 = no bounce, 1 = perfectly elastic. Applied as a runtime PhysicsMaterial on this projectile's collider when it spawns.")]
    [Range(0f, 1f)]
    [SerializeField] private float bounciness = 0f;

    [Tooltip("Friction applied alongside bounciness. Lower values let bouncy balls keep rolling instead of stalling on first contact.")]
    [Range(0f, 1f)]
    [SerializeField] private float friction = 0.2f;

    private bool timerStarted = false;
    private bool teleportTimerStarted = false;
    private Rigidbody _body;

    private void Awake()
    {
        _body = GetComponent<Rigidbody>();

        if (bounciness <= 0f)
            return;

        Collider col = GetComponent<Collider>();
        if (col == null)
            return;

        var mat = new PhysicsMaterial("ProjectileBounce")
        {
            bounciness = bounciness,
            bounceCombine = PhysicsMaterialCombine.Maximum,
            dynamicFriction = friction,
            staticFriction = friction,
            frictionCombine = PhysicsMaterialCombine.Average,
        };
        col.sharedMaterial = mat;
    }

    private void Start()
    {
        if (maxLifetime > 0f)
            Destroy(gameObject, maxLifetime);
    }

    private void FixedUpdate()
    {
        // Kinematic copies (the projectile preview on the player tank) don't drift.
        if (Wind != Vector3.zero && _body != null && !_body.isKinematic)
            _body.AddForce(Wind, ForceMode.Acceleration);
    }

    private void OnCollisionEnter(Collision collision)
    {
        Vector3 contactPoint = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
        string hitName = collision.gameObject.name;

        // Ignore only floor-like collisions that happen significantly above the gameplay floor.
        bool isFloorLikeHit =
            hitName == "FLOOR" ||
            hitName == "Floor" ||
            hitName.Contains("FLOOR_EffectMesh") ||
            hitName.Contains("GLOBAL_MESH");

        if (projectileType == ProjectileType.Explosive && isFloorLikeHit)
        {
            GameObject gameplayFloor = GameConfigManager.Instance != null
                ? GameConfigManager.Instance.GetGameplayFloor()
                : null;
            if (gameplayFloor != null)
            {
                float floorY = gameplayFloor.transform.position.y;
                float deltaY = contactPoint.y - floorY;

                if (deltaY > elevatedFloorIgnoreThreshold)
                {
                    Collider projectileCollider = GetComponent<Collider>();
                    if (projectileCollider != null)
                        Physics.IgnoreCollision(projectileCollider, collision.collider);

                    return;
                }
            }
        }

        HasImpacted = true;

        // Fire the one-shot impact callback before type-specific handling can destroy us.
        if (onImpact != null)
        {
            var callback = onImpact;
            onImpact = null;
            callback.Invoke(contactPoint);
        }

        // If we hit a Target, Target.cs handles damage. Teleport balls survive the hit
        // and keep bouncing — Target now skips destroying them so their delayed-teleport
        // timer can run to completion.
        Target target = collision.gameObject.GetComponent<Target>();
        if (target != null)
        {
            if (projectileType == ProjectileType.Explosive)
                SpawnExplosionEffect(contactPoint);
            if (projectileType == ProjectileType.Teleport)
                StartTeleportTimerIfNeeded();
            return;
        }

        // Non-Target collision (ground, walls, etc.)
        switch (projectileType)
        {
            case ProjectileType.Standard:
                // First non-enemy hit starts the fuse — keep rolling, explode after timer
                if (!timerStarted)
                {
                    timerStarted = true;
                    Invoke(nameof(Explode), selfDestructTimer);
                }
                break;

            case ProjectileType.Explosive:
                // Explode immediately on any surface, at the actual contact point
                Explode(contactPoint);
                break;

            case ProjectileType.Teleport:
                // Defer the teleport: ball stays alive and bouncy until the timer fires.
                StartTeleportTimerIfNeeded();
                break;
        }
    }

    private void StartTeleportTimerIfNeeded()
    {
        if (teleportTimerStarted) return;
        teleportTimerStarted = true;
        Invoke(nameof(ResolveTeleport), teleportDelay);
    }

    private void ResolveTeleport()
    {
        if (shooter != null)
        {
            Vector3 destination = transform.position;
            destination.y += 0.05f;
            // Online, a human tank's transform belongs to its owner's headset.
            if (shooter.TryGetComponent(out NetworkTankControl network) && network.IsSpawned && !network.IsOwner)
                network.Teleport(destination);
            else
                shooter.position = destination;
        }
        SpawnExplosionEffect(transform.position);
        Destroy(gameObject);
    }

    /// <summary>Online clients: the explosion the host showed for this projectile.</summary>
    public void PlayExplosionEffect(Vector3 position)
    {
        SpawnExplosionEffect(position);
    }

    private void Explode()
    {
        Explode(transform.position);
    }

    private void Explode(Vector3 position)
    {
        SpawnExplosionEffect(position);
        Exploded?.Invoke(this, position);
        Destroy(gameObject);
    }

    private void SpawnExplosionEffect()
    {
        SpawnExplosionEffect(transform.position);
    }

    private void SpawnExplosionEffect(Vector3 position)
    {
        if (explosionEffectPrefab != null)
        {
            GameObject effect = Instantiate(explosionEffectPrefab, position, Quaternion.identity);
            Destroy(effect, explosionEffectDuration);
        }
    }
}
