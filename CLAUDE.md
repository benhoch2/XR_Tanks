# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

XR Tanks is a Unity 6 (`6000.3.12f1`) Mixed Reality game for Meta Quest. The player drives a tank around their real room, spawned onto the MRUK (Meta XR Mixed Reality Utility Kit) scene mesh, and shoots enemy tanks and crates. Rendering is URP. Input uses the new Input System with direct `<XRController>` bindings (no generated action asset wrapper).

Entry scene: `Assets/Scenes/SampleScene.unity` — this is the only gameplay scene.

## Build / Run

There are no CLI build scripts or tests. Development happens in the Unity Editor:

- Open the project in Unity `6000.3.12f1` (see `ProjectSettings/ProjectVersion.txt`).
- Play mode in the editor works with the Meta XR Simulator / link cable. MRUK room data is required — without it, `GameConfigManager` falls back to a "Floor" GameObject and spawns the player tank in front of the main camera (see `SpawnPlayerTankFallback`).
- Builds output to `Builds/OculusPC/` or `Builds/OculusQuest/` (gitignored).
- Controller mappings are documented in `CONTROLS.md` and mirrored in `GameConfigManager` as read-only `[DebugMember]` strings that appear in the in-headset config menu.

## Runtime Architecture

### GameConfigManager is the bootstrapper

`Assets/Scripts/GameConfigManager.cs` is a `DontDestroyOnLoad` singleton that owns scene startup and pushes config to every other system. Read this file first when you need to understand *when* something happens — most gameplay initialization is a coroutine sequence driven from here, not from individual component `Start()` methods.

Startup flow (`BeginSceneStartup` → `SpawnPlayerTankNearHeadset`):
1. Suppress the Oculus guardian boundary on all `OVRManager`s.
2. Apply `numberOfEnemies` / `numberOfCrates` to every MRUK `FindSpawnPositions` by matching the spawner's `SpawnObject.name` against `"TargetTank"` / `"TargetBox"`.
3. Wait (polling, up to ~10s) for `MRUK.Instance.GetCurrentRoom()`. If it never appears, fall through to fallback spawn.
4. Toggle the fallback `Floor` GameObject's collider off during MRUK-based spawn selection so it doesn't bias placement, then re-enable it at the room's floor Y for stable gameplay physics.
5. Build a runtime NavMesh via `NavMeshBuilder.BuildNavMeshData` with tank-scale settings (`agentRadius 0.12`, `agentHeight 0.25`, `voxelSize 0.03`). Call `_navMeshDataInstance.Remove()` before rebuilding — the field is tracked so scene reloads clean up.
6. Spawn 5 player-tank candidates via the `TankFree` spawner, pick the one closest to the headset (horizontal distance), destroy the rest, face it toward the headset's forward, and snap it to sit 1cm above the MRUK floor.
7. Wait for enemy `Target`s to appear, then `ConfigureEnemyTankMovementWhenReady` warps each onto the NavMesh, enables/tunes its `NavMeshAgent`, attaches or configures its `EnemyTankAI`, and wires `WheelRotation.tankBody` to the moving root.

Scene reload: `ReloadScene()` is exposed as an Immersive Debugger button. `OnSceneLoaded` re-runs the whole startup and tears down any stale NavMesh data.

### Config flow: GameConfigManager → gameplay components

Per-frame, several components pull their values from the singleton rather than using their serialized inspector defaults:

- `EnemyTankAI.SyncConfigFromManager` overwrites `moveSpeed`, `chaseRange`, `stopRange`, `obstacleCheckDistance`, scan parameters, and `behaviorMode` each `Update()`. Inspector values on the prefab are effectively just defaults; the source of truth is `GameConfigManager.Instance`. Fields on `EnemyTankAI` that come from the manager are marked `[HideInInspector]` for this reason — do not re-expose them without also disabling the sync.
- `ShootingControls.Start()` reads `powerUpDuration`, `projectileMinSpeed`, `projectileMaxSpeed` once.

`enemyAIMode` is an `int` (0 = ChaseWander, 1 = PatrolScan) because `[DebugMember(Min=0,Max=1)]` only supports numeric sliders in the Immersive Debugger — it gets mapped to `EnemyTankAI.BehaviorMode` at both read sites.

### In-headset config menu (Immersive Debugger)

The Y button on the left controller opens Meta's Immersive Debugger panel, which auto-populates from `[DebugMember]` attributes on `GameConfigManager` (categorized as "Game Config" and "Controls"). When the panel's visibility toggles, `OnDebugPanelVisibilityChanged` sets `Time.timeScale` to 0 / 1 if `pauseWhenConfigMenuOpen` is true. This is why `GameConfigManager` waits up to 10 seconds in a coroutine for the `DebugInterface` to exist — the panel is created lazily and may start inactive.

### Player control split

`TowerController` (on the tank root) owns both driving *and* turret rotation/tilt — don't be misled by the class name. `ShootingControls` owns firing. `TankFlip` owns the hold-B-to-reset behavior. Each script creates its own `InputAction` instances directly against `<XRController>` paths in `OnEnable` and disposes them in `OnDisable`; there is no shared action asset wiring these together.

### Projectile / damage model

`Projectile` has three `ProjectileType`s with different collision behavior:
- **Standard (gray):** damages `Target`s on direct hit; on first non-target hit, starts a 5s fuse then explodes.
- **Explosive (blue):** explodes immediately on any non-target surface. Special case in `OnCollisionEnter`: floor-like hits (`FLOOR`, `Floor`, `FLOOR_EffectMesh`, `GLOBAL_MESH`) are *ignored* if they happen more than `elevatedFloorIgnoreThreshold` (5cm) above the gameplay `Floor`. Without this, MRUK effect-mesh colliders at bouncing altitudes would detonate shots mid-air.
- **Teleport (red):** teleports the `shooter` transform to the impact point.

`Target` handles both `OnCollisionEnter` and `OnTriggerEnter` so it works whether the projectile's collider or the target's is the trigger. `maxHitPoints <= 0` or `projectile.damage <= 0` is the "instant kill" path used by crates; positive HP drives the attached `PowerBar` health bar (which uses `useColorLerp` for green→red).

### PowerBar is also the health bar

`PowerBar` is used for both the charging indicator on the player tank and per-enemy health bars. It billboards toward the main camera (or a `CameraRig`-named camera) and hides both bar renderers entirely when power is 0. When reusing it, set `useColorLerp = true` for health (green at full, red at empty).

## Gotchas

- `EnemyTankAI` requires `NavMeshAgent.updateRotation = false` because the class implements its own tank-style steering (separate rotate + forward-move with reverse/turn penalties) in `DriveLikeTank`. The agent provides pathing (`steeringTarget`) but does not move or rotate the transform on its own.
- The runtime NavMesh only includes colliders on the `Default` layer (`NavMeshBuilder.CollectSources(..., LayerMask.GetMask("Default"), ...)`). If new obstacle layers are added, update that call.
- Projectile prefabs are cycled by *index* in `ShootingControls.projectilePrefabs` (A button cycles). Preview spawning re-parents a kinematic copy onto the tank with scale `previewScale`; destroy the old preview before creating a new one.
- There is no `.cursor/rules/`, `.cursorrules`, or Copilot instructions file — this is the only repo-level guidance file.
- `Assets/_Recovery/` contains auto-recovered scene files; ignore them unless explicitly restoring work.
