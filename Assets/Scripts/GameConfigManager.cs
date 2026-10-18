using System.Collections;
using System.Collections.Generic;
using Meta.XR.ImmersiveDebugger;
using Meta.XR.ImmersiveDebugger.UserInterface;
using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

public class GameConfigManager : MonoBehaviour
{
    public static GameConfigManager Instance { get; private set; }

    [Header("Spawning")]
    [DebugMember(Min = 1, Max = 50, Category = "Game Config")]
    public int numberOfEnemies = 5;

    [DebugMember(Min = 1, Max = 50, Category = "Game Config")]
    public int numberOfCrates = 10;

    [Header("Projectile")]
    [DebugMember(Min = 1, Max = 50, Category = "Game Config")]
    public int projectileMinSpeed = 5;

    [DebugMember(Min = 1, Max = 100, Category = "Game Config")]
    public int projectileMaxSpeed = 20;

    [Header("Power")]
    [DebugMember(Min = 1, Max = 10, Category = "Game Config")]
    public int powerUpDuration = 2;

    [DebugMember(Category = "Game Config")]
    public bool pauseWhenConfigMenuOpen = true;

    [Header("NavMesh")]
    [Tooltip("Layers to include when building the runtime NavMesh from the environment's colliders " +
             "(MRUK room or VR terrain). " +
             "Default = the Default layer only. If obstacle prefabs are placed on Walls, " +
             "Obstacles, or any other layer, add those layers here or they'll be missing " +
             "from the NavMesh and enemies will walk through them.")]
    [SerializeField]
    private LayerMask navMeshLayerMask = 1; // bit 0 = Default layer

    [Header("Testing")]
    [DebugMember(Category = "Testing")]
    public bool enemyShootingEnabled = true;

    [DebugMember(Category = "Testing")]
    public bool playerInvulnerable = false;

    // VR scene only (read by PlayerRigFollow / TerrainEnvironment). Kept here rather than on
    // those scene components so the values survive the scene reload after every round.
    [Header("VR")]
    [Tooltip("Comfort mode: the camera rig jumps to keep up with the tank instead of gliding.")]
    [DebugMember(Category = "VR")]
    public bool vrSnapFollow = false;

    [Tooltip("Generate a new mountain map every round. Turn off to keep replaying the current seed.")]
    [DebugMember(Category = "VR")]
    public bool vrNewTerrainEachRound = true;

    [Tooltip("Seed of the current mountain map. Written each round while vrNewTerrainEachRound is on.")]
    [DebugMember(Min = 0, Max = 99999, Category = "VR")]
    public int vrTerrainSeed = 1;

    [Header("Controls — Right")]
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "R Trigger")]
    public string ctrlRTrigger = "Fire (hold to charge)";
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "A Button")]
    public string ctrlA = "Cycle projectile";
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "B Button (hold 1s)")]
    public string ctrlB = "Reset / flip tank";
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "R Stick X")]
    public string ctrlRStickX = "Rotate turret";
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "R Stick Y")]
    public string ctrlRStickY = "Tilt turret";

    [Header("Controls — Left")]
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "Y Button")]
    public string ctrlY = "Open / close config menu";
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "L Stick Y")]
    public string ctrlLStickY = "Drive forward / back";
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "L Stick X")]
    public string ctrlLStickX = "Turn left / right";

    [Header("Controls — VR only")]
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "X Button (VR)")]
    public string ctrlX = "Recenter behind tank";
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "Grip (VR)")]
    public string ctrlGrip = "Hold + move hand to drag yourself";

    private const string MRSceneName = "SampleScene";
    private const string VRSceneName = "VRMountains";

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Snapshot inspector defaults BEFORE applying any persisted overrides so
        // RestoreDefaults() can reach them later. Then load whatever the user
        // saved on the previous run.
        CaptureDefaults();
        LoadPersistedConfig();

        SceneManager.sceneLoaded += OnSceneLoaded;
        _debugPanelCoroutine = StartCoroutine(SubscribeToDebugPanel());
    }

    void OnDestroy()
    {
        SaveConfig();
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_debugInterface != null)
        {
            _debugInterface.OnVisibilityChangedEvent -= OnDebugPanelVisibilityChanged;
        }
        Time.timeScale = 1f;
        if (_navMeshDataInstance.valid)
            _navMeshDataInstance.Remove();
    }

    void OnApplicationPause(bool paused)
    {
        // On Quest, the headset going to sleep / app being backgrounded fires
        // OnApplicationPause(true). Save here so settings survive even if the
        // OS later kills the app without a clean OnApplicationQuit.
        if (paused)
            SaveConfig();
    }

    void OnApplicationQuit()
    {
        SaveConfig();
    }

    private DebugInterface _debugInterface;
    private NavMeshDataInstance _navMeshDataInstance;
    private bool _sceneStartupTriggered;
    private GameObject _gameplayFloorCache;

    // Tracked coroutine handles so a scene reload can stop the previous run before
    // launching a fresh one. Without this, old coroutines kept running on the
    // DontDestroyOnLoad singleton and could touch destroyed scene objects.
    private Coroutine _debugPanelCoroutine;
    private Coroutine _spawnCoroutine;
    private Coroutine _enemyConfigCoroutine;
    private Coroutine _winSequenceCoroutine;

    // Win condition: count surviving enemy Targets (not crates, not the player).
    // Armed once after ConfigureEnemyTankMovementWhenReady finishes and the count
    // reflects all spawned enemies — prevents a stray early "0 enemies → win"
    // trigger before any have actually appeared.
    private int _enemiesAlive;
    private bool _winConditionArmed;

    /// <summary>
    /// Cached lookup for the gameplay "Floor" GameObject. The cache is invalidated
    /// when the referenced object is destroyed, so it survives scene reloads as long
    /// as a new "Floor" exists in the new scene.
    /// </summary>
    public GameObject GetGameplayFloor()
    {
        if (_gameplayFloorCache == null)
            _gameplayFloorCache = GameObject.Find("Floor");
        return _gameplayFloorCache;
    }

    private IEnumerator SubscribeToDebugPanel()
    {
        // Wait for the Immersive Debugger panel to be created
        // The panel may start inactive (hidden), so we must include inactive objects
        DebugInterface panel = null;
        for (int i = 0; i < 600; i++) // wait up to ~10 seconds
        {
            panel = FindAnyObjectByType<DebugInterface>(FindObjectsInactive.Include);
            if (panel != null) break;
            yield return null;
        }

        if (panel == null)
        {
            Debug.LogWarning("[GameConfig] DebugInterface not found, cannot auto-pause.");
            yield break;
        }

        _debugInterface = panel;
        _debugInterface.OnVisibilityChangedEvent += OnDebugPanelVisibilityChanged;
    }

    private void OnDebugPanelVisibilityChanged(Controller controller)
    {
        if (!pauseWhenConfigMenuOpen)
        {
            Time.timeScale = 1f;
            return;
        }

        Time.timeScale = controller.Visibility ? 0f : 1f;
    }

    void Start()
    {
        BeginSceneStartup();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ResetSceneStartupState();
        _debugPanelCoroutine = StartCoroutine(SubscribeToDebugPanel());
        BeginSceneStartup();
    }

    private void ResetSceneStartupState()
    {
        _sceneStartupTriggered = false;
        Time.timeScale = 1f;
        _gameplayFloorCache = null;
        _enemiesAlive = 0;
        _winConditionArmed = false;

        StopTrackedCoroutine(ref _debugPanelCoroutine);
        StopTrackedCoroutine(ref _spawnCoroutine);
        StopTrackedCoroutine(ref _enemyConfigCoroutine);
        StopTrackedCoroutine(ref _winSequenceCoroutine);

        if (_navMeshDataInstance.valid)
            _navMeshDataInstance.Remove();

        if (_debugInterface != null)
        {
            _debugInterface.OnVisibilityChangedEvent -= OnDebugPanelVisibilityChanged;
            _debugInterface = null;
        }
    }

    private void StopTrackedCoroutine(ref Coroutine handle)
    {
        if (handle != null)
        {
            StopCoroutine(handle);
            handle = null;
        }
    }

    private void BeginSceneStartup()
    {
        if (_sceneStartupTriggered)
            return;

        _sceneStartupTriggered = true;
        Time.timeScale = 1f;

        GameEnvironment environment = FindSceneEnvironment();
        ConfigureMetaXRRuntime(environment.SuppressBoundary);
        environment.ApplySpawnCounts(numberOfEnemies, numberOfCrates);
        _spawnCoroutine = StartCoroutine(RunSceneStartup(environment));
    }

    /// <summary>
    /// The world is chosen by the scene, not by fields on this manager: the manager outlives
    /// scene switches, so its own serialized state comes from whichever scene booted first.
    /// </summary>
    private static GameEnvironment FindSceneEnvironment()
    {
        GameEnvironment environment = FindAnyObjectByType<GameEnvironment>();
        if (environment != null)
            return environment;

        // SampleScene predates the environment split and has no GameEnvironment. Created in
        // the active scene, so it is destroyed with it like any other scene object.
        return new GameObject("MR Room Environment").AddComponent<MRRoomEnvironment>();
    }

    private IEnumerator RunSceneStartup(GameEnvironment environment)
    {
        // The environment's coroutines run on the environment itself so a scene unload
        // stops them along with it; this coroutine is stopped via _spawnCoroutine.
        yield return environment.StartCoroutine(environment.PrepareWorld());

        AsyncOperation navMeshBuild = BeginRuntimeNavMeshBuild(environment);
        if (navMeshBuild != null)
            yield return navMeshBuild;

        _enemyConfigCoroutine = StartCoroutine(ConfigureEnemyTankMovementWhenReady());

        yield return environment.StartCoroutine(environment.SpawnActors());
    }

    private void ConfigureMetaXRRuntime(bool suppressBoundary)
    {
#if UNITY_EDITOR
        Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
#endif

        foreach (var manager in FindObjectsByType<OVRManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            manager.shouldBoundaryVisibilityBeSuppressed = suppressBoundary;
        }
    }

    /// <summary>
    /// Starts an asynchronous NavMesh build over the environment's colliders. Async because a
    /// synchronous build stalls the frame, which in a headset shows as a frozen view.
    /// Returns null if there is nothing to build from.
    /// </summary>
    private AsyncOperation BeginRuntimeNavMeshBuild(GameEnvironment environment)
    {
        Transform sourceRoot = environment.NavMeshSourceRoot;
        List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(sourceRoot, navMeshLayerMask, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), sources);

        if (sources.Count == 0)
        {
            Debug.LogWarning("[NavMesh] No navmesh sources were collected.");
            return null;
        }

        Bounds bounds = environment.NavMeshBounds ?? ComputeColliderBounds(sourceRoot);

        if (_navMeshDataInstance.valid)
            _navMeshDataInstance.Remove();

        NavMeshBuildSettings settings = CreateTankNavMeshSettings();
        environment.TuneNavMesh(ref settings);

        NavMeshData data = new NavMeshData(settings.agentTypeID);
        _navMeshDataInstance = NavMesh.AddNavMeshData(data);
        return NavMeshBuilder.UpdateNavMeshDataAsync(data, settings, sources, bounds);
    }

    /// <summary>
    /// Tank-scale NavMesh settings every environment starts from before its TuneNavMesh.
    /// Public so the editor terrain validation builds the same NavMesh the game does.
    /// </summary>
    public static NavMeshBuildSettings CreateTankNavMeshSettings()
    {
        NavMeshBuildSettings settings = NavMesh.GetSettingsByIndex(0);
        settings.agentRadius = 0.12f;
        settings.agentHeight = 0.25f;
        settings.agentClimb = 0.2f;
        settings.agentSlope = 60f;
        settings.overrideVoxelSize = true;
        settings.voxelSize = 0.03f;
        settings.overrideTileSize = true;
        settings.tileSize = 64;
        return settings;
    }

    private static Bounds ComputeColliderBounds(Transform sourceRoot)
    {
        Bounds bounds = new Bounds(Vector3.zero, new Vector3(20f, 10f, 20f));
        bool hasBounds = false;

        IEnumerable<Collider> colliders = sourceRoot != null
            ? sourceRoot.GetComponentsInChildren<Collider>()
            : FindObjectsByType<Collider>(FindObjectsSortMode.None);

        foreach (var col in colliders)
        {
            if (!col.enabled || col.isTrigger)
                continue;

            if (!hasBounds)
            {
                bounds = col.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(col.bounds);
            }
        }
        bounds.Expand(new Vector3(1f, 1f, 1f));
        return bounds;
    }

    private IEnumerator ConfigureEnemyTankMovementWhenReady()
    {
        for (int i = 0; i < 180; i++)
        {
            bool foundEnemy = false;
            foreach (var target in FindObjectsByType<Target>(FindObjectsSortMode.None))
            {
                if (IsEnemy(target))
                {
                    foundEnemy = true;
                    break;
                }
            }

            if (foundEnemy)
                break;

            yield return null;
        }

        foreach (var target in FindObjectsByType<Target>(FindObjectsSortMode.None))
        {
            if (!IsEnemy(target))
                continue;

            Transform root = target.transform;

            Rotator rotator = target.GetComponentInChildren<Rotator>(true);
            if (rotator != null)
                rotator.enabled = false;

            bool foundNavPosition = NavMesh.SamplePosition(root.position, out NavMeshHit hit, 6f, NavMesh.AllAreas);

            if (!foundNavPosition)
            {
                GameObject gameplayFloor = GetGameplayFloor();
                if (gameplayFloor != null)
                {
                    Vector3 floorProbe = root.position;
                    floorProbe.y = gameplayFloor.transform.position.y + 0.05f;
                    foundNavPosition = NavMesh.SamplePosition(floorProbe, out hit, 6f, NavMesh.AllAreas);
                }
            }

            if (!foundNavPosition && Camera.main != null)
                foundNavPosition = NavMesh.SamplePosition(Camera.main.transform.position, out hit, 8f, NavMesh.AllAreas);

            if (!foundNavPosition)
            {
                Debug.LogWarning($"[NavMesh] Could not find navmesh position near enemy '{target.name}' at {root.position}");
                continue;
            }

            NavMeshAgent agent = root.GetComponent<NavMeshAgent>();
            if (agent == null)
            {
                Debug.LogWarning($"[NavMesh] Enemy '{target.name}' is missing a NavMeshAgent on the prefab.");
                continue;
            }

            // Per-tank stats (moveSpeed, stopRange, etc.) live on the EnemyTankAI prefab now,
            // so we read them off the AI to seed the agent rather than pushing global manager
            // values. This is what lets Light/Medium/Heavy variants behave differently.
            EnemyTankAI ai = root.GetComponent<EnemyTankAI>();
            if (ai == null)
                ai = root.gameObject.AddComponent<EnemyTankAI>();

            agent.enabled = true;
            agent.speed = ai.moveSpeed;
            agent.angularSpeed = 180f;
            agent.acceleration = 2f;
            agent.stoppingDistance = ai.stopRange;
            agent.autoBraking = true;
            agent.autoTraverseOffMeshLink = false;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
            agent.Warp(hit.position);

            ai.ResetBehaviorState();

            // Wire wheel animations to the moving enemy root so the wheels turn with movement.
            foreach (var wheel in target.GetComponentsInChildren<WheelRotation>(true))
            {
                wheel.tankBody = root;
                wheel.enabled = true;
            }
        }

        // Arm the win condition once enemies are fully configured. Counting here
        // (rather than during Target.Awake) avoids races between scene-reload
        // ordering and the singleton's lifecycle.
        _enemiesAlive = CountAliveEnemies();
        _winConditionArmed = _enemiesAlive > 0;
    }

    private int CountAliveEnemies()
    {
        int alive = 0;
        foreach (var t in FindObjectsByType<Target>(FindObjectsSortMode.None))
        {
            if (IsEnemy(t))
                alive++;
        }
        return alive;
    }

    /// <summary>
    /// An enemy tank: a Target with hit points (crates have none) that isn't the player
    /// (the player tank is the one carrying ShootingControls).
    /// </summary>
    private static bool IsEnemy(Target target)
    {
        return target != null
            && target.maxHitPoints > 0
            && target.GetComponent<ShootingControls>() == null;
    }

    public void OnEnemyKilled()
    {
        if (!_winConditionArmed)
            return;

        _enemiesAlive--;
        if (_enemiesAlive <= 0)
        {
            _winConditionArmed = false;
            _winSequenceCoroutine = StartCoroutine(WinSequenceReload());
        }
    }

    private IEnumerator WinSequenceReload()
    {
        // Brief celebration window before the scene reloads so the kill effect
        // on the final enemy can play out and the player feels the resolution.
        yield return new WaitForSecondsRealtime(2.5f);
        ReloadScene();
    }

    [DebugMember(Category = "Game Config")]
    public void ReloadScene()
    {
        ResetSceneStartupState();
        Scene active = SceneManager.GetActiveScene();
        SceneManager.LoadScene(active.name);
    }

    /// <summary>Toggles between the MR room scene and the VR mountain scene.</summary>
    [DebugMember(Category = "Game Config")]
    public void SwitchMRVR()
    {
        string target = SceneManager.GetActiveScene().name == VRSceneName ? MRSceneName : VRSceneName;
        if (!Application.CanStreamedLevelBeLoaded(target))
        {
            Debug.LogWarning($"[GameConfig] Scene '{target}' is not in the build settings; cannot switch.");
            return;
        }

        ResetSceneStartupState();
        SceneManager.LoadScene(target);
    }

    [DebugMember(Category = "Game Config")]
    public void RestoreDefaults()
    {
        numberOfEnemies = _defaults.numberOfEnemies;
        numberOfCrates = _defaults.numberOfCrates;
        projectileMinSpeed = _defaults.projectileMinSpeed;
        projectileMaxSpeed = _defaults.projectileMaxSpeed;
        powerUpDuration = _defaults.powerUpDuration;
        pauseWhenConfigMenuOpen = _defaults.pauseWhenConfigMenuOpen;
        enemyShootingEnabled = _defaults.enemyShootingEnabled;
        playerInvulnerable = _defaults.playerInvulnerable;
        vrSnapFollow = _defaults.vrSnapFollow;
        vrNewTerrainEachRound = _defaults.vrNewTerrainEachRound;
        vrTerrainSeed = _defaults.vrTerrainSeed;

        SaveConfig();

        Debug.Log("[GameConfig] Restored defaults. Hit Reload Scene to respawn with the new counts.");
    }

    // ---------- Persistence ----------

    private const string PrefPrefix = "XRTanks.GameConfig.";

    private struct ConfigDefaults
    {
        public int numberOfEnemies, numberOfCrates;
        public int projectileMinSpeed, projectileMaxSpeed, powerUpDuration;
        public bool pauseWhenConfigMenuOpen, enemyShootingEnabled, playerInvulnerable;
        public bool vrSnapFollow, vrNewTerrainEachRound;
        public int vrTerrainSeed;
    }

    private ConfigDefaults _defaults;

    private void CaptureDefaults()
    {
        _defaults.numberOfEnemies = numberOfEnemies;
        _defaults.numberOfCrates = numberOfCrates;
        _defaults.projectileMinSpeed = projectileMinSpeed;
        _defaults.projectileMaxSpeed = projectileMaxSpeed;
        _defaults.powerUpDuration = powerUpDuration;
        _defaults.pauseWhenConfigMenuOpen = pauseWhenConfigMenuOpen;
        _defaults.enemyShootingEnabled = enemyShootingEnabled;
        _defaults.playerInvulnerable = playerInvulnerable;
        _defaults.vrSnapFollow = vrSnapFollow;
        _defaults.vrNewTerrainEachRound = vrNewTerrainEachRound;
        _defaults.vrTerrainSeed = vrTerrainSeed;
    }

    private void LoadPersistedConfig()
    {
        numberOfEnemies = PlayerPrefs.GetInt(PrefPrefix + nameof(numberOfEnemies), numberOfEnemies);
        numberOfCrates = PlayerPrefs.GetInt(PrefPrefix + nameof(numberOfCrates), numberOfCrates);
        projectileMinSpeed = PlayerPrefs.GetInt(PrefPrefix + nameof(projectileMinSpeed), projectileMinSpeed);
        projectileMaxSpeed = PlayerPrefs.GetInt(PrefPrefix + nameof(projectileMaxSpeed), projectileMaxSpeed);
        powerUpDuration = PlayerPrefs.GetInt(PrefPrefix + nameof(powerUpDuration), powerUpDuration);
        pauseWhenConfigMenuOpen = PlayerPrefs.GetInt(PrefPrefix + nameof(pauseWhenConfigMenuOpen), pauseWhenConfigMenuOpen ? 1 : 0) != 0;
        enemyShootingEnabled = PlayerPrefs.GetInt(PrefPrefix + nameof(enemyShootingEnabled), enemyShootingEnabled ? 1 : 0) != 0;
        playerInvulnerable = PlayerPrefs.GetInt(PrefPrefix + nameof(playerInvulnerable), playerInvulnerable ? 1 : 0) != 0;
        vrSnapFollow = PlayerPrefs.GetInt(PrefPrefix + nameof(vrSnapFollow), vrSnapFollow ? 1 : 0) != 0;
        vrNewTerrainEachRound = PlayerPrefs.GetInt(PrefPrefix + nameof(vrNewTerrainEachRound), vrNewTerrainEachRound ? 1 : 0) != 0;
        vrTerrainSeed = PlayerPrefs.GetInt(PrefPrefix + nameof(vrTerrainSeed), vrTerrainSeed);
    }

    private void SaveConfig()
    {
        PlayerPrefs.SetInt(PrefPrefix + nameof(numberOfEnemies), numberOfEnemies);
        PlayerPrefs.SetInt(PrefPrefix + nameof(numberOfCrates), numberOfCrates);
        PlayerPrefs.SetInt(PrefPrefix + nameof(projectileMinSpeed), projectileMinSpeed);
        PlayerPrefs.SetInt(PrefPrefix + nameof(projectileMaxSpeed), projectileMaxSpeed);
        PlayerPrefs.SetInt(PrefPrefix + nameof(powerUpDuration), powerUpDuration);
        PlayerPrefs.SetInt(PrefPrefix + nameof(pauseWhenConfigMenuOpen), pauseWhenConfigMenuOpen ? 1 : 0);
        PlayerPrefs.SetInt(PrefPrefix + nameof(enemyShootingEnabled), enemyShootingEnabled ? 1 : 0);
        PlayerPrefs.SetInt(PrefPrefix + nameof(playerInvulnerable), playerInvulnerable ? 1 : 0);
        PlayerPrefs.SetInt(PrefPrefix + nameof(vrSnapFollow), vrSnapFollow ? 1 : 0);
        PlayerPrefs.SetInt(PrefPrefix + nameof(vrNewTerrainEachRound), vrNewTerrainEachRound ? 1 : 0);
        PlayerPrefs.SetInt(PrefPrefix + nameof(vrTerrainSeed), vrTerrainSeed);
        PlayerPrefs.Save();
    }
}
