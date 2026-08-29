using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace GwyfAimbotMod
{
    [BepInPlugin("com.ammar.gwyf.aimbot", "GWYF Aimbot", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public override void Load()
        {
            Log.LogInfo("Aimbot Plugin Loaded!");

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
