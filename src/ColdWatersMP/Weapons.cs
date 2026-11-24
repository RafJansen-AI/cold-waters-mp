// Cold Waters MP - step 4: torpedoes and damage across both games. C# 5 / .NET 3.5.
// Rules: the firer's game runs the real torpedo (guidance, wire, seeker). The target's game shows a
// "puppet torpedo" that follows the firer's state and, if it touches the target's real sub, lets the
// game apply damage there. The target reports hits and its damage state back to the firer.
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Weapons
    {
        public const byte G_TORP_SPAWN = 5, G_TORP_STATE = 6, G_TORP_END = 7, G_TORP_HIT = 8, G_DAMAGE = 9;

        class Incoming
        {
            public Torpedo T;
            public Vector3 Pos0, Pos1; public Vector3 Euler1; public float T0, T1, Local1; public int Count;
            public float SpawnLocal; public float EndAt = -1f; public bool Homing; public bool Armed;
            public float LastHoming = -100f; public bool HomingLogged; public bool DroppedPayload;
        }

        static int _nextId = 1;
        static readonly Dictionary<int, Torpedo> _own = new Dictionary<int, Torpedo>();
        static readonly Dictionary<Torpedo, int> _ownIds = new Dictionary<Torpedo, int>();
        static readonly Dictionary<int, Incoming> _incoming = new Dictionary<int, Incoming>();
        static readonly HashSet<Torpedo> _puppetTorps = new HashSet<Torpedo>();
        static float _sendTimer, _damageTimer;
        static float _lastSentDamage = -1f;
        static bool _lastSentSinking;

        static Session S { get { return Duel.Session; } }
        static bool Active { get { return Duel.InDuel && S != null && S.State == SessionState.Ready; } }

        public static void Reset()
        {
            _own.Clear(); _ownIds.Clear(); _incoming.Clear(); _puppetTorps.Clear();
            _lastSentDamage = -1f; _lastSentSinking = false;
        }

        public static bool IsPuppetTorpedo(Torpedo t) { return t != null && _puppetTorps.Contains(t); }
        public static bool IsOwnTorpedo(Torpedo t) { return t != null && _ownIds.ContainsKey(t); }

        // ---------- firer side ----------

        public static void OnPlayerFired(Torpedo t) { OnPlayerFired(t, false, null); }

        public static void OnPlayerFired(Torpedo t, bool payload, Torpedo carrier)
        {
            if (!Active || t == null) return;
            int id = _nextId++;
            _own[id] = t; _ownIds[t] = id;
            int weaponId = t.databaseweapondata.weaponID;
            Vector3 pos = t.transform.position; Vector3 eul = t.transform.eulerAngles;
            int carrierId = -1;
            if (carrier != null) _ownIds.TryGetValue(carrier, out carrierId);
            S.SendGame(G_TORP_SPAWN, w => { w.Write(id); w.Write(weaponId); WriteV3(w, pos); WriteV3(w, eul); w.Write(payload); w.Write(carrierId); }, true);
            Duel.Log("Fired torpedo #" + id + " (" + t.databaseweapondata.weaponName + ")");
        }

        static void SendOwnTorpedoStates()
        {
            List<int> ended = null;
            foreach (KeyValuePair<int, Torpedo> kv in _own)
            {
                Torpedo t = kv.Value;
                int id = kv.Key;
                if (t == null) { if (ended == null) ended = new List<int>(); ended.Add(id); continue; }
                Vector3 pos = t.transform.position; Vector3 eul = t.transform.eulerAngles; float time = Time.time;
                bool homing = Duel.Puppet != null && t.targetTransform == Duel.Puppet.transform;
                S.SendGame(G_TORP_STATE, w => { w.Write(id); w.Write(time); WriteV3(w, pos); WriteV3(w, eul); w.Write(homing); }, false);
            }
            if (ended == null) return;
            foreach (int id in ended)
            {
                _own.Remove(id);
                int idCopy = id;
                S.SendGame(G_TORP_END, w => w.Write(idCopy), true);
                Duel.Log("Torpedo #" + id + " ended on our side");
            }
            // clean reverse map
            List<Torpedo> dead = new List<Torpedo>();
            foreach (Torpedo k in _ownIds.Keys) if (k == null) dead.Add(k);
            foreach (Torpedo k in dead) _ownIds.Remove(k);
        }

        // ---------- target side ----------

        static void SpawnPuppetTorpedo(int id, int weaponId, Vector3 pos, Vector3 eul, bool payload)
        {
            if (Duel.Puppet == null) return;
            DatabaseWeaponData wd = UIFunctions.globaluifunctions.database.databaseweapondata[weaponId];
            GameObject go = (GameObject)Object.Instantiate(wd.weaponObject, pos, Quaternion.Euler(eul));
            go.SetActive(true);
            Torpedo t = go.GetComponent<Torpedo>();
            t.databaseweapondata = wd;
            t.vesselFiredFrom = Duel.Puppet;
            t.whichNavy = 1;
            t.onWire = false;
            t.usesParachute = false;
            t.InitialiseTorpedo();
            _puppetTorps.Add(t);
            UIFunctions.globaluifunctions.playerfunctions.sensormanager.AddTorpedoToArray(t);
            Incoming inc = new Incoming { T = t, Pos0 = pos, Pos1 = pos, Euler1 = eul, T0 = 0, T1 = 0, Local1 = Time.time, Count = 0, SpawnLocal = Time.time };
            _incoming[id] = inc;
            Duel.Log("Opponent fired torpedo #" + id + " (" + wd.weaponName + ")");
            if (payload) return; // dropped by a rocket far from the firer: no launch transient
            try { LaunchTransient(Duel.Puppet, wd); }
            catch (System.Exception ex) { Duel.Log("Launch transient check failed: " + ex.Message); }
        }

        /// <summary>Same rule as the AI's launch (Enemy_Torpedo.FireTorpedo): if our sonar can hear the
        /// launch transient, give the "launch transient" sonar report with its bearing.</summary>
        static void LaunchTransient(Vessel firer, DatabaseWeaponData wd)
        {
            Vessel own = GameDataManager.playervesselsonlevel[0];
            if (own == null || firer == null || wd.swimOut) return;
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            SensorManager sm = pf.sensormanager;
            float heard = sm.CheckCanHearTransient(own, firer);
            if (heard <= 10f) { Duel.Log("Launch transient not heard (" + heard.ToString("F1") + ")"); return; }
            Transform nav = own.acoustics.sensorNavigator.transform;
            nav.LookAt(firer.transform.position);
            float brg = nav.eulerAngles.y; // true bearing, as in the TorpedoInTheWater callout (the AI-launch code uses local)
            if (brg < 0f) brg += 360f;
            string msg;
            if (sm.detectedByPlayer[firer.vesselListIndex])
            {
                pf.voicemanager.currentVesselIndex = firer.vesselListIndex;
                msg = LanguageManager.GetDictionaryString(LanguageManager.messageLogDictionary, "TransientKnown");
                msg = msg.Replace("<CONTACT>", pf.GetFullContactName(sm.tacticalmap.mapContact[firer.vesselListIndex].contactText.text, firer.vesselListIndex));
            }
            else msg = LanguageManager.GetDictionaryString(LanguageManager.messageLogDictionary, "TransientUnknown");
            msg = msg.Replace("<BRG>", brg.ToString("0"));
            pf.PlayerMessage(msg, pf.messageLogColors["Sonar"], "TransientKnown");
            Duel.Log("Launch transient heard (" + heard.ToString("F1") + "), bearing " + brg.ToString("0"));
        }

        /// <summary>Called from the Torpedo.FixedUpdate prefix for puppet torpedoes instead of the game's logic.</summary>
        public static void DrivePuppetTorpedo(Torpedo t)
        {
            int id = -1; Incoming inc = null;
            foreach (KeyValuePair<int, Incoming> kv in _incoming) if (kv.Value.T == t) { id = kv.Key; inc = kv.Value; break; }
            if (inc == null) return;

            if (t.destroyMe)
            {
                // The game flagged it: it touched our sub and Compartment.OnTriggerEnter applied damage.
                int idCopy = id;
                S.SendGame(G_TORP_HIT, w => w.Write(idCopy), true);
                Duel.Log("Opponent torpedo #" + id + " hit us");
                Kill(id, true);
                return;
            }
            if (inc.EndAt > 0f && Time.time > inc.EndAt)
            {
                Duel.Log("Opponent torpedo #" + id + (inc.DroppedPayload ? " ended (carrier, payload was released)" : " ended (miss)"));
                Kill(id, false);
                return;
            }
            // The game arms a torpedo's hit collider in Torpedo.FixedUpdate, which puppets skip.
            // Player torpedoes (navy 0 on the firer's side) arm after 5 s; do the same here.
            if (!inc.Armed && Time.time - inc.SpawnLocal > 5f) Arm(t, inc);
            // "Torpedo in the water" callout: the game gives it when a torpedo targets the player's sub.
            Vessel own = GameDataManager.playervesselsonlevel[0];
            // Hold the lock for 2 s after the flag drops: states are unreliable and seekers flicker.
            if (own != null) t.targetTransform = (inc.Homing || Time.time - inc.LastHoming < 2f) ? own.transform : null;

            Vector3 target = inc.Pos1;
            float dt = inc.T1 - inc.T0;
            if (inc.Count > 1 && dt > 0.001f && dt < 2f)
                target += (inc.Pos1 - inc.Pos0) / dt * Mathf.Min(Time.time - inc.Local1, 1f);
            Transform tr = t.transform;
            Vector3 err = target - tr.position;
            if (err.magnitude > 0.5f) tr.position = target;
            else tr.position += err * Mathf.Min(1f, Time.deltaTime * 8f);
            tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(inc.Euler1), Mathf.Min(1f, Time.deltaTime * 8f));
        }

        static void Arm(Torpedo t, Incoming inc)
        {
            inc.Armed = true;
            if (t.boxcollider != null) ((Collider)t.boxcollider).enabled = true;
            SphereCollider sc = t.gameObject.GetComponent<SphereCollider>();
            if (sc == null) sc = t.gameObject.AddComponent<SphereCollider>();
            sc.enabled = true;
            sc.radius = 0.05f;
        }

        static void Kill(int id, bool effects)
        {
            Incoming inc;
            if (!_incoming.TryGetValue(id, out inc)) return;
            _incoming.Remove(id);
            if (inc.T != null)
            {
                _puppetTorps.Remove(inc.T);
                AccessTools.Method(typeof(Torpedo), "DestroyTorpedo").Invoke(inc.T, new object[] { effects });
            }
        }

        static void SendDamageIfChanged()
        {
            Vessel own = GameDataManager.playervesselsonlevel[0];
            if (own == null || own.damagesystem == null) return;
            float frac = own.damagesystem.shipTotalDamagePoints > 0 ? own.damagesystem.shipCurrentDamagePoints / own.damagesystem.shipTotalDamagePoints : 0f;
            bool sinking = own.isSinking;
            if (Mathf.Abs(frac - _lastSentDamage) < 0.005f && sinking == _lastSentSinking) return;
            _lastSentDamage = frac; _lastSentSinking = sinking;
            S.SendGame(G_DAMAGE, w => { w.Write(frac); w.Write(sinking); }, true);
            Duel.Log("Our damage " + (frac * 100f).ToString("F0") + "%" + (sinking ? ", SINKING" : ""));
        }

        static void ApplyOpponentDamage(float frac, bool sinking)
        {
            Vessel p = Duel.Puppet;
            if (p == null) return;
            p.damagesystem.shipCurrentDamagePoints = frac * p.damagesystem.shipTotalDamagePoints;
            Duel.Log("Opponent damage " + (frac * 100f).ToString("F0") + "%" + (sinking ? ", SINKING" : ""));
            if (sinking && !p.isSinking)
            {
                Compartment c = p.GetComponentInChildren<Compartment>();
                if (c != null) c.SinkShip(p, true);
                else Duel.Log("WARNING: could not find a compartment to sink the puppet");
            }
        }

        // ---------- per-frame + messages ----------

        public static void Update()
        {
            if (!Active) return;
            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer >= 0.1f) { _sendTimer = 0f; SendOwnTorpedoStates(); }
            _damageTimer += Time.unscaledDeltaTime;
            if (_damageTimer >= 0.5f) { _damageTimer = 0f; SendDamageIfChanged(); }
        }

        public static bool OnMessage(byte type, BinaryReader r)
        {
            switch (type)
            {
                case G_TORP_SPAWN:
                {
                    int id = r.ReadInt32(); int wid = r.ReadInt32(); Vector3 p = ReadV3(r); Vector3 e = ReadV3(r); bool payload = r.ReadBoolean(); int carrierId = r.ReadInt32();
                    Incoming carrier;
                    if (carrierId >= 0 && _incoming.TryGetValue(carrierId, out carrier)) carrier.DroppedPayload = true;
                    if (Duel.InDuel) SpawnPuppetTorpedo(id, wid, p, e, payload);
                    return true;
                }
                case G_TORP_STATE:
                {
                    int id = r.ReadInt32(); float time = r.ReadSingle(); Vector3 p = ReadV3(r); Vector3 e = ReadV3(r); bool homing = r.ReadBoolean();
                    Incoming inc;
                    if (_incoming.TryGetValue(id, out inc) && time > inc.T1 && inc.EndAt < 0f)
                    {
                        inc.Pos0 = inc.Pos1; inc.T0 = inc.T1; inc.Pos1 = p; inc.T1 = time; inc.Euler1 = e; inc.Local1 = Time.time; inc.Count++;
                        if (homing && !inc.HomingLogged) { inc.HomingLogged = true; Duel.Log("Opponent torpedo #" + id + " is homing on us"); }
                        if (homing) inc.LastHoming = Time.time;
                        inc.Homing = homing;
                    }
                    return true;
                }
                case G_TORP_END:
                {
                    int id = r.ReadInt32();
                    // Grace period: the firer's torpedo may end (proximity, impact) a moment before ours
                    // touches the hull here. Keep it running briefly so our collision can still count.
                    Incoming inc;
                    if (_incoming.TryGetValue(id, out inc) && inc.EndAt < 0f) inc.EndAt = Time.time + 1.0f;
                    return true;
                }
                case G_TORP_HIT:
                {
                    int id = r.ReadInt32();
                    Torpedo t;
                    Duel.Log("Our torpedo #" + id + " HIT the opponent");
                    if (_own.TryGetValue(id, out t) && t != null) t.destroyMe = true; // explode here too
                    _own.Remove(id);
                    return true;
                }
                case G_DAMAGE:
                {
                    float f = r.ReadSingle(); bool sinking = r.ReadBoolean();
                    ApplyOpponentDamage(f, sinking);
                    return true;
                }
            }
            return false;
        }

        static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static Vector3 ReadV3(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
    }

    // ---------- patches ----------

    /// <summary>Player launch. FireTube may clear torpedoesOnWire[tube] for unguided torpedoes or a
    /// broken wire, so the new torpedo is captured from InitialiseTorpedo while FireTube runs.</summary>
    [HarmonyPatch(typeof(WeaponSource), "FireTube")]
    static class Patch_FireTube
    {
        internal static bool InFireTube;
        internal static Torpedo Launched;

        static void Prefix() { InFireTube = true; Launched = null; }

        static void Postfix()
        {
            InFireTube = false;
            Torpedo t = Launched;
            Launched = null;
            if (Duel.InDuel && t != null) Weapons.OnPlayerFired(t);
        }

        /// <summary>Runs even if FireTube throws, so a failed launch can't leave the capture flag set.</summary>
        static System.Exception Finalizer(System.Exception __exception)
        {
            InFireTube = false;
            Launched = null;
            return __exception;
        }
    }

    /// <summary>FireTube lets enemy AI react to the player's launch transient. In a duel the only
    /// enemy is the puppet, whose AI is off, and its attack code throws. The opponent hears our
    /// launch in their own game instead (Weapons.LaunchTransient).</summary>
    [HarmonyPatch(typeof(CombatAI), "CheckPlayerTransient")]
    static class Patch_CheckPlayerTransient
    {
        static bool Prefix() { return !Duel.InDuel; }
    }

    /// <summary>No AI attack orders for the puppet, whoever issues them.</summary>
    [HarmonyPatch(typeof(VesselAI), "AttackPosition")]
    static class Patch_VesselAI_AttackPosition
    {
        static bool Prefix(VesselAI __instance)
        {
            return !(Duel.InDuel && Duel.Puppet != null && Duel.Puppet.vesselai == __instance);
        }
    }

    [HarmonyPatch(typeof(CombatAI), "CheckToAttackPlayer")]
    static class Patch_CheckToAttackPlayer
    {
        static bool Prefix(Vessel activeVessel) { return !Duel.IsPuppet(activeVessel); }
    }

    [HarmonyPatch(typeof(Torpedo), "InitialiseTorpedo")]
    static class Patch_InitialiseTorpedo
    {
        static void Postfix(Torpedo __instance)
        {
            if (Patch_FireTube.InFireTube && Patch_FireTube.Launched == null) { Patch_FireTube.Launched = __instance; return; }
            Torpedo parent = Patch_Torpedo_FixedUpdate.OwnParent;
            if (parent != null && __instance != parent && !Weapons.IsPuppetTorpedo(__instance) && !Weapons.IsOwnTorpedo(__instance))
            {
                Duel.Log("Payload released by " + parent.databaseweapondata.weaponName);
                Weapons.OnPlayerFired(__instance, true, parent);
            }
        }
    }

    /// <summary>Puppet torpedoes follow the network; the game's own guidance must not run.
    /// For our own weapons, remember which one is updating: a rocket (Vjuga, Vodopad...) releases its
    /// torpedo payload inside FixedUpdate, and that payload must be sent to the opponent too.</summary>
    [HarmonyPatch(typeof(Torpedo), "FixedUpdate")]
    static class Patch_Torpedo_FixedUpdate
    {
        internal static Torpedo OwnParent;

        static bool Prefix(Torpedo __instance)
        {
            if (Weapons.IsPuppetTorpedo(__instance)) { Weapons.DrivePuppetTorpedo(__instance); return false; }
            OwnParent = Duel.InDuel && Weapons.IsOwnTorpedo(__instance) ? __instance : null;
            return true;
        }

        static void Postfix() { OwnParent = null; }

        static System.Exception Finalizer(System.Exception __exception) { OwnParent = null; return __exception; }
    }

    /// <summary>Firer side: our real torpedo touching the opponent's puppet does no damage here;
    /// the opponent's game decides and reports back.</summary>
    [HarmonyPatch(typeof(Compartment), "OnTriggerEnter")]
    static class Patch_Compartment_OnTriggerEnter
    {
        static bool Prefix(Compartment __instance, Collider otherObject)
        {
            if (!Duel.IsPuppet(__instance.activeVessel)) return true;
            return otherObject == null || otherObject.GetComponent<Torpedo>() == null;
        }
    }
}
