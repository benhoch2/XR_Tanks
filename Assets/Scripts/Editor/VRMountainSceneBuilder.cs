using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Generates the VR scenes from the MR scene (SampleScene), so the camera rig, managers
/// and light stay in one place:
/// <list type="bullet">
/// <item>VRMountains — strips the MRUK / passthrough / occlusion building blocks and
/// spawners, switches the camera and OVRManager to VR, sets up sky, fog and lighting, and
/// adds the TerrainEnvironment, TurnBasedMatch and PlayerRigFollow.</item>
/// <item>MainMenu — the same VR setup minus the game: just the menu and a ground disc.</item>
/// </list>
/// Re-running it rebuilds both from scratch. Build order: MainMenu, SampleScene, VRMountains.
///
/// Batch mode (editor closed):
///   Unity.exe -batchmode -projectPath . -executeMethod VRMountainSceneBuilder.BuildFromCommandLine -quit
///   Unity.exe -batchmode -projectPath . -executeMethod VRMountainSceneBuilder.ValidateTerrain -validationOut &lt;dir&gt; -quit
///   Unity.exe -batchmode -projectPath . -buildTarget Android -executeMethod VRMountainSceneBuilder.BuildQuestApk -quit
/// </summary>
public static class VRMountainSceneBuilder
{
    private const string SourceScenePath = "Assets/Scenes/SampleScene.unity";
    private const string TargetScenePath = "Assets/Scenes/VRMountains.unity";
    private const string MenuScenePath = "Assets/Scenes/MainMenu.unity";
    private const string MaterialFolder = "Assets/Materials/VR";
    private const string SkyMaterialPath = MaterialFolder + "/MountainSky.mat";
    private const string TerrainMaterialPath = MaterialFolder + "/MountainTerrain.mat";
    private const string UnlitMaterialPath = MaterialFolder + "/UnlitColor.mat";
    private const string MenuGroundMaterialPath = MaterialFolder + "/MenuGround.mat";

    private const string CameraRigName = "[BuildingBlock] Camera Rig";

    // Everything that only makes sense with a scanned room and passthrough.
    private static readonly string[] MixedRealityRoots =
    {
        "[BuildingBlock] Passthrough",
        "[BuildingBlock] MR Utility Kit",
        "[BuildingBlock] Effect Mesh",
        "[BuildingBlock] Occlusion Dependencies",
        "[BuildingBlock] Occlusion",
        "TankSpawner",
        "TargetSpawner",
        "Floor",
        "Main Camera", // inactive duplicate; would compete with CenterEyeAnchor for Camera.main
    };

    private static readonly Color HorizonColor = new Color(0.70f, 0.78f, 0.86f);

    [MenuItem("XR Tanks/Build VR Scenes (Mountains + Main Menu)")]
    public static void BuildFromMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        if ((File.Exists(TargetScenePath) || File.Exists(MenuScenePath))
            && !EditorUtility.DisplayDialog("Build VR Scenes",
                $"{TargetScenePath} and {MenuScenePath} will be regenerated from {SourceScenePath}. Continue?",
                "Rebuild", "Cancel"))
            return;

        Build();
    }

    public static void BuildFromCommandLine()
    {
        Build();
    }

    private static void Build()
    {
        NetworkAssetBuilder.Result network = NetworkAssetBuilder.Build();
        BuildMountainScene(network);
        BuildMenuScene(network);

        SetBuildOrder(MenuScenePath, SourceScenePath, TargetScenePath);
        AssetDatabase.SaveAssets();
        EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
    }

    private static void BuildMountainScene(NetworkAssetBuilder.Result network)
    {
        Scene scene = CreateVRSceneFromSource(TargetScenePath, out GameObject rig);
        Material terrain = CreateOrUpdateMaterial(TerrainMaterialPath, "Universal Render Pipeline/Terrain/Lit", null);
        Material unlit = CreateOrUpdateMaterial(UnlitMaterialPath, "Universal Render Pipeline/Unlit", null);

        PlayerRigFollow follow = rig.GetComponent<PlayerRigFollow>();
        if (follow == null)
            follow = rig.AddComponent<PlayerRigFollow>();

        var environmentObject = new GameObject("VR Environment");
        SceneManager.MoveGameObjectToScene(environmentObject, scene);

        var match = environmentObject.AddComponent<TurnBasedMatch>();
        var matchSo = new SerializedObject(match);
        SetReference(matchSo, "rigFollow", follow);
        SetReference(matchSo, "unlitMaterial", unlit);
        SetReference(matchSo, "onlineSyncPrefab", network.matchSync);
        matchSo.ApplyModifiedPropertiesWithoutUndo();

        var environment = environmentObject.AddComponent<TerrainEnvironment>();
        var so = new SerializedObject(environment);
        SetReference(so, "playerTankPrefab", LoadPrefab("Assets/Prefabs/TankFree_Red.prefab"));
        SetReference(so, "enemyPrefab", LoadPrefab("Assets/Prefabs/TargetTankRandomizer.prefab"));
        SetReference(so, "cratePrefab", LoadPrefab("Assets/Prefabs/TargetBox.prefab"));
        SetReference(so, "terrainMaterial", terrain);
        SetReference(so, "rigFollow", follow);
        SetReference(so, "turnBasedMatch", match);
        so.ApplyModifiedPropertiesWithoutUndo();

        SaveScene(scene, TargetScenePath);
    }

    private static void BuildMenuScene(NetworkAssetBuilder.Result network)
    {
        Scene scene = CreateVRSceneFromSource(MenuScenePath, out GameObject rig);

        // The menu starts no round; GameConfigManager comes in with the game scenes.
        GameObject config = FindRoot(scene, "GameConfigManager");
        if (config != null)
            Object.DestroyImmediate(config);

        Material unlit = CreateOrUpdateMaterial(UnlitMaterialPath, "Universal Render Pipeline/Unlit", null);
        Material groundMaterial = CreateOrUpdateMaterial(MenuGroundMaterialPath, "Universal Render Pipeline/Lit",
            m => m.color = new Color(0.33f, 0.5f, 0.24f));

        var menu = rig.AddComponent<MainMenu>();
        var so = new SerializedObject(menu);
        SetReference(so, "unlitMaterial", unlit);
        SetReference(so, "networkBootstrapPrefab", network.bootstrap);
        so.ApplyModifiedPropertiesWithoutUndo();

        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ground.name = "Menu Ground";
        SceneManager.MoveGameObjectToScene(ground, scene);
        ground.transform.localScale = new Vector3(30f, 0.01f, 30f);
        ground.transform.position = new Vector3(0f, -0.01f, 0f);
        ground.GetComponent<Renderer>().sharedMaterial = groundMaterial;
        Object.DestroyImmediate(ground.GetComponent<Collider>());

        SaveScene(scene, MenuScenePath);
    }

    /// <summary>Copies SampleScene to <paramref name="path"/> and turns it into a passthrough-free VR scene.</summary>
    private static Scene CreateVRSceneFromSource(string path, out GameObject rig)
    {
        if (File.Exists(path))
            AssetDatabase.DeleteAsset(path);
        if (!AssetDatabase.CopyAsset(SourceScenePath, path))
            throw new System.InvalidOperationException($"Could not copy {SourceScenePath} to {path}.");

        Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

        foreach (string rootName in MixedRealityRoots)
        {
            GameObject root = FindRoot(scene, rootName);
            if (root != null)
                Object.DestroyImmediate(root);
            else
                Debug.LogWarning($"[VRSceneBuilder] Expected MR root '{rootName}' not found; skipped.");
        }

        rig = FindRoot(scene, CameraRigName);
        if (rig == null)
            throw new System.InvalidOperationException($"'{CameraRigName}' not found in {SourceScenePath}.");

        ConfigureOVRManager(rig);
        ConfigureCamera(rig);

        Material sky = CreateOrUpdateMaterial(SkyMaterialPath, "Skybox/Procedural", ConfigureSky);
        ConfigureLighting(scene, sky);
        return scene;
    }

    private static void SaveScene(Scene scene, string path)
    {
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new System.InvalidOperationException($"Could not save {path}.");
        Debug.Log($"[VRSceneBuilder] Built {path}.");
    }

    private static void ConfigureOVRManager(GameObject rig)
    {
        OVRManager manager = rig.GetComponentInChildren<OVRManager>(true);
        if (manager == null)
            throw new System.InvalidOperationException("No OVRManager on the camera rig.");

        var so = new SerializedObject(manager);
        SetBool(so, "isInsightPassthroughEnabled", false);
        // Meta requires the Guardian whenever passthrough is off.
        SetBool(so, "shouldBoundaryVisibilityBeSuppressed", false);
        SetBool(so, "requestScenePermissionOnStartup", false);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ConfigureCamera(GameObject rig)
    {
        Camera centerEye = rig.GetComponentsInChildren<Camera>(true).FirstOrDefault(c => c.name == "CenterEyeAnchor");
        if (centerEye == null)
            throw new System.InvalidOperationException("No CenterEyeAnchor camera under the camera rig.");

        // MR clears to transparent black so the passthrough underlay shows through.
        centerEye.clearFlags = CameraClearFlags.Skybox;
        Color background = centerEye.backgroundColor;
        background.a = 1f;
        centerEye.backgroundColor = background;
        centerEye.farClipPlane = 150f;
        EditorUtility.SetDirty(centerEye);
    }

    private static void ConfigureSky(Material sky)
    {
        sky.SetFloat("_SunSize", 0.04f);
        sky.SetFloat("_AtmosphereThickness", 0.9f);
        sky.SetColor("_SkyTint", new Color(0.55f, 0.65f, 0.80f));
        sky.SetColor("_GroundColor", new Color(0.37f, 0.42f, 0.38f));
        sky.SetFloat("_Exposure", 1.2f);
    }

    private static void ConfigureLighting(Scene scene, Material sky)
    {
        RenderSettings.skybox = sky;

        // Trilight instead of skybox ambient: no ambient probe bake needed for runtime terrain.
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.55f, 0.65f, 0.80f);
        RenderSettings.ambientEquatorColor = new Color(0.45f, 0.50f, 0.50f);
        RenderSettings.ambientGroundColor = new Color(0.25f, 0.23f, 0.20f);

        // Haze the far rim for depth; the map is 36 m across.
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = HorizonColor;
        RenderSettings.fogStartDistance = 15f;
        RenderSettings.fogEndDistance = 70f;

        GameObject lightObject = FindRoot(scene, "Directional Light");
        Light sun = lightObject != null ? lightObject.GetComponent<Light>() : null;
        if (sun != null)
        {
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.intensity = 1.3f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(40f, -35f, 0f);
            RenderSettings.sun = sun;
            EditorUtility.SetDirty(sun);
        }
        else
        {
            Debug.LogWarning("[VRSceneBuilder] No 'Directional Light' found; scene has no sun.");
        }
    }

    private static Material CreateOrUpdateMaterial(string path, string shaderName, System.Action<Material> configure)
    {
        Shader shader = Shader.Find(shaderName);
        if (shader == null)
            throw new System.InvalidOperationException($"Shader '{shaderName}' not found.");

        if (!AssetDatabase.IsValidFolder(MaterialFolder))
            AssetDatabase.CreateFolder(Path.GetDirectoryName(MaterialFolder).Replace('\\', '/'), Path.GetFileName(MaterialFolder));

        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            material.shader = shader;
        }

        configure?.Invoke(material);
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>Puts <paramref name="orderedPaths"/> first in the build settings, in that order, keeping any other scenes after them.</summary>
    private static void SetBuildOrder(params string[] orderedPaths)
    {
        var scenes = orderedPaths.Select(path => new EditorBuildSettingsScene(path, true)).ToList();
        scenes.AddRange(EditorBuildSettings.scenes.Where(existing => !orderedPaths.Contains(existing.path)));
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    private static GameObject FindRoot(Scene scene, string rootName)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == rootName);
    }

    private static GameObject LoadPrefab(string path)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
            throw new System.InvalidOperationException($"Prefab '{path}' not found.");
        return prefab;
    }

    private static void SetBool(SerializedObject so, string property, bool value)
    {
        SerializedProperty p = so.FindProperty(property);
        if (p == null)
            throw new System.InvalidOperationException($"{so.targetObject.GetType().Name} has no serialized '{property}'.");
        p.boolValue = value;
    }

    private static void SetReference(SerializedObject so, string property, Object value)
    {
        SerializedProperty p = so.FindProperty(property);
        if (p == null)
            throw new System.InvalidOperationException($"{so.targetObject.GetType().Name} has no serialized '{property}'.");
        p.objectReferenceValue = value;
    }

    // ---------- Quest build ----------

    /// <summary>
    /// Builds a Quest APK that boots into the main menu (then VRMountains and SampleScene),
    /// without touching the build settings. Run with -buildTarget Android so
    /// the editor doesn't have to switch platforms mid-run. Optional -apkOut &lt;path&gt;.
    /// </summary>
    public static void BuildQuestApk()
    {
        BuildQuestApk(GetCommandLineArg("-apkOut") ?? "Builds/OculusQuest/XRTanks_VR.apk", BuildOptions.None);
    }

    /// <summary>Same build from the open editor, then installs and launches it on the USB-connected Quest.</summary>
    [MenuItem("XR Tanks/Build and Run on Quest")]
    public static void BuildAndRunQuestFromMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        BuildQuestApk("Builds/OculusQuest/XRTanks_VR.apk", BuildOptions.AutoRunPlayer);
    }

    private static void BuildQuestApk(string output, BuildOptions buildOptions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));

        var options = new BuildPlayerOptions
        {
            scenes = new[] { MenuScenePath, TargetScenePath, SourceScenePath },
            locationPathName = output,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = buildOptions,
        };

        UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;
        if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            throw new System.InvalidOperationException($"Quest build {summary.result}: {summary.totalErrors} errors.");

        Debug.Log($"[VRSceneBuilder] Built {output} ({summary.totalSize / (1024 * 1024)} MB) in {summary.totalTime.TotalSeconds:F0}s.");
    }

    // ---------- Terrain validation ----------

    private static readonly int[] ValidationSeeds = { 1, 2, 3, 42, 777 };
    private const int ValidationEnemies = 10;
    private const int ValidationCrates = 10;

    /// <summary>
    /// Generates the terrain for a few seeds in an empty scene, builds the same runtime NavMesh
    /// the game does, plans spawns, and logs stats. Writes a top-down PNG per seed: shaded
    /// relief, darkened where there is no NavMesh, player blue, enemies red, crates yellow.
    /// </summary>
    public static void ValidateTerrain()
    {
        string outDir = GetCommandLineArg("-validationOut")
                        ?? Path.Combine(Directory.GetCurrentDirectory(), "Temp", "TerrainValidation");
        Directory.CreateDirectory(outDir);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        foreach (int seed in ValidationSeeds)
        {
            var go = new GameObject("Validation Environment");
            var environment = go.AddComponent<TerrainEnvironment>();
            environment.ApplySpawnCounts(ValidationEnemies, ValidationCrates);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            environment.BuildTerrainImmediate(seed);
            long generateMs = timer.ElapsedMilliseconds;

            timer.Restart();
            NavMeshBuildSettings settings = GameConfigManager.CreateTankNavMeshSettings();
            environment.TuneNavMesh(ref settings);
            var sources = new List<NavMeshBuildSource>();
            UnityEngine.AI.NavMeshBuilder.CollectSources(environment.NavMeshSourceRoot, 1, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), sources);
            NavMeshData data = UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(settings, sources, environment.NavMeshBounds.Value, Vector3.zero, Quaternion.identity);
            NavMeshDataInstance instance = NavMesh.AddNavMeshData(data);
            long navMeshMs = timer.ElapsedMilliseconds;

            TerrainEnvironment.SpawnPlan plan = environment.PlanSpawns();
            string png = Path.Combine(outDir, $"terrain_seed_{seed}.png");
            MapStats stats = RenderMap(environment, plan, png);

            Debug.Log($"[TerrainValidation] seed {seed}: generate {generateMs} ms, navmesh {navMeshMs} ms, " +
                      $"height {stats.minHeight:F2}–{stats.maxHeight:F2} m, interior slope<35° {stats.drivablePercent:F0}%, " +
                      $"interior on NavMesh {stats.navMeshPercent:F0}%, " +
                      $"enemies {plan.enemies.Count}/{ValidationEnemies}, crates {plan.crates.Count}/{ValidationCrates}, " +
                      $"player at ({plan.players[0].x:F1}, {plan.players[0].y:F2}, {plan.players[0].z:F1}) slope {environment.SlopeAt(plan.players[0]):F0}° → {png}");

            instance.Remove();
            Object.DestroyImmediate(data);
            Object.DestroyImmediate(go);
        }
    }

    private struct MapStats
    {
        public float minHeight, maxHeight, drivablePercent, navMeshPercent;
    }

    private static MapStats RenderMap(TerrainEnvironment environment, TerrainEnvironment.SpawnPlan plan, string path)
    {
        const int size = 256;
        MountainTerrainGenerator.Settings s = environment.TerrainSettings;
        Vector3 origin = environment.transform.position - new Vector3(s.size, 0f, s.size) * 0.5f;
        float cell = s.size / size;
        float interiorHalf = s.size * 0.5f - s.rimWidth;
        Vector3 lightDir = new Vector3(-1f, 1.5f, 1f).normalized;

        var stats = new MapStats { minHeight = float.MaxValue, maxHeight = float.MinValue };
        int interior = 0, drivable = 0, onNavMesh = 0;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector3 p = origin + new Vector3((x + 0.5f) * cell, 0f, (y + 0.5f) * cell);
                float h = environment.GetGroundHeight(p);
                p.y = h;
                stats.minHeight = Mathf.Min(stats.minHeight, h);
                stats.maxHeight = Mathf.Max(stats.maxHeight, h);

                float hx = environment.GetGroundHeight(p + Vector3.right * cell) - h;
                float hz = environment.GetGroundHeight(p + Vector3.forward * cell) - h;
                Vector3 normal = new Vector3(-hx, cell, -hz).normalized;
                float shade = 0.45f + 0.55f * Mathf.Clamp01(Vector3.Dot(normal, lightDir));

                float t = h / s.maxHeight;
                Color c = t < 0.35f
                    ? Color.Lerp(new Color(0.33f, 0.52f, 0.20f), new Color(0.47f, 0.40f, 0.28f), t / 0.35f)
                    : Color.Lerp(new Color(0.47f, 0.45f, 0.44f), Color.white, (t - 0.35f) / 0.65f);

                bool navigable = NavMesh.SamplePosition(p, out _, 0.1f, NavMesh.AllAreas);
                if (!navigable)
                    c *= 0.6f;

                Vector3 fromCenter = p - environment.transform.position;
                if (Mathf.Abs(fromCenter.x) < interiorHalf && Mathf.Abs(fromCenter.z) < interiorHalf)
                {
                    interior++;
                    if (environment.SlopeAt(p) < 35f) drivable++;
                    if (navigable) onNavMesh++;
                }

                c *= shade;
                c.a = 1f;
                texture.SetPixel(x, y, c);
            }
        }

        foreach (Vector3 crate in plan.crates) DrawMarker(texture, origin, cell, crate, Color.yellow, 2);
        foreach (Vector3 enemy in plan.enemies) DrawMarker(texture, origin, cell, enemy, Color.red, 3);
        foreach (Vector3 player in plan.players) DrawMarker(texture, origin, cell, player, new Color(0.2f, 0.5f, 1f), 4);

        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);

        stats.drivablePercent = interior > 0 ? 100f * drivable / interior : 0f;
        stats.navMeshPercent = interior > 0 ? 100f * onNavMesh / interior : 0f;
        return stats;
    }

    private static void DrawMarker(Texture2D texture, Vector3 origin, float cell, Vector3 world, Color color, int radius)
    {
        int cx = Mathf.FloorToInt((world.x - origin.x) / cell);
        int cy = Mathf.FloorToInt((world.z - origin.z) / cell);
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= texture.width || y >= texture.height) continue;
                bool border = Mathf.Abs(dx) == radius || Mathf.Abs(dy) == radius;
                texture.SetPixel(x, y, border ? Color.black : color);
            }
        }
    }

    private static string GetCommandLineArg(string name)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }
}
