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
            DirectEvaluation,       // Pass 1: Dense power sweep across direct line and near angles
            AngleSweep,             // Pass 2: Wide multi-power angular sweep
            PowerScan,              // Resumable dense power sweep for one direction
            CandidateRefinement,    // Pass 3: Micro-angle and targeted power search around promising flyby candidates
            PowerRefinement,        // Pass 4: Sinking band centering (find [min, max] powers and pick center)
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
        private bool _isAutoAiming = false;
        private bool _isCachedSolution = false;
        private bool _isLiveVerifiedSolution = false;

        private Vector3[] _winningPath;
        private float _winningPower; // In actual Game Force units (0 to maxPower)
        private Vector3 _winningDirection;
        private float _winningMinDist;

        // Pass 1: Direct & Near Angles
        private static readonly float[] DirectAngles = { 0f, 1.25f, -1.25f, 2.5f, -2.5f, 4.0f, -4.0f, 6.0f, -6.0f, 9.0f, -9.0f, 13.0f, -13.0f };
        private int _directAngleIndex = 0;

        // Pass 2: Angle sweep parameters
        private List<float> _candidateAngles = new List<float>();
        private int _currentAngleIndex = 0;

        // Multi-power probing fractions during wide angle sweep (7 levels covering short putts to max power)
        private static readonly float[] ProbePowerFractions = { 0.15f, 0.30f, 0.45f, 0.60f, 0.75f, 0.90f, 0.98f };
        private int _currentProbeIndex = 0;

        // Candidate angles that passed near the hole during probe shots
        private struct CandidateAngle
        {
            public float AngleOffset;
            public float ClosestFlybyDist;
            public float ProbePower;
        }
        private readonly List<CandidateAngle> _promisingCandidates = new List<CandidateAngle>(32);
        private int _candidateRefineIndex = 0;
        private int _candidateRefineStep = 0;
        private static readonly float[] RefineAngleOffsets = { 0f, -0.75f, 0.75f, -1.5f, 1.5f, -2.25f, 2.25f };

        // ---- resumable power scan ----
        private Vector3 _scanDir;
        private int _scanIndex;
        private int _scanCount;
        private float _scanLow;
        private float _scanStep;
        private float _scanBestDist;
        private Vector3[] _scanBestPath;
        private float _scanBestP;
        private SearchState _scanReturnTo;

        // Pass 4: Sweet-spot centering for power bar tolerance
        private int _scanRefineIndex;
        private float _scanSinkMin;
        private float _scanSinkMax;
        private Vector3[] _scanSinkPath;
        private static readonly float[] ScanRefineOffsets = { -0.5f, -0.25f, 0.25f, 0.5f, -0.75f, 0.75f, -1.0f, 1.0f };

        private Vector3 _lastSearchBallPos = Vector3.negativeInfinity;
        private Vector3 _dirToHole = Vector3.forward;

        // ---- internal 1:1 simulation ----
        private ShadowPhysicsWorld _shadow;
        private ShadowTrajectorySimulator _shadowSim;
        private ShotTraceRecorder _recorder;

        // Simulation context
        private bool _simContextReady;
        private bool _simUseShadow;
        private BallTuning _simTuning;
        private float _simBallRadius;
        private float _simDrag;
        private float _simAngDrag;
        private PhysicMaterial _simBallMat;

        // Last non-zero pull force seen
        private float _lastPullForce;

        private bool _loggedEnvironment;
        private BallMovement _loggedBall;
        private int _loggedHole = int.MinValue;
        private int _loggedCupHole = int.MinValue;

        // Live-aim throttle
        private float _lastLiveAimTime = -1f;
        private float _lastLiveAimForce = -1f;
        private Vector3 _lastLiveAimDir = Vector3.zero;

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
            _candidateAngles.Add(0f);

            float step = Mathf.Max(0.5f, Plugin.AngleStepDegrees.Value);
            float span = Mathf.Clamp(Plugin.AngleSpanDegrees.Value, step, 180f);

            for (float a = step; a <= span; a += step)
            {
                _candidateAngles.Add(a);
                _candidateAngles.Add(-a);
            }
        }

        void Update()
        {
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

            int holeNow = (int)_targetBall.HoleNumber;
            if (_loggedBall != _targetBall || _loggedHole != holeNow)
            {
                _loggedBall = _targetBall;
                _loggedHole = holeNow;
                DiagnosticsLog.Section("hole " + holeNow + " in scene '" + _targetBall.gameObject.scene.name + "'");
                DiagnosticsLog.WriteBall(_targetBall);
            }

            if (Plugin.UseShadowPhysics.Value)
            {
                _shadow.EnsureBuilt(_targetBall, (int)_targetBall.HoleNumber, Plugin.BuildBudgetMs.Value);
            }

            bool shadowReady = Plugin.UseShadowPhysics.Value && _shadow != null && _shadow.IsReady;
            if (!_simContextReady || shadowReady != _simUseShadow)
            {
                PrepareSimulationContext();
                _searchState = SearchState.Idle;
                _hasSolution = false;
                _lastSearchBallPos = Vector3.negativeInfinity;

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

            // Auto-aim assist & Perfect execution (hold [F] to lock aim & power, release to fire!)
            if (_hasSolution && _winningDirection.sqrMagnitude > 0.001f)
            {
                if (Input.GetKey(Plugin.AutoAimKey.Value))
                {
                    _isAutoAiming = true;

                    Vector3 lookDir = new Vector3(_winningDirection.x, 0f, _winningDirection.z).normalized;
                    if (lookDir.sqrMagnitude > 0.001f)
                    {
                        float targetYaw = Mathf.Atan2(lookDir.x, lookDir.z) * Mathf.Rad2Deg;

                        var mouseAim = FindObjectOfType<MouseAim>();
                        float currentPitch = 20f;
                        if (mouseAim != null && mouseAim.m_trans != null)
                        {
                            currentPitch = mouseAim.m_trans.eulerAngles.x;
                        }
                        else if (Camera.main != null)
                        {
                            currentPitch = Camera.main.transform.eulerAngles.x;
                        }

                        Quaternion targetRot = Quaternion.Euler(currentPitch, targetYaw, 0f);

                        // 1. Properly orient MouseAim (GWYF's camera controller)
                        if (mouseAim != null)
                        {
                            if (mouseAim.m_trans != null)
                            {
                                if (Plugin.AutoAimSnap.Value)
                                {
                                    mouseAim.m_trans.rotation = targetRot;
                                }
                                else
                                {
                                    mouseAim.m_trans.rotation = Quaternion.Slerp(mouseAim.m_trans.rotation, targetRot, Time.deltaTime * 35f);
                                }
                            }
                            mouseAim.ResetRotation(new Vector3(currentPitch, targetYaw, 0f), 0f);
                        }

                        // 2. Orient Camera.main
                        if (Camera.main != null)
                        {
                            if (Plugin.AutoAimSnap.Value)
                            {
                                Camera.main.transform.rotation = targetRot;
                            }
                            else
                            {
                                Camera.main.transform.rotation = Quaternion.Slerp(Camera.main.transform.rotation, targetRot, Time.deltaTime * 35f);
                            }
                        }

                        // 3. Set the exact required winning power into the power bar data & activate HUD
                        if (_worldPowerBar != null)
                        {
                            if (_worldPowerBar.m_forceData != null)
                            {
                                _worldPowerBar.m_forceData.SetValue(_winningPower);
                            }
                            _worldPowerBar.m_shootPressed = true;
                            _worldPowerBar.m_playerTurn = true;
                        }
                        if (_soFiller != null && _soFiller.m_hitForce != null)
                        {
                            _soFiller.m_hitForce.SetValue(_winningPower);
                        }
                    }
                }

                // When AutoAim key is RELEASED, execute the perfect shot!
                if (Input.GetKeyUp(Plugin.AutoAimKey.Value) && _isAutoAiming)
                {
                    _isAutoAiming = false;
                    ExecuteShot();
                }
            }
            else
            {
                _isAutoAiming = false;
            }
        }

        [HideFromIl2Cpp]
        private void ExecuteShot()
        {
            if (_targetBall == null || !_hasSolution || _winningDirection.sqrMagnitude < 0.001f) return;

            var rb = _targetBall.m_rigidBody != null ? _targetBall.m_rigidBody : _targetBall.GetComponent<Rigidbody>();
            if (rb == null) return;

            Vector3 lookDir = new Vector3(_winningDirection.x, 0f, _winningDirection.z).normalized;
            float maxPower = GetMaxPower();
            float force = _winningPower;

            // 1. Force data synchronization
            if (_worldPowerBar != null && _worldPowerBar.m_forceData != null)
            {
                _worldPowerBar.m_forceData.SetValue(force);
            }
            if (_soFiller != null && _soFiller.m_hitForce != null)
            {
                _soFiller.m_hitForce.SetValue(force);
            }

            // 2. Exact camera & MouseAim lock
            float targetYaw = Mathf.Atan2(lookDir.x, lookDir.z) * Mathf.Rad2Deg;
            var mouseAim = FindObjectOfType<MouseAim>();
            float currentPitch = 20f;
            if (mouseAim != null && mouseAim.m_trans != null)
            {
                currentPitch = mouseAim.m_trans.eulerAngles.x;
                mouseAim.m_trans.rotation = Quaternion.Euler(currentPitch, targetYaw, 0f);
                mouseAim.ResetRotation(new Vector3(currentPitch, targetYaw, 0f), 0f);
            }
            if (Camera.main != null)
            {
                Camera.main.transform.rotation = Quaternion.Euler(currentPitch, targetYaw, 0f);
            }

            // 3. Compute launch velocity matching the 1:1 physics simulation
            float speed = CalculateBallSpeed(force, maxPower);

            // 4. Launch ball
            rb.isKinematic = false;
            rb.velocity = lookDir * speed;
            rb.angularVelocity = Vector3.zero;
            rb.drag = _targetBall.dragToHitBall > 0 ? _targetBall.dragToHitBall : 0.35f;
            rb.angularDrag = _targetBall.angDragToHitBall > 0 ? _targetBall.angDragToHitBall : 0.05f;

            // 5. Fire game shot lifecycle methods
            _targetBall.HasTakenShot = true;
            _targetBall.OnShotStarted();
            _targetBall.ApplyOnShotStarted();
            if (_targetBall.m_CallOnShotTaken != null)
            {
                _targetBall.m_CallOnShotTaken.Invoke();
            }

            if (_worldPowerBar != null)
            {
                _worldPowerBar.m_shootPressed = false;
                _worldPowerBar.OnTakenShot();
            }

            _lastPullForce = force;

            DiagnosticsLog.Line("autoaim", "SHOT EXECUTED: force " + DiagnosticsLog.F(force)
                + " (" + (force / maxPower * 100f).ToString("F1") + "%)"
                + "  dir " + DiagnosticsLog.V(lookDir)
                + "  speed " + DiagnosticsLog.F(speed) + " m/s");
            DiagnosticsLog.Flush();
        }

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
            if (!Plugin.TraceEnabled.Value || _targetBall == null) return;
            _recorder.Sample(_targetBall, _lastPullForce, GetMaxPower());
        }

        [HideFromIl2Cpp]
        private void StartNewSearch()
        {
            _searchState = SearchState.DirectEvaluation;
            _hasSolution = false;
            _isHoleInOne = false;
            _isCachedSolution = false;
            _isLiveVerifiedSolution = false;
            _directAngleIndex = 0;
            _currentAngleIndex = 0;
            _currentProbeIndex = 0;
            _candidateRefineIndex = 0;
            _candidateRefineStep = 0;
            _scanIndex = 0;
            _scanCount = 0;
            _scanBestPath = null;
            _scanSinkPath = null;
            _promisingCandidates.Clear();
            _lastSearchBallPos = _targetBall.transform.position;

            _winningMinDist = float.MaxValue;
            _winningPath = null;

            // Fast-path: Check persistent cache for a known Hole-in-One on this hole & ball position
            if (_targetBall != null && _targetHole != null)
            {
                string sceneName = _targetBall.gameObject.scene.name;
                int holeNumber = (int)_targetBall.HoleNumber;
                Vector3 ballPos = _targetBall.transform.position;

                if (ShotSolutionCache.TryGetSolution(sceneName, holeNumber, ballPos, out var cachedSol))
                {
                    if (cachedSol.IsHoleInOne && cachedSol.IsValid)
                    {
                        _winningDirection = cachedSol.Direction.ToVector3().normalized;
                        _winningPower = cachedSol.Power;
                        _winningMinDist = cachedSol.MinDistance;
                        _isHoleInOne = true;
                        _isCachedSolution = true;
                        _isLiveVerifiedSolution = cachedSol.IsLiveVerified;
                        _winningPath = cachedSol.GetPathArray();
                        _hasSolution = true;
                        _searchState = SearchState.Completed;

                        Plugin.Logger.LogInfo($"ShotSolutionCache: Found cached HIO for {sceneName} #{holeNumber} (Verified: {_isLiveVerifiedSolution}, Power: {_winningPower:F0})");
                        DiagnosticsLog.Line("cache", $"LOADED HIO FROM CACHE: hole {holeNumber} power {_winningPower:F0} live {_isLiveVerifiedSolution}");
                        UpdateSolutionVisuals();
                        return;
                    }
                }
            }

            _shadowSim.ResetStats();
            PrepareSimulationContext();
        }

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
                Vector3 ballPos = _targetBall.transform.position;
                Vector3 pathStart = _winningPath[0];
                float horizDist = Vector2.Distance(new Vector2(ballPos.x, ballPos.z), new Vector2(pathStart.x, pathStart.z));
                if (horizDist > 0.30f)
                {
                    _hasSolution = false;
                }
            }
        }

        [HideFromIl2Cpp]
        private float CalculateBallSpeed(float force, float maxPower)
        {
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
                while (true)
                {
                    if ((Time.realtimeSinceStartup - startTime) * 1000f >= maxMs) return;

                    // STAGE 1: Direct line & near angles
                    if (_searchState == SearchState.DirectEvaluation)
                    {
                        if (_directAngleIndex >= DirectAngles.Length)
                        {
                            _searchState = SearchState.AngleSweep;
                            _currentAngleIndex = 0;
                            _currentProbeIndex = 0;
                            continue;
                        }

                        float angle = DirectAngles[_directAngleIndex];
                        _directAngleIndex++;
                        Vector3 testDir = Quaternion.Euler(0, angle, 0) * _dirToHole;
                        BeginPowerScan(testDir, maxPower, SearchState.DirectEvaluation);
                        continue;
                    }

                    // STAGE 2: Wide Angle Sweep with multi-power probing
                    if (_searchState == SearchState.AngleSweep)
                    {
                        if (_currentAngleIndex >= _candidateAngles.Count)
                        {
                            if (_promisingCandidates.Count > 0)
                            {
                                _promisingCandidates.Sort((a, b) => a.ClosestFlybyDist.CompareTo(b.ClosestFlybyDist));
                                _searchState = SearchState.CandidateRefinement;
                                _candidateRefineIndex = 0;
                                _candidateRefineStep = 0;
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

                    // STAGE 3: Resumable power sweep
                    if (_searchState == SearchState.PowerScan)
                    {
                        if (StepPowerScan(ballPos, holePos, maxPower)) return;
                        continue;
                    }

                    // STAGE 4: Candidate Refinement (bounce bank candidates)
                    if (_searchState == SearchState.CandidateRefinement)
                    {
                        if (_candidateRefineIndex >= _promisingCandidates.Count || _candidateRefineIndex >= 12)
                        {
                            CompleteSearchWithBestPath();
                            return;
                        }

                        CandidateAngle candidate = _promisingCandidates[_candidateRefineIndex];
                        if (_candidateRefineStep >= RefineAngleOffsets.Length)
                        {
                            _candidateRefineIndex++;
                            _candidateRefineStep = 0;
                            continue;
                        }

                        float subAngle = candidate.AngleOffset + RefineAngleOffsets[_candidateRefineStep];
                        _candidateRefineStep++;
                        Vector3 candidateDir = Quaternion.Euler(0, subAngle, 0) * _dirToHole;

                        // Targeted power scan around the candidate probe power
                        float lowP = Mathf.Max(maxPower * 0.06f, candidate.ProbePower - maxPower * 0.22f);
                        float highP = Mathf.Min(maxPower * 0.98f, candidate.ProbePower + maxPower * 0.22f);
                        BeginPowerScanCustomRange(candidateDir, maxPower, lowP, highP, 14, SearchState.CandidateRefinement);
                        continue;
                    }

                    // STAGE 5: Power Refinement
                    if (_searchState == SearchState.PowerRefinement)
                    {
                        if (StepPowerRefinement(ballPos, holePos, maxPower)) return;
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

        [HideFromIl2Cpp]
        private bool StepAngleSweep(Vector3 ballPos, Vector3 holePos, float maxPower)
        {
            float angle = _candidateAngles[_currentAngleIndex];
            Vector3 testDir = Quaternion.Euler(0, angle, 0) * _dirToHole;

            float probeFrac = ProbePowerFractions[_currentProbeIndex];
            float probeP = maxPower * probeFrac;

            string sceneName = _targetBall.gameObject.scene.name;
            int holeNumber = (int)_targetBall.HoleNumber;

            if (ShotSolutionCache.IsBlacklisted(sceneName, holeNumber, ballPos, testDir, probeP))
            {
                // Skip blacklisted probe shot
                _currentProbeIndex++;
                if (_currentProbeIndex >= ProbePowerFractions.Length)
                {
                    _currentProbeIndex = 0;
                    _currentAngleIndex++;
                }
                return false;
            }

            float speed = CalculateBallSpeed(probeP, maxPower);
            var probeResult = RunSimulation(ballPos, testDir * speed, holePos, Plugin.ProbeSimSeconds.Value, 1);

            if (probeResult.Sunk)
            {
                BeginPowerRefinement(testDir, probeP, maxPower, probeResult.Path);
                return true;
            }

            if (!probeResult.HitHazard)
            {
                if (probeResult.MinDistanceToHole < 3.5f)
                {
                    _promisingCandidates.Add(new CandidateAngle
                    {
                        AngleOffset = angle,
                        ClosestFlybyDist = probeResult.MinDistanceToHole,
                        ProbePower = probeP
                    });
                }

                if (probeResult.FinalDistanceToHole < _winningMinDist && probeP >= maxPower * 0.10f)
                {
                    _winningMinDist = probeResult.FinalDistanceToHole;
                    _winningPath = probeResult.Path;
                    _winningPower = probeP;
                    _winningDirection = testDir;
                    UpdateSolutionVisuals();
                }
            }

            _currentProbeIndex++;
            if (_currentProbeIndex >= ProbePowerFractions.Length)
            {
                _currentProbeIndex = 0;
                _currentAngleIndex++;
            }
            return false;
        }

        [HideFromIl2Cpp]
        private void BeginPowerScan(Vector3 dir, float maxPower, SearchState returnTo)
        {
            float lowP = maxPower * 0.06f;
            float highP = maxPower * 0.98f;
            int subdivisions = Mathf.Max(6, Plugin.PowerSubdivisions.Value);
            BeginPowerScanCustomRange(dir, maxPower, lowP, highP, subdivisions, returnTo);
        }

        [HideFromIl2Cpp]
        private void BeginPowerScanCustomRange(Vector3 dir, float maxPower, float lowP, float highP, int subdivisions, SearchState returnTo)
        {
            _scanDir = dir;
            _scanLow = lowP;
            _scanStep = (highP - lowP) / subdivisions;
            _scanCount = subdivisions + 1;
            _scanIndex = 0;
            _scanBestDist = float.MaxValue;
            _scanBestPath = null;
            _scanBestP = 0f;
            _scanReturnTo = returnTo;
            _searchState = SearchState.PowerScan;
        }

        [HideFromIl2Cpp]
        private bool StepPowerScan(Vector3 ballPos, Vector3 holePos, float maxPower)
        {
            float simSeconds = Plugin.MaxSimSeconds.Value;

            if (_scanIndex >= _scanCount)
            {
                if (_scanBestDist < _winningMinDist && _scanBestPath != null)
                {
                    _winningMinDist = _scanBestDist;
                    _winningPath = _scanBestPath;
                    _winningPower = _scanBestP;
                    _winningDirection = _scanDir;
                    UpdateSolutionVisuals();
                }

                _searchState = _scanReturnTo;
                return false;
            }

            float testP = _scanLow + _scanIndex * _scanStep;
            _scanIndex++;

            string sceneName = _targetBall.gameObject.scene.name;
            int holeNumber = (int)_targetBall.HoleNumber;

            if (ShotSolutionCache.IsBlacklisted(sceneName, holeNumber, ballPos, _scanDir, testP))
            {
                return false;
            }

            var result = RunSimulation(ballPos, _scanDir * CalculateBallSpeed(testP, maxPower), holePos, simSeconds, 1);

            if (result.Sunk)
            {
                BeginPowerRefinement(_scanDir, testP, maxPower, result.Path);
                return true;
            }

            if (!result.HitHazard
                && result.FinalDistanceToHole < _scanBestDist
                && testP >= maxPower * 0.10f)
            {
                _scanBestDist = result.FinalDistanceToHole;
                _scanBestPath = result.Path;
                _scanBestP = testP;
            }

            return false;
        }

        [HideFromIl2Cpp]
        private void BeginPowerRefinement(Vector3 dir, float sinkPower, float maxPower, Vector3[] initialPath)
        {
            _scanDir = dir;
            _scanSinkMin = sinkPower;
            _scanSinkMax = sinkPower;
            _scanSinkPath = initialPath;
            _scanRefineIndex = 0;
            _searchState = SearchState.PowerRefinement;

            // Apply preliminary solution immediately so user sees it right away
            ApplyWinningPath(initialPath, sinkPower, dir, 0f, true);
        }

        [HideFromIl2Cpp]
        private bool StepPowerRefinement(Vector3 ballPos, Vector3 holePos, float maxPower)
        {
            float simSeconds = Plugin.MaxSimSeconds.Value;
            float stepSize = maxPower * 0.025f;

            if (_scanRefineIndex < ScanRefineOffsets.Length)
            {
                float fp = Mathf.Clamp(
                    _scanSinkMin + ScanRefineOffsets[_scanRefineIndex] * stepSize,
                    maxPower * 0.05f,
                    maxPower * 0.99f);
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

            if (_targetBall != null)
            {
                string sceneName = _targetBall.gameObject.scene.name;
                int holeNumber = (int)_targetBall.HoleNumber;
                Vector3 ballPos = _targetBall.transform.position;
                ShotSolutionCache.RecordSolution(sceneName, holeNumber, ballPos, direction, force, path, minDist, isHoleInOne, isLiveVerified: false);
            }

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

                float progress = 0f;
                if (_searchState == SearchState.DirectEvaluation)
                {
                    float frac = DirectAngles.Length > 0 ? (float)_directAngleIndex / DirectAngles.Length : 0f;
                    progress = frac * 20f;
                }
                else if (_searchState == SearchState.AngleSweep)
                {
                    float frac = _candidateAngles.Count > 0 ? ((float)_currentAngleIndex + (float)_currentProbeIndex / ProbePowerFractions.Length) / _candidateAngles.Count : 0f;
                    progress = 20f + frac * 50f;
                }
                else if (_searchState == SearchState.CandidateRefinement)
                {
                    float totalCand = Mathf.Min(12, Mathf.Max(1, _promisingCandidates.Count));
                    float frac = ((float)_candidateRefineIndex + (float)_candidateRefineStep / RefineAngleOffsets.Length) / totalCand;
                    progress = 70f + Mathf.Clamp01(frac) * 25f;
                }
                else if (_searchState == SearchState.PowerRefinement)
                {
                    progress = 95f + Mathf.Clamp01((float)_scanRefineIndex / ScanRefineOffsets.Length) * 5f;
                }
                else if (_searchState == SearchState.PowerScan)
                {
                    float scanFrac = _scanCount > 0 ? (float)_scanIndex / _scanCount : 0f;
                    progress = Mathf.Clamp(scanFrac * 100f, 5f, 95f);
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
                bool trustworthy = !_recorder.HasResult || _recorder.MaxDeviation < 0.25f;

                string title;
                Color titleCol;

                if (_isAutoAiming)
                {
                    title = "★ AUTO-AIM AKTIV: LASS [F] LOS ZUM SCHLAGEN! ★";
                    titleCol = Color.cyan;
                }
                else if (_isLiveVerifiedSolution)
                {
                    title = "★ 100% VERIFIZIERTES HOLE-IN-ONE (CACHE) ★";
                    titleCol = new Color(0.1f, 1f, 0.4f);
                }
                else if (_isCachedSolution)
                {
                    title = "★ HOLE-IN-ONE GELADEN (CACHE) ★";
                    titleCol = new Color(0.2f, 0.9f, 1f);
                }
                else
                {
                    title = trustworthy ? "★ HOLE-IN-ONE GEFUNDEN! ★" : $"Hole-in-One (UNSICHER: letzte Abweichung {_recorder.MaxDeviation:F2} m)";
                    titleCol = trustworthy ? new Color(0.1f, 1f, 0.4f) : new Color(1f, 0.75f, 0.1f);
                }

                GUI.color = titleCol;
                GUI.Label(new Rect(20, 18, boxWidth - 20, 30), title, headerStyle);

                float ratio = Mathf.Clamp01(_winningPower / maxPower);
                GUI.color = Color.white;
                GUI.Label(new Rect(20, 48, boxWidth - 20, 25), $"Benötigte Power: {_winningPower:F0} ({ratio * 100f:F1}%) | Halte [{Plugin.AutoAimKey.Value}] gedrückt & lass los zum Schlagen!", subStyle);
                GUI.Label(new Rect(20, 72, boxWidth - 20, 25), $"Loch-Distanz: {Vector3.Distance(_targetBall.transform.position, _targetHole.HolePosition.position):F1}m", subStyle);

                DrawSimulatedPowerBar(maxPower);
            }
            else if (_hasSolution && !_isHoleInOne)
            {
                GUI.color = _isAutoAiming ? Color.cyan : new Color(1f, 0.6f, 0.1f);
                GUI.Label(new Rect(20, 18, boxWidth - 20, 30),
                    _isAutoAiming
                        ? "★ AUTO-AIM AKTIV: LASS [F] LOS ZUM SCHLAGEN! ★"
                        : $"Bester Annäherungsschlag (Rest: {_winningMinDist:F2}m)",
                    headerStyle);

                float ratio = Mathf.Clamp01(_winningPower / maxPower);
                GUI.color = Color.white;
                GUI.Label(new Rect(20, 48, boxWidth - 20, 25), $"Empfohlene Power: {_winningPower:F0} ({ratio * 100f:F1}%) | Halte [{Plugin.AutoAimKey.Value}] gedrückt & lass los zum Schlagen!", subStyle);
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
