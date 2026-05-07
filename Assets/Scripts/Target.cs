using System.Collections;
using System.Collections.Generic;
using UnityEngine;

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
    [Tooltip("Multiplier applied to the projectile's velocity at impact to derive the knockback impulse. " +
             "0 disables knockback. EnemyTankAI / TowerController each divide the result by their own mass " +
             "so a Heavy tank slides less than a Light tank from the same hit.")]
    [SerializeField] private float knockbackImpulseScale = 0.1f;

    // Shared buffer for the per-hit ParticleSystem walk in OnNonLethalHit.
    // Reused across all Target instances on the main thread to avoid per-hit allocations.
    private static readonly List<ParticleSystem> _hitParticleBuffer = new List<ParticleSystem>(8);

    private void Awake()
    {
        currentHitPoints = maxHitPoints;
        UpdateHealthBar();
    }

    private void HandleHit(GameObject hitObject, Vector3 hitPoint)
    {
        Projectile proj = hitObject.GetComponent<Projectile>();
        if (proj != null)
        {
            int damage = proj.damage;

            // Pull the projectile's velocity BEFORE Destroy schedules it for cleanup,
            // so we can derive a knockback impulse from the actual incoming momentum.
            ApplyImpactKnockback(hitObject);

            // Teleport balls own their own lifetime (deferred-teleport timer) and must survive
            // enemy bounces. Every other type gets destroyed on contact.
            if (proj.projectileType != ProjectileType.Teleport)
                Destroy(hitObject);

            SpawnHitExplosion(hitPoint);
            TryFirePlayerHitConfirmedHaptic(proj);

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
                // If this is a crate (has a CrateReward), let it roll and apply its outcome
                // to the shooter before we destroy it. Player projectiles set proj.shooter to
                // the player tank root, which carries both ShootingControls and Target.
                if (proj.shooter != null && TryGetComponent(out CrateReward reward))
                    reward.RollAndApply(proj.shooter.gameObject, hitPoint);

                SpawnEffect(effectDuration);
                Destroy(gameObject);
                return;
            }

            // Health-based path (enemies and the player tank).
            currentHitPoints -= damage;
            UpdateHealthBar();

            if (currentHitPoints <= 0)
            {
                SpawnEffect(effectDuration);
                HandleLethalHit();
            }
            else
            {
                // Non-lethal hit: spawn effect at collision point
                OnNonLethalHit(hitPoint);
            }
        }
    }

    private void HandleLethalHit()
    {
        // The player tank carries ShootingControls; enemies don't.
        if (GetComponent<ShootingControls>() != null)
        {
            StartCoroutine(HandlePlayerDeath());
            return;
        }

        // Enemy died — notify the manager so the win condition can resolve.
        if (GameConfigManager.Instance != null)
            GameConfigManager.Instance.OnEnemyKilled();

        Destroy(gameObject);
    }

    private IEnumerator HandlePlayerDeath()
    {
        // Disable input scripts so the player can't drive/aim/fire while dying.
        if (TryGetComponent(out TowerController tc)) tc.enabled = false;
        if (TryGetComponent(out ShootingControls sc)) sc.enabled = false;
        if (TryGetComponent(out TankFlip tf)) tf.enabled = false;

        // Long, heavy death pulse on both controllers.
        Haptics.Pulse(this, OVRInput.Controller.LTouch, 0.7f, 0.9f, 0.5f);
        Haptics.Pulse(this, OVRInput.Controller.RTouch, 0.7f, 0.9f, 0.5f);

        yield return new WaitForSecondsRealtime(3f);

        if (GameConfigManager.Instance != null)
            GameConfigManager.Instance.ReloadScene();
    }

    private void SpawnHitExplosion(Vector3 position)
    {
        if (hitExplosionPrefab == null) return;
        GameObject effect = Instantiate(hitExplosionPrefab, position, Quaternion.identity);
        Destroy(effect, hitExplosionDuration);
    }

    private void ApplyImpactKnockback(GameObject projectileObj)
    {
        if (knockbackImpulseScale <= 0f || projectileObj == null)
            return;

        Rigidbody projRb = projectileObj.GetComponent<Rigidbody>();
        if (projRb == null)
            return;

        Vector3 impulse = projRb.linearVelocity * knockbackImpulseScale;

        if (TryGetComponent(out EnemyTankAI enemy))
            enemy.ApplyKnockback(impulse);
        else if (TryGetComponent(out TowerController player))
            player.ApplyKnockback(impulse);
    }

    private void TryFirePlayerHitConfirmedHaptic(Projectile proj)
    {
        // Only ping the player when their own shot connects with another Target
        // (not when an enemy's shot lands, and not when the player gets hit).
        if (proj == null || proj.shooter == null)
            return;

        if (GetComponent<ShootingControls>() != null)
            return; // we ARE the player; no self hit-confirm

        ShootingControls shooterControls = proj.shooter.GetComponent<ShootingControls>();
        if (shooterControls == null)
            return; // shooter isn't the player

        Haptics.Pulse(shooterControls, OVRInput.Controller.RTouch, 0.6f, 0.7f, 0.07f);
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

                // Force all particle systems to respect the transform scale.
                // Uses the non-allocating List overload so we don't pay an
                // array-allocation cost on every non-lethal hit.
                effect.GetComponentsInChildren(_hitParticleBuffer);
                for (int i = 0; i < _hitParticleBuffer.Count; i++)
                {
                    var main = _hitParticleBuffer[i].main;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                }
                _hitParticleBuffer.Clear();

                Destroy(effect, effectDuration * 0.5f);
            }
        }
    }

	private void OnCollisionEnter(Collision collision)
	{
		Vector3 contactPoint = collision.contactCount > 0
			? collision.GetContact(0).point
			: transform.position;
		HandleHit(collision.gameObject, contactPoint);
	}

	private void OnTriggerEnter(Collider other)
	{
		// Triggers don't have contact points; use closest point on our collider
		Vector3 contactPoint = other.ClosestPoint(transform.position);
		HandleHit(other.gameObject, contactPoint);
	}

    private void UpdateHealthBar()
    {
        if (healthBar == null || maxHitPoints <= 0) return;
        healthBar.power = Mathf.Clamp01((float)currentHitPoints / maxHitPoints);
    }

    /// <summary>
    /// Restores up to <paramref name="amount"/> hit points, capped at <see cref="maxHitPoints"/>.
    /// Returns true if any healing actually applied (so callers can suppress UI feedback when
    /// the target is already at full health).
    /// </summary>
    public bool Heal(int amount)
    {
        if (amount <= 0 || maxHitPoints <= 0 || currentHitPoints >= maxHitPoints) return false;
        currentHitPoints = Mathf.Min(currentHitPoints + amount, maxHitPoints);
        UpdateHealthBar();
        return true;
    }

    /// <summary>
    /// Continuous-damage entry point used by DPS weapons (e.g. the laser cannon). Skips the
    /// projectile/crate paths in <see cref="HandleHit"/> and just deducts HP, runs the lethal
    /// check, and triggers the same death sequence as a projectile kill. No-op for crates
    /// (maxHitPoints &lt;= 0) — callers should special-case those via <see cref="Kill"/>.
    /// </summary>
    public void TakeDamage(int damage)
    {
        if (damage <= 0 || maxHitPoints <= 0 || currentHitPoints <= 0) return;

        currentHitPoints -= damage;
        UpdateHealthBar();

        if (currentHitPoints <= 0)
        {
            SpawnEffect(effectDuration);
            HandleLethalHit();
        }
    }

    /// <summary>
    /// Insta-kill entry point used by weapons that bypass HP entirely (e.g. the suicide drone).
    /// Spawns the death effect, then runs the same lethal-hit follow-up that the projectile path
    /// uses (notify GameConfigManager for enemies, run player death sequence for the player tank,
    /// or just destroy the GameObject for crates).
    /// </summary>
    public void Kill()
    {
        currentHitPoints = 0;
        UpdateHealthBar();
        SpawnEffect(effectDuration);

        // For zero-HP targets (crates) HandleLethalHit's player/enemy logic doesn't apply.
        if (maxHitPoints <= 0)
        {
            Destroy(gameObject);
            return;
        }

        HandleLethalHit();
    }
}
