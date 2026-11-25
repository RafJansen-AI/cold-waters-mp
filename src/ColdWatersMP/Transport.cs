// Cold Waters MP - minimal 1-peer UDP transport with a reliable ordered channel.
// C# 5 / .NET 3.5 (Unity 5.4 Mono). Main-thread only: call Poll() every frame.
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
        public byte[] Payload;
        public string Reason;
    }

    /// <summary>Pluggable transport (UDP now; relay / Steam P2P later).</summary>
    public interface ITransport
    {
        bool IsConnected { get; }
        float RttMs { get; }
        void StartHost(int port);
        void StartClient(string host, int port);
        void Send(byte[] payload, bool reliable);
        void Poll(List<NetEvent> events);
        void Stop();
        /// <summary>Host: drop the current peer at the next Poll and keep listening; refuse its reconnects for a few seconds.</summary>
        void Kick(string reason);
    }

    public class UdpTransport : ITransport
    {
        // Packet kinds
        const byte K_CONNECT = 1, K_ACCEPT = 2, K_UNRELIABLE = 3, K_RELIABLE = 4, K_ACK = 5,
                   K_PING = 6, K_PONG = 7, K_DISCONNECT = 8;
        const uint MAGIC = 0x43574D50; // "CWMP"
        const double ResendSec = 0.2, ConnectRetrySec = 0.5, PingSec = 1.0, TimeoutSec = 10.0;
        const int MaxResends = 50;

        class Pending { public ushort Seq; public byte[] Packet; public double LastSent; public int Tries; }

        Socket _sock;
        EndPoint _peer;
        IPEndPoint _hostEp;
        bool _isHost, _connected, _running;
        double _lastHeard, _lastPing, _lastConnectTry;
        ushort _sendSeq, _recvExpected;
        readonly Dictionary<ushort, Pending> _unacked = new Dictionary<ushort, Pending>();
        readonly Dictionary<ushort, byte[]> _outOfOrder = new Dictionary<ushort, byte[]>();
        readonly byte[] _buf = new byte[65536];
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        float _rtt;

        public bool IsConnected { get { return _connected; } }
        public float RttMs { get { return _rtt; } }
        double Now { get { return _clock.Elapsed.TotalSeconds; } }

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
                if (_connected && _peer != null) { try { RawSend(Header(K_DISCONNECT, 0, 0)); } catch (Exception) { } }
                try { _sock.Close(); } catch (Exception) { }
            }
            _sock = null; _peer = null; _connected = false; _running = false;
            _sendSeq = 0; _recvExpected = 0;
            _unacked.Clear(); _outOfOrder.Clear();
        }

        public void Send(byte[] payload, bool reliable)
        {
            if (!_connected) return;
            if (reliable)
            {
                ushort seq = _sendSeq++;
                byte[] pkt = Header(K_RELIABLE, seq, payload.Length);
                Buffer.BlockCopy(payload, 0, pkt, 7, payload.Length);
                _unacked[seq] = new Pending { Seq = seq, Packet = pkt, LastSent = Now, Tries = 1 };
                RawSend(pkt);
            }
            else
            {
                byte[] pkt = Header(K_UNRELIABLE, 0, payload.Length);
                Buffer.BlockCopy(payload, 0, pkt, 7, payload.Length);
                RawSend(pkt);
            }
        }

        string _kickReason; EndPoint _blocked; double _blockedUntil;

        public void Kick(string reason) { if (_isHost && _connected) _kickReason = reason; }

        public void Poll(List<NetEvent> events)
        {
            if (!_running || _sock == null) return;
            double now = Now;
            if (_kickReason != null)
            {
                string why = _kickReason; _kickReason = null;
                _blocked = _peer; _blockedUntil = now + 5.0;
                try { RawSend(Header(K_DISCONNECT, 0, 0)); } catch (Exception) { }
                _connected = false; // so Stop() inside Drop doesn't send a second disconnect
                EndPoint keepBlocked = _blocked; double keepUntil = _blockedUntil;
                Drop(events, why);
                _blocked = keepBlocked; _blockedUntil = keepUntil;
                return;
            }

            // Client: keep knocking until accepted
            if (!_isHost && !_connected && now - _lastConnectTry > ConnectRetrySec)
            {
                _lastConnectTry = now;
                _peer = _hostEp;
                RawSend(Header(K_CONNECT, 0, 0));
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

                if (kind == K_CONNECT && _isHost)
                {
                    if (!_connected && _blocked != null && now < _blockedUntil && SameEp(from, _blocked)) continue; // just refused
                    if (!_connected) { _peer = from; _connected = true; _lastHeard = now; events.Add(new NetEvent { Type = NetEventType.Connected }); }
                    if (SameEp(from, _peer)) RawSend(Header(K_ACCEPT, 0, 0));
                    continue;
                }
                if (_peer == null || !SameEp(from, _peer)) continue; // 1v1: ignore strangers
                _lastHeard = now;

                switch (kind)
                {
                    case K_ACCEPT:
                        if (!_isHost && !_connected) { _connected = true; events.Add(new NetEvent { Type = NetEventType.Connected }); }
                        break;
                    case K_UNRELIABLE:
                        if (_connected) events.Add(new NetEvent { Type = NetEventType.Data, Payload = Slice(_buf, 7, len) });
                        break;
                    case K_RELIABLE:
                        if (!_connected) break;
                        RawSend(Header(K_ACK, seq, 0));
                        DeliverOrdered(seq, Slice(_buf, 7, len), events);
                        break;
                    case K_ACK:
                        _unacked.Remove(seq);
                        break;
                    case K_PING:
                    {
                        byte[] pong = Header(K_PONG, 0, len);
                        Buffer.BlockCopy(_buf, 7, pong, 7, len);
                        RawSend(pong);
                        break;
                    }
                    case K_PONG:
                        if (len >= 8)
                        {
                            double sent = BitConverter.ToDouble(_buf, 7);
                            float sample = (float)((now - sent) * 1000.0);
                            _rtt = _rtt <= 0 ? sample : _rtt * 0.8f + sample * 0.2f;
                        }
                        break;
                    case K_DISCONNECT:
                        Drop(events, "Peer left");
                        return;
                }
            }

            if (!_connected) return;

            // Resend unacked reliable packets
            List<ushort> dead = null;
            foreach (Pending p in _unacked.Values)
            {
                if (now - p.LastSent < ResendSec) continue;
                if (p.Tries >= MaxResends) { if (dead == null) dead = new List<ushort>(); dead.Add(p.Seq); continue; }
                p.Tries++; p.LastSent = now; RawSend(p.Packet);
            }
            if (dead != null) { Drop(events, "Reliable message lost"); return; }

            if (now - _lastPing > PingSec)
            {
                _lastPing = now;
                byte[] ping = Header(K_PING, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(now), 0, ping, 7, 8);
                RawSend(ping);
            }
            if (now - _lastHeard > TimeoutSec) Drop(events, "Timed out");
        }

        void DeliverOrdered(ushort seq, byte[] payload, List<NetEvent> events)
        {
            short diff = (short)(seq - _recvExpected);
            if (diff < 0) return;                 // duplicate of something already delivered
            if (diff > 0) { _outOfOrder[seq] = payload; return; }
            events.Add(new NetEvent { Type = NetEventType.Data, Payload = payload });
            _recvExpected++;
            byte[] next;
            while (_outOfOrder.TryGetValue(_recvExpected, out next))
            {
                _outOfOrder.Remove(_recvExpected);
                events.Add(new NetEvent { Type = NetEventType.Data, Payload = next });
                _recvExpected++;
            }
        }

        void Drop(List<NetEvent> events, string reason)
        {
            bool wasHost = _isHost;
            int port = wasHost && _sock != null ? ((IPEndPoint)_sock.LocalEndPoint).Port : 0;
            IPEndPoint hostEp = _hostEp;
            Stop();
            events.Add(new NetEvent { Type = NetEventType.Disconnected, Reason = reason });
            // Host keeps listening for a new/returning client; client stays stopped.
            if (wasHost) StartHost(port);
            else _hostEp = hostEp;
        }

        /// <summary>Testing only: fraction of outgoing packets to drop (0..1).</summary>
        public static double SimulatedLoss;
        static readonly Random _lossRng = new Random();

        void RawSend(byte[] pkt)
        {
            if (_sock == null || _peer == null) return;
            if (SimulatedLoss > 0 && _lossRng.NextDouble() < SimulatedLoss) return;
            try { _sock.SendTo(pkt, _peer); } catch (SocketException) { }
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
