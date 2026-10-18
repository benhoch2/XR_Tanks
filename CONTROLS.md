# XR Tanks — Controller Mappings

## Right Controller

| Button | Action | Script |
|---|---|---|
| **Trigger** (hold + release) | Fire projectile (hold to charge power, release to shoot) | `ShootingControls` |
| **A Button** | Cycle projectile type (Standard → Explosive → Teleport) | `ShootingControls` |
| **B Button** (hold 1s) | Reset/flip tank upright | `TankFlip` |
| **Thumbstick X** | Rotate turret left/right | `TowerController` |
| **Thumbstick Y** | Tilt turret up/down | `TowerController` |

## Left Controller

| Button | Action | Script |
|---|---|---|
| **Y Button** | Open / close config menu | Immersive Debugger |
| **Thumbstick Y** | Drive tank forward/backward | `TowerController` |
| **Thumbstick X** | Turn tank left/right | `TowerController` |

## VR Scene Only (`VRMountains`)

| Button | Action | Script |
|---|---|---|
| **X Button** | Recenter: stand behind the tank, facing its forward | `PlayerRigFollow` |
| **Grip** (either hand, hold + move) | Grab the world and drag yourself; push down to rise above the mountains | `PlayerRigFollow` |

In the MR scene these buttons are unmapped.

## Unmapped Buttons

| Button | Hand |
|---|---|
| Trigger | Left |
| Thumbstick Click | Left |
| Thumbstick Click | Right |
| Menu Button | Left |
