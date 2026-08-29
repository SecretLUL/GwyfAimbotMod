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
    /// Einmaliger Dump der Physik-Parameter des laufenden Spiels: Rigidbody des eigenen Balls,
    /// PhysX-Globals, Zeitschritt, Ball-Collider samt PhysicMaterial, Material von Boden und
    /// naechster Wand, die Drag-Saetze auf BallMovement und die Power-Kurve.
    ///
    /// Ausgabe geht in das BepInEx-Log und zusaetzlich als JSON neben LogOutput.log.
    /// Grundlage, um die geratenen Konstanten im Simulator durch gemessene Werte zu ersetzen.
    /// </summary>
    internal static class PhysicsParameterDump
    {
        private const float GroundProbeDistance = 5f;
        private const float WallProbeDistance = 30f;

        // Alle Layer ausser 2 (Ignore Raycast) - identisch zur Maske im TrajectorySimulator.
        private const int ProbeLayerMask = ~0 & ~(1 << 2);

        public static void Run(BallMovement ball)
        {
            try
            {
                if (ball == null) ball = FindOwnBall();
                if (ball == null)
                {
                    Plugin.Logger.LogWarning("Parameter-Dump: kein BallMovement in der Szene gefunden.");
                    return;
                }

                var j = new JsonBuilder();
                j.BeginObject(null);
                WriteMeta(j, ball);
                WriteTime(j);
                WritePhysicsGlobals(j);
                WriteRigidbody(j, ball);
                WriteBallColliders(j, ball);
                WriteBallMovement(j, ball);
                WritePowerCurve(j, ball);
                WriteProbes(j, ball);
                j.EndObject();

                string text = j.ToString();

                string path = null;
                string fileError = null;
                try
                {
                    path = WriteToFile(text);
                }
                catch (Exception ex)
                {
                    fileError = ex.Message;
                }

                Plugin.Logger.LogInfo(path != null
                    ? "=== GWYF Physik-Parameter-Dump  ->  " + path + " ==="
                    : "=== GWYF Physik-Parameter-Dump (JSON nicht geschrieben: " + fileError + ") ===");
                Plugin.Logger.LogInfo(text);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("Parameter-Dump fehlgeschlagen: " + ex);
            }
        }

        private static string WriteToFile(string text)
        {
            // Paths.BepInExRootPath ist der Ordner, in dem auch LogOutput.log liegt.
            string file = Path.Combine(
                Paths.BepInExRootPath,
                "gwyf-physics-dump_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return file;
        }

        // ------------------------------------------------------------------ Abschnitte

        private static void WriteMeta(JsonBuilder j, BallMovement ball)
        {
            j.BeginObject("meta");
            j.Prop("timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            j.Prop("plugin", "com.ammar.gwyf.aimbot");
            j.Prop("unityVersion", Application.unityVersion);
            j.Prop("gameVersion", Application.version);
            j.Prop("scene", ball.gameObject.scene.name);
            j.Prop("holeNumber", (int)ball.HoleNumber);
            j.Prop("isMasterBall", ball.IsMasterBall);
            j.Prop("ballPath", HierarchyPath(ball.transform));
            j.EndObject();
        }

        private static void WriteTime(JsonBuilder j)
        {
            j.BeginObject("time");
            j.Prop("fixedDeltaTime", Time.fixedDeltaTime);
            j.Prop("maximumDeltaTime", Time.maximumDeltaTime);
            j.Prop("timeScale", Time.timeScale);
            j.EndObject();
        }

        private static void WritePhysicsGlobals(JsonBuilder j)
        {
            j.BeginObject("physicsGlobals");
            j.Prop("gravity", Physics.gravity);
            j.Prop("bounceThreshold", Physics.bounceThreshold);
            j.Prop("defaultContactOffset", Physics.defaultContactOffset);
            j.Prop("defaultSolverIterations", Physics.defaultSolverIterations);
            j.Prop("defaultSolverVelocityIterations", Physics.defaultSolverVelocityIterations);
            j.Prop("defaultMaxDepenetrationVelocity", Physics.defaultMaxDepenetrationVelocity);
            j.Prop("sleepThreshold", Physics.sleepThreshold);
            // Nicht in der Anforderungsliste, aber prozessweit und damit fuer die Schatten-Szene
            // ebenso bindend wie die uebrigen Globals.
            j.Prop("defaultMaxAngularSpeed", Physics.defaultMaxAngularSpeed);
            j.Prop("queriesHitTriggers", Physics.queriesHitTriggers);
            j.Prop("queriesHitBackfaces", Physics.queriesHitBackfaces);
            j.Prop("autoSimulation", Physics.autoSimulation);
            j.Prop("autoSyncTransforms", Physics.autoSyncTransforms);
            j.Prop("improvedPatchFriction", Physics.improvedPatchFriction);
            j.EndObject();
        }

        private static void WriteRigidbody(JsonBuilder j, BallMovement ball)
        {
            j.BeginObject("rigidbody");

            var rb = ball.m_rigidBody;
            if (rb == null) rb = ball.GetComponent<Rigidbody>();
            if (rb == null)
            {
                j.Prop("present", false);
                j.EndObject();
                return;
            }

            j.Prop("present", true);
            j.Prop("mass", rb.mass);
            j.Prop("drag", rb.drag);
            j.Prop("angularDrag", rb.angularDrag);
            j.Prop("useGravity", rb.useGravity);
            j.Prop("isKinematic", rb.isKinematic);
            j.Prop("centerOfMass", rb.centerOfMass);
            j.Prop("worldCenterOfMass", rb.worldCenterOfMass);
            j.Prop("inertiaTensor", rb.inertiaTensor);
            // inertiaTensor ist ohne die zugehoerige Rotation nicht uebertragbar.
            j.Prop("inertiaTensorRotation", rb.inertiaTensorRotation);
            j.Prop("maxAngularVelocity", rb.maxAngularVelocity);
            j.Prop("maxDepenetrationVelocity", rb.maxDepenetrationVelocity);
            j.PropEnum("collisionDetectionMode", rb.collisionDetectionMode.ToString(), (int)rb.collisionDetectionMode);
            j.Prop("solverIterations", rb.solverIterations);
            j.Prop("solverVelocityIterations", rb.solverVelocityIterations);
            j.Prop("sleepThreshold", rb.sleepThreshold);
            j.PropEnum("interpolation", rb.interpolation.ToString(), (int)rb.interpolation);
            j.PropEnum("constraints", rb.constraints.ToString(), (int)rb.constraints);
            j.Prop("detectCollisions", rb.detectCollisions);
            j.Prop("freezeRotation", rb.freezeRotation);

            // Momentanzustand: Startbedingung fuer den Abgleich Vorhersage <-> Spiel.
            j.BeginObject("state");
            j.Prop("position", rb.position);
            j.Prop("rotation", rb.rotation);
            j.Prop("velocity", rb.velocity);
            j.Prop("angularVelocity", rb.angularVelocity);
            j.EndObject();

            j.EndObject();
        }

        private static void WriteBallColliders(JsonBuilder j, BallMovement ball)
        {
            // Der Ball traegt je nach Ballform einen Sphere- oder einen MeshCollider,
            // deshalb werden alle ausgegeben statt einen zu raten.
            j.BeginArray("ballColliders");

            Vector3 lossy = ball.transform.lossyScale;
            var cols = ball.GetComponents<Collider>();
            for (int i = 0; i < cols.Length; i++)
            {
                var col = cols[i];
                if (col == null) continue;

                j.BeginObject(null);
                j.Prop("type", ColliderTypeName(col));
                j.Prop("name", col.name);
                j.Prop("enabled", col.enabled);
                j.Prop("isTrigger", col.isTrigger);
                j.Prop("contactOffset", col.contactOffset);
                j.Prop("layer", col.gameObject.layer);
                j.Prop("layerName", LayerMask.LayerToName(col.gameObject.layer));
                j.Prop("lossyScale", lossy);

                var sphere = col.TryCast<SphereCollider>();
                if (sphere != null)
                {
                    j.Prop("radius", sphere.radius);
                    j.Prop("center", sphere.center);
                    float s = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
                    j.Prop("effectiveWorldRadius", sphere.radius * s);
                }

                var mesh = col.TryCast<MeshCollider>();
                if (mesh != null)
                {
                    j.Prop("convex", mesh.convex);
                    j.Prop("sharedMesh", mesh.sharedMesh != null ? mesh.sharedMesh.name : null);
                }

                WriteMaterial(j, "sharedMaterial", col.sharedMaterial);
                j.EndObject();
            }

            j.EndArray();
        }

        private static void WriteBallMovement(JsonBuilder j, BallMovement ball)
        {
            j.BeginObject("ballMovement");
            j.Prop("BallRadius", ball.BallRadius);

            j.Prop("dragToHitBall", ball.dragToHitBall);
            j.Prop("angDragToHitBall", ball.angDragToHitBall);
            j.Prop("dragToSlow", ball.dragToSlow);
            j.Prop("anglularDragToSlow", ball.anglularDragToSlow);
            j.Prop("angDragToSlowBall", ball.angDragToSlowBall);
            j.Prop("stoppingDragTimesangDragToSlowBall", ball.stoppingDragTimesangDragToSlowBall);

            j.Prop("m_sandDrag", ball.m_sandDrag);
            j.Prop("m_glueDrag", ball.m_glueDrag);
            j.Prop("m_environmentalDragToApply", ball.m_environmentalDragToApply);

            // Zustand der Drag-Umschaltung eine Sekunde nach dem Schlag (WaitOneSecondForDrag).
            j.Prop("initialDrag", ball.initialDrag);
            j.Prop("SecondTillDrag", ball.SecondTillDrag);
            j.Prop("SecondTillDragRunning", ball.SecondTillDragRunning);

            var maxForce = ball.m_maxForce;
            j.PropNullable("m_maxForce", maxForce != null ? (float?)maxForce.Value : null);
            var minForce = ball.minForce;
            j.PropNullable("minForce", minForce != null ? (float?)minForce.Value : null);

            j.EndObject();
        }

        private static void WritePowerCurve(JsonBuilder j, BallMovement ball)
        {
            j.BeginObject("m_PowerCurve");

            var curve = ball.m_PowerCurve;
            if (curve == null)
            {
                j.Prop("present", false);
                j.EndObject();
                return;
            }

            j.Prop("present", true);
            j.PropEnum("preWrapMode", curve.preWrapMode.ToString(), (int)curve.preWrapMode);
            j.PropEnum("postWrapMode", curve.postWrapMode.ToString(), (int)curve.postWrapMode);

            j.BeginArray("keys");
            var keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                j.BeginObject(null);
                j.Prop("time", k.m_Time);
                j.Prop("value", k.m_Value);
                j.Prop("inTangent", k.m_InTangent);
                j.Prop("outTangent", k.m_OutTangent);
                // Gewichte entscheiden mit darueber, was Evaluate() zwischen den Keys liefert.
                j.Prop("inWeight", k.m_InWeight);
                j.Prop("outWeight", k.m_OutWeight);
                j.PropEnum("weightedMode", k.m_WeightedMode.ToString(), (int)k.m_WeightedMode);
                j.EndObject();
            }
            j.EndArray();

            j.EndObject();
        }

        private static void WriteProbes(JsonBuilder j, BallMovement ball)
        {
            Vector3 origin = ball.transform.position;

            WriteProbe(j, "groundProbe", ball, origin, Vector3.down, GroundProbeDistance, "Vector3.down");

            // Blickrichtung auf die Horizontale projiziert - das ist die Richtung,
            // in der die Suche schiesst, und damit die Wand, die den Schlag begrenzt.
            string source = "Camera.main.forward (horizontal)";
            Vector3 aim = Vector3.zero;
            var cam = Camera.main;
            if (cam != null) aim = cam.transform.forward;
            aim.y = 0f;
            if (aim.sqrMagnitude < 0.0001f)
            {
                aim = ball.transform.forward;
                aim.y = 0f;
                source = "ball.transform.forward (horizontal)";
            }
            if (aim.sqrMagnitude < 0.0001f)
            {
                aim = Vector3.forward;
                source = "Vector3.forward (Rueckfall)";
            }
            aim.Normalize();

            WriteProbe(j, "wallProbe", ball, origin, aim, WallProbeDistance, source);
        }

        private static void WriteProbe(JsonBuilder j, string name, BallMovement ball,
                                       Vector3 origin, Vector3 direction, float maxDistance, string directionSource)
        {
            j.BeginObject(name);
            j.Prop("origin", origin);
            j.Prop("direction", direction);
            j.Prop("directionSource", directionSource);
            j.Prop("maxDistance", maxDistance);
            j.Prop("layerMask", ProbeLayerMask);

            Collider best = null;
            float bestDistance = float.MaxValue;
            Vector3 bestPoint = Vector3.zero;
            Vector3 bestNormal = Vector3.zero;

            // RaycastAll statt Raycast, damit die eigenen Ball-Collider uebersprungen werden
            // koennen, ohne sie dafuer abzuschalten.
            var hits = Physics.RaycastAll(origin, direction, maxDistance, ProbeLayerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                var col = hit.collider;
                if (col == null) continue;
                if (IsPartOfBall(col, ball)) continue;

                if (hit.distance < bestDistance)
                {
                    bestDistance = hit.distance;
                    best = col;
                    bestPoint = hit.point;
                    bestNormal = hit.normal;
                }
            }

            if (best == null)
            {
                j.Prop("hit", false);
                j.EndObject();
                return;
            }

            j.Prop("hit", true);
            j.Prop("distance", bestDistance);
            j.Prop("point", bestPoint);
            j.Prop("normal", bestNormal);

            j.BeginObject("collider");
            j.Prop("type", ColliderTypeName(best));
            j.Prop("name", best.name);
            j.Prop("path", HierarchyPath(best.transform));
            j.Prop("layer", best.gameObject.layer);
            j.Prop("layerName", LayerMask.LayerToName(best.gameObject.layer));
            j.Prop("isTrigger", best.isTrigger);
            j.Prop("contactOffset", best.contactOffset);
            j.EndObject();

            WriteMaterial(j, "sharedMaterial", best.sharedMaterial);
            j.EndObject();
        }

        private static void WriteMaterial(JsonBuilder j, string name, PhysicMaterial mat)
        {
            j.BeginObject(name);
            if (mat == null)
            {
                // Ohne Material rechnet PhysX mit dynamicFriction 0.6 / staticFriction 0.6 / bounciness 0.
                j.Prop("present", false);
                j.EndObject();
                return;
            }

            j.Prop("present", true);
            j.Prop("name", mat.name);
            j.Prop("dynamicFriction", mat.dynamicFriction);
            j.Prop("staticFriction", mat.staticFriction);
            j.Prop("bounciness", mat.bounciness);
            j.PropEnum("frictionCombine", mat.frictionCombine.ToString(), (int)mat.frictionCombine);
            j.PropEnum("bounceCombine", mat.bounceCombine.ToString(), (int)mat.bounceCombine);
            j.EndObject();
        }

        // ------------------------------------------------------------------ Helfer

        private static BallMovement FindOwnBall()
        {
            var balls = UnityEngine.Object.FindObjectsOfType<BallMovement>();
            if (balls == null || balls.Length == 0) return null;

            for (int i = 0; i < balls.Length; i++)
            {
                if (balls[i] != null && balls[i].IsMasterBall) return balls[i];
            }
            return balls[0];
        }

        private static bool IsPartOfBall(Collider col, BallMovement ball)
        {
            int ballId = ball.transform.GetInstanceID();
            var t = col.transform;
            while (t != null)
            {
                if (t.GetInstanceID() == ballId) return true;
                t = t.parent;
            }
            return false;
        }

        private static string ColliderTypeName(Collider col)
        {
            if (col.TryCast<SphereCollider>() != null) return "SphereCollider";
            if (col.TryCast<MeshCollider>() != null) return "MeshCollider";
            if (col.TryCast<BoxCollider>() != null) return "BoxCollider";
            if (col.TryCast<CapsuleCollider>() != null) return "CapsuleCollider";
            return "Collider";
        }

        private static string HierarchyPath(Transform t)
        {
            if (t == null) return null;

            string path = t.name;
            var cur = t.parent;
            while (cur != null)
            {
                path = cur.name + "/" + path;
                cur = cur.parent;
            }
            return path;
        }

        // ------------------------------------------------------------------ JSON

        /// <summary>
        /// Minimaler JSON-Schreiber. Bewusst handgeschrieben: erzwingt InvariantCulture
        /// (das Spiel laeuft auch unter Locales mit Dezimalkomma) und rundreisefaehige
        /// Float-Ausgabe, und macht nicht-endliche Werte als Zeichenkette sichtbar,
        /// statt ungueltiges JSON zu erzeugen.
        /// </summary>
        private sealed class JsonBuilder
        {
            private readonly StringBuilder _sb = new StringBuilder(16384);
            private readonly Stack<bool> _parents = new Stack<bool>();
            private int _depth;
            private bool _first = true;

            public void BeginObject(string name) { Open(name, '{'); }
            public void EndObject() { Close('}'); }
            public void BeginArray(string name) { Open(name, '['); }
            public void EndArray() { Close(']'); }

            public void Prop(string name, string value)
            {
                Pre(name);
                if (value == null) _sb.Append("null");
                else AppendString(value);
            }

            public void Prop(string name, bool value)
            {
                Pre(name);
                _sb.Append(value ? "true" : "false");
            }

            public void Prop(string name, int value)
            {
                Pre(name);
                _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            }

            public void Prop(string name, float value)
            {
                Pre(name);
                AppendFloat(value);
            }

            public void PropNullable(string name, float? value)
            {
                Pre(name);
                if (value.HasValue) AppendFloat(value.Value);
                else _sb.Append("null");
            }

            public void Prop(string name, Vector3 v)
            {
                Pre(name);
                _sb.Append("{ \"x\": "); AppendFloat(v.x);
                _sb.Append(", \"y\": "); AppendFloat(v.y);
                _sb.Append(", \"z\": "); AppendFloat(v.z);
                _sb.Append(" }");
            }

            public void Prop(string name, Quaternion q)
            {
                Pre(name);
                _sb.Append("{ \"x\": "); AppendFloat(q.x);
                _sb.Append(", \"y\": "); AppendFloat(q.y);
                _sb.Append(", \"z\": "); AppendFloat(q.z);
                _sb.Append(", \"w\": "); AppendFloat(q.w);
                _sb.Append(" }");
            }

            public void PropEnum(string name, string text, int value)
            {
                Pre(name);
                _sb.Append("{ \"name\": ");
                AppendString(text);
                _sb.Append(", \"value\": ").Append(value.ToString(CultureInfo.InvariantCulture)).Append(" }");
            }

            public override string ToString()
            {
                return _sb.ToString();
            }

            private void Open(string name, char brace)
            {
                Pre(name);
                _sb.Append(brace);
                _parents.Push(false);
                _first = true;
                _depth++;
            }

            private void Close(char brace)
            {
                _depth--;
                if (!_first)
                {
                    _sb.Append('\n');
                    _sb.Append(' ', _depth * 2);
                }
                _sb.Append(brace);
                _first = _parents.Count > 0 ? _parents.Pop() : false;
            }

            private void Pre(string name)
            {
                if (!_first) _sb.Append(',');
                if (_depth > 0)
                {
                    _sb.Append('\n');
                    _sb.Append(' ', _depth * 2);
                }
                _first = false;

                if (name != null)
                {
                    AppendString(name);
                    _sb.Append(": ");
                }
            }

            private void AppendFloat(float value)
            {
                if (float.IsNaN(value)) { _sb.Append("\"NaN\""); return; }
                if (float.IsPositiveInfinity(value)) { _sb.Append("\"Infinity\""); return; }
                if (float.IsNegativeInfinity(value)) { _sb.Append("\"-Infinity\""); return; }
                _sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }

            private void AppendString(string s)
            {
                _sb.Append('"');
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"':  _sb.Append("\\\""); break;
                        case '\\': _sb.Append("\\\\"); break;
                        case '\b': _sb.Append("\\b"); break;
                        case '\f': _sb.Append("\\f"); break;
                        case '\n': _sb.Append("\\n"); break;
                        case '\r': _sb.Append("\\r"); break;
                        case '\t': _sb.Append("\\t"); break;
                        default:
                            if (c < ' ') _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else _sb.Append(c);
                            break;
                    }
                }
                _sb.Append('"');
            }
        }
    }
}
