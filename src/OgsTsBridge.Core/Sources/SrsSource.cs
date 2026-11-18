#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// The SimRacingStudio motion-output family that Unreal VR titles emit
/// through a "Motion Platform Linker" plugin (default UDP port 33001):
/// G-Rebels, Whitewater VR and Kayak VR: Mirage.
///
/// Two layouts share the port, told apart by the first bytes:
/// - the "api" packet (version 102, 236 bytes, little-endian): magic
///   "api\0" @0, version @4, game name char[50] @8, vehicle name
///   char[50] @0x3A, location char[50] @0x6C, then floats from @0xA0:
///   speed, rpm, maxRpm, gear (int), pitch, roll, yaw (degrees), lateral
///   velocity, lateral / vertical / longitudinal acceleration, four
///   suspension values, four wheel-terrain codes (int);
/// - the plain motion block: a variable-length header followed by eight
///   floats in the last 32 bytes (heave, sway, surge, yaw, pitch, roll and
///   two extras).
///
/// Neither carries angular rates, so the source derives them from the
/// change in the Euler angles between packets (it needs two packets, and
/// skips a gap longer than a quarter second). Output: pose + body
/// dynamics (sway, heave and surge as the specific force) + channels.
///
/// Unverified against the live games: the acceleration unit (default
/// m/s², `accUnit=g` for g), whether it already carries gravity
/// (`gravIncluded`), the plain block's angle unit (`euler=deg|rad`,
/// default deg) and every rotation sign (flip options).
/// </summary>
public sealed class SrsSource : ILegacySource
{
    const int ApiSize = 0xEC;
    const double MaxRateGap = 0.25;

    double _sy = 1, _sp = 1, _sr = 1, _accScale = 1, _angle = Math.PI / 180;
    bool _gravIncluded;

    // rate derivation (touched only on the forwarder thread)
    double _prevNow = -1, _prevYaw, _prevPitch, _prevRoll;

    OgsChannel _speed, _rpm, _rpmMax, _gear, _latVel;
    OgsChannel _susp0, _susp1, _susp2, _susp3, _terrain0, _terrain1, _terrain2, _terrain3;
    OgsChannel _extra1, _extra2, _apiVersion;

    public string Id => "srs";
    public string Title => "SimRacingStudio motion output (G-Rebels / Whitewater VR / Kayak VR)";
    public string Description =>
        "The \"api\" / motion-block UDP the Unreal VR titles send through the Motion " +
        "Platform Linker (default port 33001), the same output SimRacingStudio reads.";
    public int DefaultPort => 33001;

    public IReadOnlyList<string> ProcessNames { get; } = new[]
    {
        "G_Rebels-Win64-Shipping", "WhitewaterVR-Win64-Shipping", "KayakVR-Win64-Shipping",
    };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("G-Rebels", "G_Rebels-Win64-Shipping"),
        new LegacyGame("Whitewater VR", "WhitewaterVR-Win64-Shipping"),
        new LegacyGame("Kayak VR: Mirage", "KayakVR-Win64-Shipping"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",      "negate yaw (bool, default false)"),
        ("flipPitch",    "negate pitch (bool, default false)"),
        ("flipRoll",     "negate roll (bool, default false)"),
        ("accUnit",      "acceleration unit: ms2 (default) or g"),
        ("gravIncluded", "acceleration already includes gravity reaction (bool, default false)"),
        ("euler",        "angle unit of the plain motion block: deg (default) or rad"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "accUnit", "gravIncluded", "euler");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _accScale = o.GetString("accUnit", "ms2").Equals("g", StringComparison.OrdinalIgnoreCase) ? 9.81 : 1.0;
        _gravIncluded = o.GetBool("gravIncluded", false);
        _angle = o.GetString("euler", "deg").Equals("rad", StringComparison.OrdinalIgnoreCase)
            ? 1.0 : Math.PI / 180;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("vehicle", Ogs.Telemetry.Schema.ObjectType.Vehicle, "Vehicle");
        _speed = session.AddChannel("vehicle.speed", "m/s", "Speed (api packet; unit as sent)");
        _rpm = session.AddChannel("engine.rpm", "rpm", "Engine rpm");
        _rpmMax = session.AddChannel("engine.rpmMax", "rpm", "Engine rpm maximum");
        _gear = session.AddChannel("transmission.gear", "", "Gear");
        _latVel = session.AddChannel("srs.lateralVelocity", "", "Lateral velocity (unit as sent)");
        _susp0 = session.AddChannel("srs.suspension.fl", "", "Suspension, front left (unit as sent)");
        _susp1 = session.AddChannel("srs.suspension.fr", "", "Suspension, front right");
        _susp2 = session.AddChannel("srs.suspension.rl", "", "Suspension, rear left");
        _susp3 = session.AddChannel("srs.suspension.rr", "", "Suspension, rear right");
        _terrain0 = session.AddChannel("srs.terrain.fl", "", "Terrain code under the front-left wheel");
        _terrain1 = session.AddChannel("srs.terrain.fr", "", "Terrain code under the front-right wheel");
        _terrain2 = session.AddChannel("srs.terrain.rl", "", "Terrain code under the rear-left wheel");
        _terrain3 = session.AddChannel("srs.terrain.rr", "", "Terrain code under the rear-right wheel");
        _extra1 = session.AddChannel("srs.extra1", "", "Motion block extra 1");
        _extra2 = session.AddChannel("srs.extra2", "", "Motion block extra 2");
        _apiVersion = session.AddChannel("srs.apiVersion", "", "api packet version");
    }

    public bool AcceptsDatagram(int length) => length >= 32;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static int I(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<int>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        double yaw, pitch, roll;
        Vector3 accel;
        if (d.Length >= ApiSize && d[0] == (byte)'a' && d[1] == (byte)'p' && d[2] == (byte)'i' && d[3] == 0)
        {
            pitch = F(d, 0xB0) * (Math.PI / 180);
            roll = F(d, 0xB4) * (Math.PI / 180);
            yaw = F(d, 0xB8) * (Math.PI / 180);
            accel = new Vector3(F(d, 0xC0), F(d, 0xC4), F(d, 0xC8));   // sway, heave, surge

            session.SetTypeLabel(d.Slice(0x3A, 50));
            session.SetLocation(d.Slice(0x6C, 50));
            session.SetChannel(_apiVersion, MemoryMarshal.Read<uint>(d.Slice(4, 4)));
            session.SetChannel(_speed, F(d, 0xA0));
            session.SetChannel(_rpm, F(d, 0xA4));
            session.SetChannel(_rpmMax, F(d, 0xA8));
            session.SetChannel(_gear, I(d, 0xAC));
            session.SetChannel(_latVel, F(d, 0xBC));
            session.SetChannel(_susp0, F(d, 0xCC));
            session.SetChannel(_susp1, F(d, 0xD0));
            session.SetChannel(_susp2, F(d, 0xD4));
            session.SetChannel(_susp3, F(d, 0xD8));
            session.SetChannel(_terrain0, I(d, 0xDC));
            session.SetChannel(_terrain1, I(d, 0xE0));
            session.SetChannel(_terrain2, I(d, 0xE4));
            session.SetChannel(_terrain3, I(d, 0xE8));
        }
        else
        {
            int at = d.Length - 32;                    // heave, sway, surge, yaw, pitch, roll, extra1, extra2
            accel = new Vector3(F(d, at + 4), F(d, at), F(d, at + 8));   // sway, heave, surge
            yaw = F(d, at + 12) * _angle;
            pitch = F(d, at + 16) * _angle;
            roll = F(d, at + 20) * _angle;
            session.SetChannel(_extra1, F(d, at + 24));
            session.SetChannel(_extra2, F(d, at + 28));
        }

        if (!double.IsFinite(yaw) || !double.IsFinite(pitch) || !double.IsFinite(roll)) return;
        yaw *= _sy; pitch *= _sp; roll *= _sr;

        var q = OgsMath.EulerToQuaternion(yaw, pitch, roll);
        session.SetPose(null, q);

        var linear = accel * (float)_accScale;
        var force = _gravIncluded ? linear : OgsMath.SpecificForceFromLinear(linear, q);

        Vector3 rates = default;
        double dt = now - _prevNow;
        if (_prevNow >= 0 && dt > 0.0005 && dt < MaxRateGap)
            rates = new Vector3(
                (float)(Wrap(pitch - _prevPitch) / dt),
                (float)(Wrap(yaw - _prevYaw) / dt),
                (float)(Wrap(roll - _prevRoll) / dt));
        _prevNow = now; _prevYaw = yaw; _prevPitch = pitch; _prevRoll = roll;

        session.SetBodyDynamics(force, rates);
        session.Tick(now);
    }

    static double Wrap(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }
}
}
