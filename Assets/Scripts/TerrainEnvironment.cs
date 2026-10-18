using System.Collections;
using System.Collections.Generic;
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

    [Header("Terrain")]
    [SerializeField] private MountainTerrainGenerator.Settings terrainSettings = new MountainTerrainGenerator.Settings();

    [Header("Spawning")]
    [Tooltip("Steepest ground (degrees) a tank may spawn on.")]
    [SerializeField] private float maxSpawnSlope = 15f;
    [Tooltip("Minimum distance from the player to any enemy at spawn. Keep above the enemies' chase range.")]
    [SerializeField] private float minEnemyDistanceFromPlayer = 6f;
    [SerializeField] private float minEnemySpacing = 2f;
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
        public Vector3 player;
        public readonly List<Vector3> enemies = new List<Vector3>();
        public readonly List<Vector3> crates = new List<Vector3>();
    }

    private int _seed;
    private int _enemyCount;
    private int _crateCount;
    private float[,] _heights;
    private float _minGroundY;
    private Terrain _terrain;
    private TerrainData _terrainData;
    private readonly List<Object> _runtimeAssets = new List<Object>();

    private Transform _playerTank;
    private Vector3 _playerSpawnPosition;
    private Quaternion _playerSpawnRotation;

    public int Seed => _seed;
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
        _enemyCount = enemies;
        _crateCount = crates;
    }

    public override IEnumerator PrepareWorld()
    {
        _seed = ChooseSeed();
        Debug.Log($"[Terrain] Generating mountain map with seed {_seed}.");

        // One step per frame so no single headset frame carries the whole generation cost.
        _heights = MountainTerrainGenerator.GenerateHeights(_seed, terrainSettings);
        yield return null;
        CreateTerrain();
        yield return null;
        PaintTerrain();
    }

    /// <summary>PrepareWorld without the frame spreading or seed choice, for editor tooling.</summary>
    public void BuildTerrainImmediate(int seed)
    {
        _seed = seed;
        _heights = MountainTerrainGenerator.GenerateHeights(_seed, terrainSettings);
        CreateTerrain();
        PaintTerrain();
    }

    public override IEnumerator SpawnActors()
    {
        SpawnPlan plan = PlanSpawns();
        var rng = new System.Random(_seed ^ 0x5EED);

        Transform actorsRoot = new GameObject("Actors").transform;
        actorsRoot.SetParent(transform, false);

        SpawnPlayer(plan, actorsRoot);

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

        yield break;
    }

    /// <summary>
    /// Picks spawn points for the player, enemies and crates. Needs the terrain and the
    /// runtime NavMesh: enemies must stand on the NavMesh and be able to drive to the player.
    /// </summary>
    public SpawnPlan PlanSpawns()
    {
        var rng = new System.Random(_seed);
        var plan = new SpawnPlan();

        if (!TryFindPlayerSpawn(rng, out plan.player))
        {
            Debug.LogWarning("[Terrain] No flat NavMesh point near the center; spawning the player at the center.");
            plan.player = GroundPoint(TerrainCenter);
        }

        bool playerOnNavMesh = NavMesh.SamplePosition(plan.player, out NavMeshHit playerHit, 0.5f, NavMesh.AllAreas);
        var path = new NavMeshPath();

        for (int n = 0; n < _enemyCount; n++)
        {
            for (int attempt = 0; attempt < placementAttemptsPerActor; attempt++)
            {
                Vector3 candidate = RandomInteriorGroundPoint(rng);
                if (SlopeAt(candidate) > maxSpawnSlope) continue;
                if (HorizontalDistance(candidate, plan.player) < minEnemyDistanceFromPlayer) continue;
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
                if (HorizontalDistance(candidate, plan.player) < minCrateDistanceFromPlayer) continue;

                float height = Mathf.Lerp(crateHeightRange.x, crateHeightRange.y, (float)rng.NextDouble());
                plan.crates.Add(candidate + Vector3.up * height);
                break;
            }
        }

        if (plan.enemies.Count < _enemyCount || plan.crates.Count < _crateCount)
        {
            Debug.LogWarning($"[Terrain] Placed {plan.enemies.Count}/{_enemyCount} enemies and " +
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
        if (_playerTank == null)
            return;

        // Teleport shots and physics tunnelling can put the tank past the walls or under
        // the terrain, where it would fall forever. Put it back where it started.
        Vector3 offset = _playerTank.position - TerrainCenter;
        float limit = terrainSettings.size * 0.5f + 0.5f;
        if (_playerTank.position.y < _minGroundY - 2f || Mathf.Abs(offset.x) > limit || Mathf.Abs(offset.z) > limit)
            RespawnPlayer();
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

    private void SpawnPlayer(SpawnPlan plan, Transform parent)
    {
        if (playerTankPrefab == null)
        {
            Debug.LogWarning("[Terrain] No player tank prefab assigned.");
            return;
        }

        // Start facing the nearest enemy so the action is in front of the player.
        Vector3 facing = TerrainCenter - plan.player;
        float nearest = float.MaxValue;
        foreach (Vector3 enemy in plan.enemies)
        {
            float d = HorizontalDistance(enemy, plan.player);
            if (d < nearest)
            {
                nearest = d;
                facing = enemy - plan.player;
            }
        }

        GameObject tank = Instantiate(playerTankPrefab, plan.player, Quaternion.identity, parent);
        FaceHorizontally(tank.transform, facing);
        SnapBottomToHeight(tank, GetGroundHeight(plan.player));

        _playerTank = tank.transform;
        _playerSpawnPosition = _playerTank.position;
        _playerSpawnRotation = _playerTank.rotation;

        if (rigFollow != null)
        {
            rigFollow.SetTarget(_playerTank, GetGroundHeight);
            rigFollow.SnapBehindTarget();
        }
    }

    private void RespawnPlayer()
    {
        Debug.LogWarning("[Terrain] Player tank left the map; respawning it at its start point.");
        _playerTank.SetPositionAndRotation(_playerSpawnPosition, _playerSpawnRotation);

        Rigidbody rb = _playerTank.GetComponent<Rigidbody>();
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
