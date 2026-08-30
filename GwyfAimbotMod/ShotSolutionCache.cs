using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using UnityEngine;

namespace GwyfAimbotMod
{
    public class CachedPoint
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public CachedPoint() { }

        public CachedPoint(Vector3 v)
        {
            X = (float)Math.Round(v.x, 4);
            Y = (float)Math.Round(v.y, 4);
            Z = (float)Math.Round(v.z, 4);
        }

        public Vector3 ToVector3() => new Vector3(X, Y, Z);
    }

    public class CachedSolution
    {
        public CachedPoint BallStartPosition { get; set; }
        public CachedPoint Direction { get; set; }
        public float Power { get; set; }
        public float MinDistance { get; set; }
        public bool IsHoleInOne { get; set; }
        public bool IsLiveVerified { get; set; }
        public int SuccessCount { get; set; } = 1;
        public List<CachedPoint> Path { get; set; } = new List<CachedPoint>();
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsValid
        {
            get
            {
                if (Power < 400f) return false;
                if (Direction == null) return false;
                if (Direction.X * Direction.X + Direction.Z * Direction.Z < 0.0001f) return false;
                if (Path == null || Path.Count < 2) return false;
                if (IsHoleInOne && MinDistance > 0.35f) return false;
                return true;
            }
        }

        public Vector3[] GetPathArray()
        {
            if (Path == null || Path.Count == 0) return Array.Empty<Vector3>();
            var arr = new Vector3[Path.Count];
            for (int i = 0; i < Path.Count; i++)
            {
                arr[i] = Path[i].ToVector3();
            }
            return arr;
        }
    }

    public class BlacklistEntry
    {
        public CachedPoint BallStartPosition { get; set; }
        public CachedPoint Direction { get; set; }
        public float Power { get; set; }
        public float FinalDistance { get; set; }
        public string Reason { get; set; } = "Missed";
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsValid
        {
            get
            {
                if (Power < 400f) return false;
                if (Direction == null) return false;
                if (Direction.X * Direction.X + Direction.Z * Direction.Z < 0.0001f) return false;
                return true;
            }
        }
    }

    public class HoleCacheData
    {
        public string SceneName { get; set; } = "";
        public int HoleNumber { get; set; }
        public List<CachedSolution> Solutions { get; set; } = new List<CachedSolution>();
        public List<BlacklistEntry> Blacklist { get; set; } = new List<BlacklistEntry>();
    }

    public class CacheRoot
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, HoleCacheData> Holes { get; set; } = new Dictionary<string, HoleCacheData>();
    }

    public static class ShotSolutionCache
    {
        private static CacheRoot _cache = new CacheRoot();
        private static string _cacheFilePath;
        private static bool _initialized = false;
        private static readonly object _lock = new object();

        public static void Initialize(string basePath)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(basePath))
                    {
                        basePath = Path.Combine(Paths.ConfigPath, "GwyfAimbotMod");
                    }
                    if (!Directory.Exists(basePath))
                    {
                        Directory.CreateDirectory(basePath);
                    }

                    _cacheFilePath = Path.Combine(basePath, "GwyfAimbot_Cache.json");
                    Load();
                    _initialized = true;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError("ShotSolutionCache Initialize failed: " + ex.Message);
                }
            }
        }

        private static string GetHoleKey(string sceneName, int holeNumber)
        {
            return $"{sceneName}#{holeNumber}";
        }

        public static void Load()
        {
            lock (_lock)
            {
                string[] possiblePaths = new[]
                {
                    _cacheFilePath,
                    Path.Combine(Paths.ConfigPath, "GwyfAimbotMod", "GwyfAimbot_Cache.json"),
                    Path.Combine(Paths.PluginPath, "GwyfAimbot_Cache.json"),
                    Path.Combine(Paths.PluginPath, "GwyfAimbotMod", "GwyfAimbot_Cache.json"),
                    Path.Combine(Paths.PluginPath, "data", "GwyfAimbot_Cache.json")
                };

                foreach (var path in possiblePaths)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            string json = File.ReadAllText(path);
                            var loaded = JsonSerializer.Deserialize<CacheRoot>(json);
                            if (loaded != null && loaded.Holes != null && loaded.Holes.Count > 0)
                            {
                                _cache = loaded;
                                Plugin.Logger.LogInfo($"ShotSolutionCache: Loaded {_cache.Holes.Count} hole cache entries from {path}.");
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogWarning($"ShotSolutionCache: Error reading cache from {path}: {ex.Message}");
                    }
                }

                _cache = new CacheRoot();
            }
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(_cacheFilePath)) return;

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    string json = JsonSerializer.Serialize(_cache, options);
                    File.WriteAllText(_cacheFilePath, json);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError("ShotSolutionCache: Failed to save cache: " + ex.Message);
                }
            }
        }

        public static bool TryGetSolution(string sceneName, int holeNumber, Vector3 ballPos, out CachedSolution bestSolution)
        {
            bestSolution = null;
            if (!Plugin.UseSolutionCache.Value) return false;

            lock (_lock)
            {
                if (!_initialized || _cache == null) return false;

                string key = GetHoleKey(sceneName, holeNumber);
                if (!_cache.Holes.TryGetValue(key, out var holeData) || holeData.Solutions == null || holeData.Solutions.Count == 0)
                {
                    return false;
                }

                CachedSolution best = null;
                float bestScore = float.MaxValue;

                foreach (var sol in holeData.Solutions)
                {
                    if (sol == null || !sol.IsValid) continue;

                    Vector3 solStart = sol.BallStartPosition.ToVector3();
                    float horizDist = Vector2.Distance(new Vector2(ballPos.x, ballPos.z), new Vector2(solStart.x, solStart.z));
                    if (horizDist > 0.35f) continue; // Must match the ball's tee/lie position horizontally

                    // Score calculation: Verified Live HIO is top priority (-1000), simulated HIO (-500), then closer approach
                    float score = horizDist * 2f;
                    if (sol.IsLiveVerified) score -= 1000f;
                    else if (sol.IsHoleInOne) score -= 500f;
                    else score += sol.MinDistance * 10f;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = sol;
                    }
                }

                if (best != null)
                {
                    bestSolution = best;
                    return true;
                }

                return false;
            }
        }

        public static void RecordSolution(
            string sceneName,
            int holeNumber,
            Vector3 ballPos,
            Vector3 dir,
            float power,
            Vector3[] path,
            float minDist,
            bool isHoleInOne,
            bool isLiveVerified)
        {
            if (!Plugin.UseSolutionCache.Value) return;
            if (power < 400f || dir.sqrMagnitude < 0.001f || path == null || path.Length < 2) return;

            lock (_lock)
            {
                if (!_initialized || _cache == null) return;

                string key = GetHoleKey(sceneName, holeNumber);
                if (!_cache.Holes.TryGetValue(key, out var holeData))
                {
                    holeData = new HoleCacheData
                    {
                        SceneName = sceneName,
                        HoleNumber = holeNumber
                    };
                    _cache.Holes[key] = holeData;
                }

                dir.Normalize();

                // Check if matching solution already exists
                CachedSolution existing = null;
                foreach (var s in holeData.Solutions)
                {
                    if (s == null) continue;
                    Vector3 sStart = s.BallStartPosition.ToVector3();
                    float horizDist = Vector2.Distance(new Vector2(ballPos.x, ballPos.z), new Vector2(sStart.x, sStart.z));

                    if (horizDist < 0.35f
                        && Vector3.Angle(dir, s.Direction.ToVector3()) < 2.0f
                        && Mathf.Abs(power - s.Power) / Mathf.Max(1f, s.Power) < 0.04f)
                    {
                        existing = s;
                        break;
                    }
                }

                if (existing != null)
                {
                    if (isLiveVerified)
                    {
                        existing.IsLiveVerified = true;
                        existing.SuccessCount++;
                    }
                    if (isHoleInOne) existing.IsHoleInOne = true;
                    if (minDist < existing.MinDistance) existing.MinDistance = minDist;
                    if (path != null && path.Length > 0)
                    {
                        existing.Path.Clear();
                        for (int i = 0; i < path.Length; i++) existing.Path.Add(new CachedPoint(path[i]));
                    }
                }
                else
                {
                    var newSol = new CachedSolution
                    {
                        BallStartPosition = new CachedPoint(ballPos),
                        Direction = new CachedPoint(dir),
                        Power = power,
                        MinDistance = minDist,
                        IsHoleInOne = isHoleInOne,
                        IsLiveVerified = isLiveVerified,
                        SuccessCount = 1
                    };
                    if (path != null && path.Length > 0)
                    {
                        for (int i = 0; i < path.Length; i++) newSol.Path.Add(new CachedPoint(path[i]));
                    }
                    holeData.Solutions.Add(newSol);
                }

                // If this is a Hole-in-One, remove any conflicting blacklist entry around this line
                if (isHoleInOne && holeData.Blacklist != null)
                {
                    holeData.Blacklist.RemoveAll(b =>
                    {
                        if (b == null) return false;
                        Vector3 bStart = b.BallStartPosition.ToVector3();
                        float hDist = Vector2.Distance(new Vector2(ballPos.x, ballPos.z), new Vector2(bStart.x, bStart.z));
                        return hDist < 0.35f
                            && Vector3.Angle(dir, b.Direction.ToVector3()) < 2.0f
                            && Mathf.Abs(power - b.Power) / Mathf.Max(1f, b.Power) < 0.04f;
                    });
                }

                Save();

                Plugin.Logger.LogInfo($"ShotSolutionCache: Recorded {(isLiveVerified ? "VERIFIED LIVE HIO" : (isHoleInOne ? "HIO" : "Approach"))} on {sceneName} #{holeNumber} (Power: {power:F0})");
            }
        }

        public static void RecordFailedShot(
            string sceneName,
            int holeNumber,
            Vector3 ballPos,
            Vector3 dir,
            float power,
            float finalDist,
            string reason)
        {
            if (!Plugin.UseBlacklist.Value) return;
            if (power < 400f || dir.sqrMagnitude < 0.001f) return;

            lock (_lock)
            {
                if (!_initialized || _cache == null) return;

                string key = GetHoleKey(sceneName, holeNumber);
                if (!_cache.Holes.TryGetValue(key, out var holeData))
                {
                    holeData = new HoleCacheData
                    {
                        SceneName = sceneName,
                        HoleNumber = holeNumber
                    };
                    _cache.Holes[key] = holeData;
                }

                dir.Normalize();

                // Check if already in blacklist
                bool exists = false;
                foreach (var b in holeData.Blacklist)
                {
                    if (b == null) continue;
                    Vector3 bStart = b.BallStartPosition.ToVector3();
                    float horizDist = Vector2.Distance(new Vector2(ballPos.x, ballPos.z), new Vector2(bStart.x, bStart.z));

                    if (horizDist < 0.35f
                        && Vector3.Angle(dir, b.Direction.ToVector3()) < 1.25f
                        && Mathf.Abs(power - b.Power) / Mathf.Max(1f, b.Power) < 0.035f)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    holeData.Blacklist.Add(new BlacklistEntry
                    {
                        BallStartPosition = new CachedPoint(ballPos),
                        Direction = new CachedPoint(dir),
                        Power = power,
                        FinalDistance = finalDist,
                        Reason = reason
                    });

                    // If a non-verified solution exists with this exact failing angle & power, remove it
                    if (holeData.Solutions != null)
                    {
                        holeData.Solutions.RemoveAll(s =>
                        {
                            if (s == null) return false;
                            Vector3 sStart = s.BallStartPosition.ToVector3();
                            float hDist = Vector2.Distance(new Vector2(ballPos.x, ballPos.z), new Vector2(sStart.x, sStart.z));
                            return !s.IsLiveVerified
                                && hDist < 0.35f
                                && Vector3.Angle(dir, s.Direction.ToVector3()) < 1.25f
                                && Mathf.Abs(power - s.Power) / Mathf.Max(1f, s.Power) < 0.035f;
                        });
                    }

                    Save();

                    Plugin.Logger.LogInfo($"ShotSolutionCache: Blacklisted failed shot on {sceneName} #{holeNumber} (Power: {power:F0}, Rest: {finalDist:F2}m, Reason: {reason})");
                }
            }
        }

        public static bool IsBlacklisted(string sceneName, int holeNumber, Vector3 ballPos, Vector3 dir, float power)
        {
            if (!Plugin.UseBlacklist.Value) return false;

            lock (_lock)
            {
                if (!_initialized || _cache == null) return false;

                string key = GetHoleKey(sceneName, holeNumber);
                if (!_cache.Holes.TryGetValue(key, out var holeData) || holeData.Blacklist == null || holeData.Blacklist.Count == 0)
                {
                    return false;
                }

                dir.Normalize();

                foreach (var b in holeData.Blacklist)
                {
                    if (b == null || !b.IsValid) continue;
                    Vector3 bStart = b.BallStartPosition.ToVector3();
                    float horizDist = Vector2.Distance(new Vector2(ballPos.x, ballPos.z), new Vector2(bStart.x, bStart.z));

                    if (horizDist < 0.40f
                        && Vector3.Angle(dir, b.Direction.ToVector3()) < 1.25f
                        && Mathf.Abs(power - b.Power) / Mathf.Max(1f, b.Power) < 0.035f)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
