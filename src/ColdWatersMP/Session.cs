// Cold Waters MP - session: hello/fingerprint handshake, players and teams on top of ITransport. C# 5 / .NET 3.5.
// The host is a hub: each client connects to it alone, the host checks each client's build, gives it a player
// number (host 0, clients 1..7) and relays every game message to the other players with the sender's number.
// The host also keeps the player list (name, team) and sends it to everyone when it changes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ColdWatersMP
{
    public enum MsgType : byte { Hello = 1, Welcome = 2, Reject = 3, Chat = 4, Roster = 5, TeamPick = 6, Game = 10, GameFrom = 11 }

    public enum SessionState { Idle, Listening, Connecting, Handshaking, Ready, Rejected }

    public class PlayerInfo
    {
        public int Id;          // host 0, clients 1..7
        public string Name;
        public int Team;        // 0 = A, 1 = B
        public float RttMs;     // as the host measures it (0 for the host)
    }

    public static class Fingerprint
    {
        public const int ProtocolVersion = 2; // 2: player numbers, roster and relayed game messages (team battles)
        /// <summary>This install's files that differ from DotMod v0.4c (sent with Hello so a refusal can name them).</summary>
        public static List<string> LocalDeviations = new List<string>();

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
        public const int MaxPlayers = 8, MaxPerTeam = 4;

        public readonly ITransport Transport;
        public SessionState State { get; private set; }
        public bool IsHost { get; private set; }
        public string Status { get; private set; }
        /// <summary>1v1: the other player's name. With more players: the host's name (client) or the first client's (host).</summary>
        public string PeerName { get; private set; }
        /// <summary>Our player number: host 0, clients as the host assigned them (-1 before that).</summary>
        public int MyId { get; private set; }
        /// <summary>Everyone in the session including us, in player-number order. Kept by the host, copied to clients.</summary>
        public readonly List<PlayerInfo> Players = new List<PlayerInfo>();
        public int PlayerCount { get { return Players.Count; } }
        /// <summary>The player number of the sender of the game message being handled (inside GameMessage).</summary>
        public int LastSender { get; private set; }

        readonly string _localName;
        readonly string _pluginVersion;
        readonly Dictionary<string, string> _fingerprint; // name -> hash
        /// <summary>Session password ("" = none). Only a hash is sent; the host refuses a client whose hash differs.</summary>
        public string Password = "";

        static string PasswordHash(string pw)
        {
            if (string.IsNullOrEmpty(pw)) return "";
            using (SHA256 sha = new SHA256Managed())
                return Fingerprint.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes("cwmp-session:" + pw))).Substring(0, 16);
        }
        readonly List<NetEvent> _events = new List<NetEvent>();
        readonly Action<string> _log;

        /// <summary>Gameplay messages (sub-type byte + body) from any other player; LastSender says who. Main thread.</summary>
        public event Action<byte, BinaryReader> GameMessage;
        /// <summary>True when we first have another player to play with, false when the last one is gone.</summary>
        public event Action<bool> ReadyChanged;
        /// <summary>The player list changed (joins, leaves, team changes).</summary>
        public event Action RosterChanged;

        // Host: per client connection
        class Client
        {
            public int Peer;
            public int PlayerId = -1;           // -1 until the handshake passed
            public string Name;
            public int HandshakeSince;
            public int KickAt; public string KickWhy;
            public List<string> Dev = new List<string>(); public int DevCount;
        }
        readonly Dictionary<int, Client> _clients = new Dictionary<int, Client>();

        public Session(ITransport transport, string localName, string pluginVersion,
                       Dictionary<string, string> fingerprint, Action<string> log)
        {
            Transport = transport; _localName = localName; _pluginVersion = pluginVersion;
            _fingerprint = fingerprint; _log = log;
            State = SessionState.Idle; Status = "Idle"; MyId = -1;
        }

        public void Host(int port)
        {
            IsHost = true;
            Transport.StartHost(port);
            _clients.Clear();
            Players.Clear();
            MyId = 0;
            Players.Add(new PlayerInfo { Id = 0, Name = _localName, Team = 0 });
            State = SessionState.Listening; Status = "Hosting on UDP " + port + ", waiting for players";
            _log(Status);
            FireRoster();
        }

        public void Join(string host, int port)
        {
            IsHost = false;
            Players.Clear(); MyId = -1;
            Transport.StartClient(host, port);
            State = SessionState.Connecting; Status = "Connecting to " + host + ":" + port;
            _log(Status);
        }

        public void Leave()
        {
            bool wasReady = State == SessionState.Ready;
            Transport.Stop();
            _clients.Clear(); Players.Clear(); MyId = -1;
            State = SessionState.Idle; Status = "Not connected"; PeerName = null;
            if (wasReady) SetReady(false);
            FireRoster();
        }

        public void Update()
        {
            int nowMs = System.Environment.TickCount;
            if (IsHost)
            {
                foreach (Client c in new List<Client>(_clients.Values))
                {
                    if (c.KickWhy != null && nowMs - c.KickAt >= 0) { Transport.Kick(c.Peer, c.KickWhy); c.KickWhy = null; }
                    else if (c.PlayerId < 0 && c.KickWhy == null && nowMs - c.HandshakeSince > 5000)
                    { _log("No valid hello within 5 s: dropping that connection"); Transport.Kick(c.Peer, "no hello"); c.HandshakeSince = nowMs; }
                }
                foreach (PlayerInfo p in Players) { Client c = ClientOf(p.Id); p.RttMs = c != null ? Transport.Rtt(c.Peer) : 0f; }
            }
            _events.Clear();
            Transport.Poll(_events);
            foreach (NetEvent e in _events)
            {
                try { HandleEvent(e); }
                catch (Exception ex) { _log("Error handling a network event: " + ex); } // one bad message must not drop the rest
            }
        }

        Client ClientOf(int playerId)
        {
            foreach (Client c in _clients.Values) if (c.PlayerId == playerId) return c;
            return null;
        }

        void HandleEvent(NetEvent e)
        {
            switch (e.Type)
            {
                case NetEventType.Connected:
                    _log("Transport connected");
                    if (IsHost)
                    {
                        _clients[e.Peer] = new Client { Peer = e.Peer, HandshakeSince = System.Environment.TickCount };
                        if (State != SessionState.Ready) { State = SessionState.Handshaking; Status = "Connected, checking builds"; }
                    }
                    else { State = SessionState.Handshaking; Status = "Connected, checking builds"; SendHello(); }
                    break;
                case NetEventType.Disconnected:
                    _log("Disconnected: " + e.Reason);
                    if (IsHost)
                    {
                        Client c;
                        if (_clients.TryGetValue(e.Peer, out c))
                        {
                            _clients.Remove(e.Peer);
                            if (c.PlayerId >= 0)
                            {
                                Players.RemoveAll(p => p.Id == c.PlayerId);
                                _log(c.Name + " left (" + e.Reason + ")");
                                SendRoster();
                                FireRoster();
                            }
                        }
                        if (PlayerCount <= 1)
                        {
                            bool was = State == SessionState.Ready;
                            State = SessionState.Listening; PeerName = null;
                            Status = "Disconnected (" + e.Reason + "), waiting for players";
                            if (was) SetReady(false);
                        }
                        else UpdatePeerName();
                    }
                    else
                    {
                        PeerName = null;
                        bool was = State == SessionState.Ready;
                        if (State != SessionState.Rejected) // keep the rejection reason visible
                        { State = SessionState.Idle; Status = "Disconnected (" + e.Reason + ")"; }
                        Players.Clear(); MyId = -1;
                        if (was) SetReady(false);
                        FireRoster();
                    }
                    break;
                case NetEventType.Data:
                    try { Handle(e.Peer, e.Payload, e.Reliable); }
                    catch (System.Exception ex) { _log("Ignored a message that could not be handled (" + ex.GetType().Name + ")"); _log("~" + ex); }
                    break;
            }
        }

        // ---------- sending ----------

        /// <summary>A game message to every other player. Clients send it to the host, which passes it on.</summary>
        public void SendGame(byte subType, Action<BinaryWriter> body, bool reliable)
        {
            if (State != SessionState.Ready) return;
            if (IsHost)
            {
                byte[] msg = Build(MsgType.GameFrom, w => { w.Write((byte)MyId); w.Write(subType); body(w); });
                foreach (Client c in _clients.Values) if (c.PlayerId >= 0) Transport.Send(c.Peer, msg, reliable);
            }
            else Transport.Send(0, Build(MsgType.Game, w => { w.Write(subType); body(w); }), reliable);
        }

        public void SendChat(string text)
        {
            if (State != SessionState.Ready) return;
            if (IsHost) Relay(-1, Build(MsgType.Chat, w => { w.Write(_localName); w.Write(text); }), true);
            else Transport.Send(0, Build(MsgType.Chat, w => { w.Write(_localName); w.Write(text); }), true);
        }

        /// <summary>Ask to play for team 0 (A) or 1 (B). The host may move anyone; a client only itself.</summary>
        public void PickTeam(int playerId, int team)
        {
            if (State != SessionState.Ready && !(IsHost && State == SessionState.Listening)) return;
            if (IsHost) { SetTeam(playerId, team); return; }
            if (playerId != MyId) return;
            Transport.Send(0, Build(MsgType.TeamPick, w => w.Write((byte)team)), true);
        }

        void SetTeam(int playerId, int team)
        {
            PlayerInfo p = Players.Find(x => x.Id == playerId);
            if (p == null || team < 0 || team > 1 || p.Team == team) return;
            if (Players.FindAll(x => x.Team == team).Count >= MaxPerTeam) { _log("Team " + (team == 0 ? "A" : "B") + " is full"); return; }
            p.Team = team;
            _log(p.Name + " plays for team " + (team == 0 ? "A" : "B"));
            SendRoster();
            FireRoster();
        }

        /// <summary>Host: a message to every handshaken client except one (-1: none).</summary>
        void Relay(int exceptPeer, byte[] msg, bool reliable)
        {
            foreach (Client c in _clients.Values) if (c.PlayerId >= 0 && c.Peer != exceptPeer) Transport.Send(c.Peer, msg, reliable);
        }

        void SendRoster()
        {
            if (!IsHost) return;
            byte[] msg = Build(MsgType.Roster, w =>
            {
                w.Write((byte)Players.Count);
                foreach (PlayerInfo p in Players) { w.Write((byte)p.Id); w.Write(p.Name ?? ""); w.Write((byte)p.Team); w.Write(p.RttMs); }
            });
            Relay(-1, msg, true);
        }

        void SetReady(bool ready) { if (ReadyChanged != null) ReadyChanged(ready); }
        void FireRoster() { if (RosterChanged != null) RosterChanged(); }

        void UpdatePeerName()
        {
            PlayerInfo other = Players.Find(p => p.Id != MyId);
            PeerName = other != null ? other.Name : null;
        }

        void SendHello()
        {
            Transport.Send(0, Build(MsgType.Hello, w =>
            {
                w.Write(Fingerprint.ProtocolVersion);
                w.Write(_pluginVersion);
                w.Write(_localName);
                w.Write(_fingerprint.Count);
                foreach (KeyValuePair<string, string> kv in _fingerprint) { w.Write(kv.Key); w.Write(Short(kv.Value)); } // 16 hex each: Hello stays one small packet
                // File names only, at most 4: Hello must stay one small UDP packet (Tailscale MTU 1280).
                int m = System.Math.Min(4, Fingerprint.LocalDeviations.Count);
                w.Write(Fingerprint.LocalDeviations.Count);
                for (int i = 0; i < m; i++) w.Write(ShortName(Fingerprint.LocalDeviations[i]));
                w.Write(PasswordHash(Password));
            }), true);
        }

        // ---------- receiving ----------

        void Handle(int peer, byte[] payload, bool reliable)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(payload)))
            {
                MsgType t = (MsgType)r.ReadByte();
                if (IsHost) HandleAtHost(peer, t, r, payload, reliable);
                else HandleAtClient(t, r);
            }
        }

        void HandleAtHost(int peer, MsgType t, BinaryReader r, byte[] payload, bool reliable)
        {
            Client c;
            if (!_clients.TryGetValue(peer, out c)) return;
            switch (t)
            {
                case MsgType.Hello:
                    if (c.PlayerId >= 0 || c.KickWhy != null) return; // already in, or being refused
                    HostHello(c, r);
                    break;
                case MsgType.TeamPick:
                    if (c.PlayerId >= 0) SetTeam(c.PlayerId, r.ReadByte());
                    break;
                case MsgType.Chat:
                {
                    if (c.PlayerId < 0) return;
                    r.ReadString(); string text = r.ReadString();
                    if (text.Length > 200) text = text.Substring(0, 200);
                    _log("[" + c.Name + "] " + text);
                    Relay(peer, Build(MsgType.Chat, w => { w.Write(c.Name); w.Write(text); }), true);
                    break;
                }
                case MsgType.Game:
                {
                    if (c.PlayerId < 0 || State != SessionState.Ready) return;
                    // to the other clients with the sender's number, then to us
                    byte[] fwd = new byte[payload.Length + 1];
                    fwd[0] = (byte)MsgType.GameFrom; fwd[1] = (byte)c.PlayerId;
                    Buffer.BlockCopy(payload, 1, fwd, 2, payload.Length - 1);
                    Relay(peer, fwd, reliable);
                    Deliver(c.PlayerId, r);
                    break;
                }
            }
        }

        void HostHello(Client c, BinaryReader r)
        {
            int proto = r.ReadInt32();
            string ver = r.ReadString();
            string name = r.ReadString();
            int n = r.ReadInt32();
            if (n < 0 || n > 256) return; // malformed
            Dictionary<string, string> theirs = new Dictionary<string, string>();
            for (int i = 0; i < n; i++) theirs[r.ReadString()] = r.ReadString();
            c.Dev = new List<string>(); c.DevCount = 0;
            if (r.BaseStream.Position < r.BaseStream.Length)
            {
                c.DevCount = r.ReadInt32();
                int m = System.Math.Min(4, System.Math.Max(0, c.DevCount));
                for (int i = 0; i < m; i++) { string s = r.ReadString(); c.Dev.Add(s.Length > 80 ? s.Substring(0, 80) : s); }
            }
            string theirPw = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : "";
            string problem = theirPw != PasswordHash(Password)
                ? (string.IsNullOrEmpty(Password) ? "this host has no password; clear yours" : "wrong session password")
                : Compare(proto, ver, theirs, c);
            if (problem == null && PlayerCount >= MaxPlayers) problem = "the session is full (" + MaxPlayers + " players)";
            if (problem != null)
            {
                _log("Rejecting " + name + ": " + problem);
                Transport.Send(c.Peer, Build(MsgType.Reject, w => w.Write(problem)), true);
                if (State != SessionState.Ready) Status = "Rejected " + name + ": " + problem;
                c.KickAt = System.Environment.TickCount + 1500; c.KickWhy = "refused: " + problem; // let the reason arrive, then free the place
                return;
            }
            int id = 1;
            while (Players.Exists(p => p.Id == id)) id++;
            c.PlayerId = id; c.Name = name;
            int teamA = Players.FindAll(p => p.Team == 0).Count, teamB = Players.Count - teamA;
            Players.Add(new PlayerInfo { Id = id, Name = name, Team = teamB <= teamA ? 1 : 0 }); // the smaller team
            Players.Sort((a, b) => a.Id.CompareTo(b.Id));
            Transport.Send(c.Peer, Build(MsgType.Welcome, w => { w.Write(_localName); w.Write((byte)id); }), true);
            bool first = State != SessionState.Ready;
            State = SessionState.Ready;
            Status = "Ready: " + (PlayerCount == 2 ? name + " joined" : PlayerCount + " players");
            UpdatePeerName();
            _log(name + " joined as player " + id);
            SendRoster();
            FireRoster();
            if (first) SetReady(true);
        }

        void HandleAtClient(MsgType t, BinaryReader r)
        {
            switch (t)
            {
                case MsgType.Welcome:
                {
                    string hostName = r.ReadString();
                    MyId = r.BaseStream.Position < r.BaseStream.Length ? r.ReadByte() : 1;
                    PeerName = hostName;
                    State = SessionState.Ready; Status = "Ready: joined " + hostName;
                    _log(Status + " as player " + MyId);
                    SetReady(true);
                    break;
                }
                case MsgType.Reject:
                {
                    string reason = r.ReadString();
                    State = SessionState.Rejected; Status = "Rejected by host: " + reason;
                    _log(Status);
                    Transport.Stop();
                    break;
                }
                case MsgType.Roster:
                {
                    int n = r.ReadByte();
                    Players.Clear();
                    for (int i = 0; i < n; i++)
                        Players.Add(new PlayerInfo { Id = r.ReadByte(), Name = r.ReadString(), Team = r.ReadByte(), RttMs = r.ReadSingle() });
                    if (State == SessionState.Ready)
                        Status = PlayerCount <= 2 ? "Ready: joined " + (Players.Find(p => p.Id == 0) ?? new PlayerInfo { Name = "host" }).Name : "Ready: " + PlayerCount + " players";
                    FireRoster();
                    break;
                }
                case MsgType.Chat:
                { string who = r.ReadString(); string c = r.ReadString(); if (c.Length > 200) c = c.Substring(0, 200); _log("[" + who + "] " + c); }
                    break;
                case MsgType.GameFrom:
                    if (State == SessionState.Ready) Deliver(r.ReadByte(), r);
                    break;
            }
        }

        void Deliver(int sender, BinaryReader r)
        {
            if (GameMessage == null) return;
            LastSender = sender;
            GameMessage(r.ReadByte(), r);
        }

        // ---------- build check ----------

        string Compare(int proto, string ver, Dictionary<string, string> theirs, Client c)
        {
            if (proto != Fingerprint.ProtocolVersion)
                return "protocol " + proto + " vs host " + Fingerprint.ProtocolVersion + " (both players need the same version from the GitHub Releases page)";
            if (ver != _pluginVersion) return "mod version " + ver + " vs host " + _pluginVersion + " (both players need the same version from the GitHub Releases page)";
            List<string> diffs = new List<string>();
            foreach (KeyValuePair<string, string> kv in _fingerprint)
            {
                string other;
                if (!theirs.TryGetValue(kv.Key, out other)) diffs.Add(kv.Key + " missing");
                else if (Short(other) != Short(kv.Value)) diffs.Add(kv.Key + " differs");
            }
            foreach (string k in theirs.Keys)
                if (!_fingerprint.ContainsKey(k)) diffs.Add(k + " only on the client");
            if (diffs.Count == 0) return null;
            string hint = diffs.Exists(d => d.StartsWith("ColdWatersMP.dll")) ? " - the mod DLLs differ: both download the same release from GitHub"
                        : diffs.Exists(d => d.StartsWith("Assembly-CSharp")) ? " - the game or DotMod differs: same game version and DotMod v0.4c on both"
                        : " - game data differs." + Named("Host", Fingerprint.LocalDeviations, Fingerprint.LocalDeviations.Count) + Named("Client", c.Dev, c.DevCount)
                          + " Fix: in JSGME disable and enable DotMod (its installer only updates MODS\\DotMod), or put back the original files";
            return "install mismatch: " + string.Join(", ", diffs.ToArray()) + hint;
        }

        static string Short(string h) { return h != null && h.Length > 16 ? h.Substring(0, 16) : (h ?? ""); }

        static string Named(string who, List<string> l, int count)
        {
            if (count <= 0) return " " + who + ": matches DotMod v0.4c.";
            int m = System.Math.Min(4, l.Count);
            List<string> n = new List<string>();
            for (int i = 0; i < m; i++) n.Add(ShortName(l[i]));
            return " " + who + " differs from DotMod v0.4c in " + count + " file(s): " + string.Join(", ", n.ToArray()) + (count > m ? ", ..." : "") + ".";
        }

        /// <summary>"changed vessels/wp_ss_zulu.txt" -> "changed wp_ss_zulu.txt".</summary>
        static string ShortName(string s)
        {
            int sp = s.IndexOf(' '), sl = s.LastIndexOf('/');
            return (sp > 0 && sl > sp) ? s.Substring(0, sp + 1) + s.Substring(sl + 1) : s;
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
