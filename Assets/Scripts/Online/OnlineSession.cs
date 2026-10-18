using System;
using System.Linq;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Online turn-based sessions: Unity Multiplayer Services (join codes) over Relay, driving
/// Netcode for GameObjects. No server of our own — Relay forwards the traffic. One headset
/// hosts (referees the match, runs the AI); up to <see cref="MaxPlayers"/> headsets play.
///
/// Flow: the host calls <see cref="HostAsync"/> and shares the code, others call
/// <see cref="JoinAsync"/>; the host's <see cref="StartMatch"/> sends the match setup
/// (seed, AI count, roster) to everyone and loads the VR scene through Netcode, so every
/// client follows. Direct IP (<see cref="HostDirect"/> / <see cref="JoinDirect"/>) does
/// the same without Unity services — for LAN play and in-editor tests.
/// </summary>
public static class OnlineSession
{
    public const int MaxPlayers = GameSession.MaxHumanPlayers;
    public const ushort DefaultDirectPort = 7777;
    private const string MatchSetupMessage = "XRTanks.MatchSetup";

    /// <summary>The Multiplayer Services session (null for direct-IP games).</summary>
    public static ISession Session { get; private set; }

    /// <summary>Code the host shares; null for direct-IP games.</summary>
    public static string JoinCode => Session?.Code;

    public static bool IsConnected => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
    public static bool IsHost => IsConnected && NetworkManager.Singleton.IsServer;

    /// <summary>Players in the game, host included.</summary>
    public static int PlayerCount =>
        !IsConnected ? 0
        : IsHost ? NetworkManager.Singleton.ConnectedClientsIds.Count
        : Session?.PlayerCount ?? 0;

    /// <summary>Shown by the menu after returning to it (e.g. "Disconnected from host"); cleared once read.</summary>
    public static string PendingMessage;

    /// <summary>Raised on clients when the host's match setup arrives (just before the game scene loads).</summary>
    public static event Action MatchSetupReceived;

    private static NetworkManager _hookedManager;

    /// <summary>Creates the NetworkManager from <paramref name="bootstrapPrefab"/> if there isn't one (it persists across scenes).</summary>
    public static void EnsureNetworkManager(GameObject bootstrapPrefab)
    {
        if (NetworkManager.Singleton != null)
            return;
        if (bootstrapPrefab == null)
            throw new InvalidOperationException("No NetworkBootstrap prefab assigned.");

        GameObject instance = UnityEngine.Object.Instantiate(bootstrapPrefab);
        instance.name = bootstrapPrefab.name;
    }

    // ---------- Relay sessions (internet) ----------

    /// <summary>Creates a private Relay session and returns its join code.</summary>
    public static async Task<string> HostAsync()
    {
        await SignInAsync();
        var options = new SessionOptions { MaxPlayers = MaxPlayers, IsPrivate = true }.WithRelayNetwork();
        Session = await MultiplayerService.Instance.CreateSessionAsync(options);
        HookNetwork();
        return Session.Code;
    }

    public static async Task JoinAsync(string code)
    {
        await SignInAsync();
        Session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant());
        HookNetwork();
    }

    private static async Task SignInAsync()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    // ---------- Direct IP (LAN / editor tests) ----------

    public static void HostDirect(ushort port = DefaultDirectPort)
    {
        Transport.SetConnectionData("0.0.0.0", port, "0.0.0.0");
        if (!NetworkManager.Singleton.StartHost())
            throw new InvalidOperationException($"Could not host on port {port}.");
        HookNetwork();
    }

    public static void JoinDirect(string address, ushort port = DefaultDirectPort)
    {
        Transport.SetConnectionData(address, port);
        if (!NetworkManager.Singleton.StartClient())
            throw new InvalidOperationException($"Could not connect to {address}:{port}.");
        HookNetwork();
    }

    private static UnityTransport Transport => NetworkManager.Singleton.GetComponent<UnityTransport>();

    // ---------- Match lifecycle ----------

    /// <summary>Host only: fixes the roster, sends the match setup and loads the VR scene on every headset.</summary>
    public static void StartMatch(int aiTanks)
    {
        NetworkManager manager = RequireHost();

        GameSession.OnlineRoster.Clear();
        GameSession.OnlineRoster.Add(manager.LocalClientId);
        foreach (ulong id in manager.ConnectedClientsIds)
        {
            if (id != manager.LocalClientId)
                GameSession.OnlineRoster.Add(id);
        }
        GameSession.AiTanks = Mathf.Clamp(aiTanks, 0, GameSession.MaxAiTanks);

        // Nobody joins a match in progress (there's no mid-match sync).
        if (Session is IHostSession hostSession)
        {
            hostSession.IsLocked = true;
            _ = hostSession.SavePropertiesAsync();
        }

        LoadRound(manager);
    }

    /// <summary>Host only: next round with a new map; players who left are dropped from the roster.</summary>
    public static void RestartMatch()
    {
        NetworkManager manager = RequireHost();
        GameSession.OnlineRoster.RemoveAll(id => !manager.ConnectedClientsIds.Contains(id));
        LoadRound(manager);
    }

    private static void LoadRound(NetworkManager manager)
    {
        GameSession.Mode = GameMode.TurnBased;
        GameSession.IsOnline = true;
        GameSession.OnlineSeed = UnityEngine.Random.Range(0, 100000);

        using (var writer = new FastBufferWriter(64 + 8 * GameSession.OnlineRoster.Count, Allocator.Temp))
        {
            writer.WriteValueSafe(GameSession.OnlineSeed);
            writer.WriteValueSafe(GameSession.AiTanks);
            writer.WriteValueSafe(GameSession.OnlineRoster.Count);
            foreach (ulong id in GameSession.OnlineRoster)
                writer.WriteValueSafe(id);

            // Same reliable, ordered pipeline as the scene event that follows, so clients
            // always know the seed before the scene starts building its terrain.
            foreach (ulong id in manager.ConnectedClientsIds)
            {
                if (id != manager.LocalClientId)
                    manager.CustomMessagingManager.SendNamedMessage(MatchSetupMessage, id, writer);
            }
        }

        manager.SceneManager.LoadScene(GameSession.VirtualRealitySceneName, LoadSceneMode.Single);
    }

    /// <summary>Leaves the session (as host this ends it for everyone) and resets to offline.</summary>
    public static async Task LeaveAsync()
    {
        ISession session = Session;
        Session = null;
        UnhookNetwork();

        try
        {
            if (session != null)
                await session.LeaveAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Online] Leaving the session failed: {e.Message}");
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();
        GameSession.ResetOnline();
    }

    private static NetworkManager RequireHost()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsServer)
            throw new InvalidOperationException("Only the host can start the match.");
        return manager;
    }

    // ---------- Network events ----------

    private static void HookNetwork()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (_hookedManager == manager)
            return;
        UnhookNetwork();

        _hookedManager = manager;
        manager.OnClientDisconnectCallback += OnClientDisconnect;
        manager.OnTransportFailure += OnTransportFailure;
        if (!manager.IsServer)
            manager.CustomMessagingManager.RegisterNamedMessageHandler(MatchSetupMessage, OnMatchSetup);
    }

    private static void UnhookNetwork()
    {
        if (_hookedManager == null)
            return;

        _hookedManager.OnClientDisconnectCallback -= OnClientDisconnect;
        _hookedManager.OnTransportFailure -= OnTransportFailure;
        _hookedManager.CustomMessagingManager?.UnregisterNamedMessageHandler(MatchSetupMessage);
        _hookedManager = null;
    }

    private static void OnMatchSetup(ulong senderId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out int seed);
        reader.ReadValueSafe(out int aiTanks);
        reader.ReadValueSafe(out int count);
        GameSession.OnlineRoster.Clear();
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ulong id);
            GameSession.OnlineRoster.Add(id);
        }

        GameSession.Mode = GameMode.TurnBased;
        GameSession.IsOnline = true;
        GameSession.OnlineSeed = seed;
        GameSession.AiTanks = aiTanks;
        MatchSetupReceived?.Invoke();
    }

    private static void OnClientDisconnect(ulong clientId)
    {
        NetworkManager manager = NetworkManager.Singleton;
        // The host sees every client leave (the match skips their tank). A client only
        // cares about losing its own connection, which means the host is gone.
        if (manager == null || manager.IsServer || clientId != manager.LocalClientId)
            return;

        ReturnToMenu("Disconnected from the host.");
    }

    private static void OnTransportFailure()
    {
        ReturnToMenu("Network connection failed.");
    }

    private static async void ReturnToMenu(string message)
    {
        PendingMessage = message;
        await LeaveAsync();
        if (SceneManager.GetActiveScene().name != GameSession.MenuSceneName)
            SceneManager.LoadScene(GameSession.MenuSceneName);
    }
}
