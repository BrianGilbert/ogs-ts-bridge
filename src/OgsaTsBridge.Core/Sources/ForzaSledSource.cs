#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsaTsBridge.Sources
{
/// <summary>
/// Forza "Sled" telemetry — the 232-byte UDP packet shared by Forza
/// Motorsport 7+ and Forza Horizon 4/5. Enable in-game: Settings → Data Out, point it at this
/// machine, packet type "Sled".
///
/// The packet has no world position — the car never leaves the origin and
/// orientation rides Euler angles. Local-space vectors (acceleration,
/// velocity, angular rates) use exactly the OGSA body axes (X right, Y up,
/// Z forward; X pitch, Y yaw, Z roll), so they pass through with sign
/// flips only. Euler angles arrive in radians
/// (`euler=deg` overrides if a title disagrees). Local acceleration +
/// angular rates ride the BodyDynamics union: it is what the packet
/// measures natively and the direct input of motion-cueing washout.
///
/// Per-wheel state maps onto four wheel drive points: slip = combined
/// slip, compression = suspension travel (m), contact = rumble strip when
/// the game flags one. Wheel geometry is not in the packet — placeholder
/// offsets ship as defaults, override per car with wheelbase/track/radius.
/// </summary>
public sealed class ForzaSledSource : ILegacySource
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Packet
    {
        public uint IsRaceOn;
        public uint TimestampMS;
        public float EngineMaxRpm;
        public float EngineIdleRpm;
        public float CurrentEngineRpm;
        public float AccX, AccY, AccZ;               // local: X right, Y up, Z forward
        public float VelX, VelY, VelZ;
        public float AngVelX, AngVelY, AngVelZ;      // local: X pitch, Y yaw, Z roll, rad/s
        public float Yaw, Pitch, Roll;               // radians (euler=deg to override)
        public float SuspNormFL, SuspNormFR, SuspNormRL, SuspNormRR;
        public float SlipRatioFL, SlipRatioFR, SlipRatioRL, SlipRatioRR;
        public float WheelSpeedFL, WheelSpeedFR, WheelSpeedRL, WheelSpeedRR;
        public uint RumbleFL, RumbleFR, RumbleRL, RumbleRR;
        public float PuddleFL, PuddleFR, PuddleRL, PuddleRR;
        public float SurfRumbleFL, SurfRumbleFR, SurfRumbleRL, SurfRumbleRR;
        public float SlipAngleFL, SlipAngleFR, SlipAngleRL, SlipAngleRR;
        public float CombinedSlipFL, CombinedSlipFR, CombinedSlipRL, CombinedSlipRR;
        public float SuspMetersFL, SuspMetersFR, SuspMetersRL, SuspMetersRR;
        public uint CarOrdinal, CarClass, CarPerformanceIndex, DrivetrainType, NumCylinders;
    }
    internal const int PacketSize = 232;

    // channel handles (wired in Declare)
    OgsaChannel _rpm, _rpmMax, _rpmIdle, _speed, _cylinders, _raceOn;

    double _sy = 1, _sp = 1, _sr = 1;      // Euler sign flips (rates follow)
    double _degToRad = 1.0;                // 1 when radians, π/180 when degrees
    double _wheelbase = 2.7, _track = 1.5, _radius = 0.34;

    public string Id => "forza-sled";
    public string Title => "Forza (Sled format)";
    public string Description =>
        "Forza Motorsport 7+ / Horizon 4/5 'Sled' 232-byte UDP telemetry. " +
        "In-game: Settings → Data Out → this machine's IP, packet type Sled.";
    public int DefaultPort => 5300;

    public IReadOnlyList<string> ProcessNames { get; } = new[]
    {
        "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport", "ForzaMotorsport7",
    };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Forza Horizon 5", "ForzaHorizon5"),
        new LegacyGame("Forza Horizon 4", "ForzaHorizon4"),
        new LegacyGame("Forza Motorsport (7+)", "ForzaMotorsport"),
        new LegacyGame("Forza Motorsport 7", "ForzaMotorsport7"),
    };


    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("euler",     "Euler angle units: rad (default) or deg"),
        ("flipYaw",   "negate yaw Euler + yaw-rate sign (bool, default false)"),
        ("flipPitch", "negate pitch Euler + pitch-rate sign (bool, default false)"),
        ("flipRoll",  "negate roll Euler + roll-rate sign (bool, default false)"),
        ("wheelbase", "wheel separation fore/aft in metres for drive points (default 2.7)"),
        ("track",     "wheel separation left/right in metres (default 1.5)"),
        ("radius",    "tyre rolling radius in metres (default 0.34)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("euler", "flipYaw", "flipPitch", "flipRoll",
                      "wheelbase", "track", "radius");
        _degToRad = o.GetString("euler", "rad").Equals("deg", StringComparison.OrdinalIgnoreCase)
            ? Math.PI / 180.0 : 1.0;
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _wheelbase = o.GetDouble("wheelbase", 2.7);
        _track = o.GetDouble("track", 1.5);
        _radius = o.GetDouble("radius", 0.34);
    }

    public void Declare(OgsaSession session)
    {
        session.DeclareObject("car", CapnpGen.ObjectType.vehicle, "Forza vehicle");

        float hz = (float)(_wheelbase * 0.5), hx = (float)(_track * 0.5);
        session.AddWheel("wheel.fl", new Vector3(-hx, 0, hz), (float)_radius, driven: true, steered: true);
        session.AddWheel("wheel.fr", new Vector3(+hx, 0, hz), (float)_radius, driven: true, steered: true);
        session.AddWheel("wheel.rl", new Vector3(-hx, 0, -hz), (float)_radius, driven: true, steered: false);
        session.AddWheel("wheel.rr", new Vector3(+hx, 0, -hz), (float)_radius, driven: true, steered: false);

        // annex-ratified names first (CHANNELS.md), then format-specific.
        // Deliberately lean: discovery carries a descriptor per channel and
        // must stay well under a UDP MTU (~1.4 kB). Per-wheel detail rides
        // the four WheelState drive points instead of flat channels.
        _rpm       = session.AddChannel("engine.rpm", "rpm", "Current engine rpm");
        _rpmMax    = session.AddChannel("engine.rpmMax", "rpm", "Engine redline");
        _rpmIdle   = session.AddChannel("engine.rpmIdle", "rpm", "Idle rpm");
        _speed     = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        _cylinders = session.AddChannel("engine.cylinders", "", "Engine cylinder count");
        _raceOn    = session.AddBooleanChannel("forza.raceOn", "1 while the session is drivable");
    }

    public bool AcceptsDatagram(int length) => length == PacketSize;

    public void Translate(ReadOnlySpan<byte> datagram, OgsaSession session, double now)
    {
        var p = MemoryMarshal.Read<Packet>(datagram);
        if (p.IsRaceOn != 1)
        {
            // Menus / paused: stop the stream. Receivers hold the last pose
            // and mark the session stale per the standard's liveness rules.
            return;
        }

        session.SetPose(null, OgsaMath.EulerToQuaternion(
            _sy * p.Yaw * _degToRad, _sp * p.Pitch * _degToRad, _sr * p.Roll * _degToRad));
        session.SetBodyDynamics(
            new Vector3(p.AccX, p.AccY, p.AccZ),
            new Vector3((float)(_sp * p.AngVelX), (float)(_sy * p.AngVelY), (float)(_sr * p.AngVelZ)));

        SetWheel(session, 0, p.CombinedSlipFL, p.SuspMetersFL, p.RumbleFL);
        SetWheel(session, 1, p.CombinedSlipFR, p.SuspMetersFR, p.RumbleFR);
        SetWheel(session, 2, p.CombinedSlipRL, p.SuspMetersRL, p.RumbleRL);
        SetWheel(session, 3, p.CombinedSlipRR, p.SuspMetersRR, p.RumbleRR);

        session.SetChannel(_rpm, p.CurrentEngineRpm);
        session.SetChannel(_rpmMax, p.EngineMaxRpm);
        session.SetChannel(_rpmIdle, p.EngineIdleRpm);
        session.SetChannel(_speed, Math.Sqrt(
            (double)p.VelX * p.VelX + (double)p.VelY * p.VelY + (double)p.VelZ * p.VelZ));
        session.SetChannel(_cylinders, p.NumCylinders);
        session.SetChannel(_raceOn, true);

        session.Tick(now);
    }

    static void SetWheel(OgsaSession session, int i, float slip, float compression, uint rumble) =>
        session.SetWheel(i, slip, compression,
            rumble != 0 ? CapnpGen.ContactMedium.rumbleStrip
                        : CapnpGen.ContactMedium.asphalt);
}
}
