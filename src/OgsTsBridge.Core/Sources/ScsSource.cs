#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// SCS truck games (Euro Truck Simulator 2, American Truck Simulator) via
/// the community ets2-telemetry-server plugin's shared memory
/// (Local\Ets2TelemetryServer). The SCS SDK only exposes telemetry to
/// in-process plugins, so the plugin DLL must be installed into the game's
/// bin\win_x64\plugins folder; the page exists while the game runs.
///
/// Every field of the page through plugin revision 4 is mapped (layout
/// sequential, 1012 bytes): time (change token) @0, paused @4, revision
/// @8, game version @12/16, trailer attached @21, speed @24, local linear
/// acceleration @28, world placement @40 / rotation @52 (fractions of a
/// turn), drivetrain, fuel, user and game inputs, weights, job, lights,
/// warnings, pressures and temperatures, wear, odometer, cruise control,
/// truck make/model, warning thresholds, cabin/head/hook positions,
/// shifter, time scale, rest stop, trailer placement, navigation. Units
/// per the SCS SDK, converted to C-3 (psi → Pa, km → m).
///
/// Vehicle space per the SCS SDK: right-handed X-right / Y-up /
/// Z-backward → OGS (x, y, −z), world likewise. Heading is
/// counter-clockwise from above (0.25 = left) → yaw = −heading·2π; pitch +
/// up; roll taken as counter-clockwise looking forward → roll = −roll·2π
/// (unverified against the live game — flip options exist). The
/// acceleration is gravity-free; the specific force is derived
/// (`gravIncluded` to override). The page has no angular rates, so
/// BodyDynamics carries zero rates. Paused frames emit nothing.
/// </summary>
public sealed class ScsSource : ILegacySource
{
    internal const int PageSize = 1012;
    const double Turn = 2.0 * Math.PI;
    const double Psi = 6894.757;

    readonly PageChannels _fields = new(BuildFields());
    OgsChannel _speed, _fuelLevel, _trailerYaw, _trailerPitch, _trailerRoll;
    OgsChannel _trailerId, _trailerName, _citySource, _cityDest, _companySource, _companyDest,
               _make, _makeId, _model, _shifterType;
    double _sy = 1, _sp = 1, _sr = 1;
    bool _gravIncluded;
    uint _lastTime;
    bool _haveTime;

    public string Id => "scs";
    public string Title => "SCS truck games (ETS2 / ATS)";
    public string Description =>
        "Euro Truck Simulator 2 / American Truck Simulator through the " +
        "ets2-telemetry-server plugin's shared memory " +
        "(Local\\Ets2TelemetryServer) — install its plugin DLL into the " +
        "game's bin\\win_x64\\plugins folder.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "eurotrucks2", "amtrucks" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Euro Truck Simulator 2", "eurotrucks2"),
        new LegacyGame("American Truck Simulator", "amtrucks"),
    };

    public SourceTransport Transport => SourceTransport.SharedMemory;
    public string SharedMemoryName => "Local\\Ets2TelemetryServer";
    public int SharedMemorySize => PageSize;
    public int PollIntervalMs => 2;

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",      "negate yaw (heading) sign (bool, default false)"),
        ("flipPitch",    "negate pitch sign (bool, default false)"),
        ("flipRoll",     "negate roll sign (bool, default false)"),
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
        session.DeclareObject("truck", Ogs.Telemetry.Schema.ObjectType.Vehicle, "SCS truck");
        session.SendChangedChannelsOnly();
        _speed        = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        _fuelLevel    = session.AddChannel("fuel.level", "", "Remaining fuel fraction 0..1");
        _trailerYaw   = session.AddChannel("trailer.yaw", "rad", "Trailer heading, OGS yaw");
        _trailerPitch = session.AddChannel("trailer.pitch", "rad", "Trailer pitch");
        _trailerRoll  = session.AddChannel("trailer.roll", "rad", "Trailer roll, OGS sense");
        _fields.Declare(session);
        _trailerId     = session.AddSessionText("trailer.id", "Trailer id");
        _trailerName   = session.AddSessionText("trailer.name", "Trailer / cargo name");
        _citySource    = session.AddSessionText("job.citySource", "Job source city");
        _cityDest      = session.AddSessionText("job.cityDestination", "Job destination city");
        _companySource = session.AddSessionText("job.companySource", "Job source company");
        _companyDest   = session.AddSessionText("job.companyDestination", "Job destination company");
        _make          = session.AddSessionText("vehicle.make", "Truck make");
        _makeId        = session.AddSessionText("vehicle.makeId", "Truck make id");
        _model         = session.AddSessionText("vehicle.name", "Truck model");
        _shifterType   = session.AddSessionText("transmission.shifterType", "Shifter type");
    }

    public bool AcceptsDatagram(int length) => length >= PageSize;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static uint U(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<uint>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        uint revision = U(d, 8);
        if (revision == 0) return;                 // plugin not initialised yet
        uint time = U(d, 0);
        if (_haveTime && time == _lastTime) return; // page unchanged
        _lastTime = time;
        _haveTime = true;
        if (U(d, 4) != 0) return;                  // paused

        var q = OgsMath.EulerToQuaternion(
            _sy * -F(d, 52) * Turn, _sp * F(d, 56) * Turn, _sr * -F(d, 60) * Turn);
        // SCS space (X right, Y up, Z back) -> OGS (x right, y up, z fwd)
        session.SetPose(new Vector3(F(d, 40), F(d, 44), -F(d, 48)), q);

        var accel = new Vector3(F(d, 28), F(d, 32), -F(d, 36));
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, q),
                                default);

        session.SetChannel(_speed, Math.Abs(F(d, 24)));
        float capacity = F(d, 92);
        if (capacity > 0) session.SetChannel(_fuelLevel, Math.Clamp(F(d, 88) / capacity, 0f, 1f));
        _fields.Write(d.Slice(0, revision >= 4 ? PageSize : revision >= 3 ? 880 : 564), session);
        session.SetSessionText(_trailerId, d.Slice(172, 64));
        session.SetSessionText(_trailerName, d.Slice(236, 64));
        session.SetSessionText(_citySource, d.Slice(308, 64));
        session.SetSessionText(_cityDest, d.Slice(372, 64));
        session.SetSessionText(_companySource, d.Slice(436, 64));
        session.SetSessionText(_companyDest, d.Slice(500, 64));
        if (revision >= 3)
        {
            session.SetSessionText(_make, d.Slice(688, 64));
            session.SetSessionText(_makeId, d.Slice(752, 64));
            session.SetSessionText(_model, d.Slice(816, 64));
            session.SetTypeLabel(d.Slice(816, 64));
        }
        if (revision >= 4)
        {
            session.SetSessionText(_shifterType, d.Slice(948, 16));
            session.SetChannel(_trailerYaw, -F(d, 984) * Turn);
            session.SetChannel(_trailerPitch, F(d, 988) * Turn);
            session.SetChannel(_trailerRoll, -F(d, 992) * Turn);
        }

        session.Tick(now);
    }

    static PageField[] BuildFields()
    {
        const FieldKind f32 = FieldKind.F32, i32 = FieldKind.I32, u32 = FieldKind.U32,
                        u8 = FieldKind.U8, b8 = FieldKind.Bool8;
        return new PageField[]
        {
            // ---- revision 1 ----
            new("scs.gameVersionMajor", "", "Game telemetry version, major", 12, u32),
            new("scs.gameVersionMinor", "", "Game telemetry version, minor", 16, u32),
            new("trailer.attached", "", "Trailer attached", 21, b8),
            new("velocity.signed", "m/s", "Speed along the truck axis (negative reversing)", 24, f32),
            new("transmission.gear", "", "Engine gear (negative reverse, 0 neutral)", 64, i32),
            new("transmission.gearCount", "", "Forward gears", 68, i32),
            new("transmission.gearRanges", "", "Gear ranges", 72, i32),
            new("transmission.gearRangeActive", "", "Active gear range", 76, i32),
            new("engine.rpm", "rpm", "Engine rpm", 80, f32),
            new("engine.rpmMax", "rpm", "Engine redline", 84, f32),
            new("fuel.quantity", "l", "Fuel", 88, f32),
            new("fuel.capacity", "l", "Fuel capacity", 92, f32),
            new("fuel.rateRaw", "", "Fuel consumption rate (game units)", 96, f32),
            new("fuel.averageConsumption", "l/km", "Average consumption", 100, f32),
            new("ctrl.steering.input", "", "Player steering input -1..1 (SDK sign)", 104, f32),
            new("ctrl.throttle", "", "Player throttle 0..1", 108, f32),
            new("ctrl.brake", "", "Player brake 0..1", 112, f32),
            new("ctrl.clutch", "", "Player clutch 0..1", 116, f32),
            new("scs.steer", "", "Game steering -1..1 (after assists, SDK sign)", 120, f32),
            new("engine.throttle", "", "Throttle 0..1 (after assists)", 124, f32),
            new("brakes.pedal", "", "Brake 0..1 (after assists)", 128, f32),
            new("input.clutch", "", "Clutch 0..1 (after assists)", 132, f32),
            new("vehicle.mass", "kg", "Truck weight", 136, f32),
            new("trailer.weight", "kg", "Trailer weight", 140, f32),
            new("session.timeOfDay", "min", "Absolute game time (minutes since start of game)", 160, i32),
            new("transmission.gearsReverse", "", "Reverse gears", 164, i32),
            new("trailer.mass", "kg", "Trailer mass", 168, f32),
            new("job.income", "", "Job income (game currency)", 300, i32),
            new("job.deadline", "min", "Job deadline (game minutes)", 304, i32),

            // ---- revision 3 ----
            new("brakes.retarder", "", "Retarder level", 564, i32),
            new("transmission.shifterSlot", "", "Shifter slot", 568, i32),
            new("transmission.shifterToggle", "", "Shifter toggle bitmask", 572, i32),
            new("cruise.active", "", "Cruise control on", 580, b8),
            new("lights.wipers", "", "Wipers active", 581, b8),
            new("brakes.parking", "", "Parking brake on", 582, b8),
            new("brakes.engine", "", "Engine (motor) brake on", 583, b8),
            new("elec.enabled", "", "Electrics on", 584, b8),
            new("engine.running", "", "Engine running", 585, b8),
            new("lights.blinkerLeftActive", "", "Left blinker switched on", 586, b8),
            new("lights.blinkerRightActive", "", "Right blinker switched on", 587, b8),
            new("lights.blinkerLeftOn", "", "Left blinker lamp lit", 588, b8),
            new("lights.blinkerRightOn", "", "Right blinker lamp lit", 589, b8),
            new("lights.parking", "", "Parking lights on", 590, b8),
            new("lights.headlights", "", "Low beam on", 591, b8),
            new("lights.highBeam", "", "High beam on", 592, b8),
            new("lights.auxFront", "", "Front aux lights: 0 off, 1 dimmed, 2 full", 596, u32),
            new("lights.auxRoof", "", "Roof aux lights: 0 off, 1 dimmed, 2 full", 600, u32),
            new("lights.beacon", "", "Beacon on", 604, b8),
            new("lights.brake", "", "Brake lights lit", 605, b8),
            new("lights.reverse", "", "Reverse lights lit", 606, b8),
            new("warning.batteryVoltage", "", "Battery voltage warning", 607, b8),
            new("warning.airPressure", "", "Air pressure warning", 608, b8),
            new("warning.airPressureEmergency", "", "Air pressure emergency", 609, b8),
            new("warning.adblue", "", "AdBlue warning", 610, b8),
            new("warning.oilPressure", "", "Oil pressure warning", 611, b8),
            new("warning.waterTemp", "", "Water temperature warning", 612, b8),
            new("brakes.airPressure", "Pa", "Brake air pressure", 616, f32, Psi),
            new("brakes.temp", "degC", "Brake temperature", 620, f32),
            new("warning.fuel", "", "Fuel warning", 624, i32),
            new("fuel.adblue", "l", "AdBlue", 628, f32),
            new("fuel.adblueConsumption", "", "AdBlue consumption (game units)", 632, f32),
            new("engine.oilPressure", "Pa", "Oil pressure", 636, f32, Psi),
            new("engine.oilTemp", "degC", "Oil temperature", 640, f32),
            new("engine.temperature", "degC", "Water temperature", 644, f32),
            new("engine.voltage", "V", "Battery voltage", 648, f32),
            new("lights.dashboard", "", "Dashboard backlight 0..1", 652, f32),
            new("damage.engine", "", "Engine wear 0..1", 656, f32),
            new("damage.transmission", "", "Transmission wear 0..1", 660, f32),
            new("damage.cabin", "", "Cabin wear 0..1", 664, f32),
            new("damage.chassis", "", "Chassis wear 0..1", 668, f32),
            new("damage.wheels", "", "Wheels wear 0..1", 672, f32),
            new("damage.trailer", "", "Trailer wear 0..1", 676, f32),
            new("vehicle.odometer", "m", "Odometer", 680, f32, 1000),
            new("cruise.speed", "m/s", "Cruise control speed", 684, f32),

            // ---- revision 4 ----
            new("warning.fuelFactor", "", "Fuel warning threshold fraction", 880, f32),
            new("fuel.adblueCapacity", "l", "AdBlue capacity", 884, f32),
            new("warning.airPressureValue", "Pa", "Air pressure warning threshold", 888, f32, Psi),
            new("warning.airPressureEmergencyValue", "Pa", "Air pressure emergency threshold", 892, f32, Psi),
            new("warning.oilPressureValue", "Pa", "Oil pressure warning threshold", 896, f32, Psi),
            new("warning.waterTempValue", "degC", "Water temperature warning threshold", 900, f32),
            new("warning.batteryVoltageValue", "V", "Battery voltage warning threshold", 904, f32),
            new("brakes.retarderSteps", "", "Retarder step count", 908, u32),
            new("scs.cabinPosition.x", "m", "Cabin position, vehicle space x", 912, f32),
            new("scs.cabinPosition.y", "m", "Cabin position, vehicle space y", 916, f32),
            new("scs.cabinPosition.z", "m", "Cabin position, vehicle space z (back)", 920, f32),
            new("scs.headPosition.x", "m", "Default head position, cabin space x", 924, f32),
            new("scs.headPosition.y", "m", "Default head position, cabin space y", 928, f32),
            new("scs.headPosition.z", "m", "Default head position, cabin space z (back)", 932, f32),
            new("scs.hookPosition.x", "m", "Trailer hook position, vehicle space x", 936, f32),
            new("scs.hookPosition.y", "m", "Trailer hook position, vehicle space y", 940, f32),
            new("scs.hookPosition.z", "m", "Trailer hook position, vehicle space z (back)", 944, f32),
            new("session.timeScale", "", "Game time scale", 964, f32),
            new("driver.nextRestStop", "min", "Time to the next rest stop", 968, i32),
            new("trailer.position.x", "m", "Trailer world position, OGS x", 972, f32),
            new("trailer.position.y", "m", "Trailer world position, OGS y", 976, f32),
            new("trailer.position.z", "m", "Trailer world position, OGS z", 980, f32, -1),
            new("transmission.gearDisplayed", "", "Gear shown on the dashboard", 996, i32),
            new("nav.distance", "m", "Route distance remaining", 1000, f32),
            new("nav.time", "s", "Route time remaining", 1004, f32),
            new("nav.speedLimit", "m/s", "Current speed limit", 1008, f32),
        };
    }
}
}
