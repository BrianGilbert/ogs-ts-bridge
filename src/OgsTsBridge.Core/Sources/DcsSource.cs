#nullable enable
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace OgsTsBridge.Sources
{
/// <summary>
/// DCS World through an export script the Bridge installs
/// (`ogs-ts-bridge patch --source dcs`, or the GUI's patch button): it
/// writes Saved Games\DCS*\Scripts\OgsTsBridgeExport.lua and chains it
/// from Export.lua with one dofile line, so other tools' exports keep
/// working (the script calls any previously defined export callbacks).
///
/// Each simulation frame the script sends one ASCII datagram,
///   OGS2;t;heading;pitch;bank;ωx;ωy;ωz;ax;ay;az;px;py;pz;&lt;field table&gt;
/// — model time, LoGetSelfData attitude (rad), LoGetAngularVelocity
/// (rad/s), LoGetAccelerationUnits (g, gravity included), LoGetSelfData
/// world position (m), then every value of <see cref="Fields"/> in order:
/// air data, navigation and HSI, atmosphere, world velocity and wind,
/// engines, mechanisation (gear, flaps, brakes, control surfaces, strut
/// rods), countermeasures, payload and the MCP warning/failure lamps.
/// A value the sim does not provide (nil — module or multiplayer export
/// limits) is sent empty and leaves its channel unset. Once a second it
/// also sends `OGSI;pilot;aircraft;coalition` for the session text.
/// The script and the parser are generated from the same table.
///
/// Axes per the DCS export API: body right-handed X-forward / Y-up /
/// Z-right → OGS body (z, y, x); rates (ωz, −ωy, ωx); world X-north /
/// Y-up / Z-east → OGS (z, y, x); heading + clockwise, pitch + up, bank +
/// right-wing-down mapped direct (unverified against the live sim — flip
/// options exist). The g-units are accelerometer-style, so they scale
/// straight to specific force.
/// </summary>
public sealed class DcsSource : ILegacySource, IGamePatcher
{
    const float Gravity = 9.80665f;
    internal const int CoreCount = 13;
    const double MmHg = 133.322;
    const double Deg = Math.PI / 180.0;

    /// <summary>One exported value: Lua expression (over the script's
    /// locals — see <see cref="ExportScript"/>) → channel.</summary>
    internal readonly record struct DcsField(string Name, string Unit, string Description, string Lua,
                                             double Scale = 1, bool Boolean = false);

    internal static readonly DcsField[] Fields = BuildFields();

    readonly OgsChannel[] _channels = new OgsChannel[Fields.Length];
    readonly double[] _core = new double[CoreCount];
    OgsChannel _pilot, _aircraft, _coalition;
    double _sy = 1, _sp = 1, _sr = 1;

    public string Id => "dcs";
    public string Title => "DCS World (export script)";
    public string Description =>
        "DCS World Export.lua telemetry (UDP port 42424). Run the patch " +
        "with DCS closed to install the export script into Saved Games.";
    public int DefaultPort => 42424;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "DCS" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("DCS World", "DCS"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate heading + yaw-rate sign (bool, default false)"),
        ("flipPitch", "negate pitch + pitch-rate sign (bool, default false)"),
        ("flipRoll",  "negate bank + roll-rate sign (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("aircraft", Ogs.Telemetry.Schema.ObjectType.Aircraft, "DCS aircraft");
        session.DeclareGravity(Gravity);
        session.SendChangedChannelsOnly();
        for (int i = 0; i < Fields.Length; i++)
        {
            var f = Fields[i];
            _channels[i] = f.Boolean
                ? session.AddBooleanChannel(f.Name, f.Description)
                : session.AddChannel(f.Name, f.Unit, f.Description);
        }
        _pilot     = session.AddSessionText("driver.name", "Pilot name");
        _aircraft  = session.AddSessionText("vehicle.name", "Aircraft type");
        _coalition = session.AddSessionText("dcs.coalition", "Coalition");
    }

    public bool AcceptsDatagram(int length) => length >= 5;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (d.StartsWith("OGSI;"u8)) { ReadInfo(d.Slice(5), session); return; }
        if (!d.StartsWith("OGS2;"u8)) return;
        var rest = d.Slice(5);

        // core: every field required
        for (int i = 0; i < CoreCount; i++)
        {
            if (!NextField(ref rest, out var field) ||
                !Utf8Parser.TryParse(field, out double value, out int used) || used != field.Length)
                return; // malformed — drop
            _core[i] = value;
        }

        var q = OgsMath.EulerToQuaternion(_sy * _core[1], _sp * _core[2], _sr * _core[3]);
        // DCS world (X north, Y up, Z east) -> OGS (x east, y up, z north)
        session.SetPose(new Vector3((float)_core[12], (float)_core[11], (float)_core[10]), q);
        // DCS body (X fwd, Y up, Z right) -> OGS body (x right, y up, z fwd)
        session.SetBodyDynamics(
            new Vector3((float)_core[9], (float)_core[8], (float)_core[7]) * Gravity,
            new Vector3((float)(_sp * _core[6]), (float)(_sy * -_core[5]), (float)(_sr * _core[4])));

        // table: empty = not provided by the sim this frame
        for (int i = 0; i < Fields.Length; i++)
        {
            if (!NextField(ref rest, out var field)) break;
            if (field.IsEmpty || !Utf8Parser.TryParse(field, out double value, out int used) ||
                used != field.Length) continue;
            if (Fields[i].Boolean) session.SetChannel(_channels[i], value != 0);
            else session.SetChannel(_channels[i], value * Fields[i].Scale);
        }

        session.Tick(now);
    }

    void ReadInfo(ReadOnlySpan<byte> rest, OgsSession session)
    {
        if (NextField(ref rest, out var pilot)) session.SetSessionText(_pilot, pilot);
        if (NextField(ref rest, out var aircraft))
        {
            session.SetSessionText(_aircraft, aircraft);
            session.SetTypeLabel(aircraft);
        }
        if (NextField(ref rest, out var coalition)) session.SetSessionText(_coalition, coalition);
    }

    static bool NextField(ref ReadOnlySpan<byte> rest, out ReadOnlySpan<byte> field)
    {
        if (rest.IsEmpty) { field = default; return false; }
        int sep = rest.IndexOfAny((byte)';', (byte)'\n');
        if (sep < 0) { field = rest; rest = default; return true; }
        field = rest.Slice(0, sep);
        rest = rest[sep] == (byte)'\n' ? default : rest.Slice(sep + 1);
        return true;
    }

    static DcsField[] BuildFields()
    {
        var t = new List<DcsField>
        {
            new("air.indicatedAirspeed", "m/s", "Indicated airspeed", "c(LoGetIndicatedAirSpeed)"),
            new("air.trueAirspeed", "m/s", "True airspeed", "c(LoGetTrueAirSpeed)"),
            new("air.verticalSpeed", "m/s", "Vertical speed", "c(LoGetVerticalVelocity)"),
            new("position.altMsl", "m", "Altitude above sea level", "c(LoGetAltitudeAboveSeaLevel)"),
            new("env.heightFromGround", "m", "Altitude above ground", "c(LoGetAltitudeAboveGroundLevel)"),
            new("nav.radarAltitude", "m", "Radar altimeter", "c(LoGetRadarAltimeter)"),
            new("air.aoa", "rad", "Angle of attack (unit assumed rad)", "c(LoGetAngleOfAttack)"),
            new("air.mach", "", "Mach number", "c(LoGetMachNumber)"),
            new("air.loadFactor", "", "Normal load factor (g)", "g(acc,'y')"),
            new("nav.headingMagnetic", "rad", "Magnetic heading", "c(LoGetMagneticYaw)"),
            new("nav.adi.pitch", "rad", "ADI pitch", "adiP"),
            new("nav.adi.bank", "rad", "ADI bank", "adiB"),
            new("nav.adi.yaw", "rad", "ADI yaw", "adiY"),
            new("nav.gs", "", "Glide slope deviation -1..1", "c(LoGetGlideDeviation)"),
            new("nav.locDeflection", "", "Localizer deviation -1..1", "c(LoGetSideDeviation)"),
            new("air.turnBall", "", "Slip ball position -1..1", "c(LoGetSlipBallPosition)"),
            new("env.pressure", "Pa", "Basic atmosphere pressure", "c(LoGetBasicAtmospherePressure)", MmHg),
            new("position.lat", "rad", "Latitude", "g(self,'LatLongAlt','Lat')", Deg),
            new("position.lon", "rad", "Longitude", "g(self,'LatLongAlt','Long')", Deg),
            new("velocity.world.x", "m/s", "World velocity, OGS x (east)", "g(vel,'z')"),
            new("velocity.world.y", "m/s", "World velocity, OGS y (up)", "g(vel,'y')"),
            new("velocity.world.z", "m/s", "World velocity, OGS z (north)", "g(vel,'x')"),
            new("wind.x", "m/s", "Wind, OGS world x (east)", "g(wind,'z')"),
            new("wind.y", "m/s", "Wind, OGS world y (up)", "g(wind,'y')"),
            new("wind.z", "m/s", "Wind, OGS world z (north)", "g(wind,'x')"),
            new("nav.hsi.adf", "rad", "HSI ADF needle", "g(hsi,'ADF_raw')"),
            new("nav.hsi.rmi", "rad", "HSI RMI needle", "g(hsi,'RMI_raw')"),
            new("nav.hsi.heading", "rad", "HSI heading", "g(hsi,'Heading_raw')"),
            new("nav.hsi.headingPointer", "rad", "HSI heading bug", "g(hsi,'HeadingPointer')"),
            new("nav.hsi.course", "rad", "HSI selected course", "g(hsi,'Course')"),
            new("nav.hsi.bearingPointer", "rad", "HSI bearing pointer", "g(hsi,'BearingPointer')"),
            new("nav.hsi.courseDeviation", "rad", "HSI course deviation", "g(hsi,'CourseDeviation')"),
            new("session.missionStartTime", "s", "Mission start time", "c(LoGetMissionStartTime)"),

            // engines
            new("dcs.engine.rpmLeft", "%", "Left engine rpm, percent", "g(eng,'RPM','left')"),
            new("dcs.engine.rpmRight", "%", "Right engine rpm, percent", "g(eng,'RPM','right')"),
            new("dcs.engine.tempLeft", "degC", "Left engine temperature", "g(eng,'Temperature','left')"),
            new("dcs.engine.tempRight", "degC", "Right engine temperature", "g(eng,'Temperature','right')"),
            new("dcs.engine.hydraulicLeft", "", "Left hydraulic pressure (game units)", "g(eng,'HydraulicPressure','left')"),
            new("dcs.engine.hydraulicRight", "", "Right hydraulic pressure (game units)", "g(eng,'HydraulicPressure','right')"),
            new("dcs.engine.fuelFlowLeft", "", "Left fuel consumption (game units)", "g(eng,'FuelConsumption','left')"),
            new("dcs.engine.fuelFlowRight", "", "Right fuel consumption (game units)", "g(eng,'FuelConsumption','right')"),
            new("dcs.fuel.internal", "", "Internal fuel (game units)", "g(eng,'fuel_internal')"),
            new("dcs.fuel.external", "", "External fuel (game units)", "g(eng,'fuel_external')"),

            // mechanisation
            new("gear.position", "", "Landing gear 0..1", "g(mech,'gear','value')"),
            new("dcs.gear.status", "", "Landing gear status", "g(mech,'gear','status')"),
            new("dcs.gear.rodLeft", "", "Left main strut rod", "g(mech,'gear','main','left','rod')"),
            new("dcs.gear.rodRight", "", "Right main strut rod", "g(mech,'gear','main','right','rod')"),
            new("dcs.gear.rodNose", "", "Nose strut rod", "g(mech,'gear','nose','rod')"),
            new("ctrl.flaps", "", "Flaps 0..1", "g(mech,'flaps','value')"),
            new("ctrl.spoilers", "", "Speed brakes 0..1", "g(mech,'speedbrakes','value')"),
            new("dcs.refuelingBoom", "", "Refuelling probe 0..1", "g(mech,'refuelingboom','value')"),
            new("dcs.airIntake", "", "Air intake 0..1", "g(mech,'airintake','value')"),
            new("dcs.noseFlap", "", "Nose flap 0..1", "g(mech,'noseflap','value')"),
            new("dcs.parachute", "", "Brake parachute 0..1", "g(mech,'parachute','value')"),
            new("brakes.pedal", "", "Wheel brakes 0..1", "g(mech,'wheelbrakes','value')"),
            new("dcs.hook", "", "Arrestor hook 0..1", "g(mech,'hook','value')"),
            new("dcs.wing", "", "Wing fold/sweep 0..1", "g(mech,'wing','value')"),
            new("dcs.canopy", "", "Canopy 0..1", "g(mech,'canopy','value')"),
            new("ctrl.aileronLeft", "", "Left aileron deflection", "g(mech,'controlsurfaces','eleron','left')"),
            new("ctrl.aileronRight", "", "Right aileron deflection", "g(mech,'controlsurfaces','eleron','right')"),
            new("ctrl.elevatorLeft", "", "Left elevator deflection", "g(mech,'controlsurfaces','elevator','left')"),
            new("ctrl.elevatorRight", "", "Right elevator deflection", "g(mech,'controlsurfaces','elevator','right')"),
            new("ctrl.rudderLeft", "", "Left rudder deflection", "g(mech,'controlsurfaces','rudder','left')"),
            new("ctrl.rudderRight", "", "Right rudder deflection", "g(mech,'controlsurfaces','rudder','right')"),

            // countermeasures and payload
            new("weapon.chaff", "", "Chaff remaining", "g(snares,'chaff')"),
            new("weapon.flares", "", "Flares remaining", "g(snares,'flare')"),
            new("weapon.cannonShells", "", "Cannon shells remaining", "g(payload,'Cannon','shells')"),
            new("weapon.currentStation", "", "Selected station", "g(payload,'CurrentStation')"),
        };

        // MCP warning / failure lamps
        foreach (var lamp in new[]
        {
            "LeftEngineFailure", "RightEngineFailure", "HydraulicsFailure", "ACSFailure",
            "AutopilotFailure", "AutopilotOn", "MasterWarning", "LeftTailPlaneFailure",
            "RightTailPlaneFailure", "LeftAileronFailure", "RightAileronFailure", "CanopyOpen",
            "CannonFailure", "StallSignalization", "LeftMainPumpFailure", "RightMainPumpFailure",
            "LeftWingPumpFailure", "RightWingPumpFailure", "RadarFailure", "EOSFailure",
            "MLWSFailure", "RWSFailure", "ECMFailure", "GearFailure", "MFDFailure", "HUDFailure",
            "HelmetFailure", "FuelTankDamage",
        })
            t.Add(new($"warning.{char.ToLowerInvariant(lamp[0])}{lamp.Substring(1)}", "",
                      $"MCP lamp: {lamp}", $"g(mcp,'{lamp}')", Boolean: true));

        return t.ToArray();
    }

    // ---- IGamePatcher: export script + Export.lua hook ----

    internal const string ScriptName = "OgsTsBridgeExport.lua";
    const string HookLine =
        "local ogsTsBridgeLfs = require('lfs'); dofile(ogsTsBridgeLfs.writedir() .. 'Scripts/" + ScriptName + "')";

    public IList<string> PatchGameTelemetry(int port) =>
        PatchSavedGames(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"), port);

    /// <summary>Installs the export script into every existing DCS
    /// Saved Games folder (DCS, DCS.openbeta, …) and appends the dofile
    /// hook to its Export.lua (created if absent; existing content kept,
    /// one-time .bak-ogs-ts-bridge backup). Idempotent; re-running
    /// refreshes the script.</summary>
    public static IList<string> PatchSavedGames(string savedGamesRoot, int port)
    {
        var patched = new List<string>();
        if (!System.IO.Directory.Exists(savedGamesRoot)) return patched;
        foreach (var dir in System.IO.Directory.GetDirectories(savedGamesRoot, "DCS*"))
        {
            var scripts = System.IO.Path.Combine(dir, "Scripts");
            System.IO.Directory.CreateDirectory(scripts);

            var script = System.IO.Path.Combine(scripts, ScriptName);
            System.IO.File.WriteAllText(script, ExportScript(port));
            patched.Add(script);

            var export = System.IO.Path.Combine(scripts, "Export.lua");
            var text = System.IO.File.Exists(export) ? System.IO.File.ReadAllText(export) : "";
            if (text.Contains(ScriptName, StringComparison.Ordinal)) continue;
            if (text.Length > 0)
            {
                var backup = export + ".bak-ogs-ts-bridge";
                if (!System.IO.File.Exists(backup)) System.IO.File.Copy(export, backup);
            }
            var newline = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
            System.IO.File.WriteAllText(export, text + newline + HookLine + "\n");
            patched.Add(export);
        }
        return patched;
    }

    internal static string ExportScript(int port)
    {
        var values = new StringBuilder();
        foreach (var f in Fields) values.Append("        n(").Append(f.Lua).Append("),\n");

        return $$"""
            -- OGS-TS Bridge export: one datagram per frame to 127.0.0.1:{{port}}.
            -- Installed by ogs-ts-bridge; chains any export callbacks defined before it.
            local ogs = { host = "127.0.0.1", port = {{port}}, nextInfo = 0 }

            local prevStart = LuaExportStart
            local prevAfter = LuaExportAfterNextFrame
            local prevStop = LuaExportStop

            -- call an export function if this sim/module provides it
            local function c(f, ...)
                if type(f) ~= "function" then return nil end
                local ok, r = pcall(f, ...)
                if ok then return r end
                return nil
            end
            -- nested table lookup, nil-safe
            local function g(t, ...)
                for _, k in ipairs({ ... }) do
                    if type(t) ~= "table" then return nil end
                    t = t[k]
                end
                return t
            end
            -- value -> field text; nil sends an empty field
            local function n(v)
                if v == nil then return "" end
                if v == true then return "1" end
                if v == false then return "0" end
                if type(v) ~= "number" then return "" end
                return string.format("%.7g", v)
            end
            local function s(v)
                if v == nil then return "" end
                return (tostring(v):gsub("[;\n]", " "))
            end

            function LuaExportStart()
                if prevStart then prevStart() end
                package.path = package.path .. ";.\\LuaSocket\\?.lua"
                package.cpath = package.cpath .. ";.\\LuaSocket\\?.dll"
                local socket = require("socket")
                ogs.udp = socket.udp()
                ogs.udp:settimeout(0)
                ogs.udp:setpeername(ogs.host, ogs.port)
            end

            function LuaExportAfterNextFrame()
                if prevAfter then prevAfter() end
                if not ogs.udp then return end
                local self = c(LoGetSelfData)
                if not self then return end
                local t = c(LoGetModelTime) or 0
                local w = c(LoGetAngularVelocity) or {}
                local acc = c(LoGetAccelerationUnits) or {}
                local pos = self.Position or {}
                local vel = c(LoGetVectorVelocity)
                local wind = c(LoGetVectorWindVelocity)
                local hsi = c(LoGetControlPanel_HSI)
                local eng = c(LoGetEngineInfo)
                local mech = c(LoGetMechInfo)
                local snares = c(LoGetSnares)
                local payload = c(LoGetPayloadInfo)
                local mcp = c(LoGetMCPState)
                local adiP, adiB, adiY = nil, nil, nil
                if type(LoGetADIPitchBankYaw) == "function" then
                    local ok, p, b, y = pcall(LoGetADIPitchBankYaw)
                    if ok then adiP, adiB, adiY = p, b, y end
                end
                local fields = {
                    "OGS2", n(t), n(self.Heading or 0), n(self.Pitch or 0), n(self.Bank or 0),
                    n(w.x or 0), n(w.y or 0), n(w.z or 0),
                    n(acc.x or 0), n(acc.y or 1), n(acc.z or 0),
                    n(pos.x or 0), n(pos.y or 0), n(pos.z or 0),
            {{values}}        }
                ogs.udp:send(table.concat(fields, ";") .. "\n")
                if t >= ogs.nextInfo then
                    ogs.nextInfo = t + 1
                    ogs.udp:send("OGSI;" .. s(c(LoGetPilotName)) .. ";" .. s(self.Name) .. ";" .. s(self.Coalition) .. "\n")
                end
            end

            function LuaExportStop()
                if prevStop then prevStop() end
                if ogs.udp then ogs.udp:close(); ogs.udp = nil end
            end
            """;
    }
}
}
