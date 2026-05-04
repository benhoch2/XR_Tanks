using System.Collections;
using System.Collections.Generic;
using Meta.XR.ImmersiveDebugger;
using Meta.XR.ImmersiveDebugger.UserInterface;
using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using Meta.XR.MRUtilityKit;
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

    [Header("Enemy Movement")]
    [DebugMember(Min = 0.1f, Max = 3f, Category = "Game Config")]
    public float enemyMoveSpeed = 0.5f;

    [DebugMember(Min = 0.5f, Max = 10f, Category = "Game Config")]
    public float enemyChaseRange = 4f;

    [DebugMember(Min = 0.2f, Max = 3f, Category = "Game Config")]
    public float enemyStopRange = 1.25f;

    [DebugMember(Min = 0, Max = 1, Category = "Game Config")]
    public int enemyAIMode = 1;

    [DebugMember(Tweakable = false, Category = "Game Config", DisplayName = "AI Modes")]
    public string enemyAIModeHelp = "0 = Chase/Wander, 1 = Patrol/Scan";

    [DebugMember(Min = 10f, Max = 90f, Category = "Game Config")]
    public float enemyScanAngle = 45f;

    [DebugMember(Min = 5f, Max = 120f, Category = "Game Config")]
    public float enemyScanSpeed = 45f;

    [DebugMember(Min = 0.1f, Max = 1.5f, Category = "Game Config")]
    public float enemyObstacleCheckDistance = 0.25f;

    [DebugMember(Category = "Game Config")]
    public bool pauseWhenConfigMenuOpen = true;

    [Header("NavMesh")]
    [Tooltip("Layers to include when building the runtime NavMesh from MRUK colliders. " +
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

    [Header("Controls — Right")]
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "R Trigger")]
    public string ctrlRTrigger = "Fire (hold to charge)";
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "A Button")]
    public string ctrlA = "Cycle projectile";
    [DebugMember(Tweakable = false, Category = "Controls", DisplayName = "B Button (hold 3s)")]
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

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
        _debugPanelCoroutine = StartCoroutine(SubscribeToDebugPanel());
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_debugInterface != null)
        {
            _debugInterface.OnVisibilityChangedEvent -= OnDebugPanelVisibilityChanged;
        }
        Time.timeScale = 1f;
        if (_navMeshDataInstance.valid)
            _navMeshDataInstance.Remove();
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

        StopTrackedCoroutine(ref _debugPanelCoroutine);
        StopTrackedCoroutine(ref _spawnCoroutine);
        StopTrackedCoroutine(ref _enemyConfigCoroutine);

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
        ConfigureMetaXRRuntime();
        FindAndApplySpawnerConfig();
        _spawnCoroutine = StartCoroutine(SpawnPlayerTankNearHeadset());
    }

    private void ConfigureMetaXRRuntime()
    {
#if UNITY_EDITOR
        Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
#endif

        foreach (var manager in FindObjectsByType<OVRManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            manager.shouldBoundaryVisibilityBeSuppressed = true;
        }
    }

    private void FindAndApplySpawnerConfig()
    {
        foreach (var spawner in FindObjectsByType<FindSpawnPositions>(FindObjectsSortMode.None))
        {
            if (spawner.SpawnObject == null) continue;

            string objName = spawner.SpawnObject.name;

            if (objName.Contains("TargetTank"))
                spawner.SpawnAmount = numberOfEnemies;
            else if (objName.Contains("TargetBox"))
                spawner.SpawnAmount = numberOfCrates;
        }
    }

    private IEnumerator SpawnPlayerTankNearHeadset()
    {
        // Wait for MRUK room to be available
        MRUKRoom room = null;
        for (int i = 0; i < 600; i++)
        {
            if (MRUK.Instance != null)
                room = MRUK.Instance.GetCurrentRoom();
            if (room != null) break;
            yield return null;
        }

        if (room == null)
        {
            Debug.LogWarning("[GameConfig] MRUK room not found; using fallback Floor collider and editor tank spawn.");
            ConfigureFallbackFloor(true, null);
            SpawnPlayerTankFallback();
            BuildRuntimeNavMesh(null);
            _enemyConfigCoroutine = StartCoroutine(ConfigureEnemyTankMovementWhenReady());
            yield break;
        }

        // Align the fallback scene Floor visually to the MRUK floor, but disable it during MRUK-based spawning
        // so the large plane does not affect spawn selection or room-bounded navigation.
        MRUKAnchor floorAnchor = room.FloorAnchor;
        float? gameplayFloorY = null;
        if (floorAnchor != null)
        {
            gameplayFloorY = floorAnchor.transform.position.y;
            ConfigureFallbackFloor(false, gameplayFloorY);
        }

        // Wait for Effect Mesh colliders to generate
        for (int i = 0; i < 60; i++)
        {
            if (Physics.Raycast(new Vector3(0, 5f, 0), Vector3.down, 10f))
                break;
            yield return null;
        }

        BuildRuntimeNavMesh(room.transform);
        _enemyConfigCoroutine = StartCoroutine(ConfigureEnemyTankMovementWhenReady());

        // Find the TankSpawner and spawn 5 candidates
        FindSpawnPositions tankSpawner = null;
        foreach (var spawner in FindObjectsByType<FindSpawnPositions>(FindObjectsSortMode.None))
        {
            if (spawner.SpawnObject != null && spawner.SpawnObject.name.Contains("TankFree"))
            {
                tankSpawner = spawner;
                break;
            }
        }

        if (tankSpawner == null)
        {
            Debug.LogWarning("[GameConfig] TankSpawner not found.");
            yield break;
        }

        tankSpawner.SpawnAmount = 5;
        tankSpawner.StartSpawn(room);

        // Re-enable the stable gameplay floor immediately after spawn placement.
        if (gameplayFloorY.HasValue)
            ConfigureFallbackFloor(true, gameplayFloorY);

        // Wait for tanks to appear
        ShootingControls[] candidates = null;
        for (int i = 0; i < 120; i++)
        {
            candidates = FindObjectsByType<ShootingControls>(FindObjectsSortMode.None);
            if (candidates.Length >= 5) break;
            yield return null;
        }

        if (candidates == null || candidates.Length == 0)
        {
            Debug.LogWarning("[GameConfig] No player tanks found after StartSpawn.");
            yield break;
        }

        // Pick the candidate closest to the headset (horizontal distance)
        Camera cam = Camera.main;
        Vector3 headPos = cam != null ? cam.transform.position : Vector3.zero;

        ShootingControls closest = candidates[0];
        float bestDist = float.MaxValue;
        foreach (var tank in candidates)
        {
            Vector3 diff = tank.transform.position - headPos;
            diff.y = 0;
            float dist = diff.sqrMagnitude;
            if (dist < bestDist)
            {
                bestDist = dist;
                closest = tank;
            }
        }

        // Destroy the other candidates
        foreach (var tank in candidates)
        {
            if (tank != closest)
                Destroy(tank.gameObject);
        }

        // Face the tank toward the headset's forward direction
        if (cam != null)
        {
            Vector3 forward = cam.transform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude > 0.001f)
                closest.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        // Ensure the bottom of the tank sits slightly above the MRUK floor
        if (floorAnchor != null)
        {
            float floorY = floorAnchor.transform.position.y;
            Collider tankCollider = closest.GetComponentInChildren<Collider>();
            if (tankCollider != null)
            {
                Bounds bounds = tankCollider.bounds;
                float clearance = 0.01f;
                float offsetY = (floorY + clearance) - bounds.min.y;

                if (Mathf.Abs(offsetY) > 0.001f)
                {
                    closest.transform.position += Vector3.up * offsetY;
                }
            }
        }

    }

    private void ConfigureFallbackFloor(bool enableCollider, float? floorY)
    {
        GameObject floorObj = GetGameplayFloor();
        if (floorObj == null) return;

        if (floorY.HasValue)
        {
            Vector3 pos = floorObj.transform.position;
            pos.y = floorY.Value;
            floorObj.transform.position = pos;
        }

        Collider floorCollider = floorObj.GetComponent<Collider>();
        if (floorCollider != null)
            floorCollider.enabled = enableCollider;
    }

    private void BuildRuntimeNavMesh(Transform sourceRoot)
    {
        List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(sourceRoot, navMeshLayerMask, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), sources);

        if (sources.Count == 0)
        {
            Debug.LogWarning("[NavMesh] No navmesh sources were collected.");
            return;
        }

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

        if (_navMeshDataInstance.valid)
            _navMeshDataInstance.Remove();

        NavMeshBuildSettings settings = NavMesh.GetSettingsByIndex(0);
        settings.agentRadius = 0.12f;
        settings.agentHeight = 0.25f;
        settings.agentClimb = 0.2f;
        settings.agentSlope = 60f;
        settings.overrideVoxelSize = true;
        settings.voxelSize = 0.03f;
        settings.overrideTileSize = true;
        settings.tileSize = 64;

        NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
        if (data != null)
        {
            _navMeshDataInstance = NavMesh.AddNavMeshData(data);
        }
        else
        {
            Debug.LogWarning("[NavMesh] BuildNavMeshData returned null.");
        }
    }

    private IEnumerator ConfigureEnemyTankMovementWhenReady()
    {
        for (int i = 0; i < 180; i++)
        {
            bool foundEnemy = false;
            foreach (var target in FindObjectsByType<Target>(FindObjectsSortMode.None))
            {
                if (target != null && target.maxHitPoints > 0)
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
            if (target == null || target.maxHitPoints <= 0)
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

            agent.enabled = true;
            agent.speed = enemyMoveSpeed;
            agent.angularSpeed = 180f;
            agent.acceleration = 2f;
            agent.stoppingDistance = enemyStopRange;
            agent.autoBraking = true;
            agent.autoTraverseOffMeshLink = false;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
            agent.Warp(hit.position);

            EnemyTankAI ai = root.GetComponent<EnemyTankAI>();
            if (ai == null)
                ai = root.gameObject.AddComponent<EnemyTankAI>();

            ai.chaseRange = enemyChaseRange;
            ai.stopRange = enemyStopRange;
            ai.moveSpeed = enemyMoveSpeed;
            ai.behaviorMode = enemyAIMode == 1 ? EnemyTankAI.BehaviorMode.PatrolScan : EnemyTankAI.BehaviorMode.ChaseWander;
            ai.turretScanAngle = enemyScanAngle;
            ai.turretScanSpeed = enemyScanSpeed;
            ai.obstacleCheckDistance = enemyObstacleCheckDistance;
            ai.ResetBehaviorState();

            // Wire wheel animations to the moving enemy root so the wheels turn with movement.
            foreach (var wheel in target.GetComponentsInChildren<WheelRotation>(true))
            {
                wheel.tankBody = root;
                wheel.enabled = true;
            }
        }
    }

    private void SpawnPlayerTankFallback()
    {
        FindSpawnPositions tankSpawner = null;
        foreach (var spawner in FindObjectsByType<FindSpawnPositions>(FindObjectsSortMode.None))
        {
            if (spawner.SpawnObject != null && spawner.SpawnObject.name.Contains("TankFree"))
            {
                tankSpawner = spawner;
                break;
            }
        }

        if (tankSpawner == null || tankSpawner.SpawnObject == null)
        {
            Debug.LogWarning("[GameConfig] TankSpawner not found for fallback spawn.");
            return;
        }

        Camera cam = Camera.main;
        Vector3 spawnPos = cam != null ? cam.transform.position + cam.transform.forward * 2f : new Vector3(0f, 0f, 2f);

        GameObject floorObj = GetGameplayFloor();
        if (floorObj != null)
            spawnPos.y = floorObj.transform.position.y + 0.01f;

        Vector3 forward = cam != null ? cam.transform.forward : Vector3.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;

        GameObject tank = Instantiate(tankSpawner.SpawnObject, spawnPos, Quaternion.LookRotation(forward, Vector3.up));
        Collider tankCollider = tank.GetComponentInChildren<Collider>();
        if (floorObj != null && tankCollider != null)
        {
            float offsetY = (floorObj.transform.position.y + 0.01f) - tankCollider.bounds.min.y;
            tank.transform.position += Vector3.up * offsetY;
        }
    }

    [DebugMember(Category = "Game Config")]
    public void ReloadScene()
    {
        ResetSceneStartupState();
        Scene active = SceneManager.GetActiveScene();
        SceneManager.LoadScene(active.name);
    }
}
