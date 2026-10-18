# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

XR Tanks is a Unity 6 (`6000.3.12f1`) game for Meta Quest. The player drives a small (~13 cm) tank and shoots enemy tanks and crates. Rendering is URP. Input uses the new Input System with direct `<XRController>` bindings (no generated action asset wrapper).

Scenes (build order):
- `Assets/Scenes/MainMenu.unity` — **start screen** (`MainMenu`, laser + trigger): MR real-time, VR real-time, or VR turn-based with 1–4 hot-seat players and 1–6 AI tanks. Writes the choice to the static `GameSession` (mode + counts), which every scene reads. No GameConfigManager here; the DontDestroyOnLoad one from an earlier game skips startup when it sees `MainMenu`.
- `Assets/Scenes/SampleScene.unity` — **MR**: the player's real room under passthrough, spawned onto the MRUK (Meta XR Mixed Reality Utility Kit) scene mesh.
- `Assets/Scenes/VRMountains.unity` — **pure VR** (no passthrough, no MRUK): a procedural mountain valley, roughly 1:50 scale, with the player as a "giant" whose camera rig follows their tank. Plays real-time or turn-based (`GameSession.Mode`).

MainMenu and VRMountains are **generated** by `Assets/Scripts/Editor/VRMountainSceneBuilder.cs` (menu *XR Tanks ▸ Build VR Scenes*) from copies of SampleScene, and the builder sets the build order. Re-run it after changing SampleScene's shared objects (camera rig, GameConfigManager, light) instead of hand-editing the copies. Launching a scene directly in the editor uses `GameSession` defaults (real-time).

Immersive Debugger buttons `BackToMenu` and `SwitchMRVR` move between scenes.

## Build / Run

There are no CLI build scripts or tests. Development happens in the Unity Editor:

- Open the project in Unity `6000.3.12f1` (see `ProjectSettings/ProjectVersion.txt`).
- Play mode in the editor works with the Meta XR Simulator / link cable. In the MR scene, MRUK room data is required — without it, `MRRoomEnvironment` falls back to the "Floor" GameObject and spawns the player tank in front of the main camera (no enemies spawn in that fallback). The VR scene needs no room data.
- Batch mode (editor closed): `Unity.exe -batchmode -projectPath . -executeMethod VRMountainSceneBuilder.BuildFromCommandLine -quit` compiles everything and regenerates the VR scenes; `-executeMethod VRMountainSceneBuilder.ValidateTerrain -validationOut <dir> -quit` generates 5 seeds, builds the runtime NavMesh, plans spawns, logs stats and writes top-down PNG maps; `-buildTarget Android -executeMethod VRMountainSceneBuilder.BuildQuestApk -quit` builds `Builds/OculusQuest/XRTanks_VR.apk`. With the editor open, *XR Tanks ▸ Build and Run on Quest* does the APK build + install + launch.
- The project has the MCP for Unity package (`com.coplaydev.unity-mcp`, HTTP server on `127.0.0.1:8080`), so with the editor open Claude can compile, run builder methods, enter Play mode and read the console. To play-test without a headset, use `XRTestInput` (`Assets/Scripts/Editor/XRTestInput.cs`) in Play mode: `Connect()` adds virtual left/right `OculusTouchController`s that the game's `<XRController>{LeftHand|RightHand}` bindings read like real ones (`Press`, `Axis`, `Stick`, `ReleaseAll`), `StandUp()` lifts the head to eye height, and `AimHand(hand, worldPoint)` points a hand anchor (menu laser). With no XR runtime running nothing overwrites the anchors. Input events apply on the next frame, so press and release in separate calls. Save screenshots under `Temp/Captures` (the MCP only writes inside the project).
- Builds output to `Builds/OculusPC/` or `Builds/OculusQuest/` (gitignored).
- Controller mappings are documented in `CONTROLS.md` and mirrored in `GameConfigManager` as read-only `[DebugMember]` strings that appear in the in-headset config menu. Keep the two in sync when a binding changes.

## Runtime Architecture

### GameConfigManager is the bootstrapper

`Assets/Scripts/GameConfigManager.cs` is a `DontDestroyOnLoad` singleton that owns scene startup. Read this file first when you need to understand *when* something happens — most gameplay initialization is a coroutine sequence driven from here, not from individual component `Start()` methods.

Everything that differs between MR and VR lives on a per-scene `GameEnvironment` (`Assets/Scripts/GameEnvironment.cs`), **not** on manager fields — the manager outlives scene switches, so its serialized state comes from whichever scene booted first. `BeginSceneStartup` finds the scene's environment; a scene without one (SampleScene) gets an `MRRoomEnvironment` created at runtime.

Startup flow (`BeginSceneStartup` → `RunSceneStartup`):
1. Set `shouldBoundaryVisibilityBeSuppressed = env.SuppressBoundary` on all `OVRManager`s (MR: true; VR: false — Meta only allows hiding the Guardian while passthrough is visible).
2. `env.ApplySpawnCounts(numberOfEnemies, numberOfCrates)`, synchronously — MR pushes them into the MRUK `FindSpawnPositions` (substring match on `SpawnObject.name`: `"TargetTank"` / `"TargetBox"`) before MRUK's `SpawnOnStart` fires.
3. `env.PrepareWorld()` — MR: poll up to ~10s for the MRUK room, toggle the fallback `Floor` collider off and align it to the room floor, wait for effect-mesh colliders. VR: generate the terrain.
4. Async NavMesh build (`NavMeshBuilder.UpdateNavMeshDataAsync`, so the headset view never freezes) from `env.NavMeshSourceRoot` within `env.NavMeshBounds`, starting from `CreateTankNavMeshSettings()` (`agentRadius 0.12`, `agentHeight 0.25`, `voxelSize 0.03`, slope 60°) and adjusted by `env.TuneNavMesh`. `_navMeshDataInstance` is tracked so reloads remove it.
5. `ConfigureEnemyTankMovementWhenReady` starts: waits for enemy `Target`s (`IsEnemy`: HP > 0 and no `ShootingControls`), then warps each onto the NavMesh, enables the `NavMeshAgent` (seeding `speed`/`stoppingDistance` **from that tank's own `EnemyTankAI`**, not from global config), calls `ai.ResetBehaviorState()`, disables any `Rotator`, and wires `WheelRotation.tankBody` to the moving root. It finishes by arming the win condition.
6. `env.SpawnActors()` — MR: spawn 5 player-tank candidates via the `TankFree` spawner, keep the one closest to the headset, face it along the headset's forward, snap it 1cm above the floor. VR: place player, enemies and crates (below).

The environment's coroutines run on the environment itself (`environment.StartCoroutine`) so they die with the scene.

### VR scene: terrain and camera rig

- `MountainTerrainGenerator` is a pure function of `(seed, Settings)` (no `UnityEngine.Random`): a ridged mountain rim that walls the 36 m map in, interior ridges for cover, gentle hills, flattened valleys and a ridge-free central start valley. Plus a slope/altitude splatmap (grass/dirt/rock/snow).
- `TerrainEnvironment` builds a runtime `TerrainData` + Terrain on the Default layer (layers and textures are generated in code, destroyed in `OnDestroy`), and adds invisible edge walls on **Ignore Raycast** (they block tanks and balls but stay out of LOS casts and the NavMesh). It tunes the NavMesh to slope 35° and voxel 0.04. Spawns come from `PlanSpawns()` with `System.Random(seed)`: the player in the flattest central spot, facing the nearest enemy; enemies on the NavMesh, ≥6 m from the player, with a complete path to the player; floating crates. An out-of-bounds guard respawns a player tank that falls through or leaves the map.
- Seed: `GameConfigManager.vrTerrainSeed`, re-rolled each round while `vrNewTerrainEachRound` is on. Multiplayer later only needs to share the seed — except enemy variant picks, which `RandomEnemyVariant` still rolls with `UnityEngine.Random`.
- `PlayerRigFollow` (on the camera rig root; never moves TrackingSpace): glides the rig after the tank once it is >1.5 m from the head, snaps past 4 m (teleport/respawn) or always with the `vrSnapFollow` comfort option, follows the terrain height under the head, grip-drag to move/rise, X to recenter behind the tank. Uses unscaled time so it works while the config menu pauses the game.
- The scene's sky material, URP Terrain/Lit material, URP Unlit material (lasers, text-adjacent markers; all in `Assets/Materials/VR/`), fog, Trilight ambient and the OVRManager/camera changes (passthrough off, Skybox clear) are all set by the builder.

### VR scene: turn-based mode (`TurnBasedMatch`)

Worms-style, selected from the main menu. `TerrainEnvironment.ApplySpawnCounts` takes the human/AI counts from `GameSession` (not the config menu) and spawns one tank per human; `SpawnActors` then calls `TurnBasedMatch.Begin`. The match waits for `GameConfigManager.EnemiesReady`, then alternates **teams** (humans vs AI), cycling through each team's living tanks.

- **Human turn:** hot-seat hand-off banner ("pass the headset, press A"; skipped for a single human), then that tank's `TowerController` / `ShootingControls` / `TankFlip` are enabled with `DriveBudget` (fuel, meters) and `ShotsRemaining = 1`, a 30 s timer, and a gravity-only aim-preview arc. Every other human tank stays disabled. `ShootingControls.OnEnable` clears stale charge state so control can be handed back and forth.
- **AI turn:** `EnemyTankAI.TurnBasedControl` (set in `Awake` from `GameSession`) stops all autonomous behaviour; the match runs `EnemyTankAI.TakeTurn`: short NavMesh drive, aim with partial wind compensation, one `FireShot`. Aim correction from the last impact still applies across turns (reset when the target changes).
- **Resolution:** the turn ends when no live (non-kinematic) projectile remains; the rig jumps (`PlayerRigFollow.ViewFrom`) to watch the shot land.
- **Explosions:** `Projectile.Exploded` (raised only by fuse/surface explosions, not direct Target hits) → `TerrainEnvironment.DigCrater` + blast damage/knockback to every `Target` in `blastRadius` via `Target.TakeDamage`. The NavMesh is refreshed in place (`GameConfigManager.RefreshRuntimeNavMesh`) between turns.
- **Wind:** static `Projectile.Wind`, re-rolled each turn, applied in `Projectile.FixedUpdate`; shown as an arrow under the HUD. Reset to zero when the match is disabled.
- **Death / end:** `Target.HumanTankDestroyed` (set by the match) replaces the reload-on-death for human tanks. All AI dead → the normal win reload; all humans dead → defeat banner and reload.
- The match's hooks (`Projectile.Exploded`, `Target.HumanTankDestroyed`) are attached in `Begin`, not `OnEnable` — the component sits in the scene during real-time play too, which must keep plain explosions and reload-on-death.
- HUD, banners, name tags, wind arrow and aim preview are created at runtime (TextMeshPro + LineRenderer). Non-UI TextMeshPro renders ~0.1 m per font-size point, so sizes are set as `fontSize = meters * 10`.

Scene reload: `ReloadScene()` is exposed as an Immersive Debugger button, and is also called on win and on player death. `OnSceneLoaded` → `ResetSceneStartupState` stops every tracked coroutine handle (`_spawnCoroutine`, `_enemyConfigCoroutine`, `_debugPanelCoroutine`, `_winSequenceCoroutine`) and removes stale NavMesh data before re-running startup — the singleton outlives the scene, so untracked coroutines would otherwise keep touching destroyed objects.

### Enemy stats live on prefabs, not in GameConfigManager

**This is the most important thing to get right when editing enemy behavior.** Per-tank stats (`moveSpeed`, `chaseRange`, `stopRange`, `behaviorMode`, `mass`, `maxHitPoints`, shooting parameters) are authoritative **on the `EnemyTankAI` / `Target` prefab**. There is no per-frame push from the manager — an earlier `SyncConfigFromManager` was removed precisely so Light/Medium/Heavy variants could differ. Do not reintroduce global overwrites of those fields.

`GameConfigManager.Instance` is read at runtime in only these places:
- `EnemyTankAI` — `enemyShootingEnabled` (checked both before starting a volley and mid-volley, so toggling it aborts cleanly).
- `Target` — `playerInvulnerable` (shows hit feedback but skips damage; "am I the player?" is detected by the presence of `ShootingControls`).
- `Projectile` — `GetGameplayFloor()` for the elevated-floor filter (null in VR — there is no `Floor`, so the filter never fires on terrain).
- `MRRoomEnvironment` — `GetGameplayFloor()` to move/toggle the fallback floor.
- `TerrainEnvironment` — `vrNewTerrainEachRound` / `vrTerrainSeed` (reads, and writes the new seed).
- `PlayerRigFollow` — `vrSnapFollow`.
- `TurnBasedMatch` — `EnemiesReady`, `RefreshRuntimeNavMesh()`, `ReloadScene()` on defeat.

Plus a one-time read in `ShootingControls.Start()` for `powerUpDuration`, `projectileMinSpeed`, `projectileMaxSpeed`.

### Enemy variants

MRUK (MR) or `TerrainEnvironment` (VR) spawns `TargetTankRandomizer`, a placeholder carrying `RandomEnemyVariant`. On `Start` it picks one prefab by *relative* weight (50/30/20 → Light/Medium/Heavy), instantiates it at its own transform, and destroys itself. `TargetTank_{Light,Medium,Heavy}` are prefab variants of `TargetTank` overriding only what differs:

| | HP | moveSpeed | chase/stop | behaviorMode | mass |
|---|---|---|---|---|---|
| Light | 100 (base) | 0.5 (base) | 5 / 1 | ChaseWander | 1 |
| Medium | 75 | 0.5 | base | PatrolScan | 1 |
| Heavy | 125 | 0.3 | 3 / 1.5 | PatrolScan | 4 |

Adding a stat that should vary per variant means adding a serialized field to `EnemyTankAI` and overriding it in the variant prefab — not adding a `[DebugMember]` to the manager.

### Config persistence and the in-headset menu

The Y button on the left controller opens Meta's Immersive Debugger panel, which auto-populates from `[DebugMember]` attributes on `GameConfigManager` (categories: "Game Config", "Testing", "VR", "Controls"). When the panel's visibility toggles, `OnDebugPanelVisibilityChanged` sets `Time.timeScale` to 0 / 1 if `pauseWhenConfigMenuOpen` is true. This is why the manager waits up to 10 seconds in a coroutine for the `DebugInterface` to exist — the panel is created lazily and may start inactive.

Values persist to `PlayerPrefs` under the `XRTanks.GameConfig.` prefix, saved on `OnDestroy` / `OnApplicationQuit` / `OnApplicationPause(true)` (the Quest backgrounding case matters — the OS may kill the app without a clean quit). `Awake` calls `CaptureDefaults()` **before** `LoadPersistedConfig()` so `RestoreDefaults()` can reach the inspector values.

Adding a new tunable therefore means touching five places: the field, the `ConfigDefaults` struct, `CaptureDefaults`, `LoadPersistedConfig`, `SaveConfig`, and `RestoreDefaults`. Miss one and the setting silently fails to survive a restart.

### Player control split

`TowerController` (on the tank root) owns driving *and* turret rotation/tilt — don't be misled by the class name. `ShootingControls` owns firing and projectile cycling. `TankFlip` owns hold-B-to-reset. Each script creates its own `InputAction` instances directly against `<XRController>` paths in `OnEnable` and disposes them in `OnDisable`; there is no shared action asset wiring these together.

`TowerController.IsTippedOver` blocks *drive* input past `maxDriveTiltDegrees` (60°) but leaves turret aim working, so a tipped player can still look around while holding B to flip.

Driving is a transform write, projected onto the ground normal under the tank's leading edge (`GroundProbe.TryCastDown`), and uphill motion is blocked on ground steeper than `maxClimbSlopeDegrees` (35°) — that's what makes the VR mountain rim a wall. `TankFlip` stands the tank upright but keeps its heading.

Enemies stay upright at the root (agent-driven); `EnemyTankAI.AlignBodyToGround` tilts only the body model (the root's child containing the turret) to the slope, and `IsBlockedAhead` ignores walkable ground (normal within `walkableSlopeDeg`) so hills aren't "obstacles".

### Enemy AI: two modes, one shooting state machine

`EnemyTankAI` (the largest file in the project) has two top-level behaviors selected by `behaviorMode`:

- **ChaseWander** — pursues the player inside `chaseRange`, stops at `stopRange` and rotates in place to aim, wanders around `_homePosition` otherwise. Fires volleys on a `chaseWanderVolleyCooldown` timer; while `_chaseWanderInVolley` it freezes body movement entirely so the turret can settle.
- **PatrolScan** — `PickDirection → Driving → Scanning → Shooting`. Drives a random direction until blocked (`IsBlockedAhead` sphere-cast or `IsAtNavMeshEdge` navmesh raycast), sweeps the turret 1–4 randomized half-sweeps, then fires if line of sight allows.

Both funnel into the same volley state machine (`AimAtPlayer → Fire → WaitForImpact → BetweenShots → Done`) via `TryBeginShootingVolley` / `UpdateShootingVolley`.

Shooting details that are easy to break:
- **Ballistic solver.** `ComputeLaunchSpeed(d, h)` picks a speed from horizontal distance and height difference (based on the minimum reaching speed `v² = g(h + √(h²+d²))`, so uphill shots on terrain don't fall short), `SolveBallistic` returns the low-arc solution, `EnforceMinElevation` rotates it up to `minLaunchElevationDeg` so tanks never fire flat. `AimTurretAtWorldPosition` re-runs *the same three functions* so the barrel visibly points along the actual firing arc — change one and you must change both call sites together.
- **Turret aim must be composed in the parent's local frame** (`Quaternion.Inverse(parent.rotation) * desiredWorldRot`). A previous version drove pitch via world `eulerAngles.x`, which mixed roll into the turret as soon as the body yawed.
- **Aim correction across shots.** `Projectile.onImpact` is a one-shot callback wired at fire time; `OnProjectileImpact` records where the shot actually landed and the next shot offsets its virtual target by that error. `aimErrorMaxFactor` then adds range-scaled random miss on top.
- **Line of sight** uses `RaycastAll` sorted by distance, skipping self colliders, and defaults to "no LOS" if the player is never explicitly seen — the tank's own colliders always sit between turret and player, so a plain `Raycast` would report false clears.
- The turret finds itself by name (`FindTurretTransform` matches a child containing "tower" or "turret"). Renaming turret hierarchy nodes silently disables aiming and scanning.

### Projectile / damage model

`Projectile` has three `ProjectileType`s:
- **Standard (gray):** damages `Target`s on direct hit; on first non-target hit, starts a 5s fuse then explodes.
- **Explosive (blue):** explodes immediately on any non-target surface. Special case in `OnCollisionEnter`: floor-like hits (`FLOOR`, `Floor`, `FLOOR_EffectMesh`, `GLOBAL_MESH`) are *ignored* if they happen more than `elevatedFloorIgnoreThreshold` (5cm) above the gameplay `Floor`. Without this, MRUK effect-mesh colliders at bouncing altitudes would detonate shots mid-air.
- **Teleport (red):** the teleport is *deferred*. First contact starts a `teleportDelay` (5s) timer; the ball stays alive and bouncing, and the shooter is moved to wherever the ball ended up when the timer fires. `Target` deliberately does **not** destroy Teleport projectiles on hit so that window can run out.

`bounciness > 0` builds a runtime `PhysicsMaterial` in `Awake` — bounce is a per-prefab projectile property, not scene physics config. `maxLifetime` (30s) destroys any projectile nothing else resolved, e.g. one that flew off the VR map.

`Target` handles both `OnCollisionEnter` and `OnTriggerEnter` so it works whether the projectile's collider or the target's is the trigger. `maxHitPoints <= 0` or `projectile.damage <= 0` is the "instant kill" path used by crates; positive HP drives the attached `PowerBar` health bar.

### Knockback

`Target.ApplyImpactKnockback` reads the projectile's `linearVelocity` **before** `Destroy` is scheduled, scales it by `knockbackImpulseScale`, and forwards it to `EnemyTankAI.ApplyKnockback` or `TowerController.ApplyKnockback`. Each divides by its own `mass` and keeps only the horizontal component, then integrates and exponentially damps the velocity per-frame.

Enemies apply the step through `agent.Move` rather than the transform: a `NavMeshAgent` overwrites the transform each frame, so direct rigidbody impulses or position writes get eaten, and `agent.Move` also makes the slide collide with walls and follow the floor.

### PowerBar is also the health bar

`PowerBar` is used for both the charging indicator on the player tank and per-enemy health bars. It billboards toward the main camera (or a `CameraRig`-named camera) and hides both bar renderers entirely when power is 0. Set `useColorLerp = true` for health (green at full → red at empty); the color goes through a `MaterialPropertyBlock` on `_BaseColor`, so it will not work with a shader that doesn't expose that property.

### Win / lose

- **Win:** `GameConfigManager` counts alive enemy `Target`s (HP > 0, no `ShootingControls`) once `ConfigureEnemyTankMovementWhenReady` completes, and only *then* sets `_winConditionArmed`. Arming late is deliberate — counting during `Target.Awake` races with spawn ordering and can trigger an instant "0 enemies → win". `Target.HandleLethalHit` calls `OnEnemyKilled()`; at zero, a 2.5s celebration coroutine runs before `ReloadScene()`.
- **Lose:** the player tank's `Target` disables `TowerController` / `ShootingControls` / `TankFlip`, fires a heavy both-controller pulse, waits 3s realtime, then reloads.

Both waits use `WaitForSecondsRealtime` so they still complete with `Time.timeScale == 0`.

### Haptics

`Haptics.Pulse(runner, controller, frequency, amplitude, duration)` is a static helper around `OVRInput.SetControllerVibration` that auto-stops via a coroutine on the passed-in runner, using unscaled time so pulses finish while the config menu has the game paused. Existing call sites: charge-full ping, fire "thunk", hit-confirm (only when the *player's* shot hits something that isn't the player), flip-complete, and death.

## Gotchas

- `EnemyTankAI` requires `NavMeshAgent.updateRotation = false` because the class implements its own tank-style steering (separate rotate + forward-move with reverse/turn penalties) in `DriveLikeTank`. The agent provides pathing (`steeringTarget`) but does not move or rotate the transform on its own.
- `ResetBehaviorState()` runs from `Awake`, *before* `GameConfigManager` has enabled the agent and warped it onto the NavMesh. Anything added there must guard on `_agent.enabled && _agent.isOnNavMesh` — `ResetPath()` throws otherwise.
- The runtime NavMesh only includes colliders on the layers in `GameConfigManager.navMeshLayerMask` (serialized, defaults to `Default` only). Obstacle prefabs placed on Walls/Obstacles layers will be missing from the NavMesh and enemies will drive through them.
- Projectile prefabs are cycled by *index* in `ShootingControls.projectilePrefabs` (A button cycles). Preview spawning re-parents a kinematic copy onto the tank with scale `previewScale` and adds a `BlinkRenderer`; destroy the old preview before creating a new one.
- `Rotator` on enemy prefabs is force-disabled during enemy configuration — it's decorative for the un-configured prefab and fights the AI's own turret control.
- Static state that outlives scenes: `GameSession` (menu choice), `Projectile.Wind`, `Projectile.Exploded`, `Target.HumanTankDestroyed`. The last three must be cleared by whoever set them (TurnBasedMatch does it in `OnDisable`), or real-time rounds after a turn-based one would inherit wind and craters.
- Don't hand-edit `VRMountains.unity` or `MainMenu.unity` for anything SampleScene also has — it is regenerated by the builder and edits would be lost. VR-only tuning belongs in the builder or in `TerrainEnvironment` / `PlayerRigFollow` defaults.
- `Assets/_Recovery/` contains auto-recovered scene files; ignore them unless explicitly restoring work.
- There is no `.cursor/rules/`, `.cursorrules`, or Copilot instructions file — this and `CONTROLS.md` are the only repo-level guidance files.
