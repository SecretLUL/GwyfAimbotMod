using BepInEx.Configuration;
using UnityEngine;

namespace GwyfAimbotMod
{
    /// <summary>
    /// Converts a pull force into the launch velocity the game actually produces.
    ///
    /// Measured on ForestLevel (fixedDeltaTime 0.005, ball mass 1): a pull force of 4557 produced a
    /// launch speed of 22.717 m/s. 4557 * 0.005 / 1 = 22.785, and the missing 0.3% is exactly one
    /// step of drag - so the game applies the shot as a plain
    /// <c>Rigidbody.AddForce(dir * force)</c> (ForceMode.Force) inside FixedUpdate, which gives
    ///
    ///     speed = force * fixedDeltaTime / mass
    ///
    /// That is the model used here. It needs no fitting, and at full force it reproduces the
    /// 52 m/s that the original hard-coded MAX_PHYSICS_SPEED had approximated.
    ///
    /// <see cref="Correction"/> is a dimensionless residual (expected ~1.0) measured from real
    /// shots, so a wrong assumption still shows up as a number instead of silently biasing aim.
    ///
    /// m_PowerCurve is deliberately NOT used: on the measured build it evaluates to a flat 1.0 for
    /// every input, which made every candidate power produce the same launch speed and rendered the
    /// power search inert. <see cref="CurveIsUsable"/> reports whether that is still the case.
    /// </summary>
    internal static class ShotCalibration
    {
        /// <summary>Retained only for the legacy simulator's fallback path.</summary>
        public const float FallbackSpeedPerCurveUnit = 52.0f;

        private const int MaxSamples = 60;
        private const float MinForceRatio = 0.02f;

        private static ConfigEntry<float> s_stored;
        private static ConfigEntry<int> s_storedSamples;

        /// <summary>Dimensionless residual on the physical model; 1.0 means the model is exact.</summary>
        public static float Correction { get; private set; } = 1f;
        public static int Samples { get; private set; }
        public static float LastRelativeError { get; private set; }

        /// <summary>False once m_PowerCurve has been seen to be flat, which is the observed case.</summary>
        public static bool CurveIsUsable { get; private set; } = true;

        public static bool IsMeasured { get { return Samples > 0; } }

        /// <summary>Launch speed at full force, for display.</summary>
        public static float MaxLaunchSpeed(BallMovement ball, float maxForce)
        {
            return SpeedForForce(ball, maxForce, maxForce);
        }

        public static void Initialize(ConfigEntry<float> stored, ConfigEntry<int> storedSamples)
        {
            s_stored = stored;
            s_storedSamples = storedSamples;

            if (stored != null && stored.Value > 0.01f)
            {
                Correction = stored.Value;
                Samples = storedSamples != null ? Mathf.Max(0, storedSamples.Value) : 0;
            }
        }

        private static float BallMass(BallMovement ball)
        {
            if (ball == null) return 1f;
            var rb = ball.m_rigidBody;
            if (rb == null) rb = ball.GetComponent<Rigidbody>();
            return (rb != null && rb.mass > 0.0001f) ? rb.mass : 1f;
        }

        /// <summary>
        /// Impulse-per-force in m/s, i.e. the speed one unit of pull force buys.
        /// </summary>
        private static float SpeedPerForceUnit(BallMovement ball)
        {
            float dt = Time.fixedDeltaTime;
            if (dt <= 0f) dt = 0.02f;
            return dt / BallMass(ball);
        }

        /// <summary>Predicted launch speed in m/s for a pull force.</summary>
        public static float SpeedForForce(BallMovement ball, float force, float maxForce)
        {
            if (force <= 0f) return 0f;
            if (maxForce > 0f) force = Mathf.Min(force, maxForce);

            float speed = force * SpeedPerForceUnit(ball) * Correction;

            // The power curve is only applied when it actually shapes the response. A flat curve
            // would collapse every power onto one speed, which is the bug this replaced.
            if (ball != null && ball.m_PowerCurve != null && maxForce > 0f)
            {
                var curve = ball.m_PowerCurve;
                float ratio = Mathf.Clamp01(force / maxForce);
                float lo = curve.Evaluate(0.25f);
                float hi = curve.Evaluate(1.0f);

                if (Mathf.Abs(hi - lo) > 0.01f)
                {
                    CurveIsUsable = true;
                    speed = curve.Evaluate(ratio) * maxForce * SpeedPerForceUnit(ball) * Correction;
                }
                else
                {
                    CurveIsUsable = false;
                }
            }

            return speed;
        }

        /// <summary>
        /// Feeds one measured shot back in. <paramref name="measuredSpeed"/> is the ball's actual
        /// speed on the first physics step after the hit.
        /// </summary>
        public static void Observe(BallMovement ball, float force, float maxForce, float measuredSpeed)
        {
            if (maxForce <= 0f || force < maxForce * MinForceRatio) return;
            if (measuredSpeed <= 0.01f || float.IsNaN(measuredSpeed) || float.IsInfinity(measuredSpeed)) return;

            // Undo the current correction to get the raw model prediction.
            float modelSpeed = force * SpeedPerForceUnit(ball);
            if (modelSpeed <= 0.0001f) return;

            float sample = measuredSpeed / modelSpeed;

            float predicted = modelSpeed * Correction;
            LastRelativeError = predicted > 0.001f ? (measuredSpeed - predicted) / predicted : 0f;

            if (Samples == 0)
            {
                Correction = sample;
            }
            else
            {
                int window = Mathf.Min(Samples, MaxSamples);
                Correction += (sample - Correction) / (window + 1);
            }
            Samples++;

            if (s_stored != null) s_stored.Value = Correction;
            if (s_storedSamples != null) s_storedSamples.Value = Samples;

            Plugin.Logger.LogInfo(
                "Shot calibration: force " + force.ToString("F0") + "/" + maxForce.ToString("F0")
                + "  measured " + measuredSpeed.ToString("F3") + " m/s"
                + "  model " + modelSpeed.ToString("F3") + " m/s"
                + "  -> correction " + Correction.ToString("F4") + " after " + Samples + " shot(s)"
                + "  (this shot off by " + (LastRelativeError * 100f).ToString("F1") + "%)");
        }
    }
}
