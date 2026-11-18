#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Distance's UDP telemetry (default port 12345): one 128-byte
/// little-endian struct per datagram, every field 4 bytes (booleans are
/// 32-bit): gamePaused @0, isRacing @4, speed (km/h) @8, pitch @12, yaw
/// @16, roll @20 (degrees), angular velocity xyz @24, sway @36, velocity
/// xyz @40, acceleration xyz @52, boost @64, grip @68, wingsOpen @72,
/// carEnabled @76, carActive @80, carDestroyed @84, allWheelsOnGround
/// @88, isGrav @92, four tyre values @96, rotation quaternion @112.
///
/// Output: pose from the Euler degrees and BodyDynamics from the angular
/// velocity (Unity axes: x pitch, y yaw, z roll). The acceleration and
/// velocity vectors have no documented frame, so they are published as
/// channels only. Nothing is emitted unless the car is active.
///
/// Unverified against the live game: the Euler signs (flip options) and
/// the angular-velocity unit (`rates=rad|deg`, default rad/s).
/// </summary>
public sealed class DistanceSource : ILegacySource
{
    public const int PacketSize = 128;
    const double Deg = Math.PI / 180;

    double _sy = 1, _sp = 1, _sr = 1, _rateScale = 1;
    OgsChannel _paused, _racing, _speed, _boost, _grip, _wings, _destroyed, _wheelsDown, _grav, _sway;
    OgsChannel _vx, _vy, _vz, _ax, _ay, _az;
    readonly OgsChannel[] _tyre = new OgsChannel[4];

    public string Id => "distance";
    public string Title => "Distance";
    public string Description => "Distance telemetry mod output (UDP port 12345, 128-byte struct).";
    public int DefaultPort => 12345;
    public IReadOnlyList<string> ProcessNames { get; } = new[] { "Distance" };
    public IReadOnlyList<LegacyGame> Games { get; } = new[] { new LegacyGame("Distance", "Distance") };

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
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "Distance car");
        _speed = session.AddChannel("vehicle.speed", "m/s", "Speed");
        _paused = session.AddBooleanChannel("session.paused", "Game paused");
        _racing = session.AddBooleanChannel("distance.isRacing", "Race in progress");
        _boost = session.AddBooleanChannel("distance.boost", "Boost active");
        _grip = session.AddBooleanChannel("distance.grip", "Grip active");
        _wings = session.AddBooleanChannel("distance.wingsOpen", "Wings open");
        _destroyed = session.AddBooleanChannel("distance.destroyed", "Car destroyed");
        _wheelsDown = session.AddBooleanChannel("distance.allWheelsOnGround", "All wheels on the ground");
        _grav = session.AddBooleanChannel("distance.isGrav", "Gravity mode");
        _sway = session.AddChannel("distance.sway", "", "Sway (unit as sent)");
        _vx = session.AddChannel("distance.velocity.x", "m/s", "Velocity X");
        _vy = session.AddChannel("distance.velocity.y", "m/s", "Velocity Y");
        _vz = session.AddChannel("distance.velocity.z", "m/s", "Velocity Z");
        _ax = session.AddChannel("distance.accel.x", "m/s2", "Acceleration X (frame unverified)");
        _ay = session.AddChannel("distance.accel.y", "m/s2", "Acceleration Y");
        _az = session.AddChannel("distance.accel.z", "m/s2", "Acceleration Z");
        string[] t = { "fl", "fr", "bl", "br" };
        for (int i = 0; i < 4; i++)
            _tyre[i] = session.AddChannel($"distance.tyre.{t[i]}", "", "Tyre value (unit as sent)");
    }

    public bool AcceptsDatagram(int length) => length >= PacketSize;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static bool B(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<int>(d.Slice(off, 4)) != 0;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (!B(d, 80)) return;                                  // car not active: menus, loading
        float pitch = F(d, 12), yaw = F(d, 16), roll = F(d, 20);
        if (!float.IsFinite(pitch) || !float.IsFinite(yaw) || !float.IsFinite(roll)) return;

        session.SetPose(null, OgsMath.EulerToQuaternion(_sy * yaw * Deg, _sp * pitch * Deg, _sr * roll * Deg));
        // no documented acceleration frame: specific force stays unreported (zero), rates from the packet
        session.SetBodyDynamics(Vector3.Zero, new Vector3(
            (float)(_sp * F(d, 24) * _rateScale), (float)(_sy * F(d, 28) * _rateScale), (float)(_sr * F(d, 32) * _rateScale)));

        session.SetChannel(_speed, F(d, 8) / 3.6);
        session.SetChannel(_paused, B(d, 0));
        session.SetChannel(_racing, B(d, 4));
        session.SetChannel(_sway, F(d, 36));
        session.SetChannel(_vx, F(d, 40)); session.SetChannel(_vy, F(d, 44)); session.SetChannel(_vz, F(d, 48));
        session.SetChannel(_ax, F(d, 52)); session.SetChannel(_ay, F(d, 56)); session.SetChannel(_az, F(d, 60));
        session.SetChannel(_boost, B(d, 64));
        session.SetChannel(_grip, B(d, 68));
        session.SetChannel(_wings, B(d, 72));
        session.SetChannel(_destroyed, B(d, 84));
        session.SetChannel(_wheelsDown, B(d, 88));
        session.SetChannel(_grav, B(d, 92));
        for (int i = 0; i < 4; i++) session.SetChannel(_tyre[i], F(d, 96 + i * 4));
        session.Tick(now);
    }
}
}
