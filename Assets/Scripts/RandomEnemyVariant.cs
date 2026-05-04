using UnityEngine;

/// <summary>
/// Placeholder script. Put this on an empty "spawner" prefab that the MRUK
/// FindSpawnPositions instantiates. On Start (deferred one frame so MRUK's
/// post-spawn parenting/positioning has settled), this picks one entry from
/// <see cref="variants"/> by weighted random, instantiates it at the
/// placeholder's transform, then destroys the placeholder.
///
/// Weights are relative — they don't have to sum to 100. The list
/// {50, 30, 20} produces the same distribution as {5, 3, 2}.
/// </summary>
public class RandomEnemyVariant : MonoBehaviour
{
    [System.Serializable]
    public struct WeightedVariant
    {
        public GameObject prefab;
        [Tooltip("Relative spawn weight. Higher = more common. Doesn't have to sum to 100.")]
        [Min(0f)]
        public float weight;
    }

    [Tooltip("Variants and their relative weights. The placeholder picks one " +
             "by weighted random and replaces itself with that variant.")]
    [SerializeField] private WeightedVariant[] variants;

    private void Start()
    {
        GameObject choice = PickByWeight();
        if (choice != null)
            Instantiate(choice, transform.position, transform.rotation, transform.parent);

        Destroy(gameObject);
    }

    private GameObject PickByWeight()
    {
        if (variants == null || variants.Length == 0)
        {
            Debug.LogWarning($"[{nameof(RandomEnemyVariant)}] No variants configured on '{gameObject.name}' — nothing will spawn.", this);
            return null;
        }

        float total = 0f;
        for (int i = 0; i < variants.Length; i++)
        {
            if (variants[i].prefab == null) continue;
            total += Mathf.Max(0f, variants[i].weight);
        }

        if (total <= 0f)
        {
            Debug.LogWarning($"[{nameof(RandomEnemyVariant)}] All weights are zero on '{gameObject.name}' — falling back to the first non-null variant.", this);
            for (int i = 0; i < variants.Length; i++)
                if (variants[i].prefab != null) return variants[i].prefab;
            return null;
        }

        float roll = Random.value * total;
        float acc = 0f;
        for (int i = 0; i < variants.Length; i++)
        {
            if (variants[i].prefab == null) continue;
            acc += Mathf.Max(0f, variants[i].weight);
            if (roll <= acc) return variants[i].prefab;
        }

        // Floating-point fallback: return the last non-null variant.
        for (int i = variants.Length - 1; i >= 0; i--)
            if (variants[i].prefab != null) return variants[i].prefab;
        return null;
    }
}
