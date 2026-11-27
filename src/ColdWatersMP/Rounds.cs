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
            _ownFiring = false; _lastStarted = null;
            if (!Duel.InDuel || weapon == null) return;
            Vessel v = weapon.GetComponentInParent<Vessel>();
            _ownFiring = v == null || v.playercontrolled; // Player_* weapons only exist on the player's vessel
        }

        static Projectile_DepthCharge _lastStarted;

        /// <summary>Projectile_DepthCharge.Start postfix inside a Fire call: remember the round. The pool also calls Start on
        /// a reused round when it hands it out (ObjectPool.Instanciate: SendMessage("Start")), BEFORE the weapon has aimed
        /// it; sending at that first Start gave every copy the same unaimed path (Raf's test 27 Nov: tight group, wrong
        /// place, no hits). So the round is sent when the Fire call ends, as the game left it.</summary>
        public static void OnStarted(Projectile_DepthCharge dc)
        {
            if (_ownFiring && dc != null) _lastStarted = dc;
        }

        public static void EndOwnFire()
        {
            Projectile_DepthCharge dc = _lastStarted;
            _lastStarted = null;
            bool firing = _ownFiring;
            _ownFiring = false;
            if (firing && dc != null) Send(dc);
        }

        static void Send(Projectile_DepthCharge dc)
        {
            if (!Duel.InDuel || Duel.Session == null) return;
            try
            {
                Transform t = dc.transform;
                Vector3 pos = t.position; Quaternion rot = t.rotation;
                int id = dc.depthChargeID; float vel = dc.velocity, sink = dc.sinkSpeed, fuse = dc.dcExplodeDepth;
                bool depthEx = dc.isDepthExploded, contactEx = dc.isContactExploded, shell = dc.isNavalShell;
                Vector3 target = dc.targetPosition;
                int serial = ++_serial;
                // Which player we aimed at (and where we saw them): for the shell diagnostics.
                int targetId = -1; Vector3 seen = Vector3.zero;
                try
                {
                    int ti = UIFunctions.globaluifunctions.playerfunctions.currentTargetIndex;
                    Vessel[] ev = GameDataManager.enemyvesselsonlevel;
                    Opponent o = ev != null && ti >= 0 && ti < ev.Length ? Duel.ByPuppet(ev[ti]) : null;
                    if (o != null) { targetId = o.Id; seen = o.Puppet.transform.position; }
                }
                catch (System.Exception) { }
                Duel.Session.SendGame(G_ROUND, w =>
                {
                    w.Write(id);
                    w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
                    w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
                    w.Write(vel); w.Write(sink); w.Write(fuse);
                    w.Write(depthEx); w.Write(contactEx); w.Write(shell);
                    w.Write(target.x); w.Write(target.y); w.Write(target.z);
                    w.Write(targetId); w.Write(seen.x); w.Write(seen.y); w.Write(seen.z);
                    w.Write(serial);
                }, true);
                Tag(dc, serial, true, targetId);
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
            int targetId = r.ReadInt32();
            Vector3 seen = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            int serial = r.ReadInt32();
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
                // No FixedUpdate by hand here. The firer calls it inside the physics step, where the game then skips that step's
                // own FixedUpdate for the new round; a copy is made outside it, so the game also runs FixedUpdate before the first
                // physics step, and the round got its downward push twice: copies splashed 12-47 yd short, 1-4 steps early
                // (shell diagnostics 27 Nov). Left to the game, it gets one push per step, like the original.
                Tag(dc, serial, false, targetId);
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

        static int _serial;
        static void Tag(Projectile_DepthCharge dc, int serial, bool own, int targetId) { if (dc != null && dc.isNavalShell) RoundDiag.Tag(dc, serial, own, targetId, Duel.Session.LastSender); }
        public static void Reset() { RoundDiag.Reset(); _serial = 0; _copies.Clear(); _ownFiring = false; OwnFired = 0; OppFired = 0; OwnMortars = 0; OppMortars = 0; }
    }

    /// <summary>Gun shell diagnostics (27 Nov): each shell has a serial number shared by the firer's real shell and every
    /// copy, so the logs of two games can be compared shell by shell: where it first touches the water, and which hull
    /// compartment it touches. At mission start, the hit volumes (compartment colliders) of our own vessel and every
    /// puppet are listed, in the vessel's own frame.</summary>
    public static class RoundDiag
    {
        class Info { public int Serial; public bool Own; public int TargetId; public int Firer; public float T0; public bool Splashed; public int Touches; }
        static readonly System.Collections.Generic.Dictionary<int, Info> _shells = new System.Collections.Generic.Dictionary<int, Info>();
        const int MaxSerial = 40; // keep the log readable: the first 40 shells per firer

        public static void Reset() { _shells.Clear(); _dumped = false; }

        public static void Tag(Projectile_DepthCharge dc, int serial, bool own, int targetId, int sender)
        {
            if (serial > MaxSerial) { _shells.Remove(dc.GetInstanceID()); return; }
            _shells[dc.GetInstanceID()] = new Info { Serial = serial, Own = own, TargetId = targetId, Firer = own ? Duel.Session.MyId : sender, T0 = Time.time };
        }

        static string Name(Info i) { return "shell " + (i.Own ? "ours" : "from player " + i.Firer) + " #" + i.Serial; }

        /// <summary>The vessel the shell was aimed at, in this game: our own vessel, or that player's puppet.</summary>
        static Vessel TargetOf(Info i)
        {
            if (i.TargetId < 0) return null;
            if (Duel.Session != null && i.TargetId == Duel.Session.MyId) return GameDataManager.playervesselsonlevel[0];
            Opponent o = Duel.InGameById(i.TargetId);
            return o != null ? o.Puppet : null;
        }

        static string Rel(Vector3 p, Vessel v)
        {
            if (v == null) return "no target";
            Vector3 l = v.transform.InverseTransformPoint(p); // vessel frame: x starboard, y up, z ahead (local units)
            Vector3 s = v.transform.lossyScale;
            return "from target: " + (l.z * s.z * GameDataManager.yardsScale).ToString("F1") + " yd ahead, " + (l.x * s.x * GameDataManager.yardsScale).ToString("F1") + " yd starboard";
        }

        static string World(Vector3 p) { return "world " + (p.x * GameDataManager.yardsScale).ToString("F1") + ", " + (p.z * GameDataManager.yardsScale).ToString("F1") + " yd"; }

        /// <summary>FixedUpdate postfix: the first frame a tracked shell is in the water.</summary>
        public static void AfterFixedUpdate(Projectile_DepthCharge dc)
        {
            Info i;
            if (dc == null || !dc.hasSplashed || !_shells.TryGetValue(dc.GetInstanceID(), out i) || i.Splashed) return;
            i.Splashed = true;
            Vector3 p = dc.transform.position;
            Duel.Log("~SHELL " + Name(i) + " splashed after " + (Time.time - i.T0).ToString("F2") + " s, " + World(p) + ", " + Rel(p, TargetOf(i)) + (i.Touches > 0 ? " (touched a hull " + i.Touches + "x first)" : ""));
            _shells.Remove(dc.GetInstanceID());
        }

        /// <summary>Compartment.OnTriggerEnter prefix: a tracked shell touches a vessel's compartment.</summary>
        public static void OnTouch(Compartment c, Collider other)
        {
            if (c == null || other == null) return;
            Projectile_DepthCharge dc = other.GetComponentInParent<Projectile_DepthCharge>();
            Info i;
            if (dc == null || !_shells.TryGetValue(dc.GetInstanceID(), out i)) return;
            i.Touches++;
            if (i.Touches > 3) return;
            Vessel v = c.activeVessel;
            string whose = v == null ? "?" : v.playercontrolled ? "OUR vessel" : Duel.IsPuppet(v) ? Duel.ByPuppet(v).Label + "'s puppet" : v.databaseshipdata.shipPrefabName;
            Duel.Log("~SHELL " + Name(i) + " touched " + whose + " compartment '" + c.name + "' (collider " + other.GetType().Name + " " + other.name + ") after " + (Time.time - i.T0).ToString("F2") + " s, " + World(dc.transform.position) + ", " + Rel(dc.transform.position, v));
        }

        static bool _dumped;

        /// <summary>Once per mission, after placement: every compartment collider of our vessel and each puppet.</summary>
        public static void DumpHitVolumes()
        {
            if (_dumped) return;
            _dumped = true;
            Vessel own = GameDataManager.playervesselsonlevel[0];
            Dump("our vessel", own);
            foreach (Opponent o in Duel.InGame) if (o.Puppet != null) Dump(o.Label + "'s puppet", o.Puppet);
        }

        static void Dump(string who, Vessel v)
        {
            if (v == null) return;
            try
            {
                System.Text.StringBuilder b = new System.Text.StringBuilder();
                b.Append("~HITVOL ").Append(who).Append(" (").Append(v.databaseshipdata.shipPrefabName).Append(", layer ").Append(v.gameObject.layer)
                 .Append(", scale ").Append(v.transform.lossyScale.x.ToString("F3")).Append("): ");
                int n = 0;
                foreach (Compartment c in v.GetComponentsInChildren<Compartment>(true))
                {
                    foreach (Collider col in c.GetComponents<Collider>())
                    {
                        Bounds lb = LocalBounds(col, v.transform);
                        float y = GameDataManager.yardsScale;
                        b.Append(c.name).Append("[").Append(col.GetType().Name.Replace("Collider", "")).Append(col.isTrigger ? " trig" : "").Append(" L").Append(col.gameObject.layer)
                         .Append(" z ").Append((lb.min.z * y).ToString("F0")).Append("..").Append((lb.max.z * y).ToString("F0"))
                         .Append(" x ").Append((lb.min.x * y).ToString("F0")).Append("..").Append((lb.max.x * y).ToString("F0"))
                         .Append(" y ").Append((lb.min.y * y).ToString("F1")).Append("..").Append((lb.max.y * y).ToString("F1")).Append(" yd] ");
                        n++;
                    }
                }
                b.Append("(").Append(n).Append(" colliders; waterline at world y 1000, vessel y ").Append(((v.transform.position.y - 1000f) * GameDataManager.yardsScale).ToString("F1")).Append(" yd)");
                Duel.Log(b.ToString());
            }
            catch (System.Exception ex) { Duel.Log("~HITVOL dump failed for " + who + ": " + ex.Message); }
        }

        /// <summary>A collider's bounds in the vessel's frame, in world-sized units (vessel position at 0).</summary>
        static Bounds LocalBounds(Collider col, Transform vessel)
        {
            // Exact for box and mesh colliders (their own local box, moved to world); else the world AABB.
            Bounds src; Transform t = col.transform; bool local = true;
            BoxCollider bc = col as BoxCollider; MeshCollider mc = col as MeshCollider;
            if (bc != null) src = new Bounds(bc.center, bc.size);
            else if (mc != null && mc.sharedMesh != null) src = mc.sharedMesh.bounds;
            else { src = col.bounds; local = false; }
            Vector3[] corners = new Vector3[8];
            int k = 0;
            for (int ix = 0; ix < 2; ix++) for (int iy = 0; iy < 2; iy++) for (int iz = 0; iz < 2; iz++)
            {
                Vector3 c = new Vector3(ix == 0 ? src.min.x : src.max.x, iy == 0 ? src.min.y : src.max.y, iz == 0 ? src.min.z : src.max.z);
                corners[k++] = local ? t.TransformPoint(c) : c;
            }
            Quaternion inv = Quaternion.Inverse(vessel.rotation);
            Bounds lb = new Bounds(inv * (corners[0] - vessel.position), Vector3.zero);
            for (int j = 1; j < 8; j++) lb.Encapsulate(inv * (corners[j] - vessel.position));
            return lb;
        }
    }

    [HarmonyPatch(typeof(Projectile_DepthCharge), "FixedUpdate")]
    static class Patch_DepthChargeFixedUpdate_Diag
    {
        static void Postfix(Projectile_DepthCharge __instance) { if (Duel.InDuel) { try { RoundDiag.AfterFixedUpdate(__instance); } catch (System.Exception) { } } }
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
            if (!Duel.InDuel || __instance == null || _logged > 80) return;
            try
            {
                _logged++;
                Vector3 p = __instance.transform.position;
                string s = "~Rounds: " + (__instance.whichNavy == 0 ? "our" : "opponent's") + (__instance.isNavalShell ? " shell" : " mortar round") + " exploded at " + ((1000f - p.y) * GameDataManager.unitsToFeet).ToString("F0") + " ft";
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
