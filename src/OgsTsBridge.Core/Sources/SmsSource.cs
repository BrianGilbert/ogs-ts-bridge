#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Generic Slightly Mad Studios UDP telemetry source — one format,
/// several games: pCARS2, Automobilista 2, pCARS3 share this UDP stack
/// (default port 5606). Games confirmed compatible join the process
/// watch list; no per-game code.
///
/// Field offsets: fuelLevel @36,
/// speed m/s @40, rpm @44, maxRpm @46, steering @48, orientation radians
/// (pitch, yaw, roll) @52/56/60, local velocity @64, world velocity @76,
/// angular velocity @88 (x pitch, y yaw, z roll), local acceleration
/// @100.
///
/// Output: pose + BodyDynamics (local acceleration and angular rates are
/// the packet's native dynamics; local axes match the OGS body frame).
/// Local acceleration unit is treated as g by default; override with accUnit=ms2 if a title disagrees.
/// </summary>
public sealed class SmsSource : ILegacySource
{
    OgsChannel _rpm, _rpmMax, _speed, _fuel, _steer;
    double _sy = 1, _sp = 1, _sr = 1, _accScale = 9.81;

    public string Id => "sms";
    public string Title => "SMS engine (pCARS2 / AMS2 / pCARS3)";
    public string Description =>
        "Slightly Mad Studios UDP telemetry (default port 5606). In-game: " +
        "Options → UDP telemetry ON, send to this machine's IP and port.";
    public int DefaultPort => 5606;

    public IReadOnlyList<string> ProcessNames { get; } = new[]
    {
        "pCARS2AVX", "AMS2AVX", "pCARS3",
    };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Project CARS 2", "pCARS2AVX"),
        new LegacyGame("Automobilista 2", "AMS2AVX"),
        new LegacyGame("Project CARS 3", "pCARS3"),
    };


    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate yaw Euler sign (bool, default false)"),
        ("flipPitch", "negate pitch Euler sign (bool, default false)"),
        ("flipRoll",  "negate roll Euler sign (bool, default false)"),
        ("accUnit",   "local acceleration unit: g (default) or ms2"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "accUnit");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _accScale = o.GetString("accUnit", "g").Equals("ms2", StringComparison.OrdinalIgnoreCase)
            ? 1.0 : 9.81;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "SMS-engine car");
        _rpm    = session.AddChannel("engine.rpm", "rpm", "Current engine rpm");
        _rpmMax = session.AddChannel("engine.rpmMax", "rpm", "Engine redline");
        _speed  = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        _fuel   = session.AddChannel("fuel.level", "", "Remaining fuel fraction 0..1");
        _steer  = session.AddChannel("sms.steering", "", "Raw steering value");
    }

    public bool AcceptsDatagram(int length) => length >= 116;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static ushort U(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<ushort>(d.Slice(off, 2));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {

        session.SetPose(null, OgsMath.EulerToQuaternion(
            _sy * F(d, 56), _sp * F(d, 52), _sr * F(d, 60)));
        session.SetBodyDynamics(
            new System.Numerics.Vector3(F(d, 100) * (float)_accScale,
                                        F(d, 104) * (float)_accScale,
                                        F(d, 108) * (float)_accScale),
            new System.Numerics.Vector3(F(d, 88), F(d, 92), F(d, 96)));

        session.SetChannel(_rpm, U(d, 44));
        session.SetChannel(_rpmMax, U(d, 46));
        session.SetChannel(_speed, F(d, 40));
        session.SetChannel(_fuel, F(d, 36));
        session.SetChannel(_steer, d[48]);

        session.Tick(now);
    }
}
}
