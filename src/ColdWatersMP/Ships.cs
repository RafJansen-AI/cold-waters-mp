// Cold Waters MP - surface ships as player vessels (docs/SURFACE.md). C# 5 / .NET 3.5.
// DotMod's player ships are "_pl" classes typed SUBMARINE with a 40 ft test depth; the game flags them
// isPlayerSurfaceShip. Each has an AI twin ("_ai", or the plain name) typed as a real ship. In the other players'
// games a ship is shown as its twin, so their torpedoes, missiles, radar, map and ID book treat it as a ship.
using System.Collections.Generic;
using HarmonyLib;

namespace ColdWatersMP
{
    public static class Ships
    {
        static Dictionary<string, int> _byName;

        /// <summary>A DotMod player ship class name (usn_84_dd_spruance_pl).</summary>
        public static bool IsPlayerShip(string prefab) { return prefab != null && prefab.EndsWith("_pl"); }

        public static bool IsPlayerShip(int cls)
        {
            DatabaseShipData d = Data(cls);
            return d != null && IsPlayerShip(d.shipPrefabName);
        }

        static DatabaseShipData Data(int cls)
        {
            try
            {
                DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
                return cls >= 0 && cls < db.Length ? db[cls] : null;
            }
            catch (System.Exception) { return null; }
        }

        static int ByName(string name)
        {
            if (_byName == null)
            {
                _byName = new Dictionary<string, int>();
                DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
                for (int i = 0; i < db.Length; i++)
                    if (db[i] != null && db[i].shipPrefabName != null && !_byName.ContainsKey(db[i].shipPrefabName)) _byName[db[i].shipPrefabName] = i;
            }
            int id;
            return _byName.TryGetValue(name, out id) ? id : -1;
        }

        /// <summary>The class to spawn for another player's vessel: the AI twin for a player ship, else the class itself.</summary>
        public static int PuppetClass(int cls)
        {
            DatabaseShipData d = Data(cls);
            if (d == null || !IsPlayerShip(d.shipPrefabName)) return cls;
            string stem = d.shipPrefabName.Substring(0, d.shipPrefabName.Length - 3);
            int twin = ByName(stem + "_ai");
            if (twin < 0) twin = ByName(stem);
            if (twin < 0) { Duel.Log("~WARNING: no AI twin for " + d.shipPrefabName + "; the puppet uses the player class"); return cls; }
            return twin;
        }

        /// <summary>The AI twins of every selectable player ship (for the ID book).</summary>
        public static List<int> Twins(int[] playable)
        {
            List<int> r = new List<int>();
            if (playable == null) return r;
            foreach (int id in playable)
            {
                if (!IsPlayerShip(id)) continue;
                int t = PuppetClass(id);
                if (t != id && !r.Contains(t)) r.Add(t);
            }
            return r;
        }

        /// <summary>Noise difference (dB, after the game's compression) between a player's real class and the class
        /// their puppet was spawned as. The twin of a Spruance is 175 dB self noise, the player's ship 160.</summary>
        public static float NoiseCorrection(int realCls, Vessel puppet, float compression)
        {
            DatabaseShipData real = Data(realCls);
            if (real == null || puppet == null || puppet.databaseshipdata == null || real == puppet.databaseshipdata) return 0f;
            float diff = real.selfnoise - puppet.databaseshipdata.selfnoise;
            return compression != 0f ? diff / compression : diff;
        }
    }

    /// <summary>LoadLevel recounts the mission's helicopters from the enemy ships' aircraft (GetHelicopterNumbers). A
    /// player ship's puppet is an AI ship with helicopters, which would fly as AI hunters in the other games. Multiplayer
    /// missions have no AI aircraft.</summary>
    [HarmonyPatch(typeof(LevelLoadData), "GetHelicopterNumbers")]
    static class Patch_GetHelicopterNumbers
    {
        static void Postfix(LevelLoadData __instance)
        {
            if (!Duel.InDuel) return;
            try
            {
                LevelLoadData d = UIFunctions.globaluifunctions.levelloadmanager.levelloaddata;
                if (d != null && d.numberOfHelicopters != 0) { Duel.Log("~No AI helicopters in multiplayer (the mission's ships carried " + d.numberOfHelicopters + ")"); d.numberOfHelicopters = 0; }
                if (__instance != null) __instance.numberOfHelicopters = 0;
            }
            catch (System.Exception ex) { Duel.Log("~Helicopter count not changed: " + ex.Message); }
        }
    }
}
