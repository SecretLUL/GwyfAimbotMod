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

        private Vector3 _lastSearchBallPos = Vector3.negativeInfinity;

        // Max exit speed for GWYF physics at 100% force (10500 force)
        private const float MAX_PHYSICS_SPEED = 52.0f;

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

            InitializeSweepAngles();
        }

        private void InitializeSweepAngles()
        {
            _candidateAngles.Clear();
            _candidateAngles.Add(0f); // Direct towards hole

            // Scan outward symmetrically in 2.5 degree steps up to 180 degrees (72 angles on each side)
            for (float a = 2.5f; a <= 180f; a += 2.5f)
            {
                _candidateAngles.Add(a);
                _candidateAngles.Add(-a);
            }
        }

        void Update()
        {
            FindTargets();

            if (_targetBall == null || _targetHole == null)
            {
                ClearPaths();
                _searchState = SearchState.Idle;
                return;
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
                    ProcessSearchWithTimeBudget(2.5f); // 2.5ms per frame budget
                }

                UpdateLiveAimTrajectory();
            }

            // Auto-aim assist (hold 'F')
            if (_hasSolution && Input.GetKey(KeyCode.F) && Camera.main != null && _winningDirection.sqrMagnitude > 0.01f)
            {
                Vector3 lookDir = new Vector3(_winningDirection.x, 0, _winningDirection.z).normalized;
                if (lookDir.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(lookDir, Vector3.up);
                    Camera.main.transform.rotation = Quaternion.Slerp(Camera.main.transform.rotation, targetRot, Time.deltaTime * 12f);
                }
            }
        }

        private void StartNewSearch()
        {
            _searchState = SearchState.DirectEvaluation;
            _hasSolution = false;
            _isHoleInOne = false;
            _currentAngleIndex = 0;
            _candidateRefineIndex = 0;
            _promisingCandidates.Clear();
            _lastSearchBallPos = _targetBall.transform.position;

            _winningMinDist = float.MaxValue;
            _winningPath = null;
        }

        private void ClearPaths()
        {
            if (_solutionLineRenderer != null) _solutionLineRenderer.positionCount = 0;
            if (_liveAimLineRenderer != null) _liveAimLineRenderer.positionCount = 0;
        }

        void FindTargets()
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

        private float CalculateBallSpeed(float force, float maxPower)
        {
            float ratio = Mathf.Clamp01(force / maxPower);
            float evaluatedRatio = ratio;

            if (_targetBall != null && _targetBall.m_PowerCurve != null)
            {
                evaluatedRatio = _targetBall.m_PowerCurve.Evaluate(ratio);
            }

            return evaluatedRatio * MAX_PHYSICS_SPEED;
        }

        private void UpdateLiveAimTrajectory()
        {
            if (_liveAimLineRenderer == null || _targetBall == null || _targetHole == null) return;

            float currentForce = GetCurrentPullForce();
            float maxPower = GetMaxPower();

            if (currentForce > (maxPower * 0.005f) && Camera.main != null)
            {
                Vector3 ballPos = _targetBall.transform.position;
                Vector3 holePos = _targetHole.HolePosition.position;
                float ballRadius = _targetBall.BallRadius > 0 ? _targetBall.BallRadius : 0.18f;
                float drag = _targetBall.dragToHitBall > 0 ? _targetBall.dragToHitBall : 0.35f;
                float angDrag = _targetBall.angDragToHitBall > 0 ? _targetBall.angDragToHitBall : 0.05f;
                var col = _targetBall.GetComponent<Collider>();
                PhysicMaterial ballMat = col != null ? col.sharedMaterial : null;

                Vector3 aimDir = Camera.main.transform.forward;
                aimDir.y = 0f;
                aimDir.Normalize();
                if (aimDir.sqrMagnitude < 0.001f) aimDir = Vector3.forward;

                float speed = CalculateBallSpeed(currentForce, maxPower);
                Vector3 initVelocity = aimDir * speed;

                bool oldEnabled = true;
                if (col != null)
                {
                    oldEnabled = col.enabled;
                    col.enabled = false;
                }

                try
                {
                    var result = TrajectorySimulator.SimulateShotDetailed(
                        ballPos, initVelocity, holePos, ballRadius, drag, angDrag, ballMat, 450, 2);

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

        void ProcessSearchWithTimeBudget(float maxMs)
        {
            if (_targetBall == null || _targetHole == null) return;

            Vector3 ballPos = _targetBall.transform.position;
            Vector3 holePos = _targetHole.HolePosition.position;
            float ballRadius = _targetBall.BallRadius > 0 ? _targetBall.BallRadius : 0.18f;
            float drag = _targetBall.dragToHitBall > 0 ? _targetBall.dragToHitBall : 0.35f;
            float angDrag = _targetBall.angDragToHitBall > 0 ? _targetBall.angDragToHitBall : 0.05f;
            float maxPower = GetMaxPower();

            Vector3 dirToHole = (holePos - ballPos);
            dirToHole.y = 0;
            dirToHole.Normalize();
            if (dirToHole.sqrMagnitude < 0.001f) dirToHole = Vector3.forward;

            var ballCol = _targetBall.GetComponent<Collider>();
            PhysicMaterial ballMat = ballCol != null ? ballCol.sharedMaterial : null;
            bool oldEnabled = true;
            if (ballCol != null)
            {
                oldEnabled = ballCol.enabled;
                ballCol.enabled = false;
            }

            float startTime = Time.realtimeSinceStartup;

            try
            {
                while ((Time.realtimeSinceStartup - startTime) * 1000f < maxMs)
                {
                    // STAGE 1: Direct Line-of-Sight Check
                    if (_searchState == SearchState.DirectEvaluation)
                    {
                        if (TryFindHoleInOneAtAngle(dirToHole, ballPos, holePos, ballRadius, drag, angDrag, ballMat, maxPower, out float winPower, out Vector3[] winPath))
                        {
                            ApplyWinningPath(winPath, winPower, dirToHole, 0f, true);
                            return;
                        }

                        _searchState = SearchState.AngleSweep;
                        _currentAngleIndex = 0;
                        continue;
                    }

                    // STAGE 2: 360-Degree Geometry Sweep (Probe with medium & high powers to find candidate trajectories)
                    if (_searchState == SearchState.AngleSweep)
                    {
                        if (_currentAngleIndex >= _candidateAngles.Count)
                        {
                            if (_promisingCandidates.Count > 0)
                            {
                                // Sort candidates: closest flyby to hole first
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

                        float angle = _candidateAngles[_currentAngleIndex];
                        Vector3 testDir = Quaternion.Euler(0, angle, 0) * dirToHole;

                        // Probe with 2 representative powers (45% and 75% max power)
                        float[] probePows = new float[] { maxPower * 0.45f, maxPower * 0.75f };
                        foreach (float probeP in probePows)
                        {
                            float speed = CalculateBallSpeed(probeP, maxPower);
                            var probeResult = TrajectorySimulator.SimulateShotDetailed(
                                ballPos, testDir * speed, holePos, ballRadius, drag, angDrag, ballMat, 650, 1);

                            if (probeResult.Sunk)
                            {
                                ApplyWinningPath(probeResult.Path, probeP, testDir, 0f, true);
                                return;
                            }

                            // If this angle passes near the hole, save as candidate
                            if (probeResult.MinDistanceToHole < 1.4f)
                            {
                                _promisingCandidates.Add(new CandidateAngle
                                {
                                    AngleOffset = angle,
                                    ClosestFlybyDist = probeResult.MinDistanceToHole,
                                    ProbePower = probeP
                                });
                                break;
                            }

                            // Track best approach shot based on FINAL RESTING POSITION (not stopping in front of ball)
                            if (probeResult.FinalDistanceToHole < _winningMinDist && probeP >= maxPower * 0.15f)
                            {
                                _winningMinDist = probeResult.FinalDistanceToHole;
                                _winningPath = probeResult.Path;
                                _winningPower = probeP;
                                _winningDirection = testDir;
                            }
                        }

                        _currentAngleIndex++;
                    }

                    // STAGE 3: Precision Power Optimization on Promising Angles
                    else if (_searchState == SearchState.PowerRefinement)
                    {
                        if (_candidateRefineIndex >= _promisingCandidates.Count)
                        {
                            CompleteSearchWithBestPath();
                            return;
                        }

                        CandidateAngle candidate = _promisingCandidates[_candidateRefineIndex];
                        Vector3 candidateDir = Quaternion.Euler(0, candidate.AngleOffset, 0) * dirToHole;

                        // Fast 1D binary search on power to find exact hole-in-one
                        if (TryFindHoleInOneAtAngle(candidateDir, ballPos, holePos, ballRadius, drag, angDrag, ballMat, maxPower, out float winP, out Vector3[] winPath))
                        {
                            ApplyWinningPath(winPath, winP, candidateDir, 0f, true);
                            return;
                        }

                        _candidateRefineIndex++;
                    }
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
        /// Performs continuous 1D power search to find if any power sinks the ball at this angle
        /// </summary>
        private bool TryFindHoleInOneAtAngle(
            Vector3 testDir,
            Vector3 ballPos,
            Vector3 holePos,
            float ballRadius,
            float drag,
            float angDrag,
            PhysicMaterial ballMat,
            float maxPower,
            out float winningPower,
            out Vector3[] winningPath)
        {
            winningPower = 0f;
            winningPath = null;

            float lowP = maxPower * 0.08f;
            float highP = maxPower * 0.98f;
            int subdivisions = 20;
            float step = (highP - lowP) / subdivisions;

            float bestFinalDist = float.MaxValue;
            Vector3[] bestPath = null;
            float bestP = 0f;

            for (int i = 0; i <= subdivisions; i++)
            {
                float testP = lowP + i * step;
                float speed = CalculateBallSpeed(testP, maxPower);
                var result = TrajectorySimulator.SimulateShotDetailed(
                    ballPos, testDir * speed, holePos, ballRadius, drag, angDrag, ballMat, 800, 1);

                if (result.Sunk)
                {
                    // Fine-tune around sinking power to find optimal middle power
                    float fineMin = Mathf.Max(lowP, testP - step);
                    float fineMax = Mathf.Min(highP, testP + step);
                    float sunkMin = testP;
                    float sunkMax = testP;

                    for (float fp = fineMin; fp <= fineMax; fp += step * 0.1f)
                    {
                        float fSpeed = CalculateBallSpeed(fp, maxPower);
                        var fResult = TrajectorySimulator.SimulateShotDetailed(
                            ballPos, testDir * fSpeed, holePos, ballRadius, drag, angDrag, ballMat, 800, 1);
                        if (fResult.Sunk)
                        {
                            if (fp < sunkMin) sunkMin = fp;
                            if (fp > sunkMax) sunkMax = fp;
                            winningPath = fResult.Path;
                        }
                    }

                    winningPower = (sunkMin + sunkMax) * 0.5f;
                    if (winningPath == null) winningPath = result.Path;
                    return true;
                }

                if (result.FinalDistanceToHole < bestFinalDist && testP >= maxPower * 0.12f)
                {
                    bestFinalDist = result.FinalDistanceToHole;
                    bestPath = result.Path;
                    bestP = testP;
                }
            }

            if (bestFinalDist < _winningMinDist && bestPath != null)
            {
                _winningMinDist = bestFinalDist;
                _winningPath = bestPath;
                _winningPower = bestP;
                _winningDirection = testDir;
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

            UpdateSolutionVisuals();
        }

        private void CompleteSearchWithBestPath()
        {
            _searchState = SearchState.Completed;
            if (_winningPath != null && _winningPath.Length > 0)
            {
                _hasSolution = true;
                _isHoleInOne = false;
                UpdateSolutionVisuals();
            }
            else
            {
                _hasSolution = false;
                _isHoleInOne = false;
                if (_solutionLineRenderer != null) _solutionLineRenderer.positionCount = 0;
            }
        }

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

            float boxWidth = 550f;
            float boxHeight = 115f;
            GUI.Box(new Rect(10, 10, boxWidth, boxHeight), GUIContent.none);

            float maxPower = GetMaxPower();

            if (_searchState == SearchState.DirectEvaluation || _searchState == SearchState.AngleSweep || _searchState == SearchState.PowerRefinement)
            {
                GUI.color = Color.yellow;
                float progress = 0f;
                if (_searchState == SearchState.DirectEvaluation) progress = 10f;
                else if (_searchState == SearchState.AngleSweep && _candidateAngles.Count > 0)
                {
                    progress = 10f + ((float)_currentAngleIndex / _candidateAngles.Count) * 60f;
                }
                else if (_searchState == SearchState.PowerRefinement && _promisingCandidates.Count > 0)
                {
                    progress = 70f + ((float)_candidateRefineIndex / _promisingCandidates.Count) * 30f;
                }

                GUI.Label(new Rect(20, 18, boxWidth - 20, 30), $"Aimbot: Suche Hole-in-One Trajektorien... ({progress:F0}%)", headerStyle);
                GUI.color = Color.white;
                GUI.Label(new Rect(20, 50, boxWidth - 20, 25), "Scanne Bandenreflexionen, Fairway-Kurven und Stärken...", subStyle);
            }
            else if (_hasSolution && _isHoleInOne)
            {
                GUI.color = new Color(0.1f, 1f, 0.4f);
                GUI.Label(new Rect(20, 18, boxWidth - 20, 30), "★ HOLE-IN-ONE GEFUNDEN! ★", headerStyle);

                float ratio = Mathf.Clamp01(_winningPower / maxPower);
                GUI.color = Color.white;
                GUI.Label(new Rect(20, 48, boxWidth - 20, 25), $"Benötigte Power: {_winningPower:F0} ({ratio * 100f:F1}%) | Halte [F] für Auto-Aim", subStyle);
                GUI.Label(new Rect(20, 72, boxWidth - 20, 25), $"Loch-Distanz: {Vector3.Distance(_targetBall.transform.position, _targetHole.HolePosition.position):F1}m", subStyle);

                DrawSimulatedPowerBar(maxPower);
            }
            else if (_hasSolution && !_isHoleInOne)
            {
                GUI.color = new Color(1f, 0.6f, 0.1f);
                GUI.Label(new Rect(20, 18, boxWidth - 20, 30), $"Bester Annäherungsschlag (Rest: {_winningMinDist:F2}m)", headerStyle);

                float ratio = Mathf.Clamp01(_winningPower / maxPower);
                GUI.color = Color.white;
                GUI.Label(new Rect(20, 48, boxWidth - 20, 25), $"Empfohlene Power: {_winningPower:F0} ({ratio * 100f:F1}%) | Halte [F] für Auto-Aim", subStyle);
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
        }

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
