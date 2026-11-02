#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Schema = Ogs.Telemetry.Schema;

namespace OgsTsBridge.Sources
{
/// <summary>
/// X-Plane 11/12 over UDP RREF subscriptions — no setup in the sim. The
/// Bridge asks X-Plane (default 127.0.0.1:49000) for every dataref of the
/// curated set in OGS-Telemetry-Standard's reference-xplane12.md, and
/// X-Plane streams them back to the Bridge's input port.
///
/// Request: "RREF\0", int32 frequency (Hz, 0 = stop), int32 index, the
/// dataref path NUL-padded to 400 bytes. Reply: "RREF" + one byte, then
/// (int32 index, float32 value) pairs. Requests are re-sent every few
/// seconds, so a restarted sim resubscribes; on stop the Bridge cancels
/// them.
///
/// Frames: X-Plane's local OpenGL frame is right-handed (+x east, +y up,
/// +z south) and aircraft coordinates are +x right, +y up, +z aft; OGS-TS
/// is (x, y, −z) of both. Attitude (psi/theta/phi) and body rates
/// (Q/R/P) are aerospace-standard and map direct; the g-loads are
/// load factors (gravity included) — specific force = (side, normal,
/// axial) × g. Sign checks against the live sim are options (flip*).
///
/// The aircraft's gear and engines are read as datarefs too: once they
/// arrive (and whenever they change — an aircraft swap), the session is
/// redeclared with one wheel per gear, one propeller/jet/generic drive
/// point per engine (up to <see cref="MaxEngines"/>), and per-gear and
/// per-engine channels.
/// </summary>
public sealed class XPlaneSource : ILegacySource, IUdpRequests
{
    public const int MaxEngines = 4;
    public const int MaxGear = 6;
    const float Gravity = 9.80665f;
    const double Deg = Math.PI / 180.0;
    const double Kt = 0.514444;
    const double Ft = 0.3048;
    const double Fpm = 0.00508;
    const double Psi = 6894.757;
    const double InHg = 3386.389;
    const double RadSToRpm = 60.0 / (2 * Math.PI);

    // ---- subscriptions: every dataref read, with its rate ----

    readonly List<(string Path, int Hz)> _subs = new();
    int Sub(string path, int hz) { _subs.Add((path, hz)); return _subs.Count - 1; }

    // pose and dynamics
    readonly int _x, _y, _z, _psi, _theta, _phi, _pRate, _qRate, _rRate, _gSide, _gNormal, _gAxial;
    // scalar channels: (index, channel name, unit, description, scale, kind)
    enum Kind { Number, Boolean }
    readonly List<(int Index, string Name, string Unit, string Description, double Scale, Kind Kind)> _scalars = new();
    // per-engine and per-gear stream datarefs: [element][field]
    readonly int[,] _eng, _gear;
    static readonly (string Path, string Name, string Unit, string Description, double Scale, Kind Kind)[] EngineFields =
    {
        ("sim/cockpit2/engine/indicators/N1_percent", "n1", "", "N1 (0..1)", 0.01, Kind.Number),
        ("sim/cockpit2/engine/indicators/N2_percent", "n2", "", "N2 (0..1)", 0.01, Kind.Number),
        ("sim/cockpit2/engine/indicators/engine_speed_rpm", "rpm", "rpm", "Engine speed", 1, Kind.Number),
        ("sim/cockpit2/engine/indicators/prop_speed_rpm", "propRpm", "rpm", "Propeller speed", 1, Kind.Number),
        ("sim/flightmodel2/engines/ITT_deg_cel", "itt", "degC", "Inter-turbine temperature", 1, Kind.Number),
        ("sim/flightmodel2/engines/EGT_deg_cel", "egt", "degC", "Exhaust gas temperature", 1, Kind.Number),
        ("sim/cockpit2/engine/indicators/oil_pressure_psi", "oilPressure", "Pa", "Oil pressure", Psi, Kind.Number),
        ("sim/cockpit2/engine/indicators/fuel_flow_kg_sec", "fuelFlow", "kg/s", "Fuel flow", 1, Kind.Number),
        ("sim/cockpit2/engine/indicators/MPR_in_hg", "manifoldPressure", "Pa", "Manifold pressure", InHg, Kind.Number),
        ("sim/cockpit2/engine/indicators/torque_n_mtr", "torque", "Nm", "Engine torque", 1, Kind.Number),
        ("sim/cockpit2/engine/actuators/throttle_ratio", "throttle", "", "Throttle handle, 0..1", 1, Kind.Number),
        ("sim/cockpit2/engine/actuators/mixture_ratio", "mixture", "", "Mixture handle, 0..1", 1, Kind.Number),
        ("sim/cockpit2/engine/actuators/prop_ratio", "propLever", "", "Propeller handle, 0..1", 1, Kind.Number),
        ("sim/flightmodel2/engines/thrust_reverser_deploy_ratio", "reverser", "", "Thrust reverser deployment, 0..1", 1, Kind.Number),
        ("sim/flightmodel/engine/ENGN_running", "running", "", "Engine running", 1, Kind.Boolean),
    };
    // drive-point state per engine
    const int EPropRadS = 0, EPropPitch = 1, EThrust = 2;
    static readonly string[] EngineState =
    {
        "sim/flightmodel2/engines/prop_rotation_speed_rad_sec",
        "sim/flightmodel2/engines/prop_pitch_deg",
        "sim/flightmodel/engine/POINT_thrust",
    };
    readonly int[,] _engState;
    static readonly (string Path, string Name, string Unit, string Description, double Scale, Kind Kind)[] GearFields =
    {
        ("sim/flightmodel2/gear/deploy_ratio", "position", "", "Gear deployment, 0 up .. 1 down", 1, Kind.Number),
        ("sim/flightmodel2/gear/tire_steer_actual_deg", "steerAngle", "rad", "Steer angle, + right", Deg, Kind.Number),
        ("sim/flightmodel2/gear/tire_vertical_force_n_mtr", "loadForce", "N", "Vertical load", 1, Kind.Number),
        ("sim/flightmodel2/gear/total_deflection_rate", "damperVelocity", "m/s", "Deflection rate, + compressing", 1, Kind.Number),
        ("sim/flightmodel2/gear/tire_part_brake", "brake", "", "Braking on this gear, 0..1", 1, Kind.Number),
    };
    const int GRate = 0, GSkid = 1, GDeflection = 2, GOnGround = 3;
    static readonly string[] GearState =
    {
        "sim/flightmodel2/gear/tire_rotation_rate_rad_sec",
        "sim/flightmodel2/gear/tire_skid_ratio",
        "sim/flightmodel2/gear/tire_vertical_deflection_mtr",
        "sim/flightmodel2/gear/on_ground",
    };
    readonly int[,] _gearState;
    readonly int _surface;
    // aircraft configuration (read slowly; a change redeclares the session)
    readonly int _numEngines;
    readonly int[] _enType, _blades, _engX, _engY, _engZ;
    readonly int[] _gearType, _tireX, _tireY, _tireZ, _tireRadius, _gearSteers;
    readonly int _fuelTotal, _fuelCapacityLb;

    float[] _v = Array.Empty<float>();
    bool[] _seen = Array.Empty<bool>();
    int _configSeen; // config datarefs received at least once
    readonly List<int> _configIndices = new();

    // ---- session state ----

    readonly List<OgsChannel> _scalarChannels = new();
    OgsChannel[,] _engChannels = new OgsChannel[0, 0];
    OgsChannel[,] _gearChannels = new OgsChannel[0, 0];
    OgsChannel _fuelLevel, _brakesPedal, _engineRpm, _engineThrottle, _engineTorque, _engineRunning;
    readonly List<(int Engine, int Point, char Kind)> _engPoints = new();
    readonly List<(int Gear, int Point)> _wheels = new();
    int _engineCount;
    ulong _layout;
    bool[] _isConfig = Array.Empty<bool>();
    readonly int _leftBrake, _rightBrake;

    double _sy = 1, _sp = 1, _sr = 1;
    float _axial = 1, _side = 1;
    IPEndPoint? _target = new(IPAddress.Loopback, 49000);
    byte[][] _requests = Array.Empty<byte[]>();
    byte[][] _stops = Array.Empty<byte[]>();

    public XPlaneSource()
    {
        _x = Sub("sim/flightmodel/position/local_x", 60);
        _y = Sub("sim/flightmodel/position/local_y", 60);
        _z = Sub("sim/flightmodel/position/local_z", 60);
        _psi = Sub("sim/flightmodel/position/psi", 60);
        _theta = Sub("sim/flightmodel/position/theta", 60);
        _phi = Sub("sim/flightmodel/position/phi", 60);
        _pRate = Sub("sim/flightmodel/position/Prad", 60);
        _qRate = Sub("sim/flightmodel/position/Qrad", 60);
        _rRate = Sub("sim/flightmodel/position/Rrad", 60);
        _gSide = Sub("sim/flightmodel/forces/g_side", 60);
        _gNormal = Sub("sim/flightmodel/forces/g_nrml", 60);
        _gAxial = Sub("sim/flightmodel/forces/g_axil", 60);

        void S(string path, int hz, string name, string unit, string description, double scale = 1, Kind kind = Kind.Number) =>
            _scalars.Add((Sub(path, hz), name, unit, description, scale, kind));

        // ratified (CHANNELS.md) and session
        S("sim/time/paused", 10, "session.paused", "", "Simulation paused (C-11)", 1, Kind.Boolean);
        S("sim/flightmodel/position/groundspeed", 60, "vehicle.speed", "m/s", "Ground speed");
        // position / geo
        S("sim/flightmodel/position/latitude", 30, "position.lat", "rad", "Latitude", Deg);
        S("sim/flightmodel/position/longitude", 30, "position.lon", "rad", "Longitude", Deg);
        S("sim/flightmodel/position/elevation", 30, "position.altMsl", "m", "Altitude above mean sea level");
        S("sim/flightmodel/position/y_agl", 60, "env.heightFromGround", "m", "Height above ground");
        // air data
        S("sim/flightmodel/position/indicated_airspeed", 60, "air.speedIndicated", "m/s", "Indicated airspeed", Kt);
        S("sim/flightmodel/position/true_airspeed", 60, "air.speedTrue", "m/s", "True airspeed");
        S("sim/flightmodel/misc/machno", 30, "air.mach", "", "Mach number");
        S("sim/flightmodel/position/vh_ind", 60, "air.verticalSpeed", "m/s", "Vertical speed");
        S("sim/flightmodel/position/alpha", 60, "air.alpha", "rad", "Angle of attack", Deg);
        S("sim/flightmodel/position/beta", 60, "air.beta", "rad", "Sideslip", Deg);
        S("sim/flightmodel/position/Q_dot", 60, "air.alphaX", "rad/s2", "Pitch angular acceleration", Deg);
        S("sim/flightmodel/position/R_dot", 60, "air.alphaY", "rad/s2", "Yaw angular acceleration", Deg);
        S("sim/flightmodel/position/P_dot", 60, "air.alphaZ", "rad/s2", "Roll angular acceleration", Deg);
        S("sim/flightmodel/misc/slip", 60, "air.turnBall", "", "Turn-and-slip ball, -1..1, + right of centre");
        S("sim/flightmodel/forces/g_nrml", 60, "air.loadFactor", "", "Normal load factor, 1 in level flight");
        S("sim/flightmodel/position/vpath", 30, "air.flightPathAngle", "rad", "Flight path angle", Deg);
        S("sim/flightmodel/failures/onground_any", 30, "air.onGround", "", "Any gear on the ground", 1, Kind.Boolean);
        S("sim/flightmodel/position/mag_psi", 30, "nav.headingMagnetic", "rad", "Magnetic heading", Deg);
        S("sim/flightmodel/position/hpath", 30, "nav.track", "rad", "Ground track", Deg);
        S("sim/flightmodel/position/magnetic_variation", 1, "nav.magneticVariation", "rad", "Magnetic variation", Deg);
        // wind / weather
        S("sim/weather/aircraft/wind_now_speed_msc", 10, "wind.trueSpeed", "m/s", "Wind speed");
        S("sim/weather/aircraft/wind_now_direction_degt", 10, "wind.trueDirection", "rad", "Wind direction, from (C-7)", Deg);
        S("sim/weather/aircraft/visibility_reported_sm", 1, "env.visibility", "m", "Reported visibility", 1609.344);
        S("sim/weather/aircraft/precipitation_on_aircraft_ratio", 5, "env.precipOnAircraft", "", "Rain on the windshield, 0..1");
        S("sim/weather/aircraft/thermal_rate_ms", 5, "env.thermalRate", "m/s", "Thermal climb rate");
        S("sim/weather/aircraft/qnh_pas", 1, "env.qnh", "Pa", "QNH");
        // controls
        S("sim/cockpit2/controls/yoke_pitch_ratio", 60, "ctrl.elevator", "", "Pilot pitch input, -1..1, + nose up (C-5)");
        S("sim/cockpit2/controls/yoke_roll_ratio", 60, "ctrl.aileron", "", "Pilot roll input, -1..1, + right (C-5)");
        S("sim/cockpit2/controls/yoke_heading_ratio", 60, "ctrl.rudder", "", "Pilot yaw input, -1..1, + right (C-5)");
        S("sim/cockpit2/controls/total_pitch_ratio", 60, "ctrl.total.elevator", "", "Total pitch input incl. autopilot and augmentation");
        S("sim/cockpit2/controls/total_roll_ratio", 60, "ctrl.total.aileron", "", "Total roll input incl. autopilot and augmentation");
        S("sim/cockpit2/controls/total_heading_ratio", 60, "ctrl.total.rudder", "", "Total yaw input incl. autopilot and augmentation");
        S("sim/cockpit2/controls/elevator_trim", 10, "ctrl.trim.elevator", "", "Elevator trim, -1..1");
        S("sim/cockpit2/controls/aileron_trim", 10, "ctrl.trim.aileron", "", "Aileron trim, -1..1");
        S("sim/cockpit2/controls/rudder_trim", 10, "ctrl.trim.rudder", "", "Rudder trim, -1..1");
        S("sim/cockpit2/controls/flap_handle_request_ratio", 10, "ctrl.flaps", "", "Flap handle, 0..1");
        S("sim/cockpit2/controls/flap_system_deploy_ratio", 30, "flaps.deployed", "", "Flap deployment, 0..1");
        S("sim/cockpit2/controls/speedbrake_ratio", 10, "ctrl.spoilers", "", "Speedbrake handle, 0..1, -0.5 = armed (as sent)");
        S("sim/flightmodel2/controls/speedbrake_ratio", 30, "spoilers.deployed", "", "Speedbrake deployment, 0..1");
        S("sim/cockpit2/controls/left_brake_ratio", 30, "brakes.left", "", "Left brake pedal, 0..1");
        S("sim/cockpit2/controls/right_brake_ratio", 30, "brakes.right", "", "Right brake pedal, 0..1");
        S("sim/cockpit2/controls/gear_handle_down", 5, "gear.handleDown", "", "Gear handle down", 1, Kind.Boolean);
        S("sim/flightmodel/forces/fside_gear", 60, "gear.force.x", "N", "Ground reaction, body x");
        S("sim/flightmodel/forces/fnrml_gear", 60, "gear.force.y", "N", "Ground reaction, body y");
        S("sim/flightmodel/forces/faxil_gear", 60, "gear.force.z", "N", "Ground reaction, body z", -1);
        // fuel / mass
        _fuelTotal = Sub("sim/flightmodel/weight/m_fuel_total", 2);
        _scalars.Add((_fuelTotal, "fuel.quantity", "kg", "Fuel on board", 1, Kind.Number));
        for (int t = 0; t < 9; t++)
            S($"sim/flightmodel/weight/m_fuel[{t}]", 2, $"fuel.tank[{t + 1}]", "kg", $"Fuel tank {t + 1}");
        S("sim/flightmodel/weight/m_total", 2, "mass.total", "kg", "Total mass");
        S("sim/flightmodel2/misc/cg_offset_x", 1, "mass.cg.x", "m", "Lateral centre of gravity offset");
        S("sim/flightmodel2/misc/cg_offset_z", 1, "mass.cg.z", "m", "Longitudinal centre of gravity offset, + forward", -1);
        // warnings
        S("sim/cockpit2/annunciators/master_warning", 10, "warn.masterWarning", "", "Master warning", 1, Kind.Boolean);
        S("sim/cockpit2/annunciators/master_caution", 10, "warn.masterCaution", "", "Master caution", 1, Kind.Boolean);
        S("sim/cockpit2/annunciators/stall_warning", 30, "warn.stall", "", "Stall warning", 1, Kind.Boolean);
        S("sim/cockpit2/annunciators/gear_unsafe", 10, "warn.gearUnsafe", "", "Gear unsafe", 1, Kind.Boolean);
        S("sim/cockpit2/annunciators/low_voltage", 5, "warn.lowVoltage", "", "Bus voltage out of range", 1, Kind.Boolean);
        S("sim/cockpit2/annunciators/hydraulic_pressure", 5, "warn.hydraulicPressure", "", "Hydraulic pressure", 1, Kind.Boolean);
        S("sim/cockpit2/annunciators/fuel_quantity", 5, "warn.fuelQuantity", "", "Fuel quantity", 1, Kind.Boolean);
        S("sim/flightmodel/failures/over_g", 10, "warn.overG", "", "Over the g-load limit", 1, Kind.Boolean);
        S("sim/flightmodel/failures/over_vne", 10, "warn.overVne", "", "Over Vne", 1, Kind.Boolean);
        S("sim/flightmodel/failures/over_vfe", 10, "warn.overVfe", "", "Over Vfe", 1, Kind.Boolean);
        // electrics / cabin
        S("sim/cockpit2/electrical/battery_on[0]", 2, "elec.battery[1]", "", "Battery 1 on", 1, Kind.Boolean);
        S("sim/cockpit2/electrical/bus_volts[0]", 2, "elec.busVoltage[1]", "V", "Bus 1 voltage");
        S("sim/cockpit2/switches/avionics_power_on", 2, "elec.avionics", "", "Avionics power on", 1, Kind.Boolean);
        S("sim/cockpit2/electrical/APU_running", 2, "elec.apuRunning", "", "APU running", 1, Kind.Boolean);
        S("sim/cockpit2/pressurization/indicators/cabin_altitude_ft", 2, "cabin.altitude", "m", "Cabin altitude", Ft);
        S("sim/cockpit2/pressurization/indicators/cabin_vvi_fpm", 2, "cabin.verticalSpeed", "m/s", "Cabin vertical speed", Fpm);
        S("sim/flightmodel2/misc/canopy_open_ratio", 5, "cabin.canopy", "", "Canopy open, 0..1");
        // lights
        S("sim/cockpit2/switches/landing_lights_on", 2, "light.landing", "", "Landing light", 1, Kind.Boolean);
        S("sim/cockpit2/switches/navigation_lights_on", 2, "light.nav", "", "Navigation lights", 1, Kind.Boolean);
        S("sim/cockpit2/switches/strobe_lights_on", 2, "light.strobe", "", "Strobe lights", 1, Kind.Boolean);
        S("sim/cockpit2/switches/beacon_on", 2, "light.beacon", "", "Beacon", 1, Kind.Boolean);
        S("sim/cockpit2/switches/taxi_light_on", 2, "light.taxi", "", "Taxi light", 1, Kind.Boolean);
        // autopilot / radios
        S("sim/cockpit2/autopilot/autopilot_on", 2, "ap.on", "", "Autopilot engaged", 1, Kind.Boolean);
        S("sim/cockpit2/autopilot/flight_director_mode", 2, "ap.flightDirector", "", "Flight director: 0 off, 1 on, 2 on with servos");
        S("sim/cockpit2/autopilot/altitude_dial_ft", 2, "ap.altitude", "m", "Selected altitude", Ft);
        S("sim/cockpit2/autopilot/heading_dial_deg_mag_pilot", 2, "ap.heading", "rad", "Selected heading, magnetic", Deg);
        S("sim/cockpit2/autopilot/airspeed_dial_kts_mach", 2, "ap.airspeed", "", "Selected airspeed, knots or Mach (as sent)");
        S("sim/cockpit2/autopilot/vvi_dial_fpm", 2, "ap.verticalSpeed", "m/s", "Selected vertical speed", Fpm);
        S("sim/cockpit2/radios/actuators/com1_frequency_hz_833", 2, "nav.com1.active", "Hz", "COM1 active frequency", 1000);
        S("sim/cockpit2/radios/actuators/com1_standby_frequency_hz_833", 2, "nav.com1.standby", "Hz", "COM1 standby frequency", 1000);
        S("sim/cockpit2/radios/actuators/nav1_frequency_hz", 2, "nav.nav1.active", "Hz", "NAV1 active frequency", 10_000);
        S("sim/cockpit2/radios/indicators/nav1_hdef_dots_pilot", 30, "nav.nav1.hdef", "", "NAV1 lateral deflection, dots (as sent)");
        S("sim/cockpit2/radios/indicators/nav1_vdef_dots_pilot", 30, "nav.nav1.vdef", "", "NAV1 vertical deflection, dots (as sent)");
        S("sim/cockpit2/radios/actuators/transponder_code", 2, "nav.xpdr.code", "", "Transponder code, four octal digits (as sent)");
        // session
        S("sim/time/is_in_replay", 2, "session.replay", "", "Replay mode", 1, Kind.Boolean);
        S("sim/time/sim_speed_actual", 2, "session.simRate", "", "Achieved time multiplier");
        S("sim/time/zulu_time_sec", 1, "session.timeZulu", "s", "Zulu time since midnight");
        S("sim/time/local_time_sec", 1, "session.timeLocal", "s", "Local time since midnight");
        S("sim/flightmodel2/misc/has_crashed", 5, "session.crashed", "", "Aircraft crashed", 1, Kind.Boolean);

        _eng = new int[MaxEngines, EngineFields.Length];
        _engState = new int[MaxEngines, EngineState.Length];
        for (int e = 0; e < MaxEngines; e++)
        {
            for (int f = 0; f < EngineFields.Length; f++)
                _eng[e, f] = Sub($"{EngineFields[f].Path}[{e}]", 30);
            for (int f = 0; f < EngineState.Length; f++)
                _engState[e, f] = Sub($"{EngineState[f]}[{e}]", 60);
        }
        _gear = new int[MaxGear, GearFields.Length];
        _gearState = new int[MaxGear, GearState.Length];
        for (int g = 0; g < MaxGear; g++)
        {
            for (int f = 0; f < GearFields.Length; f++)
                _gear[g, f] = Sub($"{GearFields[f].Path}[{g}]", 60);
            for (int f = 0; f < GearState.Length; f++)
                _gearState[g, f] = Sub($"{GearState[f]}[{g}]", 60);
        }
        _surface = Sub("sim/flightmodel/ground/surface_texture_type", 10);

        int C(string path) { int i = Sub(path, 1); _configIndices.Add(i); return i; }
        _numEngines = C("sim/aircraft/engine/acf_num_engines");
        _fuelCapacityLb = C("sim/aircraft/weight/acf_m_fuel_tot");
        _enType = new int[MaxEngines]; _blades = new int[MaxEngines];
        _engX = new int[MaxEngines]; _engY = new int[MaxEngines]; _engZ = new int[MaxEngines];
        for (int e = 0; e < MaxEngines; e++)
        {
            _enType[e] = C($"sim/aircraft/prop/acf_en_type[{e}]");
            _blades[e] = C($"sim/aircraft/prop/acf_num_blades[{e}]");
            _engX[e] = C($"sim/flightmodel2/engines/location_x_mtr[{e}]");
            _engY[e] = C($"sim/flightmodel2/engines/location_y_mtr[{e}]");
            _engZ[e] = C($"sim/flightmodel2/engines/location_z_mtr[{e}]");
        }
        _gearType = new int[MaxGear]; _tireX = new int[MaxGear]; _tireY = new int[MaxGear];
        _tireZ = new int[MaxGear]; _tireRadius = new int[MaxGear]; _gearSteers = new int[MaxGear];
        for (int g = 0; g < MaxGear; g++)
        {
            _gearType[g] = C($"sim/aircraft/parts/acf_gear_type[{g}]");
            _tireX[g] = C($"sim/flightmodel/parts/tire_x_no_deflection[{g}]");
            _tireY[g] = C($"sim/flightmodel/parts/tire_y_no_deflection[{g}]");
            _tireZ[g] = C($"sim/flightmodel/parts/tire_z_no_deflection[{g}]");
            _tireRadius[g] = C($"sim/flightmodel2/gear/tire_radius_mtrs[{g}]");
            _gearSteers[g] = C($"sim/aircraft/overflow/acf_gear_steers[{g}]");
        }

        _leftBrake = IndexOf("sim/cockpit2/controls/left_brake_ratio");
        _rightBrake = IndexOf("sim/cockpit2/controls/right_brake_ratio");
        _v = new float[_subs.Count];
        _seen = new bool[_subs.Count];
        _isConfig = new bool[_subs.Count];
        foreach (int i in _configIndices) _isConfig[i] = true;
        BuildRequests();
    }

    /// <summary>Number of datarefs the source subscribes to.</summary>
    public int SubscriptionCount => _subs.Count;

    /// <summary>The dataref path subscribed under <paramref name="index"/>.</summary>
    public string Dataref(int index) => _subs[index].Path;

    /// <summary>The subscription index of <paramref name="path"/>, or -1.</summary>
    public int IndexOf(string path) => _subs.FindIndex(s => s.Path == path);

    public string Id => "xplane";
    public string Title => "X-Plane 11/12";
    public string Description =>
        "X-Plane over UDP RREF dataref subscriptions: nothing to set up in the " +
        "sim. The Bridge asks X-Plane (127.0.0.1:49000 by default; --set " +
        "host=<ip>[:port] for another machine) for the curated dataref set and " +
        "receives it on port 49100.";
    public int DefaultPort => 49100;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "X-Plane" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("X-Plane 11/12", "X-Plane"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("host",      "X-Plane's address, ip[:port] (default 127.0.0.1:49000)"),
        ("flipYaw",   "negate heading + yaw-rate sign (bool, default false)"),
        ("flipPitch", "negate pitch + pitch-rate sign (bool, default false)"),
        ("flipRoll",  "negate roll + roll-rate sign (bool, default false)"),
        ("flipAxial", "negate the axial g-load (bool, default false)"),
        ("flipSide",  "negate the side g-load (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("host", "flipYaw", "flipPitch", "flipRoll", "flipAxial", "flipSide");
        var host = o.GetString("host", "");
        if (host.Length > 0)
        {
            var parts = host.Split(':');
            _target = new IPEndPoint(IPAddress.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 49000);
        }
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _axial = o.GetBool("flipAxial", false) ? -1 : 1;
        _side = o.GetBool("flipSide", false) ? -1 : 1;
    }

    // ---- requests ----

    public IPEndPoint? RequestTarget => _target;
    public IReadOnlyList<byte[]> RequestPayloads => _requests;
    public IReadOnlyList<byte[]> StopPayloads => _stops;
    public double RequestIntervalSeconds => 5.0;

    void BuildRequests()
    {
        _requests = new byte[_subs.Count][];
        _stops = new byte[_subs.Count][];
        for (int i = 0; i < _subs.Count; i++)
        {
            _requests[i] = Rref(_subs[i].Hz, i, _subs[i].Path);
            _stops[i] = Rref(0, i, _subs[i].Path);
        }
    }

    /// <summary>One RREF request: "RREF\0", frequency, index, path padded to 400 bytes.</summary>
    public static byte[] Rref(int hz, int index, string path)
    {
        var d = new byte[5 + 4 + 4 + 400];
        Encoding.ASCII.GetBytes("RREF").CopyTo(d, 0);
        BitConverter.TryWriteBytes(d.AsSpan(5), hz);
        BitConverter.TryWriteBytes(d.AsSpan(9), index);
        var p = Encoding.ASCII.GetBytes(path);
        Array.Copy(p, 0, d, 13, Math.Min(p.Length, 399));
        return d;
    }

    // ---- declaration ----

    public void Declare(OgsSession session)
    {
        session.DeclareObject("aircraft", Schema.ObjectType.Aircraft, "X-Plane aircraft");
        session.DeclareGravity(Gravity);
        session.AddNamedPoint("cg", Vector3.Zero);

        // drive points from the aircraft layout read so far (none until it arrives)
        _engPoints.Clear();
        _wheels.Clear();
        _engineCount = _configSeen == _configIndices.Count
            ? Math.Clamp((int)_v[_numEngines], 0, MaxEngines) : 0;
        for (int g = 0; g < MaxGear && _engineCount >= 0 && _configSeen == _configIndices.Count; g++)
        {
            if ((int)_v[_gearType[g]] == 0) continue;
            var offset = new Vector3(_v[_tireX[g]], _v[_tireY[g]], -_v[_tireZ[g]]);
            int id = session.AddWheel($"gear[{g + 1}]", offset, _v[_tireRadius[g]],
                                      driven: false, steered: _v[_gearSteers[g]] != 0, suspension: false);
            _wheels.Add((g, id));
        }
        for (int e = 0; e < _engineCount; e++)
        {
            var offset = new Vector3(_v[_engX[e]], _v[_engY[e]], -_v[_engZ[e]]);
            int type = (int)_v[_enType[e]];
            string name = $"engine[{e + 1}]";
            if (type is 5 or 7) _engPoints.Add((e, session.AddJet(name, offset), 'j'));
            else if (type == 6) _engPoints.Add((e, session.AddPropeller(name, offset), 'g')); // rocket: no rotor, rpm stays 0
            else _engPoints.Add((e, session.AddPropeller(name, offset, 0f, (ushort)Math.Max(0, _v[_blades[e]])), 'p'));
        }

        // ratified channels first (page 0)
        _engineRpm = session.AddChannel("engine.rpm", "rpm", "Engine 1 speed");
        _engineThrottle = session.AddChannel("engine.throttle", "", "Engine 1 throttle handle, 0..1");
        _engineTorque = session.AddChannel("engine.torque", "Nm", "Engine 1 torque");
        _engineRunning = session.AddBooleanChannel("engine.running", "Engine 1 running");
        _brakesPedal = session.AddChannel("brakes.pedal", "", "Brake pedal, 0..1 (larger of left and right)");
        _fuelLevel = session.AddChannel("fuel.level", "", "Fuel remaining, 0..1 of capacity");

        _scalarChannels.Clear();
        foreach (var s in _scalars)
            _scalarChannels.Add(s.Kind == Kind.Boolean
                ? session.AddBooleanChannel(s.Name, s.Description)
                : session.AddChannel(s.Name, s.Unit, s.Description));

        _engChannels = new OgsChannel[_engineCount, EngineFields.Length];
        for (int e = 0; e < _engineCount; e++)
            for (int f = 0; f < EngineFields.Length; f++)
            {
                var (_, name, unit, description, _, kind) = EngineFields[f];
                string full = $"engine[{e + 1}].{name}";
                _engChannels[e, f] = kind == Kind.Boolean
                    ? session.AddBooleanChannel(full, $"Engine {e + 1}: {description}")
                    : session.AddChannel(full, unit, $"Engine {e + 1}: {description}");
            }
        _gearChannels = new OgsChannel[_wheels.Count, GearFields.Length];
        for (int w = 0; w < _wheels.Count; w++)
            for (int f = 0; f < GearFields.Length; f++)
            {
                var (_, name, unit, description, _, _) = GearFields[f];
                int g = _wheels[w].Gear;
                _gearChannels[w, f] = session.AddChannel($"gear[{g + 1}].{name}", unit, $"Gear {g + 1}: {description}");
            }
        _layout = Layout();
    }

    /// <summary>Hash of what the drive points and per-element channels
    /// depend on (FNV-1a over the configuration values); 0 until all of
    /// them have arrived. Allocation-free: checked on every datagram.</summary>
    ulong Layout()
    {
        if (_configSeen < _configIndices.Count) return 0;
        ulong h = 14695981039346656037UL;
        foreach (int i in _configIndices)
        {
            h ^= (uint)BitConverter.SingleToInt32Bits(_v[i]);
            h *= 1099511628211UL;
        }
        return h | 1; // never 0 once complete
    }

    // ---- translation ----

    public bool AcceptsDatagram(int length) => length >= 5 + 8 && (length - 5) % 8 == 0;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (d[0] != (byte)'R' || d[1] != (byte)'R' || d[2] != (byte)'E' || d[3] != (byte)'F') return;
        for (int at = 5; at + 8 <= d.Length; at += 8)
        {
            int index = MemoryMarshal.Read<int>(d.Slice(at, 4));
            if ((uint)index >= (uint)_v.Length) continue;
            _v[index] = F(d, at + 4);
            if (!_seen[index])
            {
                _seen[index] = true;
                if (_isConfig[index]) _configSeen++;
            }
        }

        // the aircraft layout arrived or changed (aircraft swap): redeclare
        if (_configSeen == _configIndices.Count && Layout() != _layout)
        {
            session.BeginRedeclare();
            Declare(session);
            session.CommitRedeclare(now);
        }

        var position = new Vector3(_v[_x], _v[_y], -_v[_z]);
        session.SetPose(position, OgsMath.EulerToQuaternion(
            _sy * _v[_psi] * Deg, _sp * _v[_theta] * Deg, _sr * _v[_phi] * Deg));
        if (_v[_scalars[0].Index] != 0) session.ClearDynamics(); // paused (C-11)
        else
            session.SetBodyDynamics(
                new Vector3(_side * _v[_gSide], _v[_gNormal], _axial * _v[_gAxial]) * Gravity,
                new Vector3((float)(_sp * _v[_qRate]), (float)(_sy * _v[_rRate]), (float)(_sr * _v[_pRate])));

        for (int i = 0; i < _scalars.Count; i++)
        {
            var s = _scalars[i];
            if (!_seen[s.Index]) continue;
            if (s.Kind == Kind.Boolean) session.SetChannel(_scalarChannels[i], _v[s.Index] != 0);
            else session.SetChannel(_scalarChannels[i], _v[s.Index] * s.Scale);
        }
        if (_seen[_eng[0, 2]]) session.SetChannel(_engineRpm, _v[_eng[0, 2]]);
        if (_seen[_eng[0, 10]]) session.SetChannel(_engineThrottle, _v[_eng[0, 10]]);
        if (_seen[_eng[0, 9]]) session.SetChannel(_engineTorque, _v[_eng[0, 9]]);
        if (_seen[_eng[0, 14]]) session.SetChannel(_engineRunning, _v[_eng[0, 14]] != 0);
        session.SetChannel(_brakesPedal, Math.Max(_v[_leftBrake], _v[_rightBrake]));
        float capacityKg = _v[_fuelCapacityLb] * 0.45359237f;
        if (capacityKg > 0) session.SetChannel(_fuelLevel, Math.Clamp(_v[_fuelTotal] / capacityKg, 0f, 1f));

        for (int e = 0; e < _engineCount; e++)
            for (int f = 0; f < EngineFields.Length; f++)
            {
                var field = EngineFields[f];
                float v = _v[_eng[e, f]];
                if (field.Kind == Kind.Boolean) session.SetChannel(_engChannels[e, f], v != 0);
                else session.SetChannel(_engChannels[e, f], v * field.Scale);
            }
        foreach (var (e, point, kind) in _engPoints)
        {
            if (kind == 'j') session.SetJet(point, _v[_eng[e, 10]]);
            else if (kind == 'p') session.SetPropeller(point, (float)(_v[_engState[e, EPropRadS]] * RadSToRpm));
        }
        var medium = Medium((int)_v[_surface]);
        for (int w = 0; w < _wheels.Count; w++)
        {
            int g = _wheels[w].Gear;
            for (int f = 0; f < GearFields.Length; f++)
                session.SetChannel(_gearChannels[w, f], _v[_gear[g, f]] * GearFields[f].Scale);
            bool onGround = _v[_gearState[g, GOnGround]] != 0;
            session.SetWheel(_wheels[w].Point,
                (float)(_v[_gearState[g, GRate]] * RadSToRpm), _v[_gearState[g, GSkid]],
                _v[_gearState[g, GDeflection]], onGround ? medium : Schema.ContactMedium.Air);
        }

        session.Tick(now);
    }

    /// <summary>X-Plane's surface type under the aircraft as a contact medium.</summary>
    static Schema.ContactMedium Medium(int surface) => surface switch
    {
        1 or 7 => Schema.ContactMedium.Water,
        2 or 3 or 9 or 10 => Schema.ContactMedium.Asphalt,
        4 => Schema.ContactMedium.Grass,
        5 or 11 => Schema.ContactMedium.Dirt,
        6 => Schema.ContactMedium.Gravel,
        8 => Schema.ContactMedium.Snow,
        _ => Schema.ContactMedium.Other,
    };
}
}
