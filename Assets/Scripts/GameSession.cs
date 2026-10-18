using System.Collections.Generic;
using Unity.Netcode;

/// <summary>How a match in the VR scene is played.</summary>
public enum GameMode
{
    /// <summary>The original game: everyone acts at once.</summary>
    RealTime,
    /// <summary>Worms-style: humans (hot-seat or online) and AI tanks take turns. VR scene only.</summary>
    TurnBased,
}

/// <summary>
/// What the main menu picked, carried across scene loads (static, so it survives the
/// round reloads too). Scenes launched directly in the editor see the defaults, which
/// is the original real-time game.
/// </summary>
public static class GameSession
{
    public const string MenuSceneName = "MainMenu";
    public const string MixedRealitySceneName = "SampleScene";
    public const string VirtualRealitySceneName = "VRMountains";

    public const int MaxHumanPlayers = 4;
    public const int MaxAiTanks = 6;

    public static GameMode Mode = GameMode.RealTime;

    /// <summary>Turn-based only: humans sharing the headset, one tank each (offline hot-seat).</summary>
    public static int HumanPlayers = 1;

    /// <summary>Turn-based only: AI tanks (replaces the config menu's enemy count). Online allows 0 (free-for-all).</summary>
    public static int AiTanks = 3;

    public static bool IsTurnBased => Mode == GameMode.TurnBased;

    // ---------- Online (turn-based over Unity Relay, see OnlineSession) ----------

    /// <summary>True for an online turn-based match: one player per headset, host referees.</summary>
    public static bool IsOnline;

    /// <summary>Terrain seed chosen by the host for the current online round; -1 until it arrives.</summary>
    public static int OnlineSeed = -1;

    /// <summary>Netcode client ids of the human players, in player order (host first).</summary>
    public static readonly List<ulong> OnlineRoster = new List<ulong>();

    /// <summary>
    /// Whether this process decides gameplay outcomes (damage, deaths, explosions, turn order).
    /// Always true offline; online only the host does — everyone else mirrors it.
    /// </summary>
    public static bool HasAuthority =>
        !IsOnline || (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer);

    /// <summary>Back to an offline session (leaving an online match).</summary>
    public static void ResetOnline()
    {
        IsOnline = false;
        OnlineSeed = -1;
        OnlineRoster.Clear();
    }
}
