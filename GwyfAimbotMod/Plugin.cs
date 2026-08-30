using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace GwyfAimbotMod
{
    [BepInPlugin("com.ammar.gwyf.aimbot", "GWYF Aimbot", "1.0.0")]
    public class Plugin : BasePlugin
    {
        // Aus dem MonoBehaviour heraus erreichbar - dort gibt es keine BasePlugin-Instanz.
        internal static ManualLogSource Logger;

        internal static ConfigEntry<KeyCode> DumpKey;
        internal static ConfigEntry<KeyCode> AutoAimKey;
        internal static ConfigEntry<bool> AutoAimAutoShoot;
        internal static ConfigEntry<bool> AutoAimSnap;

        // ---- Simulation ----
        internal static ConfigEntry<bool> UseShadowPhysics;
        internal static ConfigEntry<float> SecondsTillDrag;
        internal static ConfigEntry<float> CupRadius;
        internal static ConfigEntry<float> MaxCupEntrySpeed;
        internal static ConfigEntry<float> MaxSimSeconds;
        internal static ConfigEntry<float> ProbeSimSeconds;
        internal static ConfigEntry<float> LiveAimSimSeconds;
        internal static ConfigEntry<float> LiveAimIntervalSeconds;
        internal static ConfigEntry<float> BuildBudgetMs;
        internal static ConfigEntry<float> SearchBudgetMs;

        // ---- Suche ----
        internal static ConfigEntry<float> AngleStepDegrees;
        internal static ConfigEntry<float> AngleSpanDegrees;
        internal static ConfigEntry<int> PowerSubdivisions;

        // ---- Kalibrierung ----
        internal static ConfigEntry<float> SpeedPerCurveUnit;
        internal static ConfigEntry<int> CalibrationSamples;

        // ---- Trace ----
        internal static ConfigEntry<bool> TraceEnabled;
        internal static ConfigEntry<bool> TraceWriteJson;

        // ---- Diagnose ----
        internal static ConfigEntry<bool> DiagnosticsEnabled;

        public override void Load()
        {
            Logger = Log;

            DumpKey = Config.Bind(
                "Dump",
                "DumpKey",
                KeyCode.F9,
                "Taste fuer den einmaligen Physik-Parameter-Dump. Schreibt in das BepInEx-Log "
                + "und als JSON neben LogOutput.log.");

            AutoAimKey = Config.Bind(
                "Keys",
                "AutoAimKey",
                KeyCode.F,
                "Taste, die die Kamera perfekt auf die gefundene Loesung ausrichtet und die Kraft einstellt (halten).");

            AutoAimAutoShoot = Config.Bind(
                "Keys",
                "AutoAimAutoShoot",
                false,
                "Wenn true, wird beim Druecken der AutoAim-Taste nach dem perfekten Ausrichten und Kraftladen der Schlag sofort automatisch getriggert.");

            AutoAimSnap = Config.Bind(
                "Keys",
                "AutoAimSnap",
                true,
                "Wenn true, rastet die Kamera sofort 100% praezise auf den Zielwinkel ein. Bei false dreht sie weich.");

            UseShadowPhysics = Config.Bind(
                "Simulation",
                "UseShadowPhysics",
                true,
                "Schuesse in einer zweiten Physik-Szene mit dem PhysX-Solver des Spiels rechnen "
                + "(1:1). Bei false wird der alte, angenaeherte Eigenintegrator benutzt.");

            SecondsTillDrag = Config.Bind(
                "Simulation",
                "SecondsTillDrag",
                1.0f,
                "Sekunden nach dem Schlag, bis BallMovement von dragToHitBall auf dragToSlow "
                + "umschaltet (Coroutine WaitOneSecondForDrag). Gegen eine Trace-Aufzeichnung "
                + "nachjustierbar.");

            CupRadius = Config.Bind(
                "Simulation",
                "CupRadius",
                TrajectorySimulator.CUP_RADIUS,
                "Horizontaler Radius um HolePosition, ab dem der Ball als eingelocht gilt.");

            MaxCupEntrySpeed = Config.Bind(
                "Simulation",
                "MaxCupEntrySpeed",
                TrajectorySimulator.MAX_CUP_ENTRY_SPEED,
                "Hoechstgeschwindigkeit (m/s), mit der der Ball auf Lochhoehe noch faellt statt "
                + "darueber hinwegzurollen. Nur relevant, solange das Loch als flacher Trigger "
                + "und nicht als echte Geometrie modelliert ist.");

            MaxSimSeconds = Config.Bind(
                "Simulation",
                "MaxSimSeconds",
                12f,
                "Simulierte Spielzeit pro Schuss, bevor abgebrochen wird. Das Spiel laeuft mit "
                + "fixedDeltaTime 0.005 (200 Hz), jede Sekunde kostet also 200 Solver-Schritte.");

            ProbeSimSeconds = Config.Bind(
                "Simulation",
                "ProbeSimSeconds",
                6.0f,
                "Simulierte Zeit fuer die Probeschuesse des Winkelsweeps.");

            LiveAimSimSeconds = Config.Bind(
                "Simulation",
                "LiveAimSimSeconds",
                3f,
                "Simulierte Zeit fuer die Live-Linie beim Aufziehen. Bei FPS-Einbruch waehrend "
                + "des Aufziehens zuerst hier reduzieren.");

            LiveAimIntervalSeconds = Config.Bind(
                "Simulation",
                "LiveAimIntervalSeconds",
                0.1f,
                "Mindestabstand zwischen zwei Neuberechnungen der Live-Linie. Bei Aenderung von "
                + "Richtung oder Staerke wird sofort neu gerechnet, unabhaengig davon.");

            BuildBudgetMs = Config.Bind(
                "Simulation",
                "BuildBudgetMs",
                3.0f,
                "Zeitbudget pro Frame fuer den Aufbau der Schatten-Szene (einmal pro Bahn).");

            SearchBudgetMs = Config.Bind(
                "Simulation",
                "SearchBudgetMs",
                3.5f,
                "Zeitbudget pro Frame fuer die Loesungssuche.");

            AngleStepDegrees = Config.Bind(
                "Search",
                "AngleStepDegrees",
                4.0f,
                "Schrittweite des Winkelsweeps in Grad.");

            AngleSpanDegrees = Config.Bind(
                "Search",
                "AngleSpanDegrees",
                110f,
                "Maximaler Winkelversatz zur Richtung zum Loch in beide Richtungen.");

            PowerSubdivisions = Config.Bind(
                "Search",
                "PowerSubdivisions",
                16,
                "Stuetzstellen der Staerkesuche pro Richtung.");

            // Bewusst ein neuer Schluessel: der alte "SpeedPerCurveUnit" war ein absoluter
            // m/s-Faktor (~52). Hier steht jetzt ein dimensionsloser Restfehler auf
            // speed = force * fixedDeltaTime / mass, der bei ~1.0 liegen muss. Ein alter Wert
            // wuerde als Korrektur gelesen und die Vorhersage um Faktor 15 verreissen.
            SpeedPerCurveUnit = Config.Bind(
                "Calibration",
                "ForceModelCorrection",
                1.0f,
                "Dimensionsloser Restfehler auf das Modell speed = force * fixedDeltaTime / mass. "
                + "1.0 heisst: Modell trifft exakt. Wird bei jedem echten Schlag nachgefuehrt - "
                + "nicht von Hand setzen.");

            CalibrationSamples = Config.Bind(
                "Calibration",
                "ForceModelSamples",
                0,
                "Anzahl der bisher gemessenen Schlaege. Auf 0 setzen, um neu zu kalibrieren.");

            TraceEnabled = Config.Bind(
                "Trace",
                "Enabled",
                true,
                "Jeden echten Schlag mitschneiden und nach dem Ausrollen gegen die Vorhersage "
                + "vergleichen. Die Abweichung landet im Log und im HUD.");

            TraceWriteJson = Config.Bind(
                "Trace",
                "WriteJson",
                true,
                "Zusaetzlich zu jedem Vergleich eine JSON-Datei unter BepInEx/traces/ schreiben.");

            DiagnosticsEnabled = Config.Bind(
                "Diagnostics",
                "Enabled",
                true,
                "Schreibt pro Sitzung eine vollstaendige Log-Datei nach BepInEx/gwyf-diag/: "
                + "Physik-Umgebung, Aufbau der Schatten-Szene, jeder Schlag mit gemessenem Drag, "
                + "und wo eine Vorhersage vom echten Pfad abweicht.");

            DiagnosticsLog.Initialize(DiagnosticsEnabled.Value);
            ShotCalibration.Initialize(SpeedPerCurveUnit, CalibrationSamples);

            Log.LogInfo("Aimbot Plugin Loaded!");
            Log.LogInfo($"Physik-Parameter-Dump auf Taste [{DumpKey.Value}] (aenderbar in {Config.ConfigFilePath}).");
            Log.LogInfo(UseShadowPhysics.Value
                ? "Trajektorien werden in einer Schatten-Physik-Szene mit dem Solver des Spiels gerechnet."
                : "Schatten-Physik deaktiviert - es laeuft der angenaeherte Eigenintegrator.");

            // Register our custom MonoBehaviour with IL2CPP
            ClassInjector.RegisterTypeInIl2Cpp<AimbotBehaviour>();

            // Instantiate a GameObject and attach our behaviour
            var aimbotObj = new GameObject("AimbotObject");
            GameObject.DontDestroyOnLoad(aimbotObj);
            aimbotObj.AddComponent<AimbotBehaviour>();
            aimbotObj.hideFlags = HideFlags.HideAndDontSave;
        }
    }
}
