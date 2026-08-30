using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace GwyfAimbotMod
{
    public class AimbotBehaviour : MonoBehaviour
    {
        public AimbotBehaviour(IntPtr ptr) : base(ptr) { }

        private enum SearchState
        {
            Idle,
            DirectEvaluation,
            AngleSweep,
            PowerScan,        // resumable power sweep for one direction, one trajectory per step
            PowerRefinement,
            Completed
        }

        private LineRenderer _solutionLineRenderer;
        private LineRenderer _liveAimLineRenderer;

        private BallMovement _targetBall;
        private FlagPoint _targetHole;
        private int _lastTargetHoleInt = -1;
        private float _nextTargetSearchTime = 0f;

        private WorldPowerBarCosmetic _worldPowerBar;
        private InGameSOFiller _soFiller;
        private RectTransform _powerBarRect;

        private SearchState _searchState = SearchState.Idle;
        private bool _isHoleInOne = false;
        private bool _hasSolution = false;

        private Vector3[] _winningPath;
        private float _winningPower; // In actual Game Force units (0 to maxPower)
        private Vector3 _winningDirection;
        private float _winningMinDist;

        // Angle sweep parameters
        private List<float> _candidateAngles = new List<float>();
        private int _currentAngleIndex = 0;

        // Candidate angles that passed near the hole during probe shots
        private struct CandidateAngle
        {
            public float AngleOffset;
            public float ClosestFlybyDist;
            public float ProbePower;
        }
        private List<CandidateAngle> _promisingCandidates = new List<CandidateAngle>();
        private int _candidateRefineIndex = 0;

        // Which of the two probe powers of the current angle is next. The sweep used to run both
        // in one go, which put two full trajectories into a single frame.
        private int _currentProbeIndex = 0;

        // ---- resumable power scan ----
        // A full power sweep is 13+ trajectories. Running it inside one frame cost over half a
        // second at 200 Hz, so it is stepped one trajectory at a time like every other stage.
        private Vector3 _scanDir;
        private int _scanIndex;
        private int _scanCount;
        private float _scanLow;
        private float _scanStep;
        private float _scanBestDist;
        private Vector3[] _scanBestPath;
        private float _scanBestP;
        private SearchState _scanReturnTo;

        // Second phase: centre the solution inside the band of powers that sink, so the player
        // gets tolerance on the power bar instead of a knife edge.
        private bool _scanRefining;
        private int _scanRefineIndex;
        private float _scanSinkMin;
        private float _scanSinkMax;
        private Vector3[] _scanSinkPath;
        private static readonly float[] ScanRefineOffsets = { -0.5f, -0.25f, 0.25f, 0.5f };

        private Vector3 _lastSearchBallPos = Vector3.negativeInfinity;

        // ---- internal 1:1 simulation ----
        private ShadowPhysicsWorld _shadow;
        private ShadowTrajectorySimulator _shadowSim;
        private ShotTraceRecorder _recorder;

        // Simulation context, refreshed once per search instead of per candidate shot.
        private bool _simContextReady;
        private bool _simUseShadow;
        private BallTuning _simTuning;
        private float _simBallRadius;
        private float _simDrag;
        private float _simAngDrag;
        private PhysicMaterial _simBallMat;

        // Last non-zero pull force seen; the force is already back to 0 by the time the ball moves.
        private float _lastPullForce;

        private bool _loggedEnvironment;
        private BallMovement _loggedBall;
        private int _loggedHole = int.MinValue;
        private int _loggedCupHole = int.MinValue;

        void Start()
        {
            // 1. Solution line renderer (Green / Orange)
            _solutionLineRenderer = gameObject.AddComponent<LineRenderer>();
            _solutionLineRenderer.startWidth = 0.055f;
            _solutionLineRenderer.endWidth = 0.055f;
            var mat1 = new Material(Shader.Find("Hidden/Internal-Colored"));
            _solutionLineRenderer.material = mat1;
            _solutionLineRenderer.positionCount = 0;

            // 2. Live Aim line renderer (Cyan / White for real-time aiming)
            var liveObj = new GameObject("LiveAimLine");
            liveObj.transform.SetParent(transform);
            _liveAimLineRenderer = liveObj.AddComponent<LineRenderer>();
            _liveAimLineRenderer.startWidth = 0.035f;
            _liveAimLineRenderer.endWidth = 0.035f;
            var mat2 = new Material(Shader.Find("Hidden/Internal-Colored"));
            _liveAimLineRenderer.material = mat2;
            _liveAimLineRenderer.startColor = new Color(0.2f, 0.9f, 1f, 0.85f);
            _liveAimLineRenderer.endColor = new Color(1f, 1f, 1f, 0.35f);
            _liveAimLineRenderer.positionCount = 0;

            _shadow = new ShadowPhysicsWorld();
            _shadowSim = new ShadowTrajectorySimulator();
            _recorder = new ShotTraceRecorder();

            InitializeSweepAngles();
        }

        [HideFromIl2Cpp]
        private void InitializeSweepAngles()
        {
            _candidateAngles.Clear();
            _candidateAngles.Add(0f); // Direct towards hole

            float step = Mathf.Max(0.5f, Plugin.AngleStepDegrees.Value);
            float span = Mathf.Clamp(Plugin.AngleSpanDegrees.Value, step, 180f);

            // Scan outward symmetrically so the cheapest, most direct angles are tried first.
            for (float a = step; a <= span; a += step)
            {
                _candidateAngles.Add(a);
                _candidateAngles.Add(-a);
            }
        }

        void Update()
        {
            // Vor dem Zielabgleich, damit der Dump auch dann laeuft, wenn Ball oder Loch
            // noch nicht gefunden sind - PhysicsParameterDump sucht sich den Ball selbst.
            if (Plugin.DumpKey != null && Input.GetKeyDown(Plugin.DumpKey.Value))
            {
                PhysicsParameterDump.Run(_targetBall);
            }

            FindTargets();

            if (!_loggedEnvironment)
            {
                _loggedEnvironment = true;
                DiagnosticsLog.WriteEnvironment();
            }

            if (_targetBall == null || _targetHole == null)
            {
                ClearPaths();
                _searchState = SearchState.Idle;
                return;
            }

            // Full ball state once per ball and once per hole: everything a later trace has to be
            // read against, without needing the F9 dump to have been pressed.
            int holeNow = (int)_targetBall.HoleNumber;
            if (_loggedBall != _targetBall || _loggedHole != holeNow)
            {
                _loggedBall = _targetBall;
                _loggedHole = holeNow;
                DiagnosticsLog.Section("hole " + holeNow + " in scene '" + _targetBall.gameObject.scene.name + "'");
                DiagnosticsLog.WriteBall(_targetBall);
            }

            // Keep the mirrored hole in sync. Costs a slice of one frame per hole, not per shot.
            if (Plugin.UseShadowPhysics.Value)
            {
                _shadow.EnsureBuilt(_targetBall, (int)_targetBall.HoleNumber, Plugin.BuildBudgetMs.Value);
            }

            // The shadow world usually finishes building after a search has already started with
            // the fallback engine. Paths from the two engines are not comparable, so switch the
            // engine only here and restart the search when it changes.
            bool shadowReady = Plugin.UseShadowPhysics.Value && _shadow != null && _shadow.IsReady;
            if (!_simContextReady || shadowReady != _simUseShadow)
            {
                PrepareSimulationContext();
                _searchState = SearchState.Idle;
                _hasSolution = false;
                _lastSearchBallPos = Vector3.negativeInfinity;

                // Once the mirror exists, record what geometry actually surrounds the cup. If the
                // hole is a real opening in the mesh the ball should drop in on its own; if nothing
                // is mirrored there, sinking can only ever come from the radius heuristic.
                if (shadowReady && _loggedCupHole != _loggedHole)
                {
                    _loggedCupHole = _loggedHole;
                    Vector3 cup = _targetHole.HolePosition.position;
                    DiagnosticsLog.Line("cup", "hole " + _loggedHole + " at " + DiagnosticsLog.V(cup)
                        + "   ball at " + DiagnosticsLog.V(_targetBall.transform.position)
                        + "   distance " + DiagnosticsLog.F(Vector3.Distance(cup, _targetBall.transform.position)) + " m");
                    DiagnosticsLog.Line("cup", "geometry around cup  " + _shadow.DescribeNear(cup, 0.6f));
                    DiagnosticsLog.Flush();
                }
            }

            float pull = GetCurrentPullForce();
            if (pull > 0.01f) _lastPullForce = pull;

            // Comparison of the last real shot against its prediction. Deliberately outside
            // FixedUpdate: a physics scene must not be stepped from inside the physics loop.
            if (Plugin.TraceEnabled.Value)
            {
                _recorder.Flush(
                    _shadow, _shadowSim, _targetBall,
                    _targetHole.HolePosition.position,
                    Plugin.CupRadius.Value,
                    Plugin.MaxCupEntrySpeed.Value,
                    Plugin.SecondsTillDrag.Value,
                    Plugin.TraceWriteJson.Value);
            }

            bool isMoving = false;
            var rb = _targetBall.GetComponent<Rigidbody>();
            if (rb != null)
            {
                isMoving = rb.velocity.sqrMagnitude > 0.001f;
            }

            if (isMoving)
            {
                _searchState = SearchState.Idle;
                _hasSolution = false;
                _lastSearchBallPos = Vector3.negativeInfinity;
                ClearPaths();
            }
            else
            {
                if (_searchState == SearchState.Idle && !_hasSolution)
                {
                    if (Vector3.Distance(_targetBall.transform.position, _lastSearchBallPos) > 0.02f)
                    {
                        StartNewSearch();
                    }
                }

                if (_searchState != SearchState.Idle && _searchState != SearchState.Completed)
                {
                    ProcessSearchWithTimeBudget(Plugin.SearchBudgetMs.Value);
                }

                UpdateLiveAimTrajectory();
            }

            // Auto-aim assist (hold the configured key)
            if (_hasSolution && Input.GetKey(Plugin.AutoAimKey.Value) && Camera.main != null && _winningDirection.sqrMagnitude > 0.01f)
            {
                Vector3 lookDir = new Vector3(_winningDirection.x, 0, _winningDirection.z).normalized;
                if (lookDir.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(lookDir, Vector3.up);
                    Camera.main.transform.rotation = Quaternion.Slerp(Camera.main.transform.rotation, targetRot, Time.deltaTime * 12f);
                }
            }
        }

        // The session log has to survive the user simply closing the game, so it is flushed on
        // every shutdown path Unity offers rather than only on a timer.
        void OnApplicationQuit()
        {
            DiagnosticsLog.Line("session", "application quit");
            DiagnosticsLog.Flush();
        }

        void OnDestroy()
        {
            DiagnosticsLog.Flush();
        }

        void FixedUpdate()
        {
            // One sample per physics step, so the recorded path lines up index-for-index with a
            // simulation stepped at the same fixedDeltaTime.
            if (!Plugin.TraceEnabled.Value || _targetBall == null) return;
            _recorder.Sample(_targetBall, _lastPullForce, GetMaxPower());
        }

        [HideFromIl2Cpp]
        private void StartNewSearch()
        {
            _searchState = SearchState.DirectEvaluation;
            _hasSolution = false;
            _isHoleInOne = false;
            _currentAngleIndex = 0;
            _currentProbeIndex = 0;
            _candidateRefineIndex = 0;
            _scanRefining = false;
            _scanIndex = 0;
            _scanCount = 0;
            _scanBestPath = null;
            _scanSinkPath = null;
            _promisingCandidates.Clear();
            _lastSearchBallPos = _targetBall.transform.position;

            _winningMinDist = float.MaxValue;
            _winningPath = null;

            _shadowSim.ResetStats();
            PrepareSimulationContext();
        }

        /// <summary>
        /// Picks the simulation engine and caches everything it needs, so the per-candidate loop
        /// never touches IL2CPP fields.
        /// </summary>
        [HideFromIl2Cpp]
        private void PrepareSimulationContext()
        {
            _simUseShadow = Plugin.UseShadowPhysics.Value && _shadow != null && _shadow.IsReady;

            if (_targetBall == null) return;

            _simContextReady = true;
            _simTuning = BallTuning.Capture(_targetBall, Plugin.SecondsTillDrag.Value);

            _simBallRadius = _targetBall.BallRadius > 0 ? _targetBall.BallRadius : TrajectorySimulator.DEFAULT_BALL_RADIUS;
            _simDrag = _targetBall.dragToHitBall > 0 ? _targetBall.dragToHitBall : 0.35f;
            _simAngDrag = _targetBall.angDragToHitBall > 0 ? _targetBall.angDragToHitBall : 0.05f;

            var col = _targetBall.GetComponent<Collider>();
            _simBallMat = col != null ? col.sharedMaterial : null;
        }

        /// <summary>
        /// Runs one candidate shot through whichever engine is active. The shadow engine replays the
        /// game's own solver against the mirrored hole; the legacy engine is the approximate
        /// integrator, used only while the shadow world is unavailable.
        /// </summary>
        [HideFromIl2Cpp]
        private SimulationResult RunSimulation(Vector3 startPos, Vector3 velocity, Vector3 holePos, float simSeconds, int pointStride)
        {
            simSeconds = Mathf.Clamp(simSeconds, 0.25f, Plugin.MaxSimSeconds.Value);

            if (_simUseShadow)
            {
                return _shadowSim.Run(
                    _shadow,
                    _simTuning,
                    startPos,
                    _targetBall.transform.rotation,
                    velocity,
                    Vector3.zero,
                    holePos,
                    Plugin.CupRadius.Value,
                    Plugin.MaxCupEntrySpeed.Value,
                    simSeconds,
                    pointStride);
            }

            // The legacy integrator runs its own fixed 0.016 s step.
            int maxSteps = Mathf.Clamp(Mathf.CeilToInt(simSeconds / 0.016f), 50, 2000);
            return TrajectorySimulator.SimulateShotDetailed(
                startPos, velocity, holePos, _simBallRadius, _simDrag, _simAngDrag, _simBallMat, maxSteps, pointStride);
        }

        [HideFromIl2Cpp]
        private void ClearPaths()
        {
            if (_solutionLineRenderer != null) _solutionLineRenderer.positionCount = 0;
            if (_liveAimLineRenderer != null) _liveAimLineRenderer.positionCount = 0;
        }

        [HideFromIl2Cpp]
        private void FindTargets()
        {
            if (_targetBall == null || !_targetBall.gameObject.activeInHierarchy || Time.time > _nextTargetSearchTime)
            {
                _nextTargetSearchTime = Time.time + 1.2f;

                if (_targetBall == null || !_targetBall.gameObject.activeInHierarchy)
                {
                    var balls = FindObjectsOfType<BallMovement>();
                    foreach (var ball in balls)
                    {
                        if (ball.IsMasterBall)
                        {
                            _targetBall = ball;
                            _searchState = SearchState.Idle;
                            _hasSolution = false;
                            break;
                        }
                    }
                }

                int currentHoleInt = -1;
                if (_targetBall != null)
                {
                    currentHoleInt = (int)_targetBall.HoleNumber;
                }
                else
                {
                    var options = Resources.FindObjectsOfTypeAll<GameOptions>();
                    if (options != null && options.Length > 0)
                    {
                        currentHoleInt = options[0].currentHoleNumber;
                    }
                }

                if (_targetHole == null || !_targetHole.gameObject.activeInHierarchy || _lastTargetHoleInt != currentHoleInt)
                {
                    var flags = FindObjectsOfType<FlagPoint>();
                    FlagPoint closestFlag = null;
                    float closestDist = float.MaxValue;
                    bool foundExact = false;

                    foreach (var flag in flags)
                    {
                        int flagHoleNum = flag.m_holeNumber;
                        if (currentHoleInt != -1 && flagHoleNum == currentHoleInt)
                        {
                            _targetHole = flag;
                            _lastTargetHoleInt = currentHoleInt;
                            _searchState = SearchState.Idle;
                            _hasSolution = false;
                            foundExact = true;
                            break;
                        }

                        if (_targetBall != null)
                        {
                            float dist = Vector3.Distance(_targetBall.transform.position, flag.transform.position);
                            if (dist < closestDist)
                            {
                                closestDist = dist;
                                closestFlag = flag;
                            }
                        }
                    }

                    if (!foundExact && closestFlag != null)
                    {
                        _targetHole = closestFlag;
                        _lastTargetHoleInt = currentHoleInt;
                        _searchState = SearchState.Idle;
                        _hasSolution = false;
                    }
                }

                if (_worldPowerBar == null || !_worldPowerBar.gameObject.activeInHierarchy)
                {
                    _worldPowerBar = FindObjectOfType<WorldPowerBarCosmetic>();
                }
                if (_soFiller == null || !_soFiller.gameObject.activeInHierarchy)
                {
                    _soFiller = FindObjectOfType<InGameSOFiller>();
                }

                if (_powerBarRect == null)
                {
                    if (_worldPowerBar != null && _worldPowerBar.m_hudObject != null)
                    {
                        _powerBarRect = _worldPowerBar.m_hudObject.GetComponent<RectTransform>();
                    }

                    if (_powerBarRect == null)
                    {
                        GameObject powerBarObj = GameObject.Find("PowerBarFill")
                                              ?? GameObject.Find("PowerBar_Fill")
                                              ?? GameObject.Find("PowerMeter")
                                              ?? GameObject.Find("PlayerInfoHolder")
                                              ?? GameObject.Find("FillArea");
                        if (powerBarObj != null)
                        {
                            _powerBarRect = powerBarObj.GetComponent<RectTransform>();
                        }
                    }
                }
            }

            if (_hasSolution && _targetBall != null && _winningPath != null && _winningPath.Length > 0)
            {
                if (Vector3.Distance(_targetBall.transform.position, _winningPath[0]) > 0.12f)
                {
                    _hasSolution = false;
                }
            }
        }

        [HideFromIl2Cpp]
        private float CalculateBallSpeed(float force, float maxPower)
        {
            // Measured, not guessed: k comes from the launch velocity of real shots.
            return ShotCalibration.SpeedForForce(_targetBall, force, maxPower);
        }

        [HideFromIl2Cpp]
        private void UpdateLiveAimTrajectory()
        {
            if (_liveAimLineRenderer == null || _targetBall == null || _targetHole == null) return;

            float currentForce = GetCurrentPullForce();
            float maxPower = GetMaxPower();

            if (currentForce > (maxPower * 0.005f) && Camera.main != null)
            {
                Vector3 ballPos = _targetBall.transform.position;
                Vector3 holePos = _targetHole.HolePosition.position;

                Vector3 aimDir = Camera.main.transform.forward;
                aimDir.y = 0f;
                aimDir.Normalize();
                if (aimDir.sqrMagnitude < 0.001f) aimDir = Vector3.forward;

                // Only recompute when the shot actually changed, or after the throttle interval.
                // In between, the previously drawn line stays on screen.
                float forceStep = Mathf.Max(1f, maxPower * 0.01f);
                bool changed = Mathf.Abs(currentForce - _lastLiveAimForce) > forceStep
                               || Vector3.Angle(aimDir, _lastLiveAimDir) > 0.75f;
                bool due = Time.unscaledTime - _lastLiveAimTime >= Plugin.LiveAimIntervalSeconds.Value;

                if (!changed && !due) return;

                _lastLiveAimTime = Time.unscaledTime;
                _lastLiveAimForce = currentForce;
                _lastLiveAimDir = aimDir;

                float speed = CalculateBallSpeed(currentForce, maxPower);
                Vector3 initVelocity = aimDir * speed;

                // The legacy integrator sweeps the live scene, so the real ball's own collider has
                // to be taken out of the query. The shadow scene does not contain it at all.
                var col = _simUseShadow ? null : _targetBall.GetComponent<Collider>();
                bool oldEnabled = true;
                if (col != null)
                {
                    oldEnabled = col.enabled;
                    col.enabled = false;
                }

                try
                {
                    var result = RunSimulation(ballPos, initVelocity, holePos, Plugin.LiveAimSimSeconds.Value, 2);

                    _liveAimLineRenderer.positionCount = result.Path.Length;
                    for (int i = 0; i < result.Path.Length; i++)
                    {
                        _liveAimLineRenderer.SetPosition(i, result.Path[i]);
                    }

                    if (result.Sunk)
                    {
                        _liveAimLineRenderer.startColor = Color.green;
                        _liveAimLineRenderer.endColor = Color.cyan;
                    }
                    else if (result.HitHazard)
                    {
                        _liveAimLineRenderer.startColor = new Color(1f, 0.25f, 0.2f, 0.9f);
                        _liveAimLineRenderer.endColor = new Color(0.6f, 0.1f, 0.1f, 0.5f);
                    }
                    else
                    {
                        _liveAimLineRenderer.startColor = new Color(0.2f, 0.9f, 1f, 0.85f);
                        _liveAimLineRenderer.endColor = new Color(1f, 1f, 1f, 0.35f);
                    }
                }
                finally
                {
                    if (col != null) col.enabled = oldEnabled;
                }
            }
            else
            {
                _liveAimLineRenderer.positionCount = 0;
            }
        }

        [HideFromIl2Cpp]
        private void ProcessSearchWithTimeBudget(float maxMs)
        {
            if (_targetBall == null || _targetHole == null) return;

            Vector3 ballPos = _targetBall.transform.position;
            Vector3 holePos = _targetHole.HolePosition.position;
            float maxPower = GetMaxPower();

            Vector3 dirToHole = (holePos - ballPos);
            dirToHole.y = 0;
            dirToHole.Normalize();
            if (dirToHole.sqrMagnitude < 0.001f) dirToHole = Vector3.forward;
            _dirToHole = dirToHole;

            // Only the legacy integrator needs the real ball hidden from its sweeps.
            var ballCol = _simUseShadow ? null : _targetBall.GetComponent<Collider>();
            bool oldEnabled = true;
            if (ballCol != null)
            {
                oldEnabled = ballCol.enabled;
                ballCol.enabled = false;
            }

            float startTime = Time.realtimeSinceStartup;

            try
            {
                // One trajectory per iteration, budget checked BEFORE each. A single trajectory at
                // 200 Hz costs more than a 60 Hz frame, so the loop can only ever overrun by one -
                // it used to run a whole 13-trajectory power sweep before looking at the clock.
                while (true)
                {
                    if ((Time.realtimeSinceStartup - startTime) * 1000f >= maxMs) return;

                    // STAGE 1: direct line to the hole
                    if (_searchState == SearchState.DirectEvaluation)
                    {
                        BeginPowerScan(dirToHole, maxPower, SearchState.AngleSweep);
                        continue;
                    }

                    // STAGE 2: geometry sweep, probing each angle with two representative powers
                    if (_searchState == SearchState.AngleSweep)
                    {
                        if (_currentAngleIndex >= _candidateAngles.Count)
                        {
                            if (_promisingCandidates.Count > 0)
                            {
                                _promisingCandidates.Sort((a, b) => a.ClosestFlybyDist.CompareTo(b.ClosestFlybyDist));
                                _searchState = SearchState.PowerRefinement;
                                _candidateRefineIndex = 0;
                            }
                            else
                            {
                                CompleteSearchWithBestPath();
                                return;
                            }
                            continue;
                        }

                        if (StepAngleSweep(ballPos, holePos, maxPower)) return;
                        continue;
                    }

                    // STAGE 3: resumable power sweep for one direction
                    if (_searchState == SearchState.PowerScan)
                    {
                        if (StepPowerScan(ballPos, holePos, maxPower)) return;
                        continue;
                    }

                    // STAGE 4: refine the angles that flew close
                    if (_searchState == SearchState.PowerRefinement)
                    {
                        if (_candidateRefineIndex >= _promisingCandidates.Count)
                        {
                            CompleteSearchWithBestPath();
                            return;
                        }

                        CandidateAngle candidate = _promisingCandidates[_candidateRefineIndex];
                        Vector3 candidateDir = Quaternion.Euler(0, candidate.AngleOffset, 0) * dirToHole;
                        BeginPowerScan(candidateDir, maxPower, SearchState.PowerRefinement);
                        continue;
                    }

                    return;
                }
            }
            finally
            {
                if (ballCol != null)
                {
                    ballCol.enabled = oldEnabled;
                }
            }
        }

        /// <summary>
        /// Runs one probe trajectory of the angle sweep. Returns true when a solution was found and
        /// the search is finished.
        /// </summary>
        [HideFromIl2Cpp]
        private bool StepAngleSweep(Vector3 ballPos, Vector3 holePos, float maxPower)
        {
            float angle = _candidateAngles[_currentAngleIndex];
            Vector3 testDir = Quaternion.Euler(0, angle, 0) * _dirToHole;

            float probeP = _currentProbeIndex == 0 ? maxPower * 0.45f : maxPower * 0.75f;
            float speed = CalculateBallSpeed(probeP, maxPower);
            var probeResult = RunSimulation(ballPos, testDir * speed, holePos, Plugin.ProbeSimSeconds.Value, 1);

            bool advanceAngle = true;

            if (probeResult.Sunk)
            {
                ApplyWinningPath(probeResult.Path, probeP, testDir, 0f, true);
                return true;
            }

            // A shot that ends in water is not a fallback worth recommending.
            if (!probeResult.HitHazard)
            {
                if (probeResult.MinDistanceToHole < 1.4f)
                {
                    _promisingCandidates.Add(new CandidateAngle
                    {
                        AngleOffset = angle,
                        ClosestFlybyDist = probeResult.MinDistanceToHole,
                        ProbePower = probeP
                    });
                    // Close enough to be worth a full power sweep later; the second probe adds nothing.
                    _currentProbeIndex = 0;
                    _currentAngleIndex++;
                    return false;
                }

                if (probeResult.FinalDistanceToHole < _winningMinDist && probeP >= maxPower * 0.15f)
                {
                    _winningMinDist = probeResult.FinalDistanceToHole;
                    _winningPath = probeResult.Path;
                    _winningPower = probeP;
                    _winningDirection = testDir;
                }
            }

            _currentProbeIndex++;
            if (_currentProbeIndex < 2) advanceAngle = false;

            if (advanceAngle)
            {
                _currentProbeIndex = 0;
                _currentAngleIndex++;
            }
            return false;
        }

        // Cached so the sweep helpers do not each recompute it.
        private Vector3 _dirToHole = Vector3.forward;

        // Live-aim throttle. Recomputing the preview every frame while charging costs a full
        // trajectory per frame, which alone is enough to halve the framerate at 200 Hz.
        private float _lastLiveAimTime = -1f;
        private float _lastLiveAimForce = -1f;
        private Vector3 _lastLiveAimDir = Vector3.zero;

        /// <summary>Starts a resumable power sweep along <paramref name="dir"/>.</summary>
        [HideFromIl2Cpp]
        private void BeginPowerScan(Vector3 dir, float maxPower, SearchState returnTo)
        {
            float lowP = maxPower * 0.08f;
            float highP = maxPower * 0.98f;
            int subdivisions = Mathf.Max(4, Plugin.PowerSubdivisions.Value);

            _scanDir = dir;
            _scanLow = lowP;
            _scanStep = (highP - lowP) / subdivisions;
            _scanCount = subdivisions + 1;
            _scanIndex = 0;
            _scanBestDist = float.MaxValue;
            _scanBestPath = null;
            _scanBestP = 0f;
            _scanRefining = false;
            _scanRefineIndex = 0;
            _scanSinkPath = null;
            _scanReturnTo = returnTo;
            _searchState = SearchState.PowerScan;
        }

        /// <summary>
        /// Runs exactly one trajectory of the power sweep. Returns true when the search is finished.
        /// </summary>
        [HideFromIl2Cpp]
        private bool StepPowerScan(Vector3 ballPos, Vector3 holePos, float maxPower)
        {
            float simSeconds = Plugin.MaxSimSeconds.Value;

            if (_scanRefining)
            {
                // Widen the known sinking band so the recommended power sits in its middle.
                if (_scanRefineIndex < ScanRefineOffsets.Length)
                {
                    float fp = Mathf.Clamp(
                        _scanSinkMin + ScanRefineOffsets[_scanRefineIndex] * _scanStep,
                        _scanLow,
                        _scanLow + (_scanCount - 1) * _scanStep);
                    _scanRefineIndex++;

                    var fr = RunSimulation(ballPos, _scanDir * CalculateBallSpeed(fp, maxPower), holePos, simSeconds, 1);
                    if (fr.Sunk)
                    {
                        if (fp < _scanSinkMin) _scanSinkMin = fp;
                        if (fp > _scanSinkMax) _scanSinkMax = fp;
                        _scanSinkPath = fr.Path;
                    }
                    return false;
                }

                float winningPower = (_scanSinkMin + _scanSinkMax) * 0.5f;
                ApplyWinningPath(_scanSinkPath, winningPower, _scanDir, 0f, true);
                return true;
            }

            if (_scanIndex >= _scanCount)
            {
                // Sweep exhausted without a sink: keep the best approach and resume the caller.
                if (_scanBestDist < _winningMinDist && _scanBestPath != null)
                {
                    _winningMinDist = _scanBestDist;
                    _winningPath = _scanBestPath;
                    _winningPower = _scanBestP;
                    _winningDirection = _scanDir;
                }

                if (_scanReturnTo == SearchState.AngleSweep)
                {
                    _searchState = SearchState.AngleSweep;
                    _currentAngleIndex = 0;
                    _currentProbeIndex = 0;
                }
                else
                {
                    _candidateRefineIndex++;
                    _searchState = SearchState.PowerRefinement;
                }
                return false;
            }

            float testP = _scanLow + _scanIndex * _scanStep;
            _scanIndex++;

            var result = RunSimulation(ballPos, _scanDir * CalculateBallSpeed(testP, maxPower), holePos, simSeconds, 1);

            if (result.Sunk)
            {
                _scanRefining = true;
                _scanRefineIndex = 0;
                _scanSinkMin = testP;
                _scanSinkMax = testP;
                _scanSinkPath = result.Path;
                return false;
            }

            if (!result.HitHazard
                && result.FinalDistanceToHole < _scanBestDist
                && testP >= maxPower * 0.12f)
            {
                _scanBestDist = result.FinalDistanceToHole;
                _scanBestPath = result.Path;
                _scanBestP = testP;
            }

            return false;
        }

        [HideFromIl2Cpp]
        private void ApplyWinningPath(Vector3[] path, float force, Vector3 direction, float minDist, bool isHoleInOne)
        {
            _winningPath = path;
            _winningPower = force;
            _winningDirection = direction;
            _winningMinDist = minDist;
            _isHoleInOne = isHoleInOne;
            _hasSolution = true;
            _searchState = SearchState.Completed;

            DiagnosticsLog.Line("search", (isHoleInOne ? "HOLE-IN-ONE" : "approach")
                + "  force " + DiagnosticsLog.F(force)
                + "  dir " + DiagnosticsLog.V(direction)
                + "  minDist " + DiagnosticsLog.F(minDist)
                + "  pathPoints " + (path != null ? path.Length : 0)
                + "  engine " + (_simUseShadow ? "shadow" : "legacy")
                + "  trajectories " + _shadowSim.RunCount
                + "  avg " + DiagnosticsLog.F(_shadowSim.AverageRunMs) + " ms");

            UpdateSolutionVisuals();
        }

        [HideFromIl2Cpp]
        private void CompleteSearchWithBestPath()
        {
            _searchState = SearchState.Completed;
            if (_winningPath != null && _winningPath.Length > 0)
            {
                _hasSolution = true;
                _isHoleInOne = false;
                DiagnosticsLog.Line("search", "no hole-in-one found; best approach leaves "
                    + DiagnosticsLog.F(_winningMinDist) + " m"
                    + "  force " + DiagnosticsLog.F(_winningPower)
                    + "  candidates " + _promisingCandidates.Count
                    + "  angles " + _candidateAngles.Count
                    + "  trajectories " + _shadowSim.RunCount
                    + "  avg " + DiagnosticsLog.F(_shadowSim.AverageRunMs) + " ms"
                    + "  engine " + (_simUseShadow ? "shadow" : "legacy"));
                UpdateSolutionVisuals();
            }
            else
            {
                _hasSolution = false;
                _isHoleInOne = false;
                DiagnosticsLog.Line("search", "no usable path at all (every candidate lost or stalled)");
                if (_solutionLineRenderer != null) _solutionLineRenderer.positionCount = 0;
            }
        }

        [HideFromIl2Cpp]
        private void UpdateSolutionVisuals()
        {
            if (_solutionLineRenderer == null || _winningPath == null) return;

            _solutionLineRenderer.positionCount = _winningPath.Length;
            for (int i = 0; i < _winningPath.Length; i++)
            {
                _solutionLineRenderer.SetPosition(i, _winningPath[i]);
            }

            if (_isHoleInOne)
            {
                _solutionLineRenderer.startColor = new Color(0f, 1f, 0.4f, 0.95f);
                _solutionLineRenderer.endColor = new Color(0f, 0.8f, 1f, 0.95f);
                _solutionLineRenderer.startWidth = 0.055f;
                _solutionLineRenderer.endWidth = 0.055f;
            }
            else
            {
                _solutionLineRenderer.startColor = new Color(1f, 0.55f, 0f, 0.9f);
                _solutionLineRenderer.endColor = new Color(1f, 0.9f, 0f, 0.9f);
                _solutionLineRenderer.startWidth = 0.045f;
                _solutionLineRenderer.endWidth = 0.045f;
            }
        }

        [HideFromIl2Cpp]
        private Rect GetPowerBarScreenRect()
        {
            if (_powerBarRect != null && _powerBarRect.gameObject.activeInHierarchy)
            {
                Vector3[] corners = new Vector3[4];
                _powerBarRect.GetWorldCorners(corners);
                float x = corners[0].x;
                float y = Screen.height - corners[1].y;
                float w = corners[2].x - corners[0].x;
                float h = corners[1].y - corners[0].y;
                if (w > 30 && h > 8)
                {
                    return new Rect(x, y, w, h);
                }
            }

            float barWidth = Screen.width * 0.233f;
            float barHeight = Screen.height * 0.026f;
            float barX = Screen.width * 0.048f;
            float barY = Screen.height - (Screen.height * 0.038f) - barHeight;

            return new Rect(barX, barY, barWidth, barHeight);
        }

        [HideFromIl2Cpp]
        private float GetMaxPower()
        {
            if (_targetBall != null && _targetBall.m_maxForce != null && _targetBall.m_maxForce.Value > 100f)
            {
                return _targetBall.m_maxForce.Value;
            }
            if (_worldPowerBar != null && _worldPowerBar.m_maxForceData != null && _worldPowerBar.m_maxForceData.Value > 100f)
            {
                return _worldPowerBar.m_maxForceData.Value;
            }
            return 10500f;
        }

        [HideFromIl2Cpp]
        private float GetCurrentPullForce()
        {
            if (_worldPowerBar != null && _worldPowerBar.m_forceData != null)
            {
                return _worldPowerBar.m_forceData.Value;
            }
            if (_soFiller != null && _soFiller.m_hitForce != null)
            {
                return _soFiller.m_hitForce.Value;
            }
            return 0f;
        }

        /// <summary>Status line describing which engine is running and how well it matches.</summary>
        [HideFromIl2Cpp]
        private string BuildEngineStatus()
        {
            if (!Plugin.UseShadowPhysics.Value)
                return "Engine: Naeherung (Schatten-Physik in der Config aus)";

            if (_shadow == null)
                return "Engine: Naeherung";

            switch (_shadow.BuildState)
            {
                case ShadowPhysicsWorld.State.Unsupported:
                    return "Engine: Naeherung - Schatten-Szene nicht verfuegbar (" + _shadow.UnsupportedReason + ")";

                case ShadowPhysicsWorld.State.Ready:
                    string s = "Engine: Schatten-PhysX  |  " + _shadow.MirroredColliders + " Collider";
                    if (_shadowSim != null && _shadowSim.RunCount > 0)
                    {
                        s += "  |  " + _shadowSim.AverageRunMs.ToString("F1") + " ms/Bahn";
                    }
                    return s;

                case ShadowPhysicsWorld.State.Empty:
                    return "Engine: Schatten-Szene wird vorbereitet...";

                default:
                    return "Engine: Schatten-Szene wird gebaut... ("
                           + (_shadow.BuildProgress * 100f).ToString("F0") + "%) - bis dahin Naeherung";
            }
        }

        void OnGUI()
        {
            if (_targetBall == null || _targetHole == null) return;

            GUI.color = Color.white;
            GUIStyle headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold
            };
            GUIStyle subStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Normal
            };
            GUIStyle infoStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Normal
            };

            float boxWidth = 620f;
            float boxHeight = 160f;
            GUI.Box(new Rect(10, 10, boxWidth, boxHeight), GUIContent.none);

            float maxPower = GetMaxPower();

            if (_searchState != SearchState.Idle && _searchState != SearchState.Completed)
            {
                GUI.color = Color.yellow;

                // The power scan runs on behalf of whichever stage started it, so its progress is
                // reported inside that stage's band rather than as a state of its own.
                SearchState shown = _searchState == SearchState.PowerScan ? _scanReturnTo : _searchState;
                float scanFraction = _scanCount > 0 ? Mathf.Clamp01((float)_scanIndex / _scanCount) : 0f;

                float progress;
                if (shown == SearchState.AngleSweep && _candidateAngles.Count > 0)
                {
                    if (_searchState == SearchState.PowerScan)
                    {
                        // The direct-line scan that runs before the sweep starts.
                        progress = 2f + scanFraction * 8f;
                    }
                    else
                    {
                        float perAngle = (_currentAngleIndex + _currentProbeIndex * 0.5f) / _candidateAngles.Count;
                        progress = 10f + perAngle * 60f;
                    }
                }
                else if (shown == SearchState.PowerRefinement && _promisingCandidates.Count > 0)
                {
                    float perCandidate = (_candidateRefineIndex + scanFraction) / _promisingCandidates.Count;
                    progress = 70f + Mathf.Clamp01(perCandidate) * 30f;
                }
                else
                {
                    progress = 2f + scanFraction * 8f;
                }

                GUI.Label(new Rect(20, 18, boxWidth - 20, 30), $"Aimbot: Suche Hole-in-One Trajektorien... ({progress:F0}%)", headerStyle);
                GUI.color = Color.white;
                string detail = _shadowSim.RunCount > 0
                    ? $"Bahnen: {_shadowSim.RunCount} | {_shadowSim.AverageRunMs:F0} ms/Bahn | Winkel {_currentAngleIndex}/{_candidateAngles.Count} | Kandidaten {_promisingCandidates.Count}"
                    : "Scanne Bandenreflexionen, Fairway-Kurven und Stärken...";
                GUI.Label(new Rect(20, 50, boxWidth - 20, 25), detail, subStyle);
            }
            else if (_hasSolution && _isHoleInOne)
            {
                // A hole-in-one is only as trustworthy as the last measured deviation. Claiming
                // certainty while the prediction is known to drift metres is what made the mod
                // promise shots it could not deliver.
                bool trustworthy = !_recorder.HasResult || _recorder.MaxDeviation < 0.25f;

                GUI.color = trustworthy ? new Color(0.1f, 1f, 0.4f) : new Color(1f, 0.75f, 0.1f);
                GUI.Label(new Rect(20, 18, boxWidth - 20, 30),
                    trustworthy
                        ? "★ HOLE-IN-ONE GEFUNDEN! ★"
                        : $"Hole-in-One (UNSICHER: letzte Abweichung {_recorder.MaxDeviation:F2} m)",
                    headerStyle);

                float ratio = Mathf.Clamp01(_winningPower / maxPower);
                GUI.color = Color.white;
                GUI.Label(new Rect(20, 48, boxWidth - 20, 25), $"Benötigte Power: {_winningPower:F0} ({ratio * 100f:F1}%) | Halte [{Plugin.AutoAimKey.Value}] für Auto-Aim", subStyle);
                GUI.Label(new Rect(20, 72, boxWidth - 20, 25), $"Loch-Distanz: {Vector3.Distance(_targetBall.transform.position, _targetHole.HolePosition.position):F1}m", subStyle);

                DrawSimulatedPowerBar(maxPower);
            }
            else if (_hasSolution && !_isHoleInOne)
            {
                GUI.color = new Color(1f, 0.6f, 0.1f);
                GUI.Label(new Rect(20, 18, boxWidth - 20, 30), $"Bester Annäherungsschlag (Rest: {_winningMinDist:F2}m)", headerStyle);

                float ratio = Mathf.Clamp01(_winningPower / maxPower);
                GUI.color = Color.white;
                GUI.Label(new Rect(20, 48, boxWidth - 20, 25), $"Empfohlene Power: {_winningPower:F0} ({ratio * 100f:F1}%) | Halte [{Plugin.AutoAimKey.Value}] für Auto-Aim", subStyle);
                GUI.Label(new Rect(20, 72, boxWidth - 20, 25), "Kein direkter 1-Hit-Pfad gefunden.", subStyle);

                DrawSimulatedPowerBar(maxPower);
            }
            else
            {
                GUI.color = Color.gray;
                GUI.Label(new Rect(20, 18, boxWidth - 20, 30), "Aimbot: Kein Pfad ermittelbar", headerStyle);
                float dist = Vector3.Distance(_targetBall.transform.position, _targetHole.HolePosition.position);
                GUI.Label(new Rect(20, 48, boxWidth - 20, 25), $"Direkte Entfernung: {dist:F1}m", subStyle);
            }

            // ---- engine / calibration / measured accuracy ----
            GUI.color = new Color(0.75f, 0.85f, 1f);
            GUI.Label(new Rect(20, 100, boxWidth - 20, 20), BuildEngineStatus(), infoStyle);

            GUI.color = ShotCalibration.IsMeasured ? new Color(0.7f, 1f, 0.7f) : new Color(1f, 0.85f, 0.5f);
            float vMax = ShotCalibration.MaxLaunchSpeed(_targetBall, maxPower);
            string calib = $"Abschuss: {vMax:F1} m/s bei 100% (Korrektur {ShotCalibration.Correction:F3} aus {ShotCalibration.Samples} Schlaegen)";
            if (!ShotCalibration.CurveIsUsable) calib += " | PowerCurve flach -> ignoriert";
            if (MeasuredDragSchedule.IsMeasured)
            {
                calib += $" | Drag {MeasuredDragSchedule.DragAfterHit:F2}->{MeasuredDragSchedule.DragToSlow:F2} gemessen";
            }
            GUI.Label(new Rect(20, 120, boxWidth - 20, 20), calib, infoStyle);

            if (_recorder != null && _recorder.HasResult)
            {
                float dev = _recorder.MaxDeviation;
                GUI.color = dev < 0.05f ? Color.green : (dev < 0.30f ? Color.yellow : new Color(1f, 0.5f, 0.4f));
                GUI.Label(new Rect(20, 140, boxWidth - 20, 20),
                    $"Letzter Schlag vs. Vorhersage: max {_recorder.MaxDeviation:F3} m | Ø {_recorder.MeanDeviation:F3} m | Ende {_recorder.FinalDeviation:F3} m ({_recorder.ComparedSteps} Schritte)",
                    infoStyle);
            }
            else if (_recorder != null && _recorder.IsRecording)
            {
                GUI.color = Color.cyan;
                GUI.Label(new Rect(20, 140, boxWidth - 20, 20), "Schlag wird mitgeschnitten...", infoStyle);
            }

            GUI.color = Color.white;
        }

        [HideFromIl2Cpp]
        private void DrawColorBox(Rect rect, Color color)
        {
            Color oldBg = GUI.backgroundColor;
            Color oldColor = GUI.color;
            GUI.backgroundColor = color;
            GUI.color = color;
            GUI.Box(rect, GUIContent.none);
            GUI.backgroundColor = oldBg;
            GUI.color = oldColor;
        }

        [HideFromIl2Cpp]
        private void DrawSimulatedPowerBar(float maxPower)
        {
            Rect barRect = GetPowerBarScreenRect();
            float targetRatio = Mathf.Clamp01(_winningPower / maxPower);
            float targetMarkerX = barRect.x + (barRect.width * targetRatio);

            float currentForce = GetCurrentPullForce();
            float currentRatio = Mathf.Clamp01(currentForce / maxPower);

            Color ghostColor = _isHoleInOne ? new Color(0f, 1f, 0.6f, 0.7f) : new Color(1f, 0.6f, 0f, 0.7f);
            DrawColorBox(new Rect(barRect.x, barRect.y, barRect.width * targetRatio, barRect.height), ghostColor);

            DrawColorBox(new Rect(targetMarkerX - 3, barRect.y - 8, 6, barRect.height + 16), Color.black);

            Color needleColor = _isHoleInOne ? Color.magenta : new Color(1f, 0.4f, 0f);
            DrawColorBox(new Rect(targetMarkerX - 2, barRect.y - 7, 4, barRect.height + 14), needleColor);

            GUIStyle arrowStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            GUI.color = needleColor;
            GUI.Label(new Rect(targetMarkerX - 15, barRect.y - 28, 30, 24), "▼", arrowStyle);

            GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter
            };
            GUI.color = Color.white;
            GUI.Label(new Rect(targetMarkerX - 60, barRect.y + barRect.height + 3, 120, 24), $"{_winningPower:F0} ({targetRatio * 100f:F0}%)", labelStyle);

            if (currentForce > (maxPower * 0.01f))
            {
                float diff = currentForce - _winningPower;
                float tolerance = maxPower * 0.018f;
                bool isPerfect = Mathf.Abs(diff) <= tolerance;

                GUIStyle statusStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 17,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };

                if (isPerfect)
                {
                    DrawColorBox(new Rect(barRect.x - 4, barRect.y - 4, barRect.width + 8, barRect.height + 8), new Color(0f, 1f, 0.2f, 0.8f));

                    GUI.color = Color.green;
                    GUI.Label(new Rect(barRect.x, barRect.y - 48, barRect.width, 24), "★ PERFEKTE STÄRKE! LOSLASSEN! ★", statusStyle);
                }
                else if (diff < -tolerance)
                {
                    GUI.color = Color.yellow;
                    GUI.Label(new Rect(barRect.x, barRect.y - 48, barRect.width, 24), $"Noch ziehen: +{Mathf.Abs(diff):F0} ({currentForce:F0} / {_winningPower:F0})", statusStyle);
                }
                else
                {
                    GUI.color = Color.red;
                    GUI.Label(new Rect(barRect.x, barRect.y - 48, barRect.width, 24), $"ZU STARK! -{diff:F0} ({currentForce:F0} / {_winningPower:F0})", statusStyle);
                }
            }
        }
    }
}
