// Cold Waters MP - environment sync. C# 5 / .NET 3.5.
// Each game rolls weather, sea state, time of day and the ocean's layer, duct and ambient noise with
// UnityEngine.Random while loading. The host picks one seed per mission and sends it; both games
// seed Random with it just for those rolls, so both get the same ocean (given the same mission).
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class EnvSync
    {
        public const byte G_ENVSEED = 12;
        static int _seed;
        static bool _haveSeed;
        static readonly System.Random _rng = new System.Random();

        public static bool Active { get { return _haveSeed && Duel.Session != null && Duel.Session.State == SessionState.Ready; } }

        /// <summary>Host: new seed for the next mission, sent to the client.</summary>
        public static void HostNewSeed()
        {
            if (Duel.Session == null || !Duel.Session.IsHost) return;
            _seed = _rng.Next(1, int.MaxValue);
            _haveSeed = true;
            SendSeed();
        }

        public static void SendSeed()
        {
            if (Duel.Session == null || !Duel.Session.IsHost || !_haveSeed) return;
            int s = _seed;
            Duel.Session.SendGame(G_ENVSEED, w => w.Write(s), true);
            Duel.Log("Environment seed sent (" + s + ")");
        }

        public static bool OnMessage(byte type, BinaryReader r)
        {
            if (type != G_ENVSEED) return false;
            _seed = r.ReadInt32(); _haveSeed = true;
            Duel.Log("Environment seed received (" + _seed + ")");
            return true;
        }

        /// <summary>Seed Random for one roll site; salt keeps the sites independent.</summary>
        public static bool Begin(int salt)
        {
            if (!Active || !GameDataManager.missionMode) return false;
            SetSeed(_seed ^ salt);
            return true;
        }

        /// <summary>Back to unpredictable randomness for everything else.</summary>
        public static void End(bool began)
        {
            if (began) SetSeed(System.Environment.TickCount ^ _rng.Next());
        }

        static MethodInfo _initState;
        static PropertyInfo _seedProp;
        static void SetSeed(int s)
        {
            if (_initState == null && _seedProp == null)
            {
                _initState = typeof(Random).GetMethod("InitState", BindingFlags.Public | BindingFlags.Static, null, new System.Type[] { typeof(int) }, null);
                if (_initState == null) _seedProp = typeof(Random).GetProperty("seed", BindingFlags.Public | BindingFlags.Static);
            }
            if (_initState != null) _initState.Invoke(null, new object[] { s });
            else if (_seedProp != null) _seedProp.SetValue(null, s, null);
        }
    }

    [HarmonyPatch(typeof(LevelLoadManager), "GetEnvironment")]
    static class Patch_GetEnvironment
    {
        static void Prefix(out bool __state) { __state = EnvSync.Begin(0x1E5A); }
        static void Postfix(bool __state, int __result)
        {
            EnvSync.End(__state);
            if (__state) Duel.Log("ENV seeded: environment " + __result);
        }
        static System.Exception Finalizer(System.Exception __exception, bool __state) { EnvSync.End(__state); return __exception; }
    }

    [HarmonyPatch(typeof(LevelLoadManager), "CreateEnvironment")]
    static class Patch_CreateEnvironment
    {
        static void Prefix(out bool __state) { __state = EnvSync.Begin(0x2C3B); }
        static void Postfix(bool __state) { EnvSync.End(__state); }
        static System.Exception Finalizer(System.Exception __exception, bool __state) { EnvSync.End(__state); return __exception; }
    }

    [HarmonyPatch(typeof(SensorManager), "InitialiseOceanCharacteristics")]
    static class Patch_InitialiseOcean
    {
        static void Prefix(out bool __state) { __state = EnvSync.Begin(0x3D7C); }
        static void Postfix(SensorManager __instance, bool __state)
        {
            EnvSync.End(__state);
            if (__state)
                Duel.Log("ENV seeded: sea " + __instance.seaState + ", layer " + __instance.layerDepthInFeet.ToString("F0") + " ft str "
                    + __instance.layerStrength.ToString("F2") + ", duct " + __instance.surfaceDuctStrength.ToString("F2")
                    + ", ambient " + __instance.currentOceanAmbientNoise.ToString("F1"));
        }
        static System.Exception Finalizer(System.Exception __exception, bool __state) { EnvSync.End(__state); return __exception; }
    }
}
