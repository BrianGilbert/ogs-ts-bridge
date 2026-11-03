#nullable enable
using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// rFactor 2 engine shared memory, as published by the rF2 Shared Memory
/// Map Plugin (rF2State.h, Pack=4) — one format, two games: rFactor 2 and
/// Le Mans Ultimate. Enable the plugin in-game (rF2: Settings → Plugins;
/// LMU: UserData\player\CustomPluginVariables.JSON, " Enabled": 1).
///
/// Maps (each preceded by an 8-byte version block, begin @0 / end @4):
/// $rFactor2SMMP_Telemetry$ (every car's physics, stride 1888 @16),
/// $rFactor2SMMP_Scoring$ (rF2ScoringInfo @12, cars stride 584 @560),
/// $rFactor2SMMP_Extended$ (physics options @24, damage per mID % 512
/// @64 stride 16, status / LSI messages, pit speed limit) and
/// $rFactor2SMMP_ForceFeedback$ (one double @8). Gather copies the
/// player's telemetry slot every poll (torn copies dropped) and refreshes
/// the scoring, extended and FFB parts every ~100 ms, assembling one
/// snapshot: header @0 (version, inRealtime, part flags), telemetry
/// @<see cref="V"/>, scoring info @<see cref="S"/>, player scoring
/// @<see cref="P"/>, extended parts @<see cref="X"/>.
///
/// Everything for the player's car is mapped: physics, inputs, engine,
/// aero, damage, hybrid, all wheel data, scoring and timing, weather,
/// session, server and rules messages (the other cars' entries — the
/// standings grid — are not; the session carries one object). Graphics
/// (camera), pit menu, rules and multi-session maps are not read.
///
/// Wheels: WheelState rpm from mRotation, compression = mSuspensionDeflection,
/// contact from mSurfaceType; extended block: steerAngle = mToe,
/// loadForce = mTireLoad, damperVelocity derived from the deflection
/// change over mDeltaTime (the format has no damper velocity),
/// tyrePressure kPa → Pa, tyreTemp = carcass K → °C, tyreWear = mWear.
///
/// Axes per the ISI convention: left-handed X-left / Y-up / Z-back for
/// local and world vectors (unverified against the live game — flip
/// options exist), mapped to OGS as (−x, y, −z), rates (−ωx, ωy, −ωz).
/// mLocalAccel is taken as gravity-free; the specific force is derived
/// (`gravIncluded` to override). Nothing is emitted outside realtime.
/// </summary>
public sealed class Rf2Source : ILegacySource, ISharedMemoryGather
{
    const int VehicleStride = 1888;
    const int TelemetryVehicles = 16;
    const int ScoringInfoAt = 12, ScoringInfoSize = 548;
    const int ScoringVehicles = 560, ScoringStride = 584;
    const int MaxVehicles = 128;
    const int SlowRefreshPolls = 50;                 // ~100 ms at a 2 ms poll
    const double RadPerSecToRpm = 60.0 / (2.0 * Math.PI);
    const double Kelvin = -273.15;

    // snapshot layout
    internal const int V = 16;                       // player telemetry
    internal const int S = V + VehicleStride;        // scoring info (1904)
    internal const int P = S + ScoringInfoSize;      // player scoring (2452)
    internal const int X = P + ScoringStride;        // extended parts (3036)
    const int XPhysics = X, XDamage = X + 40, XStatus = X + 56, XHistory = X + 184,
              XPitLimit = X + 312, XLsi = X + 316, XFfb = X + 700;
    internal const int SnapshotSize = XFfb + 8;      // 3744
    static readonly string[] W = { "fl", "fr", "rl", "rr" };

    readonly PageChannels _fields = new(BuildFields());
    OgsChannel _speed, _fuelLevel, _steer, _sector, _sectorInPit, _impactX, _impactY, _impactZ, _windX, _windY, _windZ;
    readonly OgsChannel[] _lsi = new OgsChannel[4];
    OgsChannel _track, _vehicle, _vehicleClass, _driver, _plrFile, _server, _pitGroup,
               _compoundFront, _compoundRear, _status, _history;
    double _sy = 1, _sp = 1, _sr = 1;
    double _wheelbase = 2.7, _track_ = 1.6, _radius = 0.33;
    bool _gravIncluded;
    readonly double[] _lastDeflection = new double[4];
    bool _haveDeflection;

    // gather state (forwarder thread only)
    int _playerId = -1, _slot = -1, _slowPolls = SlowRefreshPolls;
    byte _inRealtime;
    uint _lastVersion;
    bool _haveVersion;
    readonly byte[] _scoringCache = new byte[ScoringInfoSize + ScoringStride];
    readonly byte[] _extendedCache = new byte[SnapshotSize - X];
    bool _haveScoring, _haveExtended;

    public string Id => "rf2";
    public string Title => "rFactor 2 engine (rFactor 2 / Le Mans Ultimate)";
    public string Description =>
        "rF2 Shared Memory Map Plugin ($rFactor2SMMP_*) — rFactor 2 and " +
        "Le Mans Ultimate. Enable the plugin in the game's plugin settings.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "rFactor2", "Le Mans Ultimate" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("rFactor 2", "rFactor2"),
        new LegacyGame("Le Mans Ultimate", "Le Mans Ultimate"),
    };

    public SourceTransport Transport => SourceTransport.SharedMemory;
    public string SharedMemoryName => "$rFactor2SMMP_Telemetry$";
    public int SharedMemorySize => SnapshotSize;
    public int PollIntervalMs => 2;
    public IReadOnlyList<string> AuxiliaryMapNames { get; } = new[]
    {
        "$rFactor2SMMP_Scoring$", "$rFactor2SMMP_Extended$", "$rFactor2SMMP_ForceFeedback$",
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",      "negate yaw Euler + yaw-rate sign (bool, default false)"),
        ("flipPitch",    "negate pitch Euler + pitch-rate sign (bool, default false)"),
        ("flipRoll",     "negate roll Euler + roll-rate sign (bool, default false)"),
        ("gravIncluded", "mLocalAccel already includes gravity reaction (bool, default false)"),
        ("wheelbase",    "wheel separation fore/aft in metres (default 2.7)"),
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
        _wheelbase = o.GetDouble("wheelbase", 2.7);
        _track_ = o.GetDouble("track", 1.6);
        _radius = o.GetDouble("radius", 0.33);
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "rFactor 2 engine car");
        session.SendChangedChannelsOnly();
        float hz = (float)(_wheelbase * 0.5), hx = (float)(_track_ * 0.5);
        session.AddWheel("wheel.fl", new Vector3(-hx, 0, hz), (float)_radius, true, true);
        session.AddWheel("wheel.fr", new Vector3(+hx, 0, hz), (float)_radius, true, true);
        session.AddWheel("wheel.rl", new Vector3(-hx, 0, -hz), (float)_radius, true, false);
        session.AddWheel("wheel.rr", new Vector3(+hx, 0, -hz), (float)_radius, true, false);

        _speed       = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        _fuelLevel   = session.AddChannel("fuel.level", "", "Remaining fuel fraction 0..1");
        _steer       = session.AddChannel("ctrl.steering.angle", "rad", "Handwheel angle, + = right");
        _sector      = session.AddChannel("lap.sector", "", "Current sector (0-based)");
        _sectorInPit = session.AddBooleanChannel("pit.inPitLane", "In the pit lane (mCurrentSector sign bit)");
        _impactX     = session.AddChannel("impact.position.x", "m", "Last impact position, OGS x");
        _impactY     = session.AddChannel("impact.position.y", "m", "Last impact position, OGS y");
        _impactZ     = session.AddChannel("impact.position.z", "m", "Last impact position, OGS z");
        _windX       = session.AddChannel("wind.x", "m/s", "Wind, OGS world x");
        _windY       = session.AddChannel("wind.y", "m/s", "Wind, OGS world y");
        _windZ       = session.AddChannel("wind.z", "m/s", "Wind, OGS world z");
        _fields.Declare(session);

        _track         = session.AddSessionText("session.track", "Track name");
        _vehicle       = session.AddSessionText("vehicle.name", "Vehicle name");
        _vehicleClass  = session.AddSessionText("vehicle.class", "Vehicle class");
        _driver        = session.AddSessionText("driver.name", "Driver name");
        _plrFile       = session.AddSessionText("rf2.profile", "Player profile file name");
        _server        = session.AddSessionText("server.name", "Server name");
        _pitGroup      = session.AddSessionText("pit.group", "Pit group");
        _compoundFront = session.AddSessionText("tyre.compoundFront", "Front tyre compound");
        _compoundRear  = session.AddSessionText("tyre.compoundRear", "Rear tyre compound");
        _status        = session.AddSessionText("message.status", "Status message");
        _history       = session.AddSessionText("message.history", "Last message history line");
        _lsi[0] = session.AddSessionText("message.lsiPhase", "Race control: phase message");
        _lsi[1] = session.AddSessionText("message.lsiPitState", "Race control: pit state message");
        _lsi[2] = session.AddSessionText("message.lsiOrder", "Race control: order instruction");
        _lsi[3] = session.AddSessionText("message.lsiRules", "Race control: rules instruction");
    }

    public bool AcceptsDatagram(int length) => length >= SnapshotSize;

    // ---- ISharedMemoryGather (forwarder thread) ----

    public int Gather(MemoryMappedViewAccessor primary,
                      IReadOnlyList<MemoryMappedViewAccessor?> auxiliary, byte[] buffer)
    {
        if (++_slowPolls >= SlowRefreshPolls)
        {
            _slowPolls = 0;
            if (auxiliary[0] is { } scoring) ScanScoring(scoring);
            ReadExtended(auxiliary[1], auxiliary[2]);
        }

        uint begin = primary.ReadUInt32(0), end = primary.ReadUInt32(4);
        if (begin != end) return 0;                        // being written
        if (_haveVersion && end == _lastVersion) return 0; // nothing new

        int count = Math.Clamp(primary.ReadInt32(12), 0, MaxVehicles);
        if (!FindSlot(primary, count)) return 0;

        primary.ReadArray(TelemetryVehicles + (long)_slot * VehicleStride, buffer, V, VehicleStride);
        if (primary.ReadUInt32(0) != end) return 0;        // torn copy — retry next poll

        _lastVersion = end;
        _haveVersion = true;
        MemoryMarshal.Write(buffer.AsSpan(0, 4), in end);
        buffer[4] = _inRealtime;
        buffer[5] = _haveScoring ? (byte)1 : (byte)0;
        buffer[6] = _haveExtended ? (byte)1 : (byte)0;
        Buffer.BlockCopy(_scoringCache, 0, buffer, S, _scoringCache.Length);
        Buffer.BlockCopy(_extendedCache, 0, buffer, X, _extendedCache.Length);
        return SnapshotSize;
    }

    void ScanScoring(MemoryMappedViewAccessor scoring)
    {
        scoring.ReadArray(ScoringInfoAt, _scoringCache, 0, ScoringInfoSize);
        _inRealtime = _scoringCache[115];
        int count = Math.Clamp(MemoryMarshal.Read<int>(_scoringCache.AsSpan(104)), 0, MaxVehicles);
        for (int i = 0; i < count; i++)
        {
            long at = ScoringVehicles + (long)i * ScoringStride;
            if (scoring.ReadByte(at + 196) == 0) continue;
            _playerId = scoring.ReadInt32(at);
            scoring.ReadArray(at, _scoringCache, ScoringInfoSize, ScoringStride);
            _haveScoring = true;
            return;
        }
        _playerId = -1;
        _haveScoring = false;
    }

    void ReadExtended(MemoryMappedViewAccessor? extended, MemoryMappedViewAccessor? ffb)
    {
        var c = _extendedCache;
        if (extended is not null)
        {
            extended.ReadArray(8 + 16, c, XPhysics - X, 40);
            if (_playerId >= 0)
                extended.ReadArray(8 + 56 + (_playerId % 512) * 16L, c, XDamage - X, 16);
            extended.ReadArray(8 + 9316, c, XStatus - X, 128);
            extended.ReadArray(8 + 9452, c, XHistory - X, 128);
            extended.ReadArray(8 + 9580, c, XPitLimit - X, 4);
            extended.ReadArray(8 + 9600, c, XLsi - X, 96);
            extended.ReadArray(8 + 9704, c, XLsi - X + 96, 96);
            extended.ReadArray(8 + 9808, c, XLsi - X + 192, 96);
            extended.ReadArray(8 + 9912, c, XLsi - X + 288, 96);
            _haveExtended = true;
        }
        if (ffb is not null) ffb.ReadArray(8, c, XFfb - X, 8);
    }

    bool FindSlot(MemoryMappedViewAccessor telemetry, int count)
    {
        if (_playerId < 0)
        {
            // scoring not mapped (yet): a single-car session is unambiguous
            _slot = count == 1 ? 0 : -1;
            if (count == 1) _inRealtime = 1;
            return _slot >= 0;
        }
        if (_slot >= 0 && _slot < count &&
            telemetry.ReadInt32(TelemetryVehicles + (long)_slot * VehicleStride) == _playerId)
            return true;
        for (int i = 0; i < count; i++)
        {
            if (telemetry.ReadInt32(TelemetryVehicles + (long)i * VehicleStride) != _playerId) continue;
            _slot = i;
            return true;
        }
        _slot = -1;
        return false;
    }

    // ---- translation ----

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static double D(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<double>(d.Slice(off, 8));
    static int I(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<int>(d.Slice(off, 4));
    static Vector3 Map(ReadOnlySpan<byte> d, int off) =>
        new(-(float)D(d, off), (float)D(d, off + 8), -(float)D(d, off + 16));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (d[4] == 0) return; // garage / menus — not driving

        // mOri rows map local -> world; local forward is −Z, local right is −X.
        // The world shares the local convention, so its vectors take the
        // same (−x, y, −z) map into OGS world axes.
        const int ori = V + 232;
        var fwd = new Vector3((float)D(d, ori + 16), -(float)D(d, ori + 40), (float)D(d, ori + 64));
        var right = new Vector3((float)D(d, ori), -(float)D(d, ori + 24), (float)D(d, ori + 48));
        var (yaw, pitch, roll) = OgsMath.EulerFromForwardRight(fwd, right);
        var q = OgsMath.EulerToQuaternion(_sy * yaw, _sp * pitch, _sr * roll);
        session.SetPose(Map(d, V + 160), q);

        var accel = Map(d, V + 208);
        var rates = new Vector3((float)(_sp * -D(d, V + 304)), (float)(_sy * D(d, V + 312)),
                                (float)(_sr * -D(d, V + 320)));
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, q), rates);

        double dt = D(d, V + 4);
        for (int w = 0; w < 4; w++)
        {
            int at = V + 848 + w * 260;
            double deflection = D(d, at);
            // no damper velocity until two deflection samples exist
            float damper = _haveDeflection && dt > 0 ? (float)((deflection - _lastDeflection[w]) / dt) : float.NaN;
            _lastDeflection[w] = deflection;
            session.SetWheel(w, (float)(Math.Abs(D(d, at + 40)) * RadPerSecToRpm), float.NaN,
                (float)deflection, Surface(d[at + 176]));
            session.SetWheelDynamics(w, (float)D(d, at + 196), (float)D(d, at + 104), damper);
            session.SetWheelTyre(w,
                (float)(D(d, at + 120) * 1000), (float)(D(d, at + 204) + Kelvin), (float)D(d, at + 152));
        }
        _haveDeflection = true;

        double vx = D(d, V + 184), vy = D(d, V + 192), vz = D(d, V + 200);
        session.SetChannel(_speed, Math.Sqrt(vx * vx + vy * vy + vz * vz));
        double capacity = D(d, V + 608);
        if (capacity > 0) session.SetChannel(_fuelLevel, Math.Clamp(D(d, V + 524) / capacity, 0, 1));
        // steering −1..1 (left to right) over the physical lock-to-lock range
        session.SetChannel(_steer, D(d, V + 404) * F(d, V + 692) * 0.5 * Math.PI / 180.0);
        session.SetChannel(_sectorInPit, I(d, V + 600) < 0);
        var impact = Map(d, V + 568);
        session.SetChannel(_impactX, impact.X);
        session.SetChannel(_impactY, impact.Y);
        session.SetChannel(_impactZ, impact.Z);

        // the table spans telemetry, scoring and extended parts; parts not
        // gathered yet are cut off so their zeros are never sent
        _fields.Write(d.Slice(0, d[6] != 0 ? SnapshotSize : d[5] != 0 ? X : S), session);
        session.SetChannel(_sector, I(d, V + 600) & 0x7FFFFFFF);
        session.SetSessionText(_vehicle, d.Slice(V + 32, 64));
        session.SetSessionText(_compoundFront, d.Slice(V + 620, 18));
        session.SetSessionText(_compoundRear, d.Slice(V + 638, 18));
        session.SetTypeLabel(d.Slice(V + 32, 64));

        if (d[5] != 0)
        {
            var wind = Map(d, S + 244);
            session.SetChannel(_windX, wind.X);
            session.SetChannel(_windY, wind.Y);
            session.SetChannel(_windZ, wind.Z);
            session.SetSessionText(_track, d.Slice(S, 64));
            session.SetLocation(d.Slice(S, 64));
            session.SetSessionText(_driver, d.Slice(P + 4, 32));
            session.SetSessionText(_vehicleClass, d.Slice(P + 200, 32));
            session.SetSessionText(_plrFile, d.Slice(S + 148, 64));
            session.SetSessionText(_server, d.Slice(S + 296, 32));
            session.SetSessionText(_pitGroup, d.Slice(P + 480, 24));
        }
        if (d[6] != 0)
        {
            session.SetSessionText(_status, d.Slice(XStatus, 128));
            session.SetSessionText(_history, d.Slice(XHistory, 128));
            for (int i = 0; i < 4; i++) session.SetSessionText(_lsi[i], d.Slice(XLsi + i * 96, 96));
        }

        session.Tick(now);
    }

    /// <summary>rF2 mSurfaceType: 0 dry, 1 wet, 2 grass, 3 dirt, 4 gravel,
    /// 5 rumblestrip, 6 special.</summary>
    static Ogs.Telemetry.Schema.ContactMedium Surface(byte type) => type switch
    {
        0 or 1 => Ogs.Telemetry.Schema.ContactMedium.Asphalt,
        2 => Ogs.Telemetry.Schema.ContactMedium.Grass,
        3 => Ogs.Telemetry.Schema.ContactMedium.Dirt,
        4 => Ogs.Telemetry.Schema.ContactMedium.Gravel,
        5 => Ogs.Telemetry.Schema.ContactMedium.RumbleStrip,
        _ => Ogs.Telemetry.Schema.ContactMedium.Other,
    };

    static PageField[] BuildFields()
    {
        const FieldKind f32 = FieldKind.F32, f64 = FieldKind.F64, i32 = FieldKind.I32,
                        u8 = FieldKind.U8, i8 = FieldKind.I8, i16 = FieldKind.I16, b8 = FieldKind.Bool8;
        var t = new List<PageField>
        {
            // ---- vehicle telemetry ----
            new("rf2.deltaTime", "s", "Time since last update", V + 4, f64),
            new("session.elapsedTime", "s", "Game session time", V + 12, f64),
            new("lap.current", "", "Current lap number", V + 20, i32),
            new("lap.startTime", "s", "Session time this lap started", V + 24, f64),
            new("velocity.body.x", "m/s", "Local velocity, OGS body x (right)", V + 184, f64, -1),
            new("velocity.body.y", "m/s", "Local velocity, OGS body y (up)", V + 192, f64),
            new("velocity.body.z", "m/s", "Local velocity, OGS body z (forward)", V + 200, f64, -1),
            new("air.alphaX", "rad/s2", "Angular acceleration, OGS body x (pitch)", V + 328, f64, -1),
            new("air.alphaY", "rad/s2", "Angular acceleration, OGS body y (yaw)", V + 336, f64),
            new("air.alphaZ", "rad/s2", "Angular acceleration, OGS body z (roll)", V + 344, f64, -1),
            new("transmission.gear", "", "-1 reverse, 0 neutral, 1..n forward", V + 352, i32),
            new("engine.rpm", "rpm", "Engine rpm", V + 356, f64),
            new("engine.temperature", "degC", "Coolant temperature", V + 364, f64),
            new("engine.oilTemp", "degC", "Oil temperature", V + 372, f64),
            new("transmission.clutchRpm", "rpm", "Clutch rpm", V + 380, f64),
            new("ctrl.throttle", "", "Throttle pedal 0..1", V + 388, f64),
            new("ctrl.brake", "", "Brake pedal 0..1", V + 396, f64),
            new("ctrl.steering.input", "", "Steering input -1..1 (left to right)", V + 404, f64),
            new("ctrl.clutch", "", "Clutch pedal 0..1", V + 412, f64),
            new("engine.throttle", "", "Throttle 0..1 (after aids)", V + 420, f64),
            new("brakes.pedal", "", "Brake 0..1 (after aids)", V + 428, f64),
            new("ctrl.steering.filtered", "", "Steering -1..1 (after aids)", V + 436, f64),
            new("input.clutch", "", "Clutch 0..1 (after aids)", V + 444, f64),
            new("ctrl.ffb.torque", "Nm", "Torque around the steering shaft", V + 452, f64),
            new("susp.thirdSpring.front.deflection", "m", "Front third spring deflection", V + 460, f64),
            new("susp.thirdSpring.rear.deflection", "m", "Rear third spring deflection", V + 468, f64),
            new("susp.frontWingHeight", "m", "Front wing height", V + 476, f64),
            new("susp.frontRideHeight", "m", "Front ride height", V + 484, f64),
            new("susp.rearRideHeight", "m", "Rear ride height", V + 492, f64),
            new("aero.drag", "N", "Drag", V + 500, f64),
            new("aero.frontDownforce", "N", "Front downforce", V + 508, f64),
            new("aero.rearDownforce", "N", "Rear downforce", V + 516, f64),
            new("fuel.quantity", "l", "Fuel", V + 524, f64),
            new("engine.rpmMax", "rpm", "Rev limit", V + 532, f64),
            new("pit.scheduledStops", "", "Scheduled pit stops", V + 540, u8),
            new("engine.overheating", "", "Overheating warning shown", V + 541, b8),
            new("damage.detached", "", "A part (besides wheels) has detached", V + 542, b8),
            new("lights.headlights", "", "Headlights on", V + 543, b8),
            new("impact.time", "s", "Session time of the last impact", V + 552, f64),
            new("impact.magnitude", "", "Magnitude of the last impact", V + 560, f64),
            new("engine.torque", "Nm", "Engine torque (incl. additive)", V + 592, f64),
            new("pit.limiter", "", "Speed limiter on", V + 604, b8),
            new("transmission.gearCount", "", "Forward gears", V + 605, u8),
            new("tyre.compoundIndexFront", "", "Front compound index within brand", V + 606, u8),
            new("tyre.compoundIndexRear", "", "Rear compound index within brand", V + 607, u8),
            new("fuel.capacity", "l", "Fuel capacity", V + 608, f64),
            new("drs.frontFlap", "", "Front flap activated", V + 616, b8),
            new("drs.active", "", "Rear flap activated", V + 617, b8),
            new("drs.legalStatus", "", "0 disallowed, 1 detected not yet allowed, 2 allowed", V + 618, u8),
            new("engine.ignition", "", "0 off, 1 ignition, 2 ignition + starter", V + 619, u8),
            new("pit.limiterAvailable", "", "Speed limiter available", V + 656, b8),
            new("engine.antiStall", "", "Anti-stall activated", V + 657, b8),
            new("ctrl.steering.visualRange", "rad", "Visual steering wheel range", V + 660, f32, Math.PI / 180),
            new("brakes.bias", "", "Rear brake bias fraction", V + 664, f64),
            new("engine.boostPressure", "", "Turbo boost pressure (game units)", V + 672, f64),
            new("rf2.graphicsOffset.x", "m", "Static CG to graphical centre, rF2 x", V + 680, f32),
            new("rf2.graphicsOffset.y", "m", "Static CG to graphical centre, rF2 y", V + 684, f32),
            new("rf2.graphicsOffset.z", "m", "Static CG to graphical centre, rF2 z", V + 688, f32),
            new("ctrl.steering.range", "rad", "Physical steering wheel range", V + 692, f32, Math.PI / 180),
            new("ers.batterySoc", "", "Battery charge 0..1", V + 696, f64),
            new("ers.motorTorque", "Nm", "Electric boost motor torque", V + 704, f64),
            new("ers.motorRpm", "rpm", "Electric boost motor rpm", V + 712, f64),
            new("ers.motorTemp", "degC", "Electric boost motor temperature", V + 720, f64),
            new("ers.coolantTemp", "degC", "Electric boost cooler water temperature", V + 728, f64),
            new("ers.motorState", "", "0 unavailable, 1 inactive, 2 propulsion, 3 regeneration", V + 736, u8),

            // ---- scoring info ----
            new("session.type", "", "0 testday, 1-4 practice, 5-8 qualify, 9 warmup, 10-13 race", S + 64, i32),
            new("session.currentTime", "s", "Current session time", S + 68, f64),
            new("session.endTime", "s", "Session end time", S + 76, f64),
            new("session.lapsTotal", "", "Maximum laps", S + 84, i32),
            new("session.layoutLength", "m", "Lap distance", S + 88, f64),
            new("race.carCount", "", "Cars in session", S + 104, i32),
            new("session.phase", "", "0 garage, 1 warm-up, 2 gridwalk, 3 formation, 4 countdown, 5 green, 6 FCY, 7 stopped, 8 over, 9 paused", S + 108, u8),
            new("flags.yellowState", "", "-1 invalid, 0 none, 1 pending, 2 pits closed, 3 pit lead lap, 4 pits open, 5 last lap, 6 resume, 7 race halt", S + 109, i8),
            new("flags.sector1Yellow", "", "Local yellow in sector 1", S + 110, i8),
            new("flags.sector2Yellow", "", "Local yellow in sector 2", S + 111, i8),
            new("flags.sector3Yellow", "", "Local yellow in sector 3", S + 112, i8),
            new("session.startLight", "", "Start light frame", S + 113, u8),
            new("session.redLights", "", "Number of red lights in the start sequence", S + 114, u8),
            new("session.inRealtime", "", "In realtime (driving)", S + 115, b8),
            new("weather.cloudDarkness", "", "Cloud darkness 0..1", S + 212, f64),
            new("weather.precipitation", "", "Rain severity 0..1", S + 220, f64),
            new("weather.ambientTemp", "degC", "Ambient temperature", S + 228, f64),
            new("weather.trackTemp", "degC", "Track temperature", S + 236, f64),
            new("weather.wetnessMin", "", "Minimum path wetness", S + 268, f64),
            new("weather.wetnessMax", "", "Maximum path wetness", S + 276, f64),
            new("server.gameMode", "", "1 server, 2 client, 3 server and client", S + 284, u8),
            new("server.passwordProtected", "", "Server is password protected", S + 285, b8),
            new("server.maxPlayers", "", "Maximum players", S + 292, i32),
            new("session.startTime", "s", "Session start time", S + 328, f32),
            new("weather.wetness", "", "Average path wetness", S + 332, f64),

            // ---- player scoring ----
            new("lap.completed", "", "Laps completed", P + 100, i16),
            new("lap.sectorScoring", "", "Sector: 0 sector 3, 1 sector 1, 2 sector 2", P + 102, i8),
            new("race.finishStatus", "", "0 running, 1 finished, 2 DNF, 3 DQ", P + 103, i8),
            new("lap.distance", "m", "Distance into the lap", P + 104, f64),
            new("track.pathLateral", "m", "Lateral offset from the approximate centre path", P + 112, f64),
            new("track.edge", "m", "Track edge offset on the car's side", P + 120, f64),
            new("lap.bestSector1", "s", "Best sector 1", P + 128, f64),
            new("lap.bestSector2Cumulative", "s", "Best sectors 1+2", P + 136, f64),
            new("lap.bestTime", "s", "Best lap", P + 144, f64),
            new("lap.lastSector1", "s", "Last lap sector 1", P + 152, f64),
            new("lap.lastSector2Cumulative", "s", "Last lap sectors 1+2", P + 160, f64),
            new("lap.lastTime", "s", "Last lap", P + 168, f64),
            new("lap.currentSector1", "s", "Current lap sector 1", P + 176, f64),
            new("lap.currentSector2Cumulative", "s", "Current lap sectors 1+2", P + 184, f64),
            new("pit.stopsDone", "", "Pit stops made", P + 192, i16),
            new("penalty.count", "", "Outstanding penalties", P + 194, i16),
            new("vehicle.controlType", "", "-1 nobody, 0 player, 1 AI, 2 remote, 3 replay", P + 197, i8),
            new("pit.inPits", "", "Between pit entrance and exit", P + 198, b8),
            new("race.position", "", "Position (1 = first)", P + 199, u8),
            new("race.gapAhead", "s", "Time behind the car ahead", P + 232, f64),
            new("race.lapsBehindNext", "", "Laps behind the car ahead", P + 240, i32),
            new("race.gapLeader", "s", "Time behind the leader", P + 244, f64),
            new("race.lapsBehindLeader", "", "Laps behind the leader", P + 252, i32),
            new("pit.state", "", "0 none, 1 request, 2 entering, 3 stopped, 4 exiting", P + 457, u8),
            new("race.serverScored", "", "Counted in multiplayer standings", P + 458, u8),
            new("race.individualPhase", "", "Game phase for this car (+9 after formation, 10 under yellow)", P + 459, u8),
            new("race.qualificationPosition", "", "Qualifying position (1-based)", P + 460, i32, NotAvailable: -1),
            new("lap.currentTime", "s", "Time into the lap", P + 464, f64),
            new("lap.estimatedTime", "s", "Estimated lap time", P + 472, f64),
            new("flags.primary", "", "0 green, 6 blue", P + 504, u8),
            new("flags.underYellow", "", "Car is under yellow", P + 505, b8),
            new("lap.countFlag", "", "0 not counted, 1 lap only, 2 lap and time", P + 506, u8),
            new("pit.inGarageStall", "", "In the garage stall", P + 507, b8),
            new("pit.lapDistance", "m", "Pit lane lap distance", P + 524, f32),
            new("lap.bestLapSector1", "s", "Sector 1 of the best lap", P + 528, f32),
            new("lap.bestLapSector2Cumulative", "s", "Sectors 1+2 of the best lap", P + 532, f32),

            // ---- extended: physics options, damage, pit limit, FFB ----
            new("aids.tcLevel", "", "Traction control 0 off - 3 high", XPhysics + 0, u8),
            new("aids.absLevel", "", "ABS 0 off - 2 high", XPhysics + 1, u8),
            new("aids.espLevel", "", "Stability control 0 off - 2 high", XPhysics + 2, u8),
            new("aids.autoShift", "", "0 off, 1 up, 2 down, 3 all", XPhysics + 3, u8),
            new("aids.autoClutch", "", "Auto clutch", XPhysics + 4, b8),
            new("aids.invulnerable", "", "Invulnerable", XPhysics + 5, b8),
            new("aids.oppositeLock", "", "Opposite lock help", XPhysics + 6, b8),
            new("aids.steeringHelp", "", "Steering help 0 off - 3 high", XPhysics + 7, u8),
            new("aids.brakingHelp", "", "Braking help 0 off - 2 high", XPhysics + 8, u8),
            new("aids.spinRecovery", "", "Spin recovery", XPhysics + 9, b8),
            new("aids.autoPit", "", "Auto pit", XPhysics + 10, b8),
            new("aids.autoLift", "", "Auto lift", XPhysics + 11, b8),
            new("aids.autoBlip", "", "Auto blip", XPhysics + 12, b8),
            new("session.fuelUseRate", "", "Fuel multiplier", XPhysics + 13, u8),
            new("session.tyreWearRate", "", "Tyre wear multiplier", XPhysics + 14, u8),
            new("session.mechanicalFailures", "", "0 off, 1 normal, 2 timescaled", XPhysics + 15, u8),
            new("aids.pitcrewPush", "", "Pit crew push allowed", XPhysics + 16, b8),
            new("aids.repeatShifts", "", "Repeat shift prevention 0-5", XPhysics + 17, u8),
            new("aids.holdClutch", "", "Hold clutch at start", XPhysics + 18, b8),
            new("aids.autoReverse", "", "Auto reverse", XPhysics + 19, b8),
            new("aids.alternateNeutral", "", "Up+down shift selects neutral", XPhysics + 20, b8),
            new("aids.aiControl", "", "Player car under AI control", XPhysics + 21, b8),
            new("aids.manualShiftOverrideTime", "s", "Auto-shift pause after a manual shift", XPhysics + 24, f32),
            new("aids.autoShiftOverrideTime", "s", "Manual-shift pause after an auto shift", XPhysics + 28, f32),
            new("aids.speedSensitiveSteering", "", "Speed sensitive steering 0..1", XPhysics + 32, f32),
            new("aids.steerRatioSpeed", "m/s", "Speed below which lock expands to full", XPhysics + 36, f32),
            new("damage.maxImpact", "", "Largest impact magnitude", XDamage, f64),
            new("damage.accumulatedImpact", "", "Accumulated impact magnitude", XDamage + 8, f64),
            new("pit.speedLimit", "m/s", "Current pit speed limit", XPitLimit, f32),
            new("ctrl.ffb.output", "", "Force feedback value", XFfb, f64),
        };
        for (int i = 0; i < 8; i++)
            t.Add(new($"damage.dent{i}", "", "Dent severity at location " + i + " (0 none, 1 some, 2 more)", V + 544 + i, u8));

        for (int w = 0; w < 4; w++)
        {
            string p = W[w];
            int a = V + 848 + w * 260;
            t.Add(new($"susp.{p}.rideHeight", "m", "Ride height", a + 8, f64));
            t.Add(new($"susp.{p}.force", "N", "Pushrod load", a + 16, f64));
            t.Add(new($"brakes.{p}.temp", "degC", "Brake temperature", a + 24, f64));
            t.Add(new($"brakes.{p}.pressure", "", "Brake pressure 0..1 (game units)", a + 32, f64));
            t.Add(new($"tyre.{p}.lateralPatchVel", "m/s", "Lateral velocity at the contact patch", a + 48, f64));
            t.Add(new($"tyre.{p}.longitudinalPatchVel", "m/s", "Longitudinal velocity at the contact patch", a + 56, f64));
            t.Add(new($"tyre.{p}.lateralGroundVel", "m/s", "Lateral ground velocity at the contact patch", a + 64, f64));
            t.Add(new($"tyre.{p}.longitudinalGroundVel", "m/s", "Longitudinal ground velocity at the contact patch", a + 72, f64));
            t.Add(new($"susp.{p}.camber", "rad", "Camber (+ outward)", a + 80, f64));
            t.Add(new($"tyre.{p}.lateralForce", "N", "Lateral force", a + 88, f64));
            t.Add(new($"tyre.{p}.longitudinalForce", "N", "Longitudinal force", a + 96, f64));
            t.Add(new($"tyre.{p}.gripFraction", "", "Fraction of the patch sliding", a + 112, f64));
            t.Add(new($"tyre.{p}.tempLeft", "degC", "Tread temperature, left", a + 128, f64, 1, Kelvin));
            t.Add(new($"tyre.{p}.tempCenter", "degC", "Tread temperature, centre", a + 136, f64, 1, Kelvin));
            t.Add(new($"tyre.{p}.tempRight", "degC", "Tread temperature, right", a + 144, f64, 1, Kelvin));
            t.Add(new($"tyre.{p}.surfaceType", "", "0 dry, 1 wet, 2 grass, 3 dirt, 4 gravel, 5 rumble, 6 special", a + 176, u8));
            t.Add(new($"tyre.{p}.flat", "", "Tyre flat", a + 177, b8));
            t.Add(new($"tyre.{p}.detached", "", "Wheel detached", a + 178, b8));
            t.Add(new($"tyre.{p}.radius", "m", "Static undeflected radius", a + 179, u8, 0.01));
            t.Add(new($"tyre.{p}.verticalDeflection", "m", "Tyre deflection from its radius", a + 180, f64));
            t.Add(new($"susp.{p}.wheelY", "m", "Wheel y relative to the vehicle", a + 188, f64));
            t.Add(new($"tyre.{p}.innerTempLeft", "degC", "Inner layer temperature, left", a + 212, f64, 1, Kelvin));
            t.Add(new($"tyre.{p}.innerTempCenter", "degC", "Inner layer temperature, centre", a + 220, f64, 1, Kelvin));
            t.Add(new($"tyre.{p}.innerTempRight", "degC", "Inner layer temperature, right", a + 228, f64, 1, Kelvin));
        }
        return t.ToArray();
    }
}
}
