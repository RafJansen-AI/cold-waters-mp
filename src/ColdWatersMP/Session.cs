// Cold Waters MP - session: hello/fingerprint handshake on top of ITransport. C# 5 / .NET 3.5.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ColdWatersMP
{
    public enum MsgType : byte { Hello = 1, Welcome = 2, Reject = 3, Chat = 4, Game = 10 }

    public enum SessionState { Idle, Listening, Connecting, Handshaking, Ready, Rejected }

    public static class Fingerprint
    {
        public const int ProtocolVersion = 1;

        /// <summary>SHA-256 (hex) of a file, or "missing".</summary>
        public static string HashFile(string path)
        {
            if (!File.Exists(path)) return "missing";
            using (FileStream fs = File.OpenRead(path))
            using (SHA256 sha = new SHA256Managed())
                return ToHex(sha.ComputeHash(fs));
        }

        /// <summary>SHA-256 over every file in a folder (sorted relative paths + contents), or "missing".</summary>
        public static string HashFolder(string dir)
        {
            if (!Directory.Exists(dir)) return "missing";
            string[] files = Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories); // the data is text; skip big binaries
            System.Array.Sort(files, System.StringComparer.Ordinal);
            using (SHA256 sha = new SHA256Managed())
            {
                byte[] buf = new byte[65536];
                foreach (string f in files)
                {
                    byte[] name = Encoding.UTF8.GetBytes(f.Substring(dir.Length).Replace('\\', '/').ToLowerInvariant());
                    sha.TransformBlock(name, 0, name.Length, null, 0);
                    using (FileStream fs = File.OpenRead(f))
                    {
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return ToHex(sha.Hash);
            }
        }

        public static string ToHex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    public class Session
    {
        public readonly ITransport Transport;
        public SessionState State { get; private set; }
        public bool IsHost { get; private set; }
        public string Status { get; private set; }
        public string PeerName { get; private set; }

        readonly string _localName;
        readonly string _pluginVersion;
        readonly Dictionary<string, string> _fingerprint; // name -> hash
        readonly List<NetEvent> _events = new List<NetEvent>();
        readonly Action<string> _log;

        /// <summary>Gameplay messages (sub-type byte + body). Raised on the main thread.</summary>
        public event Action<byte, BinaryReader> GameMessage;
        /// <summary>Raised when the session becomes Ready (true) or stops being Ready (false).</summary>
        public event Action<bool> ReadyChanged;

        public Session(ITransport transport, string localName, string pluginVersion,
                       Dictionary<string, string> fingerprint, Action<string> log)
        {
            Transport = transport; _localName = localName; _pluginVersion = pluginVersion;
            _fingerprint = fingerprint; _log = log;
            State = SessionState.Idle; Status = "Idle";
        }

        public void Host(int port)
        {
            IsHost = true;
            Transport.StartHost(port);
            State = SessionState.Listening; Status = "Hosting on UDP " + port + ", waiting for opponent";
            _log(Status);
        }

        public void Join(string host, int port)
        {
            IsHost = false;
            Transport.StartClient(host, port);
            State = SessionState.Connecting; Status = "Connecting to " + host + ":" + port;
            _log(Status);
        }

        public void Leave()
        {
            if (State == SessionState.Ready) SetReady(false);
            Transport.Stop();
            State = SessionState.Idle; Status = "Idle"; PeerName = null;
        }

        public void Update()
        {
            _events.Clear();
            Transport.Poll(_events);
            foreach (NetEvent e in _events)
            {
                try { HandleEvent(e); }
                catch (Exception ex) { _log("Error handling a network event: " + ex); } // one bad message must not drop the rest
            }
        }

        void HandleEvent(NetEvent e)
        {
            {
                switch (e.Type)
                {
                    case NetEventType.Connected:
                        State = SessionState.Handshaking; Status = "Connected, checking builds";
                        _log("Transport connected");
                        if (!IsHost) SendHello();
                        break;
                    case NetEventType.Disconnected:
                        _log("Disconnected: " + e.Reason);
                        PeerName = null;
                        if (State == SessionState.Ready) SetReady(false);
                        if (State == SessionState.Rejected) break; // keep the rejection reason visible
                        State = IsHost ? SessionState.Listening : SessionState.Idle;
                        Status = "Disconnected (" + e.Reason + ")" + (IsHost ? ", waiting for opponent" : "");
                        break;
                    case NetEventType.Data:
                        try { Handle(e.Payload); }
                        catch (System.Exception ex) { _log("Ignored a malformed message from the other player (" + ex.GetType().Name + ")"); }
                        break;
                }
            }
        }

        public void SendGame(byte subType, Action<BinaryWriter> body, bool reliable)
        {
            if (State != SessionState.Ready) return;
            Transport.Send(Build(MsgType.Game, w => { w.Write(subType); body(w); }), reliable);
        }

        void SetReady(bool ready)
        {
            if (ReadyChanged != null) ReadyChanged(ready);
        }

        public void SendChat(string text)
        {
            if (State != SessionState.Ready) return;
            Transport.Send(Build(MsgType.Chat, w => w.Write(text)), true);
        }

        void SendHello()
        {
            Transport.Send(Build(MsgType.Hello, w =>
            {
                w.Write(Fingerprint.ProtocolVersion);
                w.Write(_pluginVersion);
                w.Write(_localName);
                w.Write(_fingerprint.Count);
                foreach (KeyValuePair<string, string> kv in _fingerprint) { w.Write(kv.Key); w.Write(kv.Value); }
            }), true);
        }

        void Handle(byte[] payload)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(payload)))
            {
                MsgType t = (MsgType)r.ReadByte();
                switch (t)
                {
                    case MsgType.Hello:
                    {
                        if (!IsHost) return;
                        int proto = r.ReadInt32();
                        string ver = r.ReadString();
                        string name = r.ReadString();
                        int n = r.ReadInt32();
                        if (n < 0 || n > 256) return; // malformed
                        Dictionary<string, string> theirs = new Dictionary<string, string>();
                        for (int i = 0; i < n; i++) theirs[r.ReadString()] = r.ReadString();
                        string problem = Compare(proto, ver, theirs);
                        if (problem != null)
                        {
                            _log("Rejecting " + name + ": " + problem);
                            Transport.Send(Build(MsgType.Reject, w => w.Write(problem)), true);
                            Status = "Rejected " + name + ": " + problem;
                            // Host stays listening; client disconnects after reading the reason.
                            return;
                        }
                        PeerName = name;
                        Transport.Send(Build(MsgType.Welcome, w => w.Write(_localName)), true);
                        State = SessionState.Ready; Status = "Ready: " + name + " joined";
                        _log(Status);
                        SetReady(true);
                        break;
                    }
                    case MsgType.Welcome:
                        PeerName = r.ReadString();
                        State = SessionState.Ready; Status = "Ready: joined " + PeerName;
                        _log(Status);
                        SetReady(true);
                        break;
                    case MsgType.Reject:
                    {
                        string reason = r.ReadString();
                        State = SessionState.Rejected; Status = "Rejected by host: " + reason;
                        _log(Status);
                        Transport.Stop();
                        break;
                    }
                    case MsgType.Chat:
                        { string c = r.ReadString(); if (c.Length > 200) c = c.Substring(0, 200); _log("[" + PeerName + "] " + c); }
                        break;
                    case MsgType.Game:
                        if (State == SessionState.Ready && GameMessage != null) GameMessage(r.ReadByte(), r);
                        break;
                }
            }
        }

        string Compare(int proto, string ver, Dictionary<string, string> theirs)
        {
            if (proto != Fingerprint.ProtocolVersion)
                return "protocol " + proto + " vs host " + Fingerprint.ProtocolVersion;
            if (ver != _pluginVersion) return "mod version " + ver + " vs host " + _pluginVersion + " (both players need the same version from the GitHub Releases page)";
            List<string> diffs = new List<string>();
            foreach (KeyValuePair<string, string> kv in _fingerprint)
            {
                string other;
                if (!theirs.TryGetValue(kv.Key, out other)) diffs.Add(kv.Key + " missing");
                else if (other != kv.Value) diffs.Add(kv.Key + " differs");
            }
            foreach (string k in theirs.Keys)
                if (!_fingerprint.ContainsKey(k)) diffs.Add(k + " only on the client");
            if (diffs.Count == 0) return null;
            string hint = diffs.Exists(d => d.StartsWith("ColdWatersMP.dll")) ? " - the mod DLLs differ: both download the same release from GitHub"
                        : diffs.Exists(d => d.StartsWith("Assembly-CSharp")) ? " - the game or DotMod differs: same game version and DotMod v0.4c on both"
                        : " - game data differs: put back edited files or reinstall DotMod on one side";
            return "install mismatch: " + string.Join(", ", diffs.ToArray()) + hint;
        }

        static byte[] Build(MsgType t, Action<BinaryWriter> body)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((byte)t);
                body(w);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
