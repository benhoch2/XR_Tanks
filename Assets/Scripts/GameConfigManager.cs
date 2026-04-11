using System.Collections;
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
        StartCoroutine(MovePlayerTankToHeadset());
    }

    private void FindAndApplySpawnerConfig()
    {
        foreach (var spawner in FindObjectsByType<FindSpawnPositions>(FindObjectsSortMode.None))
        {
            if (spawner.SpawnObject == null) continue;

            string objName = spawner.SpawnObject.name;

            if (objName.Contains("TargetTank"))
            {
                spawner.SpawnAmount = numberOfEnemies;
                Debug.Log($"[GameConfig] Set enemy spawner ({objName}) to {numberOfEnemies}");
            }
            else if (objName.Contains("TargetBox"))
            {
                spawner.SpawnAmount = numberOfCrates;
                Debug.Log($"[GameConfig] Set crate spawner ({objName}) to {numberOfCrates}");
            }
        }
    }

    private IEnumerator MovePlayerTankToHeadset()
    {
        // Wait for MRUK to spawn the player tank (it spawns via a scene-loaded callback)
        ShootingControls playerTank = null;
        for (int i = 0; i < 300; i++) // wait up to ~5 seconds
        {
            playerTank = FindAnyObjectByType<ShootingControls>();
            if (playerTank != null) break;
            yield return null;
        }

        if (playerTank == null)
        {
            Debug.LogWarning("[GameConfig] Player tank not found, cannot reposition near headset.");
            yield break;
        }

        Camera cam = Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[GameConfig] Main camera not found, cannot reposition tank near headset.");
            yield break;
        }

        // Place the tank at the headset's XZ position, keeping its spawned Y (floor level)
        Vector3 headsetPos = cam.transform.position;
        Vector3 tankPos = playerTank.transform.position;
        playerTank.transform.position = new Vector3(headsetPos.x, tankPos.y, headsetPos.z);

        // Face the tank in the headset's forward direction (projected to horizontal)
        Vector3 forward = cam.transform.forward;
        forward.y = 0;
        if (forward.sqrMagnitude > 0.001f)
        {
            playerTank.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        Debug.Log($"[GameConfig] Moved player tank to headset position: {playerTank.transform.position}");
    }

    [DebugMember(Category = "Game Config")]
    public void ReloadScene()
    {
        Debug.Log("Reloading scene...");
        Scene active = SceneManager.GetActiveScene();
        SceneManager.LoadScene(active.name);
    }
}
