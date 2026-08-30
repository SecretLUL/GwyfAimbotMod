using System;
using System.Collections.Generic;
using UnityEngine;

namespace GwyfAimbotMod
{
    /// <summary>
    /// The drag schedule BallMovement applies to the ball after a hit. Captured once per search so
    /// the per-step loop never has to touch IL2CPP fields.
    /// </summary>
    internal struct BallTuning
    {
        public bool Valid;
        public float DragAfterHit;
        public float AngularDragAfterHit;
        public float DragToSlow;
        public float AngularDragToSlow;
        public float SecondsTillDrag;
        public float EnvironmentalDrag;
        public float Radius;
        public float SleepSpeedThreshold;

        /// <summary>True when the drag schedule came from a recorded shot rather than field names.</summary>
        public bool FromMeasurement;

        /// <summary>
        /// Reads the live tuning off BallMovement.
        ///
        /// Note on <paramref name="secondsTillDrag"/>: BallMovement.SecondTillDrag is a bool flag,
        /// not a duration - the delay lives in the coroutine WaitOneSecondForDrag, whose name (and
        /// the m_waitWholeSecond field it yields on) puts it at one second. It is passed in from
        /// config so it can be corrected against a recorded trace without a rebuild.
        /// </summary>
        public static BallTuning Capture(BallMovement ball, float secondsTillDrag)
        {
            var t = new BallTuning();
            if (ball == null) return t;

            t.Valid = true;
            t.Radius = ball.BallRadius;
            t.SleepSpeedThreshold = ball.sleepSpeedThreshold;

            // Preferred: the schedule read straight off the live rigidbody during a real shot.
            // Deriving it from the field names instead was measurably wrong - it added
            // m_environmentalDragToApply on top of dragToHitBall and made the simulated ball lose
            // ~0.25% of its speed per step, which compounds into metres over a full shot.
            if (MeasuredDragSchedule.IsMeasured)
            {
                t.DragAfterHit = MeasuredDragSchedule.DragAfterHit;
                t.AngularDragAfterHit = MeasuredDragSchedule.AngularDragAfterHit;
                t.DragToSlow = MeasuredDragSchedule.DragToSlow;
                t.AngularDragToSlow = MeasuredDragSchedule.AngularDragToSlow;
                t.SecondsTillDrag = MeasuredDragSchedule.SwitchSeconds;
                // The measured value is the live rb.drag, which already contains any sand/glue term.
                t.EnvironmentalDrag = 0f;
                t.FromMeasurement = true;
                return t;
            }

            t.DragAfterHit = ball.dragToHitBall;
            t.AngularDragAfterHit = ball.angDragToHitBall;
            t.DragToSlow = ball.dragToSlow;

            // BallMovement carries two names for the post-hit angular drag; prefer the one that is set.
            float angSlow = ball.angDragToSlowBall;
            if (angSlow <= 0f) angSlow = ball.anglularDragToSlow;
            t.AngularDragToSlow = angSlow;

            t.SecondsTillDrag = secondsTillDrag > 0f ? secondsTillDrag : float.MaxValue;
            t.EnvironmentalDrag = ball.m_environmentalDragToApply;
            t.FromMeasurement = false;

            return t;
        }
    }

    /// <summary>
    /// Runs a shot inside <see cref="ShadowPhysicsWorld"/> by stepping the game's own PhysX solver.
    ///
    /// The only game logic reproduced here is BallMovement's drag schedule (high drag right after
    /// the hit, switching to the rolling drag after SecondTillDrag). Everything else - restitution,
    /// friction, contact resolution, sleep - is decided by PhysX against the mirrored colliders and
    /// their real physics materials, so there are no tuned constants to drift.
    /// </summary>
    internal sealed class ShadowTrajectorySimulator
    {
        private readonly List<Vector3> _points = new List<Vector3>(1024);

        /// <summary>Consecutive below-threshold steps before the ball counts as at rest.</summary>
        private const int RestStepsRequired = 8;

        /// <summary>Used when BallMovement.sleepSpeedThreshold is not set.</summary>
        private const float FallbackRestSpeed = 0.02f;

        public int RunCount { get; private set; }
        public long StepCount { get; private set; }
        public float LastRunMs { get; private set; }
        public float AverageRunMs { get; private set; }

        public void ResetStats()
        {
            RunCount = 0;
            StepCount = 0;
            LastRunMs = 0f;
            AverageRunMs = 0f;
        }

        /// <summary>
        /// Simulates one shot. The shadow ball is reset to the given state, then stepped with the
        /// game's fixedDeltaTime until it sinks, is lost, comes to rest, or the time budget expires.
        /// </summary>
        public SimulationResult Run(
            ShadowPhysicsWorld world,
            BallTuning tuning,
            Vector3 startPos,
            Quaternion startRot,
            Vector3 startVelocity,
            Vector3 startAngularVelocity,
            Vector3 holePos,
            float cupRadius,
            float maxCupEntrySpeed,
            float maxSimSeconds,
            int pointStride)
        {
            var result = new SimulationResult();
            result.Path = Array.Empty<Vector3>();

            if (world == null || !world.IsReady) return result;
            if (pointStride < 1) pointStride = 1;

            var rb = world.Ball;
            var physics = world.Scene;

            float dt = Time.fixedDeltaTime;
            if (dt <= 0f) dt = 0.02f;
            int maxSteps = Mathf.Max(1, Mathf.CeilToInt(maxSimSeconds / dt));

            float wallStart = Time.realtimeSinceStartup;

            // ---- deterministic reset -------------------------------------------------
            // Sleep() zeroes both velocities and drops the actor's accumulated state, so every run
            // starts from the same place regardless of what the previous candidate shot did.
            rb.Sleep();
            rb.transform.position = startPos;
            rb.transform.rotation = startRot;
            rb.position = startPos;
            rb.rotation = startRot;
            rb.velocity = startVelocity;
            rb.angularVelocity = startAngularVelocity;
            rb.drag = tuning.DragAfterHit + tuning.EnvironmentalDrag;
            rb.angularDrag = tuning.AngularDragAfterHit;
            rb.WakeUp();
            Physics.SyncTransforms();

            _points.Clear();
            _points.Add(startPos);

            Vector3 pos = startPos;
            Vector3 prevPos = startPos;
            Vector3 vel = startVelocity;
            Vector3 prevVel = startVelocity;

            float minDist = Vector3.Distance(startPos, holePos);
            float simTime = 0f;
            bool dragSwitched = false;
            int restSteps = 0;
            int bounces = 0;
            int step = 0;

            var hazards = world.HazardVolumes;
            bool checkHazards = hazards != null && hazards.Count > 0;

            // Match the game's own rest test rather than inventing a threshold.
            float restSpeed = tuning.SleepSpeedThreshold > 0f ? tuning.SleepSpeedThreshold : FallbackRestSpeed;
            float restSpeedSqr = restSpeed * restSpeed;

            for (; step < maxSteps; step++)
            {
                if (!dragSwitched && simTime >= tuning.SecondsTillDrag)
                {
                    rb.drag = tuning.DragToSlow + tuning.EnvironmentalDrag;
                    rb.angularDrag = tuning.AngularDragToSlow;
                    dragSwitched = true;
                }

                physics.Simulate(dt);
                simTime += dt;

                prevPos = pos;
                pos = rb.position;
                vel = rb.velocity;

                // Direction change of more than ~30 degrees between steps is a contact.
                float prevMag = prevVel.magnitude;
                float mag = vel.magnitude;
                if (prevMag > 0.2f && mag > 0.2f && Vector3.Dot(prevVel, vel) < 0.86f * prevMag * mag)
                {
                    bounces++;
                }
                prevVel = vel;

                if (step % pointStride == 0) _points.Add(pos);

                // Continuous segment check for distance and cup entry
                Vector3 seg = pos - prevPos;
                float segLenSqr = seg.sqrMagnitude;
                Vector3 checkPoint = pos;
                if (segLenSqr > 0.00001f)
                {
                    float t = Mathf.Clamp01(Vector3.Dot(holePos - prevPos, seg) / segLenSqr);
                    checkPoint = prevPos + seg * t;
                }

                float dist = Vector3.Distance(checkPoint, holePos);
                if (dist < minDist) minDist = dist;

                // ---- cup ---------------------------------------------------------
                float dx = checkPoint.x - holePos.x;
                float dz = checkPoint.z - holePos.z;
                float hDist = Mathf.Sqrt(dx * dx + dz * dz);
                float vDist = checkPoint.y - holePos.y;

                if (hDist < cupRadius)
                {
                    // Below the cup plane means the ball is physically inside the cup: the mirrored
                    // geometry already decided, no speed gate needed.
                    if (vDist < -0.20f)
                    {
                        result.Sunk = true;
                        minDist = 0f;
                        _points.Add(checkPoint);
                        step++;
                        break;
                    }

                    // At cup level the cup may be a flat trigger rather than real geometry, so the
                    // classic "slow enough to drop" gate still applies.
                    if (vDist >= -0.30f && vDist <= 0.45f && mag <= maxCupEntrySpeed)
                    {
                        result.Sunk = true;
                        minDist = 0f;
                        _points.Add(checkPoint);
                        step++;
                        break;
                    }
                }

                // ---- hazards -----------------------------------------------------
                if (checkHazards)
                {
                    for (int i = 0; i < hazards.Count; i++)
                    {
                        var vol = hazards[i];
                        if (vol == null) continue;
                        if (vol.bounds.Contains(pos))
                        {
                            result.HitHazard = true;
                            break;
                        }
                    }
                    if (result.HitHazard)
                    {
                        _points.Add(pos);
                        step++;
                        break;
                    }
                }

                // ---- rest --------------------------------------------------------
                if (rb.IsSleeping())
                {
                    result.Rested = true;
                    step++;
                    break;
                }

                if (vel.sqrMagnitude < restSpeedSqr)
                {
                    if (++restSteps >= RestStepsRequired)
                    {
                        result.Rested = true;
                        step++;
                        break;
                    }
                }
                else
                {
                    restSteps = 0;
                }

                // ---- fell out of the world ---------------------------------------
                if (pos.y < holePos.y - 80f || pos.sqrMagnitude > 250000f)
                {
                    result.HitHazard = true;
                    step++;
                    break;
                }
            }

            if (_points.Count == 0 || _points[_points.Count - 1] != pos) _points.Add(pos);

            result.Path = _points.ToArray();
            result.MinDistanceToHole = minDist;
            result.FinalDistanceToHole = Vector3.Distance(pos, holePos);
            result.FinalPosition = pos;
            result.FinalVelocity = vel;
            result.TotalSteps = step;
            result.BounceCount = bounces;
            result.SimulatedSeconds = simTime;

            // Leave the shadow ball asleep so an idle frame costs nothing.
            rb.Sleep();

            LastRunMs = (Time.realtimeSinceStartup - wallStart) * 1000f;
            RunCount++;
            StepCount += step;
            AverageRunMs += (LastRunMs - AverageRunMs) / Mathf.Min(RunCount, 60);

            return result;
        }
    }
}
