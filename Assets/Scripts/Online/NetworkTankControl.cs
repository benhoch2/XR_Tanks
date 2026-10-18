using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Online roles for a tank.
/// <list type="bullet">
/// <item><b>Human tank</b> — owned by its player's headset, which drives it (owner-authoritative
/// NetworkTransform / NetworkRigidbody) when the turn state says it's their turn. Shots go to the
/// host through <see cref="RequestFire"/>; the host validates and spawns the projectile.</item>
/// <item><b>AI tank</b> — the host runs <see cref="EnemyTankAI"/> and its NavMeshAgent; clients
/// see a puppet driven by server NetworkTransforms.</item>
/// </list>
/// Offline these prefabs aren't used (see <see cref="NetworkPrefabMap"/>).
/// </summary>
public class NetworkTankControl : NetworkBehaviour
{
    /// <summary>True when this headset controls <paramref name="tank"/> (always offline).</summary>
    public static bool IsLocallyControlled(GameObject tank)
    {
        if (!GameSession.IsOnline || tank == null)
            return true;
        return tank.TryGetComponent(out NetworkObject networkObject) && networkObject.IsSpawned && networkObject.IsOwner;
    }

    public override void OnNetworkSpawn()
    {
        if (TryGetComponent(out ShootingControls gun))
            SetUpHumanTank(gun);
        else
            SetUpAiTank();
    }

    private void SetUpHumanTank(ShootingControls gun)
    {
        // The turn presentation turns these on for the owner during their own turn.
        gun.enabled = false;
        if (TryGetComponent(out TowerController drive)) drive.enabled = false;
        if (TryGetComponent(out TankFlip flip)) flip.enabled = false;

        if (!IsOwner)
            return;

        var environment = FindAnyObjectByType<TerrainEnvironment>();
        if (environment != null)
            StartCoroutine(HoldUntilGroundExists(environment));
    }

    private IEnumerator HoldUntilGroundExists(TerrainEnvironment environment)
    {
        // The host may spawn our tank before this headset finished generating its terrain;
        // pin it at its spawn pose (re-applied every frame, since NetworkRigidbody also sets
        // the kinematic state on spawn) until the ground is there.
        Rigidbody body = GetComponent<Rigidbody>();
        transform.GetPositionAndRotation(out Vector3 spawnPosition, out Quaternion spawnRotation);

        while (!environment.IsWorldReady)
        {
            if (body != null)
                body.isKinematic = true;
            transform.SetPositionAndRotation(spawnPosition, spawnRotation);
            yield return null;
        }

        if (body != null)
            body.isKinematic = false;
        environment.RegisterLocalHumanTank(transform);
    }

    private void SetUpAiTank()
    {
        if (IsServer)
            return;

        // Puppet: the host's AI decides everything.
        if (TryGetComponent(out EnemyTankAI ai)) ai.enabled = false;
        if (TryGetComponent(out NavMeshAgent agent)) agent.enabled = false;
        foreach (Rotator rotator in GetComponentsInChildren<Rotator>(true))
            rotator.enabled = false;
        foreach (WheelRotation wheel in GetComponentsInChildren<WheelRotation>(true))
        {
            wheel.tankBody = transform;
            wheel.enabled = true;
        }
    }

    // ---------- Firing ----------

    /// <summary>Owner only: asks the host to fire this tank's shot.</summary>
    public void RequestFire(int projectileIndex, Vector3 position, Quaternion rotation, Vector3 velocity)
    {
        FireRpc(projectileIndex, position, rotation, velocity);
    }

    [Rpc(SendTo.Server)]
    private void FireRpc(int projectileIndex, Vector3 position, Quaternion rotation, Vector3 velocity, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        TurnBasedMatch match = TurnBasedMatch.Instance;
        if (match == null || !match.CanFire(transform))
            return;

        var gun = GetComponent<ShootingControls>();
        GameObject prefab = NetworkPrefabMap.Resolve(gun.GetProjectilePrefab(projectileIndex));
        if (prefab == null)
            return;

        GameObject shot = Instantiate(prefab, position, rotation);
        var projectile = shot.GetComponent<Projectile>();
        projectile.damage = gun.ProjectileDamage;
        projectile.shooter = transform;

        Collider shotCollider = shot.GetComponent<Collider>();
        if (shotCollider != null)
        {
            foreach (Collider tankCollider in GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(shotCollider, tankCollider);
        }

        Rigidbody body = shot.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.linearVelocity = velocity;
        }

        shot.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
        match.OnShotFired(transform, projectile);
    }

    // ---------- Teleport shots ----------

    /// <summary>Host only: moves the tank (owner-authoritative, so the owner has to do it).</summary>
    public void Teleport(Vector3 destination)
    {
        TeleportRpc(destination);
    }

    [Rpc(SendTo.Owner)]
    private void TeleportRpc(Vector3 destination)
    {
        transform.position = destination;
        if (TryGetComponent(out Rigidbody body) && !body.isKinematic)
            body.linearVelocity = Vector3.zero;
    }
}
