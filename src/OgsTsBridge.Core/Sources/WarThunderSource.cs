#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;

namespace OgsTsBridge.Sources
{
/// <summary>
/// War Thunder's built-in local web server (http://127.0.0.1:8111, on
/// whenever the game runs): /state (flight model: "H, m", "TAS, km/h",
/// "IAS, km/h", "M", "AoA, deg", "AoS, deg", "Ny", "Vy, m/s", "Wx, deg/s",
/// control surfaces in %, fuel mass, per-engine throttle, rpm, manifold
/// pressure, temperatures, power and thrust) and /indicators (the cockpit
/// instrument readings, keyed by gauge, plus "type", the vehicle).
///
/// Both documents are polled every <see cref="PollIntervalMs"/> and every
/// key is mapped (<see cref="JsonChannels"/>): /state keys carry their unit
/// after a comma and convert to C-3 (km/h, deg, deg/s, %, atm, hp, kgs and
/// C handled; others published as sent), well-known ones get standard
/// names; /indicators keys are instrument readings published raw as
/// "warthunder.ind.&lt;key&gt;"; "type" is the type label.
///
/// Pose from the indicators' compass heading and artificial-horizon pitch
/// and roll (degrees; horizon roll taken as − bank — signs unverified
/// against the live game, flip options exist). Specific force from the
/// normal load factor (0, Ny·g, 0) and the roll rate Wx; only aircraft
/// report these. Nothing is emitted while /state says valid = false.
/// </summary>
public sealed class WarThunderSource : IPolledSource
{
    const double Deg = Math.PI / 180;
    const float Gravity = 9.80665f;
    static readonly IPEndPoint Server = new(IPAddress.Loopback, 8111);

    static readonly JsonKey[] StateKnown =
    {
        new("H, m", "position.altMsl", "m", "Altitude"),
        new("TAS, km/h", "air.trueAirspeed", "m/s", "True airspeed", 1 / 3.6),
        new("IAS, km/h", "air.indicatedAirspeed", "m/s", "Indicated airspeed", 1 / 3.6),
        new("M", "air.mach", "", "Mach number"),
        new("AoA, deg", "air.aoa", "rad", "Angle of attack", Deg),
        new("AoS, deg", "air.beta", "rad", "Angle of sideslip", Deg),
        new("Ny", "air.loadFactor", "", "Normal load factor"),
        new("Vy, m/s", "air.verticalSpeed", "m/s", "Vertical speed"),
        new("Wx, deg/s", "warthunder.rollRate", "rad/s", "Roll rate", Deg),
        new("aileron, %", "ctrl.aileron", "", "Aileron −1..1", 0.01),
        new("elevator, %", "ctrl.elevator", "", "Elevator −1..1", 0.01),
        new("rudder, %", "ctrl.rudder", "", "Rudder −1..1", 0.01),
        new("flaps, %", "ctrl.flaps", "", "Flaps 0..1", 0.01),
        new("gear, %", "gear.position", "", "Gear 0 up .. 1 down", 0.01),
        new("airbrake, %", "ctrl.spoilers", "", "Air brake 0..1", 0.01),
        new("throttle 1, %", "engine.throttle", "", "Engine 1 throttle 0..1", 0.01),
        new("RPM 1", "engine.rpm", "rpm", "Engine 1 rpm"),
    };

    readonly JsonChannels _state = new("warthunder", StateKnown, ResolveUnitKey);
    readonly JsonChannels _ind = new("warthunder.ind");
    readonly int _valid, _ny, _wx, _compass, _pitch, _roll;
    readonly LocalHttp _http = new(Server);
    readonly byte[] _stateRequest = LocalHttp.Request("/state", Server);
    readonly byte[] _indRequest = LocalHttp.Request("/indicators", Server);
    double _sy = 1, _sp = 1, _sr = 1;
    int _pollMs = 20;

    public WarThunderSource()
    {
        _valid = _state.Watch("valid");
        _ny = _state.Watch("Ny");
        _wx = _state.Watch("Wx, deg/s");
        _compass = _ind.Watch("compass");
        _pitch = _ind.Watch("aviahorizon_pitch");
        _roll = _ind.Watch("aviahorizon_roll");
        _state.Ignore("valid");
        _ind.Ignore("valid");
        _ind.Ignore("type"); // the type label
    }

    /// <summary>"name, unit" keys: the unit converted to C-3.</summary>
    static JsonKey? ResolveUnitKey(string key)
    {
        int comma = key.LastIndexOf(", ", StringComparison.Ordinal);
        if (comma < 0) return null;
        var name = "warthunder." + JsonChannels.Sanitize(key[..comma]);
        var unit = key[(comma + 2)..];
        (string u, double scale)? c = unit switch
        {
            "km/h" => ("m/s", 1 / 3.6), "m/s" => ("m/s", 1), "m" => ("m", 1), "deg" => ("rad", Deg),
            "deg/s" => ("rad/s", Deg), "%" => ("", 0.01), "kg" => ("kg", 1), "atm" => ("Pa", 101325),
            "hp" => ("W", 745.7), "kgs" => ("N", 9.80665), "C" => ("degC", 1),
            _ => null,
        };
        return c is { } v
            ? new JsonKey(key, name, v.u, $"{key[..comma]} (sent in {unit})", v.scale)
            : new JsonKey(key, name, "", $"{key[..comma]} (raw, {unit})");
    }

    public string Id => "warthunder";
    public string Title => "War Thunder (localhost:8111)";
    public string Description =>
        "War Thunder's local telemetry server (http://127.0.0.1:8111 /state " +
        "and /indicators), polled; on whenever the game runs.";
    public int DefaultPort => 8111;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "aces" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("War Thunder", "aces"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("pollMs",    "poll interval in ms (default 20)"),
        ("flipYaw",   "negate heading (bool, default false)"),
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
    public int PollBufferSize => 1 << 17;
    public string PollTarget => "War Thunder on 127.0.0.1:8111";

    public bool Connect() => _http.Connect();
    public void Disconnect() => _http.Close();

    /// <summary>Both documents, /state then /indicators, separated by a NUL.</summary>
    public int Read(byte[] buffer)
    {
        int half = buffer.Length / 2;
        int state = _http.Get(_stateRequest, buffer.AsSpan(0, half - 1));
        if (state < 0) return -1;
        buffer[state] = 0;
        int ind = _http.Get(_indRequest, buffer.AsSpan(state + 1, buffer.Length - state - 1));
        if (ind < 0) return -1;
        return state + 1 + ind;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("vehicle", Ogs.Telemetry.Schema.ObjectType.Aircraft, "War Thunder vehicle");
        session.DeclareGravity(Gravity);
        session.SendChangedChannelsOnly();
        _state.Declare(session);
        _ind.Declare(session);
    }

    public bool AcceptsDatagram(int length) => length > 0;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        int split = d.IndexOf((byte)0);
        var state = split < 0 ? d : d[..split];
        var ind = split < 0 ? default : d[(split + 1)..];

        bool grew = _state.Read(state, session) | _ind.Read(ind, session);
        if (grew)
        {
            // keys not seen before: grow the channel set (cold path)
            session.BeginRedeclare();
            Declare(session);
            session.CommitRedeclare(now);
            _state.Read(state, session);
            _ind.Read(ind, session);
        }
        TypeLabel(ind, session);
        if (_state[_valid] < 0.5 || double.IsNaN(_ind[_compass])) return;

        double pitch = double.IsNaN(_ind[_pitch]) ? 0 : _ind[_pitch];
        double roll = double.IsNaN(_ind[_roll]) ? 0 : _ind[_roll];
        session.SetPose(null, OgsMath.EulerToQuaternion(
            _sy * _ind[_compass] * Deg, _sp * pitch * Deg, -_sr * roll * Deg));
        double ny = double.IsNaN(_state[_ny]) ? 1 : _state[_ny];
        double wx = double.IsNaN(_state[_wx]) ? 0 : _state[_wx];
        session.SetBodyDynamics(new Vector3(0, (float)ny * Gravity, 0), new Vector3(0, 0, (float)(wx * Deg)));
        session.Tick(now);
    }

    /// <summary>The indicators' "type" string (the vehicle) as type label.</summary>
    static void TypeLabel(ReadOnlySpan<byte> ind, OgsSession session)
    {
        int at = ind.IndexOf("\"type\""u8);
        if (at < 0) return;
        var rest = ind[(at + 6)..];
        int open = rest.IndexOf((byte)'"');
        if (open < 0) return;
        rest = rest[(open + 1)..];
        int close = rest.IndexOf((byte)'"');
        if (close > 0) session.SetTypeLabel(rest[..close]);
    }
}
}
