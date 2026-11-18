#nullable enable
using System;
using System.Collections.Generic;

namespace OgsTsBridge.Sources
{
/// <summary>
/// X-Wing Alliance with the X-Wing Alliance Upgrade's CockpitLook output
/// (default UDP port 1138): one flat JSON object per datagram, keys
/// "XWA.player.*" (shipname, crafttypename, speed, throttle, ELSlasers,
/// ELSshields, ELSbeam, s-foils, shielddirection, shieldfront, shieldback,
/// hull, shake, beamactive, undertractorbeam, underjammingbeam,
/// activeweapon, laserfired, warheadfired, yaw_inertia, pitch_inertia,
/// roll_inertia, abs_yaw, abs_pitch, abs_roll, accel_inertia) and
/// "XWA.status.*" (location, hangar). Flags may arrive as true/false or
/// 0/1; both map to booleans or numbers as sent.
///
/// Every key is mapped (<see cref="JsonChannels"/>) and published as
/// "xwa.&lt;name&gt;"; the channel set is learned from the stream. Pose from
/// abs_yaw / abs_pitch / abs_roll. The inertia values carry no documented
/// unit or frame, so there is no BodyDynamics: they ride channels.
///
/// Unverified against the live game: the attitude unit (`euler=deg|rad`,
/// default degrees) and its signs (flip options).
/// </summary>
public sealed class XwaSource : ILegacySource
{
    readonly JsonChannels _json;
    readonly int _yaw, _pitch, _roll;
    double _sy = 1, _sp = 1, _sr = 1, _angle = Math.PI / 180;

    public XwaSource()
    {
        _json = new JsonChannels("xwa", resolve: Resolve);
        _yaw = _json.Watch("XWA.player.abs_yaw");
        _pitch = _json.Watch("XWA.player.abs_pitch");
        _roll = _json.Watch("XWA.player.abs_roll");
    }

    static JsonKey? Resolve(string key)
    {
        const string player = "XWA.player.", status = "XWA.status.";
        string? name = key.StartsWith(player, StringComparison.Ordinal) ? key[player.Length..]
                     : key.StartsWith(status, StringComparison.Ordinal) ? "status." + key[status.Length..]
                     : null;
        return name is null ? null : new JsonKey(key, "xwa." + JsonChannels.Sanitize(name), "", $"'{key}' (raw)");
    }

    public string Id => "xwa";
    public string Title => "X-Wing Alliance (XWAU CockpitLook output)";
    public string Description =>
        "X-Wing Alliance Upgrade JSON telemetry (UDP port 1138) from the CockpitLook output.";
    public int DefaultPort => 1138;
    public IReadOnlyList<string> ProcessNames { get; } = new[] { "xwingalliance" };
    public IReadOnlyList<LegacyGame> Games { get; } = new[] { new LegacyGame("X-Wing Alliance", "xwingalliance") };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate yaw (bool, default false)"),
        ("flipPitch", "negate pitch (bool, default false)"),
        ("flipRoll",  "negate roll (bool, default false)"),
        ("euler",     "attitude unit: deg (default) or rad"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "euler");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _angle = o.GetString("euler", "deg").Equals("rad", StringComparison.OrdinalIgnoreCase) ? 1.0 : Math.PI / 180;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("ship", Ogs.Telemetry.Schema.ObjectType.Aircraft, "X-Wing Alliance craft");
        session.SendChangedChannelsOnly();
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
        double yaw = _json[_yaw], pitch = _json[_pitch], roll = _json[_roll];
        if (double.IsNaN(yaw) || double.IsNaN(pitch) || double.IsNaN(roll)) return;
        session.SetPose(null, OgsMath.EulerToQuaternion(_sy * yaw * _angle, _sp * pitch * _angle, _sr * roll * _angle));
        session.Tick(now);
    }
}
}
