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

    [Header("Spawners (assign in Inspector)")]
    [SerializeField] private FindSpawnPositions enemyTankSpawner;
    [SerializeField] private FindSpawnPositions crateSpawner;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // Copy persisted values from surviving instance
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        ApplySpawnerConfig();
    }

    private void ApplySpawnerConfig()
    {
        if (enemyTankSpawner != null)
            enemyTankSpawner.SpawnAmount = numberOfEnemies;

        if (crateSpawner != null)
            crateSpawner.SpawnAmount = numberOfCrates;
    }

    [DebugMember(Category = "Game Config")]
    public void ReloadScene()
    {
        Debug.Log("Reloading scene...");
        Scene active = SceneManager.GetActiveScene();
        SceneManager.LoadScene(active.name);
    }
}
