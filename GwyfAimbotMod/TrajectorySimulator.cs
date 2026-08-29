using System;
using System.Collections.Generic;
using UnityEngine;

namespace GwyfAimbotMod
{
    public struct SimulationResult
    {
        public bool Sunk;
        public Vector3[] Path;
        public float MinDistanceToHole;
        public float FinalDistanceToHole;
        public Vector3 FinalPosition;
        public Vector3 FinalVelocity;
        public int TotalSteps;
        public int BounceCount;
    }

    public static class TrajectorySimulator
    {
        private static readonly List<Vector3> s_PointBuffer = new List<Vector3>(1200);

        public const float DEFAULT_BALL_RADIUS = 0.18f;
        public const float CUP_RADIUS = 0.34f;
        public const float MAX_CUP_ENTRY_SPEED = 5.8f; // Max speed (m/s) to drop cleanly in cup
        public const float DEFAULT_BOUNCE_THRESHOLD = 1.4f;

        /// <summary>
        /// Combines bounciness according to Unity PhysicMaterialCombine rule
        /// </summary>
        public static float CombineBounciness(PhysicMaterial matA, PhysicMaterial matB, float defaultVal = 0.72f)
        {
            if (matA == null && matB == null) return defaultVal;
            if (matA == null) return matB.bounciness > 0.01f ? matB.bounciness : defaultVal;
            if (matB == null) return matA.bounciness > 0.01f ? matA.bounciness : defaultVal;

            var combine = matA.bounceCombine;
            if (matB.bounceCombine > combine) combine = matB.bounceCombine;

            switch (combine)
            {
                case PhysicMaterialCombine.Multiply:
                    return matA.bounciness * matB.bounciness;
                case PhysicMaterialCombine.Minimum:
                    return Mathf.Min(matA.bounciness, matB.bounciness);
                case PhysicMaterialCombine.Maximum:
                    return Mathf.Max(matA.bounciness, matB.bounciness);
                case PhysicMaterialCombine.Average:
                default:
                    return (matA.bounciness + matB.bounciness) * 0.5f;
            }
        }

        /// <summary>
        /// Combines friction according to Unity PhysicMaterialCombine rule
        /// </summary>
        public static float CombineFriction(PhysicMaterial matA, PhysicMaterial matB, float defaultVal = 0.20f)
        {
            if (matA == null && matB == null) return defaultVal;
            if (matA == null) return matB.dynamicFriction > 0.001f ? matB.dynamicFriction : defaultVal;
            if (matB == null) return matA.dynamicFriction > 0.001f ? matA.dynamicFriction : defaultVal;

            var combine = matA.frictionCombine;
            if (matB.frictionCombine > combine) combine = matB.frictionCombine;

            switch (combine)
            {
                case PhysicMaterialCombine.Multiply:
                    return matA.dynamicFriction * matB.dynamicFriction;
                case PhysicMaterialCombine.Minimum:
                    return Mathf.Min(matA.dynamicFriction, matB.dynamicFriction);
                case PhysicMaterialCombine.Maximum:
                    return Mathf.Max(matA.dynamicFriction, matB.dynamicFriction);
                case PhysicMaterialCombine.Average:
                default:
                    return (matA.dynamicFriction + matB.dynamicFriction) * 0.5f;
            }
        }

        /// <summary>
        /// Full detailed physical trajectory integration matching Unity PhysX in Golf With Your Friends.
        /// </summary>
        public static SimulationResult SimulateShotDetailed(
            Vector3 startPos,
            Vector3 initialVelocity,
            Vector3 holePos,
            float ballRadius,
            float linearDrag,
            float angularDrag,
            PhysicMaterial ballMaterial,
            int maxSteps = 1000,
            int pointStride = 1)
        {
            if (ballRadius <= 0.01f) ballRadius = DEFAULT_BALL_RADIUS;
            if (linearDrag < 0f) linearDrag = 0.35f;
            if (angularDrag < 0f) angularDrag = 0.05f;

            float dt = 0.016f; // High-precision 60Hz physics sub-step
            Vector3 gravity = Physics.gravity.sqrMagnitude > 0.01f ? Physics.gravity : new Vector3(0f, -9.81f, 0f);
            float bounceThreshold = DEFAULT_BOUNCE_THRESHOLD;
            
            // All layers except Layer 2 (Ignore Raycast)
            int layerMask = ~0 & ~(1 << 2);

            s_PointBuffer.Clear();
            s_PointBuffer.Add(startPos);

            Vector3 pos = startPos;
            Vector3 vel = initialVelocity;
            float minDist = Vector3.Distance(startPos, holePos);
            bool isGrounded = false;
            Vector3 groundNormal = Vector3.up;
            PhysicMaterial groundMat = null;
            int bounceCount = 0;
            bool sunk = false;

            // Initial ground adherence check
            if (Physics.SphereCast(startPos + Vector3.up * 0.06f, ballRadius * 0.90f, Vector3.down, out RaycastHit initHit, 0.15f, layerMask, QueryTriggerInteraction.Ignore))
            {
                if (initHit.normal.y >= 0.35f)
                {
                    isGrounded = true;
                    groundNormal = initHit.normal;
                    groundMat = initHit.collider != null ? initHit.collider.sharedMaterial : null;
                    pos = initHit.point + groundNormal * (ballRadius + 0.001f);
                    
                    // Project initial velocity onto ground slope
                    vel = vel - groundNormal * Vector3.Dot(vel, groundNormal);
                }
            }

            int step = 0;
            for (; step < maxSteps; step++)
            {
                float distToHole = Vector3.Distance(pos, holePos);
                if (distToHole < minDist)
                {
                    minDist = distToHole;
                }

                // Hole cup entry check
                Vector3 horizBall = new Vector3(pos.x, 0f, pos.z);
                Vector3 horizHole = new Vector3(holePos.x, 0f, holePos.z);
                float hDist = Vector3.Distance(horizBall, horizHole);
                float vDist = pos.y - holePos.y;

                if (hDist < CUP_RADIUS && vDist >= -0.30f && vDist <= 0.40f)
                {
                    float currentSpeed = vel.magnitude;
                    if (currentSpeed <= MAX_CUP_ENTRY_SPEED)
                    {
                        s_PointBuffer.Add(holePos);
                        minDist = 0f;
                        sunk = true;
                        break;
                    }
                    else if (currentSpeed <= MAX_CUP_ENTRY_SPEED * 1.4f && hDist < CUP_RADIUS * 0.45f)
                    {
                        // Flag stick deflection
                        vel *= 0.30f;
                        s_PointBuffer.Add(pos);
                    }
                }

                // Physics Acceleration
                if (isGrounded)
                {
                    float normalGravDot = Vector3.Dot(gravity, groundNormal);
                    Vector3 slopeGravity = gravity - groundNormal * normalGravDot;
                    float normalForce = Mathf.Max(0f, -normalGravDot);

                    float dynFric = CombineFriction(ballMaterial, groundMat, 0.18f);
                    float rollResistance = 0.04f + angularDrag * 0.3f;

                    vel += slopeGravity * dt;

                    float speed = vel.magnitude;
                    if (speed > 0.0001f)
                    {
                        Vector3 tangentDir = vel / speed;
                        float frictionDecel = (dynFric * normalForce * 0.25f + rollResistance * speed) * dt;
                        float newSpeed = Mathf.Max(0f, speed - frictionDecel);
                        vel = tangentDir * newSpeed;
                    }

                    vel *= Mathf.Clamp01(1f - linearDrag * 0.15f * dt);
                }
                else
                {
                    vel += gravity * dt;
                    vel *= Mathf.Clamp01(1f - linearDrag * dt);
                }

                // Continuous Collision Detection & Motion Step
                float timeRemaining = dt;
                int subStep = 0;

                while (timeRemaining > 0.0001f && subStep < 4)
                {
                    Vector3 moveVec = vel * timeRemaining;
                    float moveDist = moveVec.magnitude;

                    if (moveDist > 0.0001f)
                    {
                        Vector3 moveDir = moveVec / moveDist;

                        // Forward obstacle sweep
                        if (Physics.SphereCast(pos, ballRadius * 0.95f, moveDir, out RaycastHit hit, moveDist + 0.003f, layerMask, QueryTriggerInteraction.Ignore))
                        {
                            float hitDist = Mathf.Max(0f, hit.distance - 0.002f);
                            float fraction = Mathf.Clamp01(hitDist / moveDist);

                            pos = hit.point + hit.normal * (ballRadius + 0.002f);

                            PhysicMaterial hitMat = hit.collider != null ? hit.collider.sharedMaterial : null;

                            float normalSpeed = Vector3.Dot(vel, hit.normal);
                            Vector3 normalVel = hit.normal * normalSpeed;
                            Vector3 tangentVel = vel - normalVel;

                            if (normalSpeed < 0f)
                            {
                                bounceCount++;

                                // Ground impact
                                if (hit.normal.y >= 0.45f)
                                {
                                    if (Mathf.Abs(normalSpeed) < bounceThreshold)
                                    {
                                        isGrounded = true;
                                        groundNormal = hit.normal;
                                        groundMat = hitMat;
                                        normalVel = Vector3.zero;
                                        vel = tangentVel;
                                        timeRemaining = 0f;
                                        break;
                                    }
                                    else
                                    {
                                        float bounce = CombineBounciness(ballMaterial, hitMat, 0.65f);
                                        normalVel = -normalVel * bounce;
                                        vel = normalVel + tangentVel;
                                    }
                                }
                                else // Vertical / angled Wall impact
                                {
                                    float wallBounce = CombineBounciness(ballMaterial, hitMat, 0.78f);
                                    normalVel = -normalVel * wallBounce;

                                    // Very low tangent friction on walls in GWYF so the ball glides cleanly
                                    float wallFriction = 0.05f;
                                    tangentVel *= Mathf.Clamp01(1f - wallFriction);

                                    vel = normalVel + tangentVel;

                                    if (isGrounded && groundNormal.y >= 0.40f)
                                    {
                                        // Keep ball grounded on floor after wall deflection
                                        vel = vel - groundNormal * Vector3.Dot(vel, groundNormal);
                                    }
                                    else
                                    {
                                        isGrounded = false;
                                    }
                                }
                            }

                            timeRemaining *= (1f - fraction);
                            subStep++;
                        }
                        else
                        {
                            pos += moveVec;
                            timeRemaining = 0f;

                            // Downward ground probe
                            if (isGrounded)
                            {
                                Vector3 probeOrigin = pos + groundNormal * 0.05f;
                                Vector3 probeDir = -groundNormal;
                                float probeDist = 0.16f;

                                if (Physics.SphereCast(probeOrigin, ballRadius * 0.85f, probeDir, out RaycastHit groundHit, probeDist, layerMask, QueryTriggerInteraction.Ignore))
                                {
                                    if (groundHit.normal.y >= 0.30f)
                                    {
                                        groundNormal = groundHit.normal;
                                        groundMat = groundHit.collider != null ? groundHit.collider.sharedMaterial : null;
                                        pos = groundHit.point + groundNormal * (ballRadius + 0.001f);
                                        vel = vel - groundNormal * Vector3.Dot(vel, groundNormal);
                                    }
                                    else
                                    {
                                        isGrounded = false;
                                    }
                                }
                                else
                                {
                                    isGrounded = false;
                                }
                            }
                        }
                    }
                    else
                    {
                        timeRemaining = 0f;
                    }
                }

                if (step % pointStride == 0 || isGrounded != (step > 0 && s_PointBuffer.Count > 1))
                {
                    s_PointBuffer.Add(pos);
                }

                if (vel.sqrMagnitude < 0.001f && isGrounded)
                {
                    break;
                }
                if (pos.y < -60f || pos.sqrMagnitude > 250000f)
                {
                    break;
                }
            }

            if (s_PointBuffer.Count == 0 || s_PointBuffer[s_PointBuffer.Count - 1] != pos)
            {
                s_PointBuffer.Add(pos);
            }

            float finalDist = Vector3.Distance(pos, holePos);

            return new SimulationResult
            {
                Sunk = sunk,
                Path = s_PointBuffer.ToArray(),
                MinDistanceToHole = minDist,
                FinalDistanceToHole = finalDist,
                FinalPosition = pos,
                FinalVelocity = vel,
                TotalSteps = step,
                BounceCount = bounceCount
            };
        }
    }
}
