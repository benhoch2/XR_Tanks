using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Multiplayer.PlayMode;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Test-only client for online play-tests inside the editor. Runs only in a Multiplayer Play
/// Mode virtual player tagged <see cref="Tag"/> (see <see cref="StartVirtualClient"/>): joins the
/// main editor's direct-IP host from the menu and, whenever it's this headset's turn, drives
/// forward briefly and fires through <see cref="XRTestInput"/>. Every second it appends
/// <see cref="Describe"/> to <see cref="LogPath"/> so the main editor can compare both sides.
/// </summary>
[InitializeOnLoad]
public static class OnlineTestAutopilot
{
    public const string Tag = "AutoClient";
    public static string LogPath => Path.Combine(Path.GetTempPath(), "xrtanks_autopilot.log");

    /// <summary>
    /// While this file exists the client stays in Play mode (entering it from MainMenu);
    /// deleting it stops the client. Starting Play mode from code in the main editor doesn't
    /// carry over to virtual players, so the main editor drives the client through this flag.
    /// </summary>
    public static string PlayFlagPath => Path.Combine(Path.GetTempPath(), "xrtanks_autopilot.play");

    private static readonly Vector3[] SamplePoints = { Vector3.zero, new Vector3(3f, 0f, 3f), new Vector3(-5f, 0f, 2f), new Vector3(6f, 0f, -7f) };

    private static double _nextJoinAttempt;
    private static double _nextLog;
    private static double _turnStart = -1;
    private static bool _turnDone;
    private static string _preparedScene;

    static OnlineTestAutopilot()
    {
        if (!CurrentPlayer.IsMainEditor && CurrentPlayer.ReadOnlyTags().Contains(Tag))
            EditorApplication.update += Tick;
    }

    /// <summary>Main editor: tags Multiplayer Play Mode's Player 2 as the autopilot client and launches it.</summary>
    public static string StartVirtualClient()
    {
        Assembly module = System.AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "UnityEditor.MultiplayerModule");
        System.Type playmode = module.GetType("Unity.Multiplayer.PlayMode.Editor.MultiplayerPlaymode");
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        object tags = playmode.GetProperty("PlayerTags", any).GetValue(null);
        bool hasTag = (bool)tags.GetType().GetMethod("Contains", any).Invoke(tags, new object[] { Tag });
        if (!hasTag)
            tags.GetType().GetMethod("Add", any).Invoke(tags, new object[] { Tag, null });

        object player = playmode.GetProperty("PlayerTwo", any).GetValue(null);
        string[] playerTags = (string[])player.GetType().GetProperty("Tags", any).GetValue(player);
        if (!playerTags.Contains(Tag))
            player.GetType().GetMethod("AddTag", any).Invoke(player, new object[] { Tag, null });

        object[] activateArgs = { null, null };
        bool activated = (bool)player.GetType().GetMethod("Activate", any).Invoke(player, activateArgs);
        object state = player.GetType().GetProperty("PlayerState", any).GetValue(player);
        return $"activate={activated} error={activateArgs[0]} state={state}";
    }

    /// <summary>One-line snapshot of this headset's view of the match (same format on host and client).</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.Append($"t={Time.time:F1} scene={SceneManager.GetActiveScene().name} online={GameSession.IsOnline} seed={GameSession.OnlineSeed}");

        NetworkManager manager = NetworkManager.Singleton;
        sb.Append(manager == null ? " net=none" : $" net={(manager.IsServer ? "host" : manager.IsConnectedClient ? "client" : "connecting")} id={manager.LocalClientId}");

        TerrainEnvironment environment = Object.FindAnyObjectByType<TerrainEnvironment>();
        if (environment != null && environment.IsWorldReady)
        {
            sb.Append($" craters={environment.CraterCount} ground=[");
            sb.Append(string.Join(",", SamplePoints.Select(p => environment.GetGroundHeight(p).ToString("F3"))));
            sb.Append("]");
        }

        TurnBasedMatch match = TurnBasedMatch.Instance;
        if (match != null)
        {
            TurnState state = match.OnlineState;
            sb.Append($" turn={state.Turn} active={state.ActiveIndex} phase={state.Phase} secs={state.SecondsLeft} msg='{state.Message}'");
        }

        foreach (NetworkTarget target in Object.FindObjectsByType<NetworkTarget>(FindObjectsSortMode.None)
                     .Where(t => t.ParticipantIndex.Value >= 0)
                     .OrderBy(t => t.ParticipantIndex.Value))
        {
            Vector3 p = target.transform.position;
            bool owned = target.IsOwner && target.TryGetComponent(out ShootingControls _);
            sb.Append($" | P{target.ParticipantIndex.Value}{(owned ? "*" : "")} hp={target.GetComponent<Target>().CurrentHitPoints} pos=({p.x:F2},{p.y:F2},{p.z:F2})");
        }
        return sb.ToString();
    }

    private static void Tick()
    {
        bool wantPlay = File.Exists(PlayFlagPath);
        if (!Application.isPlaying)
        {
            _preparedScene = null;
            _turnStart = -1;
            if (wantPlay && !EditorApplication.isCompiling && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/Scenes/MainMenu.unity");
                EditorApplication.EnterPlaymode();
                File.AppendAllText(LogPath, "client entering play mode\n");
            }
            return;
        }

        if (!wantPlay)
        {
            EditorApplication.ExitPlaymode();
            return;
        }

        double now = EditorApplication.timeSinceStartup;
        string scene = SceneManager.GetActiveScene().name;

        if (scene != _preparedScene)
        {
            _preparedScene = scene;
            XRTestInput.Connect();
            XRTestInput.StandUp();
        }

        if (scene == GameSession.MenuSceneName && !OnlineSession.IsConnected && now >= _nextJoinAttempt)
        {
            _nextJoinAttempt = now + 3.0;
            TryJoin();
        }

        if (scene == GameSession.VirtualRealitySceneName && GameSession.IsOnline)
            PlayOwnTurn(now);

        if (now >= _nextLog)
        {
            _nextLog = now + 1.0;
            File.AppendAllText(LogPath, Describe() + "\n");
        }
    }

    private static void TryJoin()
    {
        try
        {
            MainMenu menu = Object.FindAnyObjectByType<MainMenu>();
            if (menu == null)
                return;
            var prefab = (GameObject)typeof(MainMenu)
                .GetField("networkBootstrapPrefab", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(menu);
            OnlineSession.EnsureNetworkManager(prefab);
            OnlineSession.JoinDirect("127.0.0.1");
            File.AppendAllText(LogPath, "joining 127.0.0.1\n");
        }
        catch (System.Exception e)
        {
            File.AppendAllText(LogPath, "join failed: " + e.Message + "\n");
        }
    }

    private static void PlayOwnTurn(double now)
    {
        ShootingControls gun = Object.FindObjectsByType<NetworkTankControl>(FindObjectsSortMode.None)
            .Where(t => t.IsOwner)
            .Select(t => t.GetComponent<ShootingControls>())
            .FirstOrDefault(g => g != null);
        if (gun == null)
            return;

        if (!gun.enabled)
        {
            // Not our turn (or it just ended): be ready for the next one.
            if (_turnStart >= 0 || _turnDone)
                XRTestInput.ReleaseAll();
            _turnStart = -1;
            _turnDone = false;
            return;
        }

        if (_turnDone)
            return;
        if (_turnStart < 0)
        {
            _turnStart = now;
            File.AppendAllText(LogPath, "my turn\n");
        }

        // Drive forward 1 s, then charge for 0.8 s and release.
        double t = now - _turnStart;
        if (t < 0.3) return;
        if (t < 1.3) { XRTestInput.Stick("left", 0f, 1f); return; }
        if (t < 1.8) { XRTestInput.Stick("left", 0f, 0f); return; }
        if (t < 2.6) { XRTestInput.Axis("right", "trigger", 1f); return; }

        XRTestInput.Axis("right", "trigger", 0f);
        _turnDone = true;
        File.AppendAllText(LogPath, "fired\n");
    }
}
