#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsaTsBridge.Sources
{
/// <summary>
/// WRC (Generations-era) UDP telemetry — one format across the WRC
/// franchise titles that ship this structure (default port 20777).
/// Layout (Pack=1):
/// speed @40 m/s, position @48, velocity @60, acceleration @72 (world),
/// left/fwd/up direction vectors @84/96/108, hub data, rpm max/idle/
/// current @184/188/192, throttle @196, brake @200, clutch @204,
/// steering @208, handbrake @212.
///
/// Output: pose (orientation derived from the world direction vectors),
/// world kinematics, and channels. WRC world is right-handed Y-up;
/// vectors map to OGSA as (z, y, x).
/// </summary>
public sealed class WrcSource : ILegacySource
{
    OgsaChannel _rpm, _rpmMax, _speed, _throttle, _brake, _gear, _handbrake, _steer;
    double _sy = 1, _sp = 1, _sr = 1;

    public string Id => "wrc";
    public string Title => "WRC (Generations-era UDP)";
    public string Description =>
        "WRC UDP telemetry (default port 20777). Enable in-game UDP output " +
        "pointed at this machine.";
    public int DefaultPort => 20777;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "WRC" };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate yaw Euler sign (bool, default false)"),
        ("flipPitch", "negate pitch Euler sign (bool, default false)"),
        ("flipRoll",  "negate roll Euler sign (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
    }

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));

    public void Declare(OgsaSession session)
    {
        session.DeclareObject("car", CapnpGen.ObjectType.vehicle, "WRC rally car");
        _rpm       = session.AddChannel("engine.rpm", "rpm", "Current engine rpm");
        _rpmMax    = session.AddChannel("engine.rpmMax", "rpm", "Engine redline");
        _speed     = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        _throttle  = session.AddChannel("engine.throttle", "", "Throttle input 0..1");
        _brake     = session.AddChannel("brakes.pedal", "", "Brake input 0..1");
        _gear      = session.AddChannel("wrc.gearIndex", "", "Raw gear index (game encoding)");
        _handbrake = session.AddBooleanChannel("wrc.handbrake", "Handbrake applied");
        _steer     = session.AddChannel("wrc.steering", "", "Raw steering value");
    }

    public bool AcceptsDatagram(int length) => length >= 216;

    public void Translate(ReadOnlySpan<byte> d, OgsaSession session, double now)
    {

        var fwd = OgsaMath.RhuYUpWorldToOgsa(F(d, 96), F(d, 100), F(d, 104));
        var left = OgsaMath.RhuYUpWorldToOgsa(F(d, 84), F(d, 88), F(d, 92));
        var right = Vector3.Negate(left);

        var (yaw, pitch, roll) = OgsaMath.EulerFromForwardRight(fwd, right);
        session.SetPose(
            OgsaMath.RhuYUpWorldToOgsa(F(d, 48), F(d, 52), F(d, 56)),
            OgsaMath.EulerToQuaternion(_sy * yaw, _sp * pitch, _sr * roll));
        session.SetKinematics(
            OgsaMath.RhuYUpWorldToOgsa(F(d, 60), F(d, 64), F(d, 68)),
            OgsaMath.RhuYUpWorldToOgsa(F(d, 72), F(d, 76), F(d, 80)));

        session.SetChannel(_rpm, F(d, 192));
        session.SetChannel(_rpmMax, F(d, 184));
        session.SetChannel(_speed, F(d, 40));
        session.SetChannel(_throttle, F(d, 196));
        session.SetChannel(_brake, F(d, 200));
        session.SetChannel(_gear, d[37]);
        session.SetChannel(_handbrake, F(d, 212) != 0);
        session.SetChannel(_steer, F(d, 208));

        session.Tick(now);
    }
}
}
