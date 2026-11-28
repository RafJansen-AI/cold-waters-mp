// Cold Waters MP - the other players see your helicopter, and their SAMs and guns can shoot it down. C# 5 / .NET 3.5.
// A player's helicopter (DotModPlayerAircraft) exists only in its owner's game. The owner sends each airborne helicopter's
// type, position and rotation five times a second, and "gone" when it lands or is lost. The other games build the same
// helicopter the way the game builds AI ones (VesselBuilder.CreateAircraft) and switch off its scripts, colliders and
// physics, so it only follows the network.
// An enemy player's helicopter is also put in the game's enemy helicopter list (CombatAI.enemyHelicopters), with a map
// icon, like a mission's AI helicopter: the defender's radar sees it, Scan Air shows it, the SAM (Torpedo interceptMode
// AIRCRAFT, CheckWithinSensorAngles) and the CIWS (Player_AntiMissileGuns) engage it. Both kill a helicopter by setting
// Helicopter.durability to 0 (CIWS hits take it down from 100 in steps) and adding BoomAir (crash, splash). The game
// that shot it reports the kill (G_HELO_DOWN), the owner's game sets the helicopter's Durability to 0 (what DotMod's
// own SAMs do, MissileHome.Tick), and every game lets its copy fall. Only kills are shared, not CIWS damage that did not
// kill. Untested (28 Nov).
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Helos
    {
        public const byte G_HELO = 26, G_HELO_GONE = 27, G_HELO_DOWN = 28;

        class Remote
        {
            public GameObject Go; public int AircraftId, Owner, Slot;
            public Vector3 Pos; public Quaternion Rot; public float Last, Cable;
            public List<Transform> Rotors = new List<Transform>();
            public Helicopter H;          // the game's helicopter component (in the enemy list when Target)
            public bool Target;           // registered as an enemy air target
            public bool Down; public float DownAt;
        }

        static readonly Dictionary<int, Remote> _remote = new Dictionary<int, Remote>();   // key: owner << 8 | slot
        static readonly HashSet<int> _down = new HashSet<int>();                            // shot down this mission: ignore further news
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
                    Vector3 p = a.transform.position; Quaternion q = a.transform.rotation; float cable = Dipping.CableOf(a);
                    Duel.Session.SendGame(G_HELO, w =>
                    {
                        w.Write(s); w.Write(id);
                        w.Write(p.x); w.Write(p.y); w.Write(p.z);
                        w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); w.Write(cable);
                    }, false);
                }
            }
            foreach (int slot in _ownAirborne)
                if (!now.Contains(slot)) { int s = slot; Duel.Session.SendGame(G_HELO_GONE, w => w.Write(s), true); }
            _ownAirborne.Clear();
            foreach (int slot in now) _ownAirborne.Add(slot);
        }

        /// <summary>Another game shot down our helicopter in that slot.</summary>
        static void OwnShotDown(int slot, int by)
        {
            List<DotModPlayerAircraft> all = DotModPlayerAircraft.PlayerAircraft;
            if (all == null || slot < 0 || slot >= all.Count || all[slot] == null) return;
            DotModPlayerAircraft a = all[slot];
            if (a.IsDestroyed) return;
            a.Durability = 0f;
            Opponent o = Duel.InGameById(by);
            string what = "Our helicopter was shot down" + (o != null && Duel.Opps.Count > 1 ? " by " + o.Label : "");
            Duel.Log(what + " (slot " + slot + ")");
            try { PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions; pf.PlayerMessage(what, pf.messageLogColors["FireControl"], "CWMP"); }
            catch (System.Exception) { }
        }

        // ---------- theirs ----------

        static Remote Build(int owner, int slot, int aircraftId, Vector3 pos, Quaternion rot, bool target)
        {
            GameObject go = UIFunctions.globaluifunctions.vesselbuilder.CreateAircraft(aircraftId, pos, rot, true);
            if (go == null) return null;
            go.name = "CWMP remote helicopter";
            // No AI, no sensors, no collisions, no gravity: it follows the network.
            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.useGravity = false; }
            Remote r = new Remote { Go = go, AircraftId = aircraftId, Owner = owner, Slot = slot, Pos = pos, Rot = rot, Last = Time.time };
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                if (t != go.transform && t.name.ToLowerInvariant().Contains("rotor")) r.Rotors.Add(t);
            r.H = go.GetComponent<Helicopter>();
            try { Dipping.ShowCable(r.H, 0f); } catch (System.Exception) { } // the AI build shows its cable; stowed until the owner dips
            if (target && r.H != null)
            {
                try { MakeTarget(r); }
                catch (System.Exception ex) { Duel.Log("~Remote helicopter is not an air target: " + ex.Message); Unregister(r); }
            }
            return r;
        }

        /// <summary>What LevelLoadManager.SetupAircraft and Helicopter.Start give an AI helicopter, as far as the code that
        /// reads the enemy helicopter list needs it; then into the list.</summary>
        static void MakeTarget(Remote r)
        {
            Helicopter h = r.H;
            h.databaseaircraftdata = UIFunctions.globaluifunctions.database.databaseaircraftdata[r.AircraftId];
            h.aircraftID = r.AircraftId;
            h.durability = 100f; h.destroyed = false; h.aquiredByAAM = 0;
            if (h.sensordata == null) h.sensordata = r.Go.AddComponent<SensorData>();
            if (h.sensordata.sonobuoys == null) h.sensordata.sonobuoys = new Noisemaker[0];
            if (h.helibody == null) h.helibody = r.Go.transform;
            if (h.helicopterrigidbody == null) h.helicopterrigidbody = r.Go.GetComponent<Rigidbody>();
            if (h.helicopterrigidbody == null) { h.helicopterrigidbody = r.Go.AddComponent<Rigidbody>(); h.helicopterrigidbody.isKinematic = true; h.helicopterrigidbody.useGravity = false; }
            TacticalMap tm = UIFunctions.globaluifunctions.playerfunctions.sensormanager.tacticalmap;
            MapContact icon = ObjectPoolManager.CreatePooled(tm.torpedoMapContactObject, Vector3.zero, Quaternion.identity).GetComponent<MapContact>();
            icon.contactText.text = string.Empty;
            icon.sensorConeLines[0].gameObject.SetActive(false);
            icon.shipDisplayIcon.color = tm.navyColors[1];
            icon.shipDisplayIcon.sprite = TacticalMapIcon.mapHelicopterIcon;
            icon.gameObject.SetActive(true);
            icon.gameObject.transform.SetParent(tm.tacmapscroller, false);
            h.tacMapHelicopterIcon = icon;
            CombatAI ai = UIFunctions.globaluifunctions.combatai;
            Helicopter[] old = ai.enemyHelicopters ?? new Helicopter[0];
            if (System.Array.IndexOf(old, h) < 0)
            {
                Helicopter[] n = new Helicopter[old.Length + 1];
                old.CopyTo(n, 0); n[old.Length] = h;
                h.helicopterIndex = old.Length;
                ai.enemyHelicopters = n;
            }
            r.Target = true;
        }

        static void Unregister(Remote r)
        {
            if (r.H == null) return;
            try
            {
                CombatAI ai = UIFunctions.globaluifunctions.combatai;
                Helicopter[] old = ai.enemyHelicopters;
                if (old != null && System.Array.IndexOf(old, r.H) >= 0)
                {
                    List<Helicopter> n = new List<Helicopter>();
                    foreach (Helicopter x in old) if (x != r.H) n.Add(x);
                    ai.enemyHelicopters = n.ToArray();
                }
                if (r.H.tacMapHelicopterIcon != null) { ObjectPoolManager.DestroyPooled(r.H.tacMapHelicopterIcon.gameObject); r.H.tacMapHelicopterIcon = null; }
            }
            catch (System.Exception ex) { Duel.Log("~Remote helicopter unregister failed: " + ex.Message); }
            r.Target = false;
        }

        /// <summary>Let our copy fall (the game's BoomAir, added by the SAM or CIWS that killed it here, or by us).</summary>
        static void Fall(Remote r)
        {
            if (r.Down) return;
            r.Down = true; r.DownAt = Time.time;
            _down.Add(Key(r.Owner, r.Slot));
            try
            {
                if (r.H != null) { r.H.durability = 0f; if (r.H.tacMapHelicopterIcon != null) r.H.tacMapHelicopterIcon.gameObject.SetActive(false); }
                foreach (Rigidbody rb in r.Go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = false; rb.useGravity = true; }
                if (r.Go.GetComponent<BoomAir>() == null) r.Go.AddComponent<BoomAir>();
            }
            catch (System.Exception ex) { Duel.Log("~Remote helicopter crash failed: " + ex.Message); }
        }

        public static bool OnMessage(byte type, BinaryReader rd)
        {
            if (type != G_HELO && type != G_HELO_GONE && type != G_HELO_DOWN) return false;
            int from = Duel.Session != null ? Duel.Session.LastSender : -1;
            if (type == G_HELO_GONE)
            {
                int slot = rd.ReadInt32();
                Remote g;
                if (_remote.TryGetValue(Key(from, slot), out g) && g.Down) return true; // let it finish falling
                Remove(Key(from, slot), "landed or lost");
                return true;
            }
            if (type == G_HELO_DOWN)
            {
                int owner = rd.ReadInt32(), slot = rd.ReadInt32();
                if (!Duel.InDuel || Duel.Session == null || from == Duel.Session.MyId) return true;
                if (owner == Duel.Session.MyId) { OwnShotDown(slot, from); return true; }
                Remote d;
                if (_remote.TryGetValue(Key(owner, slot), out d)) { Fall(d); Duel.Log("~Remote helicopter shot down (reported by player " + from + ")"); }
                else _down.Add(Key(owner, slot));
                return true;
            }
            int sl = rd.ReadInt32(), aid = rd.ReadInt32();
            Vector3 p = new Vector3(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
            Quaternion q = new Quaternion(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
            float cab = rd.ReadSingle();
            if (!Duel.InDuel || Duel.Session == null || from == Duel.Session.MyId || Duel.InGameById(from) == null) return true;
            int key = Key(from, sl);
            if (_down.Contains(key)) return true;
            Remote r;
            if (!_remote.TryGetValue(key, out r) || r.Go == null || r.AircraftId != aid)
            {
                if (r != null) Remove(key, "replaced");
                Opponent o = Duel.InGameById(from);
                bool target = !Duel.IsAlly(o);
                try { r = Build(from, sl, aid, p, q, target); }
                catch (System.Exception ex) { Duel.Log("~Remote helicopter build failed: " + ex.Message); r = null; }
                if (r == null) return true;
                _remote[key] = r;
                Duel.Log("~" + (o != null ? o.Label : "player " + from) + "'s helicopter is airborne (" + UIFunctions.globaluifunctions.database.databaseaircraftdata[aid].aircraftPrefabName + ")" + (r.Target ? ", an air target" : ""));
            }
            r.Pos = p; r.Rot = q; r.Last = Time.time;
            if (cab != r.Cable)
            {
                // The cable reaches its depth: the dipping icon the game puts on the map for an AI dip
                // (Helicopter.FixedUpdate: TacticalMap.PlaceHazardIconOnMap(position, 6)), seen even without contact.
                if (cab <= -0.349f && r.Cable > -0.349f)
                {
                    try { UIFunctions.globaluifunctions.playerfunctions.sensormanager.tacticalmap.PlaceHazardIconOnMap(p, 6); Duel.Log("~Remote helicopter is dipping: map icon placed"); }
                    catch (System.Exception ex) { Duel.Log("~Dipping icon failed: " + ex.Message); }
                }
                r.Cable = cab;
                try { Dipping.ShowCable(r.H, cab); } catch (System.Exception) { }
            }
            return true;
        }

        static void Remove(int key, string why)
        {
            Remote r;
            if (!_remote.TryGetValue(key, out r)) return;
            _remote.Remove(key);
            Unregister(r);
            if (r.Go != null) Object.Destroy(r.Go);
            Duel.Log("~Remote helicopter removed (" + why + ")");
        }

        public static void Update()
        {
            if (!Duel.InDuel || Duel.Session == null) return;
            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer >= 0.2f) { _sendTimer = 0f; try { SendOwn(); } catch (System.Exception ex) { Duel.Log("~Helicopter send failed: " + ex.Message); } }
            List<int> stale = null; List<KeyValuePair<int, string>> drop = null;
            foreach (KeyValuePair<int, Remote> kv in _remote)
            {
                Remote r = kv.Value;
                if (r.Go == null) { if (drop == null) drop = new List<KeyValuePair<int, string>>(); drop.Add(new KeyValuePair<int, string>(kv.Key, "gone")); continue; }
                if (r.Down)
                {
                    // BoomAir switches it off when it reaches the water.
                    if (!r.Go.activeSelf || Time.time - r.DownAt > 30f) { if (drop == null) drop = new List<KeyValuePair<int, string>>(); drop.Add(new KeyValuePair<int, string>(kv.Key, "crashed")); }
                    continue;
                }
                if (r.Target && r.H != null && r.H.durability <= 0f)
                {
                    // Our SAM or guns killed it: tell everyone (the owner destroys the real one).
                    int owner = r.Owner, slot = r.Slot;
                    Duel.Session.SendGame(G_HELO_DOWN, w => { w.Write(owner); w.Write(slot); }, true);
                    Opponent o = Duel.InGameById(owner);
                    Duel.Log("~We shot down " + (o != null ? o.Label : "player " + owner) + "'s helicopter (slot " + slot + ")");
                    Fall(r);
                    continue;
                }
                if (Time.time - r.Last > 5f) { if (stale == null) stale = new List<int>(); stale.Add(kv.Key); continue; }
                Transform t = r.Go.transform;
                if (Vector3.Distance(t.position, r.Pos) > 2f) t.position = r.Pos; // > ~150 yd: snap
                else t.position = Vector3.Lerp(t.position, r.Pos, Mathf.Min(1f, Time.deltaTime * 6f));
                t.rotation = Quaternion.Slerp(t.rotation, r.Rot, Mathf.Min(1f, Time.deltaTime * 6f));
                foreach (Transform rot in r.Rotors)
                    if (rot != null) rot.Rotate(rot.name.ToLowerInvariant().Contains("tail") ? Vector3.right : Vector3.up, 1500f * Time.deltaTime, Space.Self);
            }
            if (stale != null) foreach (int k in stale) Remove(k, "no news");
            if (drop != null) foreach (KeyValuePair<int, string> d in drop) Remove(d.Key, d.Value);
        }

        /// <summary>Out of the game's lists before it tears down the mission's aircraft (it would expect AI parts).</summary>
        public static void UnregisterAll() { foreach (Remote r in _remote.Values) Unregister(r); }

        public static void Reset()
        {
            foreach (Remote r in _remote.Values) { Unregister(r); if (r.Go != null) Object.Destroy(r.Go); }
            _remote.Clear(); _down.Clear(); _ownAirborne.Clear(); _sendTimer = 0f;
        }
    }

    /// <summary>DotMod bug: when a player aircraft is destroyed, AircraftDestroyed.OnEnter empties its map icon's trail
    /// (TacticalMap.ClearMapContactTrail(icon, false) dequeues every marker but leaves the move count) and returns the icon
    /// to the pool, but Update keeps calling UpdateTacMapIcon, which dequeues from the empty queue: an exception every
    /// frame and DotMod's error screen (duel 28 Nov, helicopter shot down by an Igla). Single player too. A destroyed
    /// aircraft has no icon to update.</summary>
    [HarmonyPatch(typeof(DotModPlayerAircraft), "UpdateTacMapIcon")]
    static class Patch_UpdateTacMapIcon_Destroyed
    {
        static bool Prefix(DotModPlayerAircraft __instance) { return !__instance.IsDestroyed; }
    }

    [HarmonyPatch(typeof(UIFunctions), "DestroyAllAircraft")]
    static class Patch_DestroyAllAircraft_Helos
    {
        static void Prefix() { try { Helos.UnregisterAll(); } catch (System.Exception) { } }
    }
}
