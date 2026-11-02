#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace OgsTsBridge.Sources
{
/// <summary>
/// NoLimits 2 Roller Coaster Simulation telemetry server: TCP
/// 127.0.0.1:15151, available when the game is started with --telemetry
/// (the patcher makes a desktop shortcut that does that).
///
/// Messages are 'N', u16 type, u32 request id, u16 data size, data, 'L',
/// big-endian. The Bridge sends GetTelemetry (type 5) each poll; the reply
/// (type 6, per the telemetry demo client the game ships in its telemetry
/// folder) carries: state flags (1 in play, 2 braking per the protocol
/// comment — "onboard" in the demo's printout, 4 paused, 8 VR headset on),
/// the rendered frame number (new data when it changes),
/// view mode, coaster index, coaster style, train, car and seat, speed
/// (m/s), position xyz (m), rotation quaternion xyzw, and the g-force
/// vector xyz.
///
/// Every field is mapped. Frame: the quaternion, position and g-force are
/// mapped through `axes` (default x,y,z) and the g-force vector taken as
/// accelerometer-style g in the rider's frame — unverified against the
/// live game (the demo derives Euler angles for a Y-up frame). Motion is
/// emitted in play mode while the frame number advances; pause rides
/// session.paused.
/// </summary>
public sealed class NoLimits2Source : IPolledSource, IGamePatcher
{
    const float Gravity = 9.80665f;
    const ushort GetTelemetry = 5, Telemetry = 6;
    internal const int DataSize = 76;
    static readonly IPEndPoint Server = new(IPAddress.Loopback, 15151);

    readonly byte[] _request = new byte[10];
    readonly byte[] _rx = new byte[1024];
    Socket? _socket;
    uint _requestId;
    AxisMap _axes = AxisMap.Identity;

    OgsChannel _state, _frame, _view, _coaster, _style, _train, _car, _seat, _speed, _inPlay, _flag1, _paused, _vr,
        _gx, _gy, _gz;
    int _lastFrame = int.MinValue;

    public string Id => "nolimits2";
    public string Title => "NoLimits 2 (telemetry server)";
    public string Description =>
        "NoLimits 2 telemetry server (TCP 127.0.0.1:15151), polled. Start the " +
        "game with --telemetry (the patch puts a shortcut on the desktop).";
    public int DefaultPort => 15151;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "nolimits2stm" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("NoLimits 2 Roller Coaster Simulation", "nolimits2stm"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("axes", "game components feeding OGS x,y,z (default x,y,z)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("axes");
        _axes = AxisMap.Parse(o.GetString("axes", "x,y,z"));
    }

    public SourceTransport Transport => SourceTransport.Poll;
    public int PollIntervalMs => 5;
    public int PollBufferSize => DataSize;
    public string PollTarget => "NoLimits 2 telemetry on 127.0.0.1:15151 (start with --telemetry)";

    public bool Connect()
    {
        Disconnect();
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true, ReceiveTimeout = 1000, SendTimeout = 1000,
        };
        try { s.Connect(Server); }
        catch (SocketException) { s.Dispose(); return false; }
        _socket = s;
        return true;
    }

    public void Disconnect()
    {
        _socket?.Dispose();
        _socket = null;
    }

    public int Read(byte[] buffer)
    {
        var s = _socket;
        if (s is null) return -1;
        _request[0] = (byte)'N';
        BinaryPrimitives.WriteUInt16BigEndian(_request.AsSpan(1), GetTelemetry);
        BinaryPrimitives.WriteUInt32BigEndian(_request.AsSpan(3), ++_requestId);
        BinaryPrimitives.WriteUInt16BigEndian(_request.AsSpan(7), 0);
        _request[9] = (byte)'L';
        try
        {
            s.Send(_request);
            // header: 'N', type, id, size
            if (!ReceiveExactly(s, _rx, 0, 9)) return -1;
            if (_rx[0] != (byte)'N') return -1;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(_rx.AsSpan(1));
            int size = BinaryPrimitives.ReadUInt16BigEndian(_rx.AsSpan(7));
            if (size + 10 > _rx.Length || !ReceiveExactly(s, _rx, 9, size + 1)) return -1;
            if (type != Telemetry || size < DataSize) return 0; // an error / other reply
            _rx.AsSpan(9, DataSize).CopyTo(buffer);
            return DataSize;
        }
        catch (SocketException) { return -1; }
    }

    static bool ReceiveExactly(Socket s, byte[] into, int at, int count)
    {
        while (count > 0)
        {
            int n = s.Receive(into, at, count, SocketFlags.None);
            if (n <= 0) return false;
            at += n;
            count -= n;
        }
        return true;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("rider", Ogs.Telemetry.Schema.ObjectType.Vehicle, "NoLimits 2 coaster car");
        session.DeclareGravity(Gravity);
        session.SendChangedChannelsOnly();
        _paused  = session.AddBooleanChannel("session.paused", "Simulation paused");
        session.AlwaysSend(_paused);
        _state   = session.AddChannel("nolimits2.state", "", "State flags: 1 in play, 2 braking/onboard, 4 paused, 8 VR");
        _inPlay  = session.AddBooleanChannel("nolimits2.inPlay", "In play mode");
        _flag1   = session.AddBooleanChannel("nolimits2.stateBit1", "State bit 1: braking (protocol comment) / onboard (demo client)");
        _vr      = session.AddBooleanChannel("nolimits2.vrHeadset", "VR headset on");
        _frame   = session.AddChannel("nolimits2.frame", "", "Rendered frame counter");
        _view    = session.AddChannel("nolimits2.viewMode", "", "View mode");
        _coaster = session.AddChannel("nolimits2.coaster", "", "Current coaster index");
        _style   = session.AddChannel("nolimits2.coasterStyle", "", "Coaster style id");
        _train   = session.AddChannel("nolimits2.train", "", "Current train");
        _car     = session.AddChannel("nolimits2.car", "", "Current car");
        _seat    = session.AddChannel("nolimits2.seat", "", "Current seat");
        _speed   = session.AddChannel("vehicle.speed", "m/s", "Train speed");
        _gx      = session.AddChannel("nolimits2.gforce.x", "", "G-force X (game frame)");
        _gy      = session.AddChannel("nolimits2.gforce.y", "", "G-force Y (game frame)");
        _gz      = session.AddChannel("nolimits2.gforce.z", "", "G-force Z (game frame)");
    }

    public bool AcceptsDatagram(int length) => length >= DataSize;

    static int I(ReadOnlySpan<byte> d, int off) => BinaryPrimitives.ReadInt32BigEndian(d.Slice(off, 4));
    static float F(ReadOnlySpan<byte> d, int off) => BinaryPrimitives.ReadSingleBigEndian(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (d.Length < DataSize) return;
        int state = I(d, 0);
        bool inPlay = (state & 1) != 0, paused = (state & 4) != 0;
        int frame = I(d, 4);
        session.SetChannel(_state, state);
        session.SetChannel(_inPlay, inPlay);
        session.SetChannel(_flag1, (state & 2) != 0);
        session.SetChannel(_paused, paused);
        session.SetChannel(_vr, (state & 8) != 0);
        session.SetChannel(_frame, frame);
        session.SetChannel(_view, I(d, 8));
        session.SetChannel(_coaster, I(d, 12));
        session.SetChannel(_style, I(d, 16));
        session.SetChannel(_train, I(d, 20));
        session.SetChannel(_car, I(d, 24));
        session.SetChannel(_seat, I(d, 28));
        session.SetChannel(_speed, F(d, 32));
        var g = new Vector3(F(d, 64), F(d, 68), F(d, 72));
        session.SetChannel(_gx, g.X);
        session.SetChannel(_gy, g.Y);
        session.SetChannel(_gz, g.Z);
        if (!inPlay || frame == _lastFrame && !paused) return; // nothing new rendered
        _lastFrame = frame;

        var position = _axes.Vector(new Vector3(F(d, 36), F(d, 40), F(d, 44)));
        var q = Quaternion.Normalize(_axes.Rotation(new Quaternion(F(d, 48), F(d, 52), F(d, 56), F(d, 60))));
        session.SetPose(position, q);
        session.SetBodyDynamics(_axes.Vector(g) * Gravity, Vector3.Zero);
        session.Tick(now);
    }

    // ---- IGamePatcher: a desktop shortcut starting the game with --telemetry ----

    public IList<string> PatchGameTelemetry(int port)
    {
        var patched = new List<string>();
        var dir = SteamLibrary.FindGame(new[] { "NoLimits 2 Roller Coaster Simulation", "NoLimits 2" });
        if (dir is null) return patched;
        var exe = System.IO.Path.Combine(dir, "64bit", "nolimits2stm.exe");
        if (!System.IO.File.Exists(exe)) return patched;
        var link = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NoLimits 2 (telemetry).lnk");
        if (ShellShortcut.Create(link, exe, "--telemetry", "NoLimits 2 with the telemetry server on"))
            patched.Add(link);
        return patched;
    }
}
}
