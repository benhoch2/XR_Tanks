using System.Collections;
using System.Collections.Generic;
using TMPro;
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

    private TextMeshPro _hud;
    private TextMeshPro _banner;
    private LineRenderer _windArrow;
    private LineRenderer _aimPreview;
    private readonly Vector3[] _aimPoints = new Vector3[16];

    /// <summary>Called by TerrainEnvironment once the round's tanks are spawned.</summary>
    public void Begin(TerrainEnvironment environment, List<Transform> humanTanks)
    {
        _environment = environment;

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
        Projectile.Exploded += OnProjectileExploded;
        Target.HumanTankDestroyed = OnHumanTankDestroyed;

        CreatePresentation();
        StartCoroutine(Run());
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
        if (_humans.Count > 0)
            Target.HumanTankDestroyed = null;
        Projectile.Wind = Vector3.zero;
    }

    private void OnDestroy()
    {
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
        }
        else
        {
            ShowBanner("Your turn", p.color);
            yield return new WaitForSeconds(1f);
        }
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
                ViewLanding(lastSeen, shooterPosition);
                viewed = true;
            }
            yield return null;
        }

        if (!viewed)
            ViewLanding(lastSeen, shooterPosition);

        // Let the explosion play out before the next turn moves the camera.
        yield return new WaitForSeconds(1.2f);
    }

    private void ViewLanding(Vector3 point, Vector3 shooterPosition)
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

        string status = _resolving
            ? "shot fired"
            : _active.human
                ? $"{Mathf.CeilToInt(Mathf.Max(0f, _timeLeft))}s   Fuel {Mathf.RoundToInt(100f * Mathf.Clamp01(_active.drive.DriveBudget / humanFuel))}%"
                : "aiming...";
        _hud.text = $"<color=#{ColorUtility.ToHtmlStringRGB(_active.color)}>{_active.name}</color>\n<size=70%>{status}\nWind {Projectile.Wind.magnitude:F1}</size>";
        _hud.color = Color.white;

        DrawWindArrow(anchor + Vector3.down * 0.1f);
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

    private void ShowBanner(string message, Color color)
    {
        Camera cam = Camera.main;
        if (cam == null)
            return;

        Vector3 forward = cam.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        _banner.transform.position = cam.transform.position + forward.normalized * 1.2f + Vector3.up * 0.1f;
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
