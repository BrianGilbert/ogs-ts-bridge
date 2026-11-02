#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// ManiaPlanet (and Trackmania Turbo) telemetry shared memory
/// "ManiaPlanet_Telemetry" (maniaplanet_telemetry.h, 1396 bytes). The
/// game publishes it whenever it runs; no setting.
///
/// Layout: header (magic "ManiaPlanet_Telemetry" @0, version @32, size
/// @36), UpdateNumber @40 (change token); game state @44 (state, gameplay
/// variant @48, map id @112, map name @176); race state @560 (state,
/// time ms, respawns, checkpoint count, 125 checkpoint times @576,
/// checkpoints per lap @1076, laps per race @1080, timestamps); object
/// state @1108 (timestamp, discontinuity count, rotation quaternion w,x,y,z
/// @1116, translation @1132 and velocity @1144 with +x left / +y up / +z
/// front, last stable ground contact time); vehicle state @1192 (inputs,
/// engine rpm, gear, turbo, per-wheel ground contact / slip / damper
/// length, damper range, rumble, speedometer km/h, water / sparks / light
/// trails / lights / flying flags); device state @1344 (VR-chair Euler,
/// centred yaw and altitude).
///
/// Every field is mapped. The game frame is left-handed with +x left, so
/// OGS takes (−x, y, z) for vectors and the quaternion; world velocity and
/// its derivative ride Kinematics. Ticks on a new UpdateNumber while the
/// game state is running or paused (paused rides session.paused).
/// </summary>
public sealed class ManiaPlanetSource : ILegacySource
{
    internal const int PageSize = 1396;
    static readonly AxisMap Axes = AxisMap.Parse("-x,y,z");

    uint _lastUpdate = uint.MaxValue;
    Vector3 _prevVelocity;
    uint _prevStamp;
    bool _havePrev;

    OgsChannel _gameState, _raceState, _raceTime, _respawns, _checkpoints, _cpPerLap, _laps, _raceStamp, _raceStart;
    OgsChannel _objStamp, _discontinuity, _groundTime, _vehStamp, _steer, _gas, _braking, _horn, _rpm, _gear,
        _turbo, _freeWheel, _rangeMin, _rangeMax, _rumble, _speed, _water, _sparkling, _trails, _lights, _flying,
        _eulerX, _eulerY, _eulerZ, _cYaw, _cAlt, _paused, _mapId, _version;
    readonly OgsChannel[] _cpTimes = new OgsChannel[125];
    readonly OgsChannel[] _contact = new OgsChannel[4], _slip = new OgsChannel[4], _damper = new OgsChannel[4];
    readonly int[] _wheel = new int[4];

    public string Id => "maniaplanet";
    public string Title => "ManiaPlanet / Trackmania Turbo (shared memory)";
    public string Description =>
        "ManiaPlanet telemetry shared memory (ManiaPlanet_Telemetry), 2 ms " +
        "poll; published whenever the game runs.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "ManiaPlanet", "TrackmaniaTurbo" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("ManiaPlanet (Trackmania² / Shootmania)", "ManiaPlanet"),
        new LegacyGame("Trackmania Turbo", "TrackmaniaTurbo"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = Array.Empty<(string, string)>();

    public void Configure(IReadOnlyDictionary<string, string> options) =>
        new SourceOptions(options).EnsureKnown();

    public SourceTransport Transport => SourceTransport.SharedMemory;
    public string SharedMemoryName => "ManiaPlanet_Telemetry";
    public int SharedMemorySize => PageSize;
    public int PollIntervalMs => 2;

    public void Declare(OgsSession session)
    {
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "ManiaPlanet car");
        session.SendChangedChannelsOnly();
        _paused = session.AddBooleanChannel("session.paused", "Game paused");
        session.AlwaysSend(_paused);
        _version     = session.AddChannel("maniaplanet.version", "", "Telemetry header version");
        _gameState   = session.AddChannel("maniaplanet.gameState", "", "0 starting, 1 menus, 2 running, 3 paused");
        _mapId       = session.AddSessionText("maniaplanet.mapId", "Map id");
        _raceState   = session.AddChannel("maniaplanet.raceState", "", "0 before start, 1 running, 2 finished");
        _raceTime    = session.AddChannel("race.time", "s", "Race time");
        _respawns    = session.AddChannel("maniaplanet.respawns", "", "Respawn count");
        _checkpoints = session.AddChannel("maniaplanet.checkpoints", "", "Checkpoints passed");
        for (int i = 0; i < _cpTimes.Length; i++)
            _cpTimes[i] = session.AddChannel($"maniaplanet.checkpointTime{i + 1}", "s", $"Checkpoint {i + 1} time");
        _cpPerLap    = session.AddChannel("maniaplanet.checkpointsPerLap", "", "Checkpoints per lap");
        _laps        = session.AddChannel("maniaplanet.lapsPerRace", "", "Laps per race");
        _raceStamp   = session.AddChannel("maniaplanet.raceTimestamp", "", "Race state timestamp (raw)");
        _raceStart   = session.AddChannel("maniaplanet.raceStartTimestamp", "", "Race start timestamp (raw)");
        _objStamp    = session.AddChannel("maniaplanet.objectTimestamp", "", "Object state timestamp (raw)");
        _discontinuity = session.AddChannel("maniaplanet.discontinuityCount", "", "Teleport counter");
        _groundTime  = session.AddChannel("maniaplanet.lastGroundContactTime", "", "Latest stable ground contact time (raw)");
        _vehStamp    = session.AddChannel("maniaplanet.vehicleTimestamp", "", "Vehicle state timestamp (raw)");
        _steer       = session.AddChannel("maniaplanet.steer", "", "Steering input −1..1");
        _gas         = session.AddChannel("engine.throttle", "", "Gas pedal 0..1");
        _braking     = session.AddBooleanChannel("maniaplanet.braking", "Brake input");
        _horn        = session.AddBooleanChannel("maniaplanet.horn", "Horn input");
        _rpm         = session.AddChannel("engine.rpm", "rpm", "Engine speed");
        _gear        = session.AddChannel("transmission.gear", "", "Current gear");
        _turbo       = session.AddChannel("maniaplanet.turboRatio", "", "Turbo 1 → 0 as it runs out");
        _freeWheel   = session.AddBooleanChannel("maniaplanet.freeWheeling", "Engine free-wheeling");
        _rangeMin    = session.AddChannel("maniaplanet.damperRangeMin", "m", "Damper range minimum");
        _rangeMax    = session.AddChannel("maniaplanet.damperRangeMax", "m", "Damper range maximum");
        _rumble      = session.AddChannel("maniaplanet.rumble", "", "Rumble intensity");
        _speed       = session.AddChannel("vehicle.speed", "m/s", "Speedometer");
        _water       = session.AddBooleanChannel("maniaplanet.inWater", "In water");
        _sparkling   = session.AddBooleanChannel("maniaplanet.sparkling", "Sparks");
        _trails      = session.AddBooleanChannel("maniaplanet.lightTrails", "Light trails");
        _lights      = session.AddBooleanChannel("lights.headlights", "Lights on");
        _flying      = session.AddBooleanChannel("maniaplanet.flying", "Long time since touching ground");
        _eulerX      = session.AddChannel("maniaplanet.device.yaw", "rad", "VR chair Euler yaw");
        _eulerY      = session.AddChannel("maniaplanet.device.pitch", "rad", "VR chair Euler pitch");
        _eulerZ      = session.AddChannel("maniaplanet.device.roll", "rad", "VR chair Euler roll");
        _cYaw        = session.AddChannel("maniaplanet.device.centeredYaw", "rad", "VR chair accumulated, recentred yaw");
        _cAlt        = session.AddChannel("maniaplanet.device.centeredAltitude", "m", "VR chair accumulated, recentred altitude");

        string[] corner = { "fl", "fr", "rl", "rr" };
        for (int w = 0; w < 4; w++)
        {
            _contact[w] = session.AddBooleanChannel($"maniaplanet.wheel.{corner[w]}.groundContact", $"Wheel {corner[w]} on ground");
            _slip[w]    = session.AddBooleanChannel($"maniaplanet.wheel.{corner[w]}.slipping", $"Wheel {corner[w]} slipping");
            _damper[w]  = session.AddChannel($"maniaplanet.wheel.{corner[w]}.damperLength", "m", $"Wheel {corner[w]} damper length");
            _wheel[w]   = session.AddWheel("wheel." + corner[w], Vector3.Zero, 0.35f,
                driven: w >= 2, steered: w < 2, suspension: false);
        }
    }

    public bool AcceptsDatagram(int length) => length >= PageSize;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));
    static uint U(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<uint>(d.Slice(off, 4));
    static Vector3 V(ReadOnlySpan<byte> d, int off) => new(F(d, off), F(d, off + 4), F(d, off + 8));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (d.Length < PageSize || !d.StartsWith("ManiaPlanet_Telemetry"u8)) return;
        uint update = U(d, 40);
        if (update == _lastUpdate) return;
        _lastUpdate = update;

        uint gameState = U(d, 44);
        session.SetChannel(_version, U(d, 32));
        session.SetChannel(_gameState, gameState);
        if (gameState is not (2 or 3)) { _havePrev = false; return; } // starting / menus

        session.SetTypeLabel(Text(d, 48, 64));
        session.SetSessionText(_mapId, Text(d, 112, 64));
        session.SetLocation(Text(d, 176, 256));
        session.SetChannel(_paused, gameState == 3);

        session.SetChannel(_raceState, U(d, 560));
        session.SetChannel(_raceTime, U(d, 564) * 0.001);
        session.SetChannel(_respawns, U(d, 568));
        uint passed = U(d, 572);
        session.SetChannel(_checkpoints, passed);
        for (int i = 0; i < _cpTimes.Length && i < passed; i++)
            session.SetChannel(_cpTimes[i], U(d, 576 + i * 4) * 0.001);
        session.SetChannel(_cpPerLap, U(d, 1076));
        session.SetChannel(_laps, U(d, 1080));
        session.SetChannel(_raceStamp, U(d, 1084));
        session.SetChannel(_raceStart, U(d, 1088));

        uint stamp = U(d, 1108);
        session.SetChannel(_objStamp, stamp);
        uint discontinuity = U(d, 1112);
        session.SetChannel(_discontinuity, discontinuity);
        var q = new Quaternion(F(d, 1120), F(d, 1124), F(d, 1128), F(d, 1116)); // stored w,x,y,z
        var position = Axes.Vector(V(d, 1132));
        var velocity = Axes.Vector(V(d, 1144));
        session.SetPose(position, Quaternion.Normalize(Axes.Rotation(q)));
        // acceleration: velocity derivative over the object timestamps (ms)
        var accel = Vector3.Zero;
        if (_havePrev && stamp > _prevStamp && stamp - _prevStamp < 1000)
            accel = (velocity - _prevVelocity) / ((stamp - _prevStamp) * 0.001f);
        _prevVelocity = velocity;
        _prevStamp = stamp;
        _havePrev = true;
        session.SetKinematics(velocity, accel);
        session.SetChannel(_groundTime, U(d, 1156));

        session.SetChannel(_vehStamp, U(d, 1192));
        session.SetChannel(_steer, F(d, 1196));
        session.SetChannel(_gas, F(d, 1200));
        session.SetChannel(_braking, U(d, 1204) != 0);
        session.SetChannel(_horn, U(d, 1208) != 0);
        session.SetChannel(_rpm, F(d, 1212));
        session.SetChannel(_gear, MemoryMarshal.Read<int>(d.Slice(1216, 4)));
        session.SetChannel(_turbo, F(d, 1220));
        session.SetChannel(_freeWheel, U(d, 1224) != 0);
        float rangeMin = F(d, 1276), rangeMax = F(d, 1280);
        for (int w = 0; w < 4; w++)
        {
            bool contact = U(d, 1228 + w * 4) != 0;
            bool slipping = U(d, 1244 + w * 4) != 0;
            float damper = F(d, 1260 + w * 4);
            session.SetChannel(_contact[w], contact);
            session.SetChannel(_slip[w], slipping);
            session.SetChannel(_damper[w], damper);
            session.SetWheel(_wheel[w], slipping ? 1f : 0f, rangeMax - damper,
                contact ? Ogs.Telemetry.Schema.ContactMedium.Asphalt : Ogs.Telemetry.Schema.ContactMedium.Air);
        }
        session.SetChannel(_rangeMin, rangeMin);
        session.SetChannel(_rangeMax, rangeMax);
        session.SetChannel(_rumble, F(d, 1284));
        session.SetChannel(_speed, U(d, 1288) / 3.6);
        session.SetChannel(_water, U(d, 1292) != 0);
        session.SetChannel(_sparkling, U(d, 1296) != 0);
        session.SetChannel(_trails, U(d, 1300) != 0);
        session.SetChannel(_lights, U(d, 1304) != 0);
        session.SetChannel(_flying, U(d, 1308) != 0);
        session.SetChannel(_eulerX, F(d, 1344));
        session.SetChannel(_eulerY, F(d, 1348));
        session.SetChannel(_eulerZ, F(d, 1352));
        session.SetChannel(_cYaw, F(d, 1356));
        session.SetChannel(_cAlt, F(d, 1360));

        session.Tick(now);
    }

    static ReadOnlySpan<byte> Text(ReadOnlySpan<byte> d, int off, int size)
    {
        var s = d.Slice(off, size);
        int nul = s.IndexOf((byte)0);
        return nul < 0 ? s : s[..nul];
    }
}
}
