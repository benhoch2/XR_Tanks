/// <summary>How a match in the VR scene is played.</summary>
public enum GameMode
{
    /// <summary>The original game: everyone acts at once.</summary>
    RealTime,
    /// <summary>Worms-style: humans (hot-seat) and AI tanks take turns. VR scene only.</summary>
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

    /// <summary>Turn-based only: humans sharing the headset, one tank each.</summary>
    public static int HumanPlayers = 1;

    /// <summary>Turn-based only: AI tanks (replaces the config menu's enemy count).</summary>
    public static int AiTanks = 3;

    public static bool IsTurnBased => Mode == GameMode.TurnBased;
}
