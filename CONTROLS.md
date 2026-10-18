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

## Main Menu

| Button | Action |
|---|---|
| **Point + Trigger** (either hand) | Choose a game: MR real-time, VR real-time, VR turn-based; `-` / `+` set turn-based players (1-4) and AI tanks (1-6) |
| **Y Button** → Game Config → *Back To Menu* | Return to the menu from any game |

## Turn-Based Mode (VR)

Same tank controls as above, but only during your own turn:

| | |
|---|---|
| **A Button** (at the "pass the headset" banner) | Start your turn (hot-seat with 2+ players; the timer starts after this) |
| **Left Stick** | Drive, limited by the turn's fuel (turning is free) |
| **Right Trigger** | One shot per turn; the arc in front of the barrel previews the first part of the flight (wind not included) |

Wind changes every turn and is shown as an arrow under the turn display above the active tank.

## Unmapped Buttons

| Button | Hand |
|---|---|
| Trigger | Left |
| Thumbstick Click | Left |
| Thumbstick Click | Right |
| Menu Button | Left |
