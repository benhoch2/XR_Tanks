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

    private void Awake()
    {
        currentHitPoints = maxHitPoints;
    }

    bool IsBall(GameObject obj)
	{
		if (obj == null) return false;
		// Prefer tag check (set the Ball object's tag to "Ball" if possible)
		if (obj.CompareTag("Ball")) return true;
		// Fallback to name check in case tag isn't set
		string n = obj.name ?? "";
		return n == "Ball" || n.Contains("Ball");
	}

    private void HandleHit(GameObject hitObject, Vector3 hitPoint)
    {
        if (IsBall(hitObject))
        {
            // Read damage from Projectile component; default to instant kill if missing
            int damage = 0;
            Projectile proj = hitObject.GetComponent<Projectile>();
            if (proj != null)
                damage = proj.damage;

            // Always destroy the projectile
            Destroy(hitObject);

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
		HandleHit(collision.gameObject, contactPoint);
	}

	private void OnTriggerEnter(Collider other)
	{
		// Triggers don't have contact points; use closest point on our collider
		Vector3 contactPoint = other.ClosestPoint(transform.position);
		HandleHit(other.gameObject, contactPoint);
	}

}
