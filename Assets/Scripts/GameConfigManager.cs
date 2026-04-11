using System.Collections;
using Meta.XR.ImmersiveDebugger;
using Meta.XR.ImmersiveDebugger.UserInterface;
using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
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
        StartCoroutine(SubscribeToDebugPanel());
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_debugInterface != null)
        {
            _debugInterface.OnVisibilityChangedEvent -= OnDebugPanelVisibilityChanged;
        }
        Time.timeScale = 1f;
    }

    private DebugInterface _debugInterface;

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
        Debug.Log("[GameConfig] Subscribed to Immersive Debugger panel visibility.");
    }

    private void OnDebugPanelVisibilityChanged(Controller controller)
    {
        Time.timeScale = controller.Visibility ? 0f : 1f;
        Debug.Log($"[GameConfig] Debug panel {(controller.Visibility ? "opened" : "closed")}, timeScale={Time.timeScale}");
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

        // Raycast down from headset position to find the actual floor
        Vector3 headsetPos = cam.transform.position;
        Vector3 rayOrigin = new Vector3(headsetPos.x, headsetPos.y + 1f, headsetPos.z);
        float floorY = playerTank.transform.position.y; // fallback

        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 10f))
        {
            floorY = hit.point.y;
        }

        playerTank.transform.position = new Vector3(headsetPos.x, floorY, headsetPos.z);

        // Face the tank in the headset's forward direction (projected to horizontal)
        Vector3 forward = cam.transform.forward;
        forward.y = 0;
        if (forward.sqrMagnitude > 0.001f)
        {
            playerTank.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        // Reset Rigidbody so physics doesn't fight the new position
        var rb = playerTank.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
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
