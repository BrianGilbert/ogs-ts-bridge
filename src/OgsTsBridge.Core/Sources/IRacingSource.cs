#nullable enable
using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace OgsTsBridge.Sources
{
/// <summary>
/// iRacing irsdk shared memory (Local\IRSDKMemMapFileName). The page is
/// self-describing: a header (version, status, tick rate, session-info
/// update/length/offset, variable count, variable-header offset, buffer
/// count and length, four rotating buffers {tickCount, offset}), then 144-byte
/// variable headers (type, offset, count, name, description, unit), the
/// session-info YAML and the rotating data buffers.
///
/// Every variable the header lists is mapped except the CarIdx* arrays
/// (the other cars — the session carries one object), each as
/// "iracing.&lt;Name&gt;" (".&lt;i&gt;" per array element) with the irsdk unit
/// converted to C-3 where it is known (m/s, m/s^2, rad, rad/s, revs/min,
/// C, kPa, bar, l, kg/h, l/h, N*m, deg, %); variables with any other unit
/// are published raw and say so. Well-known variables also get their
/// standard names (engine.rpm, transmission.gear, vehicle.speed,
/// engine.throttle, brakes.pedal, ctrl.steering.angle, aids.abs,
/// position.lat/lon/altMsl, session.paused). The session-info YAML gives
/// the track (location) and the player's car (type label).
///
/// Gather picks the newest data buffer by tick count, copies it, and drops
/// a copy whose tick count moved meanwhile (torn) or that is not new; a
/// changed variable table or session-info update redeclares.
///
/// Frame: irsdk car axes x forward, y left, z up (right-handed) → OGS
/// body (−y, z, x); rates (−PitchRate, −YawRate, RollRate); attitude yaw
/// = −Yaw, pitch and roll direct; Lat/Long/VertAccel taken as
/// accelerometer-style (gravity included) — signs unverified against the
/// live sim (flip options exist). Shock deflections are the wheel
/// compressions.
/// </summary>
public sealed class IRacingSource : ILegacySource, ISharedMemoryGather
{
    const int HeaderSize = 112, VarHeaderSize = 144;
    internal const int MaxBuffer = 1 << 16;

    sealed record Var(string Name, int Type, int Offset, int Count, string Unit, string Description);

    readonly List<Var> _vars = new();
    readonly List<(int Offset, int Type, OgsChannel Channel, double Scale, bool Boolean)> _map = new();
    int _layoutStamp = -1, _sessionUpdate = -1, _lastTick = -1;
    bool _layoutChanged, _sessionChanged;
    string _track = "", _car = "";
    int _yaw = -1, _pitch = -1, _roll = -1, _yawRate = -1, _pitchRate = -1, _rollRate = -1,
        _lat = -1, _long = -1, _vert = -1, _onTrack = -1, _replay = -1;
    readonly int[] _shock = { -1, -1, -1, -1 };
    readonly int[] _wheel = new int[4];
    OgsChannel _paused;
    bool _truncated; // variable set cut to the session's channel limit (tests read it)
    double _sy = 1, _sp = 1, _sr = 1;

    public string Id => "iracing";
    public string Title => "iRacing (irsdk shared memory)";
    public string Description =>
        "iRacing telemetry shared memory (Local\\IRSDKMemMapFileName), " +
        "2 ms poll; published whenever the sim runs.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "iRacingSim64DX11" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("iRacing", "iRacingSim64DX11"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate yaw + yaw-rate sign (bool, default false)"),
        ("flipPitch", "negate pitch + pitch-rate sign (bool, default false)"),
        ("flipRoll",  "negate roll + roll-rate sign (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
    }

    public SourceTransport Transport => SourceTransport.SharedMemory;
    public string SharedMemoryName => "Local\\IRSDKMemMapFileName";
    public int SharedMemorySize => MaxBuffer;
    public int PollIntervalMs => 2;
    public IReadOnlyList<string> AuxiliaryMapNames => Array.Empty<string>();

    public int Gather(MemoryMappedViewAccessor primary, IReadOnlyList<MemoryMappedViewAccessor?> auxiliary, byte[] buffer)
    {
        int status = primary.ReadInt32(4);
        if ((status & 1) == 0) return 0; // not connected (no session)
        int sessionUpdate = primary.ReadInt32(12);
        int numVars = primary.ReadInt32(24), varHeaderOffset = primary.ReadInt32(28);
        int numBuf = Math.Clamp(primary.ReadInt32(32), 1, 4), bufLen = primary.ReadInt32(36);
        if (bufLen <= 0 || bufLen > buffer.Length) return 0;

        int stamp = numVars * 31 + varHeaderOffset;
        if (stamp != _layoutStamp)
        {
            ReadVarHeaders(primary, numVars, varHeaderOffset); // cold
            _layoutStamp = stamp;
            _layoutChanged = true;
        }
        if (sessionUpdate != _sessionUpdate)
        {
            ReadSessionInfo(primary, primary.ReadInt32(20), primary.ReadInt32(16)); // cold
            _sessionUpdate = sessionUpdate;
            _sessionChanged = true;
        }

        int best = 0, bestTick = int.MinValue;
        for (int b = 0; b < numBuf; b++)
        {
            int tick = primary.ReadInt32(48 + b * 16);
            if (tick > bestTick) { bestTick = tick; best = b; }
        }
        if (bestTick == _lastTick && !_layoutChanged && !_sessionChanged) return 0;
        int offset = primary.ReadInt32(48 + best * 16 + 4);
        primary.ReadArray(offset, buffer, 0, bufLen);
        if (primary.ReadInt32(48 + best * 16) != bestTick) return 0; // overwritten mid-copy
        _lastTick = bestTick;
        return bufLen;
    }

    void ReadVarHeaders(MemoryMappedViewAccessor view, int count, int at)
    {
        _vars.Clear();
        var raw = new byte[VarHeaderSize];
        for (int i = 0; i < count; i++)
        {
            view.ReadArray(at + i * VarHeaderSize, raw, 0, VarHeaderSize);
            _vars.Add(new Var(
                Str(raw, 16, 32), BitConverter.ToInt32(raw, 0), BitConverter.ToInt32(raw, 4),
                BitConverter.ToInt32(raw, 8), Str(raw, 112, 32), Str(raw, 48, 64)));
        }
    }

    void ReadSessionInfo(MemoryMappedViewAccessor view, int offset, int length)
    {
        if (length <= 0 || length > 4 << 20) return;
        var raw = new byte[length];
        view.ReadArray(offset, raw, 0, length);
        var yaml = Encoding.Latin1.GetString(raw);
        _track = YamlValue(yaml, "TrackDisplayName:") ?? _track;
        // the player's car: DriverCarIdx, then that list entry ("- CarIdx: N";
        // "DriverCarIdx: N" must not match) and its CarScreenName
        var idx = YamlValue(yaml, "DriverCarIdx:");
        int at = idx is null ? -1 : yaml.IndexOf("- CarIdx: " + idx + "\n", StringComparison.Ordinal);
        if (at < 0 && idx is not null) at = yaml.IndexOf("- CarIdx: " + idx + "\r", StringComparison.Ordinal);
        if (at >= 0) _car = YamlValue(yaml[at..], "CarScreenName:") ?? _car;
    }

    static string? YamlValue(string yaml, string key)
    {
        int at = yaml.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = at + key.Length;
        int end = yaml.IndexOfAny(new[] { '\r', '\n' }, start);
        return yaml[start..(end < 0 ? yaml.Length : end)].Trim();
    }

    static string Str(byte[] b, int off, int len)
    {
        int n = Array.IndexOf(b, (byte)0, off, len);
        return Encoding.ASCII.GetString(b, off, (n < 0 ? off + len : n) - off);
    }

    /// <summary>irsdk unit → C-3 unit and factor; null for unknown units.</summary>
    static (string Unit, double Scale)? Convert(string unit) => unit switch
    {
        "m/s" => ("m/s", 1), "m/s^2" => ("m/s2", 1), "m" => ("m", 1), "s" => ("s", 1),
        "rad" => ("rad", 1), "rad/s" => ("rad/s", 1), "deg" => ("rad", Math.PI / 180),
        "revs/min" => ("rpm", 1), "C" => ("degC", 1), "kPa" => ("Pa", 1000), "bar" => ("Pa", 1e5),
        "l" => ("m3", 0.001), "l/h" => ("m3/s", 0.001 / 3600), "kg/h" => ("kg/s", 1 / 3600.0),
        "kg" => ("kg", 1), "N*m" => ("Nm", 1), "V" => ("V", 1), "W" => ("W", 1), "J" => ("J", 1),
        "%" => ("", 1), "" => ("", 1), "Hz" => ("Hz", 1), "N" => ("N", 1),
        _ => null,
    };

    static readonly Dictionary<string, (string Channel, string Unit, double Scale)> Standard = new()
    {
        ["RPM"] = ("engine.rpm", "rpm", 1), ["Gear"] = ("transmission.gear", "", 1),
        ["Speed"] = ("vehicle.speed", "m/s", 1), ["Throttle"] = ("engine.throttle", "", 1),
        ["Brake"] = ("brakes.pedal", "", 1),
        ["SteeringWheelAngle"] = ("ctrl.steering.angle", "rad", -1), // irsdk + = counter-clockwise
        ["Lat"] = ("position.lat", "rad", Math.PI / 180), ["Lon"] = ("position.lon", "rad", Math.PI / 180),
        ["Alt"] = ("position.altMsl", "m", 1),
    };

    public void Declare(OgsSession session)
    {
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "iRacing car");
        session.SendChangedChannelsOnly();
        _paused = session.AddBooleanChannel("session.paused", "Not driving (off track or replay)");
        session.AlwaysSend(_paused);
        string[] corner = { "lf", "rf", "lr", "rr" };
        for (int w = 0; w < 4; w++)
            _wheel[w] = session.AddWheel("wheel." + corner[w], Vector3.Zero, 0.33f,
                driven: false, steered: w < 2, suspension: false);

        _map.Clear();
        _truncated = false;
        _yaw = _pitch = _roll = _yawRate = _pitchRate = _rollRate = _lat = _long = _vert = _onTrack = _replay = -1;
        Array.Fill(_shock, -1);
        int budget = OgsSession.MaxChannels - 8;
        foreach (var v in _vars)
        {
            if (v.Name.StartsWith("CarIdx", StringComparison.Ordinal) || v.Type == 0) continue;
            Remember(v);
            bool boolean = v.Type == 1;
            var unit = Convert(v.Unit);
            if (Standard.TryGetValue(v.Name, out var std) && v.Count == 1)
            {
                if (--budget < 0) { _truncated = true; break; }
                _map.Add((v.Offset, v.Type, session.AddChannel(std.Channel, std.Unit, v.Description), std.Scale, false));
            }
            if (v.Name == "BrakeABSactive")
            {
                if (--budget < 0) { _truncated = true; break; }
                _map.Add((v.Offset, v.Type, session.AddBooleanChannel("aids.abs", v.Description), 1, true));
            }
            for (int e = 0; e < v.Count; e++)
            {
                if (--budget < 0) { _truncated = true; break; }
                var name = "iracing." + v.Name + (v.Count > 1 ? "." + e : "");
                int size = v.Type == 5 ? 8 : v.Type == 1 ? 1 : 4;
                var channel = boolean
                    ? session.AddBooleanChannel(name, v.Description)
                    : unit is { } u
                        ? session.AddChannel(name, u.Unit, v.Description)
                        : session.AddChannel(name, "", $"{v.Description} (raw, irsdk unit '{v.Unit}')");
                _map.Add((v.Offset + e * size, v.Type, channel, unit?.Scale ?? 1, boolean));
            }
            if (_truncated) break;
        }
    }

    void Remember(Var v)
    {
        switch (v.Name)
        {
            case "Yaw": _yaw = v.Offset; break;
            case "Pitch": _pitch = v.Offset; break;
            case "Roll": _roll = v.Offset; break;
            case "YawRate": _yawRate = v.Offset; break;
            case "PitchRate": _pitchRate = v.Offset; break;
            case "RollRate": _rollRate = v.Offset; break;
            case "LatAccel": _lat = v.Offset; break;
            case "LongAccel": _long = v.Offset; break;
            case "VertAccel": _vert = v.Offset; break;
            case "IsOnTrack": _onTrack = v.Offset; break;
            case "IsReplayPlaying": _replay = v.Offset; break;
            case "LFshockDefl": _shock[0] = v.Offset; break;
            case "RFshockDefl": _shock[1] = v.Offset; break;
            case "LRshockDefl": _shock[2] = v.Offset; break;
            case "RRshockDefl": _shock[3] = v.Offset; break;
        }
    }

    internal bool Truncated => _truncated;

    public bool AcceptsDatagram(int length) => length > 0;

    static float F(ReadOnlySpan<byte> d, int off) => off >= 0 && off + 4 <= d.Length ? MemoryMarshal.Read<float>(d.Slice(off, 4)) : 0f;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (_layoutChanged)
        {
            _layoutChanged = false;
            session.BeginRedeclare();
            Declare(session);
            session.CommitRedeclare(now);
        }
        if (_sessionChanged)
        {
            _sessionChanged = false;
            session.SetLocation(Encoding.UTF8.GetBytes(_track));
            session.SetTypeLabel(Encoding.UTF8.GetBytes(_car));
        }

        foreach (var (offset, type, channel, scale, boolean) in _map)
        {
            if (offset < 0 || offset + (type == 5 ? 8 : 4) > d.Length && type != 1) continue;
            switch (type)
            {
                case 1: if (offset < d.Length) session.SetChannel(channel, d[offset] != 0); break;
                case 2: session.SetChannel(channel, MemoryMarshal.Read<int>(d.Slice(offset, 4)) * scale); break;
                case 3: session.SetChannel(channel, MemoryMarshal.Read<uint>(d.Slice(offset, 4))); break;
                case 4: session.SetChannel(channel, MemoryMarshal.Read<float>(d.Slice(offset, 4)) * scale); break;
                case 5: session.SetChannel(channel, MemoryMarshal.Read<double>(d.Slice(offset, 8)) * scale); break;
            }
        }

        bool onTrack = _onTrack < 0 || d[_onTrack] != 0;
        bool replay = _replay >= 0 && d[_replay] != 0;
        session.SetChannel(_paused, !onTrack || replay);
        if (_yaw < 0) return;

        session.SetPose(null, OgsMath.EulerToQuaternion(-_sy * F(d, _yaw), _sp * F(d, _pitch), _sr * F(d, _roll)));
        session.SetBodyDynamics(
            new Vector3(-F(d, _lat), F(d, _vert), F(d, _long)),
            new Vector3((float)(-_sp * F(d, _pitchRate)), (float)(-_sy * F(d, _yawRate)), (float)(_sr * F(d, _rollRate))));
        for (int w = 0; w < 4; w++)
            if (_shock[w] >= 0)
                session.SetWheel(_wheel[w], 0f, F(d, _shock[w]), Ogs.Telemetry.Schema.ContactMedium.Asphalt);

        session.Tick(now);
    }
}
}
