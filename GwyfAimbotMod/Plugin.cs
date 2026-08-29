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

        public override void Load()
        {
            Logger = Log;

            DumpKey = Config.Bind(
                "Dump",
                "DumpKey",
                KeyCode.F9,
                "Taste fuer den einmaligen Physik-Parameter-Dump. Schreibt in das BepInEx-Log "
                + "und als JSON neben LogOutput.log.");

            Log.LogInfo("Aimbot Plugin Loaded!");
            Log.LogInfo($"Physik-Parameter-Dump auf Taste [{DumpKey.Value}] (aenderbar in {Config.ConfigFilePath}).");

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
