#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsaTsBridge.Sources
{
/// <summary>
/// Generic Codemasters EGO telemetry source — one format, several games.
/// Covers Dirt Rally 2.0 (little-endian) and DiRT 4 (big-endian); byte
/// order is auto-detected from the first sane datagram and sticky for the
/// session. Games confirmed to emit this format join the process watch
/// list — no per-game code.
///
/// Packet:
/// speed m/s @28, velocity as world direction cosines @32/36/40, car
/// right vector @44-52, forward vector @56-64, suspension positions
/// (bl,br,fl,fr) @68-80, suspension velocities @84-96, rear wheel speeds
/// @100/104, steer @120, g lat/long @136/140 (G), rpm @148 (÷30 for rpm).
///
/// Output: pose + world kinematics (the packet measures world velocity
/// natively); per-wheel suspension ride the four WheelState drive points;
/// g-forces and steer go out as channels.
/// </summary>
public sealed class EgoSource : ILegacySource
{
    OgsaChannel _rpm, _speed, _gLat, _gLong, _steer;
    double _sy = 1, _sp = 1, _sr = 1, _rpmScale = 30;
    double _wheelbase = 2.6, _track = 1.6, _radius = 0.34;
    int? _bigEndian; // detected per session, sticky

    public string Id => "ego";
    public string Title => "EGO telemetry (Dirt Rally 2 / DiRT 4)";
    public string Description =>
        "Codemasters EGO UDP telemetry (default port 20777). Enable in-game " +
        "telemetry (Dirt Rally 2: hardwaresettings XML 'udp' enabled; the " +
        "game sends to this machine). Byte order auto-detected per session.";
    public int DefaultPort => 20777;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "dirtrally2", "dirt4" };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("endian",    "force byte order: auto (default) | le | be"),
        ("flipYaw",   "negate yaw Euler sign (bool, default false)"),
        ("flipPitch", "negate pitch Euler sign (bool, default false)"),
        ("flipRoll",  "negate roll Euler sign (bool, default false)"),
        ("rpmScale",  "rpm field divisor (default 30)"),
        ("wheelbase", "wheel separation fore/aft in metres (default 2.6)"),
        ("track",     "wheel separation left/right in metres (default 1.6)"),
        ("radius",    "tyre rolling radius in metres (default 0.34)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("endian", "flipYaw", "flipPitch", "flipRoll",
                      "rpmScale", "wheelbase", "track", "radius");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _rpmScale = o.GetDouble("rpmScale", 30);
        _wheelbase = o.GetDouble("wheelbase", 2.6);
        _track = o.GetDouble("track", 1.6);
        _radius = o.GetDouble("radius", 0.34);
        _bigEndian = o.GetString("endian", "auto") switch
        {
            "le" => 0, "be" => 1, _ => null,
        };
    }

    public void Declare(OgsaSession session)
    {
        session.DeclareObject("car", CapnpGen.ObjectType.vehicle, "EGO rally car");
        float hz = (float)(_wheelbase * 0.5), hx = (float)(_track * 0.5);
        session.AddWheel("wheel.fl", new Vector3(-hx, 0, hz), (float)_radius, true, true);
        session.AddWheel("wheel.fr", new Vector3(+hx, 0, hz), (float)_radius, true, true);
        session.AddWheel("wheel.rl", new Vector3(-hx, 0, -hz), (float)_radius, true, false);
        session.AddWheel("wheel.rr", new Vector3(+hx, 0, -hz), (float)_radius, true, false);

        _rpm    = session.AddChannel("engine.rpm", "rpm", "Crankshaft rpm");
        _speed  = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        _gLat   = session.AddChannel("ego.gForceLateral", "", "Lateral g, body frame");
        _gLong  = session.AddChannel("ego.gForceLongitudinal", "", "Longitudinal g, body frame");
        _steer  = session.AddChannel("ego.steer", "", "Raw steering input");
    }

    public bool AcceptsDatagram(int length) => length >= 152;

    public void Translate(ReadOnlySpan<byte> d, OgsaSession session, double now)
    {
        bool be = DetectEndian(d);


        float speed = F(d, 28, be);
        var fwd = OgsaMath.RhuYUpWorldToOgsa(F(d, 56, be), F(d, 60, be), F(d, 64, be));
        var right = OgsaMath.RhuYUpWorldToOgsa(F(d, 44, be), F(d, 48, be), F(d, 52, be));
        var dir = OgsaMath.RhuYUpWorldToOgsa(F(d, 32, be), F(d, 36, be), F(d, 40, be));

        var (yaw, pitch, roll) = OgsaMath.EulerFromForwardRight(fwd, right);
        session.SetPose(null, OgsaMath.EulerToQuaternion(_sy * yaw, _sp * pitch, _sr * roll));
        session.SetKinematics(dir * speed, default);

        session.SetWheel(0, 0f, F(d, 76, be), CapnpGen.ContactMedium.asphalt); // packet order bl,br,fl,fr
        session.SetWheel(1, 0f, F(d, 80, be), CapnpGen.ContactMedium.asphalt);
        session.SetWheel(2, 0f, F(d, 68, be), CapnpGen.ContactMedium.asphalt);
        session.SetWheel(3, 0f, F(d, 72, be), CapnpGen.ContactMedium.asphalt);

        session.SetChannel(_rpm, F(d, 148, be) / _rpmScale);
        session.SetChannel(_speed, speed);
        session.SetChannel(_gLat, F(d, 136, be));
        session.SetChannel(_gLong, F(d, 140, be));
        session.SetChannel(_steer, F(d, 120, be));

        session.Tick(now);
    }

    static float F(ReadOnlySpan<byte> d, int off, bool be) => be
        ? BitConverter.Int32BitsToSingle(
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(d.Slice(off, 4)))
        : MemoryMarshal.Read<float>(d.Slice(off, 4));

    bool DetectEndian(ReadOnlySpan<byte> d)
    {
        if (_bigEndian is { } forced) return forced != 0;
        // Speed alone can't discriminate: a subnormal misread also passes
        // [0,150]. The forward vector is always ~unit length, so its
        // magnitude separates a valid reading from byte-swapped garbage.
        static bool Plausible(ReadOnlySpan<byte> dd)
        {
            float s = MemoryMarshal.Read<float>(dd.Slice(28, 4));
            float fx = MemoryMarshal.Read<float>(dd.Slice(56, 4));
            float fy = MemoryMarshal.Read<float>(dd.Slice(60, 4));
            float fz = MemoryMarshal.Read<float>(dd.Slice(64, 4));
            float len = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
            return s is >= 0f and <= 150f && len is >= 0.8f and <= 1.2f;
        }
        static bool PlausibleBe(ReadOnlySpan<byte> dd)
        {
            float s = BitConverter.Int32BitsToSingle(
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(dd.Slice(28, 4)));
            float fx = BitConverter.Int32BitsToSingle(
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(dd.Slice(56, 4)));
            float fy = BitConverter.Int32BitsToSingle(
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(dd.Slice(60, 4)));
            float fz = BitConverter.Int32BitsToSingle(
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(dd.Slice(64, 4)));
            float len = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
            return s is >= 0f and <= 150f && len is >= 0.8f and <= 1.2f;
        }
        bool be = !Plausible(d) && PlausibleBe(d);
        _bigEndian = be ? 1 : 0;
        return be;
    }
}
}
