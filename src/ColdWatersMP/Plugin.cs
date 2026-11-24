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
        public const string PluginVersion = "0.0.19";

        ConfigEntry<string> _role, _hostAddress, _playerName, _fakeHash;
        ConfigEntry<int> _port;
        ConfigEntry<bool> _autoStart;
        ConfigEntry<float> _rangeNm;
        ConfigEntry<bool> _windowed;
        ConfigEntry<float> _teleportNm;
        Session _session;
        bool _showOverlay = true, _showSettings;
        ConfigEntry<int> _cond, _weather, _sea, _layer, _duct, _time;
        Rect _settingsRect = new Rect(540, 10, 420, 330);
        readonly List<string> _logLines = new List<string>();

        void Awake()
        {
            _role = Config.Bind("Network", "Role", "Host", "Host or Client");
            _hostAddress = Config.Bind("Network", "HostAddress", "127.0.0.1", "Host IP or name (client only)");
            _port = Config.Bind("Network", "Port", 7777, "UDP port");
            _autoStart = Config.Bind("Network", "AutoStart", true, "Host/join automatically at startup");
            _playerName = Config.Bind("Player", "Name", System.Environment.UserName, "Shown to your opponent");
            _fakeHash = Config.Bind("Debug", "FakeDllHash", "", "Testing only: pretend Assembly-CSharp.dll has this hash");
            _rangeNm = Config.Bind("Duel", "RangeNm", 6f, "Starting distance between the subs (host decides)");
            _teleportNm = Config.Bind("Debug", "TeleportNm", 1.5f, "Testing: F7 jumps your sub this far off the opponent's bow");
            _cond = Config.Bind("Match", "Conditions", 0, "Host: 0 = as the mission says, 1 = random (same for both), 2 = custom below");
            _weather = Config.Bind("Match", "Weather", 0, "Host, custom: weather index (0 = clear ...)");
            _sea = Config.Bind("Match", "SeaState", 2, "Host, custom: sea state index");
            _layer = Config.Bind("Match", "Layer", 0, "Host, custom: 0 = random, 1..6 = none .. strongest");
            _duct = Config.Bind("Match", "Duct", 0, "Host, custom: 0 = random, 1..6 = none .. strongest");
            _time = Config.Bind("Match", "Time", 0, "Host, custom: 0 random, 1 dawn, 2 day, 3 dusk, 4 night");
            LoadMatchSettings();
            _windowed = Config.Bind("Display", "Windowed1280x720", false, "Testing: force a 1280x720 window");
            // Keep simulating and networking while the window is not focused (two copies on one PC, Alt+Tab).
            Application.runInBackground = true;

            Dictionary<string, string> fp = new Dictionary<string, string>();
            string dll = Path.Combine(Paths.ManagedPath, "Assembly-CSharp.dll");
            fp["Assembly-CSharp.dll"] = string.IsNullOrEmpty(_fakeHash.Value) ? Fingerprint.HashFile(dll) : _fakeHash.Value;
            Log("Fingerprint Assembly-CSharp.dll = " + fp["Assembly-CSharp.dll"]);

            _session = new Session(new UdpTransport(), _playerName.Value, PluginVersion, fp, Log);
            Duel.Init(_session, Log, _rangeNm.Value);
            new Harmony("cwmp").PatchAll(typeof(CwmpPlugin).Assembly);
            Log("Harmony patches applied");
            if (_autoStart.Value) StartSession();
            Log("Cold Waters MP " + PluginVersion + " loaded. F6: match settings, F7: debug teleport near opponent, F8: start held mission, F9: toggle overlay, F10: (re)start session, F11: send test chat");
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
            if (Input.GetKeyDown(KeyCode.F9)) _showOverlay = !_showOverlay;
            if (Input.GetKeyDown(KeyCode.F6)) _showSettings = !_showSettings;
            if (Input.GetKeyDown(KeyCode.F10)) { _session.Leave(); StartSession(); }
            if (Input.GetKeyDown(KeyCode.F7)) Duel.DebugTeleportNearOpponent(_teleportNm.Value);
            if (Input.GetKeyDown(KeyCode.F8)) { Duel.ReleaseHeldLoad("F8 pressed"); Duel.ReleaseStations("F8 pressed"); }
            if (Input.GetKeyDown(KeyCode.F11)) _session.SendChat("ping from " + _playerName.Value + " at " + Time.realtimeSinceStartup.ToString("F1") + "s");
            try { _session.Update(); }
            catch (System.Exception ex) { Log("Session error: " + ex); }
            try { Duel.Update(); }
            catch (System.Exception ex) { Log("Duel error: " + ex); }
        }

        void OnDestroy() { if (_session != null) _session.Leave(); }

        void LoadMatchSettings()
        {
            Match.Conditions = (Match.CondMode)Mathf.Clamp(_cond.Value, 0, 2);
            Match.Weather = _weather.Value; Match.SeaState = _sea.Value; Match.Layer = _layer.Value;
            Match.Duct = _duct.Value; Match.TimeChoice = _time.Value;
        }

        void SaveMatchSettings()
        {
            _cond.Value = (int)Match.Conditions; _weather.Value = Match.Weather; _sea.Value = Match.SeaState;
            _layer.Value = Match.Layer; _duct.Value = Match.Duct; _time.Value = Match.TimeChoice;
            _rangeNm.Value = Duel.RangeNm;
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

        void SettingsWindow(int id)
        {
            if (!_session.IsHost)
            {
                GUILayout.Label("The host picks the map and conditions.");
                GUILayout.Label(Match.HaveHostMatch ? "Host's match: " + Match.LastSummary : "Nothing from the host yet: it arrives when the host starts its mission.");
                GUILayout.Label("Pick any mission and your sub as usual; the host's map replaces your mission's.");
                GUI.DragWindow();
                return;
            }
            GUILayout.Label("Map: the single mission you start (its ships are removed).");
            int before = (int)Match.Conditions * 1000 + Match.Weather * 100 + Match.SeaState * 10 + Match.Layer + Match.Duct * 7 + Match.TimeChoice * 13;
            float rangeBefore = Duel.RangeNm;
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
            int r = Mathf.RoundToInt(Duel.RangeNm * 2f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Start range", GUILayout.Width(110));
            if (GUILayout.Button("<", GUILayout.Width(28)) && r > 1) r--;
            GUILayout.Label((r / 2f).ToString("0.0") + " nm", GUILayout.Width(200));
            if (GUILayout.Button(">", GUILayout.Width(28)) && r < 60) r++;
            GUILayout.EndHorizontal();
            Duel.RangeNm = r / 2f;
            GUILayout.Label("Neutral traffic: off (not synced yet). Pause: blocked.");
            if (Match.LastSummary.Length > 0) GUILayout.Label("Last match: " + Match.LastSummary);
            int after = (int)Match.Conditions * 1000 + Match.Weather * 100 + Match.SeaState * 10 + Match.Layer + Match.Duct * 7 + Match.TimeChoice * 13;
            if (after != before || rangeBefore != Duel.RangeNm) SaveMatchSettings();
            GUI.DragWindow();
        }

        void OnGUI()
        {
            if (_showSettings) _settingsRect = GUILayout.Window(0xC3A1, _settingsRect, SettingsWindow, "Multiplayer match settings (F6)");
            if (!_showOverlay) return;
            GUILayout.BeginArea(new Rect(10, 10, 520, 260), GUI.skin.box);
            GUILayout.Label("Cold Waters MP " + PluginVersion + "  [" + (_session.IsHost ? "HOST" : "CLIENT") + "]");
            GUILayout.Label(_session.Status);
            if (_session.Transport.IsConnected) GUILayout.Label("RTT: " + _session.Transport.RttMs.ToString("F1") + " ms");
            string duel = Duel.StatusLine;
            if (duel.Length > 0) GUILayout.Label(duel);
            for (int i = System.Math.Max(0, _logLines.Count - 6); i < _logLines.Count; i++) GUILayout.Label(_logLines[i]);
            GUILayout.EndArea();
        }

        void Log(string msg)
        {
            msg = System.DateTime.Now.ToString("HH:mm:ss.f") + " " + msg;
            Logger.LogInfo("CWMP " + msg);
            _logLines.Add(msg);
            if (_logLines.Count > 50) _logLines.RemoveAt(0);
        }
    }
}
