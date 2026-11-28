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
        public const byte G_TORP_SPAWN = 5, G_TORP_STATE = 6, G_TORP_END = 7, G_TORP_HIT = 8, G_DAMAGE = 9, G_NOISEMAKER = 11, G_KNUCKLE = 14, G_DECAL = 20, G_EXPLOSION = 21, G_TORP_DOWN = 23, G_TORP_DECOYED = 29;

        class Incoming
        {
            public Opponent Firer; public int Id; // the firer's own torpedo number
            public Torpedo T;
            public Vector3 Pos0, Pos1; public Vector3 Euler1; public float T0, T1, Local1; public int Count;
            public float SpawnLocal; public float EndAt = -1f; public bool Homing; public bool Armed;
            public float LastHoming = -100f; public bool HomingLogged; public bool DroppedPayload; public bool Payload; public bool Decoy;
            public int IconState = -1, IconChanges; // last logged map icon state (-1 unknown, 0 hidden, 1 shown), changes logged
        }

        static int _nextId = 1;
        static readonly Dictionary<int, Torpedo> _own = new Dictionary<int, Torpedo>();
        static readonly Dictionary<Torpedo, int> _ownIds = new Dictionary<Torpedo, int>();
        // Other players' torpedoes, by Key(firer, their number): torpedo numbers are per firer.
        static readonly Dictionary<int, Incoming> _incoming = new Dictionary<int, Incoming>();
        public static int Key(int firer, int id) { return (firer << 20) | (id & 0xFFFFF); }
        static string Tag(Opponent firer, int id) { return (Duel.Opps.Count > 1 ? firer.Label + "'s torpedo #" : "Opponent torpedo #") + id; }
        static readonly HashSet<Torpedo> _puppetTorps = new HashSet<Torpedo>();
        static readonly HashSet<int> _onNoisemaker = new HashSet<int>();
        static readonly HashSet<int> _targetLogged = new HashSet<int>();     // own torpedoes whose first lock was logged
        static readonly Dictionary<int, float> _decoyLogAt = new Dictionary<int, float>();
        static float _sendTimer, _damageTimer;
        static float _lastSentDamage = -1f;
        static bool _lastSentSinking;

        static Session S { get { return Duel.Session; } }
        static bool Active { get { return Duel.InDuel && S != null && S.State == SessionState.Ready; } }

        public static void Reset()
        {
            _own.Clear(); _ownIds.Clear(); _incoming.Clear(); _puppetTorps.Clear(); _onNoisemaker.Clear(); _decoyLogAt.Clear();
            _lastSentDamage = -1f; _lastSentSinking = false;
            OwnFired = 0; OppFired = 0; OwnHits = 0; OppHits = 0;
        }

        // Match statistics (for the result line)
        public static int OwnFired, OppFired, OwnHits, OppHits;

        /// <summary>Client: the host says our torpedo hit a neutral there; explode it here too.</summary>
        public static void OwnTorpedoHitNeutral(int id)
        {
            Torpedo t;
            Duel.Log("Our torpedo #" + id + " hit a neutral vessel");
            if (_own.TryGetValue(id, out t) && t != null) t.destroyMe = true;
            _own.Remove(id);
        }

        public static bool IsPuppetTorpedo(Torpedo t) { return t != null && _puppetTorps.Contains(t); }
        /// <summary>Which player's torpedo, and its number, this copy is (false if it is not a copy).</summary>
        public static bool CopyOf(Torpedo t, out int firer, out int id)
        {
            firer = -1; id = -1;
            if (t == null) return false;
            foreach (KeyValuePair<int, Incoming> kv in _incoming) if (kv.Value.T == t) { firer = kv.Value.Firer.Id; id = kv.Value.Id; return true; }
            return false;
        }

        /// <summary>Our own torpedo or missile with this number, or null.</summary>
        public static Torpedo Own(int id) { Torpedo t; return _own.TryGetValue(id, out t) ? t : null; }

        static readonly HashSet<Torpedo> _decoyedCopies = new HashSet<Torpedo>();

        /// <summary>Target side: our own towed decoy destroyed this copy (Noisemaker.Update, decoy exploder range). Our game
        /// decides, as for a hit: the firer destroys its real torpedo.</summary>
        public static void CopyDestroyedByDecoy(Torpedo t)
        {
            int firerId, id;
            if (!CopyOf(t, out firerId, out id) || !_decoyedCopies.Add(t)) return;
            S.SendGame(G_TORP_DECOYED, w => { w.Write(firerId); w.Write(id); }, true);
            Opponent f = Duel.InGameById(firerId);
            Duel.Log((f != null ? Tag(f, id) : "Torpedo #" + id) + " destroyed by our towed decoy");
        }

        public static bool IsOwnTorpedo(Torpedo t) { return t != null && _ownIds.ContainsKey(t); }

        // ---------- firer side ----------

        public static void OnPlayerFired(Torpedo t) { OnPlayerFired(t, false, null); }

        public static void OnPlayerFired(Torpedo t, bool payload, Torpedo carrier) { OnPlayerFired(t, payload, carrier, false); }

        /// <param name="sam">An anti-air missile (WeaponSource.FireAAM): shown to the others, not counted as a shot at
        /// them, no launch transient.</param>
        public static void OnPlayerFired(Torpedo t, bool payload, Torpedo carrier, bool sam)
        {
            if (!Active || t == null) return;
            int id = _nextId++;
            _own[id] = t; _ownIds[t] = id;
            bool decoy = Ghosts.IsDecoy(t.databaseweapondata);
            if (!payload && !decoy && !sam) OwnFired++;
            int weaponId = t.databaseweapondata.weaponID;
            Vector3 pos = t.transform.position; Vector3 eul = t.transform.eulerAngles;
            int carrierId = -1;
            if (carrier != null) _ownIds.TryGetValue(carrier, out carrierId);
            S.SendGame(G_TORP_SPAWN, w => { w.Write(id); w.Write(weaponId); WriteV3(w, pos); WriteV3(w, eul); w.Write(payload); w.Write(carrierId); w.Write(sam); }, true);
            Duel.Log("Fired " + (sam ? "SAM" : "torpedo") + " #" + id + " (" + t.databaseweapondata.weaponName + ")");
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
                // whose sub it is homing on (their player number, -1 none): only that player's game gives the callout
                int target = -1;
                if (t.targetTransform != null) foreach (Opponent o in Duel.InGame) if (o.Puppet != null && t.targetTransform == o.Puppet.transform) { target = o.Id; break; }
                // targetNoisemaker is a scratch field; the chosen target is targetTransform.
                Noisemaker nm = t.targetTransform != null ? t.targetTransform.GetComponent<Noisemaker>() : null;
                Torpedo dt = (nm == null && t.targetTransform != null) ? t.targetTransform.GetComponent<Torpedo>() : null;
                bool decoyed = (nm != null && nm.databasecountermeasuredata != null && !nm.databasecountermeasuredata.isWakeBubble)
                    || (dt != null && dt.databaseweapondata != null && dt.databaseweapondata.weaponType == "DECOY");
                if (t.targetTransform != null && _targetLogged.Add(id))
                    Duel.Log("~Our torpedo #" + id + " first locked on " + (target >= 0 ? "player " + target + "'s vessel" : nm != null ? (nm.databasecountermeasuredata != null && nm.databasecountermeasuredata.isTowedDecoy ? "a TOWED DECOY" : "a noisemaker") : dt != null ? "a decoy" : t.targetTransform.name));
                if (decoyed != _onNoisemaker.Contains(id))
                {
                    if (decoyed) _onNoisemaker.Add(id); else _onNoisemaker.Remove(id);
                    float last;
                    if (!_decoyLogAt.TryGetValue(id, out last) || Time.time - last > 3f) // seekers flicker: log at most every 3 s
                    {
                        _decoyLogAt[id] = Time.time;
                        string what = nm != null ? "a noisemaker" : (dt != null && _ownIds.ContainsKey(dt)) ? "OUR OWN decoy" : "the opponent's decoy";
                        Duel.Log("~Our torpedo #" + id + (decoyed ? " is going for " + what : " left the noisemaker/decoy"));
                    }
                }
                S.SendGame(G_TORP_STATE, w => { w.Write(id); w.Write(time); WriteV3(w, pos); WriteV3(w, eul); w.Write(target); }, false);
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

        static void SpawnPuppetTorpedo(Opponent firer, int id, int weaponId, Vector3 pos, Vector3 eul, bool payload, bool sam)
        {
            if (firer == null || firer.Puppet == null) return;
            int key = Key(firer.Id, id);
            DatabaseWeaponData wd = UIFunctions.globaluifunctions.database.databaseweapondata[weaponId];
            GameObject go = (GameObject)Object.Instantiate(wd.weaponObject, pos, Quaternion.Euler(eul));
            go.SetActive(true);
            Torpedo t = go.GetComponent<Torpedo>();
            t.databaseweapondata = wd;
            t.vesselFiredFrom = firer.Puppet;
            t.whichNavy = 1;
            t.onWire = false;
            t.usesParachute = payload; // rocket payloads come down under a parachute
            if (sam) t.interceptMode = "AIRCRAFT"; // InitialiseTorpedo: anti-air mode, the SAM's own trail
            t.InitialiseTorpedo();
            if (payload) t.isAirborne = true;
            _puppetTorps.Add(t);
            UIFunctions.globaluifunctions.playerfunctions.sensormanager.AddTorpedoToArray(t);
            // As Enemy_Torpedo.FireTorpedo: an enemy torpedo's map icon carries no label.
            try { if (t.tacMapTorpedoIcon != null && t.tacMapTorpedoIcon.contactText != null) t.tacMapTorpedoIcon.contactText.gameObject.SetActive(false); }
            catch (System.Exception ex) { Duel.Log("~Torpedo label hide failed: " + ex.Message); }
            bool decoy = Ghosts.IsDecoy(wd);
            Incoming inc = new Incoming { Firer = firer, Id = id, T = t, Pos0 = pos, Pos1 = pos, Euler1 = eul, T0 = 0, T1 = 0, Local1 = Time.time, Count = 0, SpawnLocal = Time.time, Payload = payload, Decoy = decoy };
            _incoming[key] = inc;
            if (decoy)
            {
                // Never shown as a weapon here: the opponent's decoy becomes a ghost sub.
                Duel.Log("~" + (Duel.Opps.Count > 1 ? firer.Label : "Opponent") + " launched decoy #" + id + " (" + wd.weaponName + ")");
                try { Ghosts.OnDecoySpawned(firer, key, t, wd); } catch (System.Exception ex) { Duel.Log("~Ghost start failed: " + ex.Message); }
                return;
            }
            if (sam)
            {
                // Their anti-air missile, going for an aircraft (ours, here): shown, nothing more. Their game decides the
                // hit (Helos G_HELO_DOWN); the copy ends with theirs.
                Duel.Log((Duel.Opps.Count > 1 ? firer.Label : "Opponent") + " fired SAM #" + id + " (" + wd.weaponName + ")");
                return;
            }
            if (!payload) OppFired++;
            Duel.Log((Duel.Opps.Count > 1 ? firer.Label : "Opponent") + " fired torpedo #" + id + " (" + wd.weaponName + ")");
            bool heard = false;
            if (!payload) // dropped by a rocket far from the firer: no launch transient
            {
                try { heard = LaunchTransient(firer.Puppet, wd); }
                catch (System.Exception ex) { Duel.Log("Launch transient check failed: " + ex.Message); }
            }
            // A new torpedo's map icon starts visible until the game's next sonar check (about 1 s): an unheard
            // launch showed as a brief icon with no callout (duel test 26 Nov). Hide it; the sonar shows it when detected.
            if (!heard)
            {
                try { if (t.tacMapTorpedoIcon != null) t.tacMapTorpedoIcon.gameObject.SetActive(false); }
                catch (System.Exception ex) { Duel.Log("~Torpedo icon hide failed: " + ex.Message); }
            }
        }

        /// <summary>Same rule as the AI's launch (Enemy_Torpedo.FireTorpedo): if our sonar can hear the
        /// launch transient, give the "launch transient" sonar report with its bearing.</summary>
        static bool LaunchTransient(Vessel firer, DatabaseWeaponData wd)
        {
            Vessel own = GameDataManager.playervesselsonlevel[0];
            if (own == null || firer == null) return false;
            if (wd.swimOut) { Duel.Log("Launch transient: none, " + wd.weaponName + " swims out (as in the game)"); return false; }
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            SensorManager sm = pf.sensormanager;
            float heard = sm.CheckCanHearTransient(own, firer);
            if (heard <= 10f) { Duel.Log("Launch transient not heard (" + heard.ToString("F1") + ")"); return false; }
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
            return true;
        }

        /// <summary>Called from the Torpedo.FixedUpdate prefix for puppet torpedoes instead of the game's logic.</summary>
        public static void DrivePuppetTorpedo(Torpedo t)
        {
            int id = -1; Incoming inc = null;
            foreach (KeyValuePair<int, Incoming> kv in _incoming) if (kv.Value.T == t) { id = kv.Key; inc = kv.Value; break; }
            if (inc == null) return;
            string tag = Tag(inc.Firer, inc.Id);

            // A missile's smoke trail is switched on in Torpedo.FixedUpdate once it is out of the water, which a copy skips:
            // the Igla copy flew with no trail and could not be seen (Raf, 28 Nov). Switch it on here the same way.
            if (t.missileTrail != null && !t.missileTrail.gameObject.activeSelf && t.transform.position.y > 1000f && !t.shotDown && !t.destroyMe)
            {
                try { t.missileTrail.gameObject.SetActive(true); t.missileTrail.Play(); }
                catch (System.Exception) { }
            }

            // A missile shot down here, by our guns or SAMs (Torpedo.ShootMissileDown). The copy only follows the firer's
            // states, so it burned on and the firer never knew (Raf, 27 Nov). Our game decides: tell everyone, remove ours.
            if (t.shotDown)
            {
                int firerId = inc.Firer.Id, torpId = inc.Id;
                S.SendGame(G_TORP_DOWN, w => { w.Write(firerId); w.Write(torpId); }, true);
                Duel.Log(tag + " SHOT DOWN by us");
                Kill(id, true);
                return;
            }
            if (t.destroyMe && _decoyedCopies.Remove(t))
            {
                Kill(id, true); // reported in CopyDestroyedByDecoy
                return;
            }
            if (t.destroyMe && t.damageDealt == 0)
            {
                // Flagged without hitting anything (mission teardown, noisemaker, ...): not a hit.
                Duel.Log((inc.Decoy ? "~" : "") + tag + " removed here without a hit");
                Kill(id, false);
                return;
            }
            if (t.destroyMe && Ambient.TookNeutralHit(t))
            {
                Ambient.SendHit(inc.Firer.Id, inc.Id);
                Kill(id, true);
                return;
            }
            if (t.destroyMe)
            {
                // Compartment.OnTriggerEnter counted a hit on our sub (damageDealt) and applied damage.
                int firerId = inc.Firer.Id, torpId = inc.Id;
                S.SendGame(G_TORP_HIT, w => { w.Write(firerId); w.Write(torpId); }, true);
                Duel.Log(tag + " hit us");
                OppHits++;
                Kill(id, true);
                return;
            }
            if (inc.EndAt > 0f && Time.time > inc.EndAt)
            {
                Duel.Log((inc.Decoy ? "~" : "") + tag + (inc.DroppedPayload ? " ended (carrier, payload was released)" : " ended (miss)"));
                Kill(id, false);
                return;
            }
            // The game arms a torpedo's hit collider in Torpedo.FixedUpdate, which puppets skip.
            // Player torpedoes (navy 0 on the firer's side) arm after 5 s; do the same here.
            if (!inc.Armed && !inc.Decoy && Time.time - inc.SpawnLocal > 5f) Arm(t, inc);
            if (inc.Payload && t.isAirborne && t.transform.position.y < 1000f) Splashdown(t);
            // "Torpedo in the water" callout: the game gives it when a torpedo targets the player's sub.
            Vessel own = GameDataManager.playervesselsonlevel[0];
            // Hold the lock for 2 s after the flag drops: states are unreliable and seekers flicker.
            if (own != null) t.targetTransform = (inc.Homing || Time.time - inc.LastHoming < 2f) ? own.transform : null;
            // Diagnostics: log the first few times the opponent torpedo's map icon shows or hides (it blinks with
            // the sonar's detection checks afterwards).
            if (!inc.Decoy && t.tacMapTorpedoIcon != null && inc.IconChanges < 4)
            {
                int icon = t.tacMapTorpedoIcon.gameObject.activeInHierarchy ? 1 : 0;
                if (icon != inc.IconState)
                {
                    Duel.Log("~" + Tag(inc.Firer, inc.Id) + " map icon " + (icon == 1 ? "shown" : "hidden") + " at +" + (Time.time - inc.SpawnLocal).ToString("F1") + " s");
                    inc.IconState = icon;
                    inc.IconChanges++;
                }
            }

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

        /// <summary>What Torpedo.FixedUpdate does when a parachuted payload enters the water.</summary>
        static void Splashdown(Torpedo t)
        {
            t.isAirborne = false;
            if (t.tacMapTorpedoIcon != null) t.tacMapTorpedoIcon.gameObject.SetActive(true);
            Vector3 p = t.transform.position;
            ObjectPoolManager.CreatePooled(UIFunctions.globaluifunctions.database.splashes[0], new Vector3(p.x, 1000f, p.z), Quaternion.identity);
            if (t.parachute != null) { t.parachute.Stop(); t.parachute.gameObject.SetActive(false); }
        }

        // ---------- noisemakers ----------

        /// <summary>Our noisemaker: the opponent's torpedo seekers run in their game, so it must exist there.</summary>
        public static void OnPlayerNoisemaker(int cmId, Vector3 pos, Quaternion rot)
        {
            if (!Active) return;
            Vector3 e = rot.eulerAngles;
            S.SendGame(G_NOISEMAKER, w => { w.Write(cmId); WriteV3(w, pos); WriteV3(w, e); }, true);
            Duel.Log("Dropped noisemaker");
        }

        /// <summary>Our sub formed a knuckle (hard turn at speed, PlayerFunctions.CheckIfKnuckle).</summary>
        public static void OnPlayerKnuckle(Vector3 pos)
        {
            if (!Active) return;
            S.SendGame(G_KNUCKLE, w => WriteV3(w, pos), true);
            Duel.Log("Formed a knuckle");
        }

        /// <summary>Same as an AI sub's knuckle (VesselAI): a knuckle noisemaker in the water, no sonar callout.</summary>
        static void SpawnOpponentKnuckle(Vector3 pos)
        {
            UIFunctions ui = UIFunctions.globaluifunctions;
            DatabaseCountermeasureData cd = ui.database.databasecountermeasuredata[ui.playerfunctions.sensormanager.knuckleID];
            Noisemaker n = ObjectPoolManager.CreatePooled(cd.countermeasureObject, pos, Quaternion.identity).GetComponent<Noisemaker>();
            n.gameObject.SetActive(true);
            n.tacMapNoisemakerIcon.shipDisplayIcon.color = ui.playerfunctions.sensormanager.tacticalmap.navyColors[1];
            n.databasecountermeasuredata = cd;
            n.playerGenerated = false;
            ui.playerfunctions.sensormanager.AddNoiseMakerToArray(n);
            Duel.Log("Opponent formed a knuckle");
        }

        /// <summary>Same as an AI sub's drop (Enemy_Noisemaker.DropNoisemaker).</summary>
        static void SpawnOpponentNoisemaker(int cmId, Vector3 pos, Vector3 eul)
        {
            UIFunctions ui = UIFunctions.globaluifunctions;
            DatabaseCountermeasureData cd = ui.database.databasecountermeasuredata[cmId];
            Noisemaker n = ObjectPoolManager.CreatePooled(cd.countermeasureObject, pos, Quaternion.Euler(eul)).GetComponent<Noisemaker>();
            n.gameObject.SetActive(true);
            n.databasecountermeasuredata = cd;
            ui.playerfunctions.sensormanager.AddNoiseMakerToArray(n);
            n.tacMapNoisemakerIcon.shipDisplayIcon.color = ui.playerfunctions.sensormanager.tacticalmap.navyColors[1];
            Vessel own = GameDataManager.playervesselsonlevel[0];
            Transform nav = own.acoustics.sensorNavigator.transform;
            nav.LookAt(pos);
            string brg = nav.eulerAngles.y.ToString("0");
            ui.playerfunctions.voicemanager.currentNoisemakerBearing = nav.eulerAngles.y.ToString("000");
            string msg = LanguageManager.GetDictionaryString(LanguageManager.messageLogDictionary, "NoisemakerDropped").Replace("<BRG>", brg);
            ui.playerfunctions.PlayerMessage(msg, ui.playerfunctions.messageLogColors["Sonar"], "NoisemakerDropped");
            Duel.Log("Opponent dropped a noisemaker, bearing " + brg);
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
            if (inc.Decoy) Ghosts.OnDecoyEnded(id);
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
            if (sinking && !_lastSentSinking) Duel.OnOwnSinking();
            _lastSentDamage = frac; _lastSentSinking = sinking;
            S.SendGame(G_DAMAGE, w => { w.Write(frac); w.Write(sinking); }, true);
            Duel.Log("Our damage " + (frac * 100f).ToString("F0") + "%" + (sinking ? ", SINKING" : ""));
        }

        static void ApplyOpponentDamage(Opponent o, float frac, bool sinking)
        {
            Vessel p = o != null ? o.Puppet : null;
            if (p == null) return;
            p.damagesystem.shipCurrentDamagePoints = frac * p.damagesystem.shipTotalDamagePoints;
            Duel.Log((Duel.Opps.Count > 1 ? o.Label : "Opponent") + " damage " + (frac * 100f).ToString("F0") + "%" + (sinking ? ", SINKING" : ""));
            if (sinking) Duel.OnOppSinking(o);
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
            int from = S.LastSender;
            switch (type)
            {
                case G_TORP_SPAWN:
                {
                    int id = r.ReadInt32(); int wid = r.ReadInt32(); Vector3 p = ReadV3(r); Vector3 e = ReadV3(r); bool payload = r.ReadBoolean(); int carrierId = r.ReadInt32(); bool sam = r.ReadBoolean();
                    if (wid < 0 || wid >= UIFunctions.globaluifunctions.database.databaseweapondata.Length) { Duel.Log("Ignored a torpedo with an unknown weapon number " + wid); return true; }
                    Opponent firer = Duel.InGameById(from);
                    Incoming carrier;
                    if (carrierId > 0 && _incoming.TryGetValue(Key(from, carrierId), out carrier))
                    {
                        carrier.DroppedPayload = true;
                        Duel.Log(Tag(carrier.Firer, carrierId) + " released its payload");
                        Kill(Key(from, carrierId), false); // the payload takes over from here
                    }
                    if (Duel.InDuel && firer != null) SpawnPuppetTorpedo(firer, id, wid, p, e, payload, sam);
                    return true;
                }
                case G_TORP_STATE:
                {
                    int id = r.ReadInt32(); float time = r.ReadSingle(); Vector3 p = ReadV3(r); Vector3 e = ReadV3(r); int target = r.ReadInt32();
                    bool homing = target == S.MyId;
                    Incoming inc;
                    if (_incoming.TryGetValue(Key(from, id), out inc) && time > inc.T1 && inc.EndAt < 0f)
                    {
                        inc.Pos0 = inc.Pos1; inc.T0 = inc.T1; inc.Pos1 = p; inc.T1 = time; inc.Euler1 = e; inc.Local1 = Time.time; inc.Count++;
                        if (homing && !inc.HomingLogged) { inc.HomingLogged = true; Duel.Log(Tag(inc.Firer, id) + " is homing on us"); }
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
                    if (_incoming.TryGetValue(Key(from, id), out inc) && inc.EndAt < 0f) inc.EndAt = Time.time + 1.0f;
                    return true;
                }
                case G_TORP_HIT:
                {
                    // From the player who was hit: (firer, torpedo number).
                    int firerId = r.ReadInt32(); int id = r.ReadInt32();
                    Opponent victim = Duel.InGameById(from);
                    if (firerId == S.MyId)
                    {
                        Torpedo t;
                        Duel.Log("Our torpedo #" + id + " HIT " + (Duel.Opps.Count > 1 && victim != null ? victim.Label : "the opponent"));
                        OwnHits++;
                        Ghosts.OnOpponentHit(victim);
                        if (_own.TryGetValue(id, out t) && t != null) t.destroyMe = true; // explode here too
                        _own.Remove(id);
                    }
                    else
                    {
                        // Someone else's torpedo hit a third player: explode our copy of it there.
                        Incoming inc;
                        if (_incoming.TryGetValue(Key(firerId, id), out inc))
                        {
                            Duel.Log(Tag(inc.Firer, id) + " hit " + (victim != null ? victim.Label : "player " + from));
                            Kill(Key(firerId, id), true);
                        }
                    }
                    return true;
                }
                case G_NOISEMAKER:
                {
                    int cm = r.ReadInt32(); Vector3 p = ReadV3(r); Vector3 e = ReadV3(r);
                    if (cm < 0 || cm >= UIFunctions.globaluifunctions.database.databasecountermeasuredata.Length) { Duel.Log("Ignored an unknown countermeasure " + cm); return true; }
                    if (Duel.InDuel)
                    {
                        try { SpawnOpponentNoisemaker(cm, p, e); }
                        catch (System.Exception ex) { Duel.Log("Noisemaker spawn failed: " + ex.Message); }
                    }
                    return true;
                }
                case G_TORP_DOWN:
                {
                    // From a player whose guns or SAMs shot down a missile: (firer, number).
                    int firerId = r.ReadInt32(); int id = r.ReadInt32();
                    Opponent by = Duel.InGameById(from);
                    string who = by != null && Duel.Opps.Count > 1 ? by.Label : "the opponent";
                    if (firerId == S.MyId)
                    {
                        Torpedo t;
                        if (_own.TryGetValue(id, out t) && t != null && !t.shotDown)
                        {
                            try { t.ShootMissileDown(false); } catch (System.Exception ex) { Duel.Log("~Shoot-down failed: " + ex.Message); t.destroyMe = true; }
                        }
                        Duel.Log("Our missile #" + id + " was SHOT DOWN by " + who);
                    }
                    else
                    {
                        Incoming inc;
                        if (_incoming.TryGetValue(Key(firerId, id), out inc)) { Duel.Log(Tag(inc.Firer, id) + " shot down by " + who); Kill(Key(firerId, id), true); }
                    }
                    return true;
                }
                case G_TORP_DECOYED:
                {
                    // From the player whose towed decoy destroyed a torpedo: (firer, number).
                    int firerId = r.ReadInt32(); int id = r.ReadInt32();
                    Opponent by = Duel.InGameById(from);
                    string whose = by != null && Duel.Opps.Count > 1 ? by.Label + "'s" : "the opponent's";
                    if (firerId == S.MyId)
                    {
                        Torpedo t;
                        if (_own.TryGetValue(id, out t) && t != null && !t.destroyMe) t.destroyMe = true; // what DotMod's decoy does to it
                        Duel.Log("Our torpedo #" + id + " was destroyed by " + whose + " towed decoy");
                    }
                    else
                    {
                        Incoming inc;
                        if (_incoming.TryGetValue(Key(firerId, id), out inc)) { Duel.Log(Tag(inc.Firer, id) + " destroyed by " + whose + " towed decoy"); Kill(Key(firerId, id), true); }
                    }
                    return true;
                }
                case G_KNUCKLE:
                {
                    Vector3 p = ReadV3(r);
                    if (Duel.InDuel)
                    {
                        try { SpawnOpponentKnuckle(p); }
                        catch (System.Exception ex) { Duel.Log("Knuckle spawn failed: " + ex.Message); }
                    }
                    return true;
                }
                case G_DAMAGE:
                {
                    float f = r.ReadSingle(); bool sinking = r.ReadBoolean();
                    ApplyOpponentDamage(Duel.InGameById(from), f, sinking);
                    return true;
                }
                case G_EXPLOSION:
                {
                    int magnitude = r.ReadInt32();
                    Vector3 local = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); // where on their vessel
                    Opponent o = Duel.InDuel ? Duel.InGameById(from) : null;
                    if (o != null && o.Puppet != null)
                    {
                        try { UIFunctions.globaluifunctions.database.CreateExplosion(o.Puppet, magnitude, o.Puppet.transform.TransformPoint(local)); }
                        catch (System.Exception ex) { Duel.Log("~Secondary explosion failed: " + ex.Message); }
                        Duel.Log("~Secondary explosion on " + (Duel.Opps.Count > 1 ? o.Label + "'s" : "the opponent's") + " sub (" + magnitude + ")");
                    }
                    return true;
                }
                case G_DECAL:
                {
                    string comp = r.ReadString(); bool destroyed = r.ReadBoolean(); int decal = r.ReadInt32();
                    if (Duel.InDuel) ApplyOpponentDecal(Duel.InGameById(from), comp, destroyed, decal);
                    return true;
                }
            }
            return false;
        }

        // ---------- hull damage on the outside (holes) ----------
        // Compartment.ApplyDamageDecal puts a random hole decal on the hit compartment, only in the game of the sub that
        // was hit. That game picks the decal, applies it and tells the others, who put the same one on its puppet.

        internal static bool ApplyingRemoteDecal;

        /// <summary>ApplyDamageDecal prefix on our own sub: choose the decal here so it can be sent.</summary>
        public static void BeforeOwnDecal(Compartment c, bool destroyed, ref int placedDecalID)
        {
            if (!Active || ApplyingRemoteDecal || c == null || c.activeVessel == null || !c.activeVessel.playercontrolled) return;
            if (c.destroyedDecalPlaced || placedDecalID != -1) return; // nothing new (or a campaign reload)
            Material[] mats = destroyed ? UIFunctions.globaluifunctions.database.destroyedDecals : UIFunctions.globaluifunctions.database.damageDecals;
            if (mats == null || mats.Length == 0) return;
            int id = Random.Range(0, mats.Length);
            placedDecalID = id;
            string comp = c.name;
            S.SendGame(G_DECAL, w => { w.Write(comp); w.Write(destroyed); w.Write(id); }, true);
            Duel.Log("~Hull damage decal on our compartment " + comp + (destroyed ? " (destroyed)" : "") + ", sent");
        }

        static void ApplyOpponentDecal(Opponent o, string comp, bool destroyed, int decal)
        {
            if (o == null || o.Puppet == null) return;
            Material[] mats = destroyed ? UIFunctions.globaluifunctions.database.destroyedDecals : UIFunctions.globaluifunctions.database.damageDecals;
            if (mats == null || decal < 0 || decal >= mats.Length) return;
            foreach (Compartment c in o.Puppet.GetComponentsInChildren<Compartment>(true))
            {
                if (c == null || c.name != comp) continue;
                ApplyingRemoteDecal = true;
                try { c.ApplyDamageDecal(destroyed, decal); }
                catch (System.Exception ex) { Duel.Log("~Hull damage decal failed: " + ex.Message); }
                finally { ApplyingRemoteDecal = false; }
                Duel.Log("~Hull damage decal on " + (Duel.Opps.Count > 1 ? o.Label + "'s" : "the opponent's") + " compartment " + comp + (destroyed ? " (destroyed)" : ""));
                return;
            }
            Duel.Log("~Hull damage decal: no compartment " + comp + " on the puppet");
        }

        /// <summary>A neutral took our copy of another player's torpedo (the host says so): explode it here.</summary>
        public static void OtherTorpedoHitNeutral(int firerId, int id)
        {
            if (_incoming.ContainsKey(Key(firerId, id))) Kill(Key(firerId, id), true);
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

    /// <summary>Player SAM launch (WeaponSource.FireAAM: Instantiate + InitialiseTorpedo, like FireTube). Sent so the
    /// other games show the missile; before this the helicopter's owner saw no SAM and no shoot-down (Raf, 28 Nov).</summary>
    [HarmonyPatch(typeof(WeaponSource), "FireAAM")]
    static class Patch_FireAAM
    {
        static void Prefix() { Patch_FireTube.InFireTube = true; Patch_FireTube.Launched = null; }

        static void Postfix()
        {
            Patch_FireTube.InFireTube = false;
            Torpedo t = Patch_FireTube.Launched;
            Patch_FireTube.Launched = null;
            if (Duel.InDuel && t != null) Weapons.OnPlayerFired(t, false, null, true);
        }

        static System.Exception Finalizer(System.Exception __exception)
        {
            Patch_FireTube.InFireTube = false;
            Patch_FireTube.Launched = null;
            return __exception;
        }
    }

    /// <summary>The player's helicopter drops its torpedo itself (DotModPlayerAircraft.DropWeapon: Instantiate +
    /// InitialiseTorpedo), not through FireTube. Capture it the same way and send it as a launch from the drop point.</summary>
    [HarmonyPatch(typeof(DotModPlayerAircraft), "DropWeapon")]
    static class Patch_HelicopterDropWeapon
    {
        static void Prefix() { Patch_FireTube.InFireTube = true; Patch_FireTube.Launched = null; }

        static void Postfix()
        {
            Patch_FireTube.InFireTube = false;
            Torpedo t = Patch_FireTube.Launched;
            Patch_FireTube.Launched = null;
            // Sent like a rocket's payload (no carrier): the others get no launch transient from our ship's position.
            if (Duel.InDuel && t != null) { Duel.Log("Helicopter dropped a torpedo"); Weapons.OnPlayerFired(t, true, null); }
        }

        static System.Exception Finalizer(System.Exception __exception)
        {
            Patch_FireTube.InFireTube = false;
            Patch_FireTube.Launched = null;
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
            return !(Duel.InDuel && (Duel.IsPuppetAI(__instance) || Ghosts.IsGhostAI(__instance)));
        }
    }

    [HarmonyPatch(typeof(CombatAI), "CheckToAttackPlayer")]
    static class Patch_CheckToAttackPlayer
    {
        static bool Prefix(Vessel activeVessel) { return !Duel.IsRemote(activeVessel); }
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

    /// <summary>Player noisemaker drops (WeaponSource.DropNoisemaker is also used by nothing else for the player).</summary>
    [HarmonyPatch(typeof(PlayerFunctions), "CheckIfKnuckle")]
    static class Patch_CheckIfKnuckle
    {
        static void Prefix(out bool __state) { __state = SensorManager.playerKnuckle; }

        static void Postfix(PlayerFunctions __instance, bool __state)
        {
            if (!Duel.InDuel || __state || !SensorManager.playerKnuckle) return; // no new knuckle
            Weapons.OnPlayerKnuckle(__instance.playerVessel.vesselmovement.rudder[0].position);
        }
    }

    [HarmonyPatch(typeof(WeaponSource), "DropNoisemaker")]
    static class Patch_DropNoisemaker
    {
        static void Prefix(WeaponSource __instance, out int __state) { __state = __instance.noisemakersOnBoard; }

        static void Postfix(WeaponSource __instance, int __state)
        {
            if (!Duel.InDuel || __instance.noisemakersOnBoard >= __state) return; // nothing dropped
            Vessel v = __instance.parentVesselMovement.parentVessel;
            if (v == null || !v.playercontrolled) return;
            Weapons.OnPlayerNoisemaker(v.databaseshipdata.noiseMakerID, __instance.noisemakerTubes.position, __instance.noisemakerTubes.rotation);
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
            if (Duel.InDuel) { try { RoundDiag.OnTouch(__instance, otherObject); } catch (System.Exception) { } }
            if (Ghosts.IsGhost(__instance.activeVessel)) return false; // ghosts can't be hit
            if (Ambient.IsNeutral(__instance.activeVessel)) return Ambient.AllowHit(__instance, otherObject); // the host decides
            if (!Duel.IsPuppet(__instance.activeVessel)) return true;
            // Torpedoes, mortar rounds and shells: the puppet's own player decides (their game has a copy).
            if (otherObject == null) return true;
            if (otherObject.GetComponent<Torpedo>() != null) return false;
            if (Rounds.IsRound(otherObject)) return false;
            return true;
        }
    }
}

namespace ColdWatersMP
{
    /// <summary>Hull holes: send the ones on our own sub to the other players (Weapons.BeforeOwnDecal).</summary>
    [HarmonyPatch(typeof(Compartment), "ApplyDamageDecal")]
    static class Patch_ApplyDamageDecal
    {
        static void Prefix(Compartment __instance, bool destroyed, ref int placedDecalID)
        {
            try { Weapons.BeforeOwnDecal(__instance, destroyed, ref placedDecalID); }
            catch (System.Exception ex) { Duel.Log("~Hull damage decal send failed: " + ex.Message); }
        }
    }
}

namespace ColdWatersMP
{
    /// <summary>Secondary explosions (Compartment.ApplyExplosion after a hit, DamageSystem.CheckBurning later): the game
    /// draws a fireball and smoke cloud at the surface above the damaged vessel, only in the game of the sub that was hit.
    /// Ours go to the other players, who show the same one above our puppet.</summary>
    [HarmonyPatch(typeof(Database), "CreateExplosion")]
    static class Patch_CreateExplosion
    {
        static void Prefix(Vessel activeVessel, int magnitude, Vector3 explosionPosition)
        {
            try
            {
                if (!Duel.InDuel || Duel.Session == null || activeVessel == null || !activeVessel.playercontrolled) return;
                int m = magnitude;
                // Where on our vessel (its own frame), so the others show it at the same spot on our puppet: a shell hit
                // flashes where it hit, a fire where it burns (before: always at the vessel's centre).
                Vector3 local = activeVessel.transform.InverseTransformPoint(explosionPosition);
                Duel.Session.SendGame(Weapons.G_EXPLOSION, w => { w.Write(m); w.Write(local.x); w.Write(local.y); w.Write(local.z); }, true);
                Duel.Log("~Secondary explosion on our sub (" + m + "), sent");
            }
            catch (System.Exception ex) { Duel.Log("~Secondary explosion send failed: " + ex.Message); }
        }
    }
}
