#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Iron Rebellion UDP telemetry (default port 6969), sent by a BepInEx
/// telemetry mod: one 80-byte little-endian struct per datagram, every
/// field 4 bytes (booleans are 32-bit): velocity xyz @0, angular velocity
/// xyz @12, rotation xyz @24 (Unity Euler degrees: x pitch, y yaw, z
/// roll), adjustedTilt @36, currentLean @40, isFlying @44, isRunning @48,
/// isHit @52, weaponFired @56, stomped @60, landed @64, jumped @68,
/// stompedFoot @72 (0 none, −1 left, 1 right), speed @76.
///
/// Output: pose + BodyDynamics (angular velocity only) + the walker's
/// events as channels. Unverified: Euler signs (flip options) and the
/// angular-velocity unit (`rates=rad|deg`, default rad/s).
/// </summary>
public sealed class IronRebellionSource : ILegacySource
{
    public const int PacketSize = 80;
    const double Deg = Math.PI / 180;

    double _sy = 1, _sp = 1, _sr = 1, _rateScale = 1;
    OgsChannel _speed, _tilt, _lean, _foot, _vx, _vy, _vz;
    OgsChannel _flying, _running, _hit, _fired, _stomped, _landed, _jumped;

    public string Id => "ironrebellion";
    public string Title => "Iron Rebellion";
    public string Description => "Iron Rebellion telemetry mod output (UDP port 6969, 80-byte struct).";
    public int DefaultPort => 6969;
    public IReadOnlyList<string> ProcessNames { get; } = new[] { "Iron Rebellion" };
    public IReadOnlyList<LegacyGame> Games { get; } = new[] { new LegacyGame("Iron Rebellion", "Iron Rebellion") };

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
        session.DeclareObject("mech", Ogs.Telemetry.Schema.ObjectType.Vehicle, "Iron Rebellion walker");
        _speed = session.AddChannel("vehicle.speed", "m/s", "Speed");
        _vx = session.AddChannel("ironrebellion.velocity.x", "m/s", "Velocity X");
        _vy = session.AddChannel("ironrebellion.velocity.y", "m/s", "Velocity Y");
        _vz = session.AddChannel("ironrebellion.velocity.z", "m/s", "Velocity Z");
        _tilt = session.AddChannel("ironrebellion.adjustedTilt", "", "Adjusted tilt");
        _lean = session.AddChannel("ironrebellion.currentLean", "", "Current lean");
        _foot = session.AddChannel("ironrebellion.stompedFoot", "", "Foot that stomped: 0 none, -1 left, 1 right");
        _flying = session.AddBooleanChannel("ironrebellion.flying", "Airborne");
        _running = session.AddBooleanChannel("ironrebellion.running", "Running");
        _hit = session.AddBooleanChannel("ironrebellion.hit", "Hit this frame");
        _fired = session.AddBooleanChannel("ironrebellion.weaponFired", "Weapon fired this frame");
        _stomped = session.AddBooleanChannel("ironrebellion.stomped", "Stomped this frame");
        _landed = session.AddBooleanChannel("ironrebellion.landed", "Landed this frame");
        _jumped = session.AddBooleanChannel("ironrebellion.jumped", "Jumped this frame");
    }

    public bool AcceptsDatagram(int length) => length >= PacketSize;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static bool B(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<int>(d.Slice(off, 4)) != 0;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        float pitch = F(d, 24), yaw = F(d, 28), roll = F(d, 32);
        if (!float.IsFinite(pitch) || !float.IsFinite(yaw) || !float.IsFinite(roll)) return;

        session.SetPose(null, OgsMath.EulerToQuaternion(_sy * yaw * Deg, _sp * pitch * Deg, _sr * roll * Deg));
        session.SetBodyDynamics(Vector3.Zero, new Vector3(
            (float)(_sp * F(d, 12) * _rateScale), (float)(_sy * F(d, 16) * _rateScale), (float)(_sr * F(d, 20) * _rateScale)));

        session.SetChannel(_vx, F(d, 0)); session.SetChannel(_vy, F(d, 4)); session.SetChannel(_vz, F(d, 8));
        session.SetChannel(_tilt, F(d, 36));
        session.SetChannel(_lean, F(d, 40));
        session.SetChannel(_flying, B(d, 44));
        session.SetChannel(_running, B(d, 48));
        session.SetChannel(_hit, B(d, 52));
        session.SetChannel(_fired, B(d, 56));
        session.SetChannel(_stomped, B(d, 60));
        session.SetChannel(_landed, B(d, 64));
        session.SetChannel(_jumped, B(d, 68));
        session.SetChannel(_foot, F(d, 72));
        session.SetChannel(_speed, F(d, 76));
        session.Tick(now);
    }
}
}
