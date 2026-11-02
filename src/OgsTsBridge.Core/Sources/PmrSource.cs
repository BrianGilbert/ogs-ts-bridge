#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Project Motor Racing UDP telemetry: multicast group 224.0.0.150, port
/// 7576. Three packet types, each a type byte then a u16 version:
///
/// - 0 race info (v1): track, layout, season, weather, session and game
///   mode (u8-length strings), layout length, duration, overtime, track
///   temperature, isLaps, state (0 inactive, 1 active, 2 complete),
///   participant count;
/// - 1 participant race state (v1): vehicle id, isPlayer, vehicle and
///   driver names (later fields of this packet are not mapped: their
///   layout is unconfirmed);
/// - 2 vehicle telemetry (v2): vehicle id, per-wheel blocks (contact,
///   wheel, tyre, thermal, suspension, drivetrain), chassis (world pose,
///   world and local angular velocity, velocity and acceleration, speeds,
///   sideslip), drivetrain (engine, turbo, fuel, hybrid, gearbox, flags,
///   per-gear shift points), suspension loads, inputs, setup, general and
///   constant vehicle data.
///
/// Every field of the three packets is mapped. Telemetry ticks for the
/// player's vehicle (from the race-state packet; until it arrives, the
/// first vehicle seen). Wheel and gear counts come from the stream; a
/// change redeclares.
///
/// Frame: undocumented. The local and world vectors and the quaternion are
/// mapped through the `axes` option (default "z,y,-x": OGS right = game z,
/// up = game y, forward = −game x), and the local acceleration is taken as
/// gravity-free with the specific force derived (`gravIncluded` to
/// override) — all unverified against the live game. Power is taken as
/// kW, temperatures as °C, angles as rad; other units are published as
/// sent.
/// </summary>
public sealed class PmrSource : ILegacySource
{
    const double Rpm = 60 / (2 * Math.PI);

    // vehicle-telemetry float runs, in packet order: name, unit, description, scale
    static readonly (string, string, string, double)[] WheelFloats =
    {
        ("angVel", "rad/s", "Wheel angular velocity", 1), ("linearSpeed", "m/s", "Wheel linear speed", 1),
        ("slide.x", "", "Tyre slide X (local)", 1), ("slide.y", "", "Tyre slide Y (local)", 1), ("slide.z", "", "Tyre slide Z (local)", 1),
        ("force.x", "N", "Tyre force X (local)", 1), ("force.y", "N", "Tyre force Y (local)", 1), ("force.z", "N", "Tyre force Z (local)", 1),
        ("moment.x", "Nm", "Tyre moment X (local)", 1), ("moment.y", "Nm", "Tyre moment Y (local)", 1), ("moment.z", "Nm", "Tyre moment Z (local)", 1),
        ("contactRadius", "m", "Contact radius", 1), ("pressure", "", "Tyre pressure (as sent)", 1),
        ("inclination", "rad", "Inclination", 1), ("slipRatio", "", "Slip ratio", 1), ("slipAngle", "rad", "Slip angle", 1),
        ("tread.inner", "degC", "Tread temperature, inner", 1), ("tread.middle", "degC", "Tread temperature, middle", 1),
        ("tread.outer", "degC", "Tread temperature, outer", 1), ("carcass", "degC", "Carcass temperature", 1),
        ("internalAir", "degC", "Internal air temperature", 1), ("wellAir", "degC", "Wheel-well air temperature", 1),
        ("rim", "degC", "Rim temperature", 1), ("brake", "degC", "Brake temperature", 1),
        ("springStrain", "", "Spring strain", 1), ("damperVelocity", "m/s", "Damper velocity", 1),
        ("hubTorque", "Nm", "Hub torque", 1), ("hubPower", "W", "Hub power", 1000),
        ("wheelTorque", "Nm", "Wheel torque", 1), ("wheelPower", "W", "Wheel power", 1000),
    };

    static readonly (string, string, string, double)[] DrivetrainFloats =
    {
        ("engine.rpm", "rpm", "Engine speed", 1), ("pmr.engineRevRatio", "", "Engine rev ratio", 1),
        ("engine.torque", "Nm", "Engine torque", 1), ("engine.power", "W", "Engine power", 1000),
        ("pmr.engineLoad", "", "Engine load", 1), ("pmr.turboRpm", "rpm", "Turbo speed", 1),
        ("pmr.turboBoostPressure", "", "Turbo boost pressure (as sent)", 1), ("pmr.fuelRemaining", "", "Fuel remaining (as sent)", 1),
        ("pmr.fuelUseRate", "", "Fuel use rate (as sent)", 1), ("pmr.oilPressure", "", "Oil pressure (as sent)", 1),
        ("pmr.oilTemperature", "degC", "Oil temperature", 1), ("pmr.coolantTemperature", "degC", "Coolant temperature", 1),
        ("pmr.exhaustGasTemperature", "degC", "Exhaust gas temperature", 1), ("pmr.motorRpm", "rpm", "Hybrid motor speed", 1),
        ("pmr.batteryRemaining", "", "Battery remaining (as sent, kWh)", 1), ("pmr.batteryUseRate", "", "Battery use rate (as sent)", 1),
        ("pmr.transmissionRpm", "rpm", "Transmission speed", 1), ("pmr.gearboxInputRpm", "rpm", "Gearbox input speed", 1),
        ("pmr.gearboxOutputRpm", "rpm", "Gearbox output speed", 1), ("pmr.gearboxTorque", "Nm", "Gearbox torque", 1),
        ("pmr.gearboxPower", "W", "Gearbox power", 1000), ("pmr.gearboxLoadIn", "", "Gearbox load in", 1),
        ("pmr.gearboxLoadOut", "", "Gearbox load out", 1), ("pmr.timeSinceShift", "s", "Time since last shift", 1),
        ("pmr.estDrivenSpeed", "m/s", "Estimated driven speed", 1), ("pmr.outputTorque", "Nm", "Drivetrain output torque", 1),
        ("pmr.outputPower", "W", "Drivetrain output power", 1000), ("pmr.outputEfficiency", "", "Drivetrain output efficiency", 1),
    };

    static readonly (string, string)[] DrivetrainFlags =
    {
        ("pmr.starterActive", "Starter active"), ("engine.running", "Engine running"),
        ("pmr.engineFanRunning", "Engine fan running"), ("pmr.revLimiterActive", "Rev limiter active"),
        ("aids.tcs", "Traction control active"), ("pmr.speedLimiterEnabled", "Pit speed limiter enabled"),
        ("pmr.speedLimiterActive", "Pit speed limiter active"),
    };

    static readonly (string, string, string, double)[] InputFloats =
    {
        ("pmr.steeringInput", "", "Steering input", 1), ("engine.throttle", "", "Accelerator 0..1", 1),
        ("brakes.pedal", "", "Brake 0..1", 1), ("input.clutch", "", "Clutch 0..1", 1), ("pmr.handbrake", "", "Handbrake 0..1", 1),
    };

    static readonly (string, string, string, double)[] SetupFloats =
    {
        ("pmr.brakeBias", "", "Brake bias", 1), ("pmr.frontAntiRollStiffness", "", "Front anti-roll stiffness", 1),
        ("pmr.rearAntiRollStiffness", "", "Rear anti-roll stiffness", 1), ("pmr.regenLimit", "", "Regen limit", 1),
        ("pmr.deployLimit", "", "Deploy limit", 1),
    };

    static readonly (string, string, string, double)[] GeneralFloats =
    {
        ("pmr.centerOfGravity.x", "m", "Centre of gravity X (local)", 1), ("pmr.centerOfGravity.y", "m", "Centre of gravity Y (local)", 1),
        ("pmr.centerOfGravity.z", "m", "Centre of gravity Z (local)", 1), ("pmr.steeringWheelAngle", "rad", "Steering wheel angle", 1),
        ("pmr.totalMass", "kg", "Total mass", 1), ("pmr.drivenWheelAngVel", "rad/s", "Driven wheel angular velocity", 1),
        ("pmr.nonDrivenWheelAngVel", "rad/s", "Non-driven wheel angular velocity", 1), ("pmr.estRollingSpeed", "m/s", "Estimated rolling speed", 1),
        ("pmr.estLinearSpeed", "m/s", "Estimated linear speed", 1), ("pmr.totalBrakeForce", "N", "Total brake force", 1),
    };

    static readonly (string, string, string, double)[] ConstantFloats =
    {
        ("pmr.chassisBBMin.x", "m", "Chassis bounds min X", 1), ("pmr.chassisBBMin.y", "m", "Chassis bounds min Y", 1),
        ("pmr.chassisBBMin.z", "m", "Chassis bounds min Z", 1), ("pmr.chassisBBMax.x", "m", "Chassis bounds max X", 1),
        ("pmr.chassisBBMax.y", "m", "Chassis bounds max Y", 1), ("pmr.chassisBBMax.z", "m", "Chassis bounds max Z", 1),
        ("pmr.starterIdleRpm", "rpm", "Idle speed", 1), ("pmr.torquePeakRpm", "rpm", "Peak-torque speed", 1),
        ("pmr.powerPeakRpm", "rpm", "Peak-power speed", 1), ("engine.rpmMax", "rpm", "Maximum engine speed", 1),
        ("pmr.engineMaxTorque", "Nm", "Maximum engine torque", 1), ("pmr.engineMaxPower", "W", "Maximum engine power", 1000),
        ("pmr.engineMaxBoost", "", "Maximum boost (as sent)", 1), ("pmr.fuelCapacity", "", "Fuel capacity (as sent)", 1),
        ("pmr.batteryCapacity", "", "Battery capacity (as sent)", 1), ("pmr.trackWidthFront", "m", "Front track width", 1),
        ("pmr.trackWidthRear", "m", "Rear track width", 1), ("pmr.wheelbase", "m", "Wheelbase", 1),
    };

    // contact material i32 + the wheel floats; chassis: 28 floats
    const int WheelBytes = 4 + 30 * 4;
    const int ChassisBytes = 28 * 4;

    static readonly string[] Corner = { "fl", "fr", "rl", "rr" };

    // declared shape
    int _wheels = -1, _gears = -1, _loads = -1;
    readonly List<int> _wheelPoints = new();
    OgsChannel[] _wheel = Array.Empty<OgsChannel>();
    OgsChannel[] _wheelMaterial = Array.Empty<OgsChannel>();
    OgsChannel[] _gear = Array.Empty<OgsChannel>();
    OgsChannel[] _load = Array.Empty<OgsChannel>();
    readonly OgsChannel[] _chassis = new OgsChannel[24];
    readonly OgsChannel[] _drive = new OgsChannel[DrivetrainFloats.Length];
    readonly OgsChannel[] _driveFlag = new OgsChannel[DrivetrainFlags.Length];
    readonly OgsChannel[] _input = new OgsChannel[InputFloats.Length];
    readonly OgsChannel[] _setup = new OgsChannel[SetupFloats.Length];
    readonly OgsChannel[] _general = new OgsChannel[GeneralFloats.Length];
    readonly OgsChannel[] _constant = new OgsChannel[ConstantFloats.Length];
    readonly OgsChannel[] _raceText = new OgsChannel[6];
    readonly OgsChannel[] _race = new OgsChannel[7];
    OgsChannel _loadBias, _gearInput, _absLevel, _tcsLevel, _abs, _wheelCount, _fwdGears, _revGears, _hybrid;
    OgsChannel _vehicleId, _driver;

    // the last race-info and player packets, replayed after a redeclare
    // so their values survive the new channel set
    readonly byte[] _lastRace = new byte[2048], _lastPlayer = new byte[2048];
    int _lastRaceLength, _lastPlayerLength;

    AxisMap _axes = AxisMap.Parse("z,y,-x");
    bool _gravIncluded;
    int _playerId = -1, _followId = -1;

    public string Id => "pmr";
    public string Title => "Project Motor Racing (UDP)";
    public string Description =>
        "Project Motor Racing telemetry, UDP multicast 224.0.0.150 port 7576. " +
        "Enable UDP output in the game's settings.";
    public int DefaultPort => 7576;
    public string MulticastGroup => "224.0.0.150";

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "ProjectMotorRacingGame" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Project Motor Racing", "ProjectMotorRacingGame"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("axes", "game components feeding OGS x,y,z, e.g. z,y,-x (default z,y,-x)"),
        ("gravIncluded", "local acceleration already includes gravity reaction (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("axes", "gravIncluded");
        _axes = AxisMap.Parse(o.GetString("axes", "z,y,-x"));
        _gravIncluded = o.GetBool("gravIncluded", false);
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "Project Motor Racing car");
        session.SendChangedChannelsOnly();

        string[] texts = { "track", "layout", "season", "weather", "session", "gameMode" };
        for (int i = 0; i < texts.Length; i++)
            _raceText[i] = session.AddSessionText("pmr." + texts[i], "Race info: " + texts[i]);
        _driver = session.AddSessionText("pmr.driver", "Player driver name");
        (string, string, string)[] race =
        {
            ("pmr.layoutLength", "m", "Layout length"), ("pmr.duration", "s", "Session duration"),
            ("pmr.overtime", "s", "Overtime"), ("pmr.trackTemperature", "degC", "Track temperature"),
            ("pmr.isLaps", "", "Session counted in laps (1) or time (0)"),
            ("pmr.sessionState", "", "0 inactive, 1 active, 2 complete"), ("pmr.participants", "", "Participant count"),
        };
        for (int i = 0; i < race.Length; i++) _race[i] = session.AddChannel(race[i].Item1, race[i].Item2, race[i].Item3);

        _vehicleId = session.AddChannel("pmr.vehicleId", "", "Followed vehicle id");
        (string, string, string)[] chassis =
        {
            ("pmr.position.x", "m", "World position X (game frame)"), ("pmr.position.y", "m", "World position Y (game frame)"),
            ("pmr.position.z", "m", "World position Z (game frame)"),
            ("pmr.angularVelocityWorld.x", "rad/s", "World angular velocity X (game frame)"),
            ("pmr.angularVelocityWorld.y", "rad/s", "World angular velocity Y (game frame)"),
            ("pmr.angularVelocityWorld.z", "rad/s", "World angular velocity Z (game frame)"),
            ("pmr.angularVelocityLocal.x", "rad/s", "Local angular velocity X (game frame)"),
            ("pmr.angularVelocityLocal.y", "rad/s", "Local angular velocity Y (game frame)"),
            ("pmr.angularVelocityLocal.z", "rad/s", "Local angular velocity Z (game frame)"),
            ("pmr.velocityWorld.x", "m/s", "World velocity X (game frame)"), ("pmr.velocityWorld.y", "m/s", "World velocity Y (game frame)"),
            ("pmr.velocityWorld.z", "m/s", "World velocity Z (game frame)"),
            ("pmr.velocityLocal.x", "m/s", "Local velocity X (game frame)"), ("pmr.velocityLocal.y", "m/s", "Local velocity Y (game frame)"),
            ("pmr.velocityLocal.z", "m/s", "Local velocity Z (game frame)"),
            ("pmr.accelerationWorld.x", "m/s2", "World acceleration X (game frame)"),
            ("pmr.accelerationWorld.y", "m/s2", "World acceleration Y (game frame)"),
            ("pmr.accelerationWorld.z", "m/s2", "World acceleration Z (game frame)"),
            ("pmr.accelerationLocal.x", "m/s2", "Local acceleration X (game frame)"),
            ("pmr.accelerationLocal.y", "m/s2", "Local acceleration Y (game frame)"),
            ("pmr.accelerationLocal.z", "m/s2", "Local acceleration Z (game frame)"),
            ("vehicle.speed", "m/s", "Overall speed"), ("pmr.forwardSpeed", "m/s", "Forward speed"),
            ("pmr.sideslip", "rad", "Sideslip angle"),
        };
        for (int i = 0; i < chassis.Length; i++) _chassis[i] = session.AddChannel(chassis[i].Item1, chassis[i].Item2, chassis[i].Item3);

        Add(session, DrivetrainFloats, _drive);
        for (int i = 0; i < DrivetrainFlags.Length; i++)
            _driveFlag[i] = session.AddBooleanChannel(DrivetrainFlags[i].Item1, DrivetrainFlags[i].Item2);
        Add(session, InputFloats, _input);
        _gearInput = session.AddChannel("transmission.gear", "", "Gear: −1 reverse, 0 neutral, 1… forward");
        Add(session, SetupFloats, _setup);
        _absLevel = session.AddChannel("pmr.absLevel", "", "ABS setting");
        _tcsLevel = session.AddChannel("pmr.tcsLevel", "", "Traction control setting");
        Add(session, GeneralFloats, _general);
        _abs = session.AddBooleanChannel("aids.abs", "ABS active");
        Add(session, ConstantFloats, _constant);
        _wheelCount = session.AddChannel("pmr.numberOfWheels", "", "Wheel count");
        _fwdGears = session.AddChannel("pmr.forwardGears", "", "Forward gear count");
        _revGears = session.AddChannel("pmr.reverseGears", "", "Reverse gear count");
        _hybrid = session.AddBooleanChannel("pmr.isHybrid", "Hybrid drivetrain");
        _loadBias = session.AddChannel("pmr.suspensionLoadBias", "", "Suspension load bias");

        int wheels = Math.Max(0, _wheels), gears = Math.Max(0, _gears), loads = Math.Max(0, _loads);
        _wheelPoints.Clear();
        _wheel = new OgsChannel[wheels * WheelFloats.Length];
        _wheelMaterial = new OgsChannel[wheels];
        for (int w = 0; w < wheels; w++)
        {
            var tag = w < Corner.Length && wheels == 4 ? Corner[w] : w.ToString();
            _wheelPoints.Add(session.AddWheel("wheel." + tag, Vector3.Zero, 0.33f,
                driven: false, steered: w < 2, suspension: false));
            _wheelMaterial[w] = session.AddChannel($"pmr.wheel.{tag}.contactMaterial", "", "Contact material hash");
            for (int f = 0; f < WheelFloats.Length; f++)
            {
                var (n, u, d, _) = WheelFloats[f];
                _wheel[w * WheelFloats.Length + f] = session.AddChannel($"pmr.wheel.{tag}.{n}", u, $"{d}, wheel {tag}");
            }
        }
        _gear = new OgsChannel[gears * 2];
        for (int g = 0; g < gears; g++)
        {
            _gear[g * 2] = session.AddChannel($"pmr.gear{g + 1}.upshiftRpm", "rpm", $"Gear {g + 1} upshift speed");
            _gear[g * 2 + 1] = session.AddChannel($"pmr.gear{g + 1}.downshiftRpm", "rpm", $"Gear {g + 1} downshift speed");
        }
        _load = new OgsChannel[loads];
        for (int l = 0; l < loads; l++)
            _load[l] = session.AddChannel($"pmr.suspensionLoad{l}", "N", $"Average suspension load {l}");
    }

    static void Add(OgsSession session, (string, string, string, double)[] table, OgsChannel[] into)
    {
        for (int i = 0; i < table.Length; i++)
            into[i] = session.AddChannel(table[i].Item1, table[i].Item2, table[i].Item3);
    }

    public bool AcceptsDatagram(int length) => length >= 3;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        var c = new Cursor(d, 1);
        ushort version = c.U16();
        switch (d[0])
        {
            case 0 when version == 1:
                Keep(d, _lastRace, ref _lastRaceLength);
                RaceInfo(ref c, session);
                break;
            case 1 when version == 1:
                if (Participant(ref c, session)) Keep(d, _lastPlayer, ref _lastPlayerLength);
                break;
            case 2 when version == 2: Telemetry(d, session, now); break;
        }
    }

    static void Keep(ReadOnlySpan<byte> d, byte[] into, ref int length)
    {
        if (d.Length > into.Length) return;
        d.CopyTo(into);
        length = d.Length;
    }

    /// <summary>Re-applies the kept race-info and player packets (after a
    /// redeclare: the channel set is new).</summary>
    void Replay(OgsSession session)
    {
        if (_lastRaceLength > 0) { var c = new Cursor(_lastRace.AsSpan(0, _lastRaceLength), 3); RaceInfo(ref c, session); }
        if (_lastPlayerLength > 0) { var c = new Cursor(_lastPlayer.AsSpan(0, _lastPlayerLength), 3); Participant(ref c, session); }
    }

    void RaceInfo(ref Cursor c, OgsSession session)
    {
        for (int i = 0; i < 6; i++)
        {
            var text = c.Str();
            if (c.Failed) return;
            session.SetSessionText(_raceText[i], text);
            if (i == 0) session.SetLocation(text); // the track names the location
        }
        for (int i = 0; i < 4; i++) session.SetChannel(_race[i], c.F());
        session.SetChannel(_race[4], c.U8());
        session.SetChannel(_race[5], c.U8());
        session.SetChannel(_race[6], c.U8());
    }

    bool Participant(ref Cursor c, OgsSession session)
    {
        int id = c.I32();
        bool isPlayer = c.U8() != 0;
        var vehicle = c.Str();
        var driver = c.Str();
        if (c.Failed || !isPlayer) return false;
        _playerId = id;
        session.SetTypeLabel(vehicle);
        session.SetSessionText(_driver, driver);
        return true;
    }

    void Telemetry(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        var c = new Cursor(d, 3);
        int id = c.I32();
        if (_playerId >= 0 ? id != _playerId : _followId >= 0 && id != _followId) return;
        _followId = id;

        // shape check (cold path on change): wheels, gears, suspension loads
        int wheels = c.U8();
        var probe = c;
        probe.Skip(wheels * WheelBytes + ChassisBytes + DrivetrainFloats.Length * 4 + DrivetrainFlags.Length);
        int gears = probe.U8();
        probe.Skip(gears * 8);
        int loads = probe.U8();
        if (probe.Failed) return;
        if (wheels != _wheels || gears != _gears || loads != _loads)
        {
            _wheels = wheels; _gears = gears; _loads = loads;
            session.BeginRedeclare();
            Declare(session);
            session.CommitRedeclare(now);
            Replay(session);
        }

        for (int w = 0; w < wheels; w++)
        {
            session.SetChannel(_wheelMaterial[w], c.I32());
            int at = c.At;
            for (int f = 0; f < WheelFloats.Length; f++)
                session.SetChannel(_wheel[w * WheelFloats.Length + f], c.F() * WheelFloats[f].Item4);
            if (c.Failed) return;
            // wheel floats by index: 0 angVel, 14 slipRatio, 24 springStrain
            session.SetWheel(_wheelPoints[w], (float)(F(d, at) * Rpm), F(d, at + 14 * 4), F(d, at + 24 * 4),
                Ogs.Telemetry.Schema.ContactMedium.Asphalt);
        }

        var pos = c.V3();
        var q = new Quaternion(c.F(), c.F(), c.F(), c.F());
        var angWorld = c.V3();
        var angLocal = c.V3();
        var velWorld = c.V3();
        var velLocal = c.V3();
        var accWorld = c.V3();
        var accLocal = c.V3();
        float overall = c.F(), forward = c.F(), sideslip = c.F();
        if (c.Failed) return;
        SetV3(session, 0, pos);
        SetV3(session, 3, angWorld);
        SetV3(session, 6, angLocal);
        SetV3(session, 9, velWorld);
        SetV3(session, 12, velLocal);
        SetV3(session, 15, accWorld);
        SetV3(session, 18, accLocal);
        session.SetChannel(_chassis[21], overall);
        session.SetChannel(_chassis[22], forward);
        session.SetChannel(_chassis[23], sideslip);

        var orientation = Quaternion.Normalize(_axes.Rotation(q));
        session.SetPose(_axes.Vector(pos), orientation);
        var accel = _axes.Vector(accLocal);
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, orientation),
            _axes.Angular(angLocal));
        session.SetChannel(_vehicleId, id);

        Floats(ref c, session, DrivetrainFloats, _drive);
        for (int i = 0; i < DrivetrainFlags.Length; i++) session.SetChannel(_driveFlag[i], c.U8() != 0);
        c.U8(); // gear count (checked above)
        for (int g = 0; g < gears; g++)
        {
            session.SetChannel(_gear[g * 2], c.F());
            session.SetChannel(_gear[g * 2 + 1], c.F());
        }
        c.U8(); // load count (checked above)
        for (int l = 0; l < loads; l++) session.SetChannel(_load[l], c.F());
        session.SetChannel(_loadBias, c.F());
        Floats(ref c, session, InputFloats, _input);
        session.SetChannel(_gearInput, c.I32());
        Floats(ref c, session, SetupFloats, _setup);
        session.SetChannel(_absLevel, c.U8());
        session.SetChannel(_tcsLevel, c.U8());
        Floats(ref c, session, GeneralFloats, _general);
        session.SetChannel(_abs, c.U8() != 0);
        Floats(ref c, session, ConstantFloats, _constant);
        session.SetChannel(_wheelCount, c.U8());
        session.SetChannel(_fwdGears, c.U8());
        session.SetChannel(_revGears, c.U8());
        session.SetChannel(_hybrid, c.U8() != 0);

        session.Tick(now);
    }

    void SetV3(OgsSession session, int at, Vector3 v)
    {
        session.SetChannel(_chassis[at], v.X);
        session.SetChannel(_chassis[at + 1], v.Y);
        session.SetChannel(_chassis[at + 2], v.Z);
    }

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));

    static void Floats(ref Cursor c, OgsSession session, (string, string, string, double)[] table, OgsChannel[] channels)
    {
        for (int i = 0; i < table.Length; i++)
        {
            float v = c.F();
            if (c.Failed) return;
            session.SetChannel(channels[i], v * table[i].Item4);
        }
    }

    /// <summary>Bounds-checked little-endian reader; reads past the end
    /// return 0 and set <see cref="Failed"/>.</summary>
    ref struct Cursor
    {
        readonly ReadOnlySpan<byte> _d;
        int _at;
        public bool Failed;

        public Cursor(ReadOnlySpan<byte> d, int at) { _d = d; _at = at; Failed = false; }

        public int At => _at;

        bool Take(int n)
        {
            if (Failed || _at + n > _d.Length) { Failed = true; return false; }
            return true;
        }

        public void Skip(int n) { if (Take(n)) _at += n; }
        public byte U8() { if (!Take(1)) return 0; return _d[_at++]; }
        public ushort U16() { if (!Take(2)) return 0; var v = MemoryMarshal.Read<ushort>(_d.Slice(_at, 2)); _at += 2; return v; }
        public int I32() { if (!Take(4)) return 0; var v = MemoryMarshal.Read<int>(_d.Slice(_at, 4)); _at += 4; return v; }
        public float F() { if (!Take(4)) return 0; var v = MemoryMarshal.Read<float>(_d.Slice(_at, 4)); _at += 4; return v; }
        public Vector3 V3() => new(F(), F(), F());

        public ReadOnlySpan<byte> Str()
        {
            int len = U8();
            if (!Take(len)) return default;
            var s = _d.Slice(_at, len);
            _at += len;
            return s;
        }
    }
}
}
