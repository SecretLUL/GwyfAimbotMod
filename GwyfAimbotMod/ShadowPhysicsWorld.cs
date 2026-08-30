using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GwyfAimbotMod
{
    /// <summary>
    /// A second physics scene that mirrors the current hole and is stepped by the game's own
    /// PhysX solver instead of a hand-written integrator.
    ///
    /// Nothing in here re-implements physics. Collider shapes, physics materials, layers,
    /// rigidbody mass/inertia/solver settings, gravity and the fixed timestep are all copied
    /// from the live objects. A scene created with LocalPhysicsMode.Physics3D is never advanced
    /// by the game's own FixedUpdate, so stepping it here cannot disturb the running match -
    /// and the real ball never has to be disabled to keep it out of the query results.
    /// </summary>
    internal sealed class ShadowPhysicsWorld
    {
        internal enum State
        {
            Empty,        // nothing built yet
            Collecting,   // source colliders snapshotted, ball clone pending
            Mirroring,    // copying colliders, spread over several frames
            Ready,        // usable
            Unsupported   // local physics scenes unavailable - caller must fall back
        }

        /// <summary>Trigger volumes whose name matches one of these are treated as "shot lost".</summary>
        private static readonly string[] HazardKeywords =
        {
            "water", "wasser", "oob", "outofbounds", "out_of_bounds", "outofplay",
            "kill", "death", "respawn", "reset", "void", "lava", "hazard", "abyss"
        };

        private Scene _scene;
        private PhysicsScene _physicsScene;
        private int _generation;

        // Snapshot of the source colliders, taken once when a build starts. Copied into a managed
        // array so no Il2CppReferenceArray has to survive across frames.
        private Collider[] _sourceColliders;
        private int _mirrorIndex;
        private float _buildStartedAt;

        private GameObject _ballObject;
        private Rigidbody _ballBody;
        private float _ballRadius;

        private readonly List<Collider> _hazardVolumes = new List<Collider>(16);

        // Mirrored clones plus where each came from. Only populated while diagnostics are on:
        // it exists so a divergence can be attributed to a specific piece of geometry.
        private readonly List<Collider> _mirrored = new List<Collider>(512);
        private readonly List<string> _mirroredPaths = new List<string>(512);

        // Why colliders were left out. A large skip count almost always means the build ran while
        // the level was still streaming in, so the reasons are tracked individually.
        private int _skipNull;
        private int _skipDisabled;
        private int _skipInactive;
        private int _skipDynamicBody;
        private int _skipUnsupportedShape;
        private int _negativeScale;

        // Re-check for geometry that appeared after the snapshot (async level load).
        private float _nextRescanTime;
        private int _rescanRebuilds;
        private int _rescanHole = int.MinValue;
        private const float RescanInterval = 2f;
        private const int MaxRescanRebuilds = 2;

        /// <summary>Scene name prefix, also used to recognise our own clones during a rescan.</summary>
        private const string ShadowScenePrefix = "GwyfShadowPhysics";

        public State BuildState { get; private set; } = State.Empty;
        public int MirroredColliders { get; private set; }
        public int SkippedColliders { get; private set; }
        public int BuiltHoleNumber { get; private set; } = int.MinValue;
        public string BuiltSceneName { get; private set; }
        public float LastBuildSeconds { get; private set; }
        public string UnsupportedReason { get; private set; }

        public bool IsReady { get { return BuildState == State.Ready && _ballBody != null && _physicsScene.IsValid(); } }
        public bool IsUnsupported { get { return BuildState == State.Unsupported; } }

        public Rigidbody Ball { get { return _ballBody; } }
        public float BallRadius { get { return _ballRadius; } }
        public PhysicsScene Scene { get { return _physicsScene; } }
        public List<Collider> HazardVolumes { get { return _hazardVolumes; } }

        /// <summary>Progress of the current build, 0..1. Only meaningful while mirroring.</summary>
        public float BuildProgress
        {
            get
            {
                if (BuildState == State.Ready) return 1f;
                if (_sourceColliders == null || _sourceColliders.Length == 0) return 0f;
                return Mathf.Clamp01((float)_mirrorIndex / _sourceColliders.Length);
            }
        }

        // ------------------------------------------------------------------ build

        /// <summary>
        /// Brings the shadow world in sync with the live scene, spending at most
        /// <paramref name="budgetMs"/> of this frame on it. Rebuilds from scratch when the hole
        /// or the loaded scene changed. Safe to call every frame.
        /// </summary>
        public void EnsureBuilt(BallMovement ball, int holeNumber, float budgetMs)
        {
            if (BuildState == State.Unsupported) return;
            if (ball == null) return;

            string sceneName = ball.gameObject.scene.name;

            bool stale = BuildState != State.Empty
                         && (BuiltHoleNumber != holeNumber
                             || !string.Equals(BuiltSceneName, sceneName, StringComparison.Ordinal));

            if (stale)
            {
                Plugin.Logger.LogInfo(
                    "Shadow world: hole/scene changed (" + BuiltSceneName + "#" + BuiltHoleNumber
                    + " -> " + sceneName + "#" + holeNumber + "), rebuilding.");
                Reset();
            }

            if (BuildState == State.Ready)
            {
                RescanForLateGeometry(holeNumber, sceneName);
                return;
            }

            float start = Time.realtimeSinceStartup;

            if (BuildState == State.Empty)
            {
                if (!CreateScene()) return;
                BuiltHoleNumber = holeNumber;
                BuiltSceneName = sceneName;
                _buildStartedAt = start;
                CollectSourceColliders();
                BuildState = State.Collecting;
            }

            if (BuildState == State.Collecting)
            {
                if (!CreateShadowBall(ball))
                {
                    MarkUnsupported("shadow ball could not be created");
                    return;
                }
                _mirrorIndex = 0;
                MirroredColliders = 0;
                SkippedColliders = 0;
                _hazardVolumes.Clear();
                BuildState = State.Mirroring;
            }

            if (BuildState == State.Mirroring)
            {
                MirrorColliders(start, budgetMs);
            }
        }

        /// <summary>
        /// The level streams in asynchronously, so a build that starts too early snapshots colliders
        /// that are not active yet and loses them permanently. Counting the live colliders again
        /// every couple of seconds catches that and triggers one rebuild.
        /// </summary>
        private void RescanForLateGeometry(int holeNumber, string sceneName)
        {
            if (_rescanHole != holeNumber)
            {
                _rescanHole = holeNumber;
                _rescanRebuilds = 0;
            }

            // Bounded on purpose: a rescan that keeps disagreeing with the mirror pass must not be
            // able to rebuild forever, which is exactly what a miscount once caused.
            if (_rescanRebuilds >= MaxRescanRebuilds) return;
            if (Time.realtimeSinceStartup < _nextRescanTime) return;
            _nextRescanTime = Time.realtimeSinceStartup + RescanInterval;

            int live = CountMirrorableColliders();

            // Small drift is normal (props despawning); a real streaming miss is large.
            if (live > MirroredColliders + Mathf.Max(8, MirroredColliders / 20))
            {
                _rescanRebuilds++;
                Plugin.Logger.LogInfo(
                    "Shadow world: " + live + " mirrorable colliders live but only " + MirroredColliders
                    + " mirrored - the level finished streaming after the build. Rebuilding ("
                    + _rescanRebuilds + "/" + MaxRescanRebuilds + ").");
                DiagnosticsLog.Line("shadow", "rescan rebuild " + _rescanRebuilds + "/" + MaxRescanRebuilds
                    + ": live " + live + " vs mirrored " + MirroredColliders);

                int keepHole = _rescanHole;
                int keepCount = _rescanRebuilds;
                Reset();
                _rescanHole = keepHole;
                _rescanRebuilds = keepCount;
            }
        }

        /// <summary>
        /// Counts colliders in the live scene that the mirror pass would accept.
        ///
        /// FindObjectsOfType spans every loaded scene, so the shadow scene's own clones are in the
        /// result too. Counting them made the rescan see roughly twice the real geometry, conclude
        /// that half the level was missing, and rebuild on every interval forever.
        /// </summary>
        private static int CountMirrorableColliders()
        {
            var found = UnityEngine.Object.FindObjectsOfType<Collider>();
            int n = found != null ? found.Length : 0;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                var c = found[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;

                string owner = c.gameObject.scene.name;
                if (owner != null && owner.StartsWith(ShadowScenePrefix, StringComparison.Ordinal)) continue;

                var body = c.attachedRigidbody;
                if (body != null && !body.isKinematic) continue;
                count++;
            }
            return count;
        }

        private bool CreateScene()
        {
            try
            {
                _generation++;
                // The Il2CppInterop CreateSceneParameters has no constructor, only the field.
                var parameters = new CreateSceneParameters();
                parameters.localPhysicsMode = LocalPhysicsMode.Physics3D;
                _scene = SceneManager.CreateScene(ShadowScenePrefix + "#" + _generation, parameters);
                _physicsScene = PhysicsSceneExtensions.GetPhysicsScene(_scene);

                if (!_scene.IsValid() || !_physicsScene.IsValid())
                {
                    MarkUnsupported("CreateScene returned an invalid local physics scene");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                MarkUnsupported("CreateScene threw: " + ex.Message);
                return false;
            }
        }

        private void CollectSourceColliders()
        {
            var found = UnityEngine.Object.FindObjectsOfType<Collider>();
            int n = found != null ? found.Length : 0;

            var list = new List<Collider>(n);
            for (int i = 0; i < n; i++)
            {
                var col = found[i];
                if (col == null) continue;
                list.Add(col);
            }

            _sourceColliders = list.ToArray();
            Plugin.Logger.LogInfo(
                "Shadow world: " + _sourceColliders.Length + " candidate colliders in scene '" + BuiltSceneName + "'.");
        }

        /// <summary>
        /// Clones the live ball: same collider geometry, same physics material, same layer, and the
        /// full rigidbody tuning including the explicit inertia tensor. Colliders are added before
        /// mass/inertia are written, because adding a collider re-derives the tensor.
        /// </summary>
        private bool CreateShadowBall(BallMovement ball)
        {
            try
            {
                var srcRb = ball.m_rigidBody;
                if (srcRb == null) srcRb = ball.GetComponent<Rigidbody>();
                if (srcRb == null)
                {
                    Plugin.Logger.LogWarning("Shadow world: live ball has no Rigidbody.");
                    return false;
                }

                var srcTransform = ball.transform;

                _ballObject = new GameObject("GwyfShadowBall");
                SceneManager.MoveGameObjectToScene(_ballObject, _scene);

                // BallMovement swaps the ball between m_collideLayer and m_ignoreLayer at runtime
                // (UpdateCollisionLayer). Capturing whatever layer it happens to sit on while at
                // rest can put the shadow ball under a different collision matrix than the moving
                // ball, which shows up as contacts the real ball never has.
                int collideLayer = ball.m_collideLayer;
                int restingLayer = ball.gameObject.layer;
                _ballObject.layer = (collideLayer >= 0 && collideLayer < 32) ? collideLayer : restingLayer;
                if (_ballObject.layer != restingLayer)
                {
                    Plugin.Logger.LogInfo(
                        "Shadow world: ball mirrored on collide layer " + _ballObject.layer
                        + " (" + LayerMask.LayerToName(_ballObject.layer) + "), not its resting layer "
                        + restingLayer + " (" + LayerMask.LayerToName(restingLayer) + ").");
                }

                // Unit scale: the collider shapes carry the world size themselves (see
                // SetupMirroredCollider), so nothing depends on reproducing the scale here.
                _ballObject.transform.localScale = Vector3.one;
                _ballObject.transform.SetPositionAndRotation(srcTransform.position, srcTransform.rotation);

                var rb = _ballObject.AddComponent<Rigidbody>();

                // Mirror the whole ball hierarchy: the selectable ball shapes put the real collider
                // on a child object, so root-only mirroring gives the wrong body.
                _ballRadius = 0f;
                int colliderCount = 0;
                var srcCols = ball.GetComponentsInChildren<Collider>(false);
                for (int i = 0; i < srcCols.Length; i++)
                {
                    var src = srcCols[i];
                    if (src == null || !src.enabled || src.isTrigger) continue;

                    GameObject target;
                    if (src.transform == srcTransform)
                    {
                        target = _ballObject;
                    }
                    else
                    {
                        // Child collider: its own object under the ball root, so it belongs to the
                        // same rigidbody exactly as in the live hierarchy.
                        target = new GameObject("c" + i);
                        target.layer = _ballObject.layer;
                        target.transform.SetParent(_ballObject.transform, false);
                    }

                    Collider added;
                    if (!SetupMirroredCollider(target, src, out added))
                    {
                        Plugin.Logger.LogWarning("Shadow world: ball collider '" + src.name
                            + "' (" + ColliderKind(src) + ") could not be mirrored.");
                        continue;
                    }

                    colliderCount++;
                    var sphere = added.TryCast<SphereCollider>();
                    if (sphere != null) _ballRadius = Mathf.Max(_ballRadius, sphere.radius);
                }

                if (colliderCount == 0)
                {
                    Plugin.Logger.LogWarning("Shadow world: live ball has no usable non-trigger collider.");
                    return false;
                }

                if (_ballRadius <= 0.0001f)
                {
                    _ballRadius = ball.BallRadius > 0f ? ball.BallRadius : TrajectorySimulator.DEFAULT_BALL_RADIUS;
                }

                Plugin.Logger.LogInfo(
                    "Shadow world: ball mirrored with " + colliderCount + " collider(s) from "
                    + srcCols.Length + " in the hierarchy, radius " + _ballRadius.ToString("F4")
                    + ", mass " + srcRb.mass.ToString("F3")
                    + ", ccd " + srcRb.collisionDetectionMode
                    + ", drag " + srcRb.drag.ToString("F3") + "/" + srcRb.angularDrag.ToString("F3") + " (at rest).");

                // Order matters: mass first, then the measured tensor. Writing inertiaTensor
                // switches PhysX off auto-computation, which is what a 1:1 copy needs.
                rb.mass = srcRb.mass;
                rb.useGravity = srcRb.useGravity;
                rb.isKinematic = false;
                rb.drag = srcRb.drag;
                rb.angularDrag = srcRb.angularDrag;
                rb.collisionDetectionMode = srcRb.collisionDetectionMode;
                rb.solverIterations = srcRb.solverIterations;
                rb.solverVelocityIterations = srcRb.solverVelocityIterations;
                rb.sleepThreshold = srcRb.sleepThreshold;
                rb.maxAngularVelocity = srcRb.maxAngularVelocity;
                rb.maxDepenetrationVelocity = srcRb.maxDepenetrationVelocity;
                rb.constraints = srcRb.constraints;
                rb.detectCollisions = srcRb.detectCollisions;
                rb.freezeRotation = srcRb.freezeRotation;
                // Interpolation is render-time smoothing only; it must be off so recorded
                // positions are the raw solver output.
                rb.interpolation = RigidbodyInterpolation.None;

                rb.centerOfMass = srcRb.centerOfMass;
                rb.inertiaTensor = srcRb.inertiaTensor;
                rb.inertiaTensorRotation = srcRb.inertiaTensorRotation;

                _ballBody = rb;
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("Shadow world: CreateShadowBall failed: " + ex);
                return false;
            }
        }

        private void MirrorColliders(float frameStart, float budgetMs)
        {
            while (_mirrorIndex < _sourceColliders.Length)
            {
                if ((Time.realtimeSinceStartup - frameStart) * 1000f >= budgetMs) return;

                var src = _sourceColliders[_mirrorIndex++];
                if (src == null) { _skipNull++; SkippedColliders++; continue; }
                if (!src.enabled) { _skipDisabled++; SkippedColliders++; continue; }
                if (!src.gameObject.activeInHierarchy) { _skipInactive++; SkippedColliders++; continue; }

                // Anything carrying a non-kinematic body is another player's ball or a loose prop:
                // it would need its own simulated state, so it is left out rather than frozen in place.
                var attached = src.attachedRigidbody;
                if (attached != null && !attached.isKinematic) { _skipDynamicBody++; SkippedColliders++; continue; }

                if (MirrorOne(src)) MirroredColliders++;
                else { _skipUnsupportedShape++; SkippedColliders++; }
            }

            LastBuildSeconds = Time.realtimeSinceStartup - _buildStartedAt;
            BuildState = State.Ready;
            _sourceColliders = null;
            _nextRescanTime = Time.realtimeSinceStartup + RescanInterval;

            Plugin.Logger.LogInfo(
                "Shadow world ready: " + MirroredColliders + " colliders mirrored, " + SkippedColliders
                + " skipped, " + _hazardVolumes.Count + " hazard volume(s), ball radius "
                + _ballRadius.ToString("F4") + ", built in " + (LastBuildSeconds * 1000f).ToString("F0") + " ms.");

            // Without the breakdown a large skip count is unreadable: 'inactive' means the build
            // raced the level load, 'unsupported' means real geometry is genuinely missing.
            Plugin.Logger.LogInfo(
                "Shadow world skips: inactive " + _skipInactive
                + ", disabled " + _skipDisabled
                + ", dynamic body " + _skipDynamicBody
                + ", unsupported shape " + _skipUnsupportedShape
                + ", null " + _skipNull
                + "  |  negative-scale colliders mirrored: " + _negativeScale);

            DiagnosticsLog.Section("shadow world for hole " + BuiltHoleNumber);
            DiagnosticsLog.Line("shadow", "mirrored " + MirroredColliders + " of "
                + (MirroredColliders + SkippedColliders) + " colliders in "
                + (LastBuildSeconds * 1000f).ToString("F0") + " ms"
                + "   hazards " + _hazardVolumes.Count
                + "   ballRadius " + DiagnosticsLog.F(_ballRadius));
            DiagnosticsLog.Line("shadow", "skips: inactive " + _skipInactive
                + ", disabled " + _skipDisabled
                + ", dynamicBody " + _skipDynamicBody
                + ", unsupportedShape " + _skipUnsupportedShape
                + ", null " + _skipNull
                + "   negativeScale " + _negativeScale);
            DiagnosticsLog.Flush();

            if (_skipUnsupportedShape > 0)
            {
                Plugin.Logger.LogWarning(
                    "Shadow world: " + _skipUnsupportedShape + " collider(s) had a shape that cannot be "
                    + "mirrored (TerrainCollider/WheelCollider or a MeshCollider without a mesh). "
                    + "The ball can pass through those.");
            }
        }

        private bool MirrorOne(Collider src)
        {
            var go = new GameObject("m");
            go.layer = src.gameObject.layer;
            SceneManager.MoveGameObjectToScene(go, _scene);

            Collider clone;
            if (!SetupMirroredCollider(go, src, out clone))
            {
                UnityEngine.Object.Destroy(go);
                Plugin.Logger.LogWarning("Shadow world: cannot mirror " + ColliderKind(src)
                    + " '" + src.name + "' at " + HierarchyPath(src.transform));
                DiagnosticsLog.Line("shadow", "UNMIRRORED " + ColliderKind(src) + "  " + HierarchyPath(src.transform));
                return false;
            }

            if (clone.isTrigger && IsHazardName(src))
            {
                _hazardVolumes.Add(clone);
            }

            if (DiagnosticsLog.IsActive)
            {
                _mirrored.Add(clone);
                _mirroredPaths.Add(HierarchyPath(src.transform));
            }

            return true;
        }

        /// <summary>
        /// Recreates <paramref name="src"/> on <paramref name="target"/>, including its world
        /// placement.
        ///
        /// For box/sphere/capsule the world scale is baked into the shape parameters and the target
        /// is left at unit scale. Reproducing the scale on the transform instead is ambiguous: a
        /// hierarchy with negative scale (this course has 14 such colliders, and the game itself
        /// warns about them) does not round-trip through lossyScale, so the mirrored shape could end
        /// up rotated or offset differently from the original. Baking sidesteps that entirely.
        ///
        /// MeshColliders must keep the transform scale, since the mesh itself carries the geometry.
        /// </summary>
        private bool SetupMirroredCollider(GameObject target, Collider src, out Collider clone)
        {
            clone = null;

            var srcTransform = src.transform;
            Vector3 lossy = srcTransform.lossyScale;
            Vector3 absScale = new Vector3(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z));
            if (lossy.x < 0f || lossy.y < 0f || lossy.z < 0f) _negativeScale++;

            float uniform = Mathf.Max(absScale.x, Mathf.Max(absScale.y, absScale.z));

            var box = src.TryCast<BoxCollider>();
            if (box != null)
            {
                target.transform.localScale = Vector3.one;
                target.transform.SetPositionAndRotation(srcTransform.TransformPoint(box.center), srcTransform.rotation);
                var c = target.AddComponent<BoxCollider>();
                c.center = Vector3.zero;
                c.size = Vector3.Scale(box.size, absScale);
                clone = c;
            }

            if (clone == null)
            {
                var sphere = src.TryCast<SphereCollider>();
                if (sphere != null)
                {
                    target.transform.localScale = Vector3.one;
                    target.transform.SetPositionAndRotation(srcTransform.TransformPoint(sphere.center), srcTransform.rotation);
                    var c = target.AddComponent<SphereCollider>();
                    c.center = Vector3.zero;
                    // Unity scales a sphere by the largest axis, mirrored here on purpose.
                    c.radius = sphere.radius * uniform;
                    clone = c;
                }
            }

            if (clone == null)
            {
                var capsule = src.TryCast<CapsuleCollider>();
                if (capsule != null)
                {
                    target.transform.localScale = Vector3.one;
                    target.transform.SetPositionAndRotation(srcTransform.TransformPoint(capsule.center), srcTransform.rotation);

                    // Height follows the capsule's own axis, radius the largest of the other two.
                    int dir = capsule.direction;
                    float heightScale = dir == 0 ? absScale.x : dir == 1 ? absScale.y : absScale.z;
                    float radiusScale = dir == 0 ? Mathf.Max(absScale.y, absScale.z)
                                      : dir == 1 ? Mathf.Max(absScale.x, absScale.z)
                                                 : Mathf.Max(absScale.x, absScale.y);

                    var c = target.AddComponent<CapsuleCollider>();
                    c.center = Vector3.zero;
                    c.direction = dir;
                    c.radius = capsule.radius * radiusScale;
                    c.height = capsule.height * heightScale;
                    clone = c;
                }
            }

            if (clone == null)
            {
                var mesh = src.TryCast<MeshCollider>();
                if (mesh != null && mesh.sharedMesh != null)
                {
                    // The mesh carries the geometry, so the scale has to stay on the transform.
                    target.transform.localScale = lossy;
                    target.transform.SetPositionAndRotation(srcTransform.position, srcTransform.rotation);
                    var c = target.AddComponent<MeshCollider>();
                    // convex must be set before the mesh so PhysX cooks the right shape.
                    c.convex = mesh.convex;
                    c.sharedMesh = mesh.sharedMesh;
                    clone = c;
                }
            }

            // TerrainCollider / WheelCollider and anything else are not mirrored.
            if (clone == null) return false;

            clone.sharedMaterial = src.sharedMaterial;
            clone.isTrigger = src.isTrigger;
            clone.contactOffset = src.contactOffset;
            return true;
        }

        internal static string ColliderKind(Collider c)
        {
            if (c == null) return "null";
            if (c.TryCast<SphereCollider>() != null) return "Sphere";
            if (c.TryCast<BoxCollider>() != null) return "Box";
            if (c.TryCast<CapsuleCollider>() != null) return "Capsule";
            if (c.TryCast<MeshCollider>() != null) return "Mesh";
            return c.GetIl2CppType().Name;
        }

        internal static string HierarchyPath(Transform t)
        {
            if (t == null) return "<null>";
            string path = t.name;
            var cur = t.parent;
            int guard = 0;
            while (cur != null && guard++ < 24)
            {
                path = cur.name + "/" + path;
                cur = cur.parent;
            }
            return path;
        }

        /// <summary>
        /// Lists the mirrored colliders near <paramref name="p"/> alongside the live scene's, so a
        /// divergence can be attributed to concrete geometry rather than guessed at.
        /// </summary>
        public string DescribeNear(Vector3 p, float radius)
        {
            var sb = new System.Text.StringBuilder();

            sb.Append("shadow: ");
            int shown = 0;
            for (int i = 0; i < _mirrored.Count && shown < 8; i++)
            {
                var c = _mirrored[i];
                if (c == null) continue;
                if (c.bounds.SqrDistance(p) > radius * radius) continue;
                sb.Append('[').Append(ColliderKind(c)).Append(' ').Append(_mirroredPaths[i])
                  .Append(c.isTrigger ? " TRIGGER" : "")
                  .Append(" L").Append(c.gameObject.layer).Append("] ");
                shown++;
            }
            if (shown == 0) sb.Append("<none> ");

            sb.Append("   live: ");
            try
            {
                var hits = Physics.OverlapSphere(p, radius, ~0, QueryTriggerInteraction.Collide);
                int n = hits != null ? hits.Length : 0;
                int listed = 0;
                for (int i = 0; i < n && listed < 8; i++)
                {
                    var c = hits[i];
                    if (c == null) continue;
                    string owner = c.gameObject.scene.name;
                    if (owner != null && owner.StartsWith(ShadowScenePrefix, StringComparison.Ordinal)) continue;
                    sb.Append('[').Append(ColliderKind(c)).Append(' ').Append(HierarchyPath(c.transform))
                      .Append(c.isTrigger ? " TRIGGER" : "")
                      .Append(c.enabled ? "" : " DISABLED")
                      .Append(" L").Append(c.gameObject.layer).Append("] ");
                    listed++;
                }
                if (listed == 0) sb.Append("<none>");
            }
            catch (Exception ex)
            {
                sb.Append("<query failed: ").Append(ex.Message).Append('>');
            }

            return sb.ToString();
        }

        /// <summary>Component-wise a/b, leaving 1 where the divisor is degenerate.</summary>
        private static Vector3 SafeDivide(Vector3 a, Vector3 b)
        {
            return new Vector3(
                Mathf.Abs(b.x) > 1e-6f ? a.x / b.x : 1f,
                Mathf.Abs(b.y) > 1e-6f ? a.y / b.y : 1f,
                Mathf.Abs(b.z) > 1e-6f ? a.z / b.z : 1f);
        }

        private static bool IsHazardName(Collider src)
        {
            var t = src.transform;
            // Check the object plus two ancestors: hazard volumes are usually named on a parent.
            for (int depth = 0; depth < 3 && t != null; depth++, t = t.parent)
            {
                string candidate = t.name;
                if (string.IsNullOrEmpty(candidate)) continue;

                string lower = candidate.ToLowerInvariant();
                for (int i = 0; i < HazardKeywords.Length; i++)
                {
                    if (lower.Contains(HazardKeywords[i])) return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ teardown

        private void MarkUnsupported(string reason)
        {
            UnsupportedReason = reason;
            BuildState = State.Unsupported;
            Plugin.Logger.LogWarning(
                "Shadow world unavailable (" + reason + "). Falling back to the approximate simulator.");
        }

        public void Reset()
        {
            _ballBody = null;
            _ballObject = null;
            _sourceColliders = null;
            _hazardVolumes.Clear();
            _mirrored.Clear();
            _mirroredPaths.Clear();
            _mirrorIndex = 0;
            MirroredColliders = 0;
            SkippedColliders = 0;
            _skipNull = 0;
            _skipDisabled = 0;
            _skipInactive = 0;
            _skipDynamicBody = 0;
            _skipUnsupportedShape = 0;
            _negativeScale = 0;
            BuiltHoleNumber = int.MinValue;
            BuiltSceneName = null;

            if (_scene.IsValid())
            {
                try
                {
                    SceneManager.UnloadSceneAsync(_scene);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning("Shadow world: unloading the previous scene failed: " + ex.Message);
                }
            }

            _scene = default;
            _physicsScene = default;
            if (BuildState != State.Unsupported) BuildState = State.Empty;
        }
    }
}
