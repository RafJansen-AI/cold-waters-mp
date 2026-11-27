// Cold Waters MP - mortar rounds (RBU and similar) and naval gun shells in multiplayer. C# 5 / .NET 3.5.
// Both are Projectile_DepthCharge objects (shells have isNavalShell), started by hand by Player_RBU.FireRBUMortar and
// Player_Guns.FireGun. As with torpedoes, the target decides: the firer's game sends every round it fires; every other
// game starts a copy with the same position, direction, speed and fuse depth, and that copy can damage its own vessel.
// The firer's real rounds (and every copy) do no damage to puppets: each player's own game reports its damage.
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Rounds
    {
        public const byte G_ROUND = 22;

        static bool _ownFiring;   // inside FireRBUMortar / FireGun on our own vessel
        public static int OwnFired, OppFired, OwnMortars, OppMortars;

        public static void BeginOwnFire(Component weapon)
        {
            _ownFiring = false;
            if (!Duel.InDuel || weapon == null) return;
            Vessel v = weapon.GetComponentInParent<Vessel>();
            _ownFiring = v == null || v.playercontrolled; // Player_* weapons only exist on the player's vessel
        }

        public static void EndOwnFire() { _ownFiring = false; }

        /// <summary>Projectile_DepthCharge.Start postfix: a round we just fired; send it to the others.</summary>
        public static void OnStarted(Projectile_DepthCharge dc)
        {
            if (!_ownFiring || dc == null || !Duel.InDuel || Duel.Session == null) return;
            _ownFiring = false; // one round per Fire call
            try
            {
                Transform t = dc.transform;
                Vector3 pos = t.position; Quaternion rot = t.rotation;
                int id = dc.depthChargeID; float vel = dc.velocity, sink = dc.sinkSpeed, fuse = dc.dcExplodeDepth;
                bool depthEx = dc.isDepthExploded, contactEx = dc.isContactExploded, shell = dc.isNavalShell;
                Vector3 target = dc.targetPosition;
                Duel.Session.SendGame(G_ROUND, w =>
                {
                    w.Write(id);
                    w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
                    w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
                    w.Write(vel); w.Write(sink); w.Write(fuse);
                    w.Write(depthEx); w.Write(contactEx); w.Write(shell);
                    w.Write(target.x); w.Write(target.y); w.Write(target.z);
                }, true);
                OwnFired++;
                if (!shell) { OwnMortars++; Duel.Log("~Rounds: mortar round " + OwnMortars + " sent (fuse " + fuse.ToString("F1") + ")"); }
                else if (OwnFired - OwnMortars == 1 || (OwnFired - OwnMortars) % 20 == 0) Duel.Log("~Rounds: shell " + (OwnFired - OwnMortars) + " sent");
            }
            catch (System.Exception ex) { Duel.Log("~Round send failed: " + ex.Message); }
        }

        public static bool OnMessage(byte type, BinaryReader r)
        {
            if (type != G_ROUND) return false;
            int id = r.ReadInt32();
            Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Quaternion rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float vel = r.ReadSingle(), sink = r.ReadSingle(), fuse = r.ReadSingle();
            bool depthEx = r.ReadBoolean(), contactEx = r.ReadBoolean(), shell = r.ReadBoolean();
            Vector3 target = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!Duel.InDuel || Duel.Session == null || Duel.Session.LastSender == Duel.Session.MyId) return true;
            if (Duel.InGameById(Duel.Session.LastSender) == null) return true; // not in our mission
            try
            {
                DatabaseDepthChargeData[] db = UIFunctions.globaluifunctions.database.databasedepthchargedata;
                if (id < 0 || id >= db.Length || db[id] == null || db[id].depthChargeObject == null) { Duel.Log("~Ignored a round of unknown type " + id); return true; }
                // A fresh object, not one from the game's pool: copies made from reused pool objects flew short (about
                // 470 yd instead of 2,300; test 27 Nov), fresh ones flew like the firer's. DestroyMe then uses Destroy.
                GameObject go = (GameObject)Object.Instantiate(db[id].depthChargeObject, pos, Quaternion.identity);
                go.SetActive(true);
                go.transform.rotation = rot;
                Projectile_DepthCharge dc = go.GetComponent<Projectile_DepthCharge>();
                if (dc == null) { Duel.Log("~Round copy has no projectile"); return true; }
                dc.amPooled = false;
                // The round starts at the launcher, inside the firer's puppet here. The firer's own ship doesn't stop its
                // rounds, but the puppet's hull colliders did: copies fell short (~400 yd) while the real ones flew on.
                Opponent firer = Duel.InGameById(Duel.Session.LastSender);
                if (firer != null && firer.Puppet != null)
                {
                    Collider[] mine = go.GetComponentsInChildren<Collider>(true);
                    foreach (Collider pc in firer.Puppet.GetComponentsInChildren<Collider>(true))
                        foreach (Collider rc in mine)
                            if (pc != null && rc != null) Physics.IgnoreCollision(rc, pc, true);
                }
                dc.depthChargeID = id;
                dc.velocity = vel; dc.sinkSpeed = sink;
                dc.isDepthExploded = depthEx; dc.isContactExploded = contactEx; dc.isNavalShell = shell;
                dc.whichNavy = 1;
                dc.targetPosition = target;
                _copies.Add(dc.GetInstanceID());
                _startingCopy = true;
                try { dc.Start(); } finally { _startingCopy = false; }
                dc.dcExplodeDepth = fuse; // Start draws its own random fuse depth; use the firer's
                dc.FixedUpdate();
                OppFired++;
                if (!shell) { OppMortars++; Duel.Log("~Rounds: opponent mortar round " + OppMortars + " copied"); }
                else if (OppFired - OppMortars == 1 || (OppFired - OppMortars) % 20 == 0) Duel.Log("~Rounds: opponent shell " + (OppFired - OppMortars) + " copied");
            }
            catch (System.Exception ex) { Duel.Log("~Round copy failed: " + ex.Message); }
            return true;
        }

        /// <summary>A collider that belongs to a mortar round or gun shell (its blast zone or its body).</summary>
        public static bool IsRound(Collider c)
        {
            if (c == null) return false;
            return c.GetComponent<Projectile_Collider>() != null || c.GetComponent<Blastzone>() != null
                || c.GetComponentInParent<Projectile_DepthCharge>() != null;
        }

        /// <summary>Copies we started by hand. Unity calls Start once more on a fresh object (next frame), and for an
        /// enemy-side round Start sets the fuse to the PLAYER's own depth: copies blew up at the target's exact depth, or
        /// at a ship's 2-17 ft (3-player test 27 Nov). That second Start is skipped.</summary>
        static readonly System.Collections.Generic.HashSet<int> _copies = new System.Collections.Generic.HashSet<int>();
        static bool _startingCopy;
        public static bool SkipStart(Projectile_DepthCharge dc) { return dc != null && !_startingCopy && _copies.Contains(dc.GetInstanceID()); }

        public static void Reset() { _copies.Clear(); _ownFiring = false; OwnFired = 0; OppFired = 0; OwnMortars = 0; OppMortars = 0; }
    }

    [HarmonyPatch(typeof(Player_RBU), "FireRBUMortar")]
    static class Patch_FireRBUMortar
    {
        static void Prefix(Player_RBU __instance) { Rounds.BeginOwnFire(__instance); }
        static void Postfix() { Rounds.EndOwnFire(); }
    }

    [HarmonyPatch(typeof(Player_Guns), "FireGun")]
    static class Patch_FireGun
    {
        static void Prefix(Player_Guns __instance) { Rounds.BeginOwnFire(__instance); }
        static void Postfix() { Rounds.EndOwnFire(); }
    }

    /// <summary>Diagnostics: where each mortar round blows up, against our vessel and the opponents' puppets.</summary>
    [HarmonyPatch(typeof(Projectile_DepthCharge), "ExplodeDepthCharge")]
    internal static class Patch_DepthChargeExplode
    {
        static int _logged;
        static void Prefix(Projectile_DepthCharge __instance)
        {
            if (!Duel.InDuel || __instance == null || __instance.isNavalShell || _logged > 60) return;
            try
            {
                _logged++;
                Vector3 p = __instance.transform.position;
                string s = "~Rounds: " + (__instance.whichNavy == 0 ? "our" : "opponent's") + " mortar round exploded at " + ((1000f - p.y) * GameDataManager.unitsToFeet).ToString("F0") + " ft";
                Vessel own = GameDataManager.playervesselsonlevel != null && GameDataManager.playervesselsonlevel.Length > 0 ? GameDataManager.playervesselsonlevel[0] : null;
                if (own != null) s += " | our vessel " + Describe(p, own);
                foreach (Opponent o in Duel.InGame) if (o.Puppet != null) s += " | " + o.Label + " " + Describe(p, o.Puppet);
                Duel.Log(s);
            }
            catch (System.Exception) { }
        }

        internal static string Describe(Vector3 p, Vessel v)
        {
            Vector3 d = v.transform.position - p;
            float depth = (1000f - v.transform.position.y) * GameDataManager.unitsToFeet;
            return (new Vector2(d.x, d.z).magnitude * GameDataManager.yardsScale).ToString("F0") + " yd off, at " + depth.ToString("F0") + " ft";
        }
    }

    [HarmonyPatch(typeof(Projectile_DepthCharge), "Start")]
    static class Patch_DepthChargeStart
    {
        static bool Prefix(Projectile_DepthCharge __instance) { return !Rounds.SkipStart(__instance); }
        static void Postfix(Projectile_DepthCharge __instance) { Rounds.OnStarted(__instance); }
    }

    /// <summary>Diagnostics: where each gun shell ends (hit or splash), ours and copies, against every vessel.</summary>
    [HarmonyPatch(typeof(Projectile_DepthCharge), "DestroyMe")]
    static class Patch_DepthChargeDestroyMe
    {
        static int _logged;
        static void Prefix(Projectile_DepthCharge __instance)
        {
            if (!Duel.InDuel || __instance == null || !__instance.isNavalShell || _logged > 60) return;
            try
            {
                _logged++;
                Vector3 p = __instance.transform.position;
                string s = "~Rounds: " + (__instance.whichNavy == 0 ? "our" : "opponent's") + " shell ended at height " + ((p.y - 1000f) * GameDataManager.unitsToFeet).ToString("F0") + " ft";
                Vessel own = GameDataManager.playervesselsonlevel != null && GameDataManager.playervesselsonlevel.Length > 0 ? GameDataManager.playervesselsonlevel[0] : null;
                if (own != null) s += " | our vessel " + Patch_DepthChargeExplode.Describe(p, own);
                foreach (Opponent o in Duel.InGame) if (o.Puppet != null) s += " | " + o.Label + " " + Patch_DepthChargeExplode.Describe(p, o.Puppet);
                Duel.Log(s);
            }
            catch (System.Exception) { }
        }
    }
}
