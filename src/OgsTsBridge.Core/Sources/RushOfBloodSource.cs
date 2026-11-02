#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Until Dawn: Rush of Blood on PC through BloodRush, an unofficial
/// shadPS4 emulator build (release b002 or newer) that streams the cart's
/// motion over UDP (default 127.0.0.1:25151). No setup beyond launching
/// the game with BloodRush's shadps4.exe.
///
/// Datagram (52 bytes, little-endian, packed): speed @0, position xyz
/// @4/8/12, orientation quaternion xyzw @16/20/24/28, g-force (lateral,
/// vertical, longitudinal) @32/36/40, flags @44 (1 in play, 2 braking, 4
/// paused, 8 VR), frame @48.
///
/// The quaternion's frame is undocumented: Euler angles are taken from it
/// as yaw about Z, pitch about Y, roll about X (the reading the cart's
/// motion has been tuned against), signs unverified — flip options exist.
/// The g-forces are accelerometer-style and scale straight to specific
/// force; the packet carries no angular rates (zero). Motion is emitted
/// only while the cart is in play; pause rides session.paused.
/// </summary>
public sealed class RushOfBloodSource : ILegacySource
{
    const float Gravity = 9.80665f;
    internal const int PacketSize = 52;

    OgsChannel _speed, _px, _py, _pz, _braking, _vr, _frame, _paused;
    double _sy = 1, _sp = 1, _sr = 1;

    public string Id => "rushofblood";
    public string Title => "Until Dawn: Rush of Blood (BloodRush shadPS4)";
    public string Description =>
        "Rush of Blood cart telemetry from the BloodRush shadPS4 build (UDP " +
        "port 25151, release b002 or newer). Launch the game with its shadps4.exe.";
    public int DefaultPort => 25151;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "shadps4" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Until Dawn: Rush of Blood (BloodRush shadPS4)", "shadps4"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate yaw (bool, default false)"),
        ("flipPitch", "negate pitch (bool, default false)"),
        ("flipRoll",  "negate roll (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("cart", Ogs.Telemetry.Schema.ObjectType.Vehicle, "Rush of Blood cart");
        session.DeclareGravity(Gravity);
        _speed   = session.AddChannel("rushofblood.speed", "", "Cart speed (game units)");
        _px      = session.AddChannel("rushofblood.position.x", "", "Cart position X (game units)");
        _py      = session.AddChannel("rushofblood.position.y", "", "Cart position Y (game units)");
        _pz      = session.AddChannel("rushofblood.position.z", "", "Cart position Z (game units)");
        _braking = session.AddBooleanChannel("rushofblood.braking", "Cart braking");
        _vr      = session.AddBooleanChannel("rushofblood.vr", "Running in PC-VR mode");
        _frame   = session.AddChannel("rushofblood.frame", "", "Sample counter");
        _paused  = session.AddBooleanChannel("session.paused", "Game paused");
        session.AlwaysSend(_paused);
    }

    public bool AcceptsDatagram(int length) => length >= PacketSize;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        uint flags = MemoryMarshal.Read<uint>(d.Slice(44, 4));
        if ((flags & 1) == 0) return; // between rides

        float x = F(d, 16), y = F(d, 20), z = F(d, 24), w = F(d, 28);
        double yaw = Math.Atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z));
        double pitch = Math.Asin(Math.Clamp(2 * (w * y - z * x), -1, 1));
        double roll = Math.Atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y));
        session.SetPose(null, OgsMath.EulerToQuaternion(_sy * yaw, _sp * pitch, _sr * roll));
        session.SetBodyDynamics(new Vector3(F(d, 32), F(d, 36), F(d, 40)) * Gravity, Vector3.Zero);

        session.SetChannel(_speed, F(d, 0));
        session.SetChannel(_px, F(d, 4));
        session.SetChannel(_py, F(d, 8));
        session.SetChannel(_pz, F(d, 12));
        session.SetChannel(_braking, (flags & 2) != 0);
        session.SetChannel(_paused, (flags & 4) != 0);
        session.SetChannel(_vr, (flags & 8) != 0);
        session.SetChannel(_frame, MemoryMarshal.Read<uint>(d.Slice(48, 4)));
        session.Tick(now);
    }
}
}
