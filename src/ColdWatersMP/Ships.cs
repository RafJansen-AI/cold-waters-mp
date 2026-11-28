// Cold Waters MP - surface ships as player vessels (docs/SURFACE.md). C# 5 / .NET 3.5.
// DotMod's player ships are "_pl" classes typed SUBMARINE with a 40 ft test depth; the game flags them
// isPlayerSurfaceShip. Each has an AI twin ("_ai", or the plain name) typed as a real ship. In the other players'
// games a ship is shown as its twin, so their torpedoes, missiles, radar, map and ID book treat it as a ship.
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

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

    /// <summary>Towed decoys (Nixie). Only some player ships carry one (US and Australian frigates, destroyers, cruisers,
    /// Iowa), and none of their AI versions, so a ship's puppet had no decoy to seduce the other players' torpedoes.
    /// VesselBuilder builds a vessel's decoy from six class fields; while the mission loads, the AI version borrows the
    /// player ship's values, and gets its own back right after. The decoy's state then follows the network (Duel).</summary>
    public static class Decoys
    {
        class Saved { public int Cls; public int Id; public Vector3 Pos; public string Settings; public float Max, Min; public int Count; }
        static readonly List<Saved> _saved = new List<Saved>();

        public static void Lend(int twin, int player)
        {
            if (twin == player) return;
            DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
            DatabaseShipData t = db[twin], p = db[player];
            if (p.towedDecoyID < 0 || t.towedDecoyID >= 0) return;
            foreach (Saved s0 in _saved) if (s0.Cls == twin) return;
            _saved.Add(new Saved { Cls = twin, Id = t.towedDecoyID, Pos = t.towedDecoyPosition, Settings = t.towedDecoySettings, Max = t.towedDecoyMaxSpeed, Min = t.towedDecoyMinSpeed, Count = t.towedDecoyCount });
            t.towedDecoyID = p.towedDecoyID; t.towedDecoyPosition = p.towedDecoyPosition; t.towedDecoySettings = p.towedDecoySettings;
            t.towedDecoyMaxSpeed = p.towedDecoyMaxSpeed; t.towedDecoyMinSpeed = p.towedDecoyMinSpeed; t.towedDecoyCount = p.towedDecoyCount;
            Duel.Log("~Towed decoy: " + t.shipPrefabName + " borrows the decoy of " + p.shipPrefabName + " for this mission's puppet");
        }

        public static void GiveBack()
        {
            if (_saved.Count == 0) return;
            DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
            foreach (Saved s in _saved)
            {
                DatabaseShipData t = db[s.Cls];
                t.towedDecoyID = s.Id; t.towedDecoyPosition = s.Pos; t.towedDecoySettings = s.Settings;
                t.towedDecoyMaxSpeed = s.Max; t.towedDecoyMinSpeed = s.Min; t.towedDecoyCount = s.Count;
            }
            _saved.Clear();
        }

        // State byte: 1 deployed, 2 short stay, 4 active (noisemaker on), 8 damaged, 16 destroyed; 0 = none or stowed.
        public static byte State(Vessel v)
        {
            DotModTowedDecoy d = v != null && v.vesselmovement != null ? v.vesselmovement.towedDecoy : null;
            if (d == null) return 0;
            try
            {
                byte b = 0;
                if (d.GetDeployed()) b |= 1;
                if (d.isShortStay) b |= 2;
                if (d.GetDecoyActive()) b |= 4;
                if (d.isDamaged) b |= 8;
                if (d.isDestroyed) b |= 16;
                return b;
            }
            catch (System.Exception) { return 0; }
        }

        /// <summary>True while Apply changes a puppet's decoy (DotMod's own local checks may not).</summary>
        public static bool Applying;
        static float _diagAt;

        /// <summary>Make a puppet's decoy match its player's (only what differs, so DotMod's messages and checks run once).</summary>
        public static void Apply(Opponent o, byte want)
        {
            Vessel v = o.Puppet;
            DotModTowedDecoy d = v != null && v.vesselmovement != null ? v.vesselmovement.towedDecoy : null;
            if (d == null) return;
            Applying = true;
            try
            {
                bool destroyed = (want & 16) != 0, damaged = (want & 8) != 0, deployed = (want & 1) != 0, shortStay = (want & 2) != 0, active = (want & 4) != 0;
                if (destroyed != d.isDestroyed) d.SetDestroyed(destroyed);
                if (damaged != d.isDamaged) d.SetDamaged(damaged);
                if (destroyed) return;
                if (deployed != d.GetDeployed() || (deployed && shortStay != d.isShortStay)) d.SetDeployed(deployed, shortStay);
                if (deployed && active != d.GetDecoyActive()) d.SetDecoyActive(active);
                if (active && Time.time - _diagAt > 10f) { _diagAt = Time.time; Diag(o, d); }
                if (want != o.LastDecoy)
                {
                    o.LastDecoy = want;
                    Duel.Log((Duel.Opps.Count > 1 ? o.Label + "'s" : "Opponent's") + " towed decoy: " + (destroyed ? "destroyed" : !deployed ? "stowed" : (shortStay ? "short" : "long") + " tow, " + (active ? "ACTIVE" : "silent")) + (damaged ? ", damaged" : ""));
                }
            }
            catch (System.Exception) { } // before DotMod's Start has run
            finally { Applying = false; }
        }

        /// <summary>What the puppet's decoy really is in our game (duel 28 Nov: active per the owner, but our torpedoes
        /// never went for it).</summary>
        static void Diag(Opponent o, DotModTowedDecoy d)
        {
            try
            {
                Noisemaker nm = Traverse.Create(d).Field("noisemaker").GetValue<Noisemaker>();
                GameObject obj = Traverse.Create(d).Field("decoyObject").GetValue<GameObject>();
                Noisemaker[] list = UIFunctions.globaluifunctions.playerfunctions.sensormanager.noisemakerObjects;
                bool listed = nm != null && list != null && System.Array.IndexOf(list, nm) >= 0;
                float yd = nm != null ? Vector3.Distance(nm.transform.position, o.Puppet.transform.position) * GameDataManager.yardsScale : -1f;
                Duel.Log("~Puppet towed decoy: deployed " + d.GetDeployed() + ", active " + d.GetDecoyActive() + ", damaged " + d.isDamaged
                    + ", noisemaker in list " + listed + ", object shown " + (obj != null && obj.activeInHierarchy) + ", " + yd.ToString("F0") + " yd from the puppet"
                    + (nm != null ? ", depth " + ((1000f - nm.transform.position.y) * GameDataManager.unitsToFeet).ToString("F0") + " ft" : ""));
            }
            catch (System.Exception ex) { Duel.Log("~Puppet towed decoy check failed: " + ex.Message); }
        }
    }

    /// <summary>DotMod damages a towed decoy itself when the towing vessel is faster than the decoy's maximum or slower than
    /// the towed array's minimum (DotModTowedDecoy.Update). A puppet's decoy follows its player's state instead; a local
    /// check against the AI twin's limits could break it in the firer's game, where it has to seduce the torpedoes.</summary>
    [HarmonyPatch(typeof(DotModTowedDecoy), "SetDamaged")]
    static class Patch_TowedDecoy_SetDamaged
    {
        static float _loggedAt = -100f;

        static bool Prefix(bool damaged, Vessel ___vessel)
        {
            if (!damaged || !Duel.InDuel || Decoys.Applying || ___vessel == null || !Duel.IsPuppet(___vessel)) return true;
            if (Time.time - _loggedAt > 10f)
            {
                _loggedAt = Time.time;
                Duel.Log("~Kept the puppet's towed decoy working: DotMod would have damaged it locally (puppet speed "
                    + (___vessel.vesselmovement.shipSpeed.z * 10f).ToString("F1") + " kt, decoy max " + ___vessel.databaseshipdata.towedDecoyMaxSpeed + ", array min " + ___vessel.databaseshipdata.towedArrayMinSpeed + ")");
            }
            return false;
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
