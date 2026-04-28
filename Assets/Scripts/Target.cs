using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class Target : MonoBehaviour
{
    [Tooltip("Prefabs to spawn when hit (one will be picked at random)")]
    public GameObject[] hitEffectPrefabs;
	public float effectDuration = 1f;

    [Header("Health")]
    [Tooltip("Max hit points. 0 = instant kill (e.g. crates).")]
    public int maxHitPoints = 0;
    private int currentHitPoints;

    [Tooltip("Optional health bar (PowerBar prefab). Shown only for targets with maxHitPoints > 0.")]
    [SerializeField] private PowerBar healthBar;

    [Header("Hit Feedback")]
    [Tooltip("Optional explosion prefab spawned at the impact point on every hit (lethal or not). Leave null to skip.")]
    [SerializeField] private GameObject hitExplosionPrefab;
    [Tooltip("How long the hit explosion lingers before being destroyed.")]
    [SerializeField] private float hitExplosionDuration = 1.5f;
    [Tooltip("Knockback displacement (meters) applied to this target on every hit. Primarily matters for NavMeshAgent-driven enemies whose physics is overridden by the agent.")]
    [SerializeField] private float knockbackDistance = 0.15f;

    private NavMeshAgent _cachedAgent;
    private bool _agentLookedUp;

    private void Awake()
    {
        currentHitPoints = maxHitPoints;
        UpdateHealthBar();
    }

    private void HandleHit(GameObject hitObject, Vector3 hitPoint, Vector3 hitDirection)
    {
        Projectile proj = hitObject.GetComponent<Projectile>();
        if (proj != null)
        {
            int damage = proj.damage;

            // Teleport balls own their own lifetime (deferred-teleport timer) and must survive
            // enemy bounces. Every other type gets destroyed on contact.
            if (proj.projectileType != ProjectileType.Teleport)
                Destroy(hitObject);

            // Shared per-hit feedback (explosion + knockback) runs regardless of lethality.
            SpawnHitExplosion(hitPoint);
            ApplyKnockback(hitDirection);

            // Player-invulnerability test toggle: show feedback but skip damage entirely.
            // Detection: ShootingControls only lives on the player tank.
            if (GameConfigManager.Instance != null
                && GameConfigManager.Instance.playerInvulnerable
                && GetComponent<ShootingControls>() != null)
            {
                OnNonLethalHit(hitPoint);
                return;
            }

            // Instant kill path (crates or projectile with 0 damage)
            if (maxHitPoints <= 0 || damage <= 0)
            {
                SpawnEffect(effectDuration);
                Destroy(gameObject);
                return;
            }

            // Health-based path (enemies)
            currentHitPoints -= damage;
            Debug.Log($"{gameObject.name} hit for {damage} damage. HP: {currentHitPoints}/{maxHitPoints}");
            UpdateHealthBar();

            if (currentHitPoints <= 0)
            {
                SpawnEffect(effectDuration);
                Destroy(gameObject);
            }
            else
            {
                // Non-lethal hit: spawn effect at collision point
                OnNonLethalHit(hitPoint);
            }
        }
    }

    private void SpawnHitExplosion(Vector3 position)
    {
        if (hitExplosionPrefab == null) return;
        GameObject effect = Instantiate(hitExplosionPrefab, position, Quaternion.identity);
        Destroy(effect, hitExplosionDuration);
    }

    private void ApplyKnockback(Vector3 worldDirection)
    {
        if (knockbackDistance <= 0f) return;

        worldDirection.y = 0f;
        if (worldDirection.sqrMagnitude < 0.0001f) return;

        if (!_agentLookedUp)
        {
            _cachedAgent = GetComponent<NavMeshAgent>();
            _agentLookedUp = true;
        }

        // NavMeshAgent overrides transform each frame, so physics impulses on the Rigidbody
        // would be lost. Apply the knockback as a direct agent displacement instead.
        if (_cachedAgent != null && _cachedAgent.enabled && _cachedAgent.isOnNavMesh)
        {
            _cachedAgent.Move(worldDirection.normalized * knockbackDistance);
        }
        // For non-agent targets (player), the existing rigidbody collision impulse already handles knockback.
    }

    private void SpawnEffect(float duration)
    {
        if (hitEffectPrefabs != null && hitEffectPrefabs.Length > 0)
        {
            int idx = Random.Range(0, hitEffectPrefabs.Length);
            GameObject prefab = hitEffectPrefabs[idx];
            if (prefab != null)
            {
                GameObject effect = Instantiate(prefab, transform.position, Quaternion.identity);
                Destroy(effect, duration);
            }
        }
    }

    private void OnNonLethalHit(Vector3 hitPoint)
    {
        // Spawn a smaller hit effect (30% scale) at the collision point
        if (hitEffectPrefabs != null && hitEffectPrefabs.Length > 0)
        {
            int idx = Random.Range(0, hitEffectPrefabs.Length);
            GameObject prefab = hitEffectPrefabs[idx];
            if (prefab != null)
            {
                GameObject effect = Instantiate(prefab, hitPoint, Quaternion.identity);
                effect.transform.localScale *= 0.3f;

                // Force all particle systems to respect the transform scale
                foreach (var ps in effect.GetComponentsInChildren<ParticleSystem>())
                {
                    var main = ps.main;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                }

                Destroy(effect, effectDuration * 0.5f);
            }
        }
    }

	private void OnCollisionEnter(Collision collision)
	{
		Vector3 contactPoint = collision.contactCount > 0
			? collision.GetContact(0).point
			: transform.position;

		// Prefer the projectile's own travel direction (collision.relativeVelocity flips sign
		// in awkward ways when a NavMeshAgent is involved).
		Vector3 hitDir = Vector3.zero;
		if (collision.rigidbody != null)
			hitDir = collision.rigidbody.linearVelocity;
		if (hitDir.sqrMagnitude < 0.0001f)
			hitDir = -collision.relativeVelocity;

		HandleHit(collision.gameObject, contactPoint, hitDir);
	}

	private void OnTriggerEnter(Collider other)
	{
		// Triggers don't have contact points; use closest point on our collider
		Vector3 contactPoint = other.ClosestPoint(transform.position);

		Rigidbody otherRb = other.attachedRigidbody;
		Vector3 hitDir = otherRb != null
			? otherRb.linearVelocity
			: (transform.position - contactPoint);

		HandleHit(other.gameObject, contactPoint, hitDir);
	}

    private void UpdateHealthBar()
    {
        if (healthBar == null || maxHitPoints <= 0) return;
        healthBar.power = Mathf.Clamp01((float)currentHitPoints / maxHitPoints);
    }
}
