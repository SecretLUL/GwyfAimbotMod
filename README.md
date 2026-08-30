# GWYF Trajectory Prediction

BepInEx 6 plugin (IL2CPP) for *Golf With Your Friends* that predicts the ball's trajectory
and searches the shot parameter space for hole-in-one solutions.

Academic proof-of-concept: Investigating how accurately the rigid-body simulation of a running
Unity/PhysX game can be reproduced externally.

## Features

- **Live Trajectory** – Draws the predicted trajectory for the current aiming direction
  and shot power as the shot is being charged.
- **Solution Search** – Searches angle and power space for a trajectory that lands in the hole;
  otherwise falls back to the best approach shot.
- **Power Indicator** – Displays the required shot power on the power bar.
- **Auto-Aim Assist** – Holding `[F]` smoothly rotates the camera toward the found solution.
- **Parameter Dump** – Pressing `[F9]` logs the measured in-game physics parameters and exports
  them as JSON (see below).

> Intended for singleplayer and private sessions.

## Prerequisites

| Component | Version |
|---|---|
| Golf With Your Friends | Unity 2021.3.28f1, IL2CPP |
| BepInEx | 6.0.0-be.764 (Unity.IL2CPP, win-x64) |
| .NET SDK | 6.0+ |

BepInEx must be installed in the game folder and the game must have been launched at least once –
this generates the Il2CppInterop assemblies under `BepInEx/interop/` which this project compiles against.

## Building

```bash
cp Local.props.example Local.props     # Set GameDir to your game installation path
dotnet build
```

The build automatically copies `GwyfAimbotMod.dll` to `<GameDir>/BepInEx/plugins/`.
You can disable this by setting `<DeployToGame>false</DeployToGame>` in `Local.props`.

Alternatively, without `Local.props`:

```bash
dotnet build -p:GameDir="C:\Path\To\Game"
```

## Project Structure

```
Directory.Build.props        GameDir + derived paths, deploy switch
Local.props.example          Template for machine-specific paths (not tracked in Git)
GwyfAimbotMod/
  Plugin.cs                  BepInEx entry point, configuration, IL2CPP type registration
  AimbotBehaviour.cs         Target acquisition, search state machine, HUD overlay
  TrajectorySimulator.cs     Trajectory integration
  PhysicsParameterDump.cs    Extracts actual physics parameters (Log + JSON)
```

## Parameter Dump

Pressing `[F9]` (configurable via `BepInEx/config/com.ammar.gwyf.aimbot.cfg`,
section `[Dump]`, key `DumpKey`) writes the complete physics state once
to the BepInEx log and exports it as JSON to
`BepInEx/gwyf-physics-dump_<timestamp>.json` (alongside `LogOutput.log`).

Captured data:

| Section | Content |
|---|---|
| `time` | `fixedDeltaTime`, `maximumDeltaTime`, `timeScale` |
| `physicsGlobals` | Gravity, `bounceThreshold`, contact offset, solver iterations, depenetration, sleep threshold, and other process-wide PhysX statics |
| `rigidbody` | Mass, drag, inertia tensor and rotation, solver iterations, `collisionDetectionMode`, `interpolation`, constraints, instantaneous state |
| `ballColliders` | All ball colliders with type, radius, `lossyScale`, and `sharedMaterial` |
| `ballMovement` | Both drag sets, sand/glue/environment drag, drag-switch state, `m_maxForce` / `minForce` |
| `m_PowerCurve` | All keyframes with time, value, tangents, weights, and wrap modes |
| `groundProbe`, `wallProbe` | Raycasts downward and in look direction: hit collider, normal, and associated `PhysicMaterial` |

Since physics materials vary per hole, dumping on each hole is recommended; each keypress
creates a separate timestamped file.

## Current State

Trajectory calculation in `TrajectorySimulator.cs` currently mimics PhysX manually
(custom integrator + SphereCasts) and diverges noticeably from game behavior after a few contacts.
Known causes: lack of angular momentum / spin, missing drag switch one second after hit,
ignored trigger volumes (boosters, conveyor belts, water), timestep discrepancies.
A rework using a dedicated `PhysicsScene` with the game's solver is planned.

The parameter dump serves as the first step toward this goal: It provides the ground-truth measurements
used to replace guessed constants in the simulator (`MAX_PHYSICS_SPEED`, `CUP_RADIUS`, default bounciness and friction values).
