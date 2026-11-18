#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// New Star GP UDP telemetry (default port 12345): one 100-byte
/// little-endian struct per datagram, every field 4 bytes (booleans are
/// 32-bit): pitch @0, yaw @4, roll @8 (degrees), angular velocity xyz @12,
/// cForce @24, velocity xyz @28, acceleration xyz @40, speed @52, rpm @56,
/// gear @60 (int), four tyre values @64, wheelsOnTrack @80 (int),
/// isBoosting @84, allowDriving @88, isRacing @92, isEventOver @96.
///
/// Output: pose + BodyDynamics (angular velocity only: the acceleration
/// vector has no documented frame, so it rides channels) + channels.
/// Nothing is emitted while the event is over or driving is not allowed.
/// Unverified: Euler signs (flip options) and the angular-velocity unit
/// (`rates=rad|deg`, default rad/s).
/// </summary>
public sealed class NewStarGpSource : ILegacySource
{
    public const int PacketSize = 100;
    const double Deg = Math.PI / 180;

    double _sy = 1, _sp = 1, _sr = 1, _rateScale = 1;
    OgsChannel _speed, _rpm, _gear, _cForce, _onTrack, _boost, _racing;
    OgsChannel _vx, _vy, _vz, _ax, _ay, _az;
    readonly OgsChannel[] _tyre = new OgsChannel[4];

    public string Id => "newstargp";
    public string Title => "New Star GP";
    public string Description => "New Star GP telemetry output (UDP port 12345, 100-byte struct).";
    public int DefaultPort => 12345;
    public IReadOnlyList<string> ProcessNames { get; } = new[] { "NSGP" };
    public IReadOnlyList<LegacyGame> Games { get; } = new[] { new LegacyGame("New Star GP", "NSGP") };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate yaw (bool, default false)"),
        ("flipPitch", "negate pitch (bool, default false)"),
        ("flipRoll",  "negate roll (bool, default false)"),
        ("rates",     "angular velocity unit: rad (default) or deg"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "rates");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _rateScale = o.GetString("rates", "rad").Equals("deg", StringComparison.OrdinalIgnoreCase) ? Deg : 1;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "New Star GP car");
        _speed = session.AddChannel("vehicle.speed", "m/s", "Speed (unit as sent)");
        _rpm = session.AddChannel("engine.rpm", "rpm", "Engine rpm");
        _gear = session.AddChannel("transmission.gear", "", "Gear");
        _cForce = session.AddChannel("newstargp.cForce", "", "Cornering force (unit as sent)");
        _onTrack = session.AddChannel("newstargp.wheelsOnTrack", "", "Wheels on the track");
        _boost = session.AddBooleanChannel("newstargp.boosting", "Boost active");
        _racing = session.AddBooleanChannel("newstargp.racing", "Race in progress");
        _vx = session.AddChannel("newstargp.velocity.x", "m/s", "Velocity X");
        _vy = session.AddChannel("newstargp.velocity.y", "m/s", "Velocity Y");
        _vz = session.AddChannel("newstargp.velocity.z", "m/s", "Velocity Z");
        _ax = session.AddChannel("newstargp.accel.x", "m/s2", "Acceleration X (frame unverified)");
        _ay = session.AddChannel("newstargp.accel.y", "m/s2", "Acceleration Y");
        _az = session.AddChannel("newstargp.accel.z", "m/s2", "Acceleration Z");
        string[] t = { "fl", "fr", "bl", "br" };
        for (int i = 0; i < 4; i++)
            _tyre[i] = session.AddChannel($"newstargp.tyre.{t[i]}", "", "Tyre value (unit as sent)");
    }

    public bool AcceptsDatagram(int length) => length >= PacketSize;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static int I(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<int>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (I(d, 88) == 0 || I(d, 96) != 0) return;             // driving not allowed / event over
        float pitch = F(d, 0), yaw = F(d, 4), roll = F(d, 8);
        if (!float.IsFinite(pitch) || !float.IsFinite(yaw) || !float.IsFinite(roll)) return;

        session.SetPose(null, OgsMath.EulerToQuaternion(_sy * yaw * Deg, _sp * pitch * Deg, _sr * roll * Deg));
        session.SetBodyDynamics(Vector3.Zero, new Vector3(
            (float)(_sp * F(d, 12) * _rateScale), (float)(_sy * F(d, 16) * _rateScale), (float)(_sr * F(d, 20) * _rateScale)));

        session.SetChannel(_cForce, F(d, 24));
        session.SetChannel(_vx, F(d, 28)); session.SetChannel(_vy, F(d, 32)); session.SetChannel(_vz, F(d, 36));
        session.SetChannel(_ax, F(d, 40)); session.SetChannel(_ay, F(d, 44)); session.SetChannel(_az, F(d, 48));
        session.SetChannel(_speed, F(d, 52));
        session.SetChannel(_rpm, F(d, 56));
        session.SetChannel(_gear, I(d, 60));
        for (int i = 0; i < 4; i++) session.SetChannel(_tyre[i], F(d, 64 + i * 4));
        session.SetChannel(_onTrack, I(d, 80));
        session.SetChannel(_boost, I(d, 84) != 0);
        session.SetChannel(_racing, I(d, 92) != 0);
        session.Tick(now);
    }
}
}
