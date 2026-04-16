using UnityEngine;

public enum ProjectileType
{
    Standard,   // Gray: damages enemies on hit, 5s fuse after first non-enemy collision then explodes
    Explosive,  // Blue: explodes on contact with anything
    Teleport    // Red: teleports player tank to impact point
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

    [HideInInspector] public Transform shooter;

    [Tooltip("Explosive shots ignore floor-like collisions only when they happen clearly above the gameplay floor.")]
    public float elevatedFloorIgnoreThreshold = 0.05f;

    private bool timerStarted = false;

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
            GameObject gameplayFloor = GameObject.Find("Floor");
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


        // If we hit a Target, Target.cs handles damage and destroys us.
        // For Explosive type, also spawn our own explosion effect on the Target hit.
        // For Teleport type, also teleport the shooter.
        Target target = collision.gameObject.GetComponent<Target>();
        if (target != null)
        {
            if (projectileType == ProjectileType.Explosive)
                SpawnExplosionEffect(contactPoint);
            if (projectileType == ProjectileType.Teleport)
                TeleportShooter(collision);
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
                // Teleport player to impact point then destroy
                TeleportShooter(collision);
                Destroy(gameObject);
                break;
        }
    }

    private void TeleportShooter(Collision collision)
    {
        if (shooter == null) return;

        // Use contact point if available, offset slightly above ground so tank doesn't clip
        Vector3 hitPoint = collision.contactCount > 0
            ? collision.GetContact(0).point
            : transform.position;
        hitPoint.y += 0.05f;

        SpawnExplosionEffect();
        shooter.position = hitPoint;
    }

    private void Explode()
    {
        SpawnExplosionEffect(transform.position);
        Destroy(gameObject);
    }

    private void Explode(Vector3 position)
    {
        SpawnExplosionEffect(position);
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
