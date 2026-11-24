// Cold Waters MP - match settings: the host picks the map and the conditions for both players. C# 5 / .NET 3.5.
// The map is whatever single mission the host starts (the duel strips its ships anyway). When the host starts
// loading, it applies its condition settings to its LevelLoadData and sends the map and environment fields to
// the client. The client holds its own load until they arrive and then overwrites its own mission's fields, so
// the client's mission pick no longer matters. Weather rolls that stay random are kept equal by EnvSync's seed.
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Match
    {
        public const byte G_MATCH = 15;

        // ---- host settings (edited in the F6 window, defaults from the config) ----
        public enum CondMode { Mission = 0, Random = 1, Custom = 2 }
        public static CondMode Conditions = CondMode.Mission;
        public static int Weather = 0;      // index into sensormanager.weather
        public static int SeaState = 2;     // index into sensormanager.seaStates
        public static int Layer = 0;        // 0 = random, else strengthTypes index + 1 (strength = idx / 5)
        public static int Duct = 0;         // same
        public static int TimeChoice = 0;   // 0 random, 1 dawn, 2 day, 3 dusk, 4 night
        public static bool HostPauseBoth = false;  // host setting: Esc menu pauses both games (else pause is blocked)
        public static bool HostAllowTC = true;     // host setting: time compression by mutual agreement
        public static bool HostReducedVisibility = true; // host setting: DotMod's Reduced Visibility (denser underwater fog) for both
        static bool _matchReducedVis = true, _clientReducedVis = true;
        static bool _haveSavedFog, _savedFogAlt, _savedDevFog, _visInForce;

        /// <summary>Both games use the host's underwater visibility for the match (clearer water is an advantage).
        /// DotMod: fogDensityAlt = !reducedVisibility. The player's own setting comes back afterwards.</summary>
        static void ApplyVisibility(bool reduced)
        {
            LevelLoadManager llm = UIFunctions.globaluifunctions != null ? UIFunctions.globaluifunctions.levelloadmanager : null;
            if (llm == null) return;
            if (!_haveSavedFog) { _savedFogAlt = llm.fogDensityAlt; _savedDevFog = DevMode.overrideFogDensity; _haveSavedFog = true; }
            _visInForce = reduced;
            HoldVisibility();
            Duel.Log("Reduced Visibility for this match: " + (reduced ? "on" : "off") + " (host's setting)");
        }

        /// <summary>Every frame in a match: keep the match value (the DotMod options toggle could change it mid-game),
        /// and keep DotMod's dev-mode fog override off.</summary>
        public static void HoldVisibility()
        {
            if (!_haveSavedFog) return;
            LevelLoadManager llm = UIFunctions.globaluifunctions != null ? UIFunctions.globaluifunctions.levelloadmanager : null;
            if (llm == null) return;
            if (llm.fogDensityAlt == _visInForce) llm.fogDensityAlt = !_visInForce;
            if (DevMode.overrideFogDensity) DevMode.overrideFogDensity = false;
        }

        /// <summary>OptionsManager.Instance.settingsData[section][key] by reflection (avoids referencing INIFileParser).</summary>
        static string DotModSetting(string section, string key)
        {
            object data = HarmonyLib.AccessTools.Field(typeof(OptionsManager), "settingsData").GetValue(OptionsManager.Instance);
            object sec = data.GetType().GetProperty("Item", new System.Type[] { typeof(string) }).GetValue(data, new object[] { section });
            return (string)sec.GetType().GetProperty("Item", new System.Type[] { typeof(string) }).GetValue(sec, new object[] { key });
        }

        static void RestoreVisibility()
        {
            if (!_haveSavedFog) return;
            LevelLoadManager llm = UIFunctions.globaluifunctions != null ? UIFunctions.globaluifunctions.levelloadmanager : null;
            // The player's current DotMod setting (they may have changed it in the options during the match).
            bool own = _savedFogAlt;
            try { own = !bool.Parse(DotModSetting("Options", "reducedVisibility")); } catch (System.Exception) { }
            if (llm != null) llm.fogDensityAlt = own;
            DevMode.overrideFogDensity = _savedDevFog;
            _haveSavedFog = false;
        }
        static bool _clientPauseBoth, _clientAllowTC = true;
        /// <summary>The setting in force: the host's own, or what the host sent us.</summary>
        static bool _matchPauseBoth, _matchAllowTC = true; // host: the values sent with the current match
        public static bool PauseBoth { get { return Duel.Session != null && !Duel.Session.IsHost ? _clientPauseBoth : _matchPauseBoth; } }
        public static bool ReducedVisibility { get { return Duel.Session != null && !Duel.Session.IsHost ? _clientReducedVis : _matchReducedVis; } }
        public static bool TimeCompressionAllowed { get { return Duel.Session != null && !Duel.Session.IsHost ? _clientAllowTC : _matchAllowTC; } }
        public static readonly string[] TimeNames = { "Random", "Dawn", "Day", "Dusk", "Night" };
        static readonly int[] TimeHours = { -1, 7, 12, 17, 1 };

        // ---- what the client received ----
        static bool _haveHostMatch;
        static int _hostSeq, _sentSeq;
        /// <summary>Client: number of the host match our current mission was built from (-1: none).</summary>
        public static int AppliedSeq = -1;
        /// <summary>Host: number of the match we sent for our current mission.</summary>
        public static int SentSeq { get { return _lastSentSeq; } }
        public static string HostMapName = "";
        public static string LastSummary = "";
        static Snapshot _host;

        public static bool HaveHostMatch { get { return _haveHostMatch; } }

        class Snapshot
        {
            public string MissionName = "";
            public bool UseTerrain; public Vector2 MapPosition; public string MapElevation = "", MapNavigation = "", WorldObjects = "", MapFromCampaign = "";
            public string Date = "", Month = "", Season = "", Hemisphere = ""; public bool ForceIce;
            public bool PresetEnv; public int TimeOfDay; public string TimeString = ""; public int Environment; public int SeaState;
            public float Duct, Layer; public string CombatLocation = ""; public Vector2 MissionPosition;
        }

        static Snapshot Take(LevelLoadData d)
        {
            Snapshot s = new Snapshot();
            s.MissionName = string.IsNullOrEmpty(d.missionName) ? "mission " + UIFunctions.globaluifunctions.missionmanager.currentMissionID : d.missionName;
            s.UseTerrain = d.useTerrain; s.MapPosition = d.mapPosition;
            s.MapElevation = d.mapElevationData ?? ""; s.MapNavigation = d.mapNavigationData ?? "";
            s.WorldObjects = d.worldObjectsData ?? ""; s.MapFromCampaign = d.mapFromCampaign ?? "";
            s.Date = d.date ?? ""; s.Month = d.month ?? ""; s.Season = d.season ?? ""; s.Hemisphere = d.hemisphere ?? ""; s.ForceIce = d.forceIce;
            s.PresetEnv = d.usePresetEnvironment; s.TimeOfDay = d.timeofday; s.TimeString = d.timeOfDayString ?? "";
            s.Environment = d.environment; s.SeaState = d.seaState; s.Duct = d.ductStrength; s.Layer = d.layerStrength;
            s.CombatLocation = d.combatLocation ?? ""; s.MissionPosition = d.missionPosition;
            return s;
        }

        static void Apply(Snapshot s, LevelLoadData d)
        {
            d.useTerrain = s.UseTerrain; d.mapPosition = s.MapPosition;
            d.mapElevationData = s.MapElevation; d.mapNavigationData = s.MapNavigation;
            d.worldObjectsData = s.WorldObjects; d.mapFromCampaign = s.MapFromCampaign;
            d.date = s.Date; d.month = s.Month; d.season = s.Season; d.hemisphere = s.Hemisphere; d.forceIce = s.ForceIce;
            d.usePresetEnvironment = s.PresetEnv; d.timeofday = s.TimeOfDay; d.timeOfDayString = s.TimeString;
            d.environment = s.Environment; d.seaState = s.SeaState; d.ductStrength = s.Duct; d.layerStrength = s.Layer;
            d.combatLocation = s.CombatLocation; d.missionPosition = s.MissionPosition;
            ClearHazards(d);
            d.usePresetPositions = false; // the host places the client
        }

        /// <summary>A duel has no minefields or sonobuoys (both sides).</summary>
        static void ClearHazards(LevelLoadData d)
        {
            d.proximityMineLocations = new Vector2[0];
            d.sonobuoyLocations = new Vector3[0];
        }

        static void Write(BinaryWriter w, Snapshot s)
        {
            w.Write(s.MissionName);
            w.Write(s.UseTerrain); w.Write(s.MapPosition.x); w.Write(s.MapPosition.y);
            w.Write(s.MapElevation); w.Write(s.MapNavigation); w.Write(s.WorldObjects); w.Write(s.MapFromCampaign);
            w.Write(s.Date); w.Write(s.Month); w.Write(s.Season); w.Write(s.Hemisphere); w.Write(s.ForceIce);
            w.Write(s.PresetEnv); w.Write(s.TimeOfDay); w.Write(s.TimeString); w.Write(s.Environment); w.Write(s.SeaState);
            w.Write(s.Duct); w.Write(s.Layer); w.Write(s.CombatLocation); w.Write(s.MissionPosition.x); w.Write(s.MissionPosition.y);
        }

        static Snapshot Read(BinaryReader r)
        {
            Snapshot s = new Snapshot();
            s.MissionName = r.ReadString();
            s.UseTerrain = r.ReadBoolean(); s.MapPosition = new Vector2(r.ReadSingle(), r.ReadSingle());
            s.MapElevation = r.ReadString(); s.MapNavigation = r.ReadString(); s.WorldObjects = r.ReadString(); s.MapFromCampaign = r.ReadString();
            s.Date = r.ReadString(); s.Month = r.ReadString(); s.Season = r.ReadString(); s.Hemisphere = r.ReadString(); s.ForceIce = r.ReadBoolean();
            s.PresetEnv = r.ReadBoolean(); s.TimeOfDay = r.ReadInt32(); s.TimeString = r.ReadString(); s.Environment = r.ReadInt32(); s.SeaState = r.ReadInt32();
            s.Duct = r.ReadSingle(); s.Layer = r.ReadSingle(); s.CombatLocation = r.ReadString(); s.MissionPosition = new Vector2(r.ReadSingle(), r.ReadSingle());
            return s;
        }

        /// <summary>Host, in the LoadLevel prefix: apply our condition settings and send map + conditions.</summary>
        public static void HostPrepare(LevelLoadData d)
        {
            if (d == null) return;
            ApplyConditionSettings(d);
            ClearHazards(d);
            _lastSent = Take(d);
            _lastSentSeq = ++_sentSeq;
            _matchPauseBoth = HostPauseBoth; _matchAllowTC = HostAllowTC; _matchReducedVis = HostReducedVisibility;
            ApplyVisibility(_matchReducedVis);
            SendLast();
        }

        static Snapshot _lastSent;
        static int _lastSentSeq;

        /// <summary>Host: send the current match again (the client reconnected while we wait to load).</summary>
        public static void HostResend() { if (_lastSent != null) { SendLast(); Duel.Log("Match re-sent to the client"); } }

        static void SendLast()
        {
            Snapshot s = _lastSent; int seq = _lastSentSeq;
            bool pause = _matchPauseBoth, tc = _matchAllowTC, vis = _matchReducedVis;
            Dictionary<string, float> diff = OptionsManager.difficultySettings != null ? new Dictionary<string, float>(OptionsManager.difficultySettings) : new Dictionary<string, float>();
            Duel.Session.SendGame(G_MATCH, w =>
            {
                w.Write(seq); Write(w, s); w.Write(pause); w.Write(tc); w.Write(vis);
                w.Write(diff.Count);
                foreach (KeyValuePair<string, float> kv in diff) { w.Write(kv.Key); w.Write(kv.Value); }
            }, true);
            LastSummary = Describe(s);
            Duel.Log("Match sent: " + LastSummary);
        }

        /// <summary>Client, in the LoadLevel prefix: use the host's map and conditions.</summary>
        public static void ClientApply(LevelLoadData d)
        {
            if (!_haveHostMatch || d == null) return;
            Apply(_host, d);
            AppliedSeq = _hostSeq;
            ApplyHostDifficulty();
            ApplyVisibility(_clientReducedVis);
            LastSummary = Describe(_host);
            Duel.Log("Match applied from host: " + LastSummary);
        }

        // Difficulty: both games use the host's difficulty values for the match (hull points, TMA rate, noise,
        // reload times ...), so neither side has an easier game. The client's own values come back afterwards.
        static Dictionary<string, float> _hostDifficulty, _savedDifficulty;

        static void ApplyHostDifficulty()
        {
            Dictionary<string, float> live = OptionsManager.difficultySettings;
            if (_hostDifficulty == null || _hostDifficulty.Count == 0 || live == null) return;
            if (_savedDifficulty == null) _savedDifficulty = new Dictionary<string, float>(live);
            int changed = 0;
            foreach (KeyValuePair<string, float> kv in _hostDifficulty)
            {
                float old;
                if (!live.TryGetValue(kv.Key, out old) || old != kv.Value) changed++;
                live[kv.Key] = kv.Value;
            }
            Duel.Log("Difficulty: using the host's values (" + changed + " differed from yours)");
        }

        /// <summary>Mission over: give the client its own difficulty back.</summary>
        public static void RestoreLocalDifficulty()
        {
            RestoreVisibility();
            if (_savedDifficulty == null || OptionsManager.difficultySettings == null) return;
            foreach (KeyValuePair<string, float> kv in _savedDifficulty) OptionsManager.difficultySettings[kv.Key] = kv.Value;
            _savedDifficulty = null;
            Duel.Log("Difficulty: your own values restored");
        }

        /// <summary>The host left its mission: whatever it sent is for the old mission.</summary>
        public static void OnHostLeftMission() { _haveHostMatch = false; }

        static void ApplyConditionSettings(LevelLoadData d)
        {
            if (Conditions == CondMode.Mission) return;
            if (Conditions == CondMode.Random)
            {
                d.usePresetEnvironment = false; // the game rolls weather, sea and time; EnvSync keeps both rolls equal
                return;
            }
            SensorManager sm = UIFunctions.globaluifunctions.playerfunctions.sensormanager;
            d.usePresetEnvironment = true;
            d.environment = Mathf.Clamp(Weather, 0, sm.weather.Length - 1);
            d.seaState = Mathf.Clamp(SeaState, 0, sm.seaStates.Length - 1);
            d.layerStrength = Layer == 0 ? -1f : (Layer - 1) / 5f;
            d.ductStrength = Duct == 0 ? -1f : (Duct - 1) / 5f;
            int hour = TimeHours[Mathf.Clamp(TimeChoice, 0, TimeHours.Length - 1)];
            if (hour < 0) hour = new System.Random().Next(0, 24);
            d.timeOfDayString = (hour * 100).ToString("0000");
            d.timeofday = UIFunctions.globaluifunctions.campaignmanager.GetCampaignTimeOfDay(hour);
        }

        static string Describe(Snapshot s)
        {
            string env;
            if (!s.PresetEnv) env = "random weather (same seed both sides)";
            else
            {
                SensorManager sm = UIFunctions.globaluifunctions.playerfunctions.sensormanager;
                env = Name(sm.weather, s.Environment) + ", sea " + Name(sm.seaStates, s.SeaState)
                    + ", layer " + (s.Layer < 0f ? "random" : s.Layer.ToString("0.0")) + ", duct " + (s.Duct < 0f ? "random" : s.Duct.ToString("0.0"))
                    + ", " + s.TimeString;
            }
            return "map '" + s.MissionName + "' at " + s.MapPosition.x.ToString("0") + "," + s.MapPosition.y.ToString("0") + (s.UseTerrain ? " (terrain)" : "")
                + ", " + s.Date + ", " + env;
        }

        public static string Name(string[] a, int i) { return a != null && i >= 0 && i < a.Length ? a[i] : i.ToString(); }

        public static bool OnMessage(byte type, BinaryReader r)
        {
            if (type != G_MATCH) return false;
            int seq = r.ReadInt32();
            Snapshot s = Read(r);
            bool pause = r.ReadBoolean(), tc = r.ReadBoolean(), vis = r.ReadBoolean();
            int n = r.ReadInt32();
            Dictionary<string, float> diff = new Dictionary<string, float>();
            for (int i = 0; i < n; i++) { string k = r.ReadString(); diff[k] = r.ReadSingle(); }
            if (Duel.Session.IsHost) return true; // only the host decides
            _hostDifficulty = diff;
            _clientPauseBoth = pause; _clientAllowTC = tc; _clientReducedVis = vis;
            _host = s; _hostSeq = seq; _haveHostMatch = true;
            HostMapName = s.MissionName;
            Duel.Log("Host picked " + Describe(s));
            Duel.OnHostMatchArrived();
            return true;
        }
    }
}

namespace ColdWatersMP
{
    /// <summary>In multiplayer, every playable submarine can be chosen in any single mission (missions often
    /// restrict the list). The full list is the testing grounds' (single001) PlayerVessels, submarines only.</summary>
    [HarmonyLib.HarmonyPatch(typeof(TextParser), "PopulateLevelLoadData")]
    public static class Patch_PopulateLevelLoadData_AllSubs
    {
        static int[] _allSubs;

        static void Postfix(TextParser __instance, string filename)
        {
            if (!GameDataManager.missionMode || Duel.Session == null || Duel.Session.State != SessionState.Ready) return;
            try
            {
                if (_allSubs == null) AllSubs();
                if (_allSubs == null || _allSubs.Length == 0) return;
                PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
                System.Collections.Generic.List<int> list = new System.Collections.Generic.List<int>();
                DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
                if (pf.playerVesselList != null) // the mission's own subs first (no surface ships: not supported in duels)
                    foreach (int id in pf.playerVesselList)
                        if (id >= 0 && id < db.Length && db[id] != null && IsRealSub(db[id].shipPrefabName)) list.Add(id);
                foreach (int id in _allSubs) if (!list.Contains(id)) list.Add(id);
                pf.playerVesselList = list.ToArray();
                Duel.Log("~Sub choice: " + list.Count + " subs available in multiplayer");
            }
            catch (System.Exception ex) { Duel.Log("Could not widen the sub choice: " + ex.Message); }
        }

        /// <summary>single001 also lists player surface ships (..._pl) and a test ship, all typed SUBMARINE. Keep real
        /// submarines only: names like wp_84_ssn_alfa, knm_68_ss_kobben (third part ss, ssn, ssk, ssbn, ssgn ...).</summary>
        static bool IsRealSub(string name)
        {
            if (name == null || name.EndsWith("_pl")) return false;
            string[] p = name.Split('_');
            return p.Length >= 3 && p[2].StartsWith("ss");
        }

        /// <summary>Every selectable submarine (single001's PlayerVessels), for the sub choice and the ID book.</summary>
        public static int[] AllSubs()
        {
            if (_allSubs == null && UIFunctions.globaluifunctions != null) _allSubs = LoadAllSubs(UIFunctions.globaluifunctions.textparser);
            return _allSubs;
        }

        static int[] LoadAllSubs(TextParser tp)
        {
            string[] lines = tp.OpenTextDataFile("missions/single/single001");
            System.Collections.Generic.List<int> ids = new System.Collections.Generic.List<int>();
            foreach (string line in lines)
            {
                if (!line.StartsWith("PlayerVessels=")) continue;
                foreach (string name in line.Substring("PlayerVessels=".Length).Split(','))
                {
                    string n = name.Trim();
                    if (n.Length == 0) continue;
                    int id = tp.GetShipID(n);
                    if (id < 0 || id >= UIFunctions.globaluifunctions.database.databaseshipdata.Length) continue;
                    DatabaseShipData d = UIFunctions.globaluifunctions.database.databaseshipdata[id];
                    if (d != null && d.shipType == "SUBMARINE" && IsRealSub(d.shipPrefabName)) ids.Add(id);
                }
            }
            return ids.ToArray();
        }
    }
}
