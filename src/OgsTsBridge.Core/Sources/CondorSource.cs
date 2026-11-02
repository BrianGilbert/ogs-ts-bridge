#nullable enable
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Condor 2 soaring simulator UDP output (default port 55278). In the
/// Condor install folder, Settings\UDP.ini: [General] Enabled=1,
/// [Connection] Host=127.0.0.1, Port=55278.
///
/// Each datagram is ASCII `key=value` lines. Every key Condor sends is
/// mapped: the keys below get standard channels, and any other key
/// becomes `condor.&lt;key&gt;` carrying Condor's raw value. The channel set
/// is learned from the stream — when a datagram carries a key not seen
/// before, the session redeclares (one discovery re-send) — so nothing
/// is guessed and nothing Condor sends is dropped. Keys a datagram omits
/// keep their last value.
///
/// Standard mappings: yaw, pitch, bank (rad) → pose; rollrate,
/// pitchrate, yawrate (rad/s) → body rates; gforce → specific force
/// (0, gforce·g, 0) and `air.loadFactor`; airspeed → air.indicatedAirspeed,
/// vario → air.variometer, altitude → position.altMsl, height →
/// env.heightFromGround, slipball → air.turnBall.
///
/// Attitude and rates are assumed aerospace-standard (+pitch nose-up,
/// +bank right-wing-down, +yaw clockwise) and mapped direct (unverified
/// against the live sim — flip options exist). Only the normal load
/// factor drives the specific force: heave is carried, surge and sway
/// are not.
/// </summary>
public sealed class CondorSource : ILegacySource
{
    const float Gravity = 9.80665f;

    /// <summary>Condor keys with a standard channel (name, unit, description).</summary>
    static readonly (string Key, string Channel, string Unit, string Description)[] Known =
    {
        ("airspeed",   "air.indicatedAirspeed", "m/s", "Airspeed"),
        ("vario",      "air.variometer", "m/s", "Variometer"),
        ("altitude",   "position.altMsl", "m", "Altitude"),
        ("height",     "env.heightFromGround", "m", "Height above ground"),
        ("slipball",   "air.turnBall", "", "Slip ball deflection"),
        ("gforce",     "air.loadFactor", "", "Normal load factor (g)"),
    };

    readonly List<byte[]> _keys = new();            // every key seen, in declaration order
    readonly List<OgsChannel> _channels = new();
    readonly List<byte[]> _pendingKeys = new();
    double _sy = 1, _sp = 1, _sr = 1;
    double _yaw, _pitch, _bank, _rollRate, _pitchRate, _yawRate, _gforce = 1;

    public string Id => "condor";
    public string Title => "Condor 2 (UDP output)";
    public string Description =>
        "Condor 2 UDP telemetry (port 55278). Enable it in the Condor " +
        "folder's Settings\\UDP.ini (Enabled=1, Host=127.0.0.1, Port=55278).";
    public int DefaultPort => 55278;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "Condor" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Condor 2", "Condor"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate yaw + yaw-rate sign (bool, default false)"),
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
        session.DeclareObject("glider", Ogs.Telemetry.Schema.ObjectType.Aircraft, "Condor glider");
        session.DeclareGravity(Gravity);
        session.SendChangedChannelsOnly();
        _channels.Clear();
        foreach (var key in _keys) _channels.Add(DeclareKey(session, key));
    }

    static OgsChannel DeclareKey(OgsSession session, byte[] key)
    {
        var name = Encoding.ASCII.GetString(key);
        foreach (var k in Known)
            if (k.Key == name) return session.AddChannel(k.Channel, k.Unit, k.Description);
        return session.AddChannel("condor." + name, "", $"Condor UDP value '{name}' (raw)");
    }

    public bool AcceptsDatagram(int length) => length >= 4;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        bool any = false;
        var rest = d;
        while (!rest.IsEmpty)
        {
            int nl = rest.IndexOf((byte)'\n');
            var line = nl < 0 ? rest : rest.Slice(0, nl);
            rest = nl < 0 ? default : rest.Slice(nl + 1);
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line.Slice(0, line.Length - 1);

            int eq = line.IndexOf((byte)'=');
            if (eq <= 0) continue;
            var key = line.Slice(0, eq);
            if (!Utf8Parser.TryParse(line.Slice(eq + 1), out double v, out _)) continue;
            any = true;

            if (key.SequenceEqual("yaw"u8)) _yaw = v;
            else if (key.SequenceEqual("pitch"u8)) _pitch = v;
            else if (key.SequenceEqual("bank"u8)) _bank = v;
            else if (key.SequenceEqual("rollrate"u8)) _rollRate = v;
            else if (key.SequenceEqual("pitchrate"u8)) _pitchRate = v;
            else if (key.SequenceEqual("yawrate"u8)) _yawRate = v;
            else if (key.SequenceEqual("gforce"u8)) _gforce = v;

            int index = IndexOf(key);
            if (index >= 0) session.SetChannel(_channels[index], v);
            else if (!IsPending(key)) _pendingKeys.Add(key.ToArray()); // new key: redeclare below
        }
        if (!any) return;

        if (_pendingKeys.Count > 0)
        {
            // a key not seen before: grow the channel set (cold path, once per new key)
            _keys.AddRange(_pendingKeys);
            _pendingKeys.Clear();
            session.BeginRedeclare();
            Declare(session);
            session.CommitRedeclare(now);
            Translate(d, session, now); // re-read this datagram into the new channels
            return;
        }

        session.SetPose(null, OgsMath.EulerToQuaternion(_sy * _yaw, _sp * _pitch, _sr * _bank));
        session.SetBodyDynamics(
            new Vector3(0, (float)_gforce * Gravity, 0),
            new Vector3((float)(_sp * _pitchRate), (float)(_sy * _yawRate), (float)(_sr * _rollRate)));
        session.Tick(now);
    }

    int IndexOf(ReadOnlySpan<byte> key)
    {
        for (int i = 0; i < _keys.Count; i++)
            if (key.SequenceEqual(_keys[i])) return i;
        return -1;
    }

    bool IsPending(ReadOnlySpan<byte> key)
    {
        foreach (var k in _pendingKeys)
            if (key.SequenceEqual(k)) return true;
        return false;
    }
}
}
