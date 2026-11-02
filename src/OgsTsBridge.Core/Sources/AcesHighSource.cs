#nullable enable
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Numerics;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Aces High III flight data output (the game's flightdata.txt). Enable it
/// in Offline → Options → Controls → Force Feedback (data output, port
/// 556 by default).
///
/// One ASCII line per datagram, comma separated:
///   Version,clock,SimType,x,y,z,xvel,yvel,zvel,xacc,yacc,zacc,
///   roll,pitch,yaw,rollvel,pitchvel,yawvel,rollacc,pitchacc,yawacc
/// Positions in ft (y up, z forward, x right; x and z always 0), velocity
/// in ft/s, acceleration in ft/s², attitude in degrees (roll + right, pitch
/// + up, yaw + LEFT), angular velocity in deg/s and angular acceleration in
/// deg/s² with the same senses. SimType: 0 not in flight, 1 plane, 3
/// chute, 4 gunner, 5 attached, 6 vehicle, 7 boat, 8 object gunner, 9
/// amphibious.
///
/// The axes match OGS body axes (x right, y up, z forward), so vectors map
/// direct; yaw is negated (OGS + right), rates are (pitch, −yaw, roll).
/// The velocity and acceleration are taken as body-frame and the
/// acceleration as gravity-free, with the specific force derived (both
/// unverified against the live game — `gravIncluded` overrides). Nothing
/// is emitted while SimType is 0 (not in flight).
/// </summary>
public sealed class AcesHighSource : ILegacySource
{
    const double Deg = Math.PI / 180;
    const float Ft = 0.3048f;
    internal const int FieldCount = 21;

    readonly double[] _v = new double[FieldCount];
    OgsChannel _version, _clock, _simType, _alt, _vx, _vy, _vz, _speed, _aax, _aay, _aaz;
    bool _gravIncluded;

    public string Id => "aceshigh";
    public string Title => "Aces High III (flight data output)";
    public string Description =>
        "Aces High flight data UDP (port 556). Enable it in Offline → " +
        "Options → Controls → Force Feedback, pointed at this machine.";
    public int DefaultPort => 556;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "aceshighsteam" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Aces High III", "aceshighsteam"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("gravIncluded", "acceleration already includes gravity reaction (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("gravIncluded");
        _gravIncluded = o.GetBool("gravIncluded", false);
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("aircraft", Ogs.Telemetry.Schema.ObjectType.Aircraft, "Aces High vehicle");
        _version = session.AddChannel("aceshigh.version", "", "Output format version (hex digits read as a number)");
        _clock   = session.AddChannel("aceshigh.clock", "", "Game clock (raw)");
        _simType = session.AddChannel("aceshigh.simType", "",
            "0 not in flight, 1 plane, 3 chute, 4 gunner, 5 attached, 6 vehicle, 7 boat, 8 object gunner, 9 amphibious");
        _alt     = session.AddChannel("position.altMsl", "m", "Altitude");
        _vx      = session.AddChannel("velocity.body.x", "m/s", "Body velocity, right");
        _vy      = session.AddChannel("velocity.body.y", "m/s", "Body velocity, up");
        _vz      = session.AddChannel("velocity.body.z", "m/s", "Body velocity, forward");
        _speed   = session.AddChannel("vehicle.speed", "m/s", "Speed (velocity magnitude)");
        _aax     = session.AddChannel("aceshigh.angularAccel.x", "rad/s2", "Angular acceleration, pitch (+ nose-up)");
        _aay     = session.AddChannel("aceshigh.angularAccel.y", "rad/s2", "Angular acceleration, yaw (+ right)");
        _aaz     = session.AddChannel("aceshigh.angularAccel.z", "rad/s2", "Angular acceleration, roll (+ right)");
    }

    public bool AcceptsDatagram(int length) => length >= FieldCount * 2 - 1;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        var rest = d;
        for (int i = 0; i < FieldCount; i++)
        {
            int sep = rest.IndexOfAny((byte)',', (byte)'\n');
            var field = sep < 0 ? rest : rest.Slice(0, sep);
            if (!field.IsEmpty && field[^1] == (byte)'\r') field = field.Slice(0, field.Length - 1);
            if (!Utf8Parser.TryParse(field, out double value, out int used) || used != field.Length)
                return; // malformed — drop
            _v[i] = value;
            if (sep < 0) { if (i != FieldCount - 1) return; break; }
            rest = rest.Slice(sep + 1);
        }

        session.SetChannel(_version, _v[0]);
        session.SetChannel(_clock, _v[1]);
        session.SetChannel(_simType, _v[2]);
        if (_v[2] == 0) return; // not in flight

        var q = OgsMath.EulerToQuaternion(-_v[14] * Deg, _v[13] * Deg, _v[12] * Deg);
        session.SetPose(null, q);
        var accel = new Vector3((float)_v[9], (float)_v[10], (float)_v[11]) * Ft;
        var rates = new Vector3((float)(_v[16] * Deg), (float)(-_v[17] * Deg), (float)(_v[15] * Deg));
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, q), rates);

        var vel = new Vector3((float)_v[6], (float)_v[7], (float)_v[8]) * Ft;
        session.SetChannel(_alt, _v[4] * Ft);
        session.SetChannel(_vx, vel.X);
        session.SetChannel(_vy, vel.Y);
        session.SetChannel(_vz, vel.Z);
        session.SetChannel(_speed, vel.Length());
        session.SetChannel(_aax, _v[19] * Deg);
        session.SetChannel(_aay, -_v[20] * Deg);
        session.SetChannel(_aaz, _v[18] * Deg);

        session.Tick(now);
    }
}
}
