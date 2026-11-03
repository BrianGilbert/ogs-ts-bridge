#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// RaceRoom Racing Experience shared memory ($R3E, the r3e.h layout,
/// version 2.16, Pack=1). RaceRoom publishes the page whenever it runs;
/// there is no in-game switch.
///
/// Every player-car field of the page is mapped (the per-driver array
/// after NumCars @1940 — other cars' standings — is not; the session
/// carries one object). Hand-mapped: pose from Player.Position @48 and
/// Player.Orientation @168, BodyDynamics from Player.LocalAcceleration
/// @144 and LocalAngularVelocity @264, four wheels (TireRps @1564,
/// SuspensionDeflection @384, TireOnMtrl @1884, and — when the tyre data
/// is available — the extended block: front road-wheel angle
/// SteerInputRaw × SteerLockDegrees, TireLoad @1900, SuspensionVelocity
/// @416, TirePressure @1644 kPa, mean tread TireTemp @1676, TireWear
/// @1612), track/layout/vehicle/driver names as session text, aid
/// activity. Everything else rides the field table below with the page's
/// own units converted to C-3 (kPa → Pa, rad/s → rpm, kN → N, bar → Pa,
/// percent → 0..1); the page's −1 "N/A" sentinels are left unset.
///
/// Axes per r3e.h ("+X=left, +Y=up, +Z=back", left-handed) → OGS body and
/// world as (−x, y, −z); rates (−ωx, ωy, −ωz); Euler pitch −X, yaw Y,
/// roll −Z (the rotation signs are unverified against the live game —
/// flip options exist). LocalAcceleration is taken as gravity-free and the
/// specific force derived (`gravIncluded` to override); LocalGforce is
/// published as channels too.
///
/// Transport: shared memory, polled at 2 ms (RaceRoom's physics runs at
/// 400 Hz); Translate gates on GameSimulationTicks and emits nothing in
/// menus, replays or pause.
/// </summary>
public sealed class R3eSource : ILegacySource
{
    internal const int PageSize = 1944;
    const double RadPerSecToRpm = 60.0 / (2.0 * Math.PI);
    const double Deg = Math.PI / 180.0;
    static readonly string[] W = { "fl", "fr", "rl", "rr" };

    readonly PageChannels _fields = new(BuildFields());
    OgsChannel _steer, _fuelLevel, _absActive, _tcsActive, _espActive, _gForceX, _gForceY, _gForceZ;
    OgsChannel _track, _layout, _vehicle, _driver;
    double _sy = 1, _sp = 1, _sr = 1;
    double _wheelbase = 2.6, _track_ = 1.6, _radius = 0.33;
    bool _gravIncluded;
    int _lastTicks = int.MinValue;

    public string Id => "r3e";
    public string Title => "RaceRoom ($R3E)";
    public string Description =>
        "RaceRoom Racing Experience shared memory ($R3E) — published " +
        "whenever the game runs; no in-game setup.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "RRRE64", "RRRE" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("RaceRoom Racing Experience", "RRRE64"),
        new LegacyGame("RaceRoom Racing Experience (32-bit)", "RRRE"),
    };

    public SourceTransport Transport => SourceTransport.SharedMemory;
    public string SharedMemoryName => "$R3E";
    public int SharedMemorySize => PageSize;
    public int PollIntervalMs => 2;

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",      "negate yaw Euler + yaw-rate sign (bool, default false)"),
        ("flipPitch",    "negate pitch Euler + pitch-rate sign (bool, default false)"),
        ("flipRoll",     "negate roll Euler + roll-rate sign (bool, default false)"),
        ("gravIncluded", "LocalAcceleration already includes gravity reaction (bool, default false)"),
        ("wheelbase",    "wheel separation fore/aft in metres (default 2.6)"),
        ("track",        "wheel separation left/right in metres (default 1.6)"),
        ("radius",       "tyre rolling radius in metres (default 0.33)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "gravIncluded", "wheelbase", "track", "radius");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _gravIncluded = o.GetBool("gravIncluded", false);
        _wheelbase = o.GetDouble("wheelbase", 2.6);
        _track_ = o.GetDouble("track", 1.6);
        _radius = o.GetDouble("radius", 0.33);
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "RaceRoom car");
        session.SendChangedChannelsOnly();
        float hz = (float)(_wheelbase * 0.5), hx = (float)(_track_ * 0.5);
        session.AddWheel("wheel.fl", new Vector3(-hx, 0, hz), (float)_radius, true, true);
        session.AddWheel("wheel.fr", new Vector3(+hx, 0, hz), (float)_radius, true, true);
        session.AddWheel("wheel.rl", new Vector3(-hx, 0, -hz), (float)_radius, true, false);
        session.AddWheel("wheel.rr", new Vector3(+hx, 0, -hz), (float)_radius, true, false);

        _steer     = session.AddChannel("ctrl.steering.angle", "rad", "Handwheel angle, + = right");
        _fuelLevel = session.AddChannel("fuel.level", "", "Remaining fuel fraction 0..1");
        _absActive = session.AddBooleanChannel("aids.abs", "ABS currently active");
        _tcsActive = session.AddBooleanChannel("aids.tcs", "Traction control currently active");
        _espActive = session.AddBooleanChannel("aids.esp", "Stability control currently active");
        _gForceX   = session.AddChannel("r3e.gForce.x", "", "Driver g-force, OGS body x (right)");
        _gForceY   = session.AddChannel("r3e.gForce.y", "", "Driver g-force, OGS body y (up)");
        _gForceZ   = session.AddChannel("r3e.gForce.z", "", "Driver g-force, OGS body z (forward)");
        _fields.Declare(session);
        _track   = session.AddSessionText("session.track", "Track name");
        _layout  = session.AddSessionText("session.layout", "Track layout name");
        _vehicle = session.AddSessionText("vehicle.name", "Vehicle name");
        _driver  = session.AddSessionText("driver.name", "Player name");
    }

    public bool AcceptsDatagram(int length) => length >= PageSize;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static double D(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<double>(d.Slice(off, 8));
    static int I(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<int>(d.Slice(off, 4));
    static Vector3 Map(ReadOnlySpan<byte> d, int off) =>
        new(-(float)D(d, off), (float)D(d, off + 8), -(float)D(d, off + 16));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (I(d, 0) != 2) return;                                // not (yet) a v2 page
        if (I(d, 16) != 0 || I(d, 20) != 0 || I(d, 24) != 0) return; // paused / menus / replay
        int ticks = I(d, 36);
        if (ticks == _lastTicks) return;                         // page unchanged
        _lastTicks = ticks;

        // r3e.h: +X left, +Y up, +Z back -> OGS (x right, y up, z fwd)
        var q = OgsMath.EulerToQuaternion(
            _sy * D(d, 176), _sp * -D(d, 168), _sr * -D(d, 184));
        session.SetPose(Map(d, 48), q);

        var accel = Map(d, 144);
        var rates = new Vector3((float)(_sp * -D(d, 264)), (float)(_sy * D(d, 272)), (float)(_sr * -D(d, 280)));
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, q), rates);
        var g = Map(d, 288);
        session.SetChannel(_gForceX, g.X);
        session.SetChannel(_gForceY, g.Y);
        session.SetChannel(_gForceZ, g.Z);

        float steerRaw = F(d, 1456);
        session.SetChannel(_steer, steerRaw * I(d, 1464) * 0.5 * Deg);
        float roadWheel = (float)(steerRaw * I(d, 1460) * Deg);
        bool tyreData = F(d, 1644) >= 0;                         // -1 = N/A
        for (int w = 0; w < 4; w++)
        {
            session.SetWheel(w, (float)(Math.Abs(F(d, 1564 + w * 4)) * RadPerSecToRpm), float.NaN,
                (float)D(d, 384 + w * 8), Surface(I(d, 1884 + w * 4)));
            if (!tyreData) continue;
            int t = 1676 + w * 24;
            session.SetWheelDynamics(w, w < 2 ? roadWheel : 0f,
                Math.Max(0f, F(d, 1900 + w * 4)), (float)D(d, 416 + w * 8));
            session.SetWheelTyre(w,
                F(d, 1644 + w * 4) * 1000f, (F(d, t) + F(d, t + 4) + F(d, t + 8)) / 3f,
                Math.Max(0f, F(d, 1612 + w * 4)));
        }

        float capacity = F(d, 1404);
        if (capacity > 0) session.SetChannel(_fuelLevel, Math.Clamp(F(d, 1400) / capacity, 0f, 1f));
        session.SetChannel(_absActive, I(d, 1468) == 5);         // 5 = currently active
        session.SetChannel(_tcsActive, I(d, 1472) == 5);
        session.SetChannel(_espActive, I(d, 1476) == 5);

        _fields.Write(d, session);

        session.SetSessionText(_track, d.Slice(576, 64));
        session.SetSessionText(_layout, d.Slice(640, 64));
        session.SetSessionText(_vehicle, d.Slice(1156, 64));
        session.SetSessionText(_driver, d.Slice(1268, 64));
        session.SetLocation(d.Slice(576, 64));
        session.SetTypeLabel(d.Slice(1156, 64));

        session.Tick(now);
    }

    /// <summary>r3e MtrlType: 1 tarmac, 2 grass, 3 dirt, 4 gravel, 5 rumble.</summary>
    static Ogs.Telemetry.Schema.ContactMedium Surface(int type) => type switch
    {
        1 => Ogs.Telemetry.Schema.ContactMedium.Asphalt,
        2 => Ogs.Telemetry.Schema.ContactMedium.Grass,
        3 => Ogs.Telemetry.Schema.ContactMedium.Dirt,
        4 => Ogs.Telemetry.Schema.ContactMedium.Gravel,
        5 => Ogs.Telemetry.Schema.ContactMedium.RumbleStrip,
        _ => Ogs.Telemetry.Schema.ContactMedium.Other,
    };

    static PageField[] BuildFields()
    {
        const FieldKind f32 = FieldKind.F32, f64 = FieldKind.F64, i32 = FieldKind.I32, b32 = FieldKind.Bool32;
        const double na = -1;
        var t = new List<PageField>
        {
            // ---- player high-detail block (doubles) ----
            new("r3e.simTime", "s", "Virtual physics time", 40, f64),
            new("velocity.world.x", "m/s", "World velocity, OGS x", 72, f64, -1),
            new("velocity.world.y", "m/s", "World velocity, OGS y", 80, f64),
            new("velocity.world.z", "m/s", "World velocity, OGS z", 88, f64, -1),
            new("velocity.body.x", "m/s", "Local velocity, OGS body x (right)", 96, f64, -1),
            new("velocity.body.y", "m/s", "Local velocity, OGS body y (up)", 104, f64),
            new("velocity.body.z", "m/s", "Local velocity, OGS body z (forward)", 112, f64, -1),
            new("r3e.accel.world.x", "m/s2", "World acceleration, OGS x", 120, f64, -1),
            new("r3e.accel.world.y", "m/s2", "World acceleration, OGS y", 128, f64),
            new("r3e.accel.world.z", "m/s2", "World acceleration, OGS z", 136, f64, -1),
            new("r3e.rotation.x", "rad", "Body rotation (r3e Rotation X)", 192, f64),
            new("r3e.rotation.y", "rad", "Body rotation (r3e Rotation Y)", 200, f64),
            new("r3e.rotation.z", "rad", "Body rotation (r3e Rotation Z)", 208, f64),
            new("air.alphaX", "rad/s2", "Angular acceleration, OGS body x (pitch)", 216, f64, -1),
            new("air.alphaY", "rad/s2", "Angular acceleration, OGS body y (yaw)", 224, f64),
            new("air.alphaZ", "rad/s2", "Angular acceleration, OGS body z (roll)", 232, f64, -1),
            new("r3e.angularVelocity.world.x", "rad/s", "World angular velocity, OGS x", 240, f64, -1),
            new("r3e.angularVelocity.world.y", "rad/s", "World angular velocity, OGS y", 248, f64),
            new("r3e.angularVelocity.world.z", "rad/s", "World angular velocity, OGS z", 256, f64, -1),
            new("r3e.steeringForce", "", "Steering force through the steering bars (game units)", 312, f64),
            new("r3e.steeringForcePct", "", "Steering force fraction", 320, f64),
            new("engine.torque", "Nm", "Current engine torque", 328, f64),
            new("aero.downforce", "N", "Current downforce", 336, f64),
            new("engine.voltage", "V", "Voltage", 344, f64),
            new("ers.level", "", "ERS level (game units)", 352, f64),
            new("ers.powerMguH", "", "MGU-H power (game units)", 360, f64),
            new("ers.powerMguK", "", "MGU-K power (game units)", 368, f64),
            new("ers.torqueMguK", "", "MGU-K torque (game units)", 376, f64),
            new("susp.frontWingHeight", "m", "Front wing height", 512, f64),
            new("susp.frontRollAngle", "rad", "Front roll angle", 520, f64),
            new("susp.rearRollAngle", "rad", "Rear roll angle", 528, f64),
            new("susp.thirdSpring.front.deflection", "m", "Front third spring deflection", 536, f64),
            new("susp.thirdSpring.front.velocity", "m/s", "Front third spring velocity", 544, f64),
            new("susp.thirdSpring.rear.deflection", "m", "Rear third spring deflection", 552, f64),
            new("susp.thirdSpring.rear.velocity", "m/s", "Rear third spring velocity", 560, f64),

            // ---- event & session ----
            new("session.trackId", "", "Track id", 704, i32),
            new("session.layoutId", "", "Layout id", 708, i32),
            new("session.layoutLength", "m", "Layout length", 712, f32),
            new("session.sector2Start", "", "Sector 2 start, lap fraction", 720, f32),
            new("session.sector3Start", "", "Sector 3 start, lap fraction", 724, f32),
            new("session.race1Laps", "", "Race 1 length in laps", 728, i32, NotAvailable: na),
            new("session.race2Laps", "", "Race 2 length in laps", 732, i32, NotAvailable: na),
            new("session.race3Laps", "", "Race 3 length in laps", 736, i32, NotAvailable: na),
            new("session.race1Minutes", "min", "Race 1 length in minutes", 740, i32, NotAvailable: na),
            new("session.race2Minutes", "min", "Race 2 length in minutes", 744, i32, NotAvailable: na),
            new("session.race3Minutes", "min", "Race 3 length in minutes", 748, i32, NotAvailable: na),
            new("session.eventIndex", "", "Championship event index (0-based)", 752, i32, NotAvailable: na),
            new("session.type", "", "0 practice, 1 qualify, 2 race, 3 warmup", 756, i32, NotAvailable: na),
            new("session.iteration", "", "Iteration of this session type (1 = first)", 760, i32, NotAvailable: na),
            new("session.lengthFormat", "", "0 time, 1 laps, 2 time + extra lap", 764, i32, NotAvailable: na),
            new("pit.speedLimit", "m/s", "Pit lane speed limit", 768, f32),
            new("session.phase", "", "1 garage, 2 gridwalk, 3 formation, 4 countdown, 5 green, 6 checkered", 772, i32, NotAvailable: na),
            new("session.startLights", "", "0 off, 1-5 red lights, 6 green", 776, i32, NotAvailable: na),
            new("session.tyreWearRate", "", "Tyre wear multiplier (0 off)", 780, i32, NotAvailable: na),
            new("session.fuelUseRate", "", "Fuel use multiplier (0 off)", 784, i32, NotAvailable: na),
            new("session.lapsTotal", "", "Race length in laps", 788, i32, NotAvailable: na),
            new("session.timeTotal", "s", "Session duration", 792, f32, NotAvailable: na),
            new("session.timeRemain", "s", "Session time remaining", 796, f32, NotAvailable: na),
            new("session.maxIncidentPoints", "", "Server incident point limit", 800, i32, NotAvailable: na),

            // ---- pit ----
            new("pit.windowStatus", "", "0 disabled, 1 closed, 2 open, 3 stopped, 4 completed", 808, i32, NotAvailable: na),
            new("pit.windowStart", "", "Pit window opens (minute or lap)", 812, i32, NotAvailable: na),
            new("pit.windowEnd", "", "Pit window closes (minute or lap)", 816, i32, NotAvailable: na),
            new("pit.inPitLane", "", "In the pit lane", 820, b32, NotAvailable: na),
            new("pit.menuSelection", "", "Selected pit menu item (r3e PitMenuSelection)", 824, i32, NotAvailable: na),
            new("pit.menu.preset", "", "Preset: 1 selectable, -1 not", 828, i32),
            new("pit.menu.penalty", "", "Serve penalty: 1 marked, 0 unmarked, -1 N/A", 832, i32),
            new("pit.menu.driverChange", "", "Driver change: 1 marked, 0 unmarked, -1 N/A", 836, i32),
            new("pit.menu.fuel", "", "Refuel: 1 marked, 0 unmarked, -1 N/A", 840, i32),
            new("pit.menu.frontTyres", "", "Front tyres: 1 marked, 0 unmarked, -1 N/A", 844, i32),
            new("pit.menu.rearTyres", "", "Rear tyres: 1 marked, 0 unmarked, -1 N/A", 848, i32),
            new("pit.menu.frontWing", "", "Front wing: 1 marked, 0 unmarked, -1 N/A", 852, i32),
            new("pit.menu.rearWing", "", "Rear wing: 1 marked, 0 unmarked, -1 N/A", 856, i32),
            new("pit.menu.suspension", "", "Suspension: 1 marked, 0 unmarked, -1 N/A", 860, i32),
            new("pit.menu.buttonTop", "", "Top button: 1 selectable, -1 not", 864, i32),
            new("pit.menu.buttonBottom", "", "Bottom button: 1 selectable, -1 not", 868, i32),
            new("pit.state", "", "0 none, 1 requested, 2 heading to spot, 3 stopped, 4 exiting", 872, i32, NotAvailable: na),
            new("pit.stopDuration", "s", "Current pit stop actions duration", 876, f32, NotAvailable: na),
            new("pit.stopElapsed", "s", "Current pit stop elapsed time", 880, f32, NotAvailable: na),
            new("pit.action", "", "Bitmask: 1 preparing, 2 penalty, 4 driver change, 8 fuel, 16 front tyres, 32 rear tyres, 64 body, 128 front wing, 256 rear wing, 512 suspension", 884, i32, NotAvailable: na),
            new("pit.stopsDone", "", "Pit stops performed", 888, i32, NotAvailable: na),
            new("pit.minDurationTotal", "s", "Minimum pit stop duration", 892, f32, NotAvailable: na),
            new("pit.minDurationLeft", "s", "Minimum pit stop duration left", 896, f32, NotAvailable: na),

            // ---- flags ----
            new("flags.yellow", "", "Yellow flag", 900, b32, NotAvailable: na),
            new("flags.yellowCausedIt", "", "Player caused the yellow", 904, b32, NotAvailable: na),
            new("flags.yellowOvertake", "", "Player overtook under yellow", 908, b32, NotAvailable: na),
            new("flags.yellowPositionsGained", "", "Positions gained under yellow", 912, i32, NotAvailable: na),
            new("flags.sector1Yellow", "", "Sector 1 yellow", 916, b32, NotAvailable: na),
            new("flags.sector2Yellow", "", "Sector 2 yellow", 920, b32, NotAvailable: na),
            new("flags.sector3Yellow", "", "Sector 3 yellow", 924, b32, NotAvailable: na),
            new("flags.closestYellowDistance", "m", "Distance into track of the closest yellow", 928, f32, NotAvailable: na),
            new("flags.blue", "", "Blue flag", 932, b32, NotAvailable: na),
            new("flags.black", "", "Black flag", 936, b32, NotAvailable: na),
            new("flags.green", "", "Green flag", 940, b32, NotAvailable: na),
            new("flags.checkered", "", "Checkered flag", 944, b32, NotAvailable: na),
            new("flags.white", "", "White flag", 948, b32, NotAvailable: na),
            new("flags.blackAndWhite", "", "Black-and-white flag (r3e: 1 cut, 2 ignored blue, 3 wrong way)", 952, i32, NotAvailable: na),

            // ---- scoring & timing ----
            new("race.position", "", "Position (1 = first)", 956, i32, NotAvailable: na),
            new("race.positionClass", "", "Position in class", 960, i32, NotAvailable: na),
            new("race.finishStatus", "", "0 running, 1 finished, 2 DNF, 3 DNQ, 4 DNS, 5 DQ", 964, i32, NotAvailable: na),
            new("penalty.cutTrackWarnings", "", "Cut track warnings", 968, i32, NotAvailable: na),
            new("penalty.driveThrough", "", "Pending drive-through penalties", 972, i32, NotAvailable: na),
            new("penalty.stopAndGo", "", "Pending stop-and-go penalties", 976, i32, NotAvailable: na),
            new("penalty.pitStop", "", "Pending pit stop penalties", 980, i32, NotAvailable: na),
            new("penalty.timeDeduction", "", "Pending time deductions", 984, i32, NotAvailable: na),
            new("penalty.slowDown", "", "Pending slow-down penalties", 988, i32, NotAvailable: na),
            new("penalty.count", "", "Total pending penalties", 992, i32, NotAvailable: na),
            new("lap.completed", "", "Laps completed", 996, i32, NotAvailable: na),
            new("lap.valid", "", "Current lap valid", 1000, b32, NotAvailable: na),
            new("lap.sector", "", "Current track sector", 1004, i32, NotAvailable: na),
            new("lap.distance", "m", "Distance into the lap", 1008, f32, NotAvailable: na),
            new("lap.distPct", "", "Lap fraction 0..1", 1012, f32, NotAvailable: na),
            new("lap.bestTimeLeader", "s", "Session best lap (leader)", 1016, f32, NotAvailable: na),
            new("lap.bestTimeLeaderClass", "s", "Session best lap (class leader)", 1020, f32, NotAvailable: na),
            new("lap.sessionBestSector1", "s", "Session best lap, sector 1", 1024, f32, NotAvailable: na),
            new("lap.sessionBestSector2", "s", "Session best lap, sector 2", 1028, f32, NotAvailable: na),
            new("lap.sessionBestSector3", "s", "Session best lap, sector 3", 1032, f32, NotAvailable: na),
            new("lap.bestTime", "s", "Own best lap", 1036, f32, NotAvailable: na),
            new("lap.bestSector1", "s", "Own best lap, sector 1", 1040, f32, NotAvailable: na),
            new("lap.bestSector2", "s", "Own best lap, sector 2", 1044, f32, NotAvailable: na),
            new("lap.bestSector3", "s", "Own best lap, sector 3", 1048, f32, NotAvailable: na),
            new("lap.lastTime", "s", "Previous lap", 1052, f32, NotAvailable: na),
            new("lap.lastSector1", "s", "Previous lap, sector 1", 1056, f32, NotAvailable: na),
            new("lap.lastSector2", "s", "Previous lap, sector 2", 1060, f32, NotAvailable: na),
            new("lap.lastSector3", "s", "Previous lap, sector 3", 1064, f32, NotAvailable: na),
            new("lap.currentTime", "s", "Current lap", 1068, f32, NotAvailable: na),
            new("lap.currentSector1", "s", "Current lap, sector 1", 1072, f32, NotAvailable: na),
            new("lap.currentSector2", "s", "Current lap, sector 2", 1076, f32, NotAvailable: na),
            new("lap.currentSector3", "s", "Current lap, sector 3", 1080, f32, NotAvailable: na),
            new("lap.deltaLeader", "s", "Gap to the session leader", 1084, f32, NotAvailable: na),
            new("lap.deltaLeaderClass", "s", "Gap to the class leader", 1088, f32, NotAvailable: na),
            new("race.gapAhead", "s", "Gap to the car ahead", 1092, f32, NotAvailable: na),
            new("race.gapBehind", "s", "Gap to the car behind", 1096, f32, NotAvailable: na),
            new("lap.deltaToBestLap", "s", "Current lap vs own best", 1100, f32, NotAvailable: -1000),
            new("lap.bestIndividualSector1", "s", "Own best sector 1, any lap", 1104, f32, NotAvailable: na),
            new("lap.bestIndividualSector2", "s", "Own best sector 2, any lap", 1108, f32, NotAvailable: na),
            new("lap.bestIndividualSector3", "s", "Own best sector 3, any lap", 1112, f32, NotAvailable: na),
            new("lap.leaderBestSector1", "s", "Leader best sector 1", 1116, f32, NotAvailable: na),
            new("lap.leaderBestSector2", "s", "Leader best sector 2", 1120, f32, NotAvailable: na),
            new("lap.leaderBestSector3", "s", "Leader best sector 3", 1124, f32, NotAvailable: na),
            new("lap.classLeaderBestSector1", "s", "Class leader best sector 1", 1128, f32, NotAvailable: na),
            new("lap.classLeaderBestSector2", "s", "Class leader best sector 2", 1132, f32, NotAvailable: na),
            new("lap.classLeaderBestSector3", "s", "Class leader best sector 3", 1136, f32, NotAvailable: na),
            new("driver.incidentCount", "", "Incident points", 1140, i32, NotAvailable: na),
            new("lap.validState", "", "0 this and next valid, 1 this invalid, 2 this and next invalid", 1144, i32, NotAvailable: na),

            // ---- vehicle info ----
            new("vehicle.carNumber", "", "Car number", 1220, i32),
            new("vehicle.classId", "", "Class id", 1224, i32),
            new("vehicle.modelId", "", "Model id", 1228, i32),
            new("vehicle.teamId", "", "Team id", 1232, i32),
            new("vehicle.liveryId", "", "Livery id", 1236, i32),
            new("vehicle.manufacturerId", "", "Manufacturer id", 1240, i32),
            new("driver.userId", "", "User id", 1244, i32),
            new("vehicle.slotId", "", "Slot id", 1248, i32),
            new("vehicle.classPerformanceIndex", "", "Class performance index", 1252, i32),
            new("vehicle.engineType", "", "0 combustion, 1 electric, 2 hybrid", 1256, i32),
            new("vehicle.width", "m", "Car width", 1260, f32),
            new("vehicle.length", "m", "Car length", 1264, f32),

            // ---- vehicle state ----
            new("vehicle.controlType", "", "0 player, 1 AI, 2 remote, 3 replay", 1332, i32, NotAvailable: na),
            new("vehicle.speed", "m/s", "Speed over ground", 1336, f32),
            new("engine.rpm", "rpm", "Current engine rpm", 1340, f32, RadPerSecToRpm),
            new("engine.rpmMax", "rpm", "Engine redline", 1344, f32, RadPerSecToRpm),
            new("engine.rpmUpshift", "rpm", "Upshift rpm", 1348, f32, RadPerSecToRpm),
            new("transmission.gear", "", "-1 reverse, 0 neutral, 1..n forward", 1352, i32, NotAvailable: -2),
            new("transmission.gearCount", "", "Forward gears", 1356, i32, NotAvailable: na),
            new("r3e.cgWorld.x", "m", "CG world position, OGS x", 1360, f32, -1),
            new("r3e.cgWorld.y", "m", "CG world position, OGS y", 1364, f32),
            new("r3e.cgWorld.z", "m", "CG world position, OGS z", 1368, f32, -1),
            new("vehicle.mass", "kg", "Car + penalty weight + fuel", 1396, f32),
            new("fuel.quantity", "l", "Fuel left", 1400, f32),
            new("fuel.capacity", "l", "Fuel capacity", 1404, f32),
            new("fuel.perLap", "l", "Fuel per lap", 1408, f32),
            new("engine.temperature", "degC", "Coolant temperature", 1412, f32),
            new("engine.oilTemp", "degC", "Oil temperature", 1416, f32),
            new("fuel.pressure", "Pa", "Fuel pressure", 1420, f32, 1000),
            new("engine.oilPressure", "Pa", "Oil pressure", 1424, f32, 1000),
            new("engine.boostPressure", "Pa", "Turbo pressure", 1428, f32, 100000, NotAvailable: na),
            new("engine.throttle", "", "Throttle 0..1 (after aids)", 1432, f32, NotAvailable: na),
            new("ctrl.throttle", "", "Throttle pedal 0..1", 1436, f32, NotAvailable: na),
            new("brakes.pedal", "", "Brake 0..1 (after aids)", 1440, f32, NotAvailable: na),
            new("ctrl.brake", "", "Brake pedal 0..1", 1444, f32, NotAvailable: na),
            new("input.clutch", "", "Clutch 0..1 (after aids)", 1448, f32, NotAvailable: na),
            new("ctrl.clutch", "", "Clutch pedal 0..1", 1452, f32, NotAvailable: na),
            new("ctrl.steering.input", "", "Steering input -1..1", 1456, f32),
            new("ctrl.steering.lock", "rad", "Road-wheel steer lock, centre to full", 1460, i32, Deg),
            new("ctrl.steering.range", "rad", "Handwheel range, lock to lock", 1464, i32, Deg),
            new("aids.absSetting", "", "-1 N/A, 0 off, 1 on, 5 active", 1468, i32),
            new("aids.tcSetting", "", "-1 N/A, 0 off, 1 on, 5 active", 1472, i32),
            new("aids.espSetting", "", "-1 N/A, 0 off, 1-3 level, 5 active", 1476, i32),
            new("aids.countersteer", "", "-1 N/A, 0 off, 1 on, 5 active", 1480, i32),
            new("aids.cornering", "", "-1 N/A, 0 off, 1 on, 5 active", 1484, i32),
            new("drs.equipped", "", "DRS equipped and allowed", 1488, b32, NotAvailable: na),
            new("drs.available", "", "DRS activation available", 1492, b32, NotAvailable: na),
            new("drs.activationsLeft", "", "DRS activations left this lap", 1496, i32, NotAvailable: na),
            new("drs.active", "", "DRS engaged", 1500, b32, NotAvailable: na),
            new("pit.limiter", "", "Pit limiter active", 1504, b32, NotAvailable: na),
            new("p2p.available", "", "Push-to-pass available", 1508, b32, NotAvailable: na),
            new("p2p.active", "", "Push-to-pass engaged", 1512, b32, NotAvailable: na),
            new("p2p.count", "", "Push-to-pass activations left", 1516, i32, NotAvailable: na),
            new("p2p.engagedTimeLeft", "s", "Push-to-pass engaged time left", 1520, f32, NotAvailable: na),
            new("p2p.waitTimeLeft", "s", "Push-to-pass wait time left", 1524, f32, NotAvailable: na),
            new("brakes.bias", "", "Rear brake bias fraction", 1528, f32, NotAvailable: na),
            new("drs.activationsTotal", "", "DRS activations in total", 1532, i32, NotAvailable: na),
            new("p2p.activationsTotal", "", "Push-to-pass activations in total", 1536, i32, NotAvailable: na),
            new("ers.batterySoc", "", "Battery state of charge 0..1", 1540, f32, 0.01, NotAvailable: na),
            new("brakes.waterLeft", "l", "Brake water tank", 1544, f32, NotAvailable: na),
            new("aids.absLevel", "", "ABS setting", 1548, i32, NotAvailable: na),
            new("lights.headlightsState", "", "0 off, 1 on, 2 strobing", 1552, i32, NotAvailable: na),
            new("tyre.type", "", "0 option, 1 prime", 1560, i32, NotAvailable: na),
            new("tyre.typeFront", "", "Front: 0 option, 1 prime", 1772, i32, NotAvailable: na),
            new("tyre.typeRear", "", "Rear: 0 option, 1 prime", 1776, i32, NotAvailable: na),
            new("tyre.subtypeFront", "", "Front: 0 primary, 1 alternate, 2 soft, 3 medium, 4 hard", 1780, i32, NotAvailable: na),
            new("tyre.subtypeRear", "", "Rear: 0 primary, 1 alternate, 2 soft, 3 medium, 4 hard", 1784, i32, NotAvailable: na),
            new("aids.tcLevel", "", "Traction control setting", 1868, i32, NotAvailable: na),
            new("engine.map", "", "Engine map setting", 1872, i32, NotAvailable: na),
            new("engine.brakeSetting", "", "Engine brake setting", 1876, i32, NotAvailable: na),
            new("aids.tcCut", "", "Traction control cut 0..1", 1880, f32, 0.01, NotAvailable: na),
            new("damage.engine", "", "Engine damage 0..1 (1 = intact)", 1916, f32, NotAvailable: na),
            new("damage.transmission", "", "Transmission damage 0..1 (1 = intact)", 1920, f32, NotAvailable: na),
            new("damage.aero", "", "Aerodynamics damage 0..1 (1 = intact)", 1924, f32, NotAvailable: na),
            new("damage.suspension", "", "Suspension damage 0..1 (1 = intact)", 1928, f32, NotAvailable: na),
            new("race.carCount", "", "Cars in session (incl. player)", 1940, i32),
        };

        for (int w = 0; w < 4; w++)
        {
            string p = W[w];
            int tt = 1676 + w * 24, bt = 1788 + w * 16;
            t.Add(new($"tyre.{p}.tempLeft", "degC", "Tread temperature, left", tt, f32, NotAvailable: na));
            t.Add(new($"tyre.{p}.tempCenter", "degC", "Tread temperature, centre", tt + 4, f32, NotAvailable: na));
            t.Add(new($"tyre.{p}.tempRight", "degC", "Tread temperature, right", tt + 8, f32, NotAvailable: na));
            t.Add(new($"tyre.{p}.tempOptimal", "degC", "Optimal tyre temperature", tt + 12, f32, NotAvailable: na));
            t.Add(new($"tyre.{p}.tempCold", "degC", "Cold tyre temperature", tt + 16, f32, NotAvailable: na));
            t.Add(new($"tyre.{p}.tempHot", "degC", "Hot tyre temperature", tt + 20, f32, NotAvailable: na));
            t.Add(new($"tyre.{p}.speed", "m/s", "Wheel surface speed", 1580 + w * 4, f32));
            t.Add(new($"tyre.{p}.grip", "", "Grip 0..1", 1596 + w * 4, f32, NotAvailable: na));
            t.Add(new($"tyre.{p}.flatspot", "", "Flat-spotted", 1628 + w * 4, b32, NotAvailable: na));
            t.Add(new($"tyre.{p}.dirt", "", "Dirt on tyre 0..1", 1660 + w * 4, f32, NotAvailable: na));
            t.Add(new($"brakes.{p}.temp", "degC", "Brake temperature", bt, f32, NotAvailable: na));
            t.Add(new($"brakes.{p}.tempOptimal", "degC", "Optimal brake temperature", bt + 4, f32, NotAvailable: na));
            t.Add(new($"brakes.{p}.tempCold", "degC", "Cold brake temperature", bt + 8, f32, NotAvailable: na));
            t.Add(new($"brakes.{p}.tempHot", "degC", "Hot brake temperature", bt + 12, f32, NotAvailable: na));
            t.Add(new($"brakes.{p}.force", "N", "Brake force", 1852 + w * 4, f32, 1000, NotAvailable: na));
            t.Add(new($"susp.{p}.camber", "rad", "Camber", 448 + w * 8, f64));
            t.Add(new($"susp.{p}.rideHeight", "m", "Ride height", 480 + w * 8, f64));
        }
        return t.ToArray();
    }
}
}
