using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace GwyfAimbotMod
{
    /// <summary>
    /// One self-contained log file per session, written next to the traces.
    ///
    /// The BepInEx log is shared with the game and scrolls away; this keeps everything needed to
    /// diagnose a bad prediction in one place and in one order: the physics environment, how the
    /// shadow world was built, what each shot measured, and where each prediction drifted.
    ///
    /// Nothing here may ever throw into the game loop, so every operation is guarded and the file
    /// is written through a buffer that is flushed on a timer.
    /// </summary>
    internal static class DiagnosticsLog
    {
        private const int FlushEveryLines = 40;
        private const float FlushEverySeconds = 2f;

        private static readonly List<string> s_buffer = new List<string>(256);
        private static string s_path;
        private static bool s_enabled;
        private static bool s_failed;
        private static float s_nextFlush;
        private static float s_startTime;

        public static string Path { get { return s_path; } }
        public static bool IsActive { get { return s_enabled && !s_failed && s_path != null; } }

        public static void Initialize(bool enabled)
        {
            s_enabled = enabled;
            if (!enabled) return;

            try
            {
                string dir = System.IO.Path.Combine(Paths.BepInExRootPath, "gwyf-diag");
                Directory.CreateDirectory(dir);

                s_path = System.IO.Path.Combine(
                    dir,
                    "session_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");

                s_startTime = Time.realtimeSinceStartup;

                File.WriteAllText(s_path,
                    "GWYF Aimbot session log\n"
                    + "started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\n"
                    + "plugin  com.ammar.gwyf.aimbot\n"
                    + new string('=', 100) + "\n",
                    new UTF8Encoding(false));

                Plugin.Logger.LogInfo("Session diagnostics -> " + s_path);
            }
            catch (Exception ex)
            {
                s_failed = true;
                Plugin.Logger.LogWarning("Session diagnostics unavailable: " + ex.Message);
            }
        }

        /// <summary>One timestamped line under a short category tag.</summary>
        public static void Line(string category, string message)
        {
            if (!IsActive) return;
            try
            {
                float t = Time.realtimeSinceStartup - s_startTime;
                s_buffer.Add(t.ToString("F2", CultureInfo.InvariantCulture).PadLeft(9)
                             + "  " + (category ?? "").PadRight(10) + "  " + message);
                MaybeFlush();
            }
            catch { s_failed = true; }
        }

        public static void Section(string title)
        {
            if (!IsActive) return;
            try
            {
                s_buffer.Add("");
                s_buffer.Add("---- " + title + " " + new string('-', Mathf.Max(0, 92 - title.Length)));
                MaybeFlush();
            }
            catch { s_failed = true; }
        }

        public static void Exception(string where, Exception ex)
        {
            Line("ERROR", where + ": " + ex);
            Flush();
        }

        /// <summary>
        /// The process-wide physics settings every prediction depends on. Written once per session
        /// so a trace can always be read back against the environment that produced it.
        /// </summary>
        public static void WriteEnvironment()
        {
            if (!IsActive) return;

            Section("environment");
            Line("env", "unity " + Application.unityVersion + "   game " + Application.version);
            Line("env", "fixedDeltaTime " + F(Time.fixedDeltaTime)
                        + "   maximumDeltaTime " + F(Time.maximumDeltaTime)
                        + "   timeScale " + F(Time.timeScale));
            Line("env", "gravity " + V(Physics.gravity)
                        + "   bounceThreshold " + F(Physics.bounceThreshold)
                        + "   defaultContactOffset " + F(Physics.defaultContactOffset));
            Line("env", "solverIterations " + Physics.defaultSolverIterations
                        + "/" + Physics.defaultSolverVelocityIterations
                        + "   sleepThreshold " + F(Physics.sleepThreshold)
                        + "   maxDepenetrationVelocity " + F(Physics.defaultMaxDepenetrationVelocity));
            Line("env", "autoSimulation " + Physics.autoSimulation
                        + "   autoSyncTransforms " + Physics.autoSyncTransforms
                        + "   improvedPatchFriction " + Physics.improvedPatchFriction);
            Flush();
        }

        /// <summary>Full rigidbody + collider state of the live ball, so a drift can be attributed.</summary>
        public static void WriteBall(BallMovement ball)
        {
            if (!IsActive || ball == null) return;

            try
            {
                Section("live ball");

                var rb = ball.m_rigidBody;
                if (rb == null) rb = ball.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    Line("ball", "mass " + F(rb.mass)
                                + "   drag " + F(rb.drag) + "   angularDrag " + F(rb.angularDrag)
                                + "   useGravity " + rb.useGravity + "   kinematic " + rb.isKinematic);
                    Line("ball", "ccd " + rb.collisionDetectionMode
                                + "   solverIterations " + rb.solverIterations + "/" + rb.solverVelocityIterations
                                + "   sleepThreshold " + F(rb.sleepThreshold)
                                + "   maxAngularVelocity " + F(rb.maxAngularVelocity)
                                + "   maxDepenetration " + F(rb.maxDepenetrationVelocity));
                    Line("ball", "centerOfMass " + V(rb.centerOfMass)
                                + "   inertiaTensor " + V(rb.inertiaTensor)
                                + "   interpolation " + rb.interpolation
                                + "   constraints " + rb.constraints);
                }

                Line("ball", "BallRadius " + F(ball.BallRadius)
                            + "   lossyScale " + V(ball.transform.lossyScale)
                            + "   layer " + ball.gameObject.layer + " (" + LayerMask.LayerToName(ball.gameObject.layer) + ")"
                            + "   collideLayer " + ball.m_collideLayer
                            + "   ignoreLayer " + ball.m_ignoreLayer);

                // The drag schedule is the one piece of game logic the simulation reproduces by
                // hand, so every field that feeds it is recorded verbatim.
                Line("drag", "dragToHitBall " + F(ball.dragToHitBall)
                            + "   dragToSlow " + F(ball.dragToSlow)
                            + "   initialDrag " + F(ball.initialDrag));
                Line("drag", "angDragToHitBall " + F(ball.angDragToHitBall)
                            + "   angDragToSlowBall " + F(ball.angDragToSlowBall)
                            + "   anglularDragToSlow " + F(ball.anglularDragToSlow)
                            + "   stoppingDragTimes " + F(ball.stoppingDragTimesangDragToSlowBall));
                Line("drag", "environmentalDragToApply " + F(ball.m_environmentalDragToApply)
                            + "   sandDrag " + F(ball.m_sandDrag) + "   glueDrag " + F(ball.m_glueDrag)
                            + "   inSand " + ball.m_inSand + "   inGlue " + ball.m_inGlue
                            + "   SecondTillDrag " + ball.SecondTillDrag
                            + "   running " + ball.SecondTillDragRunning);
                Line("ball", "sleepSpeedThreshold " + F(ball.sleepSpeedThreshold)
                            + "   sleepAccelerationThreshold " + F(ball.sleepAccelerationThreshold)
                            + "   maxTimeToForceSleep " + F(ball.m_maxTimeToForceSleep)
                            + "   capUntilOverVel " + ball.capUntilOverVel);

                var maxForce = ball.m_maxForce;
                var minForce = ball.minForce;
                Line("force", "m_maxForce " + (maxForce != null ? F(maxForce.Value) : "null")
                            + "   minForce " + (minForce != null ? F(minForce.Value) : "null"));

                WritePowerCurve(ball);

                var cols = ball.GetComponentsInChildren<Collider>(true);
                Line("ball", cols.Length + " collider(s) in hierarchy:");
                for (int i = 0; i < cols.Length; i++)
                {
                    var c = cols[i];
                    if (c == null) continue;
                    string kind = c.TryCast<SphereCollider>() != null ? "Sphere"
                                : c.TryCast<BoxCollider>() != null ? "Box"
                                : c.TryCast<CapsuleCollider>() != null ? "Capsule"
                                : c.TryCast<MeshCollider>() != null ? "Mesh" : "Other";
                    var sphere = c.TryCast<SphereCollider>();
                    Line("ball", "  [" + i + "] " + kind
                                + "  name '" + c.name + "'"
                                + "  enabled " + c.enabled + "  trigger " + c.isTrigger
                                + "  onRoot " + (c.transform == ball.transform)
                                + (sphere != null ? "  radius " + F(sphere.radius) : "")
                                + "  material " + (c.sharedMaterial != null ? c.sharedMaterial.name : "<none>")
                                + "  layer " + c.gameObject.layer);
                }

                Flush();
            }
            catch (Exception ex)
            {
                Exception("WriteBall", ex);
            }
        }

        private static void WritePowerCurve(BallMovement ball)
        {
            var curve = ball.m_PowerCurve;
            if (curve == null)
            {
                Line("force", "m_PowerCurve: null");
                return;
            }

            var keys = curve.keys;
            var sb = new StringBuilder();
            sb.Append("m_PowerCurve: ").Append(keys.Length).Append(" key(s)  samples");
            for (float r = 0f; r <= 1.0001f; r += 0.25f)
            {
                sb.Append("  ").Append(F(r)).Append("->").Append(F(curve.Evaluate(r)));
            }
            Line("force", sb.ToString());

            for (int i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                Line("force", "  key[" + i + "] t " + F(k.m_Time) + "  v " + F(k.m_Value)
                            + "  in " + F(k.m_InTangent) + "  out " + F(k.m_OutTangent));
            }
        }

        private static void MaybeFlush()
        {
            if (s_buffer.Count >= FlushEveryLines || Time.realtimeSinceStartup >= s_nextFlush) Flush();
        }

        public static void Flush()
        {
            if (!IsActive || s_buffer.Count == 0) return;
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < s_buffer.Count; i++) sb.Append(s_buffer[i]).Append('\n');
                File.AppendAllText(s_path, sb.ToString(), new UTF8Encoding(false));
                s_buffer.Clear();
                s_nextFlush = Time.realtimeSinceStartup + FlushEverySeconds;
            }
            catch
            {
                s_failed = true;
                s_buffer.Clear();
            }
        }

        // Invariant-culture formatting: the game also runs under locales with a decimal comma,
        // which would otherwise make the log unparseable.
        public static string F(float v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

        public static string V(Vector3 v)
        {
            return "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")";
        }
    }
}
