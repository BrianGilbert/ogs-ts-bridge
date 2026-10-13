#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsaTsBridge.Sources
{
/// <summary>
/// BeamNG.drive "simple telemetry" output (the 88-byte UDP packet, port
/// 4444): 22 little-endian floats —
/// magic, world position/velocity/acceleration, an up vector, Euler
/// attitude, angular rates, angular accelerations.
///
/// Mapping (see README "Axis conventions" before trusting signs):
/// - world vectors: BeamNG is right-handed Z-up (X forward, Y left);
///   OGSA-TS is left-handed Y-up (X right, Y up, Z forward) — mapped
///   as ogsa = (−y, z, x), the nearest orientation-reversing isometry.
/// - attitude: roll/pitch/yaw arrive in radians. Euler signs map directly onto OGSA
///   C-4 conventions; per-axis `--set flipRoll/Pitch/Yaw=true` overrides
///   exist for verification against the real game.
/// - dynamics: world velocity + acceleration ride the Kinematics union
///   (the packet measures them natively in world space).
/// </summary>
public sealed class BeamNgSource : ILegacySource
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Packet
    {
        public float Magic;
        public float PosX, PosY, PosZ;
        public float VelX, VelY, VelZ;
        public float AccX, AccY, AccZ;
        public float UpX, UpY, UpZ;
        public float Roll, Pitch, Yaw;
        public float RollRate, PitchRate, YawRate;
        public float RollAcc, PitchAcc, YawAcc;
    }
    internal const int PacketSize = 22 * 4; // 88

    double _sy = 1, _sp = 1, _sr = 1;

    public string Id => "beamng";
    public string Title => "BeamNG.drive";
    public string Description =>
        "BeamNG simple telemetry (88-byte UDP, default port 4444). " +
        "Enable in-game: Options → Gameplay → Telemetry output ON.";
    public int DefaultPort => 4444;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "BeamNG.drive" };

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

    public void Declare(OgsaSession session) =>
        session.DeclareObject("car", CapnpGen.ObjectType.vehicle, "BeamNG.drive vehicle");

    public bool AcceptsDatagram(int length) => length == PacketSize;

    public void Translate(ReadOnlySpan<byte> datagram, OgsaSession session, double now)
    {
        var p = MemoryMarshal.Read<Packet>(datagram);

        session.SetPose(
            OgsaMath.BeamNgWorldToOgsa(p.PosX, p.PosY, p.PosZ),
            OgsaMath.EulerToQuaternion(_sy * p.Yaw, _sp * p.Pitch, _sr * p.Roll));
        session.SetKinematics(
            OgsaMath.BeamNgWorldToOgsa(p.VelX, p.VelY, p.VelZ),
            OgsaMath.BeamNgWorldToOgsa(p.AccX, p.AccY, p.AccZ));

        session.Tick(now);
    }
}
}
