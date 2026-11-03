#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Gran Turismo 7 Simulator Interface (PS4/PS5, over the network). The
/// console streams to whoever sends it a heartbeat on UDP 33739 and stops
/// ~10 s after the last one; replies arrive on 33740. The heartbeat byte
/// selects the packet: "A" type 1 (296 bytes), "B" type 2 (316, adds
/// steering angle and sway/heave/surge), "~" type 3 (344, adds energy
/// recovery and flags). Packets are Salsa20-encrypted with the key
/// "Simulator Interface Packet GT7 v" and an 8-byte nonce built from the
/// u32 at 0x40 (XOR 0xDEADBEAF / 0xDEADBEEF / 0x55FABB4F by type);
/// decrypted, they start with the magic "0S7G" (0x47375330).
///
/// Every field is mapped (layout per Nenkai's PDTools): world position,
/// velocity, rotation quaternion, angular velocity, ride height, engine
/// and turbo, fuel, oil and water, tyre temperatures, lap and race data,
/// flags, gears, pedals, road plane, per-wheel speed, radius and
/// suspension height, clutch, transmission and gear ratios, car code, and
/// the type 2/3 extensions.
///
/// Frame: the quaternion, world vectors and angular velocity are mapped
/// through `axes` (default x,y,z) and the angular velocity taken as body
/// rates; with type 2+ the sway/heave/surge values are taken as body
/// acceleration in m/s², gravity-free (else the acceleration is the
/// world-velocity derivative) — all unverified against the live game.
/// Wheel speeds are taken as rad/s. Nothing is emitted while loading or
/// with the car off track; pause rides session.paused.
/// </summary>
public sealed class Gt7Source : ILegacySource, IUdpHeartbeat
{
    const uint Magic = 0x47375330;
    const double Rpm = 60 / (2 * Math.PI);

    static readonly uint[] Key = KeyWords("Simulator Interface Packet GT7 v");

    readonly byte[] _plain = new byte[0x158];
    int _type = 3;
    int _expected = 0x158;
    uint _xor = 0x55FABB4F;
    byte[] _heartbeat = "~"u8.ToArray();
    IPEndPoint? _target = new(IPAddress.Broadcast, 33739);
    AxisMap _axes = AxisMap.Identity;
    bool _gravIncluded;
    Vector3 _prevVelocity;
    double _prevTime = double.NaN;

    readonly int[] _wheel = new int[4];
    OgsChannel[] _f = Array.Empty<OgsChannel>();
    OgsChannel _paused, _onTrack, _loading, _inGear, _turbo, _revLimit, _handbrake, _lights, _highBeam,
        _lowBeam, _asm, _tcs, _gear, _suggestedGear, _throttle, _brake, _fuel;

    /// <summary>Float fields: offset, name, unit, description, minimum packet type.</summary>
    static readonly (int Off, string Name, string Unit, string Description, int Type)[] Floats =
    {
        (0x04, "gt7.position.x", "m", "World position X", 1), (0x08, "gt7.position.y", "m", "World position Y", 1),
        (0x0C, "gt7.position.z", "m", "World position Z", 1),
        (0x10, "gt7.velocity.x", "m/s", "World velocity X", 1), (0x14, "gt7.velocity.y", "m/s", "World velocity Y", 1),
        (0x18, "gt7.velocity.z", "m/s", "World velocity Z", 1),
        (0x2C, "gt7.angularVelocity.x", "rad/s", "Angular velocity X", 1), (0x30, "gt7.angularVelocity.y", "rad/s", "Angular velocity Y", 1),
        (0x34, "gt7.angularVelocity.z", "rad/s", "Angular velocity Z", 1),
        (0x38, "gt7.bodyHeight", "m", "Body height above ground", 1), (0x3C, "engine.rpm", "rpm", "Engine speed", 1),
        (0x44, "gt7.fuelLevel", "", "Fuel level (as sent)", 1), (0x48, "gt7.fuelCapacity", "", "Fuel capacity (as sent)", 1),
        (0x4C, "vehicle.speed", "m/s", "Speed", 1), (0x50, "gt7.turboBoost", "", "Turbo boost (as sent; −1 offset)", 1),
        (0x54, "gt7.oilPressure", "", "Oil pressure (as sent)", 1), (0x58, "gt7.waterTemperature", "degC", "Water temperature", 1),
        (0x5C, "gt7.oilTemperature", "degC", "Oil temperature", 1),
        (0x60, "gt7.tyreTemp.fl", "degC", "Tyre surface temperature, front left", 1),
        (0x64, "gt7.tyreTemp.fr", "degC", "Tyre surface temperature, front right", 1),
        (0x68, "gt7.tyreTemp.rl", "degC", "Tyre surface temperature, rear left", 1),
        (0x6C, "gt7.tyreTemp.rr", "degC", "Tyre surface temperature, rear right", 1),
        (0x94, "gt7.roadPlane.x", "", "Road plane normal X", 1), (0x98, "gt7.roadPlane.y", "", "Road plane normal Y", 1),
        (0x9C, "gt7.roadPlane.z", "", "Road plane normal Z", 1), (0xA0, "gt7.roadPlaneDistance", "m", "Road plane distance", 1),
        (0xA4, "gt7.wheelSpeed.fl", "rad/s", "Wheel speed, front left", 1), (0xA8, "gt7.wheelSpeed.fr", "rad/s", "Wheel speed, front right", 1),
        (0xAC, "gt7.wheelSpeed.rl", "rad/s", "Wheel speed, rear left", 1), (0xB0, "gt7.wheelSpeed.rr", "rad/s", "Wheel speed, rear right", 1),
        (0xB4, "gt7.tyreRadius.fl", "m", "Tyre radius, front left", 1), (0xB8, "gt7.tyreRadius.fr", "m", "Tyre radius, front right", 1),
        (0xBC, "gt7.tyreRadius.rl", "m", "Tyre radius, rear left", 1), (0xC0, "gt7.tyreRadius.rr", "m", "Tyre radius, rear right", 1),
        (0xC4, "gt7.suspensionHeight.fl", "m", "Suspension height, front left", 1),
        (0xC8, "gt7.suspensionHeight.fr", "m", "Suspension height, front right", 1),
        (0xCC, "gt7.suspensionHeight.rl", "m", "Suspension height, rear left", 1),
        (0xD0, "gt7.suspensionHeight.rr", "m", "Suspension height, rear right", 1),
        (0xF4, "input.clutch", "", "Clutch pedal 0..1", 1), (0xF8, "gt7.clutchEngagement", "", "Clutch engagement 0..1", 1),
        (0xFC, "gt7.clutchToGearboxRpm", "rpm", "Speed from clutch to gearbox", 1),
        (0x100, "gt7.transmissionTopSpeed", "", "Transmission top speed (as sent)", 1),
        (0x104, "gt7.gearRatio1", "", "Gear 1 ratio", 1), (0x108, "gt7.gearRatio2", "", "Gear 2 ratio", 1),
        (0x10C, "gt7.gearRatio3", "", "Gear 3 ratio", 1), (0x110, "gt7.gearRatio4", "", "Gear 4 ratio", 1),
        (0x114, "gt7.gearRatio5", "", "Gear 5 ratio", 1), (0x118, "gt7.gearRatio6", "", "Gear 6 ratio", 1),
        (0x11C, "gt7.gearRatio7", "", "Gear 7 ratio", 1), (0x120, "gt7.gearRatio8", "", "Gear 8 ratio (cars with 8+ gears)", 1),
        (0x128, "gt7.wheelRotation", "rad", "Steering wheel rotation", 2), (0x12C, "gt7.filler0x12C", "", "Unidentified (0x12C)", 2),
        (0x130, "gt7.sway", "m/s2", "Sway (as sent)", 2), (0x134, "gt7.heave", "m/s2", "Heave (as sent)", 2),
        (0x138, "gt7.surge", "m/s2", "Surge (as sent)", 2),
        (0x140, "gt7.unk0x140", "", "Unidentified (0x140)", 3), (0x144, "gt7.unk0x144", "", "Unidentified (0x144)", 3),
        (0x148, "gt7.unk0x148", "", "Unidentified (0x148)", 3), (0x14C, "gt7.unk0x14C", "", "Unidentified (0x14C)", 3),
        (0x150, "gt7.energyRecovery", "", "Energy recovery", 3), (0x154, "gt7.unk0x154", "", "Unidentified (0x154)", 3),
    };

    /// <summary>Integer fields: offset, size (1/2/4), name, unit, description, scale, minimum packet type.</summary>
    static readonly (int Off, int Size, string Name, string Unit, string Description, double Scale, int Type)[] Ints =
    {
        (0x70, 4, "gt7.packetId", "", "Packet counter", 1, 1), (0x74, 2, "race.lap", "", "Current lap", 1, 1),
        (0x76, 2, "gt7.lapsInRace", "", "Laps in race", 1, 1), (0x78, 4, "gt7.bestLapTime", "s", "Best lap time (−0.001 = none)", 0.001, 1),
        (0x7C, 4, "gt7.lastLapTime", "s", "Last lap time (−0.001 = none)", 0.001, 1),
        (0x80, 4, "gt7.timeOfDay", "s", "Time of day", 0.001, 1),
        (0x84, 2, "race.position", "", "Pre-race start position or qualifying position", 1, 1),
        (0x86, 2, "gt7.numCars", "", "Cars at pre-race", 1, 1), (0x88, 2, "gt7.minAlertRpm", "rpm", "Shift light minimum", 1, 1),
        (0x8A, 2, "gt7.maxAlertRpm", "rpm", "Shift light maximum", 1, 1),
        (0x8C, 2, "gt7.calculatedMaxSpeed", "m/s", "Calculated maximum speed", 1 / 3.6, 1),
        (0x124, 4, "gt7.carCode", "", "Car code", 1, 1),
        (0x13C, 1, "gt7.unk0x13C", "", "Unidentified (0x13C)", 1, 3), (0x13D, 1, "gt7.unk0x13D", "", "Unidentified (0x13D)", 1, 3),
        (0x13E, 1, "gt7.powertrain", "", "Unidentified (0x13E; 4 = electric)", 1, 3),
        (0x13F, 1, "gt7.noGasConsumption", "", "No fuel consumption setting", 1, 3),
    };

    public string Id => "gt7";
    public string Title => "Gran Turismo 7 (Simulator Interface)";
    public string Description =>
        "GT7 Simulator Interface from a PS4/PS5 on the network: the Bridge " +
        "sends heartbeats to UDP 33739 (broadcast, or --set console=IP) and " +
        "listens on 33740.";
    public int DefaultPort => 33740;

    // console game: no PC process to watch — start it from the GUI or pin it
    public IReadOnlyList<string> ProcessNames { get; } = Array.Empty<string>();
    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Gran Turismo 7 (PS4/PS5 on the network)", ""),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("console", "PlayStation IP address for heartbeats (default: broadcast)"),
        ("packet", "packet type 1, 2 or 3 (default 3)"),
        ("axes", "game components feeding OGS x,y,z (default x,y,z)"),
        ("gravIncluded", "sway/heave/surge already include gravity reaction (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("console", "packet", "axes", "gravIncluded");
        var console = o.GetString("console", "");
        _target = new IPEndPoint(console.Length == 0 ? IPAddress.Broadcast : IPAddress.Parse(console), 33739);
        _type = (int)o.GetDouble("packet", 3);
        (_heartbeat, _xor, _expected) = _type switch
        {
            1 => ("A"u8.ToArray(), 0xDEADBEAFu, 0x128),
            2 => ("B"u8.ToArray(), 0xDEADBEEFu, 0x13C),
            3 => ("~"u8.ToArray(), 0x55FABB4Fu, 0x158),
            _ => throw new ArgumentException("packet must be 1, 2 or 3"),
        };
        _axes = AxisMap.Parse(o.GetString("axes", "x,y,z"));
        _gravIncluded = o.GetBool("gravIncluded", false);
    }

    public IPEndPoint? HeartbeatTarget => _target;
    public byte[] HeartbeatPayload => _heartbeat;
    public double HeartbeatIntervalSeconds => 1.0;

    public void Declare(OgsSession session)
    {
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "GT7 car");
        session.SendChangedChannelsOnly();
        var channels = new List<OgsChannel>();
        foreach (var f in Floats)
            if (f.Type <= _type) channels.Add(session.AddChannel(f.Name, f.Unit, f.Description));
        foreach (var i in Ints)
            if (i.Type <= _type) channels.Add(session.AddChannel(i.Name, i.Unit, i.Description));
        _f = channels.ToArray();

        _paused = session.AddBooleanChannel("session.paused", "Game paused");
        session.AlwaysSend(_paused);
        _onTrack   = session.AddBooleanChannel("gt7.carOnTrack", "Car on track");
        _loading   = session.AddBooleanChannel("gt7.loading", "Loading or processing");
        _inGear    = session.AddBooleanChannel("gt7.inGear", "In gear");
        _turbo     = session.AddBooleanChannel("gt7.hasTurbo", "Car has a turbo");
        _revLimit  = session.AddBooleanChannel("gt7.revLimiterAlert", "Rev limiter alert");
        _handbrake = session.AddBooleanChannel("gt7.handbrake", "Handbrake on");
        _lights    = session.AddBooleanChannel("lights.headlights", "Lights on");
        _highBeam  = session.AddBooleanChannel("lights.highBeam", "High beam on");
        _lowBeam   = session.AddBooleanChannel("gt7.lowBeam", "Low beam on");
        _asm       = session.AddBooleanChannel("aids.esp", "Active stability management active");
        _tcs       = session.AddBooleanChannel("aids.tcs", "Traction control active");
        _gear      = session.AddChannel("gt7.currentGear", "", "Current gear (raw 4 bits)");
        _suggestedGear = session.AddChannel("gt7.suggestedGear", "", "Suggested gear (raw 4 bits; 15 = none)");
        _throttle  = session.AddChannel("engine.throttle", "", "Throttle 0..1");
        _brake     = session.AddChannel("brakes.pedal", "", "Brake 0..1");
        _fuel      = session.AddChannel("fuel.level", "", "Fuel level / capacity");

        string[] corner = { "fl", "fr", "rl", "rr" };
        for (int w = 0; w < 4; w++)
            _wheel[w] = session.AddWheel("wheel." + corner[w], Vector3.Zero, 0.33f,
                driven: false, steered: w < 2, suspension: false);
    }

    public bool AcceptsDatagram(int length) => length >= 0x128;

    float F(int off) => MemoryMarshal.Read<float>(_plain.AsSpan(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (d.Length < _expected) return;
        var p = _plain.AsSpan(0, _expected);
        d[.._expected].CopyTo(p);
        uint iv1 = MemoryMarshal.Read<uint>(p.Slice(0x40, 4));
        Salsa20Xor(Key, iv1 ^ _xor, iv1, p);
        if (MemoryMarshal.Read<uint>(p) != Magic) return;

        ushort flags = MemoryMarshal.Read<ushort>(p.Slice(0x8E, 2));
        bool onTrack = (flags & 1) != 0, paused = (flags & 2) != 0, loading = (flags & 4) != 0;
        if (loading || !onTrack) { _prevTime = double.NaN; return; }

        int c = 0;
        foreach (var f in Floats)
            if (f.Type <= _type) session.SetChannel(_f[c++], F(f.Off));
        foreach (var i in Ints)
        {
            if (i.Type > _type) continue;
            double raw = i.Size switch
            {
                1 => p[i.Off],
                2 => MemoryMarshal.Read<short>(p.Slice(i.Off, 2)),
                _ => MemoryMarshal.Read<int>(p.Slice(i.Off, 4)),
            };
            session.SetChannel(_f[c++], raw * i.Scale);
        }
        session.SetChannel(_paused, paused);
        session.SetChannel(_onTrack, onTrack);
        session.SetChannel(_loading, loading);
        session.SetChannel(_inGear, (flags & 8) != 0);
        session.SetChannel(_turbo, (flags & 16) != 0);
        session.SetChannel(_revLimit, (flags & 32) != 0);
        session.SetChannel(_handbrake, (flags & 64) != 0);
        session.SetChannel(_lights, (flags & 128) != 0);
        session.SetChannel(_highBeam, (flags & 256) != 0);
        session.SetChannel(_lowBeam, (flags & 512) != 0);
        session.SetChannel(_asm, (flags & 1024) != 0);
        session.SetChannel(_tcs, (flags & 2048) != 0);
        byte gears = p[0x90];
        session.SetChannel(_gear, gears & 0xF);
        session.SetChannel(_suggestedGear, gears >> 4);
        session.SetChannel(_throttle, p[0x91] / 255.0);
        session.SetChannel(_brake, p[0x92] / 255.0);
        float capacity = F(0x48);
        if (capacity > 0) session.SetChannel(_fuel, F(0x44) / capacity);

        var position = _axes.Vector(new Vector3(F(0x04), F(0x08), F(0x0C)));
        var q = Quaternion.Normalize(_axes.Rotation(new Quaternion(F(0x1C), F(0x20), F(0x24), F(0x28))));
        session.SetPose(position, q);
        var velocity = _axes.Vector(new Vector3(F(0x10), F(0x14), F(0x18)));
        Vector3 accel;
        if (_type >= 2) accel = _axes.Vector(new Vector3(F(0x130), F(0x134), F(0x138)));
        else
        {
            // no acceleration in type 1: the world-velocity derivative, in body axes
            double dt = now - _prevTime;
            var world = double.IsNaN(dt) || dt <= 0 ? Vector3.Zero : (velocity - _prevVelocity) / (float)dt;
            accel = Vector3.Transform(world, Quaternion.Conjugate(q));
        }
        _prevVelocity = velocity;
        _prevTime = now;
        var rates = _axes.Angular(new Vector3(F(0x2C), F(0x30), F(0x34)));
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, q), rates);

        for (int w = 0; w < 4; w++)
            session.SetWheel(_wheel[w], (float)(F(0xA4 + w * 4) * Rpm), float.NaN, float.NaN,
                Ogs.Telemetry.Schema.ContactMedium.Asphalt);

        session.Tick(now);
    }

    // ---- Salsa20 (20 rounds, 256-bit key, 64-bit nonce, counter from 0) ----

    static uint[] KeyWords(string key)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(key);
        var words = new uint[8];
        for (int i = 0; i < 8; i++) words[i] = BitConverter.ToUInt32(bytes, i * 4);
        return words;
    }

    /// <summary>XORs the Salsa20 keystream into <paramref name="data"/> in place.</summary>
    internal static void Salsa20Xor(uint[] key, uint nonce0, uint nonce1, Span<byte> data)
    {
        Span<uint> s = stackalloc uint[16];
        Span<uint> x = stackalloc uint[16];
        Span<byte> block = stackalloc byte[64];
        ulong counter = 0;
        for (int off = 0; off < data.Length; off += 64, counter++)
        {
            s[0] = 0x61707865; s[1] = key[0]; s[2] = key[1]; s[3] = key[2]; s[4] = key[3];
            s[5] = 0x3320646e; s[6] = nonce0; s[7] = nonce1; s[8] = (uint)counter; s[9] = (uint)(counter >> 32);
            s[10] = 0x79622d32; s[11] = key[4]; s[12] = key[5]; s[13] = key[6]; s[14] = key[7];
            s[15] = 0x6b206574;
            s.CopyTo(x);
            for (int r = 0; r < 10; r++)
            {
                Quarter(x, 0, 4, 8, 12); Quarter(x, 5, 9, 13, 1); Quarter(x, 10, 14, 2, 6); Quarter(x, 15, 3, 7, 11);
                Quarter(x, 0, 1, 2, 3); Quarter(x, 5, 6, 7, 4); Quarter(x, 10, 11, 8, 9); Quarter(x, 15, 12, 13, 14);
            }
            for (int i = 0; i < 16; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * 4), x[i] + s[i]);
            int n = Math.Min(64, data.Length - off);
            for (int i = 0; i < n; i++) data[off + i] ^= block[i];
        }
    }

    static void Quarter(Span<uint> x, int a, int b, int c, int d)
    {
        x[b] ^= uint.RotateLeft(x[a] + x[d], 7);
        x[c] ^= uint.RotateLeft(x[b] + x[a], 9);
        x[d] ^= uint.RotateLeft(x[c] + x[b], 13);
        x[a] ^= uint.RotateLeft(x[d] + x[c], 18);
    }
}
}
