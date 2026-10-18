using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Online projectile: the host simulates it (physics, fuse, explosion, damage) and its
/// position is streamed to everyone else, who only see a kinematic copy. Collisions are
/// switched off on clients — Unity still sends collision messages to disabled scripts, so
/// disabling <see cref="Projectile"/> alone would not keep clients from reacting.
/// </summary>
[RequireComponent(typeof(Projectile))]
public class NetworkProjectile : NetworkBehaviour
{
    private Projectile _projectile;

    private void Awake()
    {
        _projectile = GetComponent<Projectile>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
            return;

        _projectile.enabled = false;
        foreach (Collider col in GetComponentsInChildren<Collider>())
            col.enabled = false;
    }

    public override void OnNetworkDespawn()
    {
        // The host shows its own explosion; clients rebuild it where the copy ended up.
        if (!IsServer && !NetworkManager.ShutdownInProgress)
            _projectile.PlayExplosionEffect(transform.position);
    }
}
