// Cold Waters MP - neutral traffic (merchants, fishing boats, whales) in duels. C# 5 / .NET 3.5.
// The host rolls the list with the game's own traffic maps and sends it with the match; both games spawn the same
// vessels after the opponent and the decoy ghosts (enemy indexes 6..14). The host's game runs their AI and decides
// hits and damage; it streams position, course, speed, damage and sinking. The client shows them with AI off.
// Sinking a neutral never counts in the result. World objects (oil rigs) stay off.
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Ambient
    {
        public const byte G_AMB_STATE = 18, G_AMB_HIT = 19;
        public enum Level { Off = 0, Random = 1, Low = 2, Medium = 3, High = 4 }
        public static Level HostLevel = Level.Random;
        public static bool ForceWhales;                     // dev setting        // host setting (F6)
        public static string LevelText(Level l)
        {
            switch (l)
            {
                case Level.Off: return "No neutral traffic";
                case Level.Random: return "Random: none, some or a lot";
                case Level.Low: return "Low: about what the map gives";
                case Level.Medium: return "Medium";
                default: return "High";
            }
        }
        public const int MaxNeutrals = 9;                   // 15 vessels - opponent - 5 ghosts

        class Entry { public int Cls; public string Flag; }
        static readonly List<Entry> _list = new List<Entry>(); // this mission's list (rolled by the host or received)
        static readonly List<int> _pool = new List<int>();     // every class that could have spawned: all go in the ID book
        public static int Count { get { return _list.Count; } }
        public static int Start { get { return Duel.NeutralStart; } }

        class Remote { public Vector3 Pos; public float Yaw, SpeedZ, Damage, T, LocalT; public bool Sinking, Have; }
        static Remote[] _remote = new Remote[0];
        static float _sendTimer;
        static readonly HashSet<Torpedo> _hitNeutral = new HashSet<Torpedo>(); // host: opponent torpedo copies that hit a neutral

        static Session S { get { return Duel.Session; } }

        public static string Describe()
        {
            if (_list.Count == 0) return "none";
            DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
            string s = "";
            foreach (Entry e in _list) s += (s.Length > 0 ? ", " : "") + (e.Cls >= 0 && e.Cls < db.Length ? db[e.Cls].shipclass : e.Cls.ToString());
            return s;
        }

        // ---------- the list ----------

        /// <summary>Host, at mission start: roll the traffic the way the game does, from its traffic maps.</summary>
        public static void HostRoll(LevelLoadData d)
        {
            _list.Clear(); _pool.Clear();
            if (HostLevel == Level.Off || d == null) { Duel.Log("Neutral traffic: off"); return; }
            // The ID book always lists every class this mission could spawn, so an empty sea isn't given away by the book.
            List<int>[] groups = { d.neutralMerchantIDs1, d.neutralMerchantIDs2, d.neutralMerchantIDs3, d.neutralMerchantIDs4,
                d.neutralFishingIDs1, d.neutralFishingIDs2, d.neutralFishingIDs3, d.neutralFishingIDs4 };
            DatabaseShipData[] sdb = UIFunctions.globaluifunctions.database.databaseshipdata;
            foreach (List<int> g in groups) if (g != null) foreach (int c in g) if (c >= 0 && c < sdb.Length && !_pool.Contains(c)) _pool.Add(c);
            for (int i = 0; i < sdb.Length; i++) if (sdb[i] != null && sdb[i].shipType == "BIOLOGIC" && !_pool.Contains(i)) _pool.Add(i);
            Level lv = HostLevel;
            if (lv == Level.Random) lv = (Level)Random.Range(0, 5); // Off 20%, Low 40%, Medium 20%, High 20%
            if (lv == Level.Random) lv = Level.Low;
            if (lv == Level.Off) { Duel.Log("Neutral traffic (Random): none this time"); return; }
            float mult = lv == Level.High ? 3f : lv == Level.Medium ? 1.75f : 1f;
            CampaignManager cm = UIFunctions.globaluifunctions.campaignmanager;
            Texture2D fish = cm != null ? cm.mapFishingTraffic : null, merch = cm != null ? cm.mapMerchantTraffic : null;
            int px = Mathf.FloorToInt(d.mapPosition.x / 16f), py = Mathf.FloorToInt(d.mapPosition.y / 16f);
            Color mp = merch != null ? merch.GetPixel(px, py) : new Color(0f, 0f, 0f, 0f);
            Color fp = fish != null ? fish.GetPixel(px, py) : new Color(0f, 0f, 0f, 0f);
            // A floor, so maps without traffic data (some single missions) still get some.
            float floor = lv == Level.High ? 0.5f : lv == Level.Medium ? 0.3f : 0.1f;
            Group(d, Mathf.Max(mp.r, floor), d.neutralMerchantIDs1, d.neutralMerchantFlags1, mult);
            Group(d, Mathf.Max(mp.g, floor), d.neutralMerchantIDs2, d.neutralMerchantFlags2, mult);
            Group(d, Mathf.Max(mp.b, floor), d.neutralMerchantIDs3, d.neutralMerchantFlags3, mult);
            Group(d, Mathf.Max(mp.a, floor), d.neutralMerchantIDs4, d.neutralMerchantFlags4, mult);
            Group(d, Mathf.Max(fp.r, floor), d.neutralFishingIDs1, d.neutralFishingFlags1, mult);
            Group(d, Mathf.Max(fp.g, floor), d.neutralFishingIDs2, d.neutralFishingFlags2, mult);
            Group(d, Mathf.Max(fp.b, floor), d.neutralFishingIDs3, d.neutralFishingFlags3, mult);
            Group(d, Mathf.Max(fp.a, floor), d.neutralFishingIDs4, d.neutralFishingFlags4, mult);
            // Shuffle so a long list doesn't always cut the same groups, then leave room for whales.
            for (int i = _list.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); Entry t = _list[i]; _list[i] = _list[j]; _list[j] = t; }
            float whaleX = lv == Level.High ? 4.5f : lv == Level.Medium ? 3f : 2f; // a bit more than the base game: 20/30/45% at its 0.1
            if (ForceWhales || Random.value < d.whaleProbability * whaleX)
            {
                List<int> whales = new List<int>();
                DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
                for (int i = 0; i < db.Length; i++) if (db[i] != null && db[i].shipType == "BIOLOGIC") whales.Add(i);
                int n = whales.Count > 0 ? Random.Range(1, 3) : 0;
                while (_list.Count > MaxNeutrals - n) _list.RemoveAt(_list.Count - 1);
                for (int k = 0; k < n; k++) _list.Add(new Entry { Cls = whales[Random.Range(0, whales.Count)], Flag = "" });
            }
            while (_list.Count > MaxNeutrals) _list.RemoveAt(_list.Count - 1);
            Duel.Log("Neutral traffic (" + HostLevel + (HostLevel == Level.Random ? " -> " + lv : "") + (merch == null && fish == null ? ", no traffic map here" : "") + "): " + _list.Count + " - " + Describe());
        }

        static void Group(LevelLoadData d, float threshold, List<int> ids, List<string> flags, float mult)
        {
            if (threshold <= 0f || ids == null || ids.Count == 0) return;
            float p1 = d.neutralProb1 > 0f ? d.neutralProb1 : 1f, p2 = d.neutralProb2 > 0f ? d.neutralProb2 : 1f;
            if (Random.value * p1 < threshold * mult) Add(ids, flags);
            if (Random.value * p2 < threshold * mult) Add(ids, flags);
        }

        static void Add(List<int> ids, List<string> flags)
        {
            _list.Add(new Entry { Cls = ids[Random.Range(0, ids.Count)], Flag = flags != null && flags.Count > 0 ? flags[Random.Range(0, flags.Count)] : "" });
        }

        public static void Write(BinaryWriter w)
        {
            w.Write(_list.Count);
            foreach (Entry e in _list) { w.Write(e.Cls); w.Write(e.Flag ?? ""); }
            w.Write(_pool.Count);
            foreach (int c in _pool) w.Write(c);
        }

        /// <summary>Client: the host's list, read with the match message.</summary>
        public static void Read(BinaryReader r, bool keep)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxNeutrals) throw new IOException("bad traffic list");
            List<Entry> l = new List<Entry>();
            int max = UIFunctions.globaluifunctions.database.databaseshipdata.Length;
            for (int i = 0; i < n; i++)
            {
                Entry e = new Entry { Cls = r.ReadInt32(), Flag = r.ReadString() };
                if (e.Cls >= 0 && e.Cls < max && e.Flag.IndexOf("..") < 0) l.Add(e); // flags are Resources paths
            }
            int pn = r.ReadInt32();
            if (pn < 0 || pn > 256) throw new IOException("bad traffic pool");
            List<int> pool = new List<int>();
            for (int i = 0; i < pn; i++) { int c = r.ReadInt32(); if (c >= 0 && c < max && !pool.Contains(c)) pool.Add(c); }
            if (!keep) return;
            _list.Clear(); _list.AddRange(l);
            _pool.Clear(); _pool.AddRange(pool);
        }

        /// <summary>The neutral classes, for the ID book.</summary>
        public static List<int> Classes()
        {
            List<int> c = new List<int>(_pool);
            foreach (Entry e in _list) if (!c.Contains(e.Cls)) c.Add(e.Cls);
            return c;
        }

        /// <summary>LevelLoadData.AssignNeutralVessels in a duel: append our list instead of the game's own roll.</summary>
        public static void Assign(LevelLoadData d)
        {
            d.wanderingNeutralsStartIndex = d.kmShipClasses.Length;
            d.neutralFlagsToSpawn = new List<Material>();
            d.neutralIDsToSpawn = new List<int>();
            LevelLoadManager llm = UIFunctions.globaluifunctions.levelloadmanager;
            if (_list.Count > 0 && llm != null && (llm.icePresent || llm.packIcePresent))
            {
                // As the game does: no traffic in ice. Both games work out the ice the same way from the host's conditions.
                Duel.Log("Neutral traffic: none, the mission area has ice");
                _list.Clear();
            }
            int room = GameDataManager.maxShips - d.kmShipClasses.Length;
            int n = System.Math.Min(_list.Count, System.Math.Max(0, room));
            for (int i = 0; i < n; i++)
            {
                d.neutralIDsToSpawn.Add(_list[i].Cls);
                Material m = null;
                if (!string.IsNullOrEmpty(_list[i].Flag)) { Object o = Resources.Load(_list[i].Flag); m = o as Material; }
                d.neutralFlagsToSpawn.Add(m);
            }
            if (n > 0)
            {
                int[] a = new int[d.kmShipClasses.Length + n];
                for (int i = 0; i < a.Length; i++) a[i] = i < d.kmShipClasses.Length ? d.kmShipClasses[i] : d.neutralIDsToSpawn[i - d.kmShipClasses.Length];
                d.kmShipClasses = a;
            }
            GameDataManager.enemyNumberofShips = d.kmShipClasses.Length;
            Duel.Log("~Neutral traffic: " + n + " vessel(s) from index " + d.wanderingNeutralsStartIndex);
        }

        // ---------- in the mission ----------

        public static bool IsNeutral(Vessel v)
        {
            return Duel.InDuel && v != null && v.vesselListIndex >= Start && v.vesselListIndex < Start + _list.Count
                && GameDataManager.enemyvesselsonlevel != null && v.vesselListIndex < GameDataManager.enemyvesselsonlevel.Length
                && GameDataManager.enemyvesselsonlevel[v.vesselListIndex] == v;
        }

        /// <summary>Client: neutrals are driven by the host, so their AI is off here.</summary>
        public static bool IsRemoteAI(VesselAI ai)
        {
            if (ai == null || !Duel.InDuel || S == null || S.IsHost) return false;
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            if (ev == null) return false;
            for (int i = Start; i < ev.Length && i < Start + _list.Count; i++) if (ev[i] != null && ev[i].vesselai == ai) return true;
            return false;
        }

        /// <summary>AfterLoadLevel.</summary>
        public static void Setup()
        {
            _sendTimer = 0f; _hitNeutral.Clear();
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            int n = ev == null ? 0 : System.Math.Max(0, System.Math.Min(_list.Count, ev.Length - Start));
            _remote = new Remote[n];
            for (int i = 0; i < n; i++) _remote[i] = new Remote();
            if (n == 0) return;
            if (!S.IsHost) { for (int i = 0; i < n; i++) if (ev[Start + i] != null) Duel.AiOff(ev[Start + i]); }
            else PlaceWhales(ev, n);
            Duel.Log("Neutral traffic in this duel: " + n + " (" + Describe() + ")" + (S.IsHost ? ", run by us" : ", run by the host"));
        }

        /// <summary>Host: the game only scatters civilian ships; whales would all start on the mission's enemy spot.
        /// Put each one somewhere around the opponent instead, 1.5 to 7 nm away. The client gets the position with the first state.</summary>
        static void PlaceWhales(Vessel[] ev, int n)
        {
            Vessel centre = Duel.Puppet != null ? Duel.Puppet : GameDataManager.playervesselsonlevel[0]; // around the first opponent
            if (centre == null) return;
            for (int i = 0; i < n; i++)
            {
                Vessel v = ev[Start + i];
                if (v == null || v.databaseshipdata.shipType != "BIOLOGIC") continue;
                // Deep enough water (300 ft+), a few tries; whales swim near the surface (30-150 ft).
                Vector3 p = v.transform.position; bool found = false;
                for (int tries = 0; tries < 12 && !found; tries++)
                {
                    float ang = Random.Range(0f, 360f), dist = Random.Range(40f, 190f);
                    Vector3 q = centre.transform.position + Quaternion.Euler(0f, ang, 0f) * Vector3.forward * dist;
                    if (Duel.SeabedFeet(q) >= 300f) { p = q; found = true; }
                }
                if (!found) { Duel.Log("~Neutral traffic: no deep water found for whale " + v.vesselListIndex + ", left where the game put it"); continue; }
                p.y = 1000f - Random.Range(30f, 150f) / GameDataManager.unitsToFeet;
                v.transform.position = p;
                v.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                Duel.Log("~Neutral traffic: whale " + v.vesselListIndex + " placed " + Vector3.Distance(p, centre.transform.position).ToString("F0") + " units out, " + ((1000f - p.y) * GameDataManager.unitsToFeet).ToString("F0") + " ft deep");
                // The AI steers back to its formation spot: move that too.
                GameObject[] fp = UIFunctions.globaluifunctions.levelloadmanager.levelloaddata.formationPositions;
                if (fp != null && Start + i < fp.Length && fp[Start + i] != null) { Vector3 f = p; f.y = 1000f; fp[Start + i].transform.position = f; }
            }
        }

        public static void Reset() { _remote = new Remote[0]; _hitNeutral.Clear(); }

        public static void Update()
        {
            if (!Duel.InDuel || _remote.Length == 0 || S == null) return;
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            if (ev == null) return;
            if (S.IsHost)
            {
                _sendTimer += Time.unscaledDeltaTime;
                if (_sendTimer < 0.5f) return;
                _sendTimer = 0f;
                float t = Time.time;
                int n = _remote.Length;
                S.SendGame(G_AMB_STATE, w =>
                {
                    w.Write(t); w.Write(n);
                    for (int i = 0; i < n; i++)
                    {
                        Vessel v = Start + i < ev.Length ? ev[Start + i] : null;
                        bool ok = v != null;
                        w.Write(ok);
                        if (!ok) continue;
                        Vector3 p = v.transform.position;
                        w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(v.transform.eulerAngles.y);
                        w.Write(v.vesselmovement != null ? v.vesselmovement.shipSpeed.z : 0f);
                        float frac = v.damagesystem != null && v.damagesystem.shipTotalDamagePoints > 0 ? v.damagesystem.shipCurrentDamagePoints / v.damagesystem.shipTotalDamagePoints : 0f;
                        w.Write(frac); w.Write(v.isSinking);
                    }
                }, false);
                return;
            }
            // client: follow the host's states
            for (int i = 0; i < _remote.Length; i++)
            {
                Remote s = _remote[i];
                Vessel v = Start + i < ev.Length ? ev[Start + i] : null;
                if (v == null || !s.Have) continue;
                if (v.isSinking) continue; // the game sinks it from here on
                float yaw = s.Yaw;
                Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                float unitsPerSec = s.SpeedZ * 10f * 2025.37f / 3600f / 75.13f; // knots -> map units per second
                Vector3 target = s.Pos + fwd * unitsPerSec * Mathf.Min(Time.time - s.LocalT, 2f);
                Transform tr = v.transform;
                Vector3 err = target - tr.position;
                if (err.magnitude > 1f) tr.position = target;
                else tr.position += err * Mathf.Min(1f, Time.deltaTime * 2f);
                Quaternion want = Quaternion.Euler(tr.eulerAngles.x, yaw, tr.eulerAngles.z);
                tr.rotation = Quaternion.Slerp(tr.rotation, want, Mathf.Min(1f, Time.deltaTime * 2f));
                if (v.vesselmovement != null) { Vector3 sp = v.vesselmovement.shipSpeed; sp.z = s.SpeedZ; v.vesselmovement.shipSpeed = sp; }
            }
        }

        static void ApplyDamage(Vessel v, float frac, bool sinking)
        {
            if (v.damagesystem != null) v.damagesystem.shipCurrentDamagePoints = frac * v.damagesystem.shipTotalDamagePoints;
            if (sinking && !v.isSinking)
            {
                Compartment c = v.GetComponentInChildren<Compartment>();
                if (c != null) { c.SinkShip(v, true); Duel.Log("Neutral " + v.databaseshipdata.shipclass + " is sinking"); }
            }
        }

        // ---------- hits (the host decides) ----------

        /// <summary>Compartment.OnTriggerEnter prefix on a neutral. Returns false to ignore the touch.</summary>
        public static bool AllowHit(Compartment c, Collider other)
        {
            if (S == null || !S.IsHost) return other == null || other.GetComponent<Torpedo>() == null; // client: the host decides
            return true;
        }

        /// <summary>Host, after the game handled the touch: an opponent torpedo copy that exploded on a neutral.</summary>
        public static void AfterTouch(Compartment c, Collider other)
        {
            if (S == null || !S.IsHost || other == null || c == null || !IsNeutral(c.activeVessel)) return;
            Torpedo t = other.GetComponent<Torpedo>();
            if (t != null && t.destroyMe && Weapons.IsPuppetTorpedo(t)) _hitNeutral.Add(t);
        }

        /// <summary>Client: neutrals don't burn on their own here; the host's damage is applied as it comes.</summary>
        public static bool SkipBurning(DamageSystem ds)
        {
            return Duel.InDuel && S != null && !S.IsHost && ds != null && IsNeutral(ds.parentVessel);
        }

        /// <summary>Host: did this opponent torpedo copy hit a neutral (not us)?</summary>
        public static bool TookNeutralHit(Torpedo t) { return t != null && _hitNeutral.Remove(t); }

        public static void SendHit(int firerId, int torpId)
        {
            S.SendGame(G_AMB_HIT, w => { w.Write(firerId); w.Write(torpId); }, true);
            Duel.Log("Torpedo #" + torpId + " of player " + firerId + " hit a neutral vessel");
        }

        public static bool OnMessage(byte type, BinaryReader r)
        {
            if (type == G_AMB_HIT)
            {
                int firer = r.ReadInt32(), id = r.ReadInt32();
                if (firer == S.MyId) Weapons.OwnTorpedoHitNeutral(id); else Weapons.OtherTorpedoHitNeutral(firer, id);
                return true;
            }
            if (type != G_AMB_STATE) return false;
            float t = r.ReadSingle(); int n = r.ReadInt32();
            if (n < 0 || n > 16) return true; // malformed
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            for (int i = 0; i < n; i++)
            {
                bool ok = r.ReadBoolean();
                if (!ok) continue;
                Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float yaw = r.ReadSingle(), spd = r.ReadSingle(), frac = r.ReadSingle(); bool sinking = r.ReadBoolean();
                if (S.IsHost || !Duel.InDuel || i >= _remote.Length) continue;
                Remote s = _remote[i];
                if (s.Have && t <= s.T) continue;
                bool first = !s.Have;
                s.Pos = p; s.Yaw = yaw; s.SpeedZ = spd; s.T = t; s.LocalT = Time.time; s.Have = true;
                Vessel v = ev != null && Start + i < ev.Length ? ev[Start + i] : null;
                if (v == null) continue;
                if (first) { v.transform.position = p; v.transform.rotation = Quaternion.Euler(0f, yaw, 0f); }
                if (frac != s.Damage || sinking != s.Sinking) { s.Damage = frac; s.Sinking = sinking; ApplyDamage(v, frac, sinking); }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Compartment), "OnTriggerEnter")]
    static class Patch_Compartment_Neutral_Post
    {
        static void Postfix(Compartment __instance, Collider otherObject) { try { Ambient.AfterTouch(__instance, otherObject); } catch (System.Exception) { } }
    }

    [HarmonyPatch(typeof(DamageSystem), "CheckBurning")]
    static class Patch_CheckBurning_Neutral
    {
        static bool Prefix(DamageSystem __instance) { return !Ambient.SkipBurning(__instance); }
    }

    [HarmonyPatch(typeof(LevelLoadData), "AssignNeutralVessels")]
    static class Patch_AssignNeutralVessels
    {
        static bool Prefix(LevelLoadData __instance)
        {
            if (!Duel.InDuel) return true;
            try { Ambient.Assign(__instance); }
            catch (System.Exception ex)
            {
                Duel.Log("Neutral traffic failed: " + ex.Message);
                __instance.wanderingNeutralsStartIndex = __instance.kmShipClasses.Length;
                __instance.neutralFlagsToSpawn = new List<Material>(); __instance.neutralIDsToSpawn = new List<int>();
            }
            return false;
        }
    }
}
