#nullable enable
using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Falcon BMS 4.x shared memory (the F4SharedMem layout): the primary
/// FlightData page "FalconSharedMemoryArea" (1924 bytes), FlightData2
/// "FalconSharedMemoryArea2" (1264 bytes) and the IntelliVibe page
/// "FalconIntellivibeSharedMemoryArea" (52 bytes). BMS publishes them
/// whenever it runs.
///
/// Gather copies the three pages into one snapshot: primary @0,
/// FlightData2 @<see cref="Fd2"/>, IntelliVibe @<see cref="Iv"/>, presence
/// flags @<see cref="Present"/>. The pages carry no change counter, so a
/// snapshot identical to the last one is dropped.
///
/// Mapped: every scalar of FlightData (position, velocities, air data,
/// attitude, engine, fuel, gear, lights and HSI bitfields, head pose, ILS,
/// HSI values, trims, DED and PFL lines with their invert masks, TACAN
/// channels, version, main power), the RWR contacts (symbol, bearing,
/// lethality, and activity / launch / selected / new-detection / jamming
/// packed into one flags channel each), every scalar of FlightData2
/// (second engine, nav mode, altimeter, power, blink, CMDS, radios, cabin
/// and hydraulics, time, aircraft index, flaps, VTOL, MP pilots' callsigns
/// and status, bump, lat/long, IFF, lighting, bitfields, radar altitude,
/// bingo, bullseye, BMS version, string/drawing area stamps, turn rate,
/// magnetic deviation, ECM) and all of IntelliVibe. Not mapped: the
/// 512-byte binary RwrInfo blob and the render-to-texture rectangles.
///
/// Frame: sim coordinates are North/East/Down in ft → OGS world (East,
/// −Down, North) in m; pitch, roll and yaw are aerospace-standard radians
/// and map direct. World velocity and its derivative ride Kinematics.
/// Nothing is emitted outside 3D, at end of flight or exit; pause or
/// freeze rides session.paused.
/// </summary>
public sealed class FalconBmsSource : ILegacySource, ISharedMemoryGather
{
    const float Ft = 0.3048f;
    const double Deg = Math.PI / 180, Kt = 0.514444, Lb = 0.45359237;
    internal const int PrimarySize = 1924, Fd2Size = 1264, IvSize = 52;
    internal const int Fd2 = PrimarySize, Iv = Fd2 + Fd2Size, Present = Iv + IvSize, SnapshotSize = Present + 1;
    const int RwrObjects = 40;

    enum K : byte { F32, I32, U32, U8, I8, I16, Bool8 }

    /// <summary>Scalar fields: snapshot offset, kind, name, unit, description, scale.</summary>
    static readonly (int Off, K Kind, string Name, string Unit, string Description, double Scale)[] Fields =
    {
        // FlightData
        (0, K.F32, "falcon.position.north", "m", "Ownship north", Ft), (4, K.F32, "falcon.position.east", "m", "Ownship east", Ft),
        (8, K.F32, "falcon.position.down", "m", "Ownship down", Ft),
        (8, K.F32, "position.altMsl", "m", "Altitude (−down)", -Ft),
        (12, K.F32, "falcon.velocity.north", "m/s", "North rate", Ft), (16, K.F32, "falcon.velocity.east", "m/s", "East rate", Ft),
        (20, K.F32, "falcon.velocity.down", "m/s", "Down rate", Ft),
        (20, K.F32, "air.verticalSpeed", "m/s", "Vertical speed (−down rate)", -Ft),
        (24, K.F32, "air.aoa", "rad", "Angle of attack", Deg), (28, K.F32, "air.beta", "rad", "Sideslip", Deg),
        (32, K.F32, "falcon.gamma", "rad", "Flight path angle", 1),
        (36, K.F32, "falcon.pitch", "rad", "Pitch", 1), (40, K.F32, "falcon.roll", "rad", "Roll", 1), (44, K.F32, "falcon.yaw", "rad", "Yaw", 1),
        (48, K.F32, "air.mach", "", "Mach number", 1), (52, K.F32, "air.indicatedAirspeed", "m/s", "Indicated airspeed", Kt),
        (56, K.F32, "air.trueAirspeed", "m/s", "True airspeed", Ft), (60, K.F32, "air.loadFactor", "", "Normal g", 1),
        (64, K.F32, "falcon.windOffset", "rad", "Wind delta to flight path marker", 1),
        (68, K.F32, "falcon.nozzlePos", "", "Nozzle open 0..1", 0.01), (72, K.F32, "falcon.internalFuel", "kg", "Internal fuel", Lb),
        (76, K.F32, "falcon.externalFuel", "kg", "External fuel", Lb), (80, K.F32, "falcon.fuelFlow", "kg/s", "Fuel flow", Lb / 3600),
        (84, K.F32, "falcon.rpm", "", "Engine rpm fraction (0..1.03)", 0.01), (88, K.F32, "falcon.ftit", "degC", "Forward turbine inlet temperature", 1),
        (92, K.F32, "gear.position", "", "Gear position 0 up .. 1 down", 1), (96, K.F32, "falcon.speedBrake", "", "Speed brake 0 closed .. 1 (60°)", 1),
        (100, K.F32, "falcon.epuFuel", "", "EPU fuel 0..1", 0.01), (104, K.F32, "falcon.oilPressure", "", "Oil pressure 0..1", 0.01),
        (108, K.U32, "falcon.lightBits", "", "Cockpit lights bitfield 1", 1),
        (112, K.F32, "falcon.head.pitch", "rad", "Head pitch from design eye", 1), (116, K.F32, "falcon.head.roll", "rad", "Head roll from design eye", 1),
        (120, K.F32, "falcon.head.yaw", "rad", "Head yaw from design eye", 1),
        (124, K.U32, "falcon.lightBits2", "", "Cockpit lights bitfield 2", 1), (128, K.U32, "falcon.lightBits3", "", "Cockpit lights bitfield 3", 1),
        (132, K.F32, "falcon.chaff", "", "Chaff remaining", 1), (136, K.F32, "falcon.flares", "", "Flares remaining", 1),
        (140, K.F32, "falcon.gear.nose", "", "Nose gear position", 1), (144, K.F32, "falcon.gear.left", "", "Left gear position", 1),
        (148, K.F32, "falcon.gear.right", "", "Right gear position", 1),
        (152, K.F32, "falcon.adiIls.horizontal", "", "ADI ILS horizontal bar", 1), (156, K.F32, "falcon.adiIls.vertical", "", "ADI ILS vertical bar", 1),
        (160, K.I32, "falcon.hsi.courseState", "", "HSI course state", 1), (164, K.I32, "falcon.hsi.headingState", "", "HSI heading state", 1),
        (168, K.I32, "falcon.hsi.totalStates", "", "HSI total states", 1),
        (172, K.F32, "falcon.hsi.courseDeviation", "", "HSI course deviation (as sent)", 1),
        (176, K.F32, "falcon.hsi.desiredCourse", "", "HSI desired course (as sent)", 1),
        (180, K.F32, "falcon.hsi.distanceToBeacon", "", "HSI distance to beacon (as sent)", 1),
        (184, K.F32, "falcon.hsi.bearingToBeacon", "", "HSI bearing to beacon (as sent)", 1),
        (188, K.F32, "falcon.hsi.currentHeading", "", "HSI current heading (as sent)", 1),
        (192, K.F32, "falcon.hsi.desiredHeading", "", "HSI desired heading (as sent)", 1),
        (196, K.F32, "falcon.hsi.deviationLimit", "", "HSI deviation limit", 1),
        (200, K.F32, "falcon.hsi.halfDeviationLimit", "", "HSI half deviation limit", 1),
        (204, K.F32, "falcon.hsi.localizerCourse", "", "HSI localizer course", 1),
        (208, K.F32, "falcon.hsi.airbaseX", "", "HSI airbase X", 1), (212, K.F32, "falcon.hsi.airbaseY", "", "HSI airbase Y", 1),
        (216, K.F32, "falcon.hsi.totalValues", "", "HSI total values", 1),
        (220, K.F32, "falcon.trim.pitch", "", "Pitch trim −0.5..0.5", 1), (224, K.F32, "falcon.trim.roll", "", "Roll trim −0.5..0.5", 1),
        (228, K.F32, "falcon.trim.yaw", "", "Yaw trim −0.5..0.5", 1), (232, K.U32, "falcon.hsiBits", "", "HSI flags bitfield", 1),
        (756, K.I32, "falcon.tacan.ufcChannel", "", "UFC TACAN channel", 1), (760, K.I32, "falcon.tacan.auxChannel", "", "AUX TACAN channel", 1),
        (764, K.I32, "falcon.rwr.count", "", "RWR contacts", 1),
        (1888, K.F32, "falcon.fwd", "", "fwd (as sent)", 1), (1892, K.F32, "falcon.aft", "", "aft (as sent)", 1),
        (1896, K.F32, "falcon.total", "", "total (as sent)", 1),
        (1900, K.I32, "falcon.version", "", "FlightData version", 1), (1904, K.I32, "falcon.version2", "", "Memory area version", 1),
        (1908, K.F32, "falcon.head.x", "m", "Head X from design eye", Ft), (1912, K.F32, "falcon.head.y", "m", "Head Y from design eye", Ft),
        (1916, K.F32, "falcon.head.z", "m", "Head Z from design eye", Ft),
        (1920, K.I32, "falcon.mainPower", "", "Main power switch 0 down, 1 middle, 2 up", 1),
        // FlightData2
        (Fd2 + 0, K.F32, "falcon.nozzlePos2", "", "Engine 2 nozzle open 0..1", 0.01), (Fd2 + 4, K.F32, "falcon.rpm2", "", "Engine 2 rpm fraction", 0.01),
        (Fd2 + 8, K.F32, "falcon.ftit2", "degC", "Engine 2 FTIT", 1), (Fd2 + 12, K.F32, "falcon.oilPressure2", "", "Engine 2 oil pressure 0..1", 0.01),
        (Fd2 + 16, K.U8, "falcon.navMode", "", "HSI navigation mode", 1), (Fd2 + 20, K.F32, "falcon.aauz", "m", "AAU barometric altitude", Ft),
        (Fd2 + 24, K.U8, "falcon.tacanInfo.ufc", "", "UFC TACAN band/mode bits", 1), (Fd2 + 25, K.U8, "falcon.tacanInfo.aux", "", "AUX TACAN band/mode bits", 1),
        (Fd2 + 28, K.I32, "falcon.altCalReading", "", "Altimeter calibration reading", 1), (Fd2 + 32, K.U32, "falcon.altBits", "", "Altimeter bitfield", 1),
        (Fd2 + 36, K.U32, "falcon.powerBits", "", "Power bus bitfield", 1), (Fd2 + 40, K.U32, "falcon.blinkBits", "", "Blink bitfield", 1),
        (Fd2 + 44, K.I32, "falcon.cmdsMode", "", "CMDS mode", 1), (Fd2 + 48, K.I32, "falcon.bupUhfPreset", "", "Backup UHF preset", 1),
        (Fd2 + 52, K.I32, "falcon.bupUhfFreq", "", "Backup UHF frequency (as sent)", 1),
        (Fd2 + 56, K.F32, "falcon.cabinAlt", "m", "Cabin altitude", Ft), (Fd2 + 60, K.F32, "falcon.hydPressureA", "", "Hydraulic pressure A (as sent)", 1),
        (Fd2 + 64, K.F32, "falcon.hydPressureB", "", "Hydraulic pressure B (as sent)", 1), (Fd2 + 68, K.U32, "falcon.time", "s", "Time of day", 1),
        (Fd2 + 72, K.I16, "falcon.vehicleAcd", "", "Aircraft type index", 1), (Fd2 + 76, K.I32, "falcon.fd2Version", "", "FlightData2 version", 1),
        (Fd2 + 80, K.F32, "falcon.fuelFlow2", "kg/s", "Engine 2 fuel flow", Lb / 3600),
        (Fd2 + 596, K.F32, "falcon.lefPos", "", "Leading-edge flap position", 1), (Fd2 + 600, K.F32, "falcon.tefPos", "", "Trailing-edge flap position", 1),
        (Fd2 + 604, K.F32, "falcon.vtolPos", "", "VTOL exhaust angle (as sent)", 1), (Fd2 + 608, K.U8, "falcon.pilotsOnline", "", "Pilots in the MP session", 1),
        (Fd2 + 1028, K.F32, "falcon.bumpIntensity", "", "Taxi bump intensity 0..1", 1),
        (Fd2 + 1032, K.F32, "position.lat", "rad", "Latitude (avionics)", Deg), (Fd2 + 1036, K.F32, "position.lon", "rad", "Longitude (avionics)", Deg),
        (Fd2 + 1100, K.U8, "falcon.iff.mode1Digit1", "", "IFF backup mode 1 digit 1", 1), (Fd2 + 1101, K.U8, "falcon.iff.mode1Digit2", "", "IFF backup mode 1 digit 2", 1),
        (Fd2 + 1102, K.U8, "falcon.iff.mode3ADigit1", "", "IFF backup mode 3A digit 1", 1), (Fd2 + 1103, K.U8, "falcon.iff.mode3ADigit2", "", "IFF backup mode 3A digit 2", 1),
        (Fd2 + 1104, K.U8, "falcon.instrLight", "", "Instrument backlight setting", 1), (Fd2 + 1108, K.U32, "falcon.bettyBits", "", "Voice warning bitfield", 1),
        (Fd2 + 1112, K.U32, "falcon.miscBits", "", "Misc bitfield", 1), (Fd2 + 1116, K.F32, "env.heightFromGround", "m", "Radar altitude", Ft),
        (Fd2 + 1120, K.F32, "falcon.bingoFuel", "kg", "Bingo fuel", Lb), (Fd2 + 1124, K.F32, "falcon.caraAlow", "m", "CARA ALOW setting", Ft),
        (Fd2 + 1128, K.F32, "falcon.bullseye.north", "m", "Bullseye north", Ft), (Fd2 + 1132, K.F32, "falcon.bullseye.east", "m", "Bullseye east", Ft),
        (Fd2 + 1136, K.I32, "falcon.bms.major", "", "BMS version major", 1), (Fd2 + 1140, K.I32, "falcon.bms.minor", "", "BMS version minor", 1),
        (Fd2 + 1144, K.I32, "falcon.bms.micro", "", "BMS version micro", 1), (Fd2 + 1148, K.I32, "falcon.bms.build", "", "BMS build number", 1),
        (Fd2 + 1152, K.U32, "falcon.stringAreaSize", "", "String area size", 1), (Fd2 + 1156, K.U32, "falcon.stringAreaTime", "", "String area change stamp", 1),
        (Fd2 + 1160, K.U32, "falcon.drawingAreaSize", "", "Drawing area size", 1), (Fd2 + 1164, K.F32, "falcon.turnRate", "rad/s", "Turn rate", Deg),
        (Fd2 + 1168, K.U8, "falcon.floodConsole", "", "Flood console brightness setting", 1),
        (Fd2 + 1172, K.F32, "falcon.magDeviationSystem", "", "Magnetic deviation, system", 1),
        (Fd2 + 1176, K.F32, "falcon.magDeviationReal", "", "Magnetic deviation, real", 1),
        (Fd2 + 1180, K.U32, "falcon.ecm1", "", "ECM program 1 state", 1), (Fd2 + 1184, K.U32, "falcon.ecm2", "", "ECM program 2 state", 1),
        (Fd2 + 1188, K.U32, "falcon.ecm3", "", "ECM program 3 state", 1), (Fd2 + 1192, K.U32, "falcon.ecm4", "", "ECM program 4 state", 1),
        (Fd2 + 1196, K.U32, "falcon.ecm5", "", "ECM program 5 state", 1), (Fd2 + 1200, K.U8, "falcon.ecmOper", "", "ECM operating state", 1),
        (Fd2 + 1244, K.I32, "falcon.radio2.preset", "", "Radio 2 preset", 1), (Fd2 + 1248, K.I32, "falcon.radio2.frequency", "", "Radio 2 frequency (as sent)", 1),
        (Fd2 + 1252, K.I8, "falcon.iff.code1", "", "IFF transponder mode 1", 1), (Fd2 + 1254, K.I16, "falcon.iff.code2", "", "IFF transponder mode 2", 1),
        (Fd2 + 1256, K.I16, "falcon.iff.code3A", "", "IFF transponder mode 3A", 1), (Fd2 + 1258, K.I16, "falcon.iff.codeC", "", "IFF transponder mode C", 1),
        (Fd2 + 1260, K.I16, "falcon.iff.code4", "", "IFF transponder mode 4", 1),
        // IntelliVibe
        (Iv + 0, K.U8, "falcon.iv.aaMissilesFired", "", "AA missiles fired", 1), (Iv + 1, K.U8, "falcon.iv.agMissilesFired", "", "AG missiles / rockets fired", 1),
        (Iv + 2, K.U8, "falcon.iv.bombsDropped", "", "Bombs dropped", 1), (Iv + 3, K.U8, "falcon.iv.flaresDropped", "", "Flares dropped", 1),
        (Iv + 4, K.U8, "falcon.iv.chaffDropped", "", "Chaff dropped", 1), (Iv + 5, K.U8, "falcon.iv.bulletsFired", "", "Bullets fired", 1),
        (Iv + 8, K.I32, "falcon.iv.collisions", "", "Collision counter", 1),
        (Iv + 12, K.Bool8, "weapon.fire", "", "Gun firing", 1), (Iv + 13, K.Bool8, "falcon.iv.endFlight", "", "Ending the flight", 1),
        (Iv + 14, K.Bool8, "falcon.iv.ejecting", "", "Ejected", 1), (Iv + 15, K.Bool8, "falcon.iv.in3D", "", "In 3D", 1),
        (Iv + 17, K.Bool8, "falcon.iv.frozen", "", "Sim frozen", 1), (Iv + 18, K.Bool8, "falcon.iv.overG", "", "G limit exceeded", 1),
        (Iv + 19, K.Bool8, "falcon.iv.onGround", "", "On the ground", 1), (Iv + 20, K.Bool8, "falcon.iv.exitGame", "", "Exited Falcon", 1),
        (Iv + 24, K.F32, "falcon.iv.gforce", "", "G force felt", 1),
        (Iv + 28, K.F32, "falcon.iv.eye.x", "", "Eye position X (as sent)", 1), (Iv + 32, K.F32, "falcon.iv.eye.y", "", "Eye position Y (as sent)", 1),
        (Iv + 36, K.F32, "falcon.iv.eye.z", "", "Eye position Z (as sent)", 1), (Iv + 40, K.I32, "falcon.iv.lastDamage", "", "Last damage direction 1..8", 1),
        (Iv + 44, K.F32, "falcon.iv.damageForce", "", "Damage force", 1), (Iv + 48, K.I32, "falcon.iv.whenDamage", "", "Damage time (raw)", 1),
    };

    static readonly (int Off, int Length, string Name, string Description)[] Texts = BuildTexts();

    static (int, int, string, string)[] BuildTexts()
    {
        var t = new List<(int, int, string, string)>();
        string[] groups = { "dedLine", "dedInvert", "pflLine", "pflInvert" };
        for (int g = 0; g < 4; g++)
            for (int i = 0; i < 5; i++)
                t.Add((236 + g * 130 + i * 26, 26, $"falcon.{groups[g]}{i + 1}", $"{groups[g]} {i + 1} (raw characters)"));
        for (int p = 0; p < 32; p++)
            t.Add((Fd2 + 609 + p * 12, 12, $"falcon.pilot{p + 1}.callsign", $"MP pilot {p + 1} callsign"));
        return t.ToArray();
    }

    readonly OgsChannel[] _field = new OgsChannel[Fields.Length];
    readonly OgsChannel[] _text = new OgsChannel[Texts.Length];
    readonly OgsChannel[] _pilotStatus = new OgsChannel[32];
    readonly OgsChannel[] _rwrSymbol = new OgsChannel[RwrObjects], _rwrBearing = new OgsChannel[RwrObjects],
        _rwrLethality = new OgsChannel[RwrObjects], _rwrFlags = new OgsChannel[RwrObjects];
    OgsChannel _paused;
    readonly byte[] _last = new byte[SnapshotSize];
    bool _haveLast;
    Vector3 _prevVelocity;
    double _prevTime = double.NaN;

    public string Id => "falconbms";
    public string Title => "Falcon BMS (shared memory)";
    public string Description =>
        "Falcon BMS 4.x shared memory (FalconSharedMemoryArea, …Area2, " +
        "IntelliVibe), 2 ms poll; published whenever BMS runs.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "Falcon BMS" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Falcon BMS 4", "Falcon BMS"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = Array.Empty<(string, string)>();

    public void Configure(IReadOnlyDictionary<string, string> options) =>
        new SourceOptions(options).EnsureKnown();

    public SourceTransport Transport => SourceTransport.SharedMemory;
    public string SharedMemoryName => "FalconSharedMemoryArea";
    public int SharedMemorySize => SnapshotSize;
    public int PollIntervalMs => 2;
    public IReadOnlyList<string> AuxiliaryMapNames { get; } =
        new[] { "FalconSharedMemoryArea2", "FalconIntellivibeSharedMemoryArea" };

    public int Gather(MemoryMappedViewAccessor primary, IReadOnlyList<MemoryMappedViewAccessor?> auxiliary, byte[] buffer)
    {
        primary.ReadArray(0, buffer, 0, PrimarySize);
        byte present = 0;
        if (auxiliary[0] is { } fd2) { fd2.ReadArray(0, buffer, Fd2, Fd2Size); present |= 1; }
        else Array.Clear(buffer, Fd2, Fd2Size);
        if (auxiliary[1] is { } iv) { iv.ReadArray(0, buffer, Iv, IvSize); present |= 2; }
        else Array.Clear(buffer, Iv, IvSize);
        buffer[Present] = present;
        // no change counter: an identical snapshot is nothing new
        if (_haveLast && buffer.AsSpan(0, SnapshotSize).SequenceEqual(_last)) return 0;
        buffer.AsSpan(0, SnapshotSize).CopyTo(_last);
        _haveLast = true;
        return SnapshotSize;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("aircraft", Ogs.Telemetry.Schema.ObjectType.Aircraft, "Falcon BMS aircraft");
        session.SendChangedChannelsOnly();
        _paused = session.AddBooleanChannel("session.paused", "Sim paused or frozen");
        session.AlwaysSend(_paused);
        for (int i = 0; i < Fields.Length; i++)
        {
            var f = Fields[i];
            _field[i] = f.Kind == K.Bool8
                ? session.AddBooleanChannel(f.Name, f.Description)
                : session.AddChannel(f.Name, f.Unit, f.Description);
        }
        for (int i = 0; i < Texts.Length; i++) _text[i] = session.AddSessionText(Texts[i].Name, Texts[i].Description);
        for (int p = 0; p < 32; p++)
            _pilotStatus[p] = session.AddChannel($"falcon.pilot{p + 1}.status", "", $"MP pilot {p + 1} fly state");
        for (int r = 0; r < RwrObjects; r++)
        {
            _rwrSymbol[r]    = session.AddChannel($"falcon.rwr{r + 1}.symbol", "", $"RWR contact {r + 1} symbol");
            _rwrBearing[r]   = session.AddChannel($"falcon.rwr{r + 1}.bearing", "rad", $"RWR contact {r + 1} bearing");
            _rwrLethality[r] = session.AddChannel($"falcon.rwr{r + 1}.lethality", "", $"RWR contact {r + 1} lethality");
            _rwrFlags[r]     = session.AddChannel($"falcon.rwr{r + 1}.flags", "",
                $"RWR contact {r + 1}: 1 missile activity, 2 launch, 4 selected, 8 new detection, bits 4+ jamming state");
        }
    }

    public bool AcceptsDatagram(int length) => length >= SnapshotSize;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static uint U(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<uint>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (d.Length < SnapshotSize) return;
        if ((d[Present] & 2) != 0)
        {
            bool in3D = d[Iv + 15] != 0, endFlight = d[Iv + 13] != 0, exit = d[Iv + 20] != 0;
            if (!in3D || endFlight || exit) { _prevTime = double.NaN; return; }
            session.SetChannel(_paused, d[Iv + 16] != 0 || d[Iv + 17] != 0);
        }

        bool fd2 = (d[Present] & 1) != 0, iv = (d[Present] & 2) != 0;
        for (int i = 0; i < Fields.Length; i++)
        {
            var f = Fields[i];
            if (f.Off >= Iv ? !iv : f.Off >= Fd2 && !fd2) continue;
            switch (f.Kind)
            {
                case K.F32: session.SetChannel(_field[i], F(d, f.Off) * f.Scale); break;
                case K.I32: session.SetChannel(_field[i], MemoryMarshal.Read<int>(d.Slice(f.Off, 4)) * f.Scale); break;
                case K.U32: session.SetChannel(_field[i], U(d, f.Off) * f.Scale); break;
                case K.U8: session.SetChannel(_field[i], d[f.Off] * f.Scale); break;
                case K.I8: session.SetChannel(_field[i], (sbyte)d[f.Off] * f.Scale); break;
                case K.I16: session.SetChannel(_field[i], MemoryMarshal.Read<short>(d.Slice(f.Off, 2)) * f.Scale); break;
                case K.Bool8: session.SetChannel(_field[i], d[f.Off] != 0); break;
            }
        }
        for (int i = 0; i < Texts.Length; i++)
        {
            var t = Texts[i];
            if (t.Off >= Fd2 && !fd2) continue;
            var s = d.Slice(t.Off, t.Length);
            int nul = s.IndexOf((byte)0);
            session.SetSessionText(_text[i], nul < 0 ? s : s[..nul]);
        }
        if (fd2)
            for (int p = 0; p < 32; p++) session.SetChannel(_pilotStatus[p], d[Fd2 + 993 + p]);

        int contacts = Math.Clamp(MemoryMarshal.Read<int>(d.Slice(764, 4)), 0, RwrObjects);
        for (int r = 0; r < contacts; r++)
        {
            session.SetChannel(_rwrSymbol[r], MemoryMarshal.Read<int>(d.Slice(768 + r * 4, 4)));
            session.SetChannel(_rwrBearing[r], F(d, 928 + r * 4));
            session.SetChannel(_rwrLethality[r], F(d, 1568 + r * 4));
            uint flags = (U(d, 1088 + r * 4) != 0 ? 1u : 0) | (U(d, 1248 + r * 4) != 0 ? 2u : 0)
                       | (U(d, 1408 + r * 4) != 0 ? 4u : 0) | (U(d, 1728 + r * 4) != 0 ? 8u : 0);
            if (fd2) flags |= (uint)d[Fd2 + 1201 + r] << 4;
            session.SetChannel(_rwrFlags[r], flags);
        }

        // pose: aerospace yaw/pitch/roll; NED → OGS world (east, up, north)
        session.SetPose(new Vector3(F(d, 4), -F(d, 8), F(d, 0)) * Ft,
            OgsMath.EulerToQuaternion(F(d, 44), F(d, 36), F(d, 40)));
        var velocity = new Vector3(F(d, 16), -F(d, 20), F(d, 12)) * Ft;
        double dt = now - _prevTime;
        var accel = double.IsNaN(dt) || dt <= 0 || dt > 1 ? Vector3.Zero : (velocity - _prevVelocity) / (float)dt;
        _prevVelocity = velocity;
        _prevTime = now;
        session.SetKinematics(velocity, accel);

        session.Tick(now);
    }
}
}
