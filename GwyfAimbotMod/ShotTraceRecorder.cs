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
    /// The drag schedule BallMovement actually applies, learned from real shots instead of guessed
    /// from field names. A constant per-step velocity deficit in the prediction is almost always a
    /// wrong drag, and that is invisible unless the live value is read back.
    /// </summary>
    internal static class MeasuredDragSchedule
    {
        public static bool IsMeasured { get; private set; }
        public static float DragAfterHit { get; private set; }
        public static float AngularDragAfterHit { get; private set; }
        public static float DragToSlow { get; private set; }
        public static float AngularDragToSlow { get; private set; }
        public static float SwitchSeconds { get; private set; }
        public static int Samples { get; private set; }

        /// <summary>
        /// Reads the schedule out of one recorded shot: the drag on the first step after the hit,
        /// the first value it changes to, and when that happened.
        /// </summary>
        public static void Observe(List<float> drag, List<float> angularDrag, float dt)
        {
            if (drag == null || drag.Count < 2) return;

            float first = drag[0];
            float firstAng = angularDrag != null && angularDrag.Count > 0 ? angularDrag[0] : 0f;

            float after = first;
            float afterAng = firstAng;
            float switchAt = -1f;

            for (int i = 1; i < drag.Count; i++)
            {
                if (Mathf.Abs(drag[i] - first) > 0.0001f)
                {
                    after = drag[i];
                    afterAng = angularDrag != null && i < angularDrag.Count ? angularDrag[i] : firstAng;
                    switchAt = i * dt;
                    break;
                }
            }

            DragAfterHit = first;
            AngularDragAfterHit = firstAng;
            DragToSlow = after;
            AngularDragToSlow = afterAng;
            SwitchSeconds = switchAt > 0f ? switchAt : float.MaxValue;
            Samples++;
            IsMeasured = true;

            DiagnosticsLog.Line("drag", "measured schedule: after hit " + DiagnosticsLog.F(DragAfterHit)
                + " (ang " + DiagnosticsLog.F(AngularDragAfterHit) + ")"
                + "  -> " + DiagnosticsLog.F(DragToSlow) + " (ang " + DiagnosticsLog.F(AngularDragToSlow) + ")"
                + (switchAt > 0f ? " at t=" + DiagnosticsLog.F(switchAt) + "s" : " (no switch within the shot)"));
        }
    }

    /// <summary>
    /// Measures how closely the shadow simulation reproduces the real shot.
    ///
    /// On every real shot the ball's position AND its live drag are sampled once per physics step.
    /// When the ball comes to rest the same opening state is replayed through
    /// <see cref="ShadowTrajectorySimulator"/> and the two paths are compared step by step. The
    /// per-step deviation is what tells you whether the reproduction is actually 1:1.
    /// </summary>
    internal sealed class ShotTraceRecorder
    {
        // Horizontal speed only: the ball also crosses a plain speed threshold when it simply
        // drops onto the tee at spawn, which used to be recorded as a shot and poisoned calibration.
        private const float StartHorizontalSpeed = 0.5f;
        private const int RestStepsRequired = 10;
        private const float MaxRecordSeconds = 30f;

        private readonly List<Vector3> _actual = new List<Vector3>(1024);
        private readonly List<float> _drag = new List<float>(1024);
        private readonly List<float> _angularDrag = new List<float>(1024);

        // BallMovement.UpdateCollisionLayer() swaps the ball between m_collideLayer and
        // m_ignoreLayer while it is in flight. The shadow ball sits on one fixed layer, so if the
        // real one switches mid-shot they stop seeing the same obstacles.
        private readonly List<int> _layer = new List<int>(1024);

        private bool _recording;
        private bool _pendingComparison;

        private Vector3 _startPos;
        private Quaternion _startRot;
        private Vector3 _startVelocity;
        private Vector3 _startAngularVelocity;
        private float _startForce;
        private float _startMaxForce;
        private float _prevHorizontalSpeed;
        private float _elapsed;
        private int _restSteps;
        private int _holeNumber;
        private string _sceneName;

        public bool HasResult { get; private set; }
        public float MaxDeviation { get; private set; }
        public float MeanDeviation { get; private set; }
        public float FinalDeviation { get; private set; }
        public int ComparedSteps { get; private set; }
        public string LastTracePath { get; private set; }

        public bool IsRecording { get { return _recording; } }

        /// <summary>Samples the live ball. Call from FixedUpdate so one sample lands per physics step.</summary>
        public void Sample(BallMovement ball, float pendingForce, float maxForce)
        {
            if (ball == null) { Stop(); return; }

            var rb = ball.m_rigidBody;
            if (rb == null) rb = ball.GetComponent<Rigidbody>();
            if (rb == null) { Stop(); return; }

            Vector3 v = rb.velocity;
            float horizontal = new Vector3(v.x, 0f, v.z).magnitude;

            if (!_recording)
            {
                if (_prevHorizontalSpeed <= StartHorizontalSpeed
                    && horizontal > StartHorizontalSpeed
                    && !_pendingComparison)
                {
                    BeginShot(ball, rb, pendingForce, maxForce);
                }

                _prevHorizontalSpeed = horizontal;
                return;
            }

            _prevHorizontalSpeed = horizontal;
            _elapsed += Time.fixedDeltaTime;

            _actual.Add(rb.position);
            _drag.Add(rb.drag);
            _angularDrag.Add(rb.angularDrag);
            _layer.Add(ball.gameObject.layer);

            float restThreshold = ball.sleepSpeedThreshold > 0f ? ball.sleepSpeedThreshold : 0.02f;
            if (v.magnitude < restThreshold || rb.IsSleeping()) _restSteps++;
            else _restSteps = 0;

            if (_restSteps >= RestStepsRequired || _elapsed >= MaxRecordSeconds)
            {
                _recording = false;
                _pendingComparison = true;
            }
        }

        private void BeginShot(BallMovement ball, Rigidbody rb, float pendingForce, float maxForce)
        {
            _recording = true;
            _elapsed = 0f;
            _restSteps = 0;
            _actual.Clear();
            _drag.Clear();
            _angularDrag.Clear();
            _layer.Clear();

            _startPos = rb.position;
            _startRot = rb.rotation;
            _startVelocity = rb.velocity;
            _startAngularVelocity = rb.angularVelocity;
            _startForce = pendingForce;
            _startMaxForce = maxForce;
            _holeNumber = (int)ball.HoleNumber;
            _sceneName = ball.gameObject.scene.name;

            _actual.Add(_startPos);
            _drag.Add(rb.drag);
            _angularDrag.Add(rb.angularDrag);
            _layer.Add(ball.gameObject.layer);

            DiagnosticsLog.Section("shot on hole " + _holeNumber);
            DiagnosticsLog.Line("shot", "start pos " + DiagnosticsLog.V(_startPos)
                + "  vel " + DiagnosticsLog.V(_startVelocity)
                + "  |v| " + DiagnosticsLog.F(_startVelocity.magnitude)
                + "  angVel " + DiagnosticsLog.V(_startAngularVelocity));
            DiagnosticsLog.Line("shot", "pullForce " + DiagnosticsLog.F(_startForce)
                + "/" + DiagnosticsLog.F(_startMaxForce)
                + "  drag at launch " + DiagnosticsLog.F(rb.drag)
                + "/" + DiagnosticsLog.F(rb.angularDrag));

            ShotCalibration.Observe(ball, _startForce, _startMaxForce, _startVelocity.magnitude);
        }

        /// <summary>
        /// Runs the prediction for the shot just recorded and writes the comparison. Call from
        /// Update - a physics scene must not be stepped from inside the physics loop.
        /// </summary>
        public void Flush(
            ShadowPhysicsWorld world,
            ShadowTrajectorySimulator simulator,
            BallMovement ball,
            Vector3 holePos,
            float cupRadius,
            float maxCupEntrySpeed,
            float secondsTillDrag,
            bool writeJson)
        {
            if (!_pendingComparison) return;
            _pendingComparison = false;

            // Learn the drag schedule before predicting, so this shot's own measurement is used.
            MeasuredDragSchedule.Observe(_drag, _angularDrag, Time.fixedDeltaTime);

            if (world == null || !world.IsReady || ball == null || _actual.Count < 2)
            {
                DiagnosticsLog.Line("shot", "comparison skipped (shadow world not ready or path too short)");
                Plugin.Logger.LogInfo("Shot trace: skipped (shadow world not ready or path too short).");
                return;
            }

            var tuning = BallTuning.Capture(ball, secondsTillDrag);

            float maxSeconds = _actual.Count * Time.fixedDeltaTime + 2f;
            var predicted = simulator.Run(
                world, tuning,
                _startPos, _startRot, _startVelocity, _startAngularVelocity,
                holePos, cupRadius, maxCupEntrySpeed,
                maxSeconds, 1);

            Compare(predicted.Path);

            string summary =
                "hole " + _holeNumber
                + "  steps " + ComparedSteps + " (actual " + _actual.Count
                + ", predicted " + (predicted.Path != null ? predicted.Path.Length : 0) + ")"
                + "  deviation max " + MaxDeviation.ToString("F3") + " m"
                + ", mean " + MeanDeviation.ToString("F3") + " m"
                + ", final " + FinalDeviation.ToString("F3") + " m"
                + "  launch " + _startVelocity.magnitude.ToString("F2") + " m/s"
                + "  sunk " + predicted.Sunk + "  hazard " + predicted.HitHazard + "  rested " + predicted.Rested;

            Plugin.Logger.LogInfo("Shot trace  " + summary);
            DiagnosticsLog.Line("shot", summary);
            DiagnosticsLog.Line("shot", "tuning used: drag " + DiagnosticsLog.F(tuning.DragAfterHit)
                + " -> " + DiagnosticsLog.F(tuning.DragToSlow)
                + " at " + (tuning.SecondsTillDrag == float.MaxValue ? "never" : DiagnosticsLog.F(tuning.SecondsTillDrag) + "s")
                + "  env " + DiagnosticsLog.F(tuning.EnvironmentalDrag)
                + "  radius " + DiagnosticsLog.F(world.BallRadius));
            LogDivergence(predicted.Path, world);

            if (writeJson)
            {
                try
                {
                    LastTracePath = WriteTrace(predicted.Path);
                    Plugin.Logger.LogInfo("Shot trace written to " + LastTracePath);
                    DiagnosticsLog.Line("shot", "trace -> " + LastTracePath);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning("Shot trace: could not write JSON: " + ex.Message);
                    DiagnosticsLog.Exception("WriteTrace", ex);
                }
            }

            DiagnosticsLog.Flush();
        }

        /// <summary>
        /// Records where the prediction first leaves the real path, and the per-step speed ratio.
        /// A ratio that is constant and below 1 is a drag error; a sudden break is a geometry error.
        /// </summary>
        private void LogDivergence(Vector3[] predicted, ShadowPhysicsWorld world)
        {
            if (!DiagnosticsLog.IsActive || predicted == null) return;

            int n = Mathf.Min(_actual.Count, predicted.Length);
            if (n < 3) return;

            float dt = Time.fixedDeltaTime;
            bool reported = false;

            // The ball's layer over the shot. A switch mid-flight means the shadow ball, which sits
            // on one fixed layer, was colliding against a different set of objects than the real one.
            if (_layer.Count > 1)
            {
                int first = _layer[0];
                int changeAt = -1;
                int changedTo = first;
                for (int i = 1; i < _layer.Count; i++)
                {
                    if (_layer[i] != first) { changeAt = i; changedTo = _layer[i]; break; }
                }
                DiagnosticsLog.Line("diverge", "ball layer during shot: starts " + first
                    + " (" + LayerMask.LayerToName(first) + ")"
                    + (changeAt >= 0
                        ? "  CHANGES to " + changedTo + " (" + LayerMask.LayerToName(changedTo)
                          + ") at step " + changeAt + " t=" + DiagnosticsLog.F(changeAt * dt) + "s"
                        : "  constant"));
            }

            for (int i = 1; i < n; i++)
            {
                float dev = Vector3.Distance(_actual[i], predicted[i]);
                if (!reported && dev > 0.05f)
                {
                    DiagnosticsLog.Line("diverge", "first > 5 cm at step " + i + " (t=" + DiagnosticsLog.F(i * dt) + "s)"
                        + "  actual " + DiagnosticsLog.V(_actual[i])
                        + "  predicted " + DiagnosticsLog.V(predicted[i]));

                    // The decisive comparison: what geometry sits at that spot in each world.
                    // A collider present in one list and not the other is the cause, not a symptom.
                    if (world != null)
                    {
                        DiagnosticsLog.Line("diverge", "geometry at contact  " + world.DescribeNear(_actual[i - 1], 0.6f));
                    }
                    reported = true;
                }
            }

            // Mean per-step distance ratio over the opening, before geometry can matter.
            int window = Mathf.Min(n - 1, 40);
            double sumActual = 0, sumPredicted = 0;
            float maxPredictedY = float.MinValue, maxActualY = float.MinValue;
            for (int i = 1; i <= window; i++)
            {
                sumActual += Vector3.Distance(_actual[i], _actual[i - 1]);
                sumPredicted += Vector3.Distance(predicted[i], predicted[i - 1]);
            }
            for (int i = 0; i < n; i++)
            {
                if (predicted[i].y > maxPredictedY) maxPredictedY = predicted[i].y;
                if (_actual[i].y > maxActualY) maxActualY = _actual[i].y;
            }

            if (sumActual > 0.0001)
            {
                double ratio = sumPredicted / sumActual;
                DiagnosticsLog.Line("diverge", "opening " + window + "-step distance ratio predicted/actual "
                    + ratio.ToString("F5", CultureInfo.InvariantCulture)
                    + "   (< 1 means the simulated ball is losing speed faster - suspect drag)");
            }

            DiagnosticsLog.Line("diverge", "max y  actual " + DiagnosticsLog.F(maxActualY)
                + "   predicted " + DiagnosticsLog.F(maxPredictedY)
                + (maxPredictedY > maxActualY + 0.15f
                    ? "   <-- simulated ball leaves the ground where the real one does not (geometry/layer)"
                    : ""));

            if (!reported) DiagnosticsLog.Line("diverge", "never exceeds 5 cm over the compared steps");
        }

        private void Compare(Vector3[] predicted)
        {
            int n = Mathf.Min(_actual.Count, predicted != null ? predicted.Length : 0);
            ComparedSteps = n;

            if (n == 0)
            {
                HasResult = false;
                return;
            }

            float max = 0f;
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                float d = Vector3.Distance(_actual[i], predicted[i]);
                if (d > max) max = d;
                sum += d;
            }

            MaxDeviation = max;
            MeanDeviation = (float)(sum / n);
            FinalDeviation = Vector3.Distance(_actual[_actual.Count - 1], predicted[predicted.Length - 1]);
            HasResult = true;
        }

        private string WriteTrace(Vector3[] predicted)
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "traces");
            Directory.CreateDirectory(dir);

            string file = Path.Combine(
                dir,
                "trace_" + Sanitize(_sceneName) + "_h" + _holeNumber + "_"
                + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");

            var j = new JsonBuilder();
            j.BeginObject(null);

            j.BeginObject("meta");
            j.Prop("timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            j.Prop("scene", _sceneName);
            j.Prop("holeNumber", _holeNumber);
            j.Prop("fixedDeltaTime", Time.fixedDeltaTime);
            j.EndObject();

            j.BeginObject("start");
            j.Prop("position", _startPos);
            j.Prop("rotation", _startRot);
            j.Prop("velocity", _startVelocity);
            j.Prop("angularVelocity", _startAngularVelocity);
            j.Prop("speed", _startVelocity.magnitude);
            j.EndObject();

            j.BeginObject("calibration");
            j.Prop("pullForce", _startForce);
            j.Prop("maxForce", _startMaxForce);
            j.Prop("correction", ShotCalibration.Correction);
            j.Prop("samples", ShotCalibration.Samples);
            j.Prop("powerCurveUsable", ShotCalibration.CurveIsUsable);
            j.EndObject();

            j.BeginObject("measuredDrag");
            j.Prop("isMeasured", MeasuredDragSchedule.IsMeasured);
            j.Prop("afterHit", MeasuredDragSchedule.DragAfterHit);
            j.Prop("angularAfterHit", MeasuredDragSchedule.AngularDragAfterHit);
            j.Prop("toSlow", MeasuredDragSchedule.DragToSlow);
            j.Prop("angularToSlow", MeasuredDragSchedule.AngularDragToSlow);
            j.Prop("switchSeconds", MeasuredDragSchedule.SwitchSeconds);
            j.EndObject();

            j.BeginObject("deviation");
            j.Prop("comparedSteps", ComparedSteps);
            j.Prop("actualSteps", _actual.Count);
            j.Prop("predictedSteps", predicted != null ? predicted.Length : 0);
            j.Prop("max", MaxDeviation);
            j.Prop("mean", MeanDeviation);
            j.Prop("final", FinalDeviation);
            j.EndObject();

            j.BeginArray("steps");
            int n = Mathf.Min(_actual.Count, predicted != null ? predicted.Length : 0);
            for (int i = 0; i < n; i++)
            {
                j.BeginObject(null);
                j.Prop("i", i);
                j.Prop("t", i * Time.fixedDeltaTime);
                j.Prop("actual", _actual[i]);
                j.Prop("predicted", predicted[i]);
                j.Prop("deviation", Vector3.Distance(_actual[i], predicted[i]));
                if (i < _drag.Count) j.Prop("liveDrag", _drag[i]);
                if (i < _angularDrag.Count) j.Prop("liveAngularDrag", _angularDrag[i]);
                j.EndObject();
            }
            j.EndArray();

            if (_actual.Count > n)
            {
                j.BeginArray("actualTail");
                for (int i = n; i < _actual.Count; i++) j.Prop(null, _actual[i]);
                j.EndArray();
            }
            else if (predicted != null && predicted.Length > n)
            {
                j.BeginArray("predictedTail");
                for (int i = n; i < predicted.Length; i++) j.Prop(null, predicted[i]);
                j.EndArray();
            }

            j.EndObject();

            File.WriteAllText(file, j.ToString(), new UTF8Encoding(false));
            return file;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            }
            return sb.ToString();
        }

        public void Stop()
        {
            _recording = false;
            _prevHorizontalSpeed = 0f;
        }
    }
}
