using Meta.XR.ImmersiveDebugger;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameConfigManager : MonoBehaviour
{
    public static GameConfigManager Instance { get; private set; }

    [Header("Spawning")]
    [DebugMember(Min = 1, Max = 50, Category = "Game Config")]
    public int numberOfEnemies = 10;

    [DebugMember(Min = 1, Max = 50, Category = "Game Config")]
    public int numberOfCrates = 10;

    [Header("Projectile")]
    [DebugMember(Min = 1, Max = 50, Category = "Game Config")]
    public int projectileMinSpeed = 1;

    [DebugMember(Min = 1, Max = 100, Category = "Game Config")]
    public int projectileMaxSpeed = 20;

    [Header("Power")]
    [DebugMember(Min = 1, Max = 10, Category = "Game Config")]
    public int powerUpDuration = 2;

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
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        FindAndApplySpawnerConfig();
    }

    private void FindAndApplySpawnerConfig()
    {
        // Re-find spawners in the new scene by tag or name
        foreach (var spawner in FindObjectsByType<FindSpawnPositions>(FindObjectsSortMode.None))
        {
            var go = spawner.gameObject;
            if (go.name.Contains("Tank"))
            {
                spawner.SpawnAmount = numberOfEnemies;
                Debug.Log($"[GameConfig] Set enemy spawner to {numberOfEnemies}");
            }
            else if (go.name.Contains("Target"))
            {
                spawner.SpawnAmount = numberOfCrates;
                Debug.Log($"[GameConfig] Set crate spawner to {numberOfCrates}");
            }
        }
    }

    [DebugMember(Category = "Game Config")]
    public void ReloadScene()
    {
        Debug.Log("Reloading scene...");
        Scene active = SceneManager.GetActiveScene();
        SceneManager.LoadScene(active.name);
    }
}
