#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// KartKraft UDP telemetry. The game publishes FlatBuffers frames (file
/// identifier "KKFB") described by the developer's open schema
/// (motorsportgames/kartkraft-telemetry, Schema/Frame.fbs); the reader
/// here walks the buffer in place, with no generated code and no
/// allocation. Every field is optional on the wire, and a frame may carry
/// motion, dashboard, or both.
///
/// Motion: pitch / roll / yaw in degrees; local acceleration X forward,
/// Y right, Z up (m/s²); angular velocity in deg/s with X = roll rate,
/// Y = pitch rate, Z = yaw rate. The wheels array runs clockwise from the
/// rear left (RL, FL, FR, RR).
///
/// Output: pose + BodyDynamics + dashboard, wheel-surface and lap
/// channels. Unverified against the live game: the Euler signs (escape
/// hatches `flipYaw/flipPitch/flipRoll`) and whether the acceleration
/// already carries gravity (`gravIncluded`; default: it does not, so the
/// reaction is added from the pose).
/// </summary>
public sealed class KartKraftSource : ILegacySource
{
    // ---- FlatBuffers table slots (declaration order in Frame.fbs) ----
    const int FrameMotion = 1, FrameDash = 2, FrameSession = 3, FrameVehicleConfig = 4,
              FrameTrackConfig = 5, FrameSessionConfig = 6;

    static readonly string[] WheelNames = { "rl", "fl", "fr", "rr" };
    const int Wheels = 4;

    double _sy = 1, _sp = 1, _sr = 1;
    bool _gravIncluded;

    OgsChannel _speed, _rpm, _steer, _throttle, _brake, _gear, _position,
               _bestLap, _currentLap, _lastLap, _lapCount, _sector,
               _rpmLimit, _rpmMax, _gearMax, _elapsed, _traction,
               _timeLimit, _lapLimit, _trackLength, _sectors,
               _worldVx, _worldVy, _worldVz, _worldPx, _worldPy, _worldPz;
    OgsChannel _track, _sessionName;
    readonly OgsChannel[] _wheelSurface = new OgsChannel[Wheels];
    readonly OgsChannel[] _wheelSlip = new OgsChannel[Wheels];

    public string Id => "kartkraft";
    public string Title => "KartKraft";
    public string Description =>
        "KartKraft FlatBuffers UDP (default port 5000). In-game: Settings → " +
        "Telemetry (UDP) → add this machine's IP and port, standard output ON; " +
        "or add the OutputEndpoints lines to Game.ini.";
    public int DefaultPort => 5000;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "project_k" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("KartKraft", "project_k"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",      "negate yaw Euler sign (bool, default false)"),
        ("flipPitch",    "negate pitch Euler sign (bool, default false)"),
        ("flipRoll",     "negate roll Euler sign (bool, default false)"),
        ("gravIncluded", "acceleration already includes gravity reaction (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "gravIncluded");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _gravIncluded = o.GetBool("gravIncluded", false);
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("kart", Ogs.Telemetry.Schema.ObjectType.Vehicle, "KartKraft kart");
        _speed    = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        _rpm      = session.AddChannel("engine.rpm", "rpm", "Engine rpm");
        _rpmMax   = session.AddChannel("engine.rpmMax", "rpm", "Rpm to display as the dial maximum");
        _rpmLimit = session.AddChannel("engine.rpmLimit", "rpm", "Rev limiter, where one applies");
        _throttle = session.AddChannel("engine.throttle", "", "Throttle input 0..1");
        _brake    = session.AddChannel("brake.input", "", "Brake input 0..1");
        _steer    = session.AddChannel("kartkraft.steer", "deg", "Steering angle");
        _gear     = session.AddChannel("transmission.gear", "", "Current gear (0 neutral, -1 reverse)");
        _gearMax  = session.AddChannel("transmission.gearMax", "", "Highest gear (1 for non-geared karts)");
        _position = session.AddChannel("race.position", "", "Position in the session, 1 = first");
        _bestLap    = session.AddChannel("lap.bestTime", "s", "Best lap time");
        _currentLap = session.AddChannel("lap.currentTime", "s", "Current lap time");
        _lastLap    = session.AddChannel("lap.lastTime", "s", "Last completed lap time");
        _lapCount   = session.AddChannel("lap.number", "", "Current lap (0 = before the first lap)");
        _sector     = session.AddChannel("lap.sector", "", "Current sector (1-based, 0 = none)");
        _elapsed    = session.AddChannel("session.timeElapsed", "s", "Session time elapsed");
        _timeLimit  = session.AddChannel("session.timeLimit", "min", "Session time limit (0 = none)");
        _lapLimit   = session.AddChannel("session.lapLimit", "", "Session lap limit (0 = none)");
        _trackLength = session.AddChannel("track.length", "m", "Track length");
        _sectors    = session.AddChannel("track.sectors", "", "Sector count");
        _traction   = session.AddChannel("kartkraft.tractionLoss", "deg", "Traction loss (mean rear slip angle)");
        _worldVx = session.AddChannel("kartkraft.worldVelocity.x", "m/s", "World-space velocity X");
        _worldVy = session.AddChannel("kartkraft.worldVelocity.y", "m/s", "World-space velocity Y");
        _worldVz = session.AddChannel("kartkraft.worldVelocity.z", "m/s", "World-space velocity Z (vertical)");
        _worldPx = session.AddChannel("kartkraft.worldPosition.x", "cm", "World position X");
        _worldPy = session.AddChannel("kartkraft.worldPosition.y", "cm", "World position Y");
        _worldPz = session.AddChannel("kartkraft.worldPosition.z", "cm", "World position Z (height)");
        for (int i = 0; i < Wheels; i++)
        {
            _wheelSurface[i] = session.AddChannel($"kartkraft.wheel.{WheelNames[i]}.surface", "",
                "Surface under the wheel (0 none, 1 asphalt, 2 grass, 3 gravel, 4 kerb, 5 sand, 6 tyre, 7 wire fence, 8 concrete, 9 plastic, 10 polished concrete)");
            _wheelSlip[i] = session.AddChannel($"kartkraft.wheel.{WheelNames[i]}.slipAngle", "deg",
                "Wheel slip angle, 0 = no slip");
        }
        _track       = session.AddSessionText("track.name", "Track name");
        _sessionName = session.AddSessionText("session.name", "Session name (game mode)");
    }

    // identifier "KKFB" sits at bytes 4..7 of a finished buffer
    public bool AcceptsDatagram(int length) => length >= 16;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (d.Length < 16 || d[4] != (byte)'K' || d[5] != (byte)'K' || d[6] != (byte)'F' || d[7] != (byte)'B')
            return;
        var frame = new Fb(d, Fb.RootPos(d));
        if (!frame.Valid) return;

        bool any = false;

        var motion = frame.Table(FrameMotion);
        if (motion.Valid)
        {
            any = true;
            const float Deg = MathF.PI / 180f;
            session.SetPose(null, OgsMath.EulerToQuaternion(
                _sy * motion.Float(2) * Deg, _sp * motion.Float(0) * Deg, _sr * motion.Float(1) * Deg));
            // local acceleration (fwd, right, up) -> OGS body frame (right, up, fwd)
            var accel = new Vector3(motion.Float(4), motion.Float(5), motion.Float(3));
            if (!_gravIncluded)
            {
                var q = OgsMath.EulerToQuaternion(
                    _sy * motion.Float(2) * Deg, _sp * motion.Float(0) * Deg, _sr * motion.Float(1) * Deg);
                accel = OgsMath.SpecificForceFromLinear(accel, q);
            }
            // angular velocity: X roll, Y pitch, Z yaw -> OGS (pitch, yaw, roll)
            session.SetBodyDynamics(accel, new Vector3(
                (float)(_sp * motion.Float(11) * Deg),
                (float)(_sy * motion.Float(12) * Deg),
                (float)(_sr * motion.Float(10) * Deg)));

            session.SetChannel(_traction, motion.Float(6));
            session.SetChannel(_worldVx, motion.Float(14));
            session.SetChannel(_worldVy, motion.Float(15));
            session.SetChannel(_worldVz, motion.Float(16));
            session.SetChannel(_worldPx, motion.Float(17));
            session.SetChannel(_worldPy, motion.Float(18));
            session.SetChannel(_worldPz, motion.Float(19));

            var wheels = motion.Vector(13);
            for (int i = 0; i < Wheels && i < wheels.Count; i++)
            {
                var w = wheels.Table(i);
                session.SetChannel(_wheelSurface[i], w.Byte(0));
                session.SetChannel(_wheelSlip[i], w.Float(1));
            }
        }

        var dash = frame.Table(FrameDash);
        if (dash.Valid)
        {
            any = true;
            session.SetChannel(_speed, dash.Float(0));
            session.SetChannel(_rpm, dash.Float(1));
            session.SetChannel(_steer, dash.Float(2));
            session.SetChannel(_throttle, dash.Float(3));
            session.SetChannel(_brake, dash.Float(4));
            session.SetChannel(_gear, (sbyte)dash.Byte(5));
            session.SetChannel(_position, (sbyte)dash.Byte(6));
            session.SetChannel(_bestLap, dash.Float(7));
            session.SetChannel(_currentLap, dash.Float(8));
            session.SetChannel(_lastLap, dash.Float(9));
            session.SetChannel(_lapCount, dash.UShort(10));
            session.SetChannel(_sector, dash.UShort(11));
        }

        var sess = frame.Table(FrameSession);
        if (sess.Valid) session.SetChannel(_elapsed, sess.Float(4));

        var vc = frame.Table(FrameVehicleConfig);
        if (vc.Valid)
        {
            session.SetChannel(_rpmLimit, vc.Float(0));
            session.SetChannel(_rpmMax, vc.Float(1));
            session.SetChannel(_gearMax, (sbyte)vc.Byte(2));
        }

        var tc = frame.Table(FrameTrackConfig);
        if (tc.Valid)
        {
            session.SetChannel(_sectors, tc.Byte(1));
            session.SetChannel(_trackLength, tc.Float(2));
            var name = tc.String(0);
            if (name.Length > 0) session.SetSessionText(_track, name);
        }

        var sc = frame.Table(FrameSessionConfig);
        if (sc.Valid)
        {
            session.SetChannel(_timeLimit, sc.UInt(1));
            session.SetChannel(_lapLimit, sc.UInt(2));
            var name = sc.String(0);
            if (name.Length > 0) session.SetSessionText(_sessionName, name);
        }

        if (any) session.Tick(now);
    }

    /// <summary>Bounds-checked, allocation-free view of one FlatBuffers
    /// table. A missing field, a short buffer or a bad offset reads as
    /// zero / empty rather than throwing: UDP delivers whatever arrives.</summary>
    readonly ref struct Fb
    {
        readonly ReadOnlySpan<byte> _b;
        readonly int _pos;

        public Fb(ReadOnlySpan<byte> b, int pos) { _b = b; _pos = pos; }

        public static int RootPos(ReadOnlySpan<byte> b) => ReadInt(b, 0, out var v) ? v : -1;

        public bool Valid => _pos >= 0 && _pos + 4 <= _b.Length;

        static bool ReadInt(ReadOnlySpan<byte> b, int at, out int v)
        {
            if (at < 0 || at + 4 > b.Length) { v = 0; return false; }
            v = MemoryMarshal.Read<int>(b.Slice(at, 4));
            return true;
        }

        /// <summary>Absolute position of field <paramref name="slot"/>, or -1.</summary>
        int Field(int slot, int size)
        {
            if (!Valid) return -1;
            if (!ReadInt(_b, _pos, out var back)) return -1;
            int vt = _pos - back;
            if (vt < 0 || vt + 4 > _b.Length) return -1;
            int vsize = MemoryMarshal.Read<ushort>(_b.Slice(vt, 2));
            int entry = 4 + slot * 2;
            if (entry + 2 > vsize || vt + entry + 2 > _b.Length) return -1;
            int off = MemoryMarshal.Read<ushort>(_b.Slice(vt + entry, 2));
            if (off == 0) return -1;
            int at = _pos + off;
            return at + size <= _b.Length ? at : -1;
        }

        public float Float(int slot)
        {
            int at = Field(slot, 4);
            if (at < 0) return 0;
            float f = MemoryMarshal.Read<float>(_b.Slice(at, 4));
            return float.IsFinite(f) ? f : 0;
        }

        public byte Byte(int slot) { int at = Field(slot, 1); return at < 0 ? (byte)0 : _b[at]; }
        public ushort UShort(int slot) { int at = Field(slot, 2); return at < 0 ? (ushort)0 : MemoryMarshal.Read<ushort>(_b.Slice(at, 2)); }
        public uint UInt(int slot) { int at = Field(slot, 4); return at < 0 ? 0 : MemoryMarshal.Read<uint>(_b.Slice(at, 4)); }

        public Fb Table(int slot)
        {
            int at = Field(slot, 4);
            if (at < 0) return new Fb(_b, -1);
            return new Fb(_b, at + MemoryMarshal.Read<int>(_b.Slice(at, 4)));
        }

        public ReadOnlySpan<byte> String(int slot)
        {
            int at = Field(slot, 4);
            if (at < 0) return default;
            int s = at + MemoryMarshal.Read<int>(_b.Slice(at, 4));
            if (!ReadInt(_b, s, out var len) || len < 0 || s + 4 + len > _b.Length) return default;
            return _b.Slice(s + 4, len);
        }

        public Vec Vector(int slot)
        {
            int at = Field(slot, 4);
            if (at < 0) return new Vec(_b, 0, 0);
            int v = at + MemoryMarshal.Read<int>(_b.Slice(at, 4));
            if (!ReadInt(_b, v, out var n) || n < 0 || v + 4 + (long)n * 4 > _b.Length) return new Vec(_b, 0, 0);
            return new Vec(_b, v + 4, n);
        }
    }

    /// <summary>A vector of tables (elements are 4-byte relative offsets).</summary>
    readonly ref struct Vec
    {
        readonly ReadOnlySpan<byte> _b;
        readonly int _start;
        public int Count { get; }

        public Vec(ReadOnlySpan<byte> b, int start, int count) { _b = b; _start = start; Count = count; }

        public Fb Table(int i)
        {
            int at = _start + i * 4;
            return new Fb(_b, at + MemoryMarshal.Read<int>(_b.Slice(at, 4)));
        }
    }
}
}
