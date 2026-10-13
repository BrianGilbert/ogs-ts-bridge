#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OgsaTsBridge.Sources
{
/// <summary>
/// Generic Codemasters F1 telemetry source — one format across F1 2020
/// through F1 2024 (default port 20777; set the game's UDP telemetry to
/// this machine). The header layout changed in 2023 (gameYear byte +
/// overallFrameIdentifier); playerCarIndex and the car array move
/// accordingly — detected from packetFormat.
///
/// Motion packet (id 0), per-car stride 60: world position @0, world
/// velocity @12, forward/right dirs as int16 fixed-point (/32767) @24/@30,
/// g-forces (lateral, longitudinal, vertical) @36, yaw/pitch/roll
/// radians @48 (reference: F1Sharp MotionData).
///
/// Telemetry packet (id 6, format ≥ 2023), per-car stride 60: speed
/// u16 km/h @0, throttle f32 @4, steer f32 @8, brake f32 @12, clutch u8
/// @16, gear i8 @17, rpm u16 @18, drs u8 @20 — older formats keep the
/// pre-2023 u8 layout and only motion is consumed.
///
/// Output: pose + BodyDynamics from the motion packet (g-forces treated
/// as accelerometer-style, gravity included, ×9.81 to m/s²); channels from
/// the 2023+ telemetry packet.
/// </summary>
public sealed class F1Source : ILegacySource
{
    OgsaChannel _rpm, _speed, _throttle, _brake, _gear, _drs;
    double _sy = 1, _sp = 1, _sr = 1;

    public string Id => "f1";
    public string Title => "Codemasters F1 (2020-2024)";
    public string Description =>
        "F1 series UDP telemetry (default port 20777). In-game: Telemetry " +
        "settings → UDP enabled, IP = this machine, format matches 2020-2024.";
    public int DefaultPort => 20777;

    public IReadOnlyList<string> ProcessNames { get; } = new[]
    {
        "F1_24", "F1_23", "F1_22", "F1_2021", "F1_2020",
    };

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
        session.DeclareObject("car", CapnpGen.ObjectType.vehicle, "F1 car");
        _rpm     = session.AddChannel("engine.rpm", "rpm", "Current engine rpm");
        _speed   = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        _throttle= session.AddChannel("engine.throttle", "", "Throttle input 0..1");
        _brake   = session.AddChannel("brakes.pedal", "", "Brake input 0..1");
        _gear    = session.AddChannel("f1.gear", "", "Raw gear number (game encoding)");
        _drs     = session.AddBooleanChannel("f1.drs", "DRS active");
    }

    public bool AcceptsDatagram(int length) => length >= 26;

    public void Translate(ReadOnlySpan<byte> d, OgsaSession session, double now)
    {
        if (d.Length < 26) return;
        ushort format = MemoryMarshal.Read<ushort>(d);
        if (format is < 2020 or > 2024) return;
        // 2023+: packetFormat, gameYear, major, minor, packetVersion, packetId,
        //        sessionUID(8), sessionTime, frameId, overallFrameId, playerIdx, secondary
        // 2020-22: same without gameYear and overallFrameId
        int packetId = d[format >= 2023 ? 6 : 5];
        int playerCarIndex = d[format >= 2023 ? 27 : 22];
        int headerSize = format >= 2023 ? 29 : 24;

        switch (packetId)
        {
            case 0: // motion
            {
                int cars = headerSize;
                if (cars + (playerCarIndex + 1) * 60 > d.Length) return;
                int p = cars + playerCarIndex * 60;

                session.SetPose(null, OgsaMath.EulerToQuaternion(
                    _sy * F(d, p + 48), _sp * F(d, p + 52), _sr * F(d, p + 56)));
                session.SetBodyDynamics(
                    new System.Numerics.Vector3(F(d, p + 36), F(d, p + 44), F(d, p + 40)) * 9.81f,
                    default);
                session.Tick(now);
                break;
            }
            case 6 when format >= 2023: // car telemetry (2023+ float layout)
            {
                int p = headerSize + playerCarIndex * 60;
                if (p + 22 > d.Length) return;
                session.SetChannel(_rpm, MemoryMarshal.Read<ushort>(d.Slice(p + 18, 2)));
                session.SetChannel(_speed, MemoryMarshal.Read<ushort>(d.Slice(p, 2)) / 3.6);
                session.SetChannel(_throttle, F(d, p + 4));
                session.SetChannel(_brake, F(d, p + 12));
                session.SetChannel(_gear, (sbyte)d[p + 17]);
                session.SetChannel(_drs, d[p + 20] != 0);
                // channel-only packet: no pose update, but keep the motion
                // heartbeat cadence (values ride the next motion frame)
                break;
            }
        }
    }
}
}
