// Cold Waters MP - duel setup and opponent puppets. C# 5 / .NET 3.5.
// Model: each machine keeps its own sub as the normal player vessel. Every other player's sub is an enemy-list
// vessel with its AI disabled, driven by network state ("puppet"). A 1v1 duel has one; a battle with more
// players has one per other player, teammates included (hardcore: nothing is shared, everyone is a contact).
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    /// <summary>Another player, as this game knows them: sub choice, mission flags, puppet and its latest states.</summary>
    public class Opponent
    {
        public int Id; public string Name = ""; public int Team;
        public int Class = -1; public string ClassName = ""; public float RpgSelfNoiseBonus; public int MissionId = -1;
        public bool InMission, InOldMission, AtStations;
        public bool RunningSilent, ActiveSonar;
        public Vessel Puppet;            // null when they are not in our current mission
        public int Base = -1;            // enemy-list index of their puppet; their ghost slots follow it
        public float LastNoise = 100f, LastSpeedNoise; // their hull noise at our last sonar check (decoys copy it)
        public bool Sunk;
        internal Duel.SubState S0, S1; internal float S1Local; internal int StateCount; internal bool HaveState;
        internal float PingTimer;
        internal bool HavePlace; internal Vector3 PlacePos; internal float PlaceHeading;
        public string Label { get { return Name.Length > 0 ? Name : "player " + Id; } }
        public byte Towed { get { return HaveState ? S1.Towed : (byte)0; } }
    }

    public static class Duel
    {
        // Game message sub-types
        const byte G_SUBCHOICE = 1, G_PLACEMENT = 2, G_STATE = 3, G_INMISSION = 4, G_STATIONS = 13;

        public const float UnitsPerNm = 2025.37f / 75.13f; // 1 unit = 75.13 yd

        public static Session Session;
        public static System.Action<string> Log = delegate { };
        public static float RangeNm = 6f;          // legacy single value (config); the band below is used
        public static float RangeMinNm = 4f, RangeMaxNm = 9f; // host: start range is random in this band
        static readonly System.Random _placeRng = new System.Random();
        static float Rnd(float a, float b) { return a + (float)_placeRng.NextDouble() * (b - a); }

        // ---------- the other players ----------
        static readonly SortedDictionary<int, Opponent> _opps = new SortedDictionary<int, Opponent>();
        /// <summary>Every other player in the session, by player number.</summary>
        public static readonly List<Opponent> Opps = new List<Opponent>();
        /// <summary>The other players in our current mission (they have a puppet here), by player number.</summary>
        public static readonly List<Opponent> InGame = new List<Opponent>();
        /// <summary>Enemy-list index where neutral traffic starts (after every opponent's puppet and ghost slots).</summary>
        public static int NeutralStart = 1 + Ghosts.Slots;
        /// <summary>Decoy ghost slots per opponent in this mission.</summary>
        public static int SlotsPer = Ghosts.Slots;

        static Opponent Opp(int id)
        {
            Opponent o;
            if (_opps.TryGetValue(id, out o)) return o;
            o = new Opponent { Id = id };
            _opps[id] = o;
            RebuildOpps();
            return o;
        }

        static void RebuildOpps() { Opps.Clear(); Opps.AddRange(_opps.Values); }

        /// <summary>The opponent whose puppet this is, or null.</summary>
        public static Opponent ByPuppet(Vessel v)
        {
            if (!InDuel || v == null) return null;
            foreach (Opponent o in InGame) if (o.Puppet == v) return o;
            return null;
        }

        /// <summary>The sender of the game message being handled.</summary>
        static Opponent Sender { get { return Opp(Session.LastSender); } }

        /// <summary>The opponent with this player number in our current mission, or null.</summary>
        public static Opponent InGameById(int id)
        {
            foreach (Opponent o in InGame) if (o.Id == id) return o;
            return null;
        }

        /// <summary>The session's roster changed: add newcomers, drop who left, take over names and teams.</summary>
        static void SyncRoster()
        {
            HashSet<int> ids = new HashSet<int>();
            foreach (PlayerInfo p in Session.Players.ToArray())
            {
                if (p.Id == Session.MyId) { MyTeam = p.Team; continue; }
                ids.Add(p.Id);
                Opponent o = Opp(p.Id);
                o.Name = p.Name ?? ""; o.Team = p.Team;
            }
            List<int> gone = new List<int>();
            foreach (int id in _opps.Keys) if (!ids.Contains(id)) gone.Add(id);
            foreach (int id in gone)
            {
                Opponent o = _opps[id];
                _opps.Remove(id);
                if (o.Puppet != null && InDuel)
                {
                    Log(o.Label + " left the session during the mission");
                    o.InMission = false;
                    CheckEnd();
                }
            }
            if (gone.Count > 0) RebuildOpps();
        }

        public static int MyTeam;

        /// <summary>One opponent's name for messages: "the opponent" in a 1v1, else the player's name.</summary>
        static string Who(Opponent o) { return Opps.Count <= 1 ? "the opponent" : o.Label; }

        static float _diagTimer;

        /// <summary>Every 10 s: what our sonar makes of each opponent (to chase one-sided detection).</summary>
        static void LogSonarDiagnostics()
        {
            try
            {
                SensorManager sm = UIFunctions.globaluifunctions.playerfunctions.sensormanager;
                Vessel own = GameDataManager.playervesselsonlevel[0];
                foreach (Opponent o in InGame)
                {
                    Vessel p = o.Puppet;
                    if (p == null) continue;
                    int i = p.vesselListIndex;
                    float rangeKyd = Vector3.Distance(own.transform.position, p.transform.position) * GameDataManager.yardsScale / 1000f;
                    Log("~SONAR " + (InGame.Count > 1 ? o.Label + " " : "") + "range " + rangeKyd.ToString("F1") + " kyd | we hear them " + sm.signalLastPassiveReading[i].ToString("F1")
                        + " (threshold " + sm.detectionThresholds.x.ToString("F1") + "), detected " + sm.detectedByPlayer[i]
                        + " | their silent " + o.RunningSilent + ", active " + o.ActiveSonar + ", speed " + (p.vesselmovement.shipSpeed.z * 10f).ToString("F0") + " kt"
                        + " | our active " + own.acoustics.usingActiveSonar
                        + " | ENV ambient " + sm.currentOceanAmbientNoise.ToString("F1") + ", layer " + sm.layerDepthInFeet.ToString("F0") + " ft str " + sm.layerStrength.ToString("F2")
                        + ", duct " + sm.surfaceDuctStrength.ToString("F2") + ", sea " + sm.seaState);
                }
                Log("~TRACKS " + Tracks.Describe());
            }
            catch (System.Exception ex) { Log("SONAR diagnostics failed: " + ex.Message); }
        }

        // Local state
        public static bool InDuel;          // our mission was loaded as a duel
        public static bool LocalInMission;
        public static bool PlacementApplied; // our duel placement was applied in this mission
        /// <summary>The first opponent's puppet (a 1v1 has only one).</summary>
        public static Vessel Puppet { get { return InGame.Count > 0 ? InGame[0].Puppet : null; } }
        static bool _havePlacement;
        static int _placeSeq = -1; // client: the host match this placement belongs to
        static Vector3 _placeOwnPos;
        static float _placeOwnHeading;
        static float _sendTimer;
        static bool _choiceMade;            // set once the player has accepted a sub on vessel select
        // Start gate: a mission load is held until every opponent's sub choice has arrived.
        static LevelLoadManager _heldLoad;
        static bool _releasingLoad;
        // Battlestations gate: all players leave the (paused) briefing together.
        static bool _ownAtStations, _releasingStations;
        static MissionManager _heldStations;
        public static bool WaitingForOpponentChoice { get { return _heldLoad != null; } }

        static bool AllChosen { get { foreach (Opponent o in Opps) if (o.Class < 0) return false; return Opps.Count > 0; } }
        static bool AnyInOldMission { get { foreach (Opponent o in Opps) if (o.InOldMission) return true; return false; } }
        /// <summary>Every opponent of this mission who is still in the session is at battlestations.</summary>
        static bool AllAtStations { get { foreach (Opponent o in InGame) if (!o.AtStations && _opps.ContainsKey(o.Id)) return false; return true; } }

        // Latest two states of a puppet, for extrapolation
        internal struct SubState { public float T; public Vector3 Pos; public Vector3 Euler; public float SpeedZ; public int Telegraph; public Vector2 Rudder; public Vector2 Ballast; public bool Cav; public bool Silent; public bool Submerged; public bool Active; public byte Towed; }

        public static string StatusLine
        {
            get
            {
                if (Session == null || Session.State != SessionState.Ready || Opps.Count == 0) return "";
                string s;
                if (Opps.Count == 1) s = "Opponent sub: " + (Opps[0].Class >= 0 ? Opps[0].ClassName : "not chosen yet");
                else
                {
                    int chosen = 0; foreach (Opponent o in Opps) if (o.Class >= 0) chosen++;
                    s = chosen + " of " + Opps.Count + " other players chose a sub";
                }
                string them = Opps.Count == 1 ? "opponent" : "other players";
                if (_heldStations != null) s += " | WAITING for the " + them + " to reach battlestations (Ctrl+F8: start anyway)";
                if (WaitingForOpponentChoice) s += !AllChosen ? " | WAITING for the " + (Opps.Count == 1 ? "opponent's" : "other players'") + " sub choice (Ctrl+F8: start anyway)" : " | WAITING for the host to start (Ctrl+F8: start anyway)";
                if (Result.Length > 0) s += " | " + Result;
                if (InDuel)
                {
                    int loaded = 0, states = 0; foreach (Opponent o in InGame) { if (o.InMission) loaded++; states += o.StateCount; }
                    s += " | " + (Opps.Count == 1 ? "duel " : "battle ") + (loaded == InGame.Count ? "running" : "waiting for " + them + " to load") + (InGame.Count > 0 ? " | states " + states : "");
                }
                return s;
            }
        }

        public static void Init(Session session, System.Action<string> log, float rangeNm)
        {
            Session = session; Log = log; RangeNm = rangeNm;
            session.GameMessage += OnGameMessage;
            session.RosterChanged += delegate
            {
                int before = _opps.Count;
                SyncRoster();
                // Someone new: they need our sub choice, a fresh ocean, and (held host load) our map.
                if (Session.State == SessionState.Ready && _opps.Count != before)
                {
                    if (_choiceMade) SendSubChoice();
                    EnvSync.HostNewSeed();
                    if (Session.IsHost && _heldLoad != null) Match.HostResend();
                }
            };
            session.ReadyChanged += delegate (bool ready)
            {
                if (ready)
                {
                    SyncRoster();
                    if (_choiceMade) SendSubChoice(); // re-send a real choice to a newly joined peer
                    EnvSync.HostNewSeed();
                    if (Session.IsHost && _heldLoad != null) Match.HostResend(); // the client needs our map to start
                }
                else
                {
                    _opps.Clear(); RebuildOpps();
                    TimeSync.Reset();             // never stay paused or compressed for a peer that is gone
                    Match.OnHostLeftMission();    // client: the host's map is stale after a reconnect
                    if (!InDuel) Match.RestoreLocalDifficulty(); // nothing was played: give back our own settings
                    Match.AppliedSeq = -1; _havePlacement = false; // a restarted host counts its matches from 1 again
                }
            };
        }

        /// <summary>Multiplayer missions need a ready session with at least one other player.</summary>
        static bool Active { get { return Session != null && Session.State == SessionState.Ready && Session.PlayerCount >= 2; } }

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
            if (_heldLoad != null && AllChosen && !AnyInOldMission) ReleaseHeldLoad("host started");
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
            InDuel = false; LocalInMission = false; PlacementApplied = false;
            foreach (Opponent o in InGame) o.Puppet = null;
            InGame.Clear();
            _ownAtStations = false; _heldStations = null; // an opponent's AtStations may already be true: they can be quicker
            Weapons.Reset();
            if (!Active || !GameDataManager.missionMode) return true;
            SyncRoster();
            if (!Session.IsHost && !_releasingLoad) Match.AppliedSeq = -1; // set again by ClientApply below
            if (!_choiceMade) OnSubAccepted(); // fallback: make sure the others learn our sub
            if (Session.IsHost && !_releasingLoad) Match.HostPrepare(llm.levelloaddata); // our map and conditions, for all of us (once)
            string them = Opps.Count == 1 ? "the opponent" : "the other players";
            if (AnyInOldMission && !_releasingLoad)
            {
                _heldLoad = llm;
                Log("Waiting for " + them + " to leave the previous mission (Ctrl+F8: start anyway)");
                return false;
            }
            if (!AllChosen && !_releasingLoad)
            {
                _heldLoad = llm;
                Log("Waiting for " + them + " to choose a sub; the mission starts when " + (Opps.Count == 1 ? "it arrives" : "all are in") + " (Ctrl+F8: start anyway)");
                return false;
            }
            if (!Session.IsHost && !Match.HaveHostMatch && !_releasingLoad)
            {
                _heldLoad = llm;
                Log("Waiting for the host to start; you get the host's map and conditions (Ctrl+F8: start anyway with yours)");
                return false;
            }
            LevelLoadData d = llm.levelloaddata;
            if (!Session.IsHost)
            {
                if (Match.HaveHostMatch) Match.ClientApply(d);
                else Log("WARNING: no map from the host; using your own mission (map and weather may differ)");
            }
            int ownCls = UIFunctions.globaluifunctions.playerfunctions.playerVesselClass;
            DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
            d.noOtherVessels = true;
            d.numberOfHelicopters = 0;
            d.numberOfAircraft = 0;
            // Per opponent (by player number): their puppet, then ghost subs for their decoys (same class, parked
            // until used). More opponents get fewer ghost slots each, so a big battle stays a sane vessel count.
            InGame.Clear(); InGame.AddRange(Opps);
            int r = InGame.Count;
            SlotsPer = r <= 1 ? Ghosts.Slots : System.Math.Max(2, Ghosts.Slots - (r - 1));
            int n = r * (1 + SlotsPer);
            NeutralStart = n;
            // The game caps the enemy list at GameDataManager.maxShips (15). Battles need more: raise it when
            // the mission plus room for neutral traffic would not fit. Its sonar and map arrays follow the
            // actual list length. A 1v1 duel (6 + 9) stays at 15.
            int need = n + Ambient.MaxNeutrals;
            if (need > GameDataManager.maxShips) { Log("~Ship cap raised from " + GameDataManager.maxShips + " to " + need); GameDataManager.maxShips = need; }
            d.enemyShipBehaviour = new string[n];
            d.kmShipClasses = new int[n];
            string names = "";
            List<int> classes = new List<int>();
            for (int k = 0; k < r; k++)
            {
                Opponent o = InGame[k];
                o.Base = k * (1 + SlotsPer);
                o.Sunk = false; o.HaveState = false; o.StateCount = 0; o.HavePlace = false;
                int cls = o.Class;
                if (cls < 0 || cls >= db.Length)
                {
                    cls = ownCls;
                    Log("WARNING: " + Who(o) + " has not chosen a sub yet; using your own class for their puppet");
                }
                classes.Add(cls);
                int sid = db[cls].shipID;
                for (int i = o.Base; i < o.Base + 1 + SlotsPer; i++) { d.enemyShipBehaviour[i] = "DEFENSIVE"; d.kmShipClasses[i] = sid; }
                names += (names.Length > 0 ? ", " : "") + ClassName(cls) + (r > 1 ? " (" + o.Label + ")" : "");
            }
            d.enemyUnitMissionCritical = new bool[n];
            d.kmShipInstances = new int[n];
            d.kmSlots = new int[n];
            InDuel = true;
            if (r == 1) Log("Duel: mission rewritten to 1 enemy (" + names + ") + " + SlotsPer + " decoy ghosts, no aircraft; neutral traffic: " + Ambient.Count);
            else Log("Battle: mission rewritten to " + r + " other players (" + names + "), " + SlotsPer + " decoy ghosts each, no aircraft; neutral traffic: " + Ambient.Count);
            SetIdBook(classes);
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
            Match.RestoreLocalDifficulty(); // the host already set the match visibility for the held load
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
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            foreach (Opponent o in InGame)
            {
                o.Puppet = o.Base >= 0 && o.Base < ev.Length ? ev[o.Base] : null;
                if (o.Puppet == null) { Log("WARNING: no puppet vessel for " + Who(o)); continue; }
                MakePuppet(o);
            }
            try { Ghosts.Setup(); } catch (System.Exception ex) { Log("Ghost setup failed: " + ex.Message); }
            try { Ambient.Setup(); } catch (System.Exception ex) { Log("Neutral traffic setup failed: " + ex.Message); }
            LocalInMission = true; PlacementApplied = false;
            { HashSet<int> teams = new HashSet<int>(); teams.Add(MyTeam); foreach (Opponent o in InGame) teams.Add(o.Team); _teamsPlay = teams.Count >= 2; }
            if (InGame.Count > 1) Log("Battle: " + (_teamsPlay ? "two teams" : "every player for themselves") + ", " + (InGame.Count + 1) + " subs");
            _ownSunk = false; _resultFinal = false; Result = ""; _startTime = Time.time;

            Vessel own = GameDataManager.playervesselsonlevel[0];
            if (Session.IsHost)
            {
                int seq = Match.SentSeq;
                List<int> ids; List<Vector3> pos; List<float> hdg;
                HostPlace(own.transform.position, out ids, out pos, out hdg);
                Session.SendGame(G_PLACEMENT, w =>
                {
                    w.Write(seq); w.Write(ids.Count);
                    for (int k = 0; k < ids.Count; k++) { w.Write(ids[k]); WriteV3(w, pos[k]); w.Write(hdg[k]); }
                }, true);
                TakePlacement(ids, pos, hdg, seq);
                Log((InGame.Count == 1 ? "Duel" : "Battle") + ": start positions set (random range in the host's band)");
            }
            ApplyPlacement();
            Session.SendGame(G_INMISSION, w => w.Write(true), true);
            // PoC: no automatic start hold (pausing via timeScale during the load coroutine is unsafe).
            // Until an opponent's first state arrives, their puppet simply waits at its placement.
        }

        /// <summary>Who starts together: the lobby teams when there are two, else everyone on their own (free for all).</summary>
        static int GroupOf(int id, int team) { return _teamsPlay ? team : id; }
        static bool _teamsPlay;

        /// <summary>Host: start positions for every player in the mission. The host's group stays where the mission put the
        /// host (new random heading); every other group goes somewhere in the range band off the host's bow. Geometry
        /// (1v1): each sub sees the other within 60 deg of its bow (never in the baffles), so the aspect varies from
        /// head-on to about 120 deg between the two lines of sight and courses. More groups share the bow sector, spread
        /// out evenly (three in a free for all make a rough triangle). Teammates start 1 nm apart, side by side.</summary>
        static void HostPlace(Vector3 hp, out List<int> ids, out List<Vector3> pos, out List<float> hdg)
        {
            ids = new List<int>(); pos = new List<Vector3>(); hdg = new List<float>();
            // the players and their groups, host first
            List<int> pid = new List<int>(); List<int> grp = new List<int>();
            pid.Add(Session.MyId); grp.Add(-1);
            grp[0] = GroupOf(Session.MyId, MyTeam);
            foreach (Opponent o in InGame) { pid.Add(o.Id); grp.Add(GroupOf(o.Id, o.Team)); }
            List<int> order = new List<int>(); order.Add(grp[0]);
            foreach (int g in grp) if (!order.Contains(g)) order.Add(g);
            int others = order.Count - 1;

            float hh = Rnd(0f, 360f);
            float range = Rnd(Mathf.Min(RangeMinNm, RangeMaxNm), Mathf.Max(RangeMinNm, RangeMaxNm));
            Vector3[] centre = new Vector3[order.Count];
            float[] bearingOf = new float[order.Count];
            centre[0] = hp;
            for (int gi = 1; gi < order.Count; gi++)
            {
                // this group's share of the bow sector (-60..60): the middle of its slice, jittered inside it
                float lo = -60f + 120f * (gi - 1) / others, hi = -60f + 120f * gi / others;
                float bearing = 0f, bestDepth = -1f, bestBearing = hh + (lo + hi) / 2f;
                for (int i = 0; i < 24; i++)
                {
                    bearing = i < 16 ? hh + Rnd(lo, hi) : Rnd(0f, 360f);
                    Vector3 d0 = Quaternion.Euler(0f, bearing, 0f) * Vector3.forward;
                    float sd = SeabedFeet(hp + d0 * (range * UnitsPerNm));
                    if (sd > bestDepth) { bestDepth = sd; bestBearing = bearing; }
                    if (sd >= 600f) break;
                }
                bearing = bestBearing;
                if (others == 1)
                {
                    float off = Mathf.Repeat(bearing - hh + 180f, 360f) - 180f;
                    if (off > 60f || off < -60f) hh = bearing + Rnd(-60f, 60f); // fallback bearing: turn the host so it still faces the client
                }
                if (bestDepth < 600f) Log("~WARNING: no deep water found at the chosen range; best seabed " + bestDepth.ToString("0") + " ft");
                Vector3 dir = Quaternion.Euler(0f, bearing, 0f) * Vector3.forward;
                centre[gi] = hp + dir * (range * UnitsPerNm); centre[gi].y = hp.y;
                bearingOf[gi] = bearing;
                Log("~Placement: group " + gi + " " + range.ToString("F1") + " nm from the host, bearing " + Mathf.Repeat(bearing, 360f).ToString("F0"));
            }
            hh = Mathf.Repeat(hh, 360f);
            // members: side by side, 1 nm apart, across the line to the other side
            for (int gi = 0; gi < order.Count; gi++)
            {
                List<int> members = new List<int>();
                for (int k = 0; k < pid.Count; k++) if (grp[k] == order[gi]) members.Add(k);
                Vector3 toEnemy = gi == 0 ? (others > 0 ? centre[1] - centre[0] : Vector3.forward) : centre[0] - centre[gi];
                toEnemy.y = 0f; if (toEnemy.sqrMagnitude < 0.01f) toEnemy = Vector3.forward;
                Vector3 side = Vector3.Cross(Vector3.up, toEnemy.normalized);
                for (int m = 0; m < members.Count; m++)
                {
                    int k = members[m];
                    // host (first member of group 0) stays put, its teammates go 1, -1, 2 ... nm sideways;
                    // other groups are centred on their spot
                    float offNm = gi == 0 ? (m == 0 ? 0f : ((m % 2 == 1) ? 1f : -1f) * ((m + 1) / 2)) : m - (members.Count - 1) / 2f;
                    Vector3 p = centre[gi] + side * (offNm * UnitsPerNm); p.y = hp.y;
                    float h;
                    if (gi == 0) h = m == 0 ? hh : Mathf.Repeat(hh + Rnd(-20f, 20f), 360f);
                    else h = Mathf.Repeat(bearingOf[gi] + 180f + Rnd(-60f, 60f), 360f);
                    ids.Add(pid[k]); pos.Add(p); hdg.Add(h);
                }
            }
        }

        /// <summary>Our own sub's and every puppet's start position, from the host's placement list.</summary>
        static void TakePlacement(List<int> ids, List<Vector3> pos, List<float> hdg, int seq)
        {
            for (int k = 0; k < ids.Count; k++)
            {
                if (ids[k] == Session.MyId) { _placeOwnPos = pos[k]; _placeOwnHeading = hdg[k]; continue; }
                Opponent o = Opp(ids[k]);
                o.PlacePos = pos[k]; o.PlaceHeading = hdg[k]; o.HavePlace = true;
            }
            _havePlacement = true; _placeSeq = seq;
        }

        /// <summary>The ID book (signatures, recognition manual, classification) comes from the mission's OtherVessels
        /// list, so a Chinese map gives a Chinese book and a NATO duel may have no NATO subs in it. For a duel, use the
        /// opponent's navy instead: every selectable submarine of the same side (Soviet/Chinese, or NATO
        /// and allies), all periods, always including their real class. The next mission load fills it again, so
        /// single player keeps its own books.</summary>
        /// <summary>Book group: Soviet and Chinese (wp, plan; the game's South China Sea book mixes them too), or NATO and
        /// allies (usn, uk, knm, rnn, aus ...), so a small navy's book doesn't give its one class away.</summary>
        static string Side(string prefab)
        {
            string n = prefab.Split('_')[0];
            return (n == "wp" || n == "plan") ? "Soviet/Chinese" : "NATO";
        }

        /// <summary>DotMod has era versions of a class (usn_68_ss_barbel, usn_84_ss_barbel). Both in the book gave doubled
        /// pages (Skipjack's two share one signature image), and the game's class check (CompareVesselPrefabNames: name
        /// without the navy prefix) treats them as different classes, so picking the other era's identical page was a
        /// wrong classification (Raf, 27 Nov). Keep one page per class: the opponent's era where the class has it,
        /// else the nearest era, so the opponent's own page doesn't stand out by era.</summary>
        static List<int> OnePerClass(List<int> book, DatabaseShipData[] db, List<int> keep)
        {
            int oppEra = Era(db[keep[0]].shipPrefabName);
            Dictionary<string, int> pick = new Dictionary<string, int>();
            List<string> order = new List<string>();
            List<int> extra = new List<int>(); // a second opponent class with the same key (two eras in one battle): keep both
            foreach (int id in book)
            {
                string key = ClassKey(db[id].shipPrefabName);
                int cur;
                if (!pick.TryGetValue(key, out cur)) { pick[key] = id; order.Add(key); continue; }
                if (keep.Contains(cur)) { if (keep.Contains(id) && !extra.Contains(id)) extra.Add(id); continue; }
                if (keep.Contains(id) || EraDistance(Era(db[id].shipPrefabName), oppEra) < EraDistance(Era(db[cur].shipPrefabName), oppEra)) pick[key] = id;
            }
            List<int> result = new List<int>();
            foreach (string k in order) result.Add(pick[k]);
            foreach (int id in extra) if (!result.Contains(id)) result.Add(id);
            if (result.Count < book.Count) Log("~ID book: " + (book.Count - result.Count) + " era duplicate(s) left out (one page per class, era " + oppEra + " first)");
            return result;
        }

        /// <summary>The prefab name without its era number: usn_84_ss_barbel -> usn_ss_barbel.</summary>
        static string ClassKey(string prefab)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (string p in prefab.Split('_'))
            {
                int n;
                if (int.TryParse(p, out n)) continue;
                if (b.Length > 0) b.Append('_');
                b.Append(p);
            }
            return b.ToString();
        }

        /// <summary>The era number in a prefab name (84 in usn_84_ss_barbel), or -1.</summary>
        static int Era(string prefab)
        {
            foreach (string p in prefab.Split('_')) { int n; if (int.TryParse(p, out n)) return n; }
            return -1;
        }

        static int EraDistance(int a, int b) { return a < 0 || b < 0 ? 1000 : System.Math.Abs(a - b); }

        /// <summary>Side colours in a duel (Raf, 27 Nov): NATO in DotMod's HUD blue, WP/PLAN in the vanilla HUD red.</summary>
        public static readonly Color NatoBlue = new Color(74f / 255f, 163f / 255f, 201f / 255f, 1f);
        public static readonly Color WpRed = new Color(1f, 136f / 255f, 154f / 255f, 1f);
        public static Color SideColor(string prefab)
        {
            string navy = prefab != null ? prefab.Split('_')[0] : "";
            return (navy == "wp" || navy == "plan") ? WpRed : NatoBlue;
        }

        static bool _oppColorLogged;

        /// <summary>The HUD's EnemyColor (navyColors[1]) is what the game draws the opponent's torpedoes, noisemakers,
        /// knuckles, wake bubbles and towed decoys in (DotMod: blue for everyone). In a duel it is the opponent's side
        /// colour, so they match the opponent's contact. BuildHUD refills the table on every HUD build, so check each frame.</summary>
        static void ApplyOpponentColor()
        {
            if (Puppet == null || Puppet.databaseshipdata == null) return;
            TacticalMap tm = UIFunctions.globaluifunctions.playerfunctions.sensormanager.tacticalmap;
            if (tm == null || tm.navyColors == null || tm.navyColors.Length < 2) return;
            Color c = SideColor(Puppet.databaseshipdata.shipPrefabName);
            if (tm.navyColors[1] == c) return;
            tm.navyColors[1] = c;
            if (!_oppColorLogged) { _oppColorLogged = true; Log("~Opponent colour on the map: " + (c == WpRed ? "WP red" : "NATO blue")); }
        }

        /// <summary>The ID book for the opponents' classes: every sub of their side(s), one page per class.</summary>
        static void SetIdBook(List<int> classes)
        {
            try
            {
                PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
                DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
                List<string> sides = new List<string>();
                foreach (int c in classes) { string sd = Side(db[c].shipPrefabName); if (!sides.Contains(sd)) sides.Add(sd); }
                string side = string.Join(" + ", sides.ToArray());
                List<int> book = new List<int>();
                int[] all = Patch_PopulateLevelLoadData_AllSubs.AllSubs();
                if (all != null)
                    foreach (int id in all)
                        if (sides.Contains(Side(db[id].shipPrefabName)) && !book.Contains(id)) book.Add(id);
                if (book.Count < 3)
                {
                    // The sub list didn't load: fall back to every real sub of that side in the database, so the book
                    // never holds just the opponent's class.
                    Log("~WARNING: ID book sub list has only " + book.Count + " entries; using the database instead");
                    for (int id = 0; id < db.Length; id++)
                        if (db[id] != null && db[id].shipType == "SUBMARINE" && Patch_PopulateLevelLoadData_AllSubs.IsRealSub(db[id].shipPrefabName)
                            && sides.Contains(Side(db[id].shipPrefabName)) && !book.Contains(id)) book.Add(id);
                }
                foreach (int c in classes) if (!book.Contains(c)) book.Add(c);
                book = OnePerClass(book, db, classes);
                int subs = book.Count;
                foreach (int c in Ambient.Classes()) if (!book.Contains(c)) book.Add(c); // neutral traffic, so it can be classified too
                pf.otherVesselList = book.ToArray();
                pf.currentSignatureIndex = Random.Range(0, subs); // a random sub page, so the first page says nothing; no bounds check in the game
                string pages = "";
                foreach (int c in classes) pages += (pages.Length > 0 ? ", " : "") + (book.IndexOf(c) + 1);
                Log("ID book: " + subs + " " + side + " submarines" + (book.Count > subs ? " + " + (book.Count - subs) + " neutral classes" : "")
                    + "; " + (classes.Count == 1 ? "opponent's class on page " : "opponents' classes on pages ") + pages);
            }
            catch (System.Exception ex) { Log("ID book not changed: " + ex.Message); }
        }

        static void MakePuppet(Opponent o)
        {
            AiOff(o.Puppet);
            Log((InGame.Count == 1 ? "Duel: enemy " : "Battle: enemy ") + o.Base + " (" + o.Puppet.databaseshipdata.shipPrefabName + ") is now " + (InGame.Count == 1 ? "the opponent" : o.Label + "'s") + " puppet, AI off");
        }

        public static void AiOff(Vessel v)
        {
            if (v.vesselai != null) v.vesselai.enabled = false;
            foreach (MonoBehaviour mb in v.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string n = mb.GetType().Name;
                if (n.StartsWith("Enemy_") || n == "VesselAI" || n == "VesselAINav") mb.enabled = false;
            }
        }

        static void ApplyPlacement()
        {
            if (!_havePlacement || !LocalInMission) return;
            if (!Session.IsHost && _placeSeq != Match.AppliedSeq)
            {
                if (_placeSeq < Match.AppliedSeq || Match.AppliedSeq < 0)
                {
                    _havePlacement = false; // from an earlier match (or we started without the host's map)
                    Log("Placement for another mission ignored (host match " + _placeSeq + ", ours " + Match.AppliedSeq + ")");
                }
                return; // a newer one: keep it until our load has applied that match
            }
            Vessel own = OwnVessel();
            if (own == null) { Log("Placement arrived before our mission was loaded; keeping it for later"); return; }
            Teleport(own, _placeOwnPos, _placeOwnHeading); // host: same spot, new heading
            foreach (Opponent o in InGame) if (o.HavePlace && o.Puppet != null) { Teleport(o.Puppet, o.PlacePos, o.PlaceHeading); o.HavePlace = false; }
            _havePlacement = false;
            PlacementApplied = true;
            Log((InGame.Count == 1 ? "Duel" : "Battle") + ": placement applied");
        }

        /// <summary>Seabed depth in feet at a point; very large if there is no terrain. Takes the shallower of the game's
        /// two probes: the terrain raycast, and the coarse heightmap that MissionManager.Battlestations uses in
        /// LevelLoadManager.CheckSpawnDepth. Where the heightmap says less than 0.5 units of water, Battlestations moves the
        /// sub 20 to 200 units sideways, which shifted duel start ranges (placed 5.9 kyd, 7.3 kyd after Battlestations).</summary>
        public static float SeabedFeet(Vector3 p)
        {
            float ray = 100000f, map = 100000f;
            LevelLoadManager llm = null;
            try { llm = UIFunctions.globaluifunctions.levelloadmanager; } catch (System.Exception) { }
            if (llm == null) return 100000f;
            try { ray = llm.GetTerrainHeightAtPosition(p.x, p.z) * GameDataManager.unitsToFeet; } catch (System.Exception) { }
            try
            {
                if (llm.currentMapGeneratorInstance != null)
                    map = -llm.GetTerrainHeightAtPositionFromHeightMap(p.x, p.z) * GameDataManager.unitsToFeet;
            }
            catch (System.Exception) { }
            return Mathf.Min(ray, map);
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

        /// <summary>Testing: jump our own sub to <paramref name="nm"/> off the nearest opponent's bow, pointing at them.
        /// Our next state update moves our sub on the opponents' screens too.</summary>
        public static void DebugTeleportNearOpponent(float nm)
        {
            Vessel own = OwnVessel();
            if (!InDuel || own == null) return;
            Vessel p = null; float best = float.MaxValue;
            foreach (Opponent o in InGame)
            {
                if (o.Puppet == null || o.Sunk) continue;
                float d = Vector3.Distance(own.transform.position, o.Puppet.transform.position);
                if (d < best) { best = d; p = o.Puppet; }
            }
            if (p == null) return;
            Vector3 fwd = p.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 pos = p.transform.position + fwd * (nm * UnitsPerNm);
            Vector3 toOpp = p.transform.position - pos;
            float heading = Mathf.Atan2(toOpp.x, toOpp.z) * Mathf.Rad2Deg;
            if (heading < 0f) heading += 360f;
            Teleport(own, pos, heading);
            Log("DEBUG: teleported to " + nm.ToString("F1") + " nm off the opponent's bow, heading " + heading.ToString("0"));
        }

        /// <summary>Battlestations prefix: tell the others; hold until they are all there too.</summary>
        public static bool OnBattlestations(MissionManager mm)
        {
            if (!InDuel || !Active || _releasingStations) return true;
            if (!_ownAtStations)
            {
                _ownAtStations = true;
                Session.SendGame(G_STATIONS, w => { }, true);
            }
            if (AllAtStations) return true;
            _heldStations = mm;
            Log("Waiting for " + (InGame.Count == 1 ? "the opponent" : "the other players") + " to reach battlestations (Ctrl+F8: start anyway)");
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
        static bool _ownSunk, _resultFinal;
        static float _startTime;
        public static string Result = "";

        public static void OnOwnSinking()
        {
            if (_ownSunk || !InDuel) return;
            _ownSunk = true;
            if (InGame.Count > 1) Say("Our sub is sinking");
            CheckEnd();
        }

        public static void OnOppSinking(Opponent o)
        {
            if (o == null || o.Sunk || !InDuel) return;
            o.Sunk = true;
            if (InGame.Count > 1) Say(o.Label + "'s " + o.ClassName + " is sinking");
            CheckEnd();
        }

        static void Say(string what)
        {
            Log(what);
            try { PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions; pf.PlayerMessage(what, pf.messageLogColors["Helm"], "CWMP"); }
            catch (System.Exception) { }
        }

        /// <summary>The match is over when at most one side has a sub afloat (sides: the lobby teams when there are two,
        /// else every player for themselves). A later sinking can still turn a defeat into a draw.</summary>
        static void CheckEnd()
        {
            if (!InDuel) return;
            HashSet<int> alive = new HashSet<int>();
            int mine = GroupOf(Session.MyId, MyTeam);
            if (!_ownSunk) alive.Add(mine);
            foreach (Opponent o in InGame) if (!o.Sunk && o.Puppet != null && _opps.ContainsKey(o.Id)) alive.Add(GroupOf(o.Id, o.Team));
            if (alive.Count > 1) return;
            string what;
            if (alive.Count == 0) what = InGame.Count == 1 ? "DRAW: both subs lost" : "DRAW: every sub lost";
            else if (alive.Contains(mine)) what = InGame.Count == 1 ? "VICTORY: the opponent's " + InGame[0].ClassName + " is sinking" : (_ownSunk ? "VICTORY for your side (your sub was lost)" : "VICTORY: last side afloat");
            else what = InGame.Count == 1 ? "DEFEAT: your sub is sinking" : "DEFEAT: your side is out";
            if (_resultFinal && Result.StartsWith(what)) return;
            _resultFinal = true;
            float mins = (Time.time - _startTime) / 60f;
            Result = what + " (" + mins.ToString("0.0") + " min; torpedoes fired " + Weapons.OwnFired + " vs " + Weapons.OppFired
                + ", hits " + Weapons.OwnHits + " vs " + Weapons.OppHits + ")";
            Log("RESULT " + Result);
            Say(what);
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
            InDuel = false; LocalInMission = false;
            foreach (Opponent o in InGame) o.Puppet = null;
            InGame.Clear();
            _choiceMade = false; _heldLoad = null; // a new mission needs a new choice
            _ownAtStations = false; _heldStations = null;
            TimeSync.Reset();
            Match.RestoreLocalDifficulty();
            foreach (Opponent o in Opps)
            {
                o.AtStations = false; o.MissionId = -1;
                o.InOldMission = o.InMission;
                if (o.InMission)
                {
                    // They are still in the old mission: their sub choice (and, for a client, the host's map) belong
                    // to it. They send fresh ones when they choose again.
                    o.Class = -1; o.ClassName = "";
                    if (!Session.IsHost && o.Id == 0) Match.OnHostLeftMission();
                }
            }
            _ownSunk = false; // keep Result on the overlay until the next mission
            EnvSync.HostNewSeed(); // host: new ocean for the next mission (the clients keep the latest seed they got)
            Weapons.Reset();
            Ghosts.Reset();
            Tracks.Reset();
            _oppColorLogged = false;
            Ambient.Reset();
        }

        // ---------- per-frame ----------

        public static void Update()
        {
            if (!InDuel || !Active) return;
            if (InGame.Count == 0 || OwnVessel() == null) return;

            _diagTimer += Time.unscaledDeltaTime;
            if (_diagTimer >= 10f) { _diagTimer = 0f; LogSonarDiagnostics(); }

            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer >= 0.1f)
            {
                _sendTimer = 0f;
                SendOwnState(OwnVessel());
            }
            foreach (Opponent o in InGame) if (o.Puppet != null) DrivePuppet(o);
            try { Ghosts.Update(); } catch (System.Exception ex) { Log("~Ghost update failed: " + ex); }
            try { Tracks.Update(); } catch (System.Exception ex) { Log("~Track update failed: " + ex.Message); }
            try { ApplyOpponentColor(); } catch (System.Exception) { }
            try { Ambient.Update(); } catch (System.Exception ex) { Log("~Neutral update failed: " + ex.Message); }
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
                w.Write(TowedState(v));
            }, false);
        }

        static void DrivePuppet(Opponent o)
        {
            if (!o.HaveState) return;
            Vessel puppet = o.Puppet;
            SubState s0 = o.S0, s1 = o.S1;
            VesselMovement m = puppet.vesselmovement;
            // Inputs, so the game's own movement and noise code see the right values
            m.telegraphValue = s1.Telegraph;
            m.rudderAngle = s1.Rudder;
            m.ballastAngle = s1.Ballast;
            m.isCavitating = s1.Cav;
            puppet.acoustics.usingActiveSonar = s1.Active;
            // Their pings, at the player's ping rate: ping sound from their sub and the ping line on our map.
            if (s1.Active && !puppet.isSinking)
            {
                PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
                o.PingTimer += Time.deltaTime;
                if (o.PingTimer > pf.activeSonarPingRate)
                {
                    o.PingTimer -= pf.activeSonarPingRate;
                    UIFunctions.globaluifunctions.combatai.PlaySonarPing(puppet);
                    pf.sensormanager.tacticalmap.DrawPingLine(puppet.transform, pf.sensormanager.tacticalmap.pingLines[0]);
                }
            }
            else o.PingTimer = UIFunctions.globaluifunctions.playerfunctions.activeSonarPingRate; // ping at once when they start
            Vector3 sp = m.shipSpeed; sp.z = s1.SpeedZ; m.shipSpeed = sp;
            puppet.submerged = s1.Submerged;
            ApplyTowed(puppet, s1.Towed);

            // Extrapolate the target pose from the last two states (unit-agnostic)
            Vector3 target = s1.Pos;
            float dtStates = s1.T - s0.T;
            if (o.StateCount > 1 && dtStates > 0.001f && dtStates < 2f)
            {
                Vector3 vel = (s1.Pos - s0.Pos) / dtStates;
                float ahead = Mathf.Min(Time.time - o.S1Local, 1f);
                target += vel * ahead;
            }
            Transform tr = puppet.transform;
            Vector3 err = target - tr.position;
            if (err.magnitude > 1.0f) tr.position = target;                 // > ~75 yd: snap
            else tr.position += err * Mathf.Min(1f, Time.deltaTime * 4f);    // smooth
            Quaternion want = Quaternion.Euler(s1.Euler);
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
            if (Ambient.OnMessage(type, r)) return;
            if (Session.LastSender == Session.MyId) return; // never our own
            Opponent o = Sender;
            string who = Opps.Count <= 1 ? "Opponent" : o.Label;
            switch (type)
            {
                case G_SUBCHOICE:
                    o.Class = r.ReadInt32(); o.ClassName = r.ReadString(); o.RpgSelfNoiseBonus = r.ReadSingle();
                    o.MissionId = r.ReadInt32();
                    if (o.Class < 0 || o.Class >= UIFunctions.globaluifunctions.database.databaseshipdata.Length) { Log("Ignored an unknown sub class " + o.Class); o.Class = -1; o.ClassName = ""; break; }
                    int ownMission = UIFunctions.globaluifunctions.missionmanager.currentMissionID;
                    if (o.MissionId != ownMission) Log("Note: " + who + " picked mission " + o.MissionId + ", you picked " + ownMission + "; the host's map and conditions are used.");
                    string mine = ClassName(o.Class);
                    if (mine != o.ClassName) Log("WARNING: " + who + "'s sub " + o.ClassName + " is " + mine + " here (data mismatch?)");
                    Log(who + " chose " + o.ClassName);
                    if (_heldLoad != null && AllChosen && !AnyInOldMission && (Session.IsHost || Match.HaveHostMatch)) ReleaseHeldLoad(who + " chose " + o.ClassName);
                    if (InDuel && o.Puppet != null && o.Puppet.databaseshipdata.shipPrefabName != o.ClassName)
                        Log("WARNING: " + who + "'s puppet is a " + o.Puppet.databaseshipdata.shipPrefabName + " but they chose " + o.ClassName + ". Pick all subs before starting any mission.");
                    break;
                case G_PLACEMENT:
                {
                    // From the host: everyone's start position. The host often sends this before our own load has
                    // applied its match, so keep it with its number; ApplyPlacement checks it.
                    int seq = r.ReadInt32(); int n = r.ReadInt32();
                    if (n < 0 || n > Session.MaxPlayers) break;
                    List<int> ids = new List<int>(); List<Vector3> pos = new List<Vector3>(); List<float> hdg = new List<float>();
                    for (int k = 0; k < n; k++) { ids.Add(r.ReadInt32()); pos.Add(ReadV3(r)); hdg.Add(r.ReadSingle()); }
                    TakePlacement(ids, pos, hdg, seq);
                    ApplyPlacement();
                    break;
                }
                case G_STATIONS:
                    o.AtStations = true;
                    Log(who + " is at battlestations");
                    if (_heldStations != null && AllAtStations) ReleaseStations(Opps.Count <= 1 ? "opponent ready" : "all players ready");
                    break;
                case G_INMISSION:
                    o.InMission = r.ReadBoolean();
                    Log(who + (o.InMission ? " is in the mission" : " left the mission"));
                    if (!o.InMission && InDuel && InGame.Count > 1 && o.Puppet != null && !o.Sunk)
                    {
                        // They quit a battle that goes on: their sub leaves it (far off the map, out of hearing) and is out.
                        Vector3 far = new Vector3(0f, o.Puppet.transform.position.y, -250f * UnitsPerNm);
                        o.Puppet.transform.position = far; o.HaveState = false;
                        o.Sunk = true;
                        Say(o.Label + " left the battle");
                        CheckEnd();
                    }
                    if (!o.InMission)
                    {
                        o.InOldMission = false;
                        if (!Session.IsHost && o.Id == 0) Match.OnHostLeftMission();
                        TimeSync.Reset();
                        o.Class = -1; o.ClassName = ""; o.AtStations = false; o.MissionId = -1; // they will choose again
                        if (_heldLoad != null && AllChosen && !AnyInOldMission && (Session.IsHost || Match.HaveHostMatch)) ReleaseHeldLoad(who + " left the old mission");
                    }
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
                    s.Towed = r.ReadByte();
                    if (o.StateCount > 0 && s.T <= o.S1.T) break; // stale/out of order
                    if (o.Sunk && !o.InMission) break; // they left this battle: their next mission is not ours
                    o.S0 = o.StateCount > 0 ? o.S1 : s; o.S1 = s; o.S1Local = Time.time;
                    o.RunningSilent = s.Silent;
                    if (s.Active != o.ActiveSonar) Log(who + " switched to " + (s.Active ? "ACTIVE" : "passive") + " sonar");
                    o.ActiveSonar = s.Active;
                    o.StateCount++; o.HaveState = true;
                    break;
                }
            }
        }

        // ---------- towed array: DotMod runs it by speed for every AI vessel; for the puppet and ghosts it follows the network ----------

        /// <summary>0 = in, 1 = out, 2 = destroyed.</summary>
        public static byte TowedState(Vessel v)
        {
            DotModTowedArray ta = v != null && v.vesselmovement != null ? v.vesselmovement.towedArray : null;
            if (ta == null) return 0;
            try { return ta.isDestroyed ? (byte)2 : ta.GetDeployed() ? (byte)1 : (byte)0; } catch (System.Exception) { return 0; }
        }

        public static void ApplyTowed(Vessel v, byte state)
        {
            DotModTowedArray ta = v != null && v.vesselmovement != null ? v.vesselmovement.towedArray : null;
            if (ta == null) return;
            try
            {
                if (state == 2) { if (!ta.isDestroyed) ta.SetDestroyed(true); }
                else
                {
                    if (ta.isDestroyed) ta.SetDestroyed(false); // a reused ghost
                    bool want = state == 1;
                    if (ta.GetDeployed() != want) ta.SetDeployed(want);
                }
            }
            catch (System.Exception) { } // before DotMod's Start has run
        }

        public static bool IsRemoteArray(DotModTowedArray ta)
        {
            if (!InDuel || ta == null) return false;
            foreach (Opponent o in InGame) if (o.Puppet != null && o.Puppet.vesselmovement != null && o.Puppet.vesselmovement.towedArray == ta) return true;
            return Ghosts.OwnsArray(ta);
        }

        static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static Vector3 ReadV3(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }

        public static bool IsPuppet(Vessel v) { return ByPuppet(v) != null; }
        public static bool IsPuppetAI(VesselAI ai)
        {
            if (!InDuel || ai == null) return false;
            foreach (Opponent o in InGame) if (o.Puppet != null && o.Puppet.vesselai == ai) return true;
            return false;
        }
        /// <summary>A puppet or one of the decoy ghosts: network-driven, AI off.</summary>
        public static bool IsRemote(Vessel v) { return IsPuppet(v) || Ghosts.IsGhost(v); }
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
            return !(Duel.InDuel && (Duel.IsPuppetAI(__instance) || Ghosts.IsGhostAI(__instance) || Ambient.IsRemoteAI(__instance)));
        }
    }

    [HarmonyPatch(typeof(DotModTowedArray), "Update")]
    static class Patch_TowedArrayUpdate
    {
        static bool Prefix(DotModTowedArray __instance) { return !Duel.IsRemoteArray(__instance); }
    }

    /// <summary>SensorManager.CheckIfIsInBaffles waives the baffles (bearing beyond 150 deg off the bow) for any listener whose
    /// class has a towed sonar while it makes 0..10 kt, for the player as long as the TOWED subsystem is undamaged. It never
    /// looks at DotMod's deploy state, so an LA with its array stowed heard straight astern (Raf, 27 Nov). In a duel the
    /// waiver needs a deployed, intact DotMod array; without one the normal 150 deg baffle applies. Vessels without a
    /// DotMod array object keep the game's rule.</summary>
    [HarmonyPatch(typeof(SensorManager), "CheckIfIsInBaffles")]
    static class Patch_CheckIfIsInBaffles
    {
        static bool Prefix(Vessel listeningVessel, Transform noiseLocation, ref bool __result)
        {
            try
            {
                if (!Duel.InDuel || listeningVessel == null || noiseLocation == null || listeningVessel.vesselmovement == null) return true;
                DotModTowedArray ta = listeningVessel.vesselmovement.towedArray;
                if (ta == null || (ta.GetDeployed() && !ta.isDestroyed)) return true; // the game's rule (array out: no baffles below 10 kt)
                float b = UIFunctions.globaluifunctions.GetBearingToTransform(listeningVessel.transform, noiseLocation);
                __result = Mathf.Abs(b) > 150f;
                return false;
            }
            catch (System.Exception) { return true; }
        }
    }

    /// <summary>GetContactColor gives any slot the HUD's EnemyColor (navyColors[1]; DotMod HUDs: blue, vanilla: red) unless
    /// its classified class is civilian, whether the slot is classified or not. There is no side rule in the game. In a
    /// duel (Raf, 27 Nov; needed for 2v2 later): a classified contact gets the colour of its class's side, NATO in DotMod's
    /// enemy blue and WP/PLAN in the vanilla enemy red; civilians keep the game's colour; an unclassified slot (also one
    /// whose track Tracks dropped or moved, and the "decoy?" untag) gets NeutralColor (navyColors[2]), as TacticalMapInit
    /// gives every contact. The own sub keeps PlayerColor.</summary>
    [HarmonyPatch(typeof(SensorManager), "GetContactColor")]
    static class Patch_GetContactColor
    {
        static void Postfix(SensorManager __instance, int i, ref Color __result)
        {
            try
            {
                if (!Duel.InDuel) return;
                TacticalMap tm = __instance.tacticalmap;
                Color[] nc = tm != null ? tm.navyColors : null;
                if (nc == null || nc.Length < 3) return;
                if (__instance.classifiedByPlayer == null || i < 0 || i >= __instance.classifiedByPlayer.Length || !__instance.classifiedByPlayer[i]) { __result = nc[2]; return; }
                DatabaseShipData d = UIFunctions.globaluifunctions.database.databaseshipdata[__instance.classifiedByPlayerAsClass[i]];
                if (d.shipType == "BIOLOGIC" || Database.GetIsCivilian(d.shipPrefabName)) return; // the game's (green)
                __result = Duel.SideColor(d.shipPrefabName);
            }
            catch (System.Exception) { }
        }
    }

    /// <summary>Fairness: give the puppet the player-only quieting the real player gets.</summary>
    [HarmonyPatch(typeof(SensorManager), "GetVesselNoiseLevel")]
    static class Patch_NoiseLevel
    {
        static void Postfix(SensorManager __instance, Vessel activeVessel, ref float __result)
        {
            if (Duel.InDuel) { try { Ghosts.RayFocus(activeVessel); } catch (System.Exception) { } }
            if (Duel.InDuel && Ghosts.Noise(activeVessel, ref __result)) return;
            Opponent opp = Duel.ByPuppet(activeVessel);
            if (opp == null) return;
            if (opp.RunningSilent)
            {
                float b = Random.Range(__instance.runningSilentBonus.x, __instance.runningSilentBonus.y);
                if (activeVessel.databaseshipdata.isDiesel) b *= Random.Range(1.2f, 1.8f);
                __result -= b;
            }
            // The player formula subtracts the RPG bonus before compression.
            if (__instance.passiveCompressionFactor != 0f) __result -= opp.RpgSelfNoiseBonus / __instance.passiveCompressionFactor;
            // A decoy copies the hull's noise, not the sonar's. The game's AI rule has already added output/4 for a pinging
            // vessel (when it was below the output), so take that off again.
            float hull = __result;
            int asid = activeVessel.databaseshipdata.activeSonarID;
            if (activeVessel.acoustics.usingActiveSonar && asid >= 0)
            {
                float o = UIFunctions.globaluifunctions.database.databasesonardata[asid].sonarOutput;
                if (hull - o / 4f < o) hull -= o / 4f;
            }
            opp.LastNoise = hull;
            opp.LastSpeedNoise = Mathf.Abs(activeVessel.vesselmovement.shipSpeed.z) * 10f * __instance.targetNoisePerKnot
                + (activeVessel.vesselmovement.isCavitating ? __instance.noiseFromCavitation : 0f);
            // A pinging player is at least as loud as their sonar's output (player rule, not the AI's +output/4).
            int sid = activeVessel.databaseshipdata.activeSonarID;
            if (opp.ActiveSonar && sid >= 0)
            {
                float output = UIFunctions.globaluifunctions.database.databasesonardata[sid].sonarOutput;
                if (__result < output) __result = output;
            }
        }
    }

    /// <summary>Difficulty: SaveVesselAcoustics multiplies every non-player vessel's noise (a dB value) by
    /// EnemyNoiseModifier (Easy 1.3, Normal 1.1, Hard 1, Elite 0.9), i.e. +40 to -13 dB. In a duel that would let
    /// the difficulty set the detection range against the opponent, so non-player vessels (opponent, decoy ghosts,
    /// neutrals) use 1.0 there. The modifier is read only here; the player's own settings are put back right after.</summary>
    [HarmonyPatch(typeof(SensorManager), "SaveVesselAcoustics")]
    static class Patch_SaveVesselAcoustics
    {
        const string Key = "EnemyNoiseModifier";

        static void Prefix(Vessel activeVessel, ref float __state)
        {
            __state = float.NaN;
            if (!Duel.InDuel || activeVessel == null || activeVessel.playercontrolled) return;
            Dictionary<string, float> d = OptionsManager.difficultySettings;
            float m;
            if (d == null || !d.TryGetValue(Key, out m) || m == 1f) return;
            __state = m;
            d[Key] = 1f;
        }

        static void Postfix(ref float __state)
        {
            if (float.IsNaN(__state) || OptionsManager.difficultySettings == null) return;
            OptionsManager.difficultySettings[Key] = __state;
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

namespace ColdWatersMP
{
    /// <summary>The briefing (PopulateBriefingText) moves the player's sub next to the "loudest" vessel of the mission,
    /// which in a duel may be a merchant or happen after our placement: the host then sits miles from where the client
    /// was placed (2 kyd placed, 24 kyd measured). In a duel, keep our sub where the placement put it.</summary>
    [HarmonyLib.HarmonyPatch(typeof(MissionManager), "MovePlayerToStartPosition")]
    static class Patch_MovePlayerToStartPosition
    {
        static void Prefix(out UnityEngine.Vector3 __state)
        {
            Vessel own = GameDataManager.playervesselsonlevel != null && GameDataManager.playervesselsonlevel.Length > 0 ? GameDataManager.playervesselsonlevel[0] : null;
            __state = own != null ? own.transform.position : new UnityEngine.Vector3(float.NaN, 0f, 0f);
            _rot = own != null ? own.transform.rotation : UnityEngine.Quaternion.identity;
        }
        static UnityEngine.Quaternion _rot;

        static void Postfix(UnityEngine.Vector3 __state)
        {
            if (!Duel.InDuel || !Duel.PlacementApplied || float.IsNaN(__state.x)) return; // only a later call ("back to briefing")
            Vessel own = GameDataManager.playervesselsonlevel[0];
            if (own == null) return;
            float moved = UnityEngine.Vector3.Distance(own.transform.position, __state);
            own.transform.position = __state; own.transform.rotation = _rot;
            Duel.Log("~Duel: briefing tried to move our sub " + moved.ToString("F0") + " units; kept the duel placement");
        }
    }
}

namespace ColdWatersMP
{
    /// <summary>Log when Battlestations' spawn-depth check moves our sub or the opponent's puppet in a duel.</summary>
    [HarmonyLib.HarmonyPatch(typeof(LevelLoadManager), "CheckSpawnDepth")]
    static class Patch_CheckSpawnDepth_Log
    {
        static void Prefix(Vessel activeVessel, out Vector3 __state)
        {
            __state = activeVessel != null ? activeVessel.transform.position : Vector3.zero;
        }

        static void Postfix(Vessel activeVessel, Vector3 __state)
        {
            if (!Duel.InDuel || activeVessel == null) return;
            try
            {
                bool own = activeVessel.playercontrolled, opp = Duel.IsPuppet(activeVessel);
                if (!own && !opp) return;
                Vector3 d = activeVessel.transform.position - __state; d.y = 0f;
                if (d.sqrMagnitude < 0.01f) return;
                Duel.Log("~CheckSpawnDepth moved " + (own ? "our sub" : "the opponent's puppet") + " " + d.magnitude.ToString("F0")
                    + " units (" + (d.magnitude / Duel.UnitsPerNm).ToString("F2") + " nm): heightmap seabed too shallow there");
            }
            catch (System.Exception) { }
        }
    }
}
