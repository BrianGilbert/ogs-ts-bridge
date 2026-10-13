#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsaTsBridge.Sources
{
/// <summary>
/// Live for Speed OutSim telemetry (default port 3156; configure OutSim in LFS's Options → Misc → Display).
///
/// OutSim packet (≥ 40 bytes): angular velocity (x roll, y pitch, z yaw,
/// rad/s, local) @4, heading/pitch/roll radians @16/20/24, local
/// acceleration @28/32/36. LFS local axes assumed right-handed X-forward,
/// Y-left, Z-up (unverified against the live game — flip options exist);
/// vectors map to OGSA body axes as (−y, z, x).
///
/// Output: pose + BodyDynamics. OutSim reports plain linear acceleration
/// (gravity excluded by default), so the accelerometer-style specific
/// force is derived: a_local − g_body, with g_body computed from the
/// packet's own orientation. Options cover both assumptions.
/// </summary>
public sealed class LfsSource : ILegacySource
{
    double _sy = 1, _sp = 1, _sr = 1, _accScale = 9.81;
    bool _gravIncluded;

    public string Id => "lfs";
    public string Title => "Live for Speed (OutSim)";
    public string Description =>
        "LFS OutSim UDP telemetry (default port 3156). Enable OutSim in " +
        "LFS options and point it at this machine.";
    public int DefaultPort => 3156;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "LFS" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Live for Speed", "LFS"),
    };


    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",        "negate yaw Euler sign (bool, default false)"),
        ("flipPitch",      "negate pitch Euler sign (bool, default false)"),
        ("flipRoll",       "negate roll Euler sign (bool, default false)"),
        ("accUnit",        "acceleration unit: g (default) or ms2"),
        ("gravIncluded",   "acceleration already includes gravity reaction (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "accUnit", "gravIncluded");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _accScale = o.GetString("accUnit", "g").Equals("ms2", StringComparison.OrdinalIgnoreCase)
            ? 1.0 : 9.81;
        _gravIncluded = o.GetBool("gravIncluded", false);
    }

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));

    public void Declare(OgsaSession session) =>
        session.DeclareObject("car", CapnpGen.ObjectType.vehicle, "LFS vehicle");

    public bool AcceptsDatagram(int length) => length >= 40;

    public void Translate(ReadOnlySpan<byte> d, OgsaSession session, double now)
    {

        var q = OgsaMath.EulerToQuaternion(_sy * F(d, 16), _sp * F(d, 20), _sr * F(d, 24));
        session.SetPose(null, q);

        // LFS local (X fwd, Y left, Z up, assumed) -> OGSA body (x right, y up, z fwd)
        Vector3 Map(float x, float y, float z) => new(-y, z, x);
        var accel = Map(F(d, 28), F(d, 32), F(d, 36)) * (float)_accScale;

        Vector3 specific;
        if (_gravIncluded)
        {
            specific = accel;
        }
        else
        {
            // specific force = a_local - g_body; g_body = q⁻¹ · (0, -g, 0)
            var gBody = Vector3.Transform(new Vector3(0, -9.81f, 0), Quaternion.Conjugate(q));
            specific = accel - gBody;
        }

        // angular rates: rotation about LFS Y(left) is nose-down, about Z(up)
        // is nose-left, about X(fwd) rolls right-side-down -> OGSA signs
        var rates = new Vector3(-F(d, 8), -F(d, 12), F(d, 4));
        session.SetBodyDynamics(specific, rates);

        session.Tick(now);
    }
}
}
