#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Schema = Ogsa.Telemetry.Schema;

namespace OgsaTsBridge.Sources
{
/// <summary>
/// Microsoft Flight Simulator 2020 and 2024 over SimConnect, mapped per
/// the standard's MSFS sender reference (reference-msfs2024.md).
///
/// Schema: pose (local-tangent position re-origined on teleport,
/// orientation from PLANE PITCH/BANK/HEADING), body dynamics from
/// ACCELERATION BODY (gravity reaction added) and ROTATION VELOCITY BODY,
/// environment from AMBIENT DENSITY/TEMPERATURE/PRESSURE, eyepoint and
/// engine named points, wheel drive points (rpm, skid, contact, nose
/// steer) and one propeller or jet drive point per engine.
///
/// Channels: geo, air data, wind, controls, engines (per engine,
/// <c>engine[n].*</c>), fuel, electrics, radios, lights, cabin, pushback,
/// touchdown and session state. With this many channels the session
/// sends changed values only (plus a full refresh with each discovery);
/// <c>session.paused</c> rides every frame (CONVENTIONS C-11).
///
/// Aircraft identity (title, engine count and type, geometry) arrives
/// once a second on change; a change redeclares the object.
/// </summary>
public sealed class MsfsSource : ISimConnectSource
{
    const double Gravity = 9.80665;
    const double EarthRadius = 6371008.8;
    const double TeleportMeters = 1000;    // larger one-frame jumps re-origin position
    const double FeetToMeters = 0.3048;
    const double GallonCubicMeters = 0.003785411784;
    const int MaxEngines = 4;
    const int MenuCameraState = 11;        // CAMERA STATE at or above: not in flight

    readonly List<SimVar> _frameVars = new();
    readonly List<SimVar> _aircraftVars = new();

    // frame variable indices
    readonly int _lat, _lon, _alt, _pitch, _bank, _heading;
    readonly int _ax, _ay, _az, _rx, _ry, _rz, _rax, _ray, _raz;
    readonly int _density, _temperature, _pressure;
    readonly int _agl, _ias, _tas, _mach, _vs, _alpha, _beta, _groundSpeed, _onGround;
    readonly int _windSpeed, _windDir, _windX, _windY, _windZ, _visibility, _precip, _inCloud;
    readonly int _aileron, _elevator, _rudder, _aileronDefl, _elevatorDefl, _rudderDefl;
    readonly int _trimElevator, _trimAileron, _trimRudder, _flapsIndex, _flapsPositions, _spoilers;
    readonly int _gearPct, _gearHandle, _gearDamage, _brakeLeft, _brakeRight, _parkingBrake;
    readonly int[] _engRpm = new int[MaxEngines], _engN1 = new int[MaxEngines], _engN2 = new int[MaxEngines],
                   _engItt = new int[MaxEngines], _engOilTemp = new int[MaxEngines],
                   _engOilPressure = new int[MaxEngines], _engFuelFlow = new int[MaxEngines],
                   _engRunning = new int[MaxEngines], _engFailed = new int[MaxEngines],
                   _engThrottle = new int[MaxEngines], _propRpm = new int[MaxEngines];
    readonly int _rotorRpm;
    readonly int _fuelKg, _fuelGallons, _fuelCapacity, _fuelSelected, _fuelWeightPerGallon;
    readonly int _busVoltage, _apu, _hydPressure;
    readonly int _com1, _com1Standby, _com2, _com2Standby, _nav1, _nav1Standby, _nav2, _nav2Standby;
    readonly int _adf, _xpdr, _gsi, _cdi;
    readonly int _lightLanding, _lightNav, _lightStrobe, _lightBeacon, _lightTaxi;
    readonly int _seatbelts, _noSmoking, _pushbackAttached, _pushbackAngle;
    readonly int _tdBank, _tdHeading, _tdPitch;
    readonly int _slew, _zulu, _frameRate, _simRate, _cameraState;
    readonly int _wheelRpmCenter, _wheelRpmLeft, _wheelRpmRight, _skid, _surface, _steer;
    readonly int[] _contact = new int[3];

    // aircraft variable indices
    readonly int _title, _engineCount, _engineType, _eyepoint;
    readonly int[] _enginePosition = new int[MaxEngines];

    // options
    double _sy = 1, _sp = 1, _sr = 1;
    bool _gravIncluded;
    string? _libraryPath;

    // current aircraft (declaration inputs)
    string _aircraftTitle = "";
    int _engines, _engineKind = -1;
    Vector3? _eyepointPos;
    readonly Vector3?[] _enginePos = new Vector3?[MaxEngines];

    // declared handles
    OgsaChannel _chLat, _chLon, _chAltMsl, _chAgl;
    OgsaChannel _chIas, _chTas, _chMach, _chVs, _chAoa, _chBeta, _chSpeed, _chOnGround;
    OgsaChannel _chAlphaX, _chAlphaY, _chAlphaZ;
    OgsaChannel _chWindSpeed, _chWindDir, _chWindX, _chWindY, _chWindZ, _chVisibility, _chPrecip, _chInCloud;
    OgsaChannel _chAileron, _chElevator, _chRudder, _chAileronDefl, _chElevatorDefl, _chRudderDefl;
    OgsaChannel _chTrimElevator, _chTrimAileron, _chTrimRudder, _chFlaps, _chSpoilers;
    OgsaChannel _chGearPos, _chGearDown, _chGearDamage, _chBrakes, _chParking;
    OgsaChannel _chEngineRpm, _chThrottle, _chEngineRunning;
    readonly OgsaChannel[] _chN1 = new OgsaChannel[MaxEngines], _chN2 = new OgsaChannel[MaxEngines],
                           _chEngRpm = new OgsaChannel[MaxEngines], _chItt = new OgsaChannel[MaxEngines],
                           _chOilTemp = new OgsaChannel[MaxEngines], _chOilPressure = new OgsaChannel[MaxEngines],
                           _chFuelFlow = new OgsaChannel[MaxEngines], _chRunning = new OgsaChannel[MaxEngines],
                           _chFailed = new OgsaChannel[MaxEngines];
    OgsaChannel _chFuelKg, _chFuelSelected, _chFuelLevel, _chFuelRate;
    OgsaChannel _chBusVoltage, _chApu, _chHyd;
    OgsaChannel _chCom1, _chCom1Standby, _chCom2, _chCom2Standby, _chNav1, _chNav1Standby, _chNav2, _chNav2Standby;
    OgsaChannel _chAdf, _chXpdr, _chGs, _chLoc;
    OgsaChannel _chLightLanding, _chLightNav, _chLightStrobe, _chLightBeacon, _chLightTaxi;
    OgsaChannel _chSeatbelts, _chNoSmoking, _chPushback, _chPushbackAngle;
    OgsaChannel _chTouchdown, _chTdBank, _chTdHeading, _chTdPitch, _chTdVs;
    OgsaChannel _chPaused, _chSlew, _chZulu, _chFrameRate, _chSimRate;
    int _wheelCenter, _wheelLeft, _wheelRight, _rotor = -1;
    readonly int[] _enginePoint = new int[MaxEngines];
    bool _jets;

    // per-frame state (forwarder thread)
    bool _hasOrigin, _hasPrevious;
    double _lat0, _lon0, _alt0, _cosLat0;
    Vector3 _position;
    Quaternion _orientation = Quaternion.Identity;
    bool _wasOnGround;
    double _touchdownVs;
    uint _pauseFlags;
    bool _simRunning = true, _menu;
    double _lastTick = double.NegativeInfinity;

    public MsfsSource()
    {
        int V(string name, string unit) { _frameVars.Add(new SimVar(name, unit)); return _frameVars.Count - 1; }
        int A(string name, string unit, SimVarKind kind = SimVarKind.Number)
        { _aircraftVars.Add(new SimVar(name, unit, kind)); return _aircraftVars.Count - 1; }

        _lat = V("PLANE LATITUDE", "radians");
        _lon = V("PLANE LONGITUDE", "radians");
        _alt = V("PLANE ALTITUDE", "meters");
        _pitch = V("PLANE PITCH DEGREES", "radians");
        _bank = V("PLANE BANK DEGREES", "radians");
        _heading = V("PLANE HEADING DEGREES TRUE", "radians");
        _ax = V("ACCELERATION BODY X", "meters per second squared");
        _ay = V("ACCELERATION BODY Y", "meters per second squared");
        _az = V("ACCELERATION BODY Z", "meters per second squared");
        _rx = V("ROTATION VELOCITY BODY X", "radians per second");
        _ry = V("ROTATION VELOCITY BODY Y", "radians per second");
        _rz = V("ROTATION VELOCITY BODY Z", "radians per second");
        _rax = V("ROTATION ACCELERATION BODY X", "radians per second squared");
        _ray = V("ROTATION ACCELERATION BODY Y", "radians per second squared");
        _raz = V("ROTATION ACCELERATION BODY Z", "radians per second squared");
        _density = V("AMBIENT DENSITY", "kilograms per cubic meter");
        _temperature = V("AMBIENT TEMPERATURE", "celsius");
        _pressure = V("AMBIENT PRESSURE", "millibars");

        _agl = V("PLANE ALT ABOVE GROUND", "meters");
        _ias = V("AIRSPEED INDICATED", "meters per second");
        _tas = V("AIRSPEED TRUE", "meters per second");
        _mach = V("AIRSPEED MACH", "mach");
        _vs = V("VERTICAL SPEED", "meters per second");
        _alpha = V("INCIDENCE ALPHA", "radians");
        _beta = V("INCIDENCE BETA", "radians");
        _groundSpeed = V("GROUND VELOCITY", "meters per second");
        _onGround = V("SIM ON GROUND", "bool");

        _windSpeed = V("AMBIENT WIND VELOCITY", "meters per second");
        _windDir = V("AMBIENT WIND DIRECTION", "radians");
        _windX = V("AMBIENT WIND X", "meters per second");
        _windY = V("AMBIENT WIND Y", "meters per second");
        _windZ = V("AMBIENT WIND Z", "meters per second");
        _visibility = V("AMBIENT VISIBILITY", "meters");
        _precip = V("AMBIENT PRECIP RATE", "millimeters of water");
        _inCloud = V("AMBIENT IN CLOUD", "bool");

        _aileron = V("AILERON POSITION", "position");
        _elevator = V("ELEVATOR POSITION", "position");
        _rudder = V("RUDDER POSITION", "position");
        _aileronDefl = V("AILERON AVERAGE DEFLECTION", "radians");
        _elevatorDefl = V("ELEVATOR DEFLECTION", "radians");
        _rudderDefl = V("RUDDER DEFLECTION", "radians");
        _trimElevator = V("ELEVATOR TRIM POSITION", "radians");
        _trimAileron = V("AILERON TRIM PCT", "percent over 100");
        _trimRudder = V("RUDDER TRIM PCT", "percent over 100");
        _flapsIndex = V("FLAPS HANDLE INDEX", "number");
        _flapsPositions = V("FLAPS NUM HANDLE POSITIONS", "number");
        _spoilers = V("SPOILERS HANDLE POSITION", "percent over 100");
        _gearPct = V("GEAR TOTAL PCT EXTENDED", "percent over 100");
        _gearHandle = V("GEAR HANDLE POSITION", "bool");
        _gearDamage = V("GEAR DAMAGE BY SPEED", "bool");
        _brakeLeft = V("BRAKE LEFT POSITION", "percent over 100");
        _brakeRight = V("BRAKE RIGHT POSITION", "percent over 100");
        _parkingBrake = V("BRAKE PARKING INDICATOR", "bool");

        for (int e = 0; e < MaxEngines; e++)
        {
            int n = e + 1;
            _engRpm[e] = V($"GENERAL ENG RPM:{n}", "rpm");
            _engN1[e] = V($"TURB ENG N1:{n}", "percent over 100");
            _engN2[e] = V($"TURB ENG N2:{n}", "percent over 100");
            _engItt[e] = V($"TURB ENG ITT:{n}", "celsius");
            _engOilTemp[e] = V($"GENERAL ENG OIL TEMPERATURE:{n}", "celsius");
            _engOilPressure[e] = V($"GENERAL ENG OIL PRESSURE:{n}", "pascals");
            _engFuelFlow[e] = V($"ENG FUEL FLOW PPH:{n}", "kilograms per second");
            _engRunning[e] = V($"GENERAL ENG COMBUSTION:{n}", "bool");
            _engFailed[e] = V($"GENERAL ENG FAILED:{n}", "bool");
            _engThrottle[e] = V($"GENERAL ENG THROTTLE LEVER POSITION:{n}", "percent over 100");
            _propRpm[e] = V($"PROP RPM:{n}", "rpm");
        }
        _rotorRpm = V("ROTOR RPM:1", "rpm");

        _fuelKg = V("FUEL TOTAL QUANTITY WEIGHT", "kilograms");
        _fuelGallons = V("FUEL TOTAL QUANTITY", "gallons");
        _fuelCapacity = V("FUEL TOTAL CAPACITY", "gallons");
        _fuelSelected = V("FUEL SELECTED QUANTITY", "gallons");
        _fuelWeightPerGallon = V("FUEL WEIGHT PER GALLON", "kilograms");
        _busVoltage = V("ELECTRICAL MAIN BUS VOLTAGE", "volts");
        _apu = V("APU SWITCH", "bool");
        _hydPressure = V("HYDRAULIC PRESSURE:1", "pascals");

        _com1 = V("COM ACTIVE FREQUENCY:1", "MHz");
        _com1Standby = V("COM STANDBY FREQUENCY:1", "MHz");
        _com2 = V("COM ACTIVE FREQUENCY:2", "MHz");
        _com2Standby = V("COM STANDBY FREQUENCY:2", "MHz");
        _nav1 = V("NAV ACTIVE FREQUENCY:1", "MHz");
        _nav1Standby = V("NAV STANDBY FREQUENCY:1", "MHz");
        _nav2 = V("NAV ACTIVE FREQUENCY:2", "MHz");
        _nav2Standby = V("NAV STANDBY FREQUENCY:2", "MHz");
        _adf = V("ADF RADIAL:1", "radians");
        _xpdr = V("TRANSPONDER CODE:1", "BCO16");
        _gsi = V("NAV GSI:1", "number");
        _cdi = V("NAV CDI:1", "number");

        _lightLanding = V("LIGHT LANDING", "bool");
        _lightNav = V("LIGHT NAV", "bool");
        _lightStrobe = V("LIGHT STROBE", "bool");
        _lightBeacon = V("LIGHT BEACON", "bool");
        _lightTaxi = V("LIGHT TAXI", "bool");
        _seatbelts = V("CABIN SEATBELTS ALERT SWITCH", "bool");
        _noSmoking = V("CABIN NO SMOKING ALERT SWITCH", "bool");
        _pushbackAttached = V("PUSHBACK ATTACHED", "bool");
        _pushbackAngle = V("PUSHBACK ANGLE", "radians");

        _tdBank = V("PLANE TOUCHDOWN BANK DEGREES", "radians");
        _tdHeading = V("PLANE TOUCHDOWN HEADING DEGREES TRUE", "radians");
        _tdPitch = V("PLANE TOUCHDOWN PITCH DEGREES", "radians");

        _slew = V("IS SLEW ACTIVE", "bool");
        _zulu = V("ZULU TIME", "seconds");
        _frameRate = V("FRAME RATE", "number");
        _simRate = V("SIMULATION RATE", "number");
        _cameraState = V("CAMERA STATE", "number");

        _wheelRpmCenter = V("CENTER WHEEL RPM", "rpm");
        _wheelRpmLeft = V("LEFT WHEEL RPM", "rpm");
        _wheelRpmRight = V("RIGHT WHEEL RPM", "rpm");
        _skid = V("GEAR SKIDDING FACTOR", "percent over 100");
        for (int i = 0; i < 3; i++) _contact[i] = V($"CONTACT POINT IS ON GROUND:{i}", "bool");
        _surface = V("SURFACE TYPE", "enum");
        _steer = V("GEAR CENTER STEER ANGLE", "radians");

        _title = A("TITLE", "", SimVarKind.Text);
        _engineCount = A("NUMBER OF ENGINES", "number");
        _engineType = A("ENGINE TYPE", "enum");
        _eyepoint = A("EYEPOINT POSITION", "meters", SimVarKind.Xyz);
        for (int e = 0; e < MaxEngines; e++)
            _enginePosition[e] = A($"STRUCT ENGINE POSITION:{e + 1}", "meters", SimVarKind.Xyz);
    }

    public string Id => "msfs";
    public string Title => "Microsoft Flight Simulator (SimConnect)";
    public string Description =>
        "Microsoft Flight Simulator 2020 / 2024 over SimConnect — no in-sim " +
        "setup; the Bridge connects once a flight is loading.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "FlightSimulator", "FlightSimulator2024" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Microsoft Flight Simulator (2020)", "FlightSimulator"),
        new LegacyGame("Microsoft Flight Simulator 2024", "FlightSimulator2024"),
    };

    public SourceTransport Transport => SourceTransport.SimConnect;
    public IReadOnlyList<SimVar> FrameVars => _frameVars;
    public IReadOnlyList<SimVar> AircraftVars => _aircraftVars;
    public string? LibraryPath => _libraryPath;

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",      "negate yaw Euler sign and yaw rate (bool, default false)"),
        ("flipPitch",    "negate pitch Euler sign and pitch rate (bool, default false)"),
        ("flipRoll",     "negate roll Euler sign and roll rate (bool, default false)"),
        ("gravIncluded", "ACCELERATION BODY already includes the gravity reaction (bool, default false)"),
        ("simconnect",   "path to SimConnect.dll or its folder (default: the bundled copy)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "gravIncluded", "simconnect");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _gravIncluded = o.GetBool("gravIncluded", false);
        var path = o.GetString("simconnect", "");
        _libraryPath = path.Length > 0 ? path : null;
    }

    public void Declare(OgsaSession session)
    {
        session.SendChangedChannelsOnly();
        DeclareAircraft(session);
    }

    void DeclareAircraft(OgsaSession session)
    {
        session.DeclareObject("aircraft", Schema.ObjectType.Aircraft, _aircraftTitle);

        if (_eyepointPos is { } eye) session.AddNamedPoint("pilot", eye);
        for (int e = 0; e < _engines; e++)
            if (_enginePos[e] is { } pos) session.AddNamedPoint($"engine[{e + 1}]", pos);

        _wheelCenter = session.AddWheel("wheel.center", Vector3.Zero, 0f, false, true, suspension: false);
        _wheelLeft = session.AddWheel("wheel.left", Vector3.Zero, 0f, false, false, suspension: false);
        _wheelRight = session.AddWheel("wheel.right", Vector3.Zero, 0f, false, false, suspension: false);

        // piston 0, jet 1, none 2, helo turbine 3, unsupported 4, turboprop 5
        _jets = _engineKind == 1;
        _rotor = _engineKind == 3 ? session.AddPropeller("rotor.main", Vector3.Zero) : -1;
        for (int e = 0; e < _engines; e++)
        {
            var offset = _enginePos[e] ?? Vector3.Zero;
            _enginePoint[e] = _engineKind switch
            {
                0 or 5 => session.AddPropeller($"engine[{e + 1}].propeller", offset),
                1 => session.AddJet($"engine[{e + 1}].jet", offset),
                _ => -1,
            };
        }

        _chPaused = session.AddBooleanChannel("session.paused", "Simulation paused or not in flight (C-11)");
        session.AlwaysSend(_chPaused);
        _chSlew = session.AddBooleanChannel("session.slew");
        _chZulu = session.AddChannel("session.timeOfDayZulu", "s");
        _chFrameRate = session.AddChannel("perf.frameRate", "Hz");
        _chSimRate = session.AddChannel("perf.simRate", "", "Simulation rate multiplier");

        _chLat = session.AddChannel("position.lat", "rad");
        _chLon = session.AddChannel("position.lon", "rad");
        _chAltMsl = session.AddChannel("position.altMsl", "m");
        _chAgl = session.AddChannel("env.heightFromGround", "m");

        _chIas = session.AddChannel("air.indicatedAirspeed", "m/s");
        _chTas = session.AddChannel("air.trueAirspeed", "m/s");
        _chMach = session.AddChannel("air.mach", "");
        _chVs = session.AddChannel("air.verticalSpeed", "m/s");
        _chAoa = session.AddChannel("air.aoa", "rad");
        _chBeta = session.AddChannel("air.beta", "rad", "Sideslip angle");
        _chAlphaX = session.AddChannel("air.alphaX", "rad/s2", "Angular acceleration, pitch (+ nose-up)");
        _chAlphaY = session.AddChannel("air.alphaY", "rad/s2", "Angular acceleration, yaw (+ nose-right)");
        _chAlphaZ = session.AddChannel("air.alphaZ", "rad/s2", "Angular acceleration, roll (+ right-down)");
        _chSpeed = session.AddChannel("vehicle.speed", "m/s");
        _chOnGround = session.AddBooleanChannel("air.onGround");

        _chWindSpeed = session.AddChannel("wind.trueSpeed", "m/s");
        _chWindDir = session.AddChannel("wind.trueDirection", "rad");
        _chWindX = session.AddChannel("wind.x", "m/s");
        _chWindY = session.AddChannel("wind.y", "m/s");
        _chWindZ = session.AddChannel("wind.z", "m/s");
        _chVisibility = session.AddChannel("env.visibility", "m");
        _chPrecip = session.AddChannel("env.precipRate", "", "Precipitation rate (sim units)");
        _chInCloud = session.AddBooleanChannel("env.inCloud");

        _chAileron = session.AddChannel("ctrl.aileron", "", "", -1, 1);
        _chElevator = session.AddChannel("ctrl.elevator", "", "", -1, 1);
        _chRudder = session.AddChannel("ctrl.rudder", "", "", -1, 1);
        _chAileronDefl = session.AddChannel("ctrl.aileronDeflection", "rad");
        _chElevatorDefl = session.AddChannel("ctrl.elevatorDeflection", "rad");
        _chRudderDefl = session.AddChannel("ctrl.rudderDeflection", "rad");
        _chTrimElevator = session.AddChannel("ctrl.trim.elevator", "rad");
        _chTrimAileron = session.AddChannel("ctrl.trim.aileron", "", "", -1, 1);
        _chTrimRudder = session.AddChannel("ctrl.trim.rudder", "", "", -1, 1);
        _chFlaps = session.AddChannel("ctrl.flaps", "", "", 0, 1);
        _chSpoilers = session.AddChannel("ctrl.spoilers", "", "", 0, 1);
        _chGearPos = session.AddChannel("gear.position", "", "", 0, 1);
        _chGearDown = session.AddBooleanChannel("gear.down");
        _chGearDamage = session.AddBooleanChannel("gear.damageBySpeed");
        _chBrakes = session.AddChannel("brakes.pedal", "", "", 0, 1);
        _chParking = session.AddBooleanChannel("brakes.parking");

        _chEngineRpm = session.AddChannel("engine.rpm", "rpm");
        _chThrottle = session.AddChannel("engine.throttle", "", "", -1, 1);
        _chEngineRunning = session.AddBooleanChannel("engine.running");
        for (int e = 0; e < _engines; e++)
        {
            string p = $"engine[{e + 1}].";
            _chN1[e] = session.AddChannel(p + "n1", "", "", 0, 1);
            _chN2[e] = session.AddChannel(p + "n2", "", "", 0, 1);
            _chEngRpm[e] = session.AddChannel(p + "rpm", "rpm");
            _chItt[e] = session.AddChannel(p + "itt", "degC");
            _chOilTemp[e] = session.AddChannel(p + "oilTemp", "degC");
            _chOilPressure[e] = session.AddChannel(p + "oilPressure", "Pa");
            _chFuelFlow[e] = session.AddChannel(p + "fuelFlow", "kg/s");
            _chRunning[e] = session.AddBooleanChannel(p + "running");
            _chFailed[e] = session.AddBooleanChannel($"fail.engine[{e + 1}]");
        }

        _chFuelKg = session.AddChannel("fuel.quantity", "kg");
        _chFuelSelected = session.AddChannel("fuel.selectedKg", "kg");
        _chFuelLevel = session.AddChannel("fuel.level", "", "", 0, 1);
        _chFuelRate = session.AddChannel("fuel.rate", "m3/s");
        _chBusVoltage = session.AddChannel("elec.busVoltage", "V");
        _chApu = session.AddBooleanChannel("elec.apuOn");
        _chHyd = session.AddChannel("hyd.pressure", "Pa");

        _chCom1 = session.AddChannel("nav.com1.active", "Hz");
        _chCom1Standby = session.AddChannel("nav.com1.standby", "Hz");
        _chCom2 = session.AddChannel("nav.com2.active", "Hz");
        _chCom2Standby = session.AddChannel("nav.com2.standby", "Hz");
        _chNav1 = session.AddChannel("nav.nav1.active", "Hz");
        _chNav1Standby = session.AddChannel("nav.nav1.standby", "Hz");
        _chNav2 = session.AddChannel("nav.nav2.active", "Hz");
        _chNav2Standby = session.AddChannel("nav.nav2.standby", "Hz");
        _chAdf = session.AddChannel("nav.adf.bearing", "rad");
        _chXpdr = session.AddChannel("nav.xpdr.code", "", "Squawk as its four digits");
        _chGs = session.AddChannel("nav.gs", "", "Glideslope deviation", -1, 1);
        _chLoc = session.AddChannel("nav.locDeflection", "", "Course deviation", -1, 1);

        _chLightLanding = session.AddBooleanChannel("light.landing");
        _chLightNav = session.AddBooleanChannel("light.nav");
        _chLightStrobe = session.AddBooleanChannel("light.strobe");
        _chLightBeacon = session.AddBooleanChannel("light.beacon");
        _chLightTaxi = session.AddBooleanChannel("light.taxi");
        _chSeatbelts = session.AddBooleanChannel("cabin.seatbelts");
        _chNoSmoking = session.AddBooleanChannel("cabin.noSmoking");
        _chPushback = session.AddBooleanChannel("pushback.attached");
        _chPushbackAngle = session.AddChannel("pushback.angle", "rad");

        _chTouchdown = session.AddBooleanChannel("impact.touchdown", "True on the frame the aircraft touches down");
        _chTdBank = session.AddChannel("impact.touchdownBank", "rad");
        _chTdHeading = session.AddChannel("impact.touchdownHeading", "rad");
        _chTdPitch = session.AddChannel("impact.touchdownPitch", "rad");
        _chTdVs = session.AddChannel("impact.verticalSpeed", "m/s", "Vertical speed at the last touchdown");
    }

    public bool AcceptsDatagram(int length) => length >= _frameVars.Count * sizeof(double);

    public void OnSimState(uint pauseFlags, bool simRunning)
    {
        _pauseFlags = pauseFlags;
        _simRunning = simRunning;
    }

    bool Paused => _pauseFlags != 0 || !_simRunning || _menu;

    public void OnAircraft(SimVarValues a, OgsaSession session, double now)
    {
        var title = a.Texts[_title] ?? "";
        int engines = Math.Clamp(Count(a.Numbers[_engineCount]), 0, MaxEngines);
        int kind = double.IsNaN(a.Numbers[_engineType]) ? -1 : (int)a.Numbers[_engineType];
        var eye = a.Vectors[_eyepoint];

        bool changed = title != _aircraftTitle || engines != _engines || kind != _engineKind ||
                       !Same(eye, _eyepointPos);
        for (int e = 0; e < MaxEngines; e++)
            changed |= !Same(a.Vectors[_enginePosition[e]], _enginePos[e]);
        if (!changed) return;

        _aircraftTitle = title;
        _engines = engines;
        _engineKind = kind;
        _eyepointPos = eye;
        for (int e = 0; e < MaxEngines; e++) _enginePos[e] = a.Vectors[_enginePosition[e]];

        session.BeginRedeclare();
        Declare(session);
        session.CommitRedeclare(now);
        _hasPrevious = false;
    }

    static int Count(double v) => double.IsNaN(v) ? 0 : (int)v;

    static bool Same(Vector3? a, Vector3? b) =>
        a.HasValue == b.HasValue && (!a.HasValue || Vector3.DistanceSquared(a.Value, b!.Value) < 1e-4f);

    public void OnIdle(OgsaSession session, double now)
    {
        // a paused sim may stop sending frames: keep streaming the frozen
        // pose with session.paused set (CONVENTIONS C-11)
        if (!Paused || !_hasPrevious || now - _lastTick < 1.0 / 60) return;
        session.SetPose(_position, _orientation);
        session.ClearDynamics();
        session.SetChannel(_chPaused, true);
        session.Tick(now);
        _lastTick = now;
    }

    public void Translate(ReadOnlySpan<byte> datagram, OgsaSession session, double now)
    {
        var v = MemoryMarshal.Cast<byte, double>(datagram);

        double camera = v[_cameraState];
        _menu = !double.IsNaN(camera) && camera >= MenuCameraState;
        bool paused = Paused;

        // pose
        UpdatePosition(v[_lat], v[_lon], v[_alt]);
        // MSFS reports pitch positive nose-down and bank positive left-down
        _orientation = OgsaMath.EulerToQuaternion(
            _sy * Z(v[_heading]), _sp * -Z(v[_pitch]), _sr * -Z(v[_bank]));
        session.SetPose(_position, _orientation);

        // dynamics: none while paused (C-11)
        if (paused) session.ClearDynamics();
        else
        {
            var a = new Vector3((float)Z(v[_ax]), (float)Z(v[_ay]), (float)Z(v[_az]));
            if (!_gravIncluded)
                a += Vector3.Transform(new Vector3(0f, (float)Gravity, 0f), Quaternion.Conjugate(_orientation));
            session.SetBodyDynamics(a, BodyRates(v, _rx, _ry, _rz));
        }

        if (!double.IsNaN(v[_density]) && !double.IsNaN(v[_temperature]) && !double.IsNaN(v[_pressure]))
            session.SetEnvironment((float)v[_density], (float)v[_temperature], (float)(v[_pressure] * 100.0));

        // wheels
        var medium = Medium(v[_surface]);
        float skid = (float)Z(v[_skid]);
        SetWheel(session, _wheelCenter, v[_wheelRpmCenter], skid, v[_contact[0]], medium);
        SetWheel(session, _wheelLeft, v[_wheelRpmLeft], skid, v[_contact[1]], medium);
        SetWheel(session, _wheelRight, v[_wheelRpmRight], skid, v[_contact[2]], medium);
        if (!double.IsNaN(v[_steer])) session.SetWheelSteer(_wheelCenter, (float)v[_steer]);

        // engines
        bool anyRunning = false;
        for (int e = 0; e < _engines; e++)
        {
            Num(session, _chN1[e], v[_engN1[e]]);
            Num(session, _chN2[e], v[_engN2[e]]);
            Num(session, _chEngRpm[e], v[_engRpm[e]]);
            Num(session, _chItt[e], v[_engItt[e]]);
            Num(session, _chOilTemp[e], v[_engOilTemp[e]]);
            Num(session, _chOilPressure[e], v[_engOilPressure[e]]);
            Num(session, _chFuelFlow[e], v[_engFuelFlow[e]]);
            Bool(session, _chRunning[e], v[_engRunning[e]]);
            Bool(session, _chFailed[e], v[_engFailed[e]]);
            anyRunning |= v[_engRunning[e]] > 0;
            if (_enginePoint[e] < 0) continue;
            if (_jets) session.SetJet(_enginePoint[e], (float)Math.Clamp(Z(v[_engThrottle[e]]), -1, 1));
            else session.SetPropeller(_enginePoint[e], (float)Z(v[_propRpm[e]]));
        }
        if (_rotor >= 0) session.SetPropeller(_rotor, (float)Z(v[_rotorRpm]));
        Num(session, _chEngineRpm, v[_engRpm[0]]);
        if (!double.IsNaN(v[_engThrottle[0]]))
            session.SetChannel(_chThrottle, Math.Clamp(v[_engThrottle[0]], -1, 1));
        session.SetChannel(_chEngineRunning, anyRunning);

        // session
        session.SetChannel(_chPaused, paused);
        Bool(session, _chSlew, v[_slew]);
        Num(session, _chZulu, v[_zulu]);
        Num(session, _chFrameRate, v[_frameRate]);
        Num(session, _chSimRate, v[_simRate]);

        // geo + air data
        Num(session, _chLat, v[_lat]);
        Num(session, _chLon, v[_lon]);
        Num(session, _chAltMsl, v[_alt]);
        Num(session, _chAgl, v[_agl]);
        Num(session, _chIas, v[_ias]);
        Num(session, _chTas, v[_tas]);
        Num(session, _chMach, v[_mach]);
        Num(session, _chVs, v[_vs]);
        Num(session, _chAoa, v[_alpha]);
        Num(session, _chBeta, v[_beta]);
        var alpha = BodyRates(v, _rax, _ray, _raz);
        session.SetChannel(_chAlphaX, alpha.X);
        session.SetChannel(_chAlphaY, alpha.Y);
        session.SetChannel(_chAlphaZ, alpha.Z);
        Num(session, _chSpeed, v[_groundSpeed]);
        Bool(session, _chOnGround, v[_onGround]);

        // wind + ambient
        Num(session, _chWindSpeed, v[_windSpeed]);
        Num(session, _chWindDir, v[_windDir]);
        Num(session, _chWindX, v[_windX]);
        Num(session, _chWindY, v[_windY]);
        Num(session, _chWindZ, v[_windZ]);
        Num(session, _chVisibility, v[_visibility]);
        Num(session, _chPrecip, v[_precip]);
        Bool(session, _chInCloud, v[_inCloud]);

        // controls
        Num(session, _chAileron, v[_aileron]);
        Num(session, _chElevator, v[_elevator]);
        Num(session, _chRudder, v[_rudder]);
        Num(session, _chAileronDefl, v[_aileronDefl]);
        Num(session, _chElevatorDefl, v[_elevatorDefl]);
        Num(session, _chRudderDefl, v[_rudderDefl]);
        Num(session, _chTrimElevator, v[_trimElevator]);
        Num(session, _chTrimAileron, v[_trimAileron]);
        Num(session, _chTrimRudder, v[_trimRudder]);
        double positions = v[_flapsPositions];
        if (!double.IsNaN(v[_flapsIndex]) && positions > 0)
            session.SetChannel(_chFlaps, Math.Clamp(v[_flapsIndex] / positions, 0, 1));
        Num(session, _chSpoilers, v[_spoilers]);
        Num(session, _chGearPos, v[_gearPct]);
        Bool(session, _chGearDown, v[_gearHandle]);
        Bool(session, _chGearDamage, v[_gearDamage]);
        if (!double.IsNaN(v[_brakeLeft]) && !double.IsNaN(v[_brakeRight]))
            session.SetChannel(_chBrakes, Math.Clamp((v[_brakeLeft] + v[_brakeRight]) * 0.5, 0, 1));
        Bool(session, _chParking, v[_parkingBrake]);

        // fuel
        Num(session, _chFuelKg, v[_fuelKg]);
        double perGallon = v[_fuelWeightPerGallon];
        if (!double.IsNaN(v[_fuelSelected]) && !double.IsNaN(perGallon))
            session.SetChannel(_chFuelSelected, v[_fuelSelected] * perGallon);
        if (!double.IsNaN(v[_fuelGallons]) && v[_fuelCapacity] > 0)
            session.SetChannel(_chFuelLevel, Math.Clamp(v[_fuelGallons] / v[_fuelCapacity], 0, 1));
        if (perGallon > 0)
        {
            double flow = 0;
            for (int e = 0; e < _engines; e++) flow += Z(v[_engFuelFlow[e]]);
            session.SetChannel(_chFuelRate, flow / (perGallon / GallonCubicMeters));
        }

        // electrics / hydraulics
        Num(session, _chBusVoltage, v[_busVoltage]);
        Bool(session, _chApu, v[_apu]);
        Num(session, _chHyd, v[_hydPressure]);

        // radios
        Num(session, _chCom1, v[_com1], 1e6);
        Num(session, _chCom1Standby, v[_com1Standby], 1e6);
        Num(session, _chCom2, v[_com2], 1e6);
        Num(session, _chCom2Standby, v[_com2Standby], 1e6);
        Num(session, _chNav1, v[_nav1], 1e6);
        Num(session, _chNav1Standby, v[_nav1Standby], 1e6);
        Num(session, _chNav2, v[_nav2], 1e6);
        Num(session, _chNav2Standby, v[_nav2Standby], 1e6);
        Num(session, _chAdf, v[_adf]);
        if (!double.IsNaN(v[_xpdr])) session.SetChannel(_chXpdr, Squawk(v[_xpdr]));
        Num(session, _chGs, v[_gsi], 1.0 / 127);
        Num(session, _chLoc, v[_cdi], 1.0 / 127);

        // lights, cabin, ground service
        Bool(session, _chLightLanding, v[_lightLanding]);
        Bool(session, _chLightNav, v[_lightNav]);
        Bool(session, _chLightStrobe, v[_lightStrobe]);
        Bool(session, _chLightBeacon, v[_lightBeacon]);
        Bool(session, _chLightTaxi, v[_lightTaxi]);
        Bool(session, _chSeatbelts, v[_seatbelts]);
        Bool(session, _chNoSmoking, v[_noSmoking]);
        Bool(session, _chPushback, v[_pushbackAttached]);
        Num(session, _chPushbackAngle, v[_pushbackAngle]);

        // touchdown: rising edge of SIM ON GROUND; the sim latches the attitude
        bool onGround = v[_onGround] > 0;
        bool touchdown = onGround && !_wasOnGround && _hasPrevious;
        if (touchdown) _touchdownVs = Z(v[_vs]);
        _wasOnGround = onGround;
        session.SetChannel(_chTouchdown, touchdown);
        Num(session, _chTdBank, -v[_tdBank], _sr);
        Num(session, _chTdHeading, v[_tdHeading], _sy);
        Num(session, _chTdPitch, -v[_tdPitch], _sp);
        session.SetChannel(_chTdVs, _touchdownVs);

        _hasPrevious = true;
        session.Tick(now);
        _lastTick = now;
    }

    // body axes match OGSA (x right, y up, z forward); MSFS rotations are
    // positive nose-down about x and left-down about z, as its Euler angles
    Vector3 BodyRates(ReadOnlySpan<double> v, int x, int y, int z) => new(
        (float)(_sp * -Z(v[x])), (float)(_sy * Z(v[y])), (float)(_sr * -Z(v[z])));

    void UpdatePosition(double lat, double lon, double alt)
    {
        if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsNaN(alt)) return;
        if (!_hasOrigin) SetOrigin(lat, lon, alt);
        var p = LocalPosition(lat, lon, alt);
        if (_hasPrevious && Vector3.Distance(p, _position) > TeleportMeters)
        {
            SetOrigin(lat, lon, alt);
            p = Vector3.Zero;
        }
        _position = p;
    }

    void SetOrigin(double lat, double lon, double alt)
    {
        _lat0 = lat; _lon0 = lon; _alt0 = alt;
        _cosLat0 = Math.Cos(lat);
        _hasOrigin = true;
    }

    // local tangent plane at the origin: +x east, +y up, +z north
    Vector3 LocalPosition(double lat, double lon, double alt)
    {
        double dLon = Math.IEEERemainder(lon - _lon0, 2 * Math.PI);
        return new Vector3(
            (float)(dLon * _cosLat0 * EarthRadius),
            (float)(alt - _alt0),
            (float)((lat - _lat0) * EarthRadius));
    }

    static void SetWheel(OgsaSession session, int index, double rpm, float skid, double contact,
                         Schema.ContactMedium medium)
    {
        if (double.IsNaN(rpm)) return;
        session.SetWheel(index, (float)rpm, skid, 0f, contact > 0 ? medium : Schema.ContactMedium.Air);
    }

    // SURFACE TYPE enum → ContactMedium
    static Schema.ContactMedium Medium(double surface) => double.IsNaN(surface)
        ? Schema.ContactMedium.Other
        : (int)surface switch
        {
            0 or 4 or 10 or 15 or 16 or 17 or 18 or 19 or 20 or 23 => Schema.ContactMedium.Asphalt,
            1 or 3 or 5 or 6 or 7 or 11 => Schema.ContactMedium.Grass,
            2 => Schema.ContactMedium.Water,
            8 => Schema.ContactMedium.Snow,
            9 => Schema.ContactMedium.Ice,
            12 or 21 or 22 or 24 => Schema.ContactMedium.Dirt,
            13 or 14 => Schema.ContactMedium.Gravel,
            _ => Schema.ContactMedium.Other,
        };

    // BCO16: four octal digits packed one per nibble
    static double Squawk(double bco)
    {
        int raw = (int)bco;
        return ((raw >> 12) & 0xF) * 1000 + ((raw >> 8) & 0xF) * 100 + ((raw >> 4) & 0xF) * 10 + (raw & 0xF);
    }

    static double Z(double x) => double.IsNaN(x) ? 0 : x;

    static void Num(OgsaSession session, OgsaChannel channel, double value, double scale = 1)
    {
        if (!double.IsNaN(value)) session.SetChannel(channel, value * scale);
    }

    static void Bool(OgsaSession session, OgsaChannel channel, double value)
    {
        if (!double.IsNaN(value)) session.SetChannel(channel, value > 0);
    }
}
}
