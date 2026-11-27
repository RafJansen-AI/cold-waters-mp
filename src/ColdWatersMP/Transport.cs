// Cold Waters MP - UDP transport with a reliable ordered channel per connection. C# 5 / .NET 3.5 (Unity 5.4 Mono).
// Main-thread only: call Poll() every frame.
// The host keeps one connection per client (a hub: clients never talk to each other); a client has one connection,
// to the host. Each connection has its own sequence numbers, acks, ping and timeout.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace ColdWatersMP
{
    public enum NetEventType { Connected, Disconnected, Data }

    public struct NetEvent
    {
        public NetEventType Type;
        /// <summary>The connection this event is about. A client's only connection (to the host) is 0.</summary>
        public int Peer;
        public byte[] Payload;
        public string Reason;
        /// <summary>Data: it came on the reliable channel (the host relays it the same way).</summary>
        public bool Reliable;
    }

    /// <summary>Pluggable transport (UDP now; relay / Steam P2P later).</summary>
    public interface ITransport
    {
        /// <summary>Client: connected to the host. Host: at least one client connected.</summary>
        bool IsConnected { get; }
        /// <summary>Client: round trip to the host. Host: the slowest client.</summary>
        float RttMs { get; }
        void StartHost(int port);
        void StartClient(string host, int port);
        void Send(int peer, byte[] payload, bool reliable);
        void Poll(List<NetEvent> events);
        void Stop();
        /// <summary>Host: drop that client at the next Poll; refuse its reconnects for a few seconds.</summary>
        void Kick(int peer, string reason);
        float Rtt(int peer);
        List<int> Peers { get; }
    }

    public class UdpTransport : ITransport
    {
        // Packet kinds
        const byte K_CONNECT = 1, K_ACCEPT = 2, K_UNRELIABLE = 3, K_RELIABLE = 4, K_ACK = 5,
                   K_PING = 6, K_PONG = 7, K_DISCONNECT = 8;
        const uint MAGIC = 0x43574D50; // "CWMP"
        const double ResendSec = 0.2, ConnectRetrySec = 0.5, PingSec = 1.0, TimeoutSec = 10.0, BlockSec = 5.0;
        const int MaxResends = 50;
        /// <summary>Most clients a host accepts at the transport level (the session caps players lower).</summary>
        public const int MaxConnections = 12;

        class Pending { public ushort Seq; public byte[] Packet; public double LastSent; public int Tries; }

        class Conn
        {
            public int Id;
            public EndPoint Ep;
            public bool Connected;
            public double LastHeard, LastPing;
            public ushort SendSeq, RecvExpected;
            public readonly Dictionary<ushort, Pending> Unacked = new Dictionary<ushort, Pending>();
            public readonly Dictionary<ushort, byte[]> OutOfOrder = new Dictionary<ushort, byte[]>();
            public float Rtt;
            public string KickReason;
        }

        Socket _sock;
        IPEndPoint _hostEp;
        bool _isHost, _running;
        double _lastConnectTry;
        int _nextId = 1;
        readonly Dictionary<int, Conn> _conns = new Dictionary<int, Conn>();
        readonly Dictionary<string, double> _blocked = new Dictionary<string, double>(); // endpoint -> until
        readonly byte[] _buf = new byte[65536];
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        readonly List<int> _peers = new List<int>();

        double Now { get { return _clock.Elapsed.TotalSeconds; } }

        public bool IsConnected { get { foreach (Conn c in _conns.Values) if (c.Connected) return true; return false; } }

        public float RttMs
        {
            get { float m = 0f; foreach (Conn c in _conns.Values) if (c.Connected && c.Rtt > m) m = c.Rtt; return m; }
        }

        public float Rtt(int peer) { Conn c; return _conns.TryGetValue(peer, out c) ? c.Rtt : 0f; }

        public List<int> Peers
        {
            get { _peers.Clear(); foreach (Conn c in _conns.Values) if (c.Connected) _peers.Add(c.Id); return _peers; }
        }

        public void StartHost(int port)
        {
            Stop();
            _isHost = true;
            _sock = MakeSocket(new IPEndPoint(IPAddress.Any, port));
            _running = true;
        }

        public void StartClient(string host, int port)
        {
            Stop();
            _isHost = false;
            IPAddress addr;
            if (!IPAddress.TryParse(host, out addr))
            {
                IPAddress[] all = Dns.GetHostAddresses(host);
                addr = null;
                foreach (IPAddress a in all) if (a.AddressFamily == AddressFamily.InterNetwork) { addr = a; break; }
                if (addr == null) throw new Exception("Could not resolve " + host);
            }
            _hostEp = new IPEndPoint(addr, port);
            _sock = MakeSocket(new IPEndPoint(IPAddress.Any, 0));
            _running = true;
            _lastConnectTry = -999;
            _conns[0] = new Conn { Id = 0, Ep = _hostEp };
        }

        static Socket MakeSocket(IPEndPoint bindEp)
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Blocking = false;
            try
            {
                // Windows: ignore ICMP port-unreachable resets (SIO_UDP_CONNRESET)
                s.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception) { }
            s.Bind(bindEp);
            return s;
        }

        public void Stop()
        {
            if (_sock != null)
            {
                foreach (Conn c in _conns.Values) if (c.Connected) { try { RawSend(c, Header(K_DISCONNECT, 0, 0)); } catch (Exception) { } }
                try { _sock.Close(); } catch (Exception) { }
            }
            _sock = null; _running = false;
            _conns.Clear(); _blocked.Clear();
        }

        public void Send(int peer, byte[] payload, bool reliable)
        {
            Conn c;
            if (!_conns.TryGetValue(peer, out c) || !c.Connected) return;
            if (reliable)
            {
                ushort seq = c.SendSeq++;
                byte[] pkt = Header(K_RELIABLE, seq, payload.Length);
                Buffer.BlockCopy(payload, 0, pkt, 7, payload.Length);
                c.Unacked[seq] = new Pending { Seq = seq, Packet = pkt, LastSent = Now, Tries = 1 };
                RawSend(c, pkt);
            }
            else
            {
                byte[] pkt = Header(K_UNRELIABLE, 0, payload.Length);
                Buffer.BlockCopy(payload, 0, pkt, 7, payload.Length);
                RawSend(c, pkt);
            }
        }

        public void Kick(int peer, string reason)
        {
            Conn c;
            if (_isHost && _conns.TryGetValue(peer, out c) && c.Connected) c.KickReason = reason;
        }

        readonly List<Conn> _scratch = new List<Conn>();

        public void Poll(List<NetEvent> events)
        {
            if (!_running || _sock == null) return;
            double now = Now;

            // Kicks requested since the last poll
            _scratch.Clear(); _scratch.AddRange(_conns.Values);
            foreach (Conn c in _scratch)
                if (c.KickReason != null)
                {
                    string why = c.KickReason; c.KickReason = null;
                    try { RawSend(c, Header(K_DISCONNECT, 0, 0)); } catch (Exception) { }
                    _blocked[c.Ep.ToString()] = now + BlockSec;
                    Drop(c, events, why);
                }

            // Client: keep knocking until accepted
            Conn toHost;
            if (!_isHost && _conns.TryGetValue(0, out toHost) && !toHost.Connected && now - _lastConnectTry > ConnectRetrySec)
            {
                _lastConnectTry = now;
                RawSend(toHost, Header(K_CONNECT, 0, 0));
            }

            // Receive everything queued
            while (_sock != null)
            {
                int n;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    if (_sock.Available <= 0) break;
                    n = _sock.ReceiveFrom(_buf, ref from);
                }
                catch (SocketException) { break; }
                if (n < 7) continue;
                if (ReadU32(_buf, 0) != MAGIC) continue;
                byte kind = _buf[4];
                ushort seq = (ushort)(_buf[5] | (_buf[6] << 8));
                int len = n - 7;
                Conn c = Find(from);

                if (kind == K_CONNECT && _isHost)
                {
                    if (c == null)
                    {
                        double until;
                        if (_blocked.TryGetValue(from.ToString(), out until) && now < until) continue; // just refused
                        if (_conns.Count >= MaxConnections) continue;
                        c = new Conn { Id = _nextId++, Ep = from, Connected = true, LastHeard = now, LastPing = now };
                        _conns[c.Id] = c;
                        events.Add(new NetEvent { Type = NetEventType.Connected, Peer = c.Id });
                    }
                    RawSend(c, Header(K_ACCEPT, 0, 0));
                    continue;
                }
                if (c == null) continue; // a stranger
                c.LastHeard = now;

                switch (kind)
                {
                    case K_ACCEPT:
                        if (!_isHost && !c.Connected) { c.Connected = true; c.LastPing = now; events.Add(new NetEvent { Type = NetEventType.Connected, Peer = c.Id }); }
                        break;
                    case K_UNRELIABLE:
                        if (c.Connected) events.Add(new NetEvent { Type = NetEventType.Data, Peer = c.Id, Payload = Slice(_buf, 7, len) });
                        break;
                    case K_RELIABLE:
                        if (!c.Connected) break;
                        RawSend(c, Header(K_ACK, seq, 0));
                        DeliverOrdered(c, seq, Slice(_buf, 7, len), events);
                        break;
                    case K_ACK:
                        c.Unacked.Remove(seq);
                        break;
                    case K_PING:
                    {
                        byte[] pong = Header(K_PONG, 0, len);
                        Buffer.BlockCopy(_buf, 7, pong, 7, len);
                        RawSend(c, pong);
                        break;
                    }
                    case K_PONG:
                        if (len >= 8)
                        {
                            double sent = BitConverter.ToDouble(_buf, 7);
                            float sample = (float)((now - sent) * 1000.0);
                            c.Rtt = c.Rtt <= 0 ? sample : c.Rtt * 0.8f + sample * 0.2f;
                        }
                        break;
                    case K_DISCONNECT:
                        if (c.Connected) Drop(c, events, _isHost ? "Player left" : "Host left");
                        break;
                }
                if (_sock == null) return; // a client's drop stops everything
            }

            // Resends, pings and timeouts per connection
            _scratch.Clear(); _scratch.AddRange(_conns.Values);
            foreach (Conn c in _scratch)
            {
                if (!c.Connected) continue;
                bool lost = false;
                foreach (Pending p in c.Unacked.Values)
                {
                    if (now - p.LastSent < ResendSec) continue;
                    if (p.Tries >= MaxResends) { lost = true; break; }
                    p.Tries++; p.LastSent = now; RawSend(c, p.Packet);
                }
                if (lost) { Drop(c, events, "Reliable message lost"); if (_sock == null) return; continue; }
                if (now - c.LastPing > PingSec)
                {
                    c.LastPing = now;
                    byte[] ping = Header(K_PING, 0, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes(now), 0, ping, 7, 8);
                    RawSend(c, ping);
                }
                if (now - c.LastHeard > TimeoutSec) { Drop(c, events, "Timed out"); if (_sock == null) return; }
            }
        }

        Conn Find(EndPoint ep)
        {
            foreach (Conn c in _conns.Values) if (SameEp(c.Ep, ep)) return c;
            return null;
        }

        static void DeliverOrdered(Conn c, ushort seq, byte[] payload, List<NetEvent> events)
        {
            short diff = (short)(seq - c.RecvExpected);
            if (diff < 0) return;                 // duplicate of something already delivered
            if (diff > 0) { c.OutOfOrder[seq] = payload; return; }
            events.Add(new NetEvent { Type = NetEventType.Data, Peer = c.Id, Payload = payload, Reliable = true });
            c.RecvExpected++;
            byte[] next;
            while (c.OutOfOrder.TryGetValue(c.RecvExpected, out next))
            {
                c.OutOfOrder.Remove(c.RecvExpected);
                events.Add(new NetEvent { Type = NetEventType.Data, Peer = c.Id, Payload = next, Reliable = true });
                c.RecvExpected++;
            }
        }

        /// <summary>Host: forget that client and keep listening. Client: stop (the session decides about rejoining).</summary>
        void Drop(Conn c, List<NetEvent> events, string reason)
        {
            c.Connected = false;
            events.Add(new NetEvent { Type = NetEventType.Disconnected, Peer = c.Id, Reason = reason });
            if (_isHost) { _conns.Remove(c.Id); return; }
            IPEndPoint hostEp = _hostEp;
            Stop();
            _hostEp = hostEp;
        }

        /// <summary>Testing only: fraction of outgoing packets to drop (0..1).</summary>
        public static double SimulatedLoss;
        static readonly Random _lossRng = new Random();

        void RawSend(Conn c, byte[] pkt)
        {
            if (_sock == null || c == null || c.Ep == null) return;
            if (SimulatedLoss > 0 && _lossRng.NextDouble() < SimulatedLoss) return;
            try { _sock.SendTo(pkt, c.Ep); } catch (SocketException) { }
        }

        static byte[] Header(byte kind, ushort seq, int payloadLen)
        {
            byte[] b = new byte[7 + payloadLen];
            b[0] = (byte)(MAGIC & 0xFF); b[1] = (byte)((MAGIC >> 8) & 0xFF);
            b[2] = (byte)((MAGIC >> 16) & 0xFF); b[3] = (byte)((MAGIC >> 24) & 0xFF);
            b[4] = kind; b[5] = (byte)(seq & 0xFF); b[6] = (byte)(seq >> 8);
            return b;
        }

        static uint ReadU32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        static byte[] Slice(byte[] src, int off, int len) { byte[] r = new byte[len]; Buffer.BlockCopy(src, off, r, 0, len); return r; }
        static bool SameEp(EndPoint a, EndPoint b) { return a != null && b != null && a.Equals(b); }
    }
}
