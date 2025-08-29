using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Collections;

public class Target : MonoBehaviour
{
    [Tooltip("Prefabs to spawn when hit (one will be picked at random)")]
    public GameObject[] hitEffectPrefabs;
	public float effectDuration = 1f;

    bool IsBall(GameObject obj)
	{
		if (obj == null) return false;
		// Prefer tag check (set the Ball object's tag to "Ball" if possible)
		if (obj.CompareTag("Ball")) return true;
		// Fallback to name check in case tag isn't set
		string n = obj.name ?? "";
		return n == "Ball" || n.Contains("Ball");
	}

    private void HandleHit(GameObject hitObject)
    {
        if (IsBall(hitObject))
        {
            if (hitEffectPrefabs != null && hitEffectPrefabs.Length > 0)
            {
                int idx = Random.Range(0, hitEffectPrefabs.Length);
                GameObject prefab = hitEffectPrefabs[idx];
                if (prefab != null)
                {
                    GameObject effect = Instantiate(prefab, transform.position, Quaternion.identity);
                    // Schedule independent destruction so it still happens even after this Target is destroyed.
                    Destroy(effect, effectDuration);
                    Debug.Log($"Scheduled destruction of {effect} in {effectDuration}s");
                }
                else
                {
                    Debug.LogWarning("Selected hitEffect prefab is null.", this);
                }
            }
            else
            {
                Debug.LogWarning("No hitEffectPrefabs assigned to target!", this);
            }

            Destroy(hitObject);
            Destroy(gameObject);
        }
    }

	private void OnCollisionEnter(Collision collision)
	{
		HandleHit(collision.gameObject);
	}

	private void OnTriggerEnter(Collider other)
	{
		HandleHit(other.gameObject);
	}

}
