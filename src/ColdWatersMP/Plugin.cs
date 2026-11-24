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
        public const string PluginVersion = "0.0.16";

        ConfigEntry<string> _role, _hostAddress, _playerName, _fakeHash;
        ConfigEntry<int> _port;
        ConfigEntry<bool> _autoStart;
        ConfigEntry<float> _rangeNm;
        ConfigEntry<bool> _windowed;
        ConfigEntry<float> _teleportNm;
        Session _session;
        bool _showOverlay = true;
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
            Log("Cold Waters MP " + PluginVersion + " loaded. F7: debug teleport near opponent, F8: start held mission, F9: toggle overlay, F10: (re)start session, F11: send test chat");
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
            if (Input.GetKeyDown(KeyCode.F10)) { _session.Leave(); StartSession(); }
            if (Input.GetKeyDown(KeyCode.F7)) Duel.DebugTeleportNearOpponent(_teleportNm.Value);
            if (Input.GetKeyDown(KeyCode.F8)) Duel.ReleaseHeldLoad("F8 pressed");
            if (Input.GetKeyDown(KeyCode.F11)) _session.SendChat("ping from " + _playerName.Value + " at " + Time.realtimeSinceStartup.ToString("F1") + "s");
            try { _session.Update(); }
            catch (System.Exception ex) { Log("Session error: " + ex); }
            try { Duel.Update(); }
            catch (System.Exception ex) { Log("Duel error: " + ex); }
        }

        void OnDestroy() { if (_session != null) _session.Leave(); }

        void OnGUI()
        {
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
