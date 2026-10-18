using System.Collections;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Mixed-reality world: the player's real room, scanned by MRUK and seen through
/// passthrough. Enemies and crates are spawned by the scene's MRUK
/// <see cref="FindSpawnPositions"/> (SpawnOnStart); the player tank is spawned here.
///
/// SampleScene predates the environment split and carries no GameEnvironment, so
/// GameConfigManager adds this one at runtime to any scene that lacks one.
/// </summary>
public class MRRoomEnvironment : GameEnvironment
{
    // Several candidates are spawned and the one closest to the headset is kept, so the
    // player starts near themselves instead of wherever MRUK's first random pick landed.
    private const int PlayerCandidateCount = 5;

    private MRUKRoom _room;
    private float? _floorY;

    public override bool SuppressBoundary => true;

    public override Transform NavMeshSourceRoot => _room != null ? _room.transform : null;

    public override Bounds? NavMeshBounds
    {
        get
        {
            if (_room != null)
                return null;

            // No room: the only floor collider is the 1000 m fallback Floor plane, so bound
            // the build to a room-sized volume around the player instead of the whole plane.
            Camera cam = Camera.main;
            Vector3 center = cam != null ? cam.transform.position : Vector3.zero;
            return new Bounds(center, new Vector3(20f, 10f, 20f));
        }
    }

    public override void ApplySpawnCounts(int enemies, int crates)
    {
        foreach (var spawner in FindObjectsByType<FindSpawnPositions>(FindObjectsSortMode.None))
        {
            if (spawner.SpawnObject == null) continue;

            string objName = spawner.SpawnObject.name;

            if (objName.Contains("TargetTank"))
                spawner.SpawnAmount = enemies;
            else if (objName.Contains("TargetBox"))
                spawner.SpawnAmount = crates;
        }
    }

    public override IEnumerator PrepareWorld()
    {
        // Wait for MRUK room to be available
        for (int i = 0; i < 600; i++)
        {
            if (MRUK.Instance != null)
                _room = MRUK.Instance.GetCurrentRoom();
            if (_room != null) break;
            yield return null;
        }

        if (_room == null)
        {
            Debug.LogWarning("[GameConfig] MRUK room not found; using fallback Floor collider and editor tank spawn.");
            ConfigureFallbackFloor(true, null);
            yield break;
        }

        // Align the fallback scene Floor visually to the MRUK floor, but disable it during MRUK-based spawning
        // so the large plane does not affect spawn selection or room-bounded navigation.
        MRUKAnchor floorAnchor = _room.FloorAnchor;
        if (floorAnchor != null)
        {
            _floorY = floorAnchor.transform.position.y;
            ConfigureFallbackFloor(false, _floorY);
        }

        // Wait for Effect Mesh colliders to generate
        for (int i = 0; i < 60; i++)
        {
            if (Physics.Raycast(new Vector3(0, 5f, 0), Vector3.down, 10f))
                break;
            yield return null;
        }
    }

    public override IEnumerator SpawnActors()
    {
        FindSpawnPositions tankSpawner = FindPlayerTankSpawner();

        if (_room == null)
        {
            SpawnPlayerTankFallback(tankSpawner);
            yield break;
        }

        if (tankSpawner == null)
        {
            Debug.LogWarning("[GameConfig] TankSpawner not found.");
            yield break;
        }

        tankSpawner.SpawnAmount = PlayerCandidateCount;
        tankSpawner.StartSpawn(_room);

        // Re-enable the stable gameplay floor immediately after spawn placement.
        if (_floorY.HasValue)
            ConfigureFallbackFloor(true, _floorY);

        // Wait for tanks to appear
        ShootingControls[] candidates = null;
        for (int i = 0; i < 120; i++)
        {
            candidates = FindObjectsByType<ShootingControls>(FindObjectsSortMode.None);
            if (candidates.Length >= PlayerCandidateCount) break;
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
            FaceHorizontally(closest.transform, cam.transform.forward);

        // Ensure the bottom of the tank sits slightly above the MRUK floor
        if (_floorY.HasValue)
            SnapBottomToHeight(closest.gameObject, _floorY.Value);
    }

    private static FindSpawnPositions FindPlayerTankSpawner()
    {
        foreach (var spawner in FindObjectsByType<FindSpawnPositions>(FindObjectsSortMode.None))
        {
            if (spawner.SpawnObject != null && spawner.SpawnObject.name.Contains("TankFree"))
                return spawner;
        }
        return null;
    }

    private static void ConfigureFallbackFloor(bool enableCollider, float? floorY)
    {
        GameObject floorObj = GameConfigManager.Instance != null ? GameConfigManager.Instance.GetGameplayFloor() : null;
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

    private static void SpawnPlayerTankFallback(FindSpawnPositions tankSpawner)
    {
        if (tankSpawner == null || tankSpawner.SpawnObject == null)
        {
            Debug.LogWarning("[GameConfig] TankSpawner not found for fallback spawn.");
            return;
        }

        Camera cam = Camera.main;
        Vector3 spawnPos = cam != null ? cam.transform.position + cam.transform.forward * 2f : new Vector3(0f, 0f, 2f);

        GameObject floorObj = GameConfigManager.Instance != null ? GameConfigManager.Instance.GetGameplayFloor() : null;
        if (floorObj != null)
            spawnPos.y = floorObj.transform.position.y + 0.01f;

        Vector3 forward = cam != null ? cam.transform.forward : Vector3.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;

        GameObject tank = Instantiate(tankSpawner.SpawnObject, spawnPos, Quaternion.LookRotation(forward, Vector3.up));
        if (floorObj != null)
            SnapBottomToHeight(tank, floorObj.transform.position.y);
    }
}
