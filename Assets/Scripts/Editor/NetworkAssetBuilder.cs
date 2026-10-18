using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generates everything online play needs, leaving the offline prefabs untouched:
/// <list type="bullet">
/// <item>network variants (<c>*_Net</c>) of the player tank, AI tank variants, crate and every
/// projectile — the same prefabs plus NetworkObject / NetworkTransform / adapters;</item>
/// <item><see cref="NetworkPrefabMap"/> (offline → online) in Resources;</item>
/// <item>the OnlineMatchSync prefab, Netcode's prefab list, and the NetworkBootstrap prefab
/// (NetworkManager + UnityTransport) the menu instantiates.</item>
/// </list>
/// Idempotent: existing variants are updated in place, so their asset GUIDs (and therefore
/// Netcode's prefab hashes) stay stable across rebuilds.
/// </summary>
public static class NetworkAssetBuilder
{
    private const string NetworkFolder = "Assets/Prefabs/Network";
    private const string BootstrapPath = NetworkFolder + "/NetworkBootstrap.prefab";
    private const string SyncPath = NetworkFolder + "/OnlineMatchSync.prefab";
    private const string PrefabListPath = NetworkFolder + "/XRTanksNetworkPrefabs.asset";
    private const string PrefabMapPath = "Assets/Resources/" + NetworkPrefabMap.ResourcePath + ".asset";

    private const string PlayerTankPath = "Assets/Prefabs/TankFree_Red.prefab";
    private const string EnemyRandomizerPath = "Assets/Prefabs/TargetTankRandomizer.prefab";
    private const string CratePath = "Assets/Prefabs/TargetBox.prefab";

    public struct Result
    {
        public GameObject bootstrap;
        public GameObject matchSync;
    }

    public static Result Build()
    {
        EnsureFolder(NetworkFolder);
        EnsureFolder("Assets/Resources");

        var entries = new List<NetworkPrefabMap.Entry>();
        var networkPrefabs = new List<GameObject>();

        void Register(GameObject offline, GameObject online)
        {
            entries.Add(new NetworkPrefabMap.Entry { offline = offline, online = online });
            networkPrefabs.Add(online);
        }

        // Projectiles: everything the player tank cycles through, plus what enemies fire.
        GameObject playerTank = Load(PlayerTankPath);
        var enemyVariants = ReadEnemyVariants();
        var projectiles = new List<GameObject>(ReadObjectArray(playerTank.GetComponent<ShootingControls>(), "projectilePrefabs"));
        foreach (GameObject enemy in enemyVariants)
        {
            var enemyProjectile = ReadObject(enemy.GetComponent<EnemyTankAI>(), "enemyProjectilePrefab") as GameObject;
            if (enemyProjectile != null && !projectiles.Contains(enemyProjectile))
                projectiles.Add(enemyProjectile);
        }
        foreach (GameObject projectile in projectiles.Where(p => p != null).Distinct())
            Register(projectile, MakeVariant(projectile, ConfigureProjectile));

        Register(playerTank, MakeVariant(playerTank, ConfigurePlayerTank));
        foreach (GameObject enemy in enemyVariants)
            Register(enemy, MakeVariant(enemy, ConfigureEnemyTank));
        GameObject crate = Load(CratePath);
        Register(crate, MakeVariant(crate, ConfigureCrate));

        GameObject sync = BuildMatchSyncPrefab();
        networkPrefabs.Add(sync);

        NetworkPrefabsList prefabList = BuildPrefabList(networkPrefabs);
        GameObject bootstrap = BuildBootstrap(prefabList);
        BuildPrefabMap(entries);

        AssetDatabase.SaveAssets();
        Debug.Log($"[NetworkAssetBuilder] {networkPrefabs.Count} network prefabs, bootstrap and prefab map up to date.");
        return new Result { bootstrap = bootstrap, matchSync = sync };
    }

    // ---------- Variant configuration (must be idempotent) ----------

    private static void ConfigurePlayerTank(GameObject root)
    {
        Ensure<NetworkObject>(root);
        // The owner's headset drives its tank; everyone else follows.
        ConfigureTransform(Ensure<NetworkTransform>(root), NetworkTransform.AuthorityModes.Owner, localRotationOnly: false);
        Ensure<NetworkRigidbody>(root);
        Ensure<NetworkTarget>(root);
        Ensure<NetworkTankControl>(root);

        var tower = ReadObject(root.GetComponent<TowerController>(), "towerBase") as GameObject;
        if (tower != null)
            ConfigureTransform(Ensure<NetworkTransform>(tower), NetworkTransform.AuthorityModes.Owner, localRotationOnly: true);
        else
            Debug.LogWarning("[NetworkAssetBuilder] Player tank has no towerBase; the turret won't sync.");
    }

    private static void ConfigureEnemyTank(GameObject root)
    {
        Ensure<NetworkObject>(root);
        // The host runs the AI; clients get a puppet.
        ConfigureTransform(Ensure<NetworkTransform>(root), NetworkTransform.AuthorityModes.Server, localRotationOnly: false);
        Ensure<NetworkTarget>(root);
        Ensure<NetworkTankControl>(root);

        // Same lookup as EnemyTankAI.FindTurretTransform; the body model is its top-level ancestor.
        Transform turret = root.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(t => t.name.ToLowerInvariant().Contains("tower") || t.name.ToLowerInvariant().Contains("turret"));
        if (turret == null)
        {
            Debug.LogWarning($"[NetworkAssetBuilder] {root.name}: no turret found; it won't sync.");
            return;
        }

        Transform body = turret;
        while (body.parent != null && body.parent != root.transform)
            body = body.parent;
        if (body != turret)
            ConfigureTransform(Ensure<NetworkTransform>(body.gameObject), NetworkTransform.AuthorityModes.Server, localRotationOnly: true);
        ConfigureTransform(Ensure<NetworkTransform>(turret.gameObject), NetworkTransform.AuthorityModes.Server, localRotationOnly: true);
    }

    private static void ConfigureCrate(GameObject root)
    {
        // Static apart from the local bobbing (Floater), so no transform sync.
        Ensure<NetworkObject>(root);
        Ensure<NetworkTarget>(root);
    }

    private static void ConfigureProjectile(GameObject root)
    {
        Ensure<NetworkObject>(root);
        ConfigureTransform(Ensure<NetworkTransform>(root), NetworkTransform.AuthorityModes.Server, localRotationOnly: false);
        Ensure<NetworkRigidbody>(root);
        Ensure<NetworkProjectile>(root);
    }

    private static void ConfigureTransform(NetworkTransform transform, NetworkTransform.AuthorityModes authority, bool localRotationOnly)
    {
        transform.AuthorityMode = authority;
        transform.SyncScaleX = transform.SyncScaleY = transform.SyncScaleZ = false;
        if (localRotationOnly)
        {
            transform.InLocalSpace = true;
            transform.SyncPositionX = transform.SyncPositionY = transform.SyncPositionZ = false;
        }
        EditorUtility.SetDirty(transform);
    }

    // ---------- Shared assets ----------

    private static GameObject BuildMatchSyncPrefab()
    {
        var go = new GameObject("OnlineMatchSync");
        go.AddComponent<NetworkObject>();
        go.AddComponent<OnlineMatchSync>();
        GameObject prefab = SaveOrReplace(go, SyncPath);
        Object.DestroyImmediate(go);
        return prefab;
    }

    private static NetworkPrefabsList BuildPrefabList(List<GameObject> prefabs)
    {
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(PrefabListPath);
        if (list == null)
        {
            list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            AssetDatabase.CreateAsset(list, PrefabListPath);
        }

        foreach (NetworkPrefab existing in list.PrefabList.ToList())
            list.Remove(existing);
        foreach (GameObject prefab in prefabs)
            list.Add(new NetworkPrefab { Prefab = prefab });

        EditorUtility.SetDirty(list);
        return list;
    }

    private static GameObject BuildBootstrap(NetworkPrefabsList prefabList)
    {
        var go = new GameObject("NetworkBootstrap");
        var manager = go.AddComponent<NetworkManager>();
        var transport = go.AddComponent<UnityTransport>();
        manager.NetworkConfig.NetworkTransport = transport;
        manager.NetworkConfig.ConnectionApproval = false;
        manager.NetworkConfig.Prefabs.NetworkPrefabsLists = new List<NetworkPrefabsList> { prefabList };

        GameObject prefab = SaveOrReplace(go, BootstrapPath);
        Object.DestroyImmediate(go);
        return prefab;
    }

    private static void BuildPrefabMap(List<NetworkPrefabMap.Entry> entries)
    {
        var map = AssetDatabase.LoadAssetAtPath<NetworkPrefabMap>(PrefabMapPath);
        if (map == null)
        {
            map = ScriptableObject.CreateInstance<NetworkPrefabMap>();
            AssetDatabase.CreateAsset(map, PrefabMapPath);
        }

        map.Entries = entries.ToArray();
        EditorUtility.SetDirty(map);
    }

    // ---------- Helpers ----------

    /// <summary>Creates (or updates in place) <c>&lt;source&gt;_Net</c>, a prefab variant of <paramref name="source"/>.</summary>
    private static GameObject MakeVariant(GameObject source, System.Action<GameObject> configure)
    {
        string path = $"{NetworkFolder}/{source.name}_Net.prefab";
        if (!File.Exists(path))
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            configure(instance);
            PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
        }
        else
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            configure(contents);
            PrefabUtility.SaveAsPrefabAsset(contents, path);
            PrefabUtility.UnloadPrefabContents(contents);
        }
        return Load(path);
    }

    private static GameObject SaveOrReplace(GameObject go, string path)
    {
        // Overwriting by path keeps the asset's GUID.
        return PrefabUtility.SaveAsPrefabAsset(go, path);
    }

    private static List<GameObject> ReadEnemyVariants()
    {
        var randomizer = Load(EnemyRandomizerPath).GetComponent<RandomEnemyVariant>();
        var result = new List<GameObject>();
        SerializedProperty variants = new SerializedObject(randomizer).FindProperty("variants");
        for (int i = 0; i < variants.arraySize; i++)
        {
            var prefab = variants.GetArrayElementAtIndex(i).FindPropertyRelative("prefab").objectReferenceValue as GameObject;
            if (prefab != null && !result.Contains(prefab))
                result.Add(prefab);
        }
        return result;
    }

    private static Object ReadObject(Object owner, string property)
    {
        return owner == null ? null : new SerializedObject(owner).FindProperty(property)?.objectReferenceValue;
    }

    private static IEnumerable<GameObject> ReadObjectArray(Object owner, string property)
    {
        SerializedProperty array = owner == null ? null : new SerializedObject(owner).FindProperty(property);
        if (array == null)
            yield break;
        for (int i = 0; i < array.arraySize; i++)
            yield return array.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
    }

    private static T Ensure<T>(GameObject go) where T : Component
    {
        return go.TryGetComponent(out T existing) ? existing : go.AddComponent<T>();
    }

    private static GameObject Load(string path)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null)
            throw new System.InvalidOperationException($"Prefab '{path}' not found.");
        return asset;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
