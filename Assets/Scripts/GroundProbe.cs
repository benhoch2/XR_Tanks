using UnityEngine;

/// <summary>
/// Downward ground probe shared by the player and enemy tanks for slope handling.
/// </summary>
public static class GroundProbe
{
    private static readonly RaycastHit[] Hits = new RaycastHit[8];

    /// <summary>
    /// Casts straight down from <paramref name="origin"/> and returns the closest hit that
    /// isn't part of <paramref name="self"/> (the tank's own colliders, its projectile
    /// preview, ...). Triggers, the Ignore Raycast layer and dynamic rigidbodies (loose
    /// projectiles, the player tank) are skipped — none of those are ground.
    /// </summary>
    public static bool TryCastDown(Transform self, Vector3 origin, float maxDistance, out RaycastHit groundHit)
    {
        int count = Physics.RaycastNonAlloc(
            origin,
            Vector3.down,
            Hits,
            maxDistance,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);

        groundHit = default;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            Transform t = Hits[i].transform;
            if (t == self || t.IsChildOf(self))
                continue;

            Rigidbody body = Hits[i].rigidbody;
            if (body != null && !body.isKinematic)
                continue;

            if (!found || Hits[i].distance < groundHit.distance)
            {
                groundHit = Hits[i];
                found = true;
            }
        }

        return found;
    }
}
