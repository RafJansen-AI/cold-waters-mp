// Cold Waters MP - step 3: 1v1 duel setup and opponent puppet. C# 5 / .NET 3.5.
// Model: each machine keeps its own sub as the normal player vessel. The opponent's sub is
// enemy 0 with its AI disabled, driven by network state ("puppet").
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Duel
    {
        // Game message sub-types
        const byte G_SUBCHOICE = 1, G_PLACEMENT = 2, G_STATE = 3, G_INMISSION = 4, G_STATIONS = 13;

        public const float UnitsPerNm = 2025.37f / 75.13f; // 1 unit = 75.13 yd

        public static Session Session;
        public static System.Action<string> Log = delegate { };
        public static float RangeNm = 6f;

        // Opponent info
        public static int OppClass = -1;
        public static string OppClassName = "";
        public static bool OppRunningSilent;
        public static bool OppActiveSonar;
        static float _pingTimer;
        static float _diagTimer;

        /// <summary>Every 10 s: what our sonar makes of the opponent (to chase one-sided detection).</summary>
        static void LogSonarDiagnostics()
        {
            try
            {
                SensorManager sm = UIFunctions.globaluifunctions.playerfunctions.sensormanager;
                int i = Puppet.vesselListIndex;
                Vessel own = GameDataManager.playervesselsonlevel[0];
                float rangeKyd = Vector3.Distance(own.transform.position, Puppet.transform.position) * GameDataManager.yardsScale / 1000f;
                Log("SONAR range " + rangeKyd.ToString("F1") + " kyd | we hear them " + sm.signalLastPassiveReading[i].ToString("F1")
                    + " (threshold " + sm.detectionThresholds.x.ToString("F1") + "), detected " + sm.detectedByPlayer[i]
                    + " | their silent " + OppRunningSilent + ", active " + OppActiveSonar + ", speed " + (Puppet.vesselmovement.shipSpeed.z * 10f).ToString("F0") + " kt"
                    + " | our active " + own.acoustics.usingActiveSonar
                    + " | ENV ambient " + sm.currentOceanAmbientNoise.ToString("F1") + ", layer " + sm.layerDepthInFeet.ToString("F0") + " ft str " + sm.layerStrength.ToString("F2")
                    + ", duct " + sm.surfaceDuctStrength.ToString("F2") + ", sea " + sm.seaState);
            }
            catch (System.Exception ex) { Log("SONAR diagnostics failed: " + ex.Message); }
        }
        public static float OppRpgSelfNoiseBonus;
        public static bool OppInMission;

        // Local state
        public static bool InDuel;          // our mission was loaded as a duel
        public static bool LocalInMission;
        public static Vessel Puppet;
        static bool _haveState, _havePlacement;
        static Vector3 _placeOwnPos, _placeOppPos;
        static float _placeOwnHeading, _placeOppHeading;
        static float _sendTimer;
        static bool _choiceMade;            // set once the player has accepted a sub on vessel select
        // Start gate: a mission load is held until the opponent's sub choice has arrived.
        static LevelLoadManager _heldLoad;
        static bool _releasingLoad;
        // Battlestations gate: both players leave the (paused) briefing together.
        static bool _ownAtStations, _oppAtStations, _releasingStations;
        static MissionManager _heldStations;
        public static int OppMissionId = -1;
        // We left while the opponent was still in that mission: don't start a new one until they leave it too.
        // (Being "in the mission" alone is not enough: they may already be in the NEW one.)
        static bool _oppInOldMission;
        public static bool WaitingForOpponentChoice { get { return _heldLoad != null; } }

        // Latest two opponent states, for extrapolation
        struct SubState { public float T; public Vector3 Pos; public Vector3 Euler; public float SpeedZ; public int Telegraph; public Vector2 Rudder; public Vector2 Ballast; public bool Cav; public bool Silent; public bool Submerged; public bool Active; }
        static SubState _s0, _s1;
        static float _s1LocalTime;
        static int _stateCount;

        public static string StatusLine
        {
            get
            {
                if (Session == null || Session.State != SessionState.Ready) return "";
                string opp = OppClass >= 0 ? OppClassName : "not chosen yet";
                string s = "Opponent sub: " + opp;
                if (_heldStations != null) s += " | WAITING for opponent to reach battlestations (F8: start anyway)";
                if (WaitingForOpponentChoice) s += OppClass < 0 ? " | WAITING for opponent's sub choice (F8: start anyway)" : " | WAITING for the host to start (F8: start anyway)";
 if (Result.Length > 0) s += " | " + Result;
                if (InDuel) s += " | duel " + (OppInMission ? "running" : "waiting for opponent to load") + (Puppet != null ? " | states " + _stateCount : "");
                return s;
            }
        }

        public static void Init(Session session, System.Action<string> log, float rangeNm)
        {
            Session = session; Log = log; RangeNm = rangeNm;
            session.GameMessage += OnGameMessage;
            session.ReadyChanged += delegate (bool ready)
            {
                if (ready)
                {
                    if (_choiceMade) SendSubChoice(); // re-send a real choice to a newly joined peer
                    EnvSync.HostNewSeed();
                    if (Session.IsHost && _heldLoad != null) Match.HostResend(); // the client needs our map to start
                }
                else
                {
                    OppClass = -1; OppClassName = ""; OppInMission = false; _oppInOldMission = false;
                    TimeSync.Reset();             // never stay paused or compressed for a peer that is gone
                    Match.OnHostLeftMission();    // client: the host's map is stale after a reconnect
                }
            };
        }

        static bool Active { get { return Session != null && Session.State == SessionState.Ready; } }

        // ---------- sub choice ----------

        /// <summary>Vessel-select Accept: the player has really chosen a sub.</summary>
        public static void OnSubAccepted()
        {
            _choiceMade = true;
            SendSubChoice();
        }

        public static void SendSubChoice()
        {
            if (!Active || !_choiceMade || UIFunctions.globaluifunctions == null) return;
            int cls = UIFunctions.globaluifunctions.playerfunctions.playerVesselClass;
            string name = ClassName(cls);
            Session.SendGame(G_SUBCHOICE, w =>
            {
                w.Write(cls); w.Write(name);
                w.Write(RPG_Mode.rpgModeEnabled ? RPG_Mode.SelfNoiseBonus : 0f);
                w.Write(UIFunctions.globaluifunctions.missionmanager.currentMissionID);
            }, true);
            Log("Sent sub choice: " + name);
        }

        static string ClassName(int cls)
        {
            try { return UIFunctions.globaluifunctions.database.databaseshipdata[cls].shipPrefabName; }
            catch (System.Exception) { return "class " + cls; }
        }

        /// <summary>Client: the host's map and conditions arrived; start a held load if the sub choice is in too.</summary>
        public static void OnHostMatchArrived()
        {
            if (_heldLoad != null && OppClass >= 0) ReleaseHeldLoad("host started");
        }

        // ---------- level load ----------

        /// <summary>LoadLevel prefix: turn the chosen single mission into a 1v1 duel.</summary>
        /// <returns>false to hold the load until the opponent's sub choice arrives.</returns>
        public static bool BeforeLoadLevel(LevelLoadManager llm)
        {
            if (LocalInMission)
            {
                // Safety net: we never saw the previous mission end. Treat it as ended now, but keep the
                // fresh sub choice and anything the opponent already sent for the new mission.
                // No "left the mission" message here: it would wipe the sub choice we just sent.
                Log("WARNING: previous duel was never closed; cleaning up before the new mission");
                EnvSync.HostNewSeed();
            }
            InDuel = false; Puppet = null; LocalInMission = false; _haveState = false; _stateCount = 0;
            _ownAtStations = false; _heldStations = null; // _oppAtStations may already be true: they can be quicker
            Weapons.Reset();
            if (!Active || !GameDataManager.missionMode) return true;
            if (!_choiceMade) OnSubAccepted(); // fallback: make sure the opponent learns our sub
            if (Session.IsHost && !_releasingLoad) Match.HostPrepare(llm.levelloaddata); // our map and conditions, for both of us (once)
            if (_oppInOldMission && !_releasingLoad)
            {
                _heldLoad = llm;
                Log("Waiting for the opponent to leave the previous mission (F8: start anyway)");
                return false;
            }
            int oppCls = OppClass;
            if (oppCls < 0 && !_releasingLoad)
            {
                _heldLoad = llm;
                Log("Waiting for the opponent to choose a sub; the mission starts when it arrives (F8: start anyway)");
                return false;
            }
            if (!Session.IsHost && !Match.HaveHostMatch && !_releasingLoad)
            {
                _heldLoad = llm;
                Log("Waiting for the host to start; you get the host's map and conditions (F8: start anyway with yours)");
                return false;
            }
            LevelLoadData d = llm.levelloaddata;
            if (!Session.IsHost)
            {
                if (Match.HaveHostMatch) Match.ClientApply(d);
                else Log("WARNING: no map from the host; using your own mission (map and weather may differ)");
            }
            if (oppCls < 0)
            {
                oppCls = UIFunctions.globaluifunctions.playerfunctions.playerVesselClass;
                Log("WARNING: opponent has not chosen a sub yet; using your own class for the puppet");
            }
            d.noOtherVessels = true;
            d.numberOfHelicopters = 0;
            d.numberOfAircraft = 0;
            d.enemyShipBehaviour = new string[] { "DEFENSIVE" };
            d.enemyUnitMissionCritical = new bool[1];
            d.kmShipClasses = new int[] { UIFunctions.globaluifunctions.database.databaseshipdata[oppCls].shipID };
            d.kmShipInstances = new int[1];
            d.kmSlots = new int[1];
            InDuel = true;
            Log("Duel: mission rewritten to 1 enemy (" + ClassName(oppCls) + "), no traffic or aircraft");
            return true;
        }

        /// <summary>Back at the main menu: a mission load still on hold must not start later by itself.</summary>
        public static void DropHeldLoad()
        {
            if (_heldLoad == null) return;
            _heldLoad = null;
            _choiceMade = false;
            // Tell the opponent: our sub choice (and, from the host, the map) no longer stand.
            if (Session != null) Session.SendGame(G_INMISSION, w => w.Write(false), true);
            Log("Held mission start cancelled (back at the main menu)");
        }

        /// <summary>Run a held mission load (opponent's choice arrived, or the player pressed F8).</summary>
        public static void ReleaseHeldLoad(string why)
        {
            LevelLoadManager llm = _heldLoad;
            if (llm == null) return;
            _heldLoad = null;
            Log("Starting the mission (" + why + ")");
            _releasingLoad = true;
            try { llm.LoadLevel(); }
            finally { _releasingLoad = false; }
        }

        /// <summary>LoadLevel postfix: set up the puppet and placement.</summary>
        public static void AfterLoadLevel()
        {
            if (!InDuel) return;
            if (GameDataManager.enemyvesselsonlevel == null || GameDataManager.enemyvesselsonlevel.Length == 0)
            {
                Log("Duel: no enemy vessel spawned, duel disabled"); InDuel = false; return;
            }
            Puppet = GameDataManager.enemyvesselsonlevel[0];
            MakePuppet(Puppet);
            LocalInMission = true;
            _ownSunk = false; _oppSunk = false; Result = ""; _startTime = Time.time;

            Vessel own = GameDataManager.playervesselsonlevel[0];
            if (Session.IsHost)
            {
                // Host stays where the mission put it; the client is placed RangeNm away.
                Vector3 hp = own.transform.position;
                float hh = own.transform.eulerAngles.y;
                // Try bearings ahead of the host first, then all round, for a spot in deep enough water.
                float bearing = 0f, bestDepth = -1f, bestBearing = hh;
                for (int i = 0; i < 16; i++)
                {
                    bearing = i < 8 ? hh + Random.Range(-60f, 60f) : Random.Range(0f, 360f);
                    Vector3 d0 = Quaternion.Euler(0f, bearing, 0f) * Vector3.forward;
                    float sd = SeabedFeet(hp + d0 * (RangeNm * UnitsPerNm));
                    if (sd > bestDepth) { bestDepth = sd; bestBearing = bearing; }
                    if (sd >= 600f) break;
                }
                bearing = bestBearing;
                if (bestDepth < 600f) Log("WARNING: no deep water found " + RangeNm + " nm from the host; best seabed " + bestDepth.ToString("0") + " ft");
                Vector3 dir = Quaternion.Euler(0f, bearing, 0f) * Vector3.forward;
                Vector3 cp = hp + dir * (RangeNm * UnitsPerNm);
                cp.y = hp.y;
                float ch = Mathf.Repeat(bearing + 180f + Random.Range(-45f, 45f), 360f);
                Session.SendGame(G_PLACEMENT, w =>
                {
                    WriteV3(w, hp); w.Write(hh); WriteV3(w, cp); w.Write(ch);
                }, true);
                _placeOwnPos = hp; _placeOwnHeading = hh; _placeOppPos = cp; _placeOppHeading = ch; _havePlacement = true;
                Log("Duel: placed opponent " + RangeNm + " nm away, bearing " + Mathf.Repeat(bearing, 360f).ToString("F0"));
            }
            ApplyPlacement();
            Session.SendGame(G_INMISSION, w => w.Write(true), true);
            // PoC: no automatic start hold (pausing via timeScale during the load coroutine is unsafe).
            // Until the opponent's first state arrives, the puppet simply waits at its placement.
        }

        static void MakePuppet(Vessel v)
        {
            if (v.vesselai != null) v.vesselai.enabled = false;
            foreach (MonoBehaviour mb in v.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string n = mb.GetType().Name;
                if (n.StartsWith("Enemy_") || n == "VesselAI" || n == "VesselAINav") mb.enabled = false;
            }
            Log("Duel: enemy 0 (" + v.databaseshipdata.shipPrefabName + ") is now the opponent puppet, AI off");
        }

        static void ApplyPlacement()
        {
            if (!_havePlacement || !LocalInMission) return;
            Vessel own = OwnVessel();
            if (own == null) { Log("Placement arrived before our mission was loaded; keeping it for later"); return; }
            if (!Session.IsHost) Teleport(own, _placeOwnPos, _placeOwnHeading);
            if (Puppet != null) Teleport(Puppet, _placeOppPos, _placeOppHeading);
            _havePlacement = false;
            Log("Duel: placement applied");
        }

        /// <summary>Seabed depth in feet at a point (the game's own terrain probe); very large if there is no terrain.</summary>
        static float SeabedFeet(Vector3 p)
        {
            try { return UIFunctions.globaluifunctions.levelloadmanager.GetTerrainHeightAtPosition(p.x, p.z) * GameDataManager.unitsToFeet; }
            catch (System.Exception) { return 100000f; }
        }

        static void Teleport(Vessel v, Vector3 pos, float heading)
        {
            if (v == null) return; // Unity-destroyed vessel from an earlier mission
            Vector3 p = pos; p.y = v.transform.position.y; // keep own spawn depth
            if (v.playercontrolled)
            {
                // Don't end up below the seabed of the host's map: stay at least 100 ft above it.
                float seabed = SeabedFeet(p), depth = (1000f - p.y) * GameDataManager.unitsToFeet;
                if (seabed < 50000f && depth > seabed - 100f)
                {
                    float newDepth = Mathf.Max(60f, seabed * 0.5f);
                    p.y = 1000f - newDepth / GameDataManager.unitsToFeet;
                    Log("Placement: seabed " + seabed.ToString("0") + " ft, moved our sub up to " + newDepth.ToString("0") + " ft");
                }
            }
            v.transform.position = p;
            Vector3 e = v.transform.eulerAngles; e.y = heading;
            v.transform.eulerAngles = e;
        }

        /// <summary>Testing: jump our own sub to <paramref name="nm"/> off the opponent's bow, pointing at them.
        /// Our next state update moves our sub on the opponent's screen too.</summary>
        public static void DebugTeleportNearOpponent(float nm)
        {
            if (!InDuel || Puppet == null) return;
            Vessel own = GameDataManager.playervesselsonlevel[0];
            if (own == null) return;
            Vector3 fwd = Puppet.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 pos = Puppet.transform.position + fwd * (nm * UnitsPerNm);
            Vector3 toOpp = Puppet.transform.position - pos;
            float heading = Mathf.Atan2(toOpp.x, toOpp.z) * Mathf.Rad2Deg;
            if (heading < 0f) heading += 360f;
            Teleport(own, pos, heading);
            Log("DEBUG: teleported to " + nm.ToString("F1") + " nm off the opponent's bow, heading " + heading.ToString("0"));
        }

        /// <summary>Battlestations prefix: tell the opponent; hold until they are there too.</summary>
        public static bool OnBattlestations(MissionManager mm)
        {
            if (!InDuel || !Active || _releasingStations) return true;
            if (!_ownAtStations)
            {
                _ownAtStations = true;
                Session.SendGame(G_STATIONS, w => { }, true);
            }
            if (_oppAtStations) return true;
            _heldStations = mm;
            Log("Waiting for the opponent to reach battlestations (F8: start anyway)");
            return false;
        }

        public static void ReleaseStations(string why)
        {
            MissionManager mm = _heldStations;
            if (mm == null) return;
            _heldStations = null;
            Log("Battlestations (" + why + ")");
            _releasingStations = true;
            try { mm.Battlestations(); }
            finally { _releasingStations = false; }
        }

        // ---------- match result ----------
        static bool _ownSunk, _oppSunk;
        static float _startTime;
        public static string Result = "";

        public static void OnOwnSinking() { if (_ownSunk || !InDuel) return; _ownSunk = true; AnnounceResult(); }
        public static void OnOppSinking() { if (_oppSunk || !InDuel) return; _oppSunk = true; AnnounceResult(); }

        static void AnnounceResult()
        {
            string what;
            if (_ownSunk && _oppSunk) what = "DRAW: both subs lost";
            else if (_oppSunk) what = "VICTORY: the opponent's " + OppClassName + " is sinking";
            else what = "DEFEAT: your sub is sinking";
            float mins = (Time.time - _startTime) / 60f;
            Result = what + " (" + mins.ToString("0.0") + " min; torpedoes fired " + Weapons.OwnFired + " vs " + Weapons.OppFired
                + ", hits " + Weapons.OwnHits + " vs " + Weapons.OppHits + ")";
            Log("RESULT " + Result);
            try
            {
                PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
                pf.PlayerMessage(what, pf.messageLogColors["Helm"], "CWMP");
            }
            catch (System.Exception) { }
        }

        /// <summary>Our own vessel, or null if none (or it was destroyed with the previous scene).</summary>
        static Vessel OwnVessel()
        {
            Vessel[] a = GameDataManager.playervesselsonlevel;
            if (a == null || a.Length == 0) return null;
            Vessel v = a[0];
            return v == null ? null : v; // Unity's == catches destroyed objects
        }

        /// <summary>We left the mission (action report, quit, back to main). Safe to call more than once.</summary>
        public static void OnLeftMission(string how)
        {
            if (!InDuel && !LocalInMission) return;
            Log("Left the mission (" + how + ")");
            if (Session != null) Session.SendGame(G_INMISSION, w => w.Write(false), true);
            EndDuel();
        }

        /// <summary>Called on mission exit (or session loss).</summary>
        public static void EndDuel()
        {
            InDuel = false; Puppet = null; LocalInMission = false;
            _choiceMade = false; _heldLoad = null; // a new mission needs a new choice
            _ownAtStations = false; _oppAtStations = false; _heldStations = null; OppMissionId = -1;
            TimeSync.Reset();
            Match.RestoreLocalDifficulty();
            _oppInOldMission = OppInMission;
            if (OppInMission)
            {
                // They are still in the old mission: their sub choice (and, for a client, the host's map) belong
                // to it. They send fresh ones when they choose again.
                OppClass = -1; OppClassName = "";
                if (!Session.IsHost) Match.OnHostLeftMission();
            }
            _ownSunk = false; _oppSunk = false; // keep Result on the overlay until the next mission
            EnvSync.HostNewSeed(); // host: new ocean for the next mission (the client keeps the latest seed it got)
            Weapons.Reset();
        }

        // ---------- per-frame ----------

        public static void Update()
        {
            if (!InDuel || !Active) return;
            if (Puppet == null || OwnVessel() == null) return;

            _diagTimer += Time.unscaledDeltaTime;
            if (_diagTimer >= 10f) { _diagTimer = 0f; LogSonarDiagnostics(); }

            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer >= 0.1f)
            {
                _sendTimer = 0f;
                SendOwnState(OwnVessel());
            }
            DrivePuppet();
            Weapons.Update();
        }

        static void SendOwnState(Vessel v)
        {
            VesselMovement m = v.vesselmovement;
            Vector3 pos = v.transform.position, eul = v.transform.eulerAngles;
            float t = Time.time;
            bool silent = PlayerFunctions.runningSilent;
            Session.SendGame(G_STATE, w =>
            {
                w.Write(t); WriteV3(w, pos); WriteV3(w, eul);
                w.Write(m.shipSpeed.z); w.Write(m.telegraphValue);
                w.Write(m.rudderAngle.x); w.Write(m.rudderAngle.y);
                w.Write(m.ballastAngle.x); w.Write(m.ballastAngle.y);
                w.Write(m.isCavitating); w.Write(silent); w.Write(v.submerged);
                w.Write(v.acoustics.usingActiveSonar);
            }, false);
        }

        static void DrivePuppet()
        {
            if (!_haveState) return;
            VesselMovement m = Puppet.vesselmovement;
            // Inputs, so the game's own movement and noise code see the right values
            m.telegraphValue = _s1.Telegraph;
            m.rudderAngle = _s1.Rudder;
            m.ballastAngle = _s1.Ballast;
            m.isCavitating = _s1.Cav;
            Puppet.acoustics.usingActiveSonar = _s1.Active;
            // Their pings, at the player's ping rate: ping sound from their sub and the ping line on our map.
            if (_s1.Active && !Puppet.isSinking)
            {
                PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
                _pingTimer += Time.deltaTime;
                if (_pingTimer > pf.activeSonarPingRate)
                {
                    _pingTimer -= pf.activeSonarPingRate;
                    UIFunctions.globaluifunctions.combatai.PlaySonarPing(Puppet);
                    pf.sensormanager.tacticalmap.DrawPingLine(Puppet.transform, pf.sensormanager.tacticalmap.pingLines[0]);
                }
            }
            else _pingTimer = UIFunctions.globaluifunctions.playerfunctions.activeSonarPingRate; // ping at once when they start
            Vector3 sp = m.shipSpeed; sp.z = _s1.SpeedZ; m.shipSpeed = sp;
            Puppet.submerged = _s1.Submerged;

            // Extrapolate the target pose from the last two states (unit-agnostic)
            Vector3 target = _s1.Pos;
            float dtStates = _s1.T - _s0.T;
            if (_stateCount > 1 && dtStates > 0.001f && dtStates < 2f)
            {
                Vector3 vel = (_s1.Pos - _s0.Pos) / dtStates;
                float ahead = Mathf.Min(Time.time - _s1LocalTime, 1f);
                target += vel * ahead;
            }
            Transform tr = Puppet.transform;
            Vector3 err = target - tr.position;
            if (err.magnitude > 1.0f) tr.position = target;                 // > ~75 yd: snap
            else tr.position += err * Mathf.Min(1f, Time.deltaTime * 4f);    // smooth
            Quaternion want = Quaternion.Euler(_s1.Euler);
            if (Quaternion.Angle(tr.rotation, want) > 20f) tr.rotation = want;
            else tr.rotation = Quaternion.Slerp(tr.rotation, want, Mathf.Min(1f, Time.deltaTime * 4f));
        }

        // ---------- messages ----------

        static void OnGameMessage(byte type, BinaryReader r)
        {
            if (Weapons.OnMessage(type, r)) return;
            if (EnvSync.OnMessage(type, r)) return;
            if (Match.OnMessage(type, r)) return;
            if (TimeSync.OnMessage(type, r)) return;
            switch (type)
            {
                case G_SUBCHOICE:
                    OppClass = r.ReadInt32(); OppClassName = r.ReadString(); OppRpgSelfNoiseBonus = r.ReadSingle();
                    OppMissionId = r.ReadInt32();
                    int ownMission = UIFunctions.globaluifunctions.missionmanager.currentMissionID;
                    if (OppMissionId != ownMission) Log("Note: opponent picked mission " + OppMissionId + ", you picked " + ownMission + "; the host's map and conditions are used.");
                    string mine = ClassName(OppClass);
                    if (mine != OppClassName) Log("WARNING: opponent's sub " + OppClassName + " is " + mine + " here (data mismatch?)");
                    Log("Opponent chose " + OppClassName);
                    if (_heldLoad != null && !_oppInOldMission && (Session.IsHost || Match.HaveHostMatch)) ReleaseHeldLoad("opponent chose " + OppClassName);
                    if (InDuel && Puppet != null && Puppet.databaseshipdata.shipPrefabName != OppClassName)
                        Log("WARNING: puppet is a " + Puppet.databaseshipdata.shipPrefabName + " but opponent chose " + OppClassName + ". Pick both subs before starting either mission.");
                    break;
                case G_PLACEMENT:
                {
                    Vector3 hp = ReadV3(r); float hh = r.ReadSingle(); Vector3 cp = ReadV3(r); float ch = r.ReadSingle();
                    // We are the client: our own sub goes to cp, the host's puppet to hp.
                    _placeOwnPos = cp; _placeOwnHeading = ch; _placeOppPos = hp; _placeOppHeading = hh;
                    _havePlacement = true;
                    ApplyPlacement();
                    break;
                }
                case G_STATIONS:
                    _oppAtStations = true;
                    Log("Opponent is at battlestations");
                    if (_heldStations != null) ReleaseStations("opponent ready");
                    break;
                case G_INMISSION:
                    OppInMission = r.ReadBoolean();
                    Log("Opponent " + (OppInMission ? "is in the mission" : "left the mission"));
                    if (!OppInMission) _oppInOldMission = false;
                    if (!OppInMission && !Session.IsHost) Match.OnHostLeftMission();
                    if (!OppInMission) TimeSync.Reset();
                    if (!OppInMission) { OppClass = -1; OppClassName = ""; _oppAtStations = false; OppMissionId = -1; } // they will choose again
                    break;
                case G_STATE:
                {
                    SubState s = new SubState();
                    s.T = r.ReadSingle(); s.Pos = ReadV3(r); s.Euler = ReadV3(r);
                    s.SpeedZ = r.ReadSingle(); s.Telegraph = r.ReadInt32();
                    s.Rudder = new Vector2(r.ReadSingle(), r.ReadSingle());
                    s.Ballast = new Vector2(r.ReadSingle(), r.ReadSingle());
                    s.Cav = r.ReadBoolean(); s.Silent = r.ReadBoolean(); s.Submerged = r.ReadBoolean();
                    s.Active = r.ReadBoolean();
                    if (_stateCount > 0 && s.T <= _s1.T) break; // stale/out of order
                    _s0 = _stateCount > 0 ? _s1 : s; _s1 = s; _s1LocalTime = Time.time;
                    OppRunningSilent = s.Silent;
                    if (s.Active != OppActiveSonar) Log("Opponent switched to " + (s.Active ? "ACTIVE" : "passive") + " sonar");
                    OppActiveSonar = s.Active;
                    _stateCount++; _haveState = true;
                    break;
                }
            }
        }

        static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static Vector3 ReadV3(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }

        public static bool IsPuppet(Vessel v) { return InDuel && v != null && v == Puppet; }
    }

    // ---------- Harmony patches ----------

    [HarmonyPatch(typeof(LevelLoadManager), "LoadLevel")]
    static class Patch_LoadLevel
    {
        static bool Prefix(LevelLoadManager __instance) { return Duel.BeforeLoadLevel(__instance); }
        static void Postfix() { Duel.AfterLoadLevel(); }
    }

    /// <summary>Sub chosen on the vessel-select screen (also reached via "random ship").</summary>
    [HarmonyPatch(typeof(MissionManager), "SelectShipAccept")]
    static class Patch_SelectShipAccept
    {
        static void Postfix() { Duel.OnSubAccepted(); }
    }

    // Leaving a mission: EndLevel runs for every mission end (sunk, quit, mission over) and brings up the
    // action report; UIFunctions.BackToMain is the menu button after it; MissionManager.BackToMain is the campaign exit.
    [HarmonyPatch(typeof(UIFunctions), "EndLevel")]
    static class Patch_EndLevel
    {
        static void Prefix() { Duel.OnLeftMission("mission ended"); }
    }

    [HarmonyPatch(typeof(UIFunctions), "CleanupLevel")]
    static class Patch_CleanupLevel
    {
        static void Prefix() { Duel.OnLeftMission("level cleanup"); }
    }

    [HarmonyPatch(typeof(UIFunctions), "BackToMain")]
    static class Patch_UIBackToMain
    {
        static void Prefix() { Duel.OnLeftMission("back to main menu"); Duel.DropHeldLoad(); }
    }

    [HarmonyPatch(typeof(MissionManager), "BackToMain")]
    static class Patch_BackToMain
    {
        static void Prefix() { Duel.OnLeftMission("campaign exit"); }
    }

    [HarmonyPatch(typeof(VesselAI), "FixedUpdate")]
    static class Patch_VesselAI_FixedUpdate
    {
        static bool Prefix(VesselAI __instance)
        {
            return !(Duel.InDuel && Duel.Puppet != null && Duel.Puppet.vesselai == __instance);
        }
    }

    /// <summary>Fairness: give the puppet the player-only quieting the real player gets.</summary>
    [HarmonyPatch(typeof(SensorManager), "GetVesselNoiseLevel")]
    static class Patch_NoiseLevel
    {
        static void Postfix(SensorManager __instance, Vessel activeVessel, ref float __result)
        {
            if (!Duel.IsPuppet(activeVessel)) return;
            if (Duel.OppRunningSilent)
            {
                float b = Random.Range(__instance.runningSilentBonus.x, __instance.runningSilentBonus.y);
                if (activeVessel.databaseshipdata.isDiesel) b *= Random.Range(1.2f, 1.8f);
                __result -= b;
            }
            // The player formula subtracts the RPG bonus before compression.
            if (__instance.passiveCompressionFactor != 0f) __result -= Duel.OppRpgSelfNoiseBonus / __instance.passiveCompressionFactor;
            // A pinging player is at least as loud as their sonar's output (player rule, not the AI's +output/4).
            int sid = activeVessel.databaseshipdata.activeSonarID;
            if (Duel.OppActiveSonar && sid >= 0)
            {
                float output = UIFunctions.globaluifunctions.database.databasesonardata[sid].sonarOutput;
                if (__result < output) __result = output;
            }
        }
    }
}

namespace ColdWatersMP
{
    /// <summary>No pausing in a duel (for now): the Esc/exit menu still opens, but the game keeps running.
    /// A paused game freezes its own sub for the opponent and can't be hit. Later: host setting (block or pause both).</summary>
    [HarmonyPatch(typeof(MissionManager), "BringInExitMenu")]
    static class Patch_BringInExitMenu
    {
        static void Postfix()
        {
            TimeSync.OnExitMenuOpened();
        }
    }

    /// <summary>The in-game help also pauses (and restores lastTimeScale on close): keep both at 1 in a duel.</summary>
    [HarmonyPatch(typeof(HelpManager), "OnEnable")]
    static class Patch_HelpOnEnable
    {
        static void Postfix(HelpManager __instance)
        {
            if (!Duel.InDuel) return;
            if (__instance.lastTimeScale == 0f) __instance.lastTimeScale = 1f;
            if (Time.timeScale == 0f) { Time.timeScale = 1f; Time.fixedDeltaTime = 0.02f; Duel.Log("Help opened: pause blocked in multiplayer"); }
        }
    }
}

namespace ColdWatersMP
{
    /// <summary>The briefing after loading keeps the game paused; start both games together.</summary>
    [HarmonyPatch(typeof(MissionManager), "Battlestations")]
    static class Patch_Battlestations
    {
        static bool Prefix(MissionManager __instance) { return Duel.OnBattlestations(__instance); }
    }
}
