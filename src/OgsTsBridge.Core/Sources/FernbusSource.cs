#nullable enable
using System;
using System.Collections.Generic;
using System.Net;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Fernbus Simulator's local REST API (http://127.0.0.1:37337, on while
/// the game runs). GET /Vehicles/Current answers a JSON object with
/// Speed, Rotation {Yaw, Pitch, Roll}, RPM, MaxRPM, Steering, Throttle,
/// Brake, EngineStarted, IsOffroad and further vehicle state; it is
/// polled every <see cref="PollIntervalMs"/>.
///
/// Every key is mapped (<see cref="JsonChannels"/>); RPM, MaxRPM,
/// Throttle and Brake get standard names, the rest publish raw as
/// "fernbus.&lt;path&gt;". Pose from Rotation (Unreal degrees). No
/// BodyDynamics: the document carries no acceleration or angular rate.
///
/// Unverified against the live game: the rotation signs (flip options),
/// and the units of Speed and Steering (published as sent).
/// </summary>
public sealed class FernbusSource : IPolledSource
{
    const double Deg = Math.PI / 180;
    static readonly IPEndPoint Server = new(IPAddress.Loopback, 37337);

    static readonly JsonKey[] Known =
    {
        new("RPM", "engine.rpm", "rpm", "Engine rpm"),
        new("MaxRPM", "engine.rpmMax", "rpm", "Engine rpm maximum"),
        new("Throttle", "engine.throttle", "", "Throttle 0..1"),
        new("Brake", "brakes.pedal", "", "Brake pedal 0..1"),
    };

    readonly JsonChannels _json = new("fernbus", Known);
    readonly int _yaw, _pitch, _roll;
    readonly LocalHttp _http = new(Server);
    readonly byte[] _request = LocalHttp.Request("/Vehicles/Current", Server);
    double _sy = 1, _sp = 1, _sr = 1;
    int _pollMs = 20;

    public FernbusSource()
    {
        _yaw = _json.Watch("Rotation.Yaw");
        _pitch = _json.Watch("Rotation.Pitch");
        _roll = _json.Watch("Rotation.Roll");
    }

    public string Id => "fernbus";
    public string Title => "Fernbus Simulator (localhost:37337)";
    public string Description =>
        "Fernbus Simulator's local REST API (http://127.0.0.1:37337/Vehicles/Current), polled; on whenever the game runs.";
    public int DefaultPort => 37337;
    public IReadOnlyList<string> ProcessNames { get; } = new[] { "Fernbus-Win64-Shipping" };
    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Fernbus Simulator", "Fernbus-Win64-Shipping"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("pollMs",    "poll interval in ms (default 20)"),
        ("flipYaw",   "negate yaw (bool, default false)"),
        ("flipPitch", "negate pitch (bool, default false)"),
        ("flipRoll",  "negate roll (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("pollMs", "flipYaw", "flipPitch", "flipRoll");
        _pollMs = Math.Clamp((int)o.GetDouble("pollMs", 20), 5, 1000);
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
    }

    public SourceTransport Transport => SourceTransport.Poll;
    public int PollIntervalMs => _pollMs;
    public int PollBufferSize => 1 << 16;
    public string PollTarget => "Fernbus Simulator on 127.0.0.1:37337";
    public bool Connect() => _http.Connect();
    public void Disconnect() => _http.Close();
    public int Read(byte[] buffer) => _http.Get(_request, buffer);

    public void Declare(OgsSession session)
    {
        session.DeclareObject("bus", Ogs.Telemetry.Schema.ObjectType.Vehicle, "Fernbus coach");
        session.SendChangedChannelsOnly();
        _json.Declare(session);
    }

    public bool AcceptsDatagram(int length) => length > 0;

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
        double yaw = _json[_yaw], pitch = _json[_pitch], roll = _json[_roll];
        if (double.IsNaN(yaw) || double.IsNaN(pitch) || double.IsNaN(roll)) return;
        session.SetPose(null, OgsMath.EulerToQuaternion(_sy * yaw * Deg, _sp * pitch * Deg, _sr * roll * Deg));
        session.Tick(now);
    }
}
}
