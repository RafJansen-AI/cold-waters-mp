// Cold Waters MP - a dipping sonar for the player's helicopter (docs/SURFACE.md). C# 5 / .NET 3.5.
// DotMod's player helicopter (DotModPlayerAircraft) has no sensors: it flies waypoints and drops torpedoes. The AI
// helicopter dips (Helicopter.CheckDippingSonar) with its type's sonar (SH-60B AQS-13F, Helix, Hormone, Lynx, Z-9 ...).
// Here a player helicopter that hovers (airborne, almost no speed) dips: after each of the game's own sonar sweeps
// (SensorManager.SonarCheck, every 8 s of game time) it runs the game's detection sums from its own position:
//   passive: target noise (GetVesselNoiseLevel) - (spreading + attenuation + sea ambient - passive sensitivity)
//   active:  sonar output - 2 x (spreading + attenuation) + target reflection x 0.8 - sea ambient + active sensitivity
// (a hovering helicopter adds no own-speed noise; no layer or convergence zone bonus yet). A detection is given to the
// player's contact picture with the game's ContactDetected and TMA update, and the ship's own sweep does not drop a
// contact the helicopter heard in the last 12 s. Active when the ship's active sonar is on; an active dip is announced to
// the other players. Applies in single player too. Untested (28 Nov).
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Dipping
    {
        public const byte G_DIP_PING = 25;
        const float HoverSpeed = 3f;     // DotModPlayerAircraft.CurrentSpeed below this: hovering (dipping)
        const float HoldSeconds = 12f;   // keep a helicopter contact this long without a new detection

        static readonly Dictionary<int, float> _heldUntil = new Dictionary<int, float>(); // enemy index -> time
        static readonly Dictionary<DotModPlayerAircraft, bool> _dipping = new Dictionary<DotModPlayerAircraft, bool>();

        public static bool Holds(int i)
        {
            float t;
            return _heldUntil.TryGetValue(i, out t) && Time.time < t;
        }

        public static void Reset() { _heldUntil.Clear(); _dipping.Clear(); }

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
                bool dip = a.IsAirborne && !a.IsDestroyed && a.CurrentSpeed < HoverSpeed && a.databaseaircraftdata.passiveSonarID >= 0;
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
            Vector3 hp = a.transform.position;
            float ambient = sm.currentOceanAmbientNoise;
            float threshold = sm.detectionThresholds.x;
            int found = 0;
            for (int i = 0; i < ev.Length; i++)
            {
                Vessel v = ev[i];
                if (v == null || v.isSinking || !v.gameObject.activeInHierarchy) continue;
                float m = Vector3.Distance(new Vector3(hp.x, 1000f, hp.z), v.transform.position) * GameDataManager.yardsScale * 0.9144f;
                if (m < 1f) m = 1f;
                float loss = sm.spreadingFactor * Mathf.Log10(m) + sm.attenuationFactor * m;
                float signal;
                if (active) signal = asd.sonarOutput - 2f * loss + v.databaseshipdata.activesonarreflection * 0.8f - ambient + asd.sonarActiveSensitivity;
                else
                {
                    float noise = (float)AccessTools.Method(typeof(SensorManager), "GetVesselNoiseLevel").Invoke(sm, new object[] { v });
                    signal = noise - (loss + ambient - ps.sonarPassiveSensitivity);
                }
                if (signal <= threshold) continue;
                found++;
                bool already = sm.detectedByPlayer != null && i < sm.detectedByPlayer.Length && sm.detectedByPlayer[i];
                _heldUntil[i] = Time.time + HoldSeconds;
                try
                {
                    if (!already) sm.ContactDetected(i, "SONAR");
                    AccessTools.Method(typeof(SensorManager), "CalculatePlayerTMA").Invoke(sm, new object[] { i, active });
                }
                catch (System.Exception ex) { Duel.Log("~Dipping sonar contact update failed: " + ex.Message); }
                if (!already) Duel.Log("~Dipping sonar (" + (active ? "active" : "passive") + ") detected vessel " + i + " (" + v.databaseshipdata.shipPrefabName + ") at " + (m / 914.4f).ToString("F1") + " kyd, signal " + signal.ToString("F1"));
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

    /// <summary>The ship's own sweep does not drop a contact the helicopter's dipping sonar holds.</summary>
    [HarmonyPatch(typeof(SensorManager), "CheckIfContactLost")]
    static class Patch_CheckIfContactLost_Dipping
    {
        static bool Prefix(int i) { return !Dipping.Holds(i); }
    }
}
