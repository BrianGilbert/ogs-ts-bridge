#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsaTsBridge.Sources
{
/// <summary>
/// Generic Kunos acpmf shared-memory source — one format, two games:
/// Assetto Corsa (acs.exe) and Assetto Corsa Competizione (acc.exe) write
/// the same physics-page prefix (the ACC struct extends AC's). Enable the
/// shared memory / "free server" developer option in-game.
///
/// Physics page prefix (the assettocorsasharedmemory
/// layout, Pack=4): PacketId @0, Gas @4,
/// Brake @8, Fuel @12, Gear @16, Rpms @20, SteerAngle @24, SpeedKmh @28,
/// local Velocity @32, AccG @44 (lateral, longitudinal, vertical, in g —
/// accelerometer-style, gravity included), WheelSlip @56,
/// SuspensionTravel (fl, fr, rl, rr) @184, Drs @200, TC @204, Heading
/// @208, Pitch @212, Roll @216.
///
/// Transport: shared memory, polled at 2 ms (well under the ~3 ms physics
/// write cadence); Translate gates on PacketId so unchanged polls emit
/// nothing. Output: pose + BodyDynamics from AccG, wheel drive points,
/// channels.
/// </summary>
public sealed class AcFamilySource : ILegacySource
{
    OgsaChannel _rpm, _throttle, _brake, _speed, _fuel, _gear;
    double _sy = 1, _sp = 1, _sr = 1;
    double _wheelbase = 2.6, _track = 1.6, _radius = 0.34;
    int _lastPacketId = int.MinValue;

    public string Id => "ac-family";
    public string Title => "Kunos acpmf (Assetto Corsa / ACC)";
    public string Description =>
        "Kunos shared-memory physics page (Local\\acpmf_physics) — Assetto " +
        "Corsa and Competizione. Enable the game's shared-memory/free-server " +
        "developer option.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "acs", "acc" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Assetto Corsa", "acs"),
        new LegacyGame("Assetto Corsa Competizione", "acc"),
    };


    public SourceTransport Transport => SourceTransport.SharedMemory;
    public string SharedMemoryName => "Local\\acpmf_physics";
    public int SharedMemorySize => 220;
    public int PollIntervalMs => 2;

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate yaw/heading Euler sign (bool, default false)"),
        ("flipPitch", "negate pitch Euler sign (bool, default false)"),
        ("flipRoll",  "negate roll Euler sign (bool, default false)"),
        ("wheelbase", "wheel separation fore/aft in metres (default 2.6)"),
        ("track",     "wheel separation left/right in metres (default 1.6)"),
        ("radius",    "tyre rolling radius in metres (default 0.34)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "wheelbase", "track", "radius");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _wheelbase = o.GetDouble("wheelbase", 2.6);
        _track = o.GetDouble("track", 1.6);
        _radius = o.GetDouble("radius", 0.34);
    }

    public void Declare(OgsaSession session)
    {
        session.DeclareObject("car", CapnpGen.ObjectType.vehicle, "Kunos vehicle");
        float hz = (float)(_wheelbase * 0.5), hx = (float)(_track * 0.5);
        session.AddWheel("wheel.fl", new Vector3(-hx, 0, hz), (float)_radius, true, true);
        session.AddWheel("wheel.fr", new Vector3(+hx, 0, hz), (float)_radius, true, true);
        session.AddWheel("wheel.rl", new Vector3(-hx, 0, -hz), (float)_radius, true, false);
        session.AddWheel("wheel.rr", new Vector3(+hx, 0, -hz), (float)_radius, true, false);

        _rpm     = session.AddChannel("engine.rpm", "rpm", "Current engine rpm");
        _throttle= session.AddChannel("engine.throttle", "", "Throttle input 0..1");
        _brake   = session.AddChannel("brakes.pedal", "", "Brake input 0..1");
        _speed   = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        _fuel    = session.AddChannel("ac.fuel", "", "Raw fuel value (game encoding)");
        _gear    = session.AddChannel("ac.gear", "", "Raw gear number (game encoding)");
    }

    public bool AcceptsDatagram(int length) => length >= 220;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static int I(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<int>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsaSession session, double now)
    {
        int packetId = MemoryMarshal.Read<int>(d);
        if (packetId == _lastPacketId) return; // page unchanged — emit nothing
        _lastPacketId = packetId;


        session.SetPose(null, OgsaMath.EulerToQuaternion(
            _sy * F(d, 208), _sp * F(d, 212), _sr * F(d, 216)));

        // AccG (lat, long, vert) in g -> OGSA body (x right, y up, z fwd)
        session.SetBodyDynamics(
            new Vector3(F(d, 44), F(d, 52), F(d, 48)) * 9.81f,
            default);

        session.SetWheel(0, F(d, 56),     F(d, 184), CapnpGen.ContactMedium.asphalt);
        session.SetWheel(1, F(d, 60),     F(d, 188), CapnpGen.ContactMedium.asphalt);
        session.SetWheel(2, F(d, 64),     F(d, 192), CapnpGen.ContactMedium.asphalt);
        session.SetWheel(3, F(d, 68),     F(d, 196), CapnpGen.ContactMedium.asphalt);

        session.SetChannel(_rpm, I(d, 20));
        session.SetChannel(_throttle, F(d, 4));
        session.SetChannel(_brake, F(d, 8));
        session.SetChannel(_speed, F(d, 28) / 3.6f);
        session.SetChannel(_fuel, F(d, 12));
        session.SetChannel(_gear, I(d, 16));

        session.Tick(now);
    }
}
}
