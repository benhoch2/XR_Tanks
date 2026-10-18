using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Pure-VR world: a mountain valley generated at runtime from a seed (see
/// <see cref="MountainTerrainGenerator"/>). PrepareWorld builds the terrain, its
/// materials and invisible edge walls; SpawnActors places the player, enemies and crates.
///
/// Placement draws only from the seed, so a seed reproduces the whole layout. The one
/// exception is the enemy variant, which <see cref="RandomEnemyVariant"/> still rolls itself.
/// </summary>
public class TerrainEnvironment : GameEnvironment
{
    [Header("Prefabs")]
    [SerializeField] private GameObject playerTankPrefab;
    [Tooltip("Spawned per enemy; TargetTankRandomizer picks Light/Medium/Heavy itself.")]
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField] private GameObject cratePrefab;

    [Header("Scene")]
    [Tooltip("URP Terrain/Lit material. Referenced from the scene so the shader ships in builds.")]
    [SerializeField] private Material terrainMaterial;
    [SerializeField] private PlayerRigFollow rigFollow;
    [Tooltip("Runs turn-based rounds (GameSession.Mode). Idle in real-time play.")]
    [SerializeField] private TurnBasedMatch turnBasedMatch;

    [Header("Terrain")]
    [SerializeField] private MountainTerrainGenerator.Settings terrainSettings = new MountainTerrainGenerator.Settings();

    [Header("Spawning")]
    [Tooltip("Steepest ground (degrees) a tank may spawn on.")]
    [SerializeField] private float maxSpawnSlope = 15f;
    [Tooltip("Minimum distance from the player to any enemy at spawn. Keep above the enemies' chase range.")]
    [SerializeField] private float minEnemyDistanceFromPlayer = 6f;
    [SerializeField] private float minEnemySpacing = 2f;
    [Tooltip("Turn-based hot-seat: minimum distance between human tanks at spawn.")]
    [SerializeField] private float minHumanSpacing = 4f;
    [SerializeField] private float minCrateDistanceFromPlayer = 1.5f;
    [Tooltip("Crates float this high above the ground (min, max), in meters. Floater bobs ±0.1 around it.")]
    [SerializeField] private Vector2 crateHeightRange = new Vector2(0.2f, 0.6f);
    [SerializeField] private int placementAttemptsPerActor = 40;

    // Tank-scale NavMesh tuned for terrain: enemies avoid slopes the player tank can't climb
    // either (TowerController.maxClimbSlopeDegrees), and a coarser voxel keeps the build over
    // the whole map short.
    private const float NavMeshSlope = 35f;
    private const float NavMeshVoxelSize = 0.04f;
    private const float CrateMaxSlope = 30f;

    /// <summary>Where SpawnActors puts everything. Planned first so the editor validation can inspect it.</summary>
    public class SpawnPlan
    {
        /// <summary>Human tanks; the first is the flattest central spot.</summary>
        public readonly List<Vector3> players = new List<Vector3>();
        public readonly List<Vector3> enemies = new List<Vector3>();
        public readonly List<Vector3> crates = new List<Vector3>();
    }

    private int _seed;
    private int _humanCount = 1;
    private int _enemyCount;
    private int _crateCount;
    private float[,] _heights;
    private float _minGroundY;
    private Terrain _terrain;
    private TerrainData _terrainData;
    private readonly List<Object> _runtimeAssets = new List<Object>();

    private readonly List<SpawnedTank> _humanTanks = new List<SpawnedTank>();

    private struct SpawnedTank
    {
        public Transform tank;
        public Vector3 position;
        public Quaternion rotation;
    }

    public int Seed => _seed;

    /// <summary>True once the terrain exists (online, network-spawned tanks wait for it).</summary>
    public bool IsWorldReady { get; private set; }

    /// <summary>Craters dug this round (tests compare it across headsets).</summary>
    public int CraterCount { get; private set; }
    public MountainTerrainGenerator.Settings TerrainSettings => terrainSettings;

    private Vector3 TerrainOrigin => _terrain != null
        ? _terrain.transform.position
        : transform.position - new Vector3(terrainSettings.size, 0f, terrainSettings.size) * 0.5f;

    private Vector3 TerrainCenter => TerrainOrigin + new Vector3(terrainSettings.size, 0f, terrainSettings.size) * 0.5f;

    public override Transform NavMeshSourceRoot => _terrain != null ? _terrain.transform : transform;

    public override Bounds? NavMeshBounds => new Bounds(
        TerrainCenter + Vector3.up * (terrainSettings.maxHeight * 0.5f),
        new Vector3(terrainSettings.size, terrainSettings.maxHeight + 2f, terrainSettings.size));

    public override void TuneNavMesh(ref NavMeshBuildSettings settings)
    {
        settings.agentSlope = NavMeshSlope;
        settings.voxelSize = NavMeshVoxelSize;
    }

    public override void ApplySpawnCounts(int enemies, int crates)
    {
        // Turn-based rounds take their line-up from the main menu instead of the config menu.
        if (GameSession.IsOnline)
        {
            // One tank per headset; 0 AI tanks is a free-for-all.
            _humanCount = Mathf.Max(1, GameSession.OnlineRoster.Count);
            _enemyCount = Mathf.Clamp(GameSession.AiTanks, 0, GameSession.MaxAiTanks);
        }
        else
        {
            _humanCount = GameSession.IsTurnBased ? Mathf.Clamp(GameSession.HumanPlayers, 1, GameSession.MaxHumanPlayers) : 1;
            _enemyCount = GameSession.IsTurnBased ? Mathf.Clamp(GameSession.AiTanks, 1, GameSession.MaxAiTanks) : enemies;
        }
        _crateCount = crates;
    }

    public override IEnumerator PrepareWorld()
    {
        if (GameSession.IsOnline)
        {
            // Every headset builds the same map from the host's seed (sent with the match setup).
            float waitStart = Time.realtimeSinceStartup;
            while (GameSession.OnlineSeed < 0 && Time.realtimeSinceStartup - waitStart < 10f)
                yield return null;
            if (GameSession.OnlineSeed < 0)
                Debug.LogError("[Terrain] No seed from the host; maps will differ between headsets.");
        }

        _seed = ChooseSeed();
        Debug.Log($"[Terrain] Generating mountain map with seed {_seed}.");

        // One step per frame so no single headset frame carries the whole generation cost.
        _heights = MountainTerrainGenerator.GenerateHeights(_seed, terrainSettings);
        yield return null;
        CreateTerrain();
        yield return null;
        PaintTerrain();
        IsWorldReady = true;
    }

    /// <summary>PrepareWorld without the frame spreading or seed choice, for editor tooling.</summary>
    public void BuildTerrainImmediate(int seed)
    {
        _seed = seed;
        _heights = MountainTerrainGenerator.GenerateHeights(_seed, terrainSettings);
        CreateTerrain();
        PaintTerrain();
        IsWorldReady = true;
    }

    public override IEnumerator SpawnActors()
    {
        if (GameSession.IsOnline)
        {
            SpawnOnline();
            yield break;
        }

        SpawnPlan plan = PlanSpawns();
        var rng = new System.Random(_seed ^ 0x5EED);

        Transform actorsRoot = new GameObject("Actors").transform;
        actorsRoot.SetParent(transform, false);

        var humans = new List<Transform>();
        for (int i = 0; i < plan.players.Count; i++)
        {
            Transform tank = SpawnPlayer(plan, i, playerTankPrefab, actorsRoot);
            if (tank != null)
            {
                humans.Add(tank);
                RegisterLocalHumanTank(tank);
            }
        }

        if (enemyPrefab != null)
        {
            foreach (Vector3 point in plan.enemies)
                Instantiate(enemyPrefab, point, RandomYaw(rng), actorsRoot);
        }
        else
        {
            Debug.LogWarning("[Terrain] No enemy prefab assigned.");
        }

        if (cratePrefab != null)
        {
            foreach (Vector3 point in plan.crates)
                Instantiate(cratePrefab, point, RandomYaw(rng), actorsRoot);
        }

        if (rigFollow != null && humans.Count > 0)
        {
            rigFollow.SetTarget(humans[0], GetGroundHeight);
            rigFollow.SnapBehindTarget();
        }

        // Turn-based: the match retargets the rig each turn (it keeps the ground source set above).
        if (GameSession.IsTurnBased && turnBasedMatch != null)
            turnBasedMatch.Begin(this, humans);

        yield break;
    }

    /// <summary>
    /// Online: the host network-spawns one tank per headset (owned by that headset), the AI
    /// tanks and the crates; clients get them through Netcode. Both then start the match.
    /// </summary>
    private void SpawnOnline()
    {
        var humans = new List<Transform>();

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            SpawnPlan plan = PlanSpawns();
            var rng = new System.Random(_seed ^ 0x5EED);

            // Network objects can only be parented under other network objects, so they spawn at the root.
            GameObject playerPrefab = NetworkPrefabMap.Resolve(playerTankPrefab);
            for (int i = 0; i < plan.players.Count && i < GameSession.OnlineRoster.Count; i++)
            {
                Transform tank = SpawnPlayer(plan, i, playerPrefab, null);
                if (tank == null)
                    continue;

                tank.GetComponent<NetworkTarget>().ParticipantIndex.Value = i;
                tank.GetComponent<NetworkObject>().SpawnWithOwnership(GameSession.OnlineRoster[i], destroyWithScene: true);
                humans.Add(tank);
            }

            var picker = enemyPrefab != null ? enemyPrefab.GetComponent<RandomEnemyVariant>() : null;
            for (int n = 0; n < plan.enemies.Count; n++)
            {
                GameObject variant = picker != null ? picker.PickVariant(() => (float)rng.NextDouble()) : null;
                if (variant == null)
                    continue;

                GameObject enemy = Instantiate(NetworkPrefabMap.Resolve(variant), plan.enemies[n], RandomYaw(rng));
                enemy.GetComponent<NetworkTarget>().ParticipantIndex.Value = plan.players.Count + n;
                enemy.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
            }

            if (cratePrefab != null)
            {
                GameObject crateNetworkPrefab = NetworkPrefabMap.Resolve(cratePrefab);
                foreach (Vector3 point in plan.crates)
                    Instantiate(crateNetworkPrefab, point, RandomYaw(rng)).GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
            }
        }

        // The match moves the camera to this headset's tank once it arrives.
        if (rigFollow != null)
            rigFollow.SetTarget(null, GetGroundHeight);
        if (turnBasedMatch != null)
            turnBasedMatch.Begin(this, humans);
    }

    /// <summary>Puts a tank this headset controls under the out-of-bounds guard.</summary>
    public void RegisterLocalHumanTank(Transform tank)
    {
        _humanTanks.Add(new SpawnedTank { tank = tank, position = tank.position, rotation = tank.rotation });
    }

    /// <summary>
    /// Picks spawn points for the player, enemies and crates. Needs the terrain and the
    /// runtime NavMesh: enemies must stand on the NavMesh and be able to drive to the player.
    /// </summary>
    public SpawnPlan PlanSpawns()
    {
        var rng = new System.Random(_seed);
        var plan = new SpawnPlan();

        if (!TryFindPlayerSpawn(rng, out Vector3 firstPlayer))
        {
            Debug.LogWarning("[Terrain] No flat NavMesh point near the center; spawning the player at the center.");
            firstPlayer = GroundPoint(TerrainCenter);
        }
        plan.players.Add(firstPlayer);

        // Further hot-seat players: anywhere flat on the NavMesh, spread apart.
        for (int n = 1; n < _humanCount; n++)
        {
            for (int attempt = 0; attempt < placementAttemptsPerActor; attempt++)
            {
                Vector3 candidate = RandomInteriorGroundPoint(rng);
                if (SlopeAt(candidate) > maxSpawnSlope) continue;
                if (IsWithin(plan.players, candidate, minHumanSpacing)) continue;
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 0.3f, NavMesh.AllAreas)) continue;

                plan.players.Add(hit.position);
                break;
            }
        }

        bool playerOnNavMesh = NavMesh.SamplePosition(firstPlayer, out NavMeshHit playerHit, 0.5f, NavMesh.AllAreas);
        var path = new NavMeshPath();

        for (int n = 0; n < _enemyCount; n++)
        {
            for (int attempt = 0; attempt < placementAttemptsPerActor; attempt++)
            {
                Vector3 candidate = RandomInteriorGroundPoint(rng);
                if (SlopeAt(candidate) > maxSpawnSlope) continue;
                if (IsWithin(plan.players, candidate, minEnemyDistanceFromPlayer)) continue;
                if (IsWithin(plan.enemies, candidate, minEnemySpacing)) continue;
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 0.3f, NavMesh.AllAreas)) continue;

                // Skip isolated ledges: an enemy there could never reach the player.
                if (playerOnNavMesh
                    && (!NavMesh.CalculatePath(hit.position, playerHit.position, NavMesh.AllAreas, path)
                        || path.status != NavMeshPathStatus.PathComplete))
                    continue;

                plan.enemies.Add(hit.position);
                break;
            }
        }

        for (int n = 0; n < _crateCount; n++)
        {
            for (int attempt = 0; attempt < placementAttemptsPerActor; attempt++)
            {
                Vector3 candidate = RandomInteriorGroundPoint(rng);
                if (SlopeAt(candidate) > CrateMaxSlope) continue;
                if (IsWithin(plan.players, candidate, minCrateDistanceFromPlayer)) continue;

                float height = Mathf.Lerp(crateHeightRange.x, crateHeightRange.y, (float)rng.NextDouble());
                plan.crates.Add(candidate + Vector3.up * height);
                break;
            }
        }

        if (plan.players.Count < _humanCount || plan.enemies.Count < _enemyCount || plan.crates.Count < _crateCount)
        {
            Debug.LogWarning($"[Terrain] Placed {plan.players.Count}/{_humanCount} players, " +
                             $"{plan.enemies.Count}/{_enemyCount} enemies and " +
                             $"{plan.crates.Count}/{_crateCount} crates on seed {_seed}.");
        }

        return plan;
    }

    /// <summary>World-space ground height under <paramref name="worldPos"/> (clamped to the map edge).</summary>
    public float GetGroundHeight(Vector3 worldPos)
    {
        return _terrain != null
            ? _terrain.SampleHeight(worldPos) + _terrain.transform.position.y
            : transform.position.y;
    }

    /// <summary>Ground steepness in degrees under <paramref name="worldPos"/>.</summary>
    public float SlopeAt(Vector3 worldPos)
    {
        if (_terrainData == null)
            return 0f;

        Vector3 local = worldPos - TerrainOrigin;
        return _terrainData.GetSteepness(
            Mathf.Clamp01(local.x / terrainSettings.size),
            Mathf.Clamp01(local.z / terrainSettings.size));
    }

    private void Update()
    {
        // Teleport shots and physics tunnelling can put a tank past the walls or under
        // the terrain, where it would fall forever. Put it back where it started.
        float limit = terrainSettings.size * 0.5f + 0.5f;
        foreach (SpawnedTank human in _humanTanks)
        {
            if (human.tank == null)
                continue;

            Vector3 offset = human.tank.position - TerrainCenter;
            if (human.tank.position.y < _minGroundY - 2f || Mathf.Abs(offset.x) > limit || Mathf.Abs(offset.z) > limit)
                RespawnPlayer(human);
        }
    }

    /// <summary>
    /// Carves an ellipsoid crater (horizontal <paramref name="radius"/>, vertical
    /// <paramref name="depth"/>) centered on <paramref name="center"/> and paints it as dirt.
    /// Ground is only ever lowered, so a blast in mid-air out of reach leaves no mark.
    /// Callers must refresh the NavMesh afterwards.
    /// </summary>
    public void DigCrater(Vector3 center, float radius, float depth)
    {
        if (_terrainData == null || radius <= 0f || depth <= 0f)
            return;

        MountainTerrainGenerator.Settings s = terrainSettings;
        Vector3 local = center - TerrainOrigin;

        int res = _terrainData.heightmapResolution;
        float cell = s.size / (res - 1);
        if (!CellRange(local, radius, cell, res, out int x0, out int z0, out int width, out int length))
            return;

        float[,] heights = _terrainData.GetHeights(x0, z0, width, length);
        bool changed = false;
        for (int zi = 0; zi < length; zi++)
        {
            for (int xi = 0; xi < width; xi++)
            {
                float dx = (x0 + xi) * cell - local.x;
                float dz = (z0 + zi) * cell - local.z;
                float t = (dx * dx + dz * dz) / (radius * radius);
                if (t >= 1f)
                    continue;

                float bottom = (local.y - depth * Mathf.Sqrt(1f - t)) / s.maxHeight;
                if (bottom < heights[zi, xi])
                {
                    heights[zi, xi] = Mathf.Max(0f, bottom);
                    changed = true;
                }
            }
        }

        if (!changed)
            return;

        _terrainData.SetHeights(x0, z0, heights);
        PaintCrater(local, radius * 1.15f);
        CraterCount++;
    }

    private void PaintCrater(Vector3 local, float radius)
    {
        int res = _terrainData.alphamapResolution;
        float cell = terrainSettings.size / (res - 1);
        if (!CellRange(local, radius, cell, res, out int x0, out int z0, out int width, out int length))
            return;

        float[,,] maps = _terrainData.GetAlphamaps(x0, z0, width, length);
        int layers = maps.GetLength(2);
        for (int zi = 0; zi < length; zi++)
        {
            for (int xi = 0; xi < width; xi++)
            {
                float dx = (x0 + xi) * cell - local.x;
                float dz = (z0 + zi) * cell - local.z;
                float scorch = 1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) / radius);
                if (scorch <= 0f)
                    continue;

                // Blend toward bare dirt; lerping every layer keeps the weights summing to 1.
                for (int l = 0; l < layers; l++)
                {
                    float targetWeight = l == MountainTerrainGenerator.DirtLayer ? 1f : 0f;
                    maps[zi, xi, l] = Mathf.Lerp(maps[zi, xi, l], targetWeight, scorch);
                }
            }
        }

        _terrainData.SetAlphamaps(x0, z0, maps);
    }

    /// <summary>Grid cells (clamped to the map) covering a circle of <paramref name="radius"/> around map-local <paramref name="local"/>.</summary>
    private static bool CellRange(Vector3 local, float radius, float cell, int res, out int x0, out int z0, out int width, out int length)
    {
        x0 = Mathf.Clamp(Mathf.FloorToInt((local.x - radius) / cell), 0, res - 1);
        z0 = Mathf.Clamp(Mathf.FloorToInt((local.z - radius) / cell), 0, res - 1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt((local.x + radius) / cell), 0, res - 1);
        int z1 = Mathf.Clamp(Mathf.CeilToInt((local.z + radius) / cell), 0, res - 1);
        width = x1 - x0 + 1;
        length = z1 - z0 + 1;
        return x1 > x0 && z1 > z0;
    }

    private void OnDestroy()
    {
        // TerrainData, layers, textures and the physics material are runtime-created assets;
        // they don't go away with the scene on their own.
        foreach (Object asset in _runtimeAssets)
        {
            if (asset == null) continue;
            if (Application.isPlaying)
                Destroy(asset);
            else
                DestroyImmediate(asset);
        }
        _runtimeAssets.Clear();
    }

    private static int ChooseSeed()
    {
        if (GameSession.IsOnline && GameSession.OnlineSeed >= 0)
            return GameSession.OnlineSeed;

        GameConfigManager config = GameConfigManager.Instance;
        if (config == null)
            return 1;

        // UnityEngine.Random only picks the seed; everything derived from it is deterministic.
        if (config.vrNewTerrainEachRound)
            config.vrTerrainSeed = Random.Range(0, 100000);
        return config.vrTerrainSeed;
    }

    private void CreateTerrain()
    {
        MountainTerrainGenerator.Settings s = terrainSettings;

        _terrainData = new TerrainData { name = $"MountainTerrain_{_seed}" };
        _runtimeAssets.Add(_terrainData);
        // Resolutions before size and layers: changing a resolution resets what depends on it.
        _terrainData.heightmapResolution = s.heightmapResolution;
        _terrainData.alphamapResolution = s.alphamapResolution;
        _terrainData.baseMapResolution = 256;
        _terrainData.size = new Vector3(s.size, s.maxHeight, s.size);
        _terrainData.SetHeights(0, 0, _heights);
        _terrainData.terrainLayers = CreateTerrainLayers();

        float minHeight = 1f;
        foreach (float h in _heights)
            minHeight = Mathf.Min(minHeight, h);

        GameObject go = Terrain.CreateTerrainGameObject(_terrainData);
        go.name = "Mountain Terrain";
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(-s.size * 0.5f, 0f, -s.size * 0.5f);

        _terrain = go.GetComponent<Terrain>();
        if (terrainMaterial != null)
            _terrain.materialTemplate = terrainMaterial;
        else
            Debug.LogWarning("[Terrain] No terrain material assigned; terrain may render incorrectly.");
        // Quest budget: coarser LOD, no self-shadowing (it still receives the tanks' shadows),
        // and the full splat shader across the whole map instead of a baked basemap.
        _terrain.heightmapPixelError = 4f;
        _terrain.basemapDistance = s.size * 2f;
        _terrain.shadowCastingMode = ShadowCastingMode.Off;
        _terrain.drawTreesAndFoliage = false;
        _terrain.allowAutoConnect = false;

        // Same grip as the MR scene's Floor.
        var physicsMaterial = new PhysicsMaterial("Terrain")
        {
            staticFriction = 0.6f,
            dynamicFriction = 0.6f,
            bounciness = 0f,
        };
        _runtimeAssets.Add(physicsMaterial);
        go.GetComponent<TerrainCollider>().sharedMaterial = physicsMaterial;

        _minGroundY = TerrainOrigin.y + minHeight * s.maxHeight;

        CreateEdgeWalls();
        Physics.SyncTransforms();
    }

    private void PaintTerrain()
    {
        _terrainData.SetAlphamaps(0, 0, MountainTerrainGenerator.ComputeSplatmap(_terrainData, _seed, terrainSettings));
    }

    private TerrainLayer[] CreateTerrainLayers()
    {
        var layers = new TerrainLayer[MountainTerrainGenerator.LayerCount];
        layers[MountainTerrainGenerator.GrassLayer] = CreateLayer("Grass", new Color(0.33f, 0.52f, 0.20f), 0.10f, 1.2f, 11);
        layers[MountainTerrainGenerator.DirtLayer] = CreateLayer("Dirt", new Color(0.47f, 0.37f, 0.26f), 0.10f, 1.0f, 23);
        layers[MountainTerrainGenerator.RockLayer] = CreateLayer("Rock", new Color(0.46f, 0.45f, 0.44f), 0.14f, 1.5f, 37);
        layers[MountainTerrainGenerator.SnowLayer] = CreateLayer("Snow", new Color(0.92f, 0.94f, 0.97f), 0.04f, 2.0f, 41);
        return layers;
    }

    private TerrainLayer CreateLayer(string layerName, Color baseColor, float variation, float tileSize, int textureSeed)
    {
        Texture2D texture = CreateNoiseTexture(baseColor, variation, textureSeed);
        var layer = new TerrainLayer
        {
            name = layerName,
            diffuseTexture = texture,
            tileSize = new Vector2(tileSize, tileSize),
        };
        _runtimeAssets.Add(texture);
        _runtimeAssets.Add(layer);
        return layer;
    }

    /// <summary>Small seamless noise texture so the ground reads as a surface rather than flat color.</summary>
    private static Texture2D CreateNoiseTexture(Color baseColor, float variation, int textureSeed)
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 2,
        };

        float ox = textureSeed * 13.7f;
        float oy = textureSeed * 7.3f;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;
                float v = y / (float)size;
                float n = TileableNoise(u, v, 4f, ox, oy) * 0.6f + TileableNoise(u, v, 12f, ox + 50f, oy + 50f) * 0.4f;
                float k = 1f + (n - 0.5f) * 2f * variation;
                // Alpha is the URP terrain layer's smoothness: 0 keeps the ground matte.
                pixels[y * size + x] = new Color(baseColor.r * k, baseColor.g * k, baseColor.b * k, 0f);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(true, true);
        return texture;
    }

    private static float TileableNoise(float u, float v, float scale, float ox, float oy)
    {
        // Blending four samples one period apart makes the texture wrap without a seam.
        float a = Mathf.PerlinNoise(u * scale + ox, v * scale + oy);
        float b = Mathf.PerlinNoise((u - 1f) * scale + ox, v * scale + oy);
        float c = Mathf.PerlinNoise(u * scale + ox, (v - 1f) * scale + oy);
        float d = Mathf.PerlinNoise((u - 1f) * scale + ox, (v - 1f) * scale + oy);
        return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
    }

    private void CreateEdgeWalls()
    {
        float size = terrainSettings.size;
        float half = size * 0.5f;
        float height = terrainSettings.maxHeight + 6f;
        const float thickness = 1f;
        Vector3 center = TerrainCenter + Vector3.up * (height * 0.5f - 1f);
        float offset = half + thickness * 0.5f;

        CreateWall("Edge Wall N", center + new Vector3(0f, 0f, offset), new Vector3(size + 2f * thickness, height, thickness));
        CreateWall("Edge Wall S", center + new Vector3(0f, 0f, -offset), new Vector3(size + 2f * thickness, height, thickness));
        CreateWall("Edge Wall E", center + new Vector3(offset, 0f, 0f), new Vector3(thickness, height, size));
        CreateWall("Edge Wall W", center + new Vector3(-offset, 0f, 0f), new Vector3(thickness, height, size));
    }

    private void CreateWall(string wallName, Vector3 position, Vector3 size)
    {
        // Ignore Raycast: still blocks tanks and projectiles, but stays out of the enemies'
        // line-of-sight casts and out of the NavMesh (which only collects the Default layer).
        var wall = new GameObject(wallName) { layer = LayerMask.NameToLayer("Ignore Raycast") };
        wall.transform.SetParent(transform, false);
        wall.transform.position = position;
        wall.AddComponent<BoxCollider>().size = size;
    }

    private bool TryFindPlayerSpawn(System.Random rng, out Vector3 point)
    {
        // The flattest of several NavMesh points in the ridge-free central valley.
        point = default;
        float bestSlope = float.MaxValue;
        for (int i = 0; i < placementAttemptsPerActor; i++)
        {
            Vector2 offset = RandomInsideUnitCircle(rng) * terrainSettings.startClearRadius;
            Vector3 candidate = GroundPoint(TerrainCenter + new Vector3(offset.x, 0f, offset.y));
            float slope = SlopeAt(candidate);
            if (slope < bestSlope && NavMesh.SamplePosition(candidate, out _, 0.3f, NavMesh.AllAreas))
            {
                bestSlope = slope;
                point = candidate;
            }
        }
        return bestSlope < float.MaxValue;
    }

    private Transform SpawnPlayer(SpawnPlan plan, int index, GameObject prefab, Transform parent)
    {
        if (prefab == null)
        {
            Debug.LogWarning("[Terrain] No player tank prefab assigned.");
            return null;
        }

        // Start facing the nearest enemy so the action is in front of the player.
        Vector3 position = plan.players[index];
        Vector3 facing = TerrainCenter - position;
        float nearest = float.MaxValue;
        foreach (Vector3 enemy in plan.enemies)
        {
            float d = HorizontalDistance(enemy, position);
            if (d < nearest)
            {
                nearest = d;
                facing = enemy - position;
            }
        }

        GameObject tank = Instantiate(prefab, position, Quaternion.identity, parent);
        tank.name = $"Player {index + 1} Tank";
        FaceHorizontally(tank.transform, facing);
        SnapBottomToHeight(tank, GetGroundHeight(position));
        return tank.transform;
    }

    private void RespawnPlayer(SpawnedTank human)
    {
        Debug.LogWarning($"[Terrain] {human.tank.name} left the map; respawning it at its start point.");
        human.tank.SetPositionAndRotation(human.position, human.rotation);

        Rigidbody rb = human.tank.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    private Vector3 GroundPoint(Vector3 worldPos)
    {
        worldPos.y = GetGroundHeight(worldPos);
        return worldPos;
    }

    private Vector3 RandomInteriorGroundPoint(System.Random rng)
    {
        // Skip the outer half of the rim; it is too steep to stand on anyway.
        float inset = terrainSettings.rimWidth * 0.5f;
        float half = terrainSettings.size * 0.5f - inset;
        float x = Mathf.Lerp(-half, half, (float)rng.NextDouble());
        float z = Mathf.Lerp(-half, half, (float)rng.NextDouble());
        return GroundPoint(TerrainCenter + new Vector3(x, 0f, z));
    }

    private static Vector2 RandomInsideUnitCircle(System.Random rng)
    {
        double angle = rng.NextDouble() * Mathf.PI * 2.0;
        double radius = System.Math.Sqrt(rng.NextDouble());
        return new Vector2((float)(System.Math.Cos(angle) * radius), (float)(System.Math.Sin(angle) * radius));
    }

    private static Quaternion RandomYaw(System.Random rng)
    {
        return Quaternion.Euler(0f, (float)(rng.NextDouble() * 360.0), 0f);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static bool IsWithin(List<Vector3> points, Vector3 candidate, float distance)
    {
        foreach (Vector3 p in points)
        {
            if (HorizontalDistance(p, candidate) < distance)
                return true;
        }
        return false;
    }
}
