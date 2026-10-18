using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Worms-style rounds in the VR scene (<see cref="GameSession.IsTurnBased"/>). The humans
/// (hot-seat, one tank each, passing the headset) are one team, the AI tanks the other;
/// the teams alternate turns, cycling through their living tanks.
///
/// A human turn: drive within a fuel budget, aim (with a short arc preview), fire one shot,
/// all before the timer runs out. An AI turn: <see cref="EnemyTankAI.TakeTurn"/>. After each
/// shot the camera rig jumps to watch it land, and the turn ends once every projectile has
/// resolved. Explosions dig craters and damage everything in the blast radius; wind changes
/// every turn.
///
/// Spawned and started by <see cref="TerrainEnvironment"/>; victory reuses the regular win
/// condition (GameConfigManager reloads the round), defeat reloads from here.
///
/// <b>Online</b> (<see cref="GameSession.IsOnline"/>): one tank per headset. The host runs the
/// rules (turn order, timer, wind, AI turns, explosions, end of game) and publishes a
/// <see cref="TurnState"/> through <see cref="OnlineMatchSync"/>; every headset, host included,
/// presents from that state (camera, banners, HUD) and enables its own tank's controls on its
/// turn. Shots reach the host through <see cref="NetworkTankControl"/>.
/// </summary>
public class TurnBasedMatch : MonoBehaviour
{
    [Header("Turns")]
    [SerializeField] private float humanTurnSeconds = 30f;
    [Tooltip("Meters a human tank may drive per turn.")]
    [SerializeField] private float humanFuel = 1.5f;
    [Tooltip("Meters an AI tank drives per turn.")]
    [SerializeField] private float aiFuel = 0.8f;
    [SerializeField] private float aiMaxDriveSeconds = 3f;
    [Tooltip("A turn ends at the latest this long after the shot, even if a projectile is still around.")]
    [SerializeField] private float maxResolveSeconds = 15f;
    [SerializeField] private float pauseBetweenTurns = 1f;
    [Tooltip("Seconds a human may fly around and scout the map before their turn starts " +
             "(timer and fuel untouched meanwhile; A ends it early). 0 = off. One headset only.")]
    [SerializeField] private float scoutSeconds = 10f;

    [Header("Wind")]
    [Tooltip("Strongest wind, as horizontal acceleration on projectiles (m/s²).")]
    [SerializeField] private float maxWind = 1f;

    [Header("Explosions")]
    [Tooltip("Tanks and crates within this distance of an explosion take damage, more near the center.")]
    [SerializeField] private float blastRadius = 0.4f;
    [SerializeField] private float craterRadius = 0.35f;
    [SerializeField] private float craterDepth = 0.12f;
    [Tooltip("Horizontal push at the blast center, fed through each tank's knockback.")]
    [SerializeField] private float blastKnockback = 0.4f;

    [Header("Presentation")]
    [SerializeField] private PlayerRigFollow rigFollow;
    [Tooltip("URP Unlit material for the aim preview, wind arrow and markers.")]
    [SerializeField] private Material unlitMaterial;
    [Tooltip("Seconds of flight the aim preview draws (gravity only — wind is the player's job).")]
    [SerializeField] private float aimPreviewSeconds = 0.45f;
    [Tooltip("How far from a landing shot the camera jumps to watch it.")]
    [SerializeField] private float viewShotDistance = 1.5f;

    [Header("Online")]
    [Tooltip("Network prefab carrying OnlineMatchSync; the host spawns it when an online round starts.")]
    [SerializeField] private GameObject onlineSyncPrefab;
    [Tooltip("How long the host waits for every headset to finish building the map before the first turn.")]
    [SerializeField] private float clientReadyTimeout = 20f;
    [SerializeField] private float gameOverSeconds = 4f;

    /// <summary>The scene's match (one per VR scene).</summary>
    public static TurnBasedMatch Instance { get; private set; }

    /// <summary>Online: the turn state as this headset last saw it (tests, diagnostics).</summary>
    public TurnState OnlineState => _state;

    private static readonly Color[] HumanColors =
    {
        new Color(1f, 0.35f, 0.3f),
        new Color(0.35f, 0.6f, 1f),
        new Color(0.4f, 0.9f, 0.4f),
        new Color(1f, 0.85f, 0.3f),
    };
    private static readonly Color AiColor = new Color(0.85f, 0.85f, 0.85f);
    private static readonly Color WindColor = new Color(0.6f, 0.9f, 1f);

    private class Participant
    {
        public int index; // online: NetworkTarget.ParticipantIndex
        public bool local; // online: this headset owns the tank
        public string name;
        public Color color;
        public bool human;
        public Transform tank;
        public Target target;
        public EnemyTankAI ai;
        public TowerController drive;
        public ShootingControls gun;
        public TankFlip flip;
        public TextMeshPro nameTag;

        public bool Alive => tank != null && target != null && target.IsAlive;
    }

    private readonly List<Participant> _humans = new List<Participant>();
    private readonly List<Participant> _ais = new List<Participant>();
    private readonly List<Object> _runtimeObjects = new List<Object>();

    private TerrainEnvironment _environment;
    private InputAction _readyAction;
    private Participant _active;
    private float _timeLeft;
    private Projectile _lastShot;
    private bool _navMeshDirty;
    private bool _gameOver;
    private bool _resolving;
    private bool _scouting;
    private float _scoutLeft;
    private bool _hooked;

    // Online
    private OnlineMatchSync _sync;
    private TurnState _state;
    private bool _shotAccepted;
    private Participant _controlled;
    private Coroutine _bannerTimeout;

    private TextMeshPro _hud;
    private TextMeshPro _banner;
    private LineRenderer _windArrow;
    private LineRenderer _aimPreview;
    private readonly Vector3[] _aimPoints = new Vector3[16];

    /// <summary>Called by TerrainEnvironment once the round's tanks are spawned.</summary>
    public void Begin(TerrainEnvironment environment, List<Transform> humanTanks)
    {
        _environment = environment;
        if (GameSession.IsOnline)
        {
            BeginOnline();
            return;
        }

        for (int i = 0; i < humanTanks.Count; i++)
        {
            Transform tank = humanTanks[i];
            var p = new Participant
            {
                name = humanTanks.Count > 1 ? $"Player {i + 1}" : "You",
                color = HumanColors[i % HumanColors.Length],
                human = true,
                tank = tank,
                target = tank.GetComponent<Target>(),
                drive = tank.GetComponent<TowerController>(),
                gun = tank.GetComponent<ShootingControls>(),
                flip = tank.GetComponent<TankFlip>(),
            };
            SetHumanControl(p, false);
            _humans.Add(p);
        }

        // Hooked here, not in OnEnable: the component also sits in the scene during
        // real-time play, which must keep its plain explosions and reload-on-death.
        HookRules();
        CreatePresentation();
        StartCoroutine(Run());
    }

    private void HookRules()
    {
        Projectile.Exploded += OnProjectileExploded;
        Target.HumanTankDestroyed = OnHumanTankDestroyed;
        _hooked = true;
    }

    private void Awake()
    {
        Instance = this;
    }

    private void OnEnable()
    {
        _readyAction = new InputAction("TurnReady", InputActionType.Button, "<XRController>{RightHand}/primaryButton");
        _readyAction.Enable();
    }

    private void OnDisable()
    {
        _readyAction?.Disable();
        _readyAction?.Dispose();
        _readyAction = null;
        Projectile.Exploded -= OnProjectileExploded;
        if (_hooked)
            Target.HumanTankDestroyed = null;
        _hooked = false;
        Projectile.Wind = Vector3.zero;
        if (_sync != null)
            _sync.State.OnValueChanged -= OnTurnStateChanged;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        foreach (Object o in _runtimeObjects)
        {
            if (o != null)
                Destroy(o);
        }
    }

    private IEnumerator Run()
    {
        // Enemies spawn as placeholders that pick their variant on Start; GameConfigManager
        // puts them on the NavMesh. Take over once that's done.
        while (GameConfigManager.Instance == null || !GameConfigManager.Instance.EnemiesReady)
            yield return null;

        int aiIndex = 0;
        foreach (EnemyTankAI ai in FindObjectsByType<EnemyTankAI>(FindObjectsSortMode.InstanceID))
        {
            ai.TurnBasedControl = true;
            _ais.Add(new Participant
            {
                name = $"AI {++aiIndex}",
                color = AiColor,
                tank = ai.transform,
                target = ai.GetComponent<Target>(),
                ai = ai,
            });
        }

        foreach (Participant p in _humans) p.nameTag = CreateText(p.name, p.color, 0.025f);
        foreach (Participant p in _ais) p.nameTag = CreateText(p.name, p.color, 0.02f);

        int humanCursor = 0, aiCursor = 0;
        while (!_gameOver)
        {
            if (CheckGameOver()) yield break;
            yield return PlayTurn(NextAlive(_humans, ref humanCursor));

            if (CheckGameOver()) yield break;
            yield return PlayTurn(NextAlive(_ais, ref aiCursor));
        }
    }

    private IEnumerator PlayTurn(Participant p)
    {
        if (p == null)
            yield break;

        RollWind();
        _active = p;
        _lastShot = null;

        if (p.human)
            yield return HumanTurn(p);
        else
            yield return AiTurn(p);

        _timeLeft = 0f;
        _resolving = true;
        yield return ResolveShots(p);
        _resolving = false;
        _active = null;

        if (_navMeshDirty && GameConfigManager.Instance != null)
        {
            _navMeshDirty = false;
            AsyncOperation refresh = GameConfigManager.Instance.RefreshRuntimeNavMesh();
            if (refresh != null)
                yield return refresh;
        }

        yield return new WaitForSeconds(pauseBetweenTurns);
    }

    private IEnumerator HumanTurn(Participant p)
    {
        if (rigFollow != null)
        {
            rigFollow.SetTarget(p.tank);
            rigFollow.SnapBehindTarget();
        }

        _timeLeft = humanTurnSeconds;
        if (_humans.Count > 1)
        {
            // Hot-seat hand-off: the clock doesn't start until the next player is ready.
            ShowBanner($"{p.name}'s turn\n<size=60%>Pass the headset, then press A</size>", p.color);
            while (p.Alive && !_readyAction.WasPressedThisFrame())
                yield return null;
            // The press that ended the hand-off must not also end the scouting.
            yield return null;
        }

        yield return Scout(p);

        ShowBanner(_humans.Count > 1 ? $"{p.name}: go!" : "Your turn", p.color);
        yield return new WaitForSeconds(0.8f);
        HideBanner();
        if (!p.Alive)
            yield break;

        SetHumanControl(p, true);
        p.drive.DriveBudget = humanFuel;
        p.gun.ShotsRemaining = 1;

        Projectile fired = null;
        System.Action<Projectile> onShot = projectile => fired = projectile;
        p.gun.ShotFired += onShot;

        while (p.Alive && fired == null && _timeLeft > 0f)
        {
            _timeLeft -= Time.deltaTime;
            UpdateAimPreview(p);
            yield return null;
        }

        if (p.gun != null)
            p.gun.ShotFired -= onShot;
        SetHumanControl(p, false);
        _aimPreview.enabled = false;
        _lastShot = fired;
    }

    /// <summary>
    /// Before a human turn: fly freely around the map (PlayerRigFollow.FreeLook) for up to
    /// <see cref="scoutSeconds"/>, or until A, then snap back behind the tank. The tank's own
    /// controls stay off meanwhile — they share the sticks.
    /// </summary>
    private IEnumerator Scout(Participant p)
    {
        if (scoutSeconds <= 0f || rigFollow == null || !p.Alive)
            yield break;

        _scouting = true;
        _scoutLeft = scoutSeconds;
        rigFollow.SetTarget(null);
        rigFollow.FreeLook = true;

        bool placed = false;
        while (p.Alive && _scoutLeft > 0f && !_readyAction.WasPressedThisFrame())
        {
            _scoutLeft -= Time.deltaTime;
            ShowBanner($"Look around  {Mathf.CeilToInt(_scoutLeft)}\n<size=55%>Left stick: fly   Right stick: turn / up-down   A: start turn</size>",
                p.color, reposition: !placed || !BannerInView());
            placed = true;
            yield return null;
        }

        rigFollow.FreeLook = false;
        _scouting = false;
        HideBanner();
        if (p.Alive)
        {
            rigFollow.ResetHeight();
            rigFollow.SetTarget(p.tank);
            rigFollow.SnapBehindTarget();
        }
    }

    /// <summary>Whether the banner is still comfortably in front of the player (it only moves when it isn't).</summary>
    private bool BannerInView()
    {
        Camera cam = Camera.main;
        if (cam == null)
            return true;
        Vector3 toBanner = _banner.transform.position - cam.transform.position;
        return toBanner.magnitude < 2.5f && Vector3.Angle(cam.transform.forward, toBanner) < 35f;
    }

    private IEnumerator AiTurn(Participant p)
    {
        if (rigFollow != null)
        {
            rigFollow.SetTarget(p.tank);
            rigFollow.SnapBehindTarget(2f);
        }

        Transform target = NearestHumanTank(p.tank.position);
        yield return p.ai.TakeTurn(target, aiFuel, aiMaxDriveSeconds);

        if (p.tank == null)
            yield break;
        foreach (Projectile projectile in FindObjectsByType<Projectile>(FindObjectsSortMode.None))
        {
            if (projectile.shooter == p.tank)
                _lastShot = projectile;
        }
    }

    /// <summary>Waits until every live projectile is gone, jumping the camera to watch the shot land.</summary>
    private IEnumerator ResolveShots(Participant shooter)
    {
        if (_lastShot == null)
            yield break;

        if (rigFollow != null)
            rigFollow.SetTarget(null);

        Vector3 shooterPosition = shooter.tank != null ? shooter.tank.position : _lastShot.transform.position;
        Vector3 lastSeen = _lastShot.transform.position;
        bool viewed = false;
        float start = Time.time;

        while (Time.time - start < maxResolveSeconds && AnyLiveProjectile())
        {
            if (_lastShot != null)
                lastSeen = _lastShot.transform.position;

            // Explosive shots land and vanish in the same frame, so a missing shot also counts as landed.
            bool landed = _lastShot == null || _lastShot.HasImpacted;
            if (!viewed && landed)
            {
                AnnounceLanding(lastSeen, shooterPosition);
                viewed = true;
            }
            yield return null;
        }

        if (!viewed)
            AnnounceLanding(lastSeen, shooterPosition);

        // Let the explosion play out before the next turn moves the camera.
        yield return new WaitForSeconds(1.2f);
    }

    /// <summary>Shows everyone where a shot landed (online through the sync, offline directly).</summary>
    private void AnnounceLanding(Vector3 point, Vector3 shooterPosition)
    {
        if (GameSession.IsOnline && _sync != null)
            _sync.ShotLandedRpc(point, shooterPosition);
        else
            ViewLanding(point, shooterPosition);
    }

    public void ViewLanding(Vector3 point, Vector3 shooterPosition)
    {
        if (rigFollow == null || Camera.main == null)
            return;

        Vector3 head = Camera.main.transform.position;
        float distance = new Vector2(point.x - head.x, point.z - head.z).magnitude;
        if (distance > viewShotDistance * 1.5f)
            rigFollow.ViewFrom(point, shooterPosition - point, viewShotDistance);
    }

    private void OnProjectileExploded(Projectile projectile, Vector3 position)
    {
        if (_environment != null)
        {
            _environment.DigCrater(position, craterRadius, craterDepth);
            _navMeshDirty = true;
            if (GameSession.IsOnline && _sync != null)
                _sync.CraterRpc(position, craterRadius, craterDepth);
        }

        foreach (Target target in FindObjectsByType<Target>(FindObjectsSortMode.None))
        {
            if (!target.IsAlive)
                continue;

            Vector3 closest = ClosestPoint(target, position);
            float distance = Vector3.Distance(closest, position);
            if (distance >= blastRadius)
                continue;

            float falloff = 1f - distance / blastRadius;
            Vector3 push = target.transform.position - position;
            push.y = 0f;
            push = push.sqrMagnitude > 0.0001f ? push.normalized * (blastKnockback * falloff) : Vector3.zero;
            if (target.TryGetComponent(out EnemyTankAI enemy))
                enemy.ApplyKnockback(push);
            else if (target.TryGetComponent(out TowerController player))
                player.ApplyKnockback(push);

            target.TakeDamage(Mathf.CeilToInt(projectile.damage * falloff), closest);
        }

        // Physics doesn't wake sleeping bodies when the ground under them changes.
        foreach (Collider c in Physics.OverlapSphere(position, craterRadius * 2f))
        {
            if (c.attachedRigidbody != null)
                c.attachedRigidbody.WakeUp();
        }
    }

    private static Vector3 ClosestPoint(Target target, Vector3 position)
    {
        Collider col = target.GetComponentInChildren<Collider>();
        if (col == null || (col is MeshCollider mesh && !mesh.convex))
            return target.transform.position;
        return col.ClosestPoint(position);
    }

    private void OnHumanTankDestroyed(Target target)
    {
        // Target destroys the wreck itself; the turn loop notices via Participant.Alive.
        foreach (Participant p in _humans)
        {
            if (p.target == target && p.nameTag != null)
                p.nameTag.text = $"{p.name}\n<size=70%>destroyed</size>";
        }
    }

    private bool CheckGameOver()
    {
        if (_gameOver)
            return true;

        if (!AnyAlive(_ais))
        {
            // GameConfigManager's win condition reloads the round.
            _gameOver = true;
            ShowBanner(_humans.Count > 1 ? "Victory!\n<size=60%>The players win</size>" : "Victory!", HumanColors[2]);
            return true;
        }

        if (!AnyAlive(_humans))
        {
            _gameOver = true;
            ShowBanner("Defeat\n<size=60%>The AI tanks win</size>", HumanColors[0]);
            StartCoroutine(ReloadAfterDefeat());
            return true;
        }

        return false;
    }

    private IEnumerator ReloadAfterDefeat()
    {
        yield return new WaitForSecondsRealtime(3f);
        if (GameConfigManager.Instance != null)
            GameConfigManager.Instance.ReloadScene();
    }

    private void RollWind()
    {
        float angle = Random.Range(0f, Mathf.PI * 2f);
        float strength = Random.Range(0f, maxWind);
        Projectile.Wind = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * strength;
    }

    private static void SetHumanControl(Participant p, bool enabled)
    {
        if (p.drive != null) p.drive.enabled = enabled && p.Alive;
        if (p.gun != null) p.gun.enabled = enabled && p.Alive;
        if (p.flip != null) p.flip.enabled = enabled && p.Alive;
    }

    private static Participant NextAlive(List<Participant> team, ref int cursor)
    {
        for (int i = 0; i < team.Count; i++)
        {
            Participant p = team[(cursor + i) % team.Count];
            if (p.Alive)
            {
                cursor = (cursor + i + 1) % team.Count;
                return p;
            }
        }
        return null;
    }

    private static bool AnyAlive(List<Participant> team)
    {
        foreach (Participant p in team)
        {
            if (p.Alive)
                return true;
        }
        return false;
    }

    private Transform NearestHumanTank(Vector3 from)
    {
        Transform best = null;
        float bestDistance = float.MaxValue;
        foreach (Participant p in _humans)
        {
            if (!p.Alive) continue;
            float d = (p.tank.position - from).sqrMagnitude;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = p.tank;
            }
        }
        return best;
    }

    private static bool AnyLiveProjectile()
    {
        foreach (Projectile projectile in FindObjectsByType<Projectile>(FindObjectsSortMode.None))
        {
            // Skip the kinematic preview copy that floats over the player tank.
            Rigidbody body = projectile.GetComponent<Rigidbody>();
            if (body == null || !body.isKinematic)
                return true;
        }
        return false;
    }

    // ---------- Online ----------

    private void BeginOnline()
    {
        CreatePresentation();
        if (GameSession.HasAuthority)
        {
            HookRules();
            if (onlineSyncPrefab == null)
            {
                Debug.LogError("[Online] TurnBasedMatch has no online sync prefab; rebuild the VR scenes.");
                return;
            }

            // AttachSync is called back from the sync's OnNetworkSpawn.
            Instantiate(onlineSyncPrefab).GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
            StartCoroutine(RunOnlineHost());
        }
        else
        {
            StartCoroutine(ReportReadyWhenBuilt());
        }
    }

    /// <summary>Called by <see cref="OnlineMatchSync"/> when it spawns on this headset.</summary>
    public void AttachSync(OnlineMatchSync sync)
    {
        // The sync can arrive before this headset finished building its world (and Begin).
        CreatePresentation();
        _sync = sync;
        _sync.State.OnValueChanged += OnTurnStateChanged;
        OnTurnStateChanged(default, _sync.State.Value);
    }

    private IEnumerator ReportReadyWhenBuilt()
    {
        while (_sync == null || _environment == null || !_environment.IsWorldReady)
            yield return null;
        _sync.ClientReadyRpc();
    }

    /// <summary>Host only: may <paramref name="tank"/> fire right now?</summary>
    public bool CanFire(Transform tank)
    {
        return GameSession.IsOnline && !_gameOver && !_shotAccepted
               && _active != null && _active.tank == tank && _state.Phase == TurnPhase.Playing;
    }

    /// <summary>Host only: a validated shot from the active tank has been spawned.</summary>
    public void OnShotFired(Transform tank, Projectile projectile)
    {
        _shotAccepted = true;
        _lastShot = projectile;
    }

    private delegate void StateEdit(ref TurnState state);

    private void EditState(StateEdit edit)
    {
        TurnState state = _sync.State.Value;
        edit(ref state);
        _sync.State.Value = state; // also raises OnTurnStateChanged here on the host
    }

    private IEnumerator RunOnlineHost()
    {
        while (_sync == null)
            yield return null;

        EditState((ref TurnState st) => { st.Phase = TurnPhase.Waiting; st.ActiveIndex = -1; });

        // Every headset builds the map itself; don't start until they have (or give up waiting).
        float waitStart = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - waitStart < clientReadyTimeout && !AllHeadsetsReady())
            yield return null;
        while (GameConfigManager.Instance == null || !GameConfigManager.Instance.EnemiesReady)
            yield return null;

        foreach (EnemyTankAI ai in FindObjectsByType<EnemyTankAI>(FindObjectsSortMode.None))
            ai.TurnBasedControl = true;
        RefreshOnlineParticipants();

        int humanCursor = 0, aiCursor = 0;
        while (!_gameOver)
        {
            if (CheckOnlineGameOver()) yield break;
            yield return PlayOnlineTurn(NextAlive(_humans, ref humanCursor));

            if (_ais.Count == 0)
                continue; // free-for-all: humans only
            if (CheckOnlineGameOver()) yield break;
            yield return PlayOnlineTurn(NextAlive(_ais, ref aiCursor));
        }
    }

    private bool AllHeadsetsReady()
    {
        NetworkManager manager = NetworkManager.Singleton;
        foreach (ulong id in GameSession.OnlineRoster)
        {
            bool connected = manager != null && manager.ConnectedClientsIds.Contains(id);
            if (connected && !_sync.ReadyClients.Contains(id))
                return false;
        }
        return true;
    }

    private IEnumerator PlayOnlineTurn(Participant p)
    {
        if (p == null)
            yield break;

        RollWind();
        _lastShot = null;
        _shotAccepted = false;
        _active = p;
        int participantCount = _humans.Count + _ais.Count;
        EditState((ref TurnState st) =>
        {
            st.Turn++;
            st.ActiveIndex = p.index;
            st.Phase = TurnPhase.Starting;
            st.SecondsLeft = Mathf.CeilToInt(humanTurnSeconds);
            st.Wind = Projectile.Wind;
            st.ParticipantCount = participantCount;
        });
        yield return new WaitForSeconds(1.2f);

        EditState((ref TurnState st) => st.Phase = TurnPhase.Playing);
        if (p.human)
        {
            float timeLeft = humanTurnSeconds;
            while (p.Alive && !_shotAccepted && timeLeft > 0f)
            {
                timeLeft -= Time.deltaTime;
                int seconds = Mathf.CeilToInt(Mathf.Max(0f, timeLeft));
                if (seconds != _state.SecondsLeft)
                    EditState((ref TurnState st) => st.SecondsLeft = seconds);
                yield return null;
            }
        }
        else if (p.ai != null)
        {
            yield return p.ai.TakeTurn(NearestHumanTank(p.tank.position), aiFuel, aiMaxDriveSeconds);
            if (p.tank != null)
            {
                foreach (Projectile projectile in FindObjectsByType<Projectile>(FindObjectsSortMode.None))
                {
                    if (projectile.shooter == p.tank)
                        _lastShot = projectile;
                }
            }
        }

        EditState((ref TurnState st) => st.Phase = TurnPhase.Resolving);
        yield return ResolveShots(p);

        if (_navMeshDirty && GameConfigManager.Instance != null)
        {
            _navMeshDirty = false;
            AsyncOperation refresh = GameConfigManager.Instance.RefreshRuntimeNavMesh();
            if (refresh != null)
                yield return refresh;
        }

        yield return new WaitForSeconds(pauseBetweenTurns);
    }

    private bool CheckOnlineGameOver()
    {
        if (_gameOver)
            return true;

        string message = null;
        if (_ais.Count > 0)
        {
            if (!AnyAlive(_ais))
                message = "Victory!\n<size=60%>The players win</size>";
            else if (!AnyAlive(_humans))
                message = "Defeat\n<size=60%>The AI tanks win</size>";
        }
        else
        {
            // Free-for-all: last tank standing.
            Participant survivor = null;
            int alive = 0;
            foreach (Participant p in _humans)
            {
                if (!p.Alive) continue;
                alive++;
                survivor = p;
            }
            if (alive == 0)
                message = "Draw";
            else if (alive == 1 && _humans.Count > 1)
                message = $"{survivor.name} wins!";
        }

        if (message == null)
            return false;

        _gameOver = true;
        EditState((ref TurnState st) =>
        {
            st.Phase = TurnPhase.GameOver;
            st.ActiveIndex = -1;
            st.Message = message;
        });
        StartCoroutine(RestartAfterGameOver());
        return true;
    }

    private IEnumerator RestartAfterGameOver()
    {
        yield return new WaitForSecondsRealtime(gameOverSeconds);
        if (GameConfigManager.Instance != null)
            GameConfigManager.Instance.ReloadScene(); // online host: next round for everyone
    }

    /// <summary>
    /// Builds the participant lists from the network-spawned tanks (every headset has them;
    /// the host assigned each its turn-order index before spawning).
    /// </summary>
    private void RefreshOnlineParticipants()
    {
        foreach (NetworkTarget networkTarget in FindObjectsByType<NetworkTarget>(FindObjectsSortMode.None))
        {
            int index = networkTarget.ParticipantIndex.Value;
            if (index < 0 || FindParticipant(index) != null)
                continue;

            Transform tank = networkTarget.transform;
            bool human = tank.TryGetComponent(out ShootingControls gun);
            var p = new Participant
            {
                index = index,
                human = human,
                local = human && networkTarget.IsOwner,
                tank = tank,
                target = tank.GetComponent<Target>(),
                ai = tank.GetComponent<EnemyTankAI>(),
                drive = tank.GetComponent<TowerController>(),
                gun = gun,
                flip = tank.GetComponent<TankFlip>(),
            };

            if (human)
            {
                p.name = p.local ? "You" : $"Player {index + 1}";
                p.color = HumanColors[index % HumanColors.Length];
                _humans.Add(p);
            }
            else
            {
                p.name = $"AI {index - GameSession.OnlineRoster.Count + 1}";
                p.color = AiColor;
                _ais.Add(p);
            }
            p.nameTag = CreateText(p.name, p.color, human ? 0.025f : 0.02f);

            // First sight of our own tank: put the camera behind it.
            if (p.local && rigFollow != null && _state.ActiveIndex < 0)
            {
                rigFollow.SetTarget(p.tank);
                rigFollow.SnapBehindTarget();
            }
        }

        _humans.Sort((a, b) => a.index.CompareTo(b.index));
        _ais.Sort((a, b) => a.index.CompareTo(b.index));
    }

    private Participant FindParticipant(int index)
    {
        foreach (Participant p in _humans) if (p.index == index) return p;
        foreach (Participant p in _ais) if (p.index == index) return p;
        return null;
    }

    /// <summary>Every headset (host included): present the host's turn state.</summary>
    private void OnTurnStateChanged(TurnState previous, TurnState current)
    {
        _state = current;
        if (!GameSession.HasAuthority)
            Projectile.Wind = current.Wind; // display only; physics runs on the host

        RefreshOnlineParticipants();
        Participant active = FindParticipant(current.ActiveIndex);

        if (current.Turn != previous.Turn && active != null)
        {
            _active = active;
            if (rigFollow != null && active.tank != null)
            {
                rigFollow.SetTarget(active.tank);
                rigFollow.SnapBehindTarget(active.human ? -1f : 2f);
            }
            ShowBanner(active.local ? "Your turn" : $"{active.name}'s turn", active.color);
            if (_bannerTimeout != null)
                StopCoroutine(_bannerTimeout);
            _bannerTimeout = StartCoroutine(HideBannerAfter(1.2f));
        }

        // Our own tank's controls are live exactly while the host says it's our turn.
        bool ourTurn = current.Phase == TurnPhase.Playing && active != null && active.local && active.Alive;
        if (ourTurn && _controlled == null)
        {
            _controlled = active;
            SetHumanControl(active, true);
            active.drive.DriveBudget = humanFuel;
            active.gun.ShotsRemaining = 1;
        }
        else if (!ourTurn && _controlled != null)
        {
            SetHumanControl(_controlled, false);
            _controlled = null;
            _aimPreview.enabled = false;
        }

        if (current.Phase == TurnPhase.Resolving && previous.Phase != TurnPhase.Resolving && rigFollow != null)
            rigFollow.SetTarget(null);
        _resolving = current.Phase == TurnPhase.Resolving;

        if (current.Phase == TurnPhase.GameOver && previous.Phase != TurnPhase.GameOver)
        {
            _gameOver = true;
            _active = null;
            ShowBanner(current.Message.ToString(), HumanColors[2]);
        }
    }

    private IEnumerator HideBannerAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (!_gameOver)
            HideBanner();
        _bannerTimeout = null;
    }

    // ---------- Presentation ----------

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null || _hud == null)
            return;

        foreach (Participant p in _humans) PlaceTag(p, cam);
        foreach (Participant p in _ais) PlaceTag(p, cam);

        bool showHud = _active != null && _active.Alive && !_gameOver;
        _hud.gameObject.SetActive(showHud);
        _windArrow.enabled = showHud;
        if (!showHud)
            return;

        Vector3 anchor = _active.tank.position + Vector3.up * 0.32f;
        _hud.transform.position = anchor;
        Billboard(_hud.transform, cam);

        int seconds = GameSession.IsOnline ? _state.SecondsLeft : Mathf.CeilToInt(Mathf.Max(0f, _timeLeft));
        bool showFuel = _active.drive != null && (!GameSession.IsOnline || _active.local);
        string status = _scouting
            ? $"scouting {Mathf.CeilToInt(Mathf.Max(0f, _scoutLeft))}s"
            : _resolving
            ? "shot fired"
            : !_active.human
                ? "aiming..."
                : showFuel
                    ? $"{seconds}s   Fuel {Mathf.RoundToInt(100f * Mathf.Clamp01(_active.drive.DriveBudget / humanFuel))}%"
                    : $"{seconds}s";
        if (GameSession.IsOnline && _state.Phase == TurnPhase.Starting)
            status = "get ready";
        _hud.text = $"<color=#{ColorUtility.ToHtmlStringRGB(_active.color)}>{_active.name}</color>\n<size=70%>{status}\nWind {Projectile.Wind.magnitude:F1}</size>";
        _hud.color = Color.white;

        DrawWindArrow(anchor + Vector3.down * 0.1f);

        if (GameSession.IsOnline && _controlled != null)
            UpdateAimPreview(_controlled);
    }

    private void PlaceTag(Participant p, Camera cam)
    {
        if (p.nameTag == null)
            return;

        bool show = p.tank != null && p != _active;
        p.nameTag.gameObject.SetActive(show);
        if (!show)
            return;

        p.nameTag.transform.position = p.tank.position + Vector3.up * 0.2f;
        Billboard(p.nameTag.transform, cam);
    }

    private void DrawWindArrow(Vector3 center)
    {
        Vector3 wind = Projectile.Wind;
        float length = 0.25f * Mathf.Clamp01(wind.magnitude / Mathf.Max(0.01f, maxWind));
        if (length < 0.01f)
        {
            _windArrow.enabled = false;
            return;
        }

        Vector3 direction = wind.normalized;
        Vector3 side = Vector3.Cross(Vector3.up, direction) * 0.03f;
        Vector3 tail = center - direction * length * 0.5f;
        Vector3 tip = center + direction * length * 0.5f;
        Vector3 back = tip - direction * 0.04f;
        _windArrow.positionCount = 5;
        _windArrow.SetPosition(0, tail);
        _windArrow.SetPosition(1, tip);
        _windArrow.SetPosition(2, back + side);
        _windArrow.SetPosition(3, tip);
        _windArrow.SetPosition(4, back - side);
    }

    private void UpdateAimPreview(Participant p)
    {
        Transform firePoint = p.gun != null ? p.gun.FirePoint : null;
        if (firePoint == null)
        {
            _aimPreview.enabled = false;
            return;
        }

        Vector3 position = firePoint.position;
        Vector3 velocity = firePoint.forward * p.gun.CurrentLaunchSpeed;
        float step = aimPreviewSeconds / (_aimPoints.Length - 1);
        for (int i = 0; i < _aimPoints.Length; i++)
        {
            float t = i * step;
            _aimPoints[i] = position + velocity * t + 0.5f * Physics.gravity * t * t;
        }

        _aimPreview.positionCount = _aimPoints.Length;
        _aimPreview.SetPositions(_aimPoints);
        _aimPreview.sharedMaterial = ColoredMaterial(p.color);
        _aimPreview.enabled = true;
    }

    /// <param name="reposition">False keeps the banner where it is (just updates the text), so it isn't head-locked.</param>
    private void ShowBanner(string message, Color color, bool reposition = true)
    {
        Camera cam = Camera.main;
        if (cam == null)
            return;

        if (reposition)
        {
            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            _banner.transform.position = cam.transform.position + forward.normalized * 1.2f + Vector3.up * 0.1f;
        }
        Billboard(_banner.transform, cam);
        _banner.text = message;
        _banner.color = color;
        _banner.gameObject.SetActive(true);
    }

    private void HideBanner()
    {
        _banner.gameObject.SetActive(false);
    }

    private void CreatePresentation()
    {
        if (_hud != null)
            return;

        _hud = CreateText("", Color.white, 0.03f);
        _banner = CreateText("", Color.white, 0.07f);
        _banner.gameObject.SetActive(false);
        _windArrow = CreateLine("Wind Arrow", WindColor, 0.006f);
        _aimPreview = CreateLine("Aim Preview", Color.white, 0.004f);
        _aimPreview.enabled = false;
    }

    /// <summary>World-space text; <paramref name="lineHeight"/> in meters.</summary>
    private TextMeshPro CreateText(string text, Color color, float lineHeight)
    {
        var go = new GameObject("Turn Text");
        go.transform.SetParent(transform, false);
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.color = color;
        // TextMeshPro (non-UI) renders roughly 0.1 m per font-size point at unit scale.
        tmp.fontSize = lineHeight * 10f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.outlineWidth = 0.2f;
        tmp.outlineColor = new Color32(0, 0, 0, 200);
        tmp.rectTransform.sizeDelta = new Vector2(2f, 0.5f);
        return tmp;
    }

    private LineRenderer CreateLine(string lineName, Color color, float width)
    {
        var go = new GameObject(lineName);
        go.transform.SetParent(transform, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.widthMultiplier = width;
        line.numCapVertices = 2;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = ColoredMaterial(color);
        return line;
    }

    private readonly Dictionary<Color, Material> _materials = new Dictionary<Color, Material>();

    private Material ColoredMaterial(Color color)
    {
        if (_materials.TryGetValue(color, out Material material))
            return material;

        material = unlitMaterial != null ? new Material(unlitMaterial) : new Material(Shader.Find("Sprites/Default"));
        material.color = color;
        _materials[color] = material;
        _runtimeObjects.Add(material);
        return material;
    }

    private static void Billboard(Transform t, Camera cam)
    {
        Vector3 away = t.position - cam.transform.position;
        if (away.sqrMagnitude > 0.0001f)
            t.rotation = Quaternion.LookRotation(away, Vector3.up);
    }
}
