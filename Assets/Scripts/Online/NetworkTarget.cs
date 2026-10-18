using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Online mirror of a <see cref="Target"/> (tanks and crates). Damage is only decided on
/// the host; this pushes the host's hit points to every client (health bar, hit effect)
/// and replays the death effect there before the host's despawn removes the object.
/// Also carries the tank's participant index in the turn order.
/// </summary>
[RequireComponent(typeof(Target))]
public class NetworkTarget : NetworkBehaviour
{
    /// <summary>Position in the match's turn order: humans first (roster order), then AI. -1 for crates.</summary>
    public readonly NetworkVariable<int> ParticipantIndex = new NetworkVariable<int>(-1);

    private readonly NetworkVariable<int> _hitPoints = new NetworkVariable<int>();
    private Target _target;

    private void Awake()
    {
        _target = GetComponent<Target>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            _hitPoints.Value = _target.CurrentHitPoints;
            _target.Died += OnServerDied;
        }
        else
        {
            _hitPoints.OnValueChanged += OnHitPointsChanged;
            _target.ApplyNetworkHitPoints(_hitPoints.Value, playEffect: false);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
            _target.Died -= OnServerDied;
        else
            _hitPoints.OnValueChanged -= OnHitPointsChanged;
    }

    private void Update()
    {
        if (IsServer && IsSpawned && _hitPoints.Value != _target.CurrentHitPoints)
            _hitPoints.Value = _target.CurrentHitPoints;
    }

    private void OnHitPointsChanged(int previous, int current)
    {
        _target.ApplyNetworkHitPoints(current, playEffect: current < previous && current > 0);
    }

    private void OnServerDied(Target target)
    {
        // Sent before the host destroys the object, on the same reliable channel as the
        // despawn, so clients play the effect while the object is still there.
        DiedRpc();
    }

    [Rpc(SendTo.NotServer)]
    private void DiedRpc()
    {
        _target.PlayDeathEffect();
    }
}
