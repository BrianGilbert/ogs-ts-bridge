#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace OgsTsBridge.Sources
{
/// <summary>
/// FlatOut 4: Total Insanity VR built-in telemetry: one JSON object per UDP
/// datagram (default 127.0.0.1:20777, 60 Hz), configured by
/// Data\flatout_telemetry.cfg in the game folder. The game ships it
/// enabled; the patcher rewrites the file with every send_* group on.
///
/// Every key is mapped (<see cref="JsonChannels"/>, learned from the
/// stream). Known keys: OutputActive, Paused, FrameValid, DeltaTime,
/// RaceTimeMS, SpeedMps, RPM, Steering, Brake, YawSpeed, Pitch/Roll/
/// YawDegrees, SuspensionTravel{FL,FR,RL,RR}, SuspensionVelocity{…},
/// LocalVelocity{X,Y,Z}; the position, acceleration, gear and impact
/// groups arrive under names learned live and are published raw.
///
/// Pose from the Euler degrees, assumed yaw + right, pitch + up, roll +
/// right (unverified against the live game — flip options exist). No
/// BodyDynamics yet: the acceleration group's key names and frames are
/// unconfirmed, so the source publishes pose and channels only. Nothing is
/// emitted unless OutputActive and FrameValid are set; Paused rides
/// session.paused.
/// </summary>
public sealed class FlatOut4Source : ILegacySource, IGamePatcher
{
    const double Deg = Math.PI / 180;

    static readonly JsonKey[] Known =
    {
        new("SpeedMps", "vehicle.speed", "m/s", "Speed"),
        new("RPM", "engine.rpm", "rpm", "Engine speed"),
        new("Brake", "brakes.pedal", "", "Brake pedal 0..1"),
        new("Throttle", "engine.throttle", "", "Throttle 0..1"),
        new("Gear", "transmission.gear", "", "Gear"),
        new("Steering", "flatout.steering", "", "Steering input"),
        new("DeltaTime", "flatout.deltaTime", "s", "Frame delta time"),
        new("RaceTimeMS", "race.time", "s", "Race time", Scale: 0.001),
        new("YawSpeed", "flatout.yawSpeed", "rad/s", "Yaw rate (unit assumed rad/s)"),
        new("PitchDegrees", "flatout.pitch", "rad", "Pitch", Scale: Deg),
        new("RollDegrees", "flatout.roll", "rad", "Roll", Scale: Deg),
        new("YawDegrees", "flatout.yaw", "rad", "Yaw", Scale: Deg),
        new("LocalVelocityX", "flatout.localVelocity.x", "m/s", "Local velocity X"),
        new("LocalVelocityY", "flatout.localVelocity.y", "m/s", "Local velocity Y"),
        new("LocalVelocityZ", "flatout.localVelocity.z", "m/s", "Local velocity Z"),
        new("SuspensionTravelFL", "susp.travel.fl", "m", "Suspension travel, front left"),
        new("SuspensionTravelFR", "susp.travel.fr", "m", "Suspension travel, front right"),
        new("SuspensionTravelRL", "susp.travel.rl", "m", "Suspension travel, rear left"),
        new("SuspensionTravelRR", "susp.travel.rr", "m", "Suspension travel, rear right"),
        new("SuspensionVelocityFL", "susp.velocity.fl", "m/s", "Suspension velocity, front left"),
        new("SuspensionVelocityFR", "susp.velocity.fr", "m/s", "Suspension velocity, front right"),
        new("SuspensionVelocityRL", "susp.velocity.rl", "m/s", "Suspension velocity, rear left"),
        new("SuspensionVelocityRR", "susp.velocity.rr", "m/s", "Suspension velocity, rear right"),
    };

    readonly JsonChannels _json = new("flatout", Known);
    readonly int _active, _paused, _valid, _yaw, _pitch, _roll;
    OgsChannel _pausedChannel;
    double _sy = 1, _sp = 1, _sr = 1;

    public FlatOut4Source()
    {
        _active = _json.Watch("OutputActive");
        _paused = _json.Watch("Paused");
        _valid = _json.Watch("FrameValid");
        _yaw = _json.Watch("YawDegrees");
        _pitch = _json.Watch("PitchDegrees");
        _roll = _json.Watch("RollDegrees");
        _json.Ignore("Paused"); // session.paused
    }

    public string Id => "flatout4";
    public string Title => "FlatOut 4: Total Insanity VR (built-in telemetry)";
    public string Description =>
        "FlatOut 4 VR JSON telemetry (UDP port 20777), on by default via the " +
        "game's Data\\flatout_telemetry.cfg; the patch re-enables every group.";
    public int DefaultPort => 20777;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "Flatout" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("FlatOut 4: Total Insanity VR", "Flatout"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate yaw (bool, default false)"),
        ("flipPitch", "negate pitch (bool, default false)"),
        ("flipRoll",  "negate roll (bool, default false)"),
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
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "FlatOut 4 car");
        session.SendChangedChannelsOnly();
        _pausedChannel = session.AddBooleanChannel("session.paused", "Game paused");
        session.AlwaysSend(_pausedChannel);
        _json.Declare(session);
    }

    public bool AcceptsDatagram(int length) => length >= 2;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (_json.Read(d, session))
        {
            // keys not seen before: grow the channel set (cold path)
            session.BeginRedeclare();
            Declare(session);
            session.CommitRedeclare(now);
            _json.Read(d, session);
        }
        if (_json[_active] < 0.5 || _json[_valid] < 0.5) return; // menus, loading
        if (double.IsNaN(_json[_yaw])) return;

        session.SetChannel(_pausedChannel, _json[_paused] >= 0.5);
        session.SetPose(null, OgsMath.EulerToQuaternion(
            _sy * _json[_yaw] * Deg, _sp * _json[_pitch] * Deg, _sr * _json[_roll] * Deg));
        session.Tick(now);
    }

    // ---- IGamePatcher: Data\flatout_telemetry.cfg ----

    internal static readonly string[] InstallFolders = { "Project Fox", "FlatOut4", "Flatout" };

    public IList<string> PatchGameTelemetry(int port)
    {
        var dir = SteamLibrary.FindGame(InstallFolders);
        return dir is null ? new List<string>() : PatchInstall(dir, port);
    }

    /// <summary>Writes Data\flatout_telemetry.cfg with every group enabled
    /// (one-time .bak-ogs-ts-bridge backup of an existing file).</summary>
    public static IList<string> PatchInstall(string installDir, int port)
    {
        var patched = new List<string>();
        var data = Path.Combine(installDir, "Data");
        if (!Directory.Exists(data)) return patched;
        var cfg = Path.Combine(data, "flatout_telemetry.cfg");
        var text = string.Join(Environment.NewLine, new[]
        {
            "enabled = true", "ip = 127.0.0.1", $"port = {port}", "rate = 60",
            "send_position = true", "send_orientation = true", "send_velocity = true",
            "send_acceleration = true", "send_engine_rpm = true", "send_gear = true",
            "send_suspension = true", "send_impact = true",
        }) + Environment.NewLine;
        if (File.Exists(cfg))
        {
            if (File.ReadAllText(cfg) == text) return patched;
            var backup = cfg + ".bak-ogs-ts-bridge";
            if (!File.Exists(backup)) File.Copy(cfg, backup);
        }
        File.WriteAllText(cfg, text);
        patched.Add(cfg);
        return patched;
    }
}
}
