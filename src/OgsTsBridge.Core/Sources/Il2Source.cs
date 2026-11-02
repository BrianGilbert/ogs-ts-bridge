#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// IL-2 Sturmovik (Great Battles) UDP output — the motion-device stream
/// and, optionally, the telemetry-device stream on the same port. Enable
/// both in data\startup.cfg (game closed):
///
///   [KEY = motiondevice]     addr = "127.0.0.1" decimation = 1 enable = true port = 4321 [END]
///   [KEY = telemetrydevice]  addr = "127.0.0.1" decimation = 1 enable = true port = 4321 [END]
///
/// Motion packet (id 0x494C0000, ≥ 44 bytes): tick @4, yaw / pitch / roll
/// rad @8/12/16, body spin rad/s @20/24/28, body acceleration m/s²
/// @32/36/40. Every motion packet ticks once.
///
/// Telemetry packet (id 0x54000101): u16 size @4, u32 tick @6, record
/// count @10, then records of u16 type + u8 float count + floats. Every
/// record type is mapped, per engine / per gear up to four: 0 engine rpm,
/// 1 intake manifold pressure (Pa), 2 and 3 unidentified engine values,
/// 4 landing gear positions, 5 landing gear pressures, 6 indicated
/// airspeed m/s, 7 unidentified g-related value, 8 acceleration
/// (longitudinal, vertical, lateral), 9 stall buffet frequency and
/// amplitude, 10 height above ground m, 11 flaps, 12 air brake. Unknown
/// record types are skipped; events that may follow the records are not
/// decoded. Telemetry packets update channels only — the next motion
/// packet carries them; they never tick.
///
/// Body axes assumed right-handed X-forward / Y-up / Z-right
/// (longitudinal, vertical, lateral) → OGS body (z, y, x); rates as
/// (ωz, −ωy, ωx); Euler mapped direct; acceleration taken as gravity-free
/// with the specific force derived (all unverified against the live game
/// — flip options and `gravIncluded` exist).
/// </summary>
public sealed class Il2Source : ILegacySource
{
    internal const uint MotionId = 0x494C0000;
    internal const uint TelemetryId = 0x54000101;
    const int MaxUnits = 4;

    readonly OgsChannel[] _rpm = new OgsChannel[MaxUnits], _manifold = new OgsChannel[MaxUnits],
                          _state2 = new OgsChannel[MaxUnits], _gearPos = new OgsChannel[MaxUnits],
                          _gearLoad = new OgsChannel[MaxUnits];
    OgsChannel _tick, _engineCount, _gearCount, _gear, _state3, _ias, _state7,
               _accLong, _accVert, _accLat, _buffetFreq, _buffetAmp, _agl, _flaps, _airbrake;
    double _sy = 1, _sp = 1, _sr = 1;
    bool _gravIncluded;

    public string Id => "il2";
    public string Title => "IL-2 Sturmovik (motion + telemetry devices)";
    public string Description =>
        "IL-2 Great Battles motiondevice/telemetrydevice UDP (port 4321). " +
        "Enable both in data\\startup.cfg, pointed at this machine.";
    public int DefaultPort => 4321;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "Il-2" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("IL-2 Sturmovik: Great Battles", "Il-2"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",      "negate yaw Euler + yaw-rate sign (bool, default false)"),
        ("flipPitch",    "negate pitch Euler + pitch-rate sign (bool, default false)"),
        ("flipRoll",     "negate roll Euler + roll-rate sign (bool, default false)"),
        ("gravIncluded", "acceleration already includes gravity reaction (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "gravIncluded");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _gravIncluded = o.GetBool("gravIncluded", false);
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("aircraft", Ogs.Telemetry.Schema.ObjectType.Aircraft, "IL-2 aircraft");
        session.SendChangedChannelsOnly();
        _tick        = session.AddChannel("il2.tick", "", "Motion packet tick counter");
        _engineCount = session.AddChannel("engine.count", "", "Engines reported");
        for (int i = 0; i < MaxUnits; i++)
        {
            _rpm[i]      = session.AddChannel(i == 0 ? "engine.rpm" : $"engine[{i}].rpm", "rpm", $"Engine {i + 1} rpm");
            _manifold[i] = session.AddChannel($"engine[{i}].manifoldPressure", "Pa", $"Engine {i + 1} intake manifold pressure");
            _state2[i]   = session.AddChannel($"il2.state2[{i}]", "", $"Engine {i + 1} unidentified value (record 2)");
        }
        _state3     = session.AddChannel("il2.state3", "", "Unidentified value (record 3)");
        _gearCount  = session.AddChannel("gear.count", "", "Landing gears reported");
        for (int i = 0; i < MaxUnits; i++)
        {
            _gearPos[i]  = session.AddChannel($"gear[{i}].position", "", $"Gear {i + 1} extension 0..1");
            _gearLoad[i] = session.AddChannel($"gear[{i}].load", "", $"Gear {i + 1} load 0..1 (game units)");
        }
        _gear       = session.AddChannel("gear.position", "", "Landing gear extension 0..1 (mean)");
        _ias        = session.AddChannel("air.indicatedAirspeed", "m/s", "Indicated airspeed");
        _state7     = session.AddChannel("il2.state7", "", "Unidentified g-related value (record 7)");
        _accLong    = session.AddChannel("il2.accel.longitudinal", "m/s2", "Telemetry acceleration, longitudinal");
        _accVert    = session.AddChannel("il2.accel.vertical", "m/s2", "Telemetry acceleration, vertical");
        _accLat     = session.AddChannel("il2.accel.lateral", "m/s2", "Telemetry acceleration, lateral");
        _buffetFreq = session.AddChannel("haptics.stallBuffet.frequency", "Hz", "Stall buffet frequency");
        _buffetAmp  = session.AddChannel("haptics.stallBuffet.amplitude", "", "Stall buffet amplitude");
        _agl        = session.AddChannel("env.heightFromGround", "m", "Height above ground");
        _flaps      = session.AddChannel("ctrl.flaps", "", "Flap position 0..1");
        _airbrake   = session.AddChannel("ctrl.spoilers", "", "Air brake position 0..1");
    }

    public bool AcceptsDatagram(int length) => length >= 11;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        uint id = MemoryMarshal.Read<uint>(d);
        if (id == TelemetryId) { ReadTelemetry(d, session); return; }
        if (id != MotionId || d.Length < 44) return;

        var q = OgsMath.EulerToQuaternion(_sy * F(d, 8), _sp * F(d, 12), _sr * F(d, 16));
        session.SetPose(null, q);

        // IL-2 body (X fwd, Y up, Z right, assumed) -> OGS body (x right, y up, z fwd)
        var accel = new Vector3(F(d, 40), F(d, 36), F(d, 32));
        var rates = new Vector3((float)(_sp * F(d, 28)), (float)(_sy * -F(d, 24)), (float)(_sr * F(d, 20)));
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, q), rates);
        session.SetChannel(_tick, MemoryMarshal.Read<uint>(d.Slice(4, 4)));

        session.Tick(now);
    }

    void ReadTelemetry(ReadOnlySpan<byte> d, OgsSession session)
    {
        int records = d[10];
        int at = 11;
        for (int r = 0; r < records && at + 3 <= d.Length; r++)
        {
            ushort type = MemoryMarshal.Read<ushort>(d.Slice(at, 2));
            int count = d[at + 2];
            at += 3;
            if (at + count * 4 > d.Length) return; // truncated
            int n = Math.Min(count, MaxUnits);
            if (count > 0)
            {
                switch (type)
                {
                    case 0:
                        session.SetChannel(_engineCount, count);
                        for (int i = 0; i < n; i++) session.SetChannel(_rpm[i], F(d, at + i * 4));
                        break;
                    case 1: for (int i = 0; i < n; i++) session.SetChannel(_manifold[i], F(d, at + i * 4)); break;
                    case 2: for (int i = 0; i < n; i++) session.SetChannel(_state2[i], F(d, at + i * 4)); break;
                    case 3: session.SetChannel(_state3, F(d, at)); break;
                    case 4:
                        float sum = 0;
                        for (int i = 0; i < count; i++) sum += F(d, at + i * 4);
                        for (int i = 0; i < n; i++) session.SetChannel(_gearPos[i], F(d, at + i * 4));
                        session.SetChannel(_gearCount, count);
                        session.SetChannel(_gear, sum / count);
                        break;
                    case 5: for (int i = 0; i < n; i++) session.SetChannel(_gearLoad[i], F(d, at + i * 4)); break;
                    case 6: session.SetChannel(_ias, F(d, at)); break;
                    case 7: session.SetChannel(_state7, F(d, at)); break;
                    case 8 when count >= 3:
                        session.SetChannel(_accLong, F(d, at));
                        session.SetChannel(_accVert, F(d, at + 4));
                        session.SetChannel(_accLat, F(d, at + 8));
                        break;
                    case 9 when count >= 2:
                        session.SetChannel(_buffetFreq, F(d, at));
                        session.SetChannel(_buffetAmp, F(d, at + 4));
                        break;
                    case 10: session.SetChannel(_agl, F(d, at)); break;
                    case 11: session.SetChannel(_flaps, F(d, at)); break;
                    case 12: session.SetChannel(_airbrake, F(d, at)); break;
                }
            }
            at += count * 4;
        }
    }
}
}
