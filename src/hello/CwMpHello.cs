// Cold Waters MP - step 0 hello-world plugin (BepInEx 5 x86 + Harmony). C# 5 compatible.
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[BepInPlugin("cwmp.hello", "Cold Waters MP Hello", "0.0.1")]
public class CwMpHelloPlugin : BaseUnityPlugin
{
    internal static ManualLogSource L;

    void Awake()
    {
        L = Logger;
        L.LogInfo("CWMP hello loaded. Unity " + Application.unityVersion
            + ", Assembly-CSharp at " + typeof(LevelLoadManager).Assembly.Location);
        new Harmony("cwmp.hello").PatchAll(typeof(CwMpHelloPlugin).Assembly);
        L.LogInfo("CWMP patches applied.");
    }
}

[HarmonyPatch(typeof(LevelLoadManager), "LoadLevel")]
static class Patch_LoadLevel
{
    static void Postfix()
    {
        CwMpHelloPlugin.L.LogInfo("CWMP LoadLevel done.");
    }
}

[HarmonyPatch(typeof(LevelLoadManager), "SetupCombat")]
static class Patch_SetupCombat
{
    static void Postfix()
    {
        var p = GameDataManager.playervesselsonlevel;
        var e = GameDataManager.enemyvesselsonlevel;
        CwMpHelloPlugin.L.LogInfo("CWMP SetupCombat done. playerVessels=" + (p == null ? -1 : p.Length)
            + " enemyVessels=" + (e == null ? -1 : e.Length));
    }
}

[HarmonyPatch(typeof(LevelLoadManager), "SpawnPlayerVessel")]
static class Patch_SpawnPlayerVessel
{
    static void Postfix(int vesselNumber, int shipClass, GameObject __result)
    {
        CwMpHelloPlugin.L.LogInfo("CWMP SpawnPlayerVessel #" + vesselNumber + " class=" + shipClass
            + " go=" + (__result == null ? "null" : __result.name));
    }
}

[HarmonyPatch(typeof(PlayerFunctions), "SetTimeCompression")]
static class Patch_SetTimeCompression
{
    static void Postfix()
    {
        CwMpHelloPlugin.L.LogInfo("CWMP SetTimeCompression -> timeScale=" + Time.timeScale
            + " fixedDeltaTime=" + Time.fixedDeltaTime);
    }
}
