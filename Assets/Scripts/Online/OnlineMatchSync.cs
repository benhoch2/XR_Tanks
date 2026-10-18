using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Phase of the current turn in an online match.</summary>
public enum TurnPhase : byte
{
    Waiting,   // before the first turn (clients still building their terrain)
    Starting,  // banner: whose turn it is
    Playing,   // the active tank may drive / fire
    Resolving, // shot in flight; camera watches it land
    GameOver,
}

/// <summary>Everything a headset needs to present the current turn. Written by the host.</summary>
public struct TurnState : INetworkSerializable, System.IEquatable<TurnState>
{
    public int Turn;              // increases every turn, so each new turn is a visible change
    public int ActiveIndex;       // participant index (NetworkTarget.ParticipantIndex), -1 = none
    public TurnPhase Phase;
    public int SecondsLeft;
    public Vector3 Wind;
    public int ParticipantCount;
    public FixedString128Bytes Message; // game-over text

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref Turn);
        serializer.SerializeValue(ref ActiveIndex);
        serializer.SerializeValue(ref Phase);
        serializer.SerializeValue(ref SecondsLeft);
        serializer.SerializeValue(ref Wind);
        serializer.SerializeValue(ref ParticipantCount);
        serializer.SerializeValue(ref Message);
    }

    public bool Equals(TurnState other) =>
        Turn == other.Turn && ActiveIndex == other.ActiveIndex && Phase == other.Phase &&
        SecondsLeft == other.SecondsLeft && Wind == other.Wind &&
        ParticipantCount == other.ParticipantCount && Message.Equals(other.Message);
}

/// <summary>
/// Network side of an online <see cref="TurnBasedMatch"/>, spawned by the host when the round
/// starts. The host's match writes <see cref="State"/>; every headset (host included) presents
/// from it. Craters and "watch where it landed" are broadcast as RPCs.
/// </summary>
public class OnlineMatchSync : NetworkBehaviour
{
    public readonly NetworkVariable<TurnState> State = new NetworkVariable<TurnState>();

    private readonly HashSet<ulong> _readyClients = new HashSet<ulong>();

    /// <summary>Host only: clients that have built their world and can be shown the first turn.</summary>
    public IReadOnlyCollection<ulong> ReadyClients => _readyClients;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
            _readyClients.Add(NetworkManager.LocalClientId);

        TurnBasedMatch match = TurnBasedMatch.Instance;
        if (match != null)
            match.AttachSync(this);
        else
            Debug.LogWarning("[Online] Match sync spawned but the scene has no TurnBasedMatch.");
    }

    /// <summary>Client → host: this headset's terrain is built.</summary>
    [Rpc(SendTo.Server)]
    public void ClientReadyRpc(RpcParams rpcParams = default)
    {
        _readyClients.Add(rpcParams.Receive.SenderClientId);
    }

    [Rpc(SendTo.NotServer)]
    public void CraterRpc(Vector3 center, float radius, float depth)
    {
        TerrainEnvironment environment = FindAnyObjectByType<TerrainEnvironment>();
        if (environment != null)
            environment.DigCrater(center, radius, depth);
    }

    [Rpc(SendTo.Everyone)]
    public void ShotLandedRpc(Vector3 point, Vector3 shooterPosition)
    {
        TurnBasedMatch.Instance?.ViewLanding(point, shooterPosition);
    }
}
