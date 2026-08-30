# GWYF Trajectory Prediction

BepInEx 6 plugin (IL2CPP) for *Golf With Your Friends* that predicts the ball's trajectory
and searches the shot parameter space for hole-in-one solutions.

Academic proof-of-concept: Investigating how accurately the rigid-body simulation of a running
Unity/PhysX game can be reproduced.

Reproducing PhysX *externally* — a hand-written integrator plus SphereCasts — was abandoned; it
diverges after a few contacts and cannot be tuned into agreement. The trajectory is now computed
*internally*: a second physics scene mirrors the hole and is stepped by the game's own solver.

## Features

- **Internal 1:1 Simulation** – Shots are simulated in a mirrored `PhysicsScene` driven by the
  game's own PhysX solver (see below).
- **Live Trajectory** – Draws the predicted trajectory for the current aiming direction
  and shot power as the shot is being charged.
- **Solution Search** – Searches angle and power space for a trajectory that lands in the hole;
  otherwise falls back to the best approach shot.
- **Power Indicator** – Displays the required shot power on the power bar.
- **Auto-Aim Assist** – Holding `[F]` smoothly rotates the camera toward the found solution.
- **Shot Calibration** – The force→launch-speed factor is measured from real shots instead of
  being a hard-coded constant.
- **Trace Comparison** – Every real shot is recorded and compared against its own prediction, so
  the accuracy of the reproduction is a measured number rather than an impression.
- **Session Log** – One file per session under `BepInEx/gwyf-diag/` containing the physics
  environment, how the shadow world was built, every shot, and where each prediction drifted.
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
Directory.Build.props          GameDir + derived paths, deploy switch
Local.props.example            Template for machine-specific paths (not tracked in Git)
GwyfAimbotMod/
  Plugin.cs                    BepInEx entry point, configuration, IL2CPP type registration
  AimbotBehaviour.cs           Target acquisition, search state machine, HUD overlay
  ShadowPhysicsWorld.cs        Mirrors the hole into a local PhysicsScene (geometry + ball clone)
  ShadowTrajectorySimulator.cs Steps that scene with the game's solver - the 1:1 path
  ShotCalibration.cs           Measured force -> launch-speed factor
  ShotTraceRecorder.cs         Records real shots, compares them against the prediction
  TrajectorySimulator.cs       Legacy approximate integrator (fallback only)
  JsonBuilder.cs               Minimal culture-invariant JSON writer
  PhysicsParameterDump.cs      Extracts actual physics parameters (Log + JSON)
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

## Internal Simulation (1:1)

`ShadowPhysicsWorld` creates a second scene via `LocalPhysicsMode.Physics3D`. Such a scene is
never advanced by the game's own `FixedUpdate`, so it can be stepped freely without disturbing the
running match. Into it are mirrored:

- every active collider of the hole — same shape parameters, same `PhysicMaterial`, same layer,
  same `contactOffset`, meshes shared rather than copied;
- a clone of the ball — same colliders, mass, centre of mass, inertia tensor and its rotation,
  solver iteration counts, `collisionDetectionMode`, sleep threshold, `maxAngularVelocity`.

`ShadowTrajectorySimulator` then resets that ball to the shot's opening state and calls
`PhysicsScene.Simulate(Time.fixedDeltaTime)` in a loop. Restitution, friction, contact resolution
and sleeping are decided by PhysX against the real materials, so there are no tuned constants left
to drift. Colliders carrying a non-kinematic `Rigidbody` (other players' balls) are skipped, which
also keeps the real ball out of the simulation without having to disable it.

The only game logic reproduced by hand is `BallMovement`'s drag schedule: `dragToHitBall` right
after the hit, switching to `dragToSlow` after the delay in the `WaitOneSecondForDrag` coroutine.
`SecondTillDrag` is a bool flag, not a duration — the one second lives in the coroutine — so the
delay is exposed as `[Simulation] SecondsTillDrag` and can be corrected against a recorded trace.

Collider shapes are mirrored by baking the world scale into the shape parameters (box size, sphere
radius, capsule radius/height) and leaving the clone at unit scale, rather than reproducing the
scale on the transform. A hierarchy with negative scale does not round-trip through `lossyScale` —
this course contains 14 such colliders and the game logs its own warning about them — so baking is
the only way to get the same shape in the same place. MeshColliders keep the transform scale,
because the mesh itself carries the geometry.

### Measured physics

These were measured from shot traces, not taken from field names:

| Quantity | Value | How it was established |
|---|---|---|
| `fixedDeltaTime` | **0.005** (200 Hz) | dumped from the running game |
| Shot application | `AddForce(dir * force)`, ForceMode.Force | force 4557 → 4557 × 0.005 / 1 kg = 22.785 m/s vs. 22.717 measured |
| Launch speed at 100 % | **52.5 m/s** | 10500 × 0.005 / 1 — the original guessed `MAX_PHYSICS_SPEED = 52` was right |
| `m_PowerCurve` | **empty, evaluates flat** | 0 keys; using it collapsed every power onto one speed |
| Ball radius / mass | 0.07 / 1.0 | ball rests at `y = 0.07`, which cross-checks the mirrored geometry |
| Drag after the hit | 0.5 linear, 1.0 angular | read off the live rigidbody per step |

The force model is therefore `speed = force * fixedDeltaTime / mass`, with a dimensionless
correction (`[Calibration] ForceModelCorrection`, measured ≈ 0.997) carrying whatever the model
does not capture. `m_PowerCurve` is applied only when it is shown not to be flat.

The drag schedule is likewise read off the live rigidbody during a real shot rather than derived
from `dragToHitBall`/`m_environmentalDragToApply`, which was measurably wrong: it cost the
simulated ball 0.25 % of its speed per step, compounding to metres over a shot.

### Measuring the accuracy

With `[Trace] Enabled`, every real shot is sampled once per physics step — position, live drag and
the ball's collision layer. When the ball comes to rest, the same opening state is replayed through
the shadow simulation and the two paths are compared step by step. Maximum, mean and final
deviation go to the log and the HUD; with `[Trace] WriteJson` the full per-step comparison is
written to `BepInEx/traces/`.

The session log turns the shape of a divergence into a diagnosis:

- a **constant** per-step distance ratio below 1 is a drag error;
- a **sudden** jump from zero to centimetres in one step is a contact resolved against different
  geometry — the log then lists the colliders present at that point in each world side by side;
- `max y` predicted far above `max y` actual means the simulated ball leaves the ground where the
  real one does not.

A hole-in-one is only reported as certain while the last measured deviation is below 25 cm;
above that the HUD labels it as unreliable rather than promising a shot it cannot deliver.

### Cost

At 200 Hz one simulated second costs 200 solver steps, and a full trajectory runs roughly 35-60 ms
— more than a 60 Hz frame. The search therefore runs at most **one trajectory per frame**, with the
time budget checked before each rather than between search stages, and the live preview while
charging is throttled. Search space and simulated durations are configurable; `AngleSpanDegrees` and
`ProbeSimSeconds` are the two knobs that move the cost most.

### Known limitations

- Moving geometry (windmills, platforms) is mirrored as a snapshot at build time, not animated.
- Trigger-driven gameplay (boosters, conveyors) is not reproduced; volumes whose name looks like a
  hazard are only detected, so such shots are discarded rather than simulated.
- Ball spin from the spin control is not applied to the opening state, and the solution search
  starts each candidate with zero angular velocity.
- Predictions currently track the real ball exactly until the first wall contact and diverge after
  it. See *Current state* below.

## Current state

The opening of a shot reproduces exactly — deviation is 0.000 m for the first ~75 physics steps,
and the per-step speed ratio is 1.00000. The force model and the drag schedule are settled.

What is not solved: at the first wall contact the paths separate in a single step, after which the
simulated ball can leave the ground where the real one never does. The direction of the error shows
the simulated ball being held back, i.e. the shadow world contains geometry the live scene does not
have at that point, or has it in the wrong place. The geometry comparison at the divergence point
was added to identify which collider is responsible.
