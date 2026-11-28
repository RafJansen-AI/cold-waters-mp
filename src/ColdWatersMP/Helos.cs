// Cold Waters MP - the other players see your helicopter. C# 5 / .NET 3.5.
// A player's helicopter (DotModPlayerAircraft) exists only in its owner's game. The owner sends each airborne helicopter's
// type, position and rotation five times a second, and "gone" when it lands or is lost. The other games build the same
// helicopter the way the game builds AI ones (VesselBuilder.CreateAircraft) and switch off everything but the looks:
// scripts, colliders and physics. Visual only: no map icon, no sensors, SAMs can't engage it (later). Untested (28 Nov).
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Helos
    {
        public const byte G_HELO = 26, G_HELO_GONE = 27;

        class Remote
        {
            public GameObject Go; public int AircraftId;
            public Vector3 Pos; public Quaternion Rot; public float Last;
            public List<Transform> Rotors = new List<Transform>();
        }

        static readonly Dictionary<int, Remote> _remote = new Dictionary<int, Remote>();   // key: sender << 8 | slot
        static readonly HashSet<int> _ownAirborne = new HashSet<int>();                     // our slots last sent as airborne
        static float _sendTimer;

        static int Key(int player, int slot) { return (player << 8) | (slot & 0xFF); }

        // ---------- our helicopters ----------

        static void SendOwn()
        {
            List<DotModPlayerAircraft> all = DotModPlayerAircraft.PlayerAircraft;
            HashSet<int> now = new HashSet<int>();
            if (all != null)
            {
                for (int slot = 0; slot < all.Count && slot < 255; slot++)
                {
                    DotModPlayerAircraft a = all[slot];
                    if (a == null || a.databaseaircraftdata == null || !a.IsAirborne || a.IsDestroyed) continue;
                    now.Add(slot);
                    int s = slot, id = a.databaseaircraftdata.aircraftID;
                    Vector3 p = a.transform.position; Quaternion q = a.transform.rotation;
                    Duel.Session.SendGame(G_HELO, w =>
                    {
                        w.Write(s); w.Write(id);
                        w.Write(p.x); w.Write(p.y); w.Write(p.z);
                        w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
                    }, false);
                }
            }
            foreach (int slot in _ownAirborne)
                if (!now.Contains(slot)) { int s = slot; Duel.Session.SendGame(G_HELO_GONE, w => w.Write(s), true); }
            _ownAirborne.Clear();
            foreach (int slot in now) _ownAirborne.Add(slot);
        }

        // ---------- theirs ----------

        static Remote Build(int aircraftId, Vector3 pos, Quaternion rot)
        {
            GameObject go = UIFunctions.globaluifunctions.vesselbuilder.CreateAircraft(aircraftId, pos, rot, true);
            if (go == null) return null;
            go.name = "CWMP remote helicopter";
            // Looks only: no AI, no sensors, no collisions, no gravity.
            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.useGravity = false; }
            Remote r = new Remote { Go = go, AircraftId = aircraftId, Pos = pos, Rot = rot, Last = Time.time };
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                if (t != go.transform && t.name.ToLowerInvariant().Contains("rotor")) r.Rotors.Add(t);
            return r;
        }

        public static bool OnMessage(byte type, BinaryReader rd)
        {
            if (type != G_HELO && type != G_HELO_GONE) return false;
            int from = Duel.Session != null ? Duel.Session.LastSender : -1;
            if (type == G_HELO_GONE)
            {
                int slot = rd.ReadInt32();
                Remove(Key(from, slot), "landed or lost");
                return true;
            }
            int sl = rd.ReadInt32(), aid = rd.ReadInt32();
            Vector3 p = new Vector3(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
            Quaternion q = new Quaternion(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
            if (!Duel.InDuel || Duel.Session == null || from == Duel.Session.MyId || Duel.InGameById(from) == null) return true;
            int key = Key(from, sl);
            Remote r;
            if (!_remote.TryGetValue(key, out r) || r.Go == null || r.AircraftId != aid)
            {
                if (r != null) Remove(key, "replaced");
                try { r = Build(aid, p, q); }
                catch (System.Exception ex) { Duel.Log("~Remote helicopter build failed: " + ex.Message); r = null; }
                if (r == null) return true;
                _remote[key] = r;
                Opponent o = Duel.InGameById(from);
                Duel.Log("~" + (o != null ? o.Label : "player " + from) + "'s helicopter is airborne (" + UIFunctions.globaluifunctions.database.databaseaircraftdata[aid].aircraftPrefabName + ")");
            }
            r.Pos = p; r.Rot = q; r.Last = Time.time;
            return true;
        }

        static void Remove(int key, string why)
        {
            Remote r;
            if (!_remote.TryGetValue(key, out r)) return;
            _remote.Remove(key);
            if (r.Go != null) Object.Destroy(r.Go);
            Duel.Log("~Remote helicopter removed (" + why + ")");
        }

        public static void Update()
        {
            if (!Duel.InDuel || Duel.Session == null) return;
            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer >= 0.2f) { _sendTimer = 0f; try { SendOwn(); } catch (System.Exception ex) { Duel.Log("~Helicopter send failed: " + ex.Message); } }
            List<int> stale = null;
            foreach (KeyValuePair<int, Remote> kv in _remote)
            {
                Remote r = kv.Value;
                if (r.Go == null || Time.time - r.Last > 5f) { if (stale == null) stale = new List<int>(); stale.Add(kv.Key); continue; }
                Transform t = r.Go.transform;
                if (Vector3.Distance(t.position, r.Pos) > 2f) t.position = r.Pos; // > ~150 yd: snap
                else t.position = Vector3.Lerp(t.position, r.Pos, Mathf.Min(1f, Time.deltaTime * 6f));
                t.rotation = Quaternion.Slerp(t.rotation, r.Rot, Mathf.Min(1f, Time.deltaTime * 6f));
                foreach (Transform rot in r.Rotors)
                    if (rot != null) rot.Rotate(rot.name.ToLowerInvariant().Contains("tail") ? Vector3.right : Vector3.up, 1500f * Time.deltaTime, Space.Self);
            }
            if (stale != null) foreach (int k in stale) Remove(k, "no news");
        }

        public static void Reset()
        {
            foreach (Remote r in _remote.Values) if (r.Go != null) Object.Destroy(r.Go);
            _remote.Clear(); _ownAirborne.Clear(); _sendTimer = 0f;
        }
    }
}
