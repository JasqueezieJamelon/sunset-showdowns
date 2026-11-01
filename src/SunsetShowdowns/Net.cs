using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LLD
{
    public enum Msg : byte
    {
        Hello = 1,      // client -> host: protocol, build, name
        Welcome,        // host -> client: your side, host name
        Reject,         // host -> client: reason, then disconnect
        Ping,           // either way: t0
        Pong,           // reply: t0, hostTime
        Ready,          // client -> host: ready flag
        RoundStart,     // host -> client: round index, draw time, seed
        Input,          // either way: hand position
        Fire,           // client -> host: when and from where
        Hit,            // host -> client: what a bullet did
        Foul,           // client -> host: I drew early  /  host -> client: ruling
        RoundEnd,       // host -> client: winner, reason
        MatchEnd,       // host -> client: winner
        Rematch,        // either way
        Snapshot,       // host -> client: correction of both bodies
        Lobby,          // host -> client: match length
        Look,           // either way: a changed loadout
    }

    /// <summary>One bullet's outcome, decided by the host and replayed identically on both ends.</summary>
    public struct NetHit
    {
        public byte Shooter;            // 0 left, 1 right
        public byte Target;
        public bool Damaged;            // false for a clean miss, which still needs its flash
        public byte Zone;
        public float MuzzleX, MuzzleY;  // for the flash and smoke
        public float EndX, EndY;        // where the shot stopped, for the tracer and dust
        public float LocalX, LocalY;    // entry, in the target body's own space
        public float ExitX, ExitY;      // exit, same space
        public float DirX, DirY;
        public float Shock, Bleed, Gore;
        public bool Instant, Cripple, Disarm, Through;
        public byte Head;               // bit 0-1 severity, bit 2 eye out, bit 3 jaw gone,
                                        // bits 4-7 how far the vault came open, in sixteenths
        public string Note;             // "through the heart"
    }

    /// <summary>
    /// Host or client for a two-player match. The host is the referee: it owns the clock, the
    /// round timing and every shot's outcome. The client sends what its hand is doing and
    /// replays what the host rules.
    /// </summary>
    public class NetSession
    {
        /// Bump when the wire format changes. Builds with different numbers refuse each other.
        /// 4: handshake carries each side's look and the match length; round ends carry a note
        ///    for the loser too; snapshots carry the whole stance, not just a position.
        /// 5: a round can end in a draw (winner 2), which scores for nobody.
        public const int Protocol = 6;   // 6: the loadout carries a hair style
        public const int DefaultPort = 27555;
        const string Key = "lastlightduels";

        public bool IsHost;
        public bool Connected;
        public bool Closed;
        public string Status = "";
        public string Error = "";
        public int LocalSide;                 // 0 = left, 1 = right
        public string RemoteName = "";
        public string LocalName = "drifter";

        // what each side looks like, swapped during the handshake so both screens agree
        public Loadout LocalLook = Loadout.Default;
        public Loadout RemoteLook = Loadout.Default;

        /// Rounds a side must win to take the match. The host owns it and the client is told.
        public int Wins = 3;

        public readonly int[] Score = new int[2];
        public int RoundIndex;
        public bool RemoteReady, LocalReady;
        public int PingMs;

        // round control, written by messages and read by the duel
        public bool RoundStartPending;
        public int PendingRound;
        public double PendingDrawTime;
        public int PendingSeed;
        public bool RoundOverPending;
        public byte RoundWinner;
        public string RoundNote = "";
        public string RoundLoserNote = "";
        public bool MatchOverPending;
        public byte MatchWinner;
        public bool RematchPending;

        public Vector2 RemoteHand;
        public bool RemoteDrawn;
        public bool RemoteHandValid;
        public readonly Queue<NetHit> Hits = new();
        public readonly List<(double time, Vector2 hand, Vector2 aim, byte side)> HostFireQueue = new();
        public bool RemoteFoul;
        public string RemoteFoulNote = "";

        NetManager _net;
        EventBasedNetListener _listener;
        NetPeer _peer;
        double _clockOffset;        // add to local time to get host time
        double _pingSentAt;
        float _pingTimer;
        readonly NetDataWriter _w = new();

        public double LocalTime => Raylib_cs.Raylib.GetTime();
        /// <summary>Shared clock. On the host this is just its own time.</summary>
        public double HostTime => LocalTime + _clockOffset;

        // ---------- lifecycle ----------
        public static NetSession StartHost(int port, string name = "drifter", Loadout look = default)
        {
            var s = new NetSession { IsHost = true, LocalSide = 0, Status = "waiting for a challenger" };
            s.LocalName = string.IsNullOrWhiteSpace(name) ? "drifter" : name;
            s.LocalLook = look.Sane();
            s._listener = new EventBasedNetListener();
            s._net = new NetManager(s._listener) { AutoRecycle = true, UpdateTime = 15 };
            s._listener.ConnectionRequestEvent += r => r.AcceptIfKey(Key);
            s._listener.PeerConnectedEvent += p => { s._peer = p; };
            s._listener.PeerDisconnectedEvent += (p, info) => s.Dropped(info.Reason.ToString());
            s._listener.NetworkReceiveEvent += (p, reader, ch, dm) => s.Receive(p, reader);
            if (!s._net.Start(port)) { s.Error = "could not open port " + port; s.Closed = true; }
            return s;
        }

        /// Identity is handed in here rather than set afterwards: the hello goes out the moment
        /// the peer connects, so a name assigned later could miss the handshake.
        public static NetSession Join(string address, int port, string name = "drifter", Loadout look = default)
        {
            var s = new NetSession { IsHost = false, LocalSide = 1, Status = "connecting" };
            s.LocalName = string.IsNullOrWhiteSpace(name) ? "drifter" : name;
            s.LocalLook = look.Sane();
            s._listener = new EventBasedNetListener();
            s._net = new NetManager(s._listener) { AutoRecycle = true, UpdateTime = 15 };
            s._listener.PeerConnectedEvent += p =>
            {
                s._peer = p;
                s._w.Reset();
                s._w.Put((byte)Msg.Hello);
                s._w.Put(Protocol);
                s._w.Put(App.Version);
                s._w.Put(s.LocalName);
                PutLook(s._w, s.LocalLook);
                p.Send(s._w, 0, DeliveryMethod.ReliableOrdered);
            };
            s._listener.PeerDisconnectedEvent += (p, info) => s.Dropped(info.Reason.ToString());
            s._listener.NetworkReceiveEvent += (p, reader, ch, dm) => s.Receive(p, reader);
            if (!s._net.Start()) { s.Error = "could not open a socket"; s.Closed = true; return s; }
            try { s._net.Connect(address, port, Key); }
            catch (Exception e) { s.Error = e.Message; s.Closed = true; }
            return s;
        }

        void Dropped(string reason)
        {
            Connected = false;
            _peer = null;
            if (string.IsNullOrEmpty(Error)) Error = Explain(reason, IsHost);
            Status = "disconnected";
        }

        public void Close()
        {
            try { _net?.Stop(); } catch { }
            Closed = true;
            Connected = false;
        }

        public void Poll(float dt)
        {
            if (_net == null) return;
            _net.PollEvents();

            if (_peer != null && _peer.ConnectionState == ConnectionState.Connected)
            {
                PingMs = _peer.Ping;
                _pingTimer -= dt;
                if (_pingTimer <= 0f)
                {
                    _pingTimer = 1f;
                    _pingSentAt = LocalTime;
                    Send(Msg.Ping, w => w.Put(_pingSentAt), DeliveryMethod.Unreliable);
                }
            }
        }

        static void PutLook(NetDataWriter w, Loadout l)
        {
            for (int f = 0; f < Loadout.Fields; f++) w.Put(l.Get(f));
        }

        static Loadout GetLook(NetPacketReader r)
        {
            var l = Loadout.Default;
            for (int f = 0; f < Loadout.Fields; f++) l.Set(f, r.GetByte());
            return l.Sane();
        }

        // ---------- sending ----------
        void Send(Msg m, Action<NetDataWriter> body, DeliveryMethod dm = DeliveryMethod.ReliableOrdered)
        {
            if (_peer == null || _peer.ConnectionState != ConnectionState.Connected) return;
            _w.Reset();
            _w.Put((byte)m);
            body?.Invoke(_w);
            _peer.Send(_w, 0, dm);
        }

        public void SendReady(bool ready)
        {
            LocalReady = ready;
            Send(Msg.Ready, w => w.Put(ready));
        }

        public void SendInput(Vector2 aim, bool drawn)
            => Send(Msg.Input, w => { w.Put(aim.X); w.Put(aim.Y); w.Put(drawn); }, DeliveryMethod.Unreliable);

        public void SendFire(double hostTime, Vector2 hand, Vector2 aim)
            => Send(Msg.Fire, w => { w.Put(hostTime); w.Put(hand.X); w.Put(hand.Y); w.Put(aim.X); w.Put(aim.Y); });

        /// <param name="why">What the fouling side is being told, relayed so the host can rule
        /// with the same words and nobody is told they were shot when they jumped the call.</param>
        public void SendFoul(string why) => Send(Msg.Foul, w => w.Put(why ?? ""));

        public void SendRoundStart(int round, double drawTime, int seed)
            => Send(Msg.RoundStart, w => { w.Put(round); w.Put(drawTime); w.Put(seed); });

        public void SendRoundEnd(byte winner, string note, string loserNote)
            => Send(Msg.RoundEnd, w =>
            {
                w.Put(winner); w.Put(Score[0]); w.Put(Score[1]);
                w.Put(note ?? ""); w.Put(loserNote ?? "");
            });

        public void SendWins(int wins) => Send(Msg.Lobby, w => w.Put(wins));

        public void SendLook(Loadout l) { LocalLook = l; Send(Msg.Look, w => PutLook(w, l)); }

        public void SendMatchEnd(byte winner)
            => Send(Msg.MatchEnd, w => w.Put(winner));

        public void SendRematch() => Send(Msg.Rematch, null);

        public void SendHit(NetHit h) => Send(Msg.Hit, w =>
        {
            w.Put(h.Shooter); w.Put(h.Target); w.Put(h.Damaged); w.Put(h.Zone);
            w.Put(h.MuzzleX); w.Put(h.MuzzleY);
            w.Put(h.EndX); w.Put(h.EndY);
            w.Put(h.LocalX); w.Put(h.LocalY);
            w.Put(h.ExitX); w.Put(h.ExitY);
            w.Put(h.DirX); w.Put(h.DirY);
            w.Put(h.Shock); w.Put(h.Bleed); w.Put(h.Gore);
            w.Put(h.Instant); w.Put(h.Cripple); w.Put(h.Disarm); w.Put(h.Through);
            w.Put(h.Head);
            w.Put(h.Note ?? "");
        });

        public void SendSnapshot(Fighter left, Fighter right) => Send(Msg.Snapshot, w =>
        {
            foreach (var f in new[] { left, right }) PutSnap(w, f.Capture());
        }, DeliveryMethod.Unreliable);

        static void PutSnap(NetDataWriter w, in Fighter.Snap s)
        {
            w.Put(s.Pelvis.X); w.Put(s.Pelvis.Y);
            w.Put(s.PelvisV.X); w.Put(s.PelvisV.Y);
            w.Put(s.Chest.X); w.Put(s.Chest.Y);
            w.Put(s.ChestV.X); w.Put(s.ChestV.Y);
            w.Put(s.Foot0.X); w.Put(s.Foot0.Y);
            w.Put(s.Foot1.X); w.Put(s.Foot1.Y);
            w.Put(s.Blood); w.Put(s.Shock); w.Put(s.Kneel);
            w.Put(s.Down); w.Put(s.Dead); w.Put(s.Air0); w.Put(s.Air1);
        }

        // ---------- receiving ----------
        void Receive(NetPeer peer, NetPacketReader r)
        {
            var m = (Msg)r.GetByte();
            switch (m)
            {
                case Msg.Hello:
                    {
                        int proto = r.GetInt();
                        string build = r.GetString();
                        string name = r.GetString();
                        if (proto != Protocol || build != App.Version)
                        {
                            string why = proto != Protocol
                                ? $"protocol {proto} does not match {Protocol}"
                                : $"build {build} does not match {App.Version}";
                            _w.Reset(); _w.Put((byte)Msg.Reject); _w.Put(why);
                            peer.Send(_w, 0, DeliveryMethod.ReliableOrdered);
                            Status = "refused: " + why;
                            peer.Disconnect();
                            return;
                        }
                        RemoteName = name;
                        RemoteLook = GetLook(r);
                        Connected = true;
                        Status = "connected";
                        Send(Msg.Welcome, w =>
                        {
                            w.Put((byte)1); w.Put(LocalName); PutLook(w, LocalLook); w.Put(Wins);
                        });
                        break;
                    }

                case Msg.Welcome:
                    LocalSide = r.GetByte();
                    RemoteName = r.GetString();
                    RemoteLook = GetLook(r);
                    Wins = r.GetInt();
                    Connected = true;
                    Status = "connected";
                    break;

                case Msg.Lobby:
                    if (!IsHost) Wins = Math.Clamp(r.GetInt(), 1, 9);
                    break;

                case Msg.Look:
                    RemoteLook = GetLook(r);
                    break;

                case Msg.Reject:
                    Error = "host refused: " + r.GetString();
                    Status = "refused";
                    break;

                case Msg.Ping:
                    {
                        double t0 = r.GetDouble();
                        Send(Msg.Pong, w => { w.Put(t0); w.Put(HostTime); }, DeliveryMethod.Unreliable);
                        break;
                    }

                case Msg.Pong:
                    {
                        double t0 = r.GetDouble();
                        double theirTime = r.GetDouble();
                        double rtt = LocalTime - t0;
                        if (!IsHost)
                        {
                            // their clock, plus the time it took the answer to reach us
                            double estimate = theirTime + rtt * 0.5 - LocalTime;
                            _clockOffset = _clockOffset == 0d ? estimate : _clockOffset * 0.8 + estimate * 0.2;
                        }
                        break;
                    }

                case Msg.Ready:
                    RemoteReady = r.GetBool();
                    break;

                case Msg.RoundStart:
                    PendingRound = r.GetInt();
                    PendingDrawTime = r.GetDouble();
                    PendingSeed = r.GetInt();
                    RoundStartPending = true;
                    break;

                case Msg.Input:
                    RemoteHand = new Vector2(r.GetFloat(), r.GetFloat());
                    RemoteDrawn = r.GetBool();
                    RemoteHandValid = true;
                    break;

                case Msg.Fire:
                    {
                        double t = r.GetDouble();
                        var hand = new Vector2(r.GetFloat(), r.GetFloat());
                        var aim = new Vector2(r.GetFloat(), r.GetFloat());
                        if (IsHost)
                        {
                            // clamp a client that claims an impossible moment
                            double now = HostTime;
                            double earliest = now - (PingMs * 0.001) - 0.25;
                            if (t < earliest) t = earliest;
                            if (t > now) t = now;
                            HostFireQueue.Add((t, hand, aim, 1));
                        }
                        break;
                    }

                case Msg.Foul:
                    RemoteFoulNote = r.GetString();
                    RemoteFoul = true;
                    break;

                case Msg.Hit:
                    {
                        var h = new NetHit
                        {
                            Shooter = r.GetByte(),
                            Target = r.GetByte(),
                            Damaged = r.GetBool(),
                            Zone = r.GetByte(),
                            MuzzleX = r.GetFloat(),
                            MuzzleY = r.GetFloat(),
                            EndX = r.GetFloat(),
                            EndY = r.GetFloat(),
                            LocalX = r.GetFloat(),
                            LocalY = r.GetFloat(),
                            ExitX = r.GetFloat(),
                            ExitY = r.GetFloat(),
                            DirX = r.GetFloat(),
                            DirY = r.GetFloat(),
                            Shock = r.GetFloat(),
                            Bleed = r.GetFloat(),
                            Gore = r.GetFloat(),
                            Instant = r.GetBool(),
                            Cripple = r.GetBool(),
                            Disarm = r.GetBool(),
                            Through = r.GetBool(),
                            Head = r.GetByte(),
                        };
                        h.Note = r.GetString();
                        Hits.Enqueue(h);
                        break;
                    }

                case Msg.RoundEnd:
                    RoundWinner = r.GetByte();
                    Score[0] = r.GetInt();
                    Score[1] = r.GetInt();
                    RoundNote = r.GetString();
                    RoundLoserNote = r.GetString();
                    RoundOverPending = true;
                    break;

                case Msg.MatchEnd:
                    MatchWinner = r.GetByte();
                    MatchOverPending = true;
                    break;

                case Msg.Rematch:
                    RematchPending = true;
                    break;

                case Msg.Snapshot:
                    // light correction; the duel decides whether to take it
                    SnapLeft = ReadBody(r);
                    SnapRight = ReadBody(r);
                    HasSnapshot = true;
                    break;
            }
        }

        public Fighter.Snap SnapLeft, SnapRight;
        public bool HasSnapshot;

        static Vector2 V(NetPacketReader r) => new Vector2(r.GetFloat(), r.GetFloat());

        static Fighter.Snap ReadBody(NetPacketReader r) => new Fighter.Snap
        {
            Pelvis = V(r), PelvisV = V(r), Chest = V(r), ChestV = V(r),
            Foot0 = V(r), Foot1 = V(r),
            Blood = r.GetFloat(), Shock = r.GetFloat(), Kneel = r.GetFloat(),
            Down = r.GetBool(), Dead = r.GetBool(), Air0 = r.GetBool(), Air1 = r.GetBool(),
        };

        /// <summary>Best guess at this machine's LAN address, for the host to show.</summary>
        public static string LocalAddress()
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect("8.8.8.8", 65530);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
            catch { return "127.0.0.1"; }
        }

        /// <summary>
        /// True for an address that only means anything inside your own house. Handing one of
        /// these to someone on another network is the commonest way a match fails to connect:
        /// their machine looks for that address on their own network and finds nothing.
        /// </summary>
        public static bool IsPrivate(string ip)
        {
            if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            return b[0] == 10
                || b[0] == 127
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 169 && b[1] == 254);
        }

        /// <summary>The address the rest of the internet sees. Null until the lookup answers.</summary>
        public static string PublicAddress { get; private set; }
        public static bool PublicLookupDone { get; private set; }
        static bool _publicStarted;

        public static void FindPublicAddress()
        {
            if (_publicStarted) return;
            _publicStarted = true;
            System.Threading.Tasks.Task.Run(async () =>
            {
                string[] feeds = { "https://api.ipify.org", "https://icanhazip.com", "https://ifconfig.me/ip" };
                foreach (var url in feeds)
                {
                    try
                    {
                        using var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                        c.DefaultRequestHeaders.Add("User-Agent", "SunsetShowdowns");
                        string t = (await c.GetStringAsync(url)).Trim();
                        if (IPAddress.TryParse(t, out var ip) &&
                            ip.AddressFamily == AddressFamily.InterNetwork && !IsPrivate(t))
                        {
                            PublicAddress = t;
                            break;
                        }
                    }
                    catch { }
                }
                PublicLookupDone = true;
            });
        }

        /// <summary>Turn a raw disconnect reason into something a player can act on.</summary>
        public static string Explain(string reason, bool wasHost)
        {
            string r = (reason ?? "").ToLowerInvariant();
            if (r.Contains("timeout") || r.Contains("connectionfailed") || r.Contains("unknownhost"))
                return wasHost
                    ? "they never arrived - check they used your internet address and that UDP 27555 is forwarded"
                    : "no answer from that address - check the address and that the host has UDP 27555 forwarded";
            if (r.Contains("remoteconnectionclose")) return "they closed the connection";
            if (r.Contains("connectionrejected")) return "the host refused the connection";
            if (r.Contains("hostunreachable") || r.Contains("networkunreachable"))
                return "that address cannot be reached from here";
            return "connection lost (" + r + ")";
        }
    }
}
