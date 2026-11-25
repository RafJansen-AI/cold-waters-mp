// Cold Waters MP - BepInEx entry point, config and debug overlay. C# 5 / .NET 3.5.
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    [BepInPlugin("cwmp", "Cold Waters MP", PluginVersion)]
    public class CwmpPlugin : BaseUnityPlugin
    {
        public static string ModHash = "";
        public const string PluginVersion = "0.0.38";

        ConfigEntry<string> _role, _hostAddress, _playerName, _fakeHash;
        ConfigEntry<int> _port;
        ConfigEntry<bool> _autoStart, _devKeys;
        ConfigEntry<float> _rangeMin, _rangeMax;
        ConfigEntry<bool> _windowed;
        ConfigEntry<float> _teleportNm;
        Session _session;
        int _overlayMode; // 0 full, 1 compact, 2 off
        bool _showSettings;
        ConfigEntry<int> _overlayCfg;
        ConfigEntry<int> _cond, _weather, _sea, _layer, _duct, _time;
        ConfigEntry<bool> _pauseBoth, _allowTC;
        ConfigEntry<bool> _reducedVis;
        ConfigEntry<int> _traffic;
        Rect _settingsRect = new Rect(540, 10, 470, 520);
        readonly List<string> _logLines = new List<string>();

        void Awake()
        {
            _role = Config.Bind("Network", "Role", "Host", "Host or Client");
            _hostAddress = Config.Bind("Network", "HostAddress", "127.0.0.1", "Host IP or name (client only)");
            _port = Config.Bind("Network", "Port", 7777, "UDP port");
            _autoStart = Config.Bind("Network", "AutoStart", false, "Host/join automatically at startup with the last role (otherwise use the MULTIPLAYER button)");
            _playerName = Config.Bind("Player", "Name", "Player", "Shown to your opponent");
            _devKeys = Config.Bind("Debug", "DevKeys", false, "Testing only: Ctrl+F7 teleport, Ctrl+F11 test chat, full overlay and FakeDllHash. Off in normal play (the teleport is a cheat).");
            _fakeHash = Config.Bind("Debug", "FakeDllHash", "", "Testing only (needs DevKeys): pretend Assembly-CSharp.dll has this hash");
            _rangeMin = Config.Bind("Duel", "RangeMinNm", 4f, "Host: shortest starting distance (the actual one is random in the band)");
            _rangeMax = Config.Bind("Duel", "RangeMaxNm", 9f, "Host: longest starting distance");
            _teleportNm = Config.Bind("Debug", "TeleportNm", 1.5f, "Testing: F7 jumps your sub this far off the opponent's bow");
            _cond = Config.Bind("Match", "Conditions", 0, "Host: 0 = as the mission says, 1 = random (same for both), 2 = custom below");
            _weather = Config.Bind("Match", "Weather", 0, "Host, custom: weather index (0 = clear ...)");
            _sea = Config.Bind("Match", "SeaState", 2, "Host, custom: sea state index");
            _layer = Config.Bind("Match", "Layer", 0, "Host, custom: 0 = random, 1..6 = none .. strongest");
            _duct = Config.Bind("Match", "Duct", 0, "Host, custom: 0 = random, 1..6 = none .. strongest");
            _time = Config.Bind("Match", "Time", 0, "Host, custom: 0 random, 1 dawn, 2 day, 3 dusk, 4 night");
            _pauseBoth = Config.Bind("Match", "PauseBoth", false, "Host: true = the Esc menu pauses both games; false = no pausing");
            _allowTC = Config.Bind("Match", "TimeCompression", true, "Host: allow time compression when both players ask for it");
            _reducedVis = Config.Bind("Match", "ReducedVisibility", true, "Host: DotMod Reduced Visibility (denser underwater fog) for both players; false = clear water");
            _traffic = Config.Bind("Match", "NeutralTraffic", 1, "Host: neutral traffic (merchants, fishing boats, whales): 0 off, 1 as the map gives it, 2 extra");
            LoadMatchSettings();
            _overlayCfg = Config.Bind("Display", "Overlay", 1, "0 = full (testing), 1 = compact one-liner, 2 = off. Ctrl+F9 cycles.");
            Duel.RangeMinNm = _rangeMin.Value; Duel.RangeMaxNm = _rangeMax.Value;
            _overlayMode = Mathf.Clamp(_overlayCfg.Value, _devKeys.Value ? 0 : 1, 2);
            _windowed = Config.Bind("Display", "ForceWindowed1280x720", false, "Testing only: force a 1280x720 window. Don't change the window size afterwards: the game keeps the HUD layout of the size it first had.");
            // Keep simulating and networking while the window is not focused (two copies on one PC, Alt+Tab).
            Application.runInBackground = true;

            Dictionary<string, string> fp = new Dictionary<string, string>();
            string dll = Path.Combine(Paths.ManagedPath, "Assembly-CSharp.dll");
            fp["Assembly-CSharp.dll"] = (!_devKeys.Value || string.IsNullOrEmpty(_fakeHash.Value)) ? Fingerprint.HashFile(dll) : _fakeHash.Value;
            Log("Fingerprint Assembly-CSharp.dll = " + fp["Assembly-CSharp.dll"]);
            // The mod itself: two different builds (or a changed copy) are refused at connect.
            try { ModHash = Fingerprint.HashFile(typeof(CwmpPlugin).Assembly.Location); fp["ColdWatersMP.dll"] = ModHash; Log("ColdWatersMP.dll SHA-256 " + ModHash); }
            catch (System.Exception ex) { Log("Could not hash the mod DLL: " + ex.Message); }
            // Game data that changes combat: both players must have the same (edited weapons.txt etc. are refused).
            try
            {
                foreach (string root in Directory.GetDirectories(Application.streamingAssetsPath))
                {
                    string top = Path.GetFileName(root);
                    foreach (string sub in new string[] { "weapons", "vessels", "sensors", "aircraft", "environment" })
                        if (Directory.Exists(Path.Combine(root, sub))) fp[top + "/" + sub] = Fingerprint.HashFolder(Path.Combine(root, sub));
                    foreach (string file in new string[] { "config.txt", "subsystems.txt" })
                        if (File.Exists(Path.Combine(root, file))) fp[top + "/" + file] = Fingerprint.HashFile(Path.Combine(root, file));
                }
                Log("Fingerprinted " + (fp.Count - 1) + " game data sets");
            }
            catch (System.Exception ex) { Log("Game data fingerprint failed: " + ex.Message); }

            _session = new Session(new UdpTransport(), _playerName.Value, PluginVersion, fp, Log);
            Duel.Init(_session, Log, 6f);
            MenuTab.OpenWindow = delegate { _showSettings = true; };
            new Harmony("cwmp").PatchAll(typeof(CwmpPlugin).Assembly);
            Log("Harmony patches applied");
            if (_autoStart.Value) StartSession();
            Log("Cold Waters MP " + PluginVersion + " loaded. Ctrl+F5: tag contact as decoy, Ctrl+F6: multiplayer window, Ctrl+F8: start a held mission, Ctrl+F9: overlay, Ctrl+F12: reconnect" + (_devKeys.Value ? ". DEV KEYS ON: Ctrl+F7 teleport, Ctrl+F11 test chat" : ""));
        }

        void Start()
        {
            if (_windowed.Value) Screen.SetResolution(1280, 720, false);
        }

        void StartSession()
        {
            try
            {
                if (_role.Value.Trim().ToLowerInvariant() == "client") _session.Join(_hostAddress.Value, _port.Value);
                else _session.Host(_port.Value);
            }
            catch (System.Exception ex) { Log("Session start failed: " + ex.Message); }
        }

        void Update()
        {
            // All mod keys are Ctrl + F-key: the plain F-keys belong to the game (F6 signature, F7 stores, F8 damage,
            // F9 time compression, F10 hide HUD, F11 stop time compression).
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl)
            {
                if (Input.GetKeyDown(KeyCode.F9)) { _overlayMode = (_overlayMode + 1) % 3; if (!_devKeys.Value && _overlayMode == 0) _overlayMode = 1; _overlayCfg.Value = _overlayMode; }
                if (Input.GetKeyDown(KeyCode.F6)) _showSettings = !_showSettings;
                if (Input.GetKeyDown(KeyCode.F12)) { _session.Leave(); StartSession(); }
                if (_devKeys.Value && Input.GetKeyDown(KeyCode.F7)) Duel.DebugTeleportNearOpponent(_teleportNm.Value);
                if (Input.GetKeyDown(KeyCode.F5)) Ghosts.ToggleMarkCurrent(); // tag the selected contact as DECOY
                if (Input.GetKeyDown(KeyCode.F8)) { Duel.ReleaseHeldLoad("Ctrl+F8 pressed"); Duel.ReleaseStations("Ctrl+F8 pressed"); }
                if (_devKeys.Value && Input.GetKeyDown(KeyCode.F11)) _session.SendChat("ping from " + _playerName.Value + " at " + Time.realtimeSinceStartup.ToString("F1") + "s");
            }
            try { _session.Update(); }
            catch (System.Exception ex) { Log("Session error: " + ex); }
            try { Duel.Update(); }
            catch (System.Exception ex) { Log("Duel error: " + ex); }
            try { SnowFix(); } catch (System.Exception) { }
            try { Match.HoldVisibility(); } catch (System.Exception) { }
        }

        /// <summary>The game switches snow on in GetEnvironment, then CreateEnvironment switches it off again and
        /// nothing turns it back on (rain has that code, snow doesn't). Show it above water with the map closed.</summary>
        static void SnowFix()
        {
            if (UIFunctions.globaluifunctions == null) return;
            LevelLoadManager llm = UIFunctions.globaluifunctions.levelloadmanager;
            if (llm == null || llm.snow == null) return;
            bool want = GameDataManager.HUDActive && LevelLoadManager.isSnowing && !ManualCameraZoom.underwater && !llm.tacticalmap.background.activeSelf;
            if (llm.snow.activeSelf != want) llm.snow.SetActive(want);
        }

        void OnDestroy() { if (_session != null) _session.Leave(); }

        void LoadMatchSettings()
        {
            Match.Conditions = (Match.CondMode)Mathf.Clamp(_cond.Value, 0, 2);
            Match.Weather = _weather.Value; Match.SeaState = _sea.Value; Match.Layer = _layer.Value;
            Match.Duct = _duct.Value; Match.TimeChoice = _time.Value;
            Match.HostPauseBoth = _pauseBoth.Value; Match.HostAllowTC = _allowTC.Value; Match.HostReducedVisibility = _reducedVis.Value;
            Ambient.HostLevel = (Ambient.Level)Mathf.Clamp(_traffic.Value, 0, 2);
        }

        void SaveMatchSettings()
        {
            _cond.Value = (int)Match.Conditions; _weather.Value = Match.Weather; _sea.Value = Match.SeaState;
            _layer.Value = Match.Layer; _duct.Value = Match.Duct; _time.Value = Match.TimeChoice;
            _rangeMin.Value = Duel.RangeMinNm; _rangeMax.Value = Duel.RangeMaxNm;
            _pauseBoth.Value = Match.HostPauseBoth; _allowTC.Value = Match.HostAllowTC; _reducedVis.Value = Match.HostReducedVisibility;
            _traffic.Value = (int)Ambient.HostLevel;
        }

        /// <summary>One "&lt; value &gt;" row; returns the new index.</summary>
        static int Stepper(string label, int value, int count, string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(110));
            if (GUILayout.Button("<", GUILayout.Width(28))) value = (value - 1 + count) % count;
            GUILayout.Label(text, GUILayout.Width(200));
            if (GUILayout.Button(">", GUILayout.Width(28))) value = (value + 1) % count;
            GUILayout.EndHorizontal();
            return value;
        }

        static string _ownAddresses;
        /// <summary>This PC's IPv4 addresses (LAN and VPN adapters), looked up once.</summary>
        static string OwnAddresses()
        {
            if (_ownAddresses != null) return _ownAddresses;
            List<string> list = new List<string>();
            try
            {
                foreach (System.Net.IPAddress a in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a)) list.Add(a.ToString());
            }
            catch (System.Exception) { }
            _ownAddresses = list.Count > 0 ? string.Join(", ", list.ToArray()) : "unknown (see your VPN tool)";
            return _ownAddresses;
        }

        /// <summary>Connection part of the multiplayer window: role, address, name, connect.</summary>
        void ConnectionSection()
        {
            bool connected = _session.State == SessionState.Ready;
            GUILayout.Label("Mod " + PluginVersion + (ModHash.Length >= 16 ? ", DLL SHA-256 " + ModHash.Substring(0, 16) + "... (compare with the release page)" : ""));
            GUILayout.Label("Status: " + _session.Status + (_session.Transport.IsConnected ? "  (" + _session.Transport.RttMs.ToString("F0") + " ms)" : ""));
            bool host = _role.Value.Trim().ToLowerInvariant() != "client";
            GUILayout.BeginHorizontal();
            GUILayout.Label("I am the", GUILayout.Width(110));
            if (GUILayout.Button(host ? "[ Host ]" : "Host", GUILayout.Width(100)) && !host) { _role.Value = "Host"; host = true; }
            if (GUILayout.Button(!host ? "[ Client ]" : "Client", GUILayout.Width(100)) && host) { _role.Value = "Client"; host = false; }
            GUILayout.EndHorizontal();
            if (!host)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Host address", GUILayout.Width(110));
                _hostAddress.Value = GUILayout.TextField(_hostAddress.Value, 64, GUILayout.Width(200));
                GUILayout.EndHorizontal();
                GUILayout.Label("The host's LAN or VPN address (Tailscale, ZeroTier, Radmin VPN ...). The host sees it in this window.");
            }
            else GUILayout.Label("Your addresses (give one to the client): " + OwnAddresses());
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(connected ? "Reconnect" : (host ? "Start hosting" : "Join"), GUILayout.Width(150))) { _session.Leave(); StartSession(); }
            if (GUILayout.Button("Close", GUILayout.Width(100))) _showSettings = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("To play: both connect here, then Single Mission. Pick any mission and your sub; the host's map, conditions and difficulty are used.");
            GUILayout.Space(6);
        }

        void SettingsWindow(int id)
        {
            ConnectionSection();
            if (!_session.IsHost)
            {
                GUILayout.Label("The host picks the map and conditions.");
                GUILayout.Label(Match.HaveHostMatch ? "Host's match: " + Match.LastSummary : "Nothing from the host yet: it arrives when the host starts its mission.");
                GUILayout.Label("Pick any mission and your sub as usual; the host's map replaces your mission's.");
                if (Match.HaveHostMatch) GUILayout.Label("Pause: " + (Match.PauseBoth ? "Esc pauses both" : "blocked") + ". Time compression: " + (Match.TimeCompressionAllowed ? "when both ask" : "off") + ". Reduced Visibility: " + (Match.ReducedVisibility ? "on" : "off") + ".");
                GUI.DragWindow();
                return;
            }
            GUILayout.Label("Map: the single mission you start (its ships are removed).");
            int before = (int)Match.Conditions * 1000 + Match.Weather * 100 + Match.SeaState * 10 + Match.Layer + Match.Duct * 7 + Match.TimeChoice * 13;
            float rangeBefore = Duel.RangeMinNm * 100f + Duel.RangeMaxNm;
            string[] modes = { "As the mission says", "Random (same for both)", "Custom" };
            Match.Conditions = (Match.CondMode)Stepper("Conditions", (int)Match.Conditions, 3, modes[(int)Match.Conditions]);
            if (Match.Conditions == Match.CondMode.Custom)
            {
                SensorManager sm = UIFunctions.globaluifunctions != null ? UIFunctions.globaluifunctions.playerfunctions.sensormanager : null;
                string[] weather = sm != null ? sm.weather : new string[] { "0", "1", "2", "3", "4", "5", "6", "7" };
                string[] seas = sm != null ? sm.seaStates : new string[] { "0", "1", "2", "3", "4", "5", "6" };
                string[] str = sm != null ? sm.strengthTypes : new string[] { "0", "1", "2", "3", "4", "5" };
                Match.Weather = Stepper("Weather", Mathf.Clamp(Match.Weather, 0, weather.Length - 1), weather.Length, Match.Name(weather, Match.Weather));
                Match.SeaState = Stepper("Sea state", Mathf.Clamp(Match.SeaState, 0, seas.Length - 1), seas.Length, Match.Name(seas, Match.SeaState));
                Match.Layer = Stepper("Layer", Mathf.Clamp(Match.Layer, 0, str.Length), str.Length + 1, Match.Layer == 0 ? "Random" : Match.Name(str, Match.Layer - 1));
                Match.Duct = Stepper("Surface duct", Mathf.Clamp(Match.Duct, 0, str.Length), str.Length + 1, Match.Duct == 0 ? "Random" : Match.Name(str, Match.Duct - 1));
                Match.TimeChoice = Stepper("Time", Mathf.Clamp(Match.TimeChoice, 0, 4), 5, Match.TimeNames[Mathf.Clamp(Match.TimeChoice, 0, 4)]);
            }
            // Start range band, in the game's units (kyd, or km with the metric option); stored in nm. The actual
            // range is random inside the band and not shown to anyone.
            bool metric = AddFields.metricSystem;
            float perNm = metric ? 1.852f : 2.0254f; string unit = metric ? " km" : " kyd";
            int lo = System.Math.Max(1, Mathf.RoundToInt(Duel.RangeMinNm * perNm)), hi = System.Math.Max(1, Mathf.RoundToInt(Duel.RangeMaxNm * perNm));
            int nlo = Stepper("Range from", lo - 1, 40, lo + unit) + 1;
            int nhi = Stepper("Range to", hi - 1, 50, hi + unit) + 1;
            if (nlo != lo) { Duel.RangeMinNm = nlo / perNm; if (Duel.RangeMaxNm < Duel.RangeMinNm) Duel.RangeMaxNm = Duel.RangeMinNm; }
            if (nhi != hi) { Duel.RangeMaxNm = nhi / perNm; if (Duel.RangeMinNm > Duel.RangeMaxNm) Duel.RangeMinNm = Duel.RangeMaxNm; }
            bool pb = Match.HostPauseBoth, tc = Match.HostAllowTC, rv = Match.HostReducedVisibility; Ambient.Level tl = Ambient.HostLevel;
            Match.HostPauseBoth = Stepper("Pause", Match.HostPauseBoth ? 1 : 0, 2, Match.HostPauseBoth ? "Esc pauses both" : "Blocked") == 1;
            Match.HostAllowTC = Stepper("Time compr.", Match.HostAllowTC ? 1 : 0, 2, Match.HostAllowTC ? "When both ask" : "Off") == 1;
            Match.HostReducedVisibility = Stepper("Visibility", Match.HostReducedVisibility ? 1 : 0, 2, Match.HostReducedVisibility ? "Reduced Visibility on" : "Reduced Visibility off") == 1;
            Ambient.HostLevel = (Ambient.Level)Stepper("Traffic", (int)Ambient.HostLevel, 3, Ambient.HostLevel == Ambient.Level.Off ? "No neutral traffic" : Ambient.HostLevel == Ambient.Level.Map ? "Traffic as the map gives it" : "Extra traffic");
            if (pb != Match.HostPauseBoth || tc != Match.HostAllowTC || rv != Match.HostReducedVisibility || tl != Ambient.HostLevel) rangeBefore = -1f; // force a save
            GUILayout.Label("Difficulty: yours (set it in the game options); the client uses it too.");
            GUILayout.Label("Settings apply from the next mission start.");
            if (Match.LastSummary.Length > 0) GUILayout.Label("Last match: " + Match.LastSummary);
            int after = (int)Match.Conditions * 1000 + Match.Weather * 100 + Match.SeaState * 10 + Match.Layer + Match.Duct * 7 + Match.TimeChoice * 13;
            if (after != before || rangeBefore != Duel.RangeMinNm * 100f + Duel.RangeMaxNm) SaveMatchSettings();
            GUI.DragWindow();
        }

        void OnGUI()
        {
            if (_showSettings) _settingsRect = GUILayout.Window(0xC3A1, _settingsRect, SettingsWindow, "Cold Waters Multiplayer (Ctrl+F6)");
            if (_overlayMode == 2) return;
            if (_overlayMode == 1)
            {
                string c = _session.Transport.IsConnected ? "MP " + (_session.IsHost ? "host" : "client") + " " + _session.Transport.RttMs.ToString("F0") + " ms" : "MP: " + _session.Status;
                if (Duel.WaitingForOpponentChoice) c += " | waiting (Ctrl+F8 starts)";
                string t = TimeSync.StatusLine; if (t.Length > 0) c += " | " + t;
                if (Duel.Result.Length > 0) c += " | " + Duel.Result;
                GUI.Label(new Rect(10, 4, 900, 22), c);
                return;
            }
            GUILayout.BeginArea(new Rect(10, 10, 520, 260), GUI.skin.box);
            GUILayout.Label("Cold Waters MP " + PluginVersion + "  [" + (_session.IsHost ? "HOST" : "CLIENT") + "]");
            GUILayout.Label(_session.Status);
            if (_session.Transport.IsConnected) GUILayout.Label("RTT: " + _session.Transport.RttMs.ToString("F1") + " ms");
            string duel = Duel.StatusLine;
            if (duel.Length > 0) GUILayout.Label(duel);
            string ts = TimeSync.StatusLine;
            if (ts.Length > 0) GUILayout.Label(ts);
            for (int i = System.Math.Max(0, _logLines.Count - 6); i < _logLines.Count; i++) GUILayout.Label(_logLines[i]);
            GUILayout.EndArea();
        }

        void Log(string msg)
        {
            bool quiet = msg.StartsWith("~"); // file only: things players must not see (exact range, sonar readings)
            if (quiet) msg = msg.Substring(1);
            msg = System.DateTime.Now.ToString("HH:mm:ss.f") + " " + msg;
            Logger.LogInfo("CWMP " + msg);
            if (quiet) return;
            _logLines.Add(msg);
            if (_logLines.Count > 50) _logLines.RemoveAt(0);
        }
    }
}
