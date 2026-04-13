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
    public int projectileMinSpeed = 5;

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
    }

    private void OnDebugPanelVisibilityChanged(Controller controller)
    {
        Time.timeScale = controller.Visibility ? 0f : 1f;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        FindAndApplySpawnerConfig();
        StartCoroutine(SpawnPlayerTankNearHeadset());
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
            Debug.LogWarning("[GameConfig] MRUK room not found, cannot spawn player tank.");
            yield break;
        }

        // Position the Floor object at the MRUK floor level as a solid fallback
        MRUKAnchor floorAnchor = room.FloorAnchor;
        if (floorAnchor != null)
        {
            float floorY = floorAnchor.transform.position.y;
            GameObject floorObj = GameObject.Find("Floor");
            if (floorObj != null)
                floorObj.transform.position = new Vector3(0, floorY, 0);
        }

        // Wait for Effect Mesh colliders to generate
        for (int i = 0; i < 60; i++)
        {
            if (Physics.Raycast(new Vector3(0, 5f, 0), Vector3.down, 10f))
                break;
            yield return null;
        }

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
    }

    [DebugMember(Category = "Game Config")]
    public void ReloadScene()
    {
        Scene active = SceneManager.GetActiveScene();
        SceneManager.LoadScene(active.name);
    }
}
