// Cold Waters MP - a dipping sonar for the player's helicopter (docs/SURFACE.md). C# 5 / .NET 3.5.
// DotMod's player helicopter (DotModPlayerAircraft) has no sensors: it flies waypoints and drops torpedoes. The AI
// helicopter dips (Helicopter.CheckDippingSonar) with its type's sonar (SH-60B AQS-13F, Helix, Hormone, Lynx, Z-9 ...).
// Here a player helicopter with a dipping sonar hovers at its last waypoint (instead of DotMod's orbit) and dips: after each of the game's own sonar sweeps
// (SensorManager.SonarCheck, every 8 s of game time) it listens from its own position with the sums the game uses for a
// dipping AI helicopter (SensorManager.CheckSingleSonar, HELICOPTER branch): spreading and attenuation loss, sea ambient
// (a helicopter has no own noise), layer bonus (a dip counts as above the layer), convergence zone bonus, masking by
// terrain or a louder vessel on the line, land masking, its sonar's passive sensitivity, and for an active dip the
// game's active return (sonar output, target reflection and aspect, active sensitivity). Not copied: the game's call
// only works for AI listeners and counts the player's reflection and raw vessel noise, so the target's noise here is
// the one our own sonar hears (GetVesselNoiseLevel, with the multiplayer corrections). A detection goes into the
// player's contact picture (ContactDetected, TMA update); the ship's sweep does not drop a contact the helicopter heard
// in the last 12 s. Active when the ship's active sonar is on; an active dip is announced to the other players.
// Applies in single player too. Untested (28 Nov).
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Dipping
    {
        public const byte G_DIP_PING = 25;
        const float HoldSeconds = 12f;   // keep a helicopter contact this long without a new detection

        static readonly Dictionary<int, float> _heldUntil = new Dictionary<int, float>(); // enemy index -> time
        static readonly Dictionary<DotModPlayerAircraft, bool> _dipping = new Dictionary<DotModPlayerAircraft, bool>();
        static System.Reflection.MethodInfo _noise, _cz, _activeNoise, _calculateTma;

        public static bool Holds(int i)
        {
            float t;
            return _heldUntil.TryGetValue(i, out t) && Time.time < t;
        }

        public static void Reset()
        {
            _heldUntil.Clear(); _dipping.Clear();
            foreach (Gear g in _gear.Values) if (g.Line != null) Object.Destroy(g.Line.gameObject);
            _gear.Clear();
        }

        /// <summary>A helicopter type with a dipping sonar (aircraft data PassiveSonarModel).</summary>
        public static bool HasDippingSonar(DotModPlayerAircraft a)
        {
            return a != null && a.databaseaircraftdata != null && a.databaseaircraftdata.aircraftType == "HELICOPTER" && a.databaseaircraftdata.passiveSonarID >= 0;
        }

        /// <summary>Hovering at its waypoint (DotMod's orbit state, held still by Patch_AircraftOrbit_Hover).</summary>
        public static bool IsHovering(DotModPlayerAircraft a)
        {
            return a.IsAirborne && !a.IsDestroyed && HasDippingSonar(a) && a.State == "AircraftOrbit";
        }

        /// <summary>Hovering with the sonar fully lowered: only then does it listen (as the AI: Helicopter.FixedUpdate
        /// calls CheckDippingSonar once the cable is down).</summary>
        public static bool IsDipping(DotModPlayerAircraft a)
        {
            Gear g;
            return IsHovering(a) && _gear.TryGetValue(a, out g) && g.Height <= CableDown + 0.001f;
        }

        // ---------- the dip itself: descend, pay out the cable, reel in ----------
        // As the AI helicopter: it flies at dipaltitude while dipping (Helicopter.Navigation) and lowers its sonar
        // (a LineRenderer at the type's DippingSonarPosition) 0.02 units/s to 0.35 units (Helicopter.FixedUpdate).
        // DotMod builds the player's helicopter without the cable (CreateAndPlaceAircraftMeshes skips
        // DippingSonarPosition for player aircraft), so the cable comes from the game's helicopter template, placed where
        // an AI copy of the same type has it.
        const float CableDown = -0.35f, CableRate = 0.02f, DescentRate = 0.12f, DipHeight = 1000.25f;

        class Gear { public LineRenderer Line; public float Height; }
        static readonly Dictionary<DotModPlayerAircraft, Gear> _gear = new Dictionary<DotModPlayerAircraft, Gear>();
        static readonly Dictionary<int, Vector3> _cableSpot = new Dictionary<int, Vector3>();   // aircraft type -> local position
        static GameObject _template;
        static float _dipAltitude = -1f;

        static GameObject Template()
        {
            if (_template == null) _template = Resources.Load("template_objects/helicopterTemplate") as GameObject;
            return _template;
        }

        static float DipAltitude()
        {
            if (_dipAltitude < 0f)
            {
                float v = 0f;
                try { GameObject t = Template(); Helicopter h = t != null ? t.GetComponent<Helicopter>() : null; if (h != null) v = h.dipaltitude; }
                catch (System.Exception) { }
                // The template says 1001.75 (about 390 ft): far too high for a dip next to the AI's (Raf, 28 Nov). About
                // 55 ft (0.25 units), so the 0.35-unit cable ends some 8 yd under water.
                _dipAltitude = DipHeight;
                Duel.Log("~Dipping altitude " + _dipAltitude.ToString("F3") + " (template " + v.ToString("F3") + ")");
            }
            return _dipAltitude;
        }

        /// <summary>Where this type hangs its cable, relative to the aircraft: from a throwaway AI copy, once per type.</summary>
        static Vector3 CableSpot(int aircraftId)
        {
            Vector3 spot;
            if (_cableSpot.TryGetValue(aircraftId, out spot)) return spot;
            spot = Vector3.zero;
            GameObject go = null;
            try
            {
                go = UIFunctions.globaluifunctions.vesselbuilder.CreateAircraft(aircraftId, new Vector3(0f, -500f, 0f), Quaternion.identity, true);
                Helicopter h = go != null ? go.GetComponent<Helicopter>() : null;
                if (h != null && h.sonarLine != null) spot = go.transform.InverseTransformPoint(h.sonarLine.transform.position);
            }
            catch (System.Exception ex) { Duel.Log("~Dipping cable position not found: " + ex.Message); }
            finally { if (go != null) { go.SetActive(false); Object.Destroy(go); } }
            _cableSpot[aircraftId] = spot;
            return spot;
        }

        static Gear GearOf(DotModPlayerAircraft a)
        {
            Gear g;
            if (_gear.TryGetValue(a, out g) && (g.Line != null || g.Height == 0f)) return g;
            g = new Gear();
            try
            {
                GameObject t = Template();
                Helicopter th = t != null ? t.GetComponent<Helicopter>() : null;
                if (th != null && th.sonarLine != null)
                {
                    GameObject lg = (GameObject)Object.Instantiate(th.sonarLine.gameObject);
                    lg.name = "CWMP dipping sonar";
                    Transform holder = Traverse.Create(a).Field("meshHolder").GetValue<Transform>() ?? a.transform;
                    lg.transform.SetParent(holder, false);
                    lg.transform.localPosition = CableSpot(a.databaseaircraftdata.aircraftID);
                    lg.transform.localRotation = Quaternion.identity;
                    g.Line = lg.GetComponent<LineRenderer>();
                    lg.SetActive(false);
                }
            }
            catch (System.Exception ex) { Duel.Log("~Dipping cable not made: " + ex.Message); }
            _gear[a] = g;
            return g;
        }

        static void SetCable(Gear g, float h)
        {
            g.Height = h;
            if (g.Line == null) return;
            g.Line.gameObject.SetActive(h < 0f);
            if (h < 0f) g.Line.SetPosition(1, new Vector3(0f, h, 0f));
        }

        /// <summary>Each frame while hovering (orbit state): down to dipping height, then the cable down.</summary>
        public static void HoverTick(DotModPlayerAircraft a)
        {
            a.DemandSpeed = 0f;
            a.CurrentSpeed = 0f;
            Gear g = GearOf(a);
            Vector3 p = a.transform.position;
            float dipY = DipAltitude();
            if (Mathf.Abs(p.y - dipY) > 0.001f)
            {
                p.y = Mathf.MoveTowards(p.y, dipY, DescentRate * Time.deltaTime);
                a.transform.position = p;
                return;
            }
            if (g.Height > CableDown) SetCable(g, Mathf.MoveTowards(g.Height, CableDown, CableRate * Time.deltaTime));
        }

        /// <summary>After DotMod's Update: reel the cable in when the helicopter is no longer hovering.</summary>
        public static void AfterAircraftUpdate(DotModPlayerAircraft a)
        {
            Gear g;
            if (!_gear.TryGetValue(a, out g) || g.Height == 0f) return;
            if (!IsHovering(a)) SetCable(g, 0f);
        }

        /// <summary>The cable's length now (0 = stowed, -0.35 = down), for the other players' copies.</summary>
        public static float CableOf(DotModPlayerAircraft a)
        {
            Gear g;
            return _gear.TryGetValue(a, out g) ? g.Height : 0f;
        }

        /// <summary>Another player's helicopter copy (built as an AI helicopter, so it has its cable): show the length.</summary>
        public static void ShowCable(Helicopter h, float height)
        {
            if (h == null || h.sonarLine == null) return;
            h.sonarLine.gameObject.SetActive(height < 0f);
            if (height < 0f) h.sonarLine.SetPosition(1, new Vector3(0f, height, 0f));
        }

        /// <summary>SonarCheck postfix: every hovering player helicopter with a sonar listens (or pings) once.</summary>
        public static void AfterSonarCheck(SensorManager sm)
        {
            List<DotModPlayerAircraft> all = DotModPlayerAircraft.PlayerAircraft;
            if (all == null || sm == null) return;
            Vessel own = GameDataManager.playervesselsonlevel != null && GameDataManager.playervesselsonlevel.Length > 0 ? GameDataManager.playervesselsonlevel[0] : null;
            if (own == null) return;
            foreach (DotModPlayerAircraft a in all)
            {
                if (a == null || a.databaseaircraftdata == null) continue;
                bool dip = IsDipping(a);
                bool was;
                _dipping.TryGetValue(a, out was);
                if (dip != was)
                {
                    _dipping[a] = dip;
                    Say(dip ? "Helicopter is dipping its sonar" : "Helicopter has raised its sonar");
                }
                if (!dip) continue;
                bool active = own.acoustics != null && own.acoustics.usingActiveSonar && a.databaseaircraftdata.activeSonarID >= 0;
                Listen(sm, a, active);
                if (active) AnnouncePing(a.transform.position);
            }
        }

        static void Listen(SensorManager sm, DotModPlayerAircraft a, bool active)
        {
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            if (ev == null) return;
            DatabaseSonarData[] sonars = UIFunctions.globaluifunctions.database.databasesonardata;
            DatabaseSonarData ps = sonars[a.databaseaircraftdata.passiveSonarID];
            DatabaseSonarData asd = active ? sonars[a.databaseaircraftdata.activeSonarID] : null;
            Transform ht = a.transform;
            Vector3 hp = ht.position;
            float ambient = sm.currentOceanAmbientNoise;   // a helicopter has no own noise (the game's listener ambient)
            float threshold = sm.detectionThresholds.x;
            if (_noise == null)
            {
                _noise = AccessTools.Method(typeof(SensorManager), "GetVesselNoiseLevel");
                _cz = AccessTools.Method(typeof(SensorManager), "GetConvergenceZoneBonus");
                _activeNoise = AccessTools.Method(typeof(SensorManager), "GetActiveSonarNoise");
                _calculateTma = AccessTools.Method(typeof(SensorManager), "CalculatePlayerTMA");
            }
            int found = 0;
            for (int i = 0; i < ev.Length; i++)
            {
                Vessel v = ev[i];
                if (v == null || v.isSinking || !v.gameObject.activeInHierarchy) continue;
                // As SensorManager.CheckSingleSonar does for a dipping AI helicopter (HELICOPTER listener branch).
                Vector3 tp = v.transform.position, dir = tp - hp;
                float dist = dir.magnitude;
                float m = dist * GameDataManager.yardsScale * 0.9144f;
                if (m < 1f) m = 1f;
                float atten = sm.attenuationFactor * m, spread = sm.spreadingFactor * Mathf.Log10(m);
                float cz = sm.convergenceZoneStrength > 0f ? (float)_cz.Invoke(sm, new object[] { m, a.gameObject, v.gameObject }) : 0f;
                bool targetAbove = v.acoustics == null || v.acoustics.currentlyAboveLayer;
                float layer = sm.GetLayerBonus(ht, true, v.transform, targetAbove); // the game counts a dip as above the layer
                float noise = (float)_noise.Invoke(sm, new object[] { v });           // as our own sonar hears this vessel
                float signal = noise - (spread + ambient - layer - cz + atten - ps.sonarPassiveSensitivity);
                float act = float.MinValue;
                if (active)
                {
                    float angle = Vector3.Angle(dir, v.transform.forward);
                    act = (float)_activeNoise.Invoke(sm, new object[] { m, spread, ambient, layer, cz, angle, v.databaseshipdata.activesonarreflection, asd.sonarOutput, atten })
                          + asd.sonarActiveSensitivity;
                }
                // Masking: terrain, or a louder vessel on the line (passive only), as the game does.
                RaycastHit hit;
                if (signal > 0f && dist > 0f && Physics.Raycast(hp, dir / dist, out hit, dist, sm.maskingLayers))
                {
                    if (hit.collider.gameObject.layer == 14) signal /= Random.Range(3f, 4f);
                    else
                    {
                        int k;
                        if (int.TryParse(hit.collider.name, out k) && k != i && k >= 0 && k < ev.Length && ev[k] != null)
                        {
                            float mk = Vector3.Distance(ev[k].transform.position, hp) * GameDataManager.yardsScale * 0.9144f;
                            if (mk < 1f) mk = 1f;
                            float masker = (float)_noise.Invoke(sm, new object[] { ev[k] }) - (sm.spreadingFactor * Mathf.Log10(mk) + ambient + sm.attenuationFactor * mk - ps.sonarPassiveSensitivity);
                            if (masker > signal) signal /= Random.Range(3f, 4f);
                        }
                    }
                }
                float land = sm.GetLandMaskingPenalty(ht, v.transform);
                if (land > 0f) { signal /= land; if (active) act /= land; }
                if (act > signal) signal = act;
                if (Duel.InDuel && Duel.IsPuppet(v)) Duel.Log("~Dip listen: vessel " + i + " at " + (m / 914.4f).ToString("F1") + " kyd, signal " + signal.ToString("F1") + " (threshold " + threshold.ToString("F1") + ", noise " + noise.ToString("F1") + ", layer " + layer.ToString("+0.0;-0.0") + ", CZ " + cz.ToString("+0.0;-0.0") + ", land x" + land.ToString("F2") + (active ? ", active " + act.ToString("F1") : "") + ")");
                if (signal <= threshold) continue;
                found++;
                bool already = sm.detectedByPlayer != null && i < sm.detectedByPlayer.Length && sm.detectedByPlayer[i];
                _heldUntil[i] = Time.time + HoldSeconds;
                try
                {
                    if (!already) sm.ContactDetected(i, "SONAR");
                    _calculateTma.Invoke(sm, new object[] { i, active });
                    if (!active) OwnSolution(sm, i, signal - threshold);
                }
                catch (System.Exception ex) { Duel.Log("~Dipping sonar contact update failed: " + ex.Message); }
                if (!already) Duel.Log("~Dipping sonar (" + (active ? "active" : "passive") + ") detected vessel " + i + " (" + v.databaseshipdata.shipPrefabName + ") at " + (m / 914.4f).ToString("F1") + " kyd, signal " + signal.ToString("F1") + " (layer " + layer.ToString("+0.0;-0.0") + ", CZ " + cz.ToString("+0.0;-0.0") + ")");
            }
        }

        const float MinGain = 3f, MaxGain = 12f;   // solution % per passive dip detection (every sweep, 8 s); balance later

        /// <summary>The difficulty's PlayerTMARate, which CalculatePlayerTMA multiplies its gains by (the AI helicopter's
        /// detection total uses EnemyTMARate the same way). 1 if it can't be read.</summary>
        static float TmaRate()
        {
            try { float r = OptionsManager.difficultySettings["PlayerTMARate"]; return r > 0f ? r : 1f; }
            catch (System.Exception) { return 1f; }
        }
        static float _solLogAt;

        /// <summary>The helicopter is a sensor of its own (Raf, 28 Nov): every passive detection builds the solution, whether
        /// the ship hears the contact or not. The game's TMA (CalculatePlayerTMA) only works from the bearing change seen
        /// from the ship, so it added nothing (hovering over the sub, the solution stayed at 46-48%). Gain by how far the
        /// signal is above the detection threshold, half a percent per dB, 3 to 12 % per sweep; the range error shrinks a
        /// little each time. Capped at the game's TMA limit.</summary>
        static void OwnSolution(SensorManager sm, int i, float margin)
        {
            float[] q = sm.solutionQualityOfContacts, err = sm.solutionRangeErrors;
            if (q == null || i >= q.Length) return;
            float cap = UIFunctions.globaluifunctions.playerfunctions.maximumPlayerTMA;
            if (cap <= 0f) cap = 100f;
            float gain = Mathf.Clamp(margin * 0.5f, MinGain, MaxGain) * TmaRate(); // as fast as the game's own TMA at this difficulty
            float before = q[i];
            q[i] = Mathf.Min(cap, q[i] + gain);
            if (err != null && i < err.Length) err[i] = Mathf.Lerp(err[i], 1f, 0.25f);
            if (Time.time - _solLogAt > 20f)
            {
                _solLogAt = Time.time;
                Duel.Log("~Dipping solution on vessel " + i + ": +" + gain.ToString("F1") + "% (TMA rate " + TmaRate().ToString("F2") + ", signal " + margin.ToString("F0") + " dB over threshold), " + before.ToString("F0") + " -> " + q[i].ToString("F0") + "% (limit " + cap.ToString("F0") + ")");
            }
        }

        static void Say(string what)
        {
            Duel.Log(what);
            try { PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions; pf.PlayerMessage(what, pf.messageLogColors["Sonar"], "CWMP"); }
            catch (System.Exception) { }
        }

        // ---------- multiplayer: an active dip is heard ----------

        static void AnnouncePing(Vector3 pos)
        {
            if (!Duel.InDuel || Duel.Session == null) return;
            Duel.Session.SendGame(G_DIP_PING, w => { w.Write(pos.x); w.Write(pos.y); w.Write(pos.z); }, true);
        }

        public static bool OnMessage(byte type, BinaryReader r)
        {
            if (type != G_DIP_PING) return false;
            Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!Duel.InDuel || Duel.Session == null || Duel.Session.LastSender == Duel.Session.MyId) return true;
            try
            {
                Vessel own = GameDataManager.playervesselsonlevel[0];
                Transform nav = own.acoustics.sensorNavigator.transform;
                nav.LookAt(new Vector3(p.x, nav.position.y, p.z));
                float brg = nav.eulerAngles.y; if (brg < 0f) brg += 360f;
                float kyd = Vector3.Distance(own.transform.position, p) * GameDataManager.yardsScale / 1000f;
                // Same range rule as hearing a vessel's ping: well beyond the dip's own reach. Announce within 30 kyd.
                if (kyd <= 30f) Say("Sonar: active dipping sonar, bearing " + brg.ToString("000"));
            }
            catch (System.Exception) { }
            return true;
        }
    }

    [HarmonyPatch(typeof(SensorManager), "SonarCheck")]
    static class Patch_SonarCheck_Dipping
    {
        static void Postfix(SensorManager __instance) { try { Dipping.AfterSonarCheck(__instance); } catch (System.Exception ex) { Duel.Log("~Dipping sonar failed: " + ex.Message); } }
    }

    /// <summary>DotMod's player helicopter circles its last waypoint (AircraftOrbit.Tick: RotateAround at cruise speed)
    /// and never stops, so it could never dip (Raf, 28 Nov). A helicopter with a dipping sonar hovers there instead:
    /// speed 0, no orbit; while it hovers it dips (Dipping.IsHovering). A new waypoint moves it on as before.
    /// Single player too.</summary>
    [HarmonyPatch(typeof(AircraftOrbit), "Tick")]
    static class Patch_AircraftOrbit_Hover
    {
        static bool Prefix(DotModPlayerAircraft ___aircraft)
        {
            if (!Dipping.HasDippingSonar(___aircraft)) return true;
            try { Dipping.HoverTick(___aircraft); }
            catch (System.Exception ex) { Duel.Log("~Hover failed: " + ex.Message); ___aircraft.DemandSpeed = 0f; ___aircraft.CurrentSpeed = 0f; }
            return false;
        }
    }

    /// <summary>DotMod turns a player aircraft toward its next waypoint at 15 deg/s (AircraftTransit.Tick), so a
    /// helicopter flies wide arcs like a plane (Raf, 28 Nov). A helicopter turns another 45 deg/s here: 60 in all, a
    /// turn radius of about 80 yd at 160 kt instead of 340. Single player too.</summary>
    [HarmonyPatch(typeof(AircraftTransit), "Tick")]
    static class Patch_AircraftTransit_HeloTurn
    {
        const float ExtraTurn = 45f;

        static void Prefix(DotModPlayerAircraft ___aircraft)
        {
            DotModPlayerAircraft a = ___aircraft;
            if (a == null || a.databaseaircraftdata == null || a.databaseaircraftdata.aircraftType != "HELICOPTER") return;
            List<Vector3> wps = a.Waypoints;
            if (wps == null || wps.Count == 0) return;
            Vector3 to = wps[0] - a.transform.position;
            if (to.sqrMagnitude < 1e-6f) return;
            a.transform.rotation = Quaternion.RotateTowards(a.transform.rotation, Quaternion.LookRotation(to), ExtraTurn * Time.deltaTime);
        }
    }

    [HarmonyPatch(typeof(DotModPlayerAircraft), "Update")]
    static class Patch_PlayerAircraftUpdate_Cable
    {
        static void Postfix(DotModPlayerAircraft __instance) { try { Dipping.AfterAircraftUpdate(__instance); } catch (System.Exception) { } }
    }

    /// <summary>The ship's own sweep does not drop a contact the helicopter's dipping sonar holds.</summary>
    [HarmonyPatch(typeof(SensorManager), "CheckIfContactLost")]
    static class Patch_CheckIfContactLost_Dipping
    {
        static bool Prefix(int i) { return !Dipping.Holds(i); }
    }
}
