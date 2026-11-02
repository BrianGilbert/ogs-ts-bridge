#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// X-Plane 11/12 "DATA" UDP output (Settings → Data Output → Network:
/// tick "Send network data output" for the rows below, IP = this machine,
/// port = 49100). One format, both versions.
///
/// Datagram: "DATA" + one byte, then 36-byte rows — int32 row index and
/// eight float32 values. Rows read (X-Plane's Data Output index):
/// 3 speeds (kias @0, ktas @2, ktgs @3), 4 Mach/VVI/g-load (Mach @0, VVI
/// fpm @2, g normal @4, axial @5, side @6), 16 angular velocities (Q, P,
/// R rad/s @0..2), 17 pitch/roll/heading (deg, true heading @2), 18 alpha
/// @0 / beta @1 (deg), 20 lat/lon/alt (deg, ft MSL @2, ft AGL @3), 37
/// engine rpm (engine 1 @0). Enable at least 4, 16 and 17 for motion.
/// Rows a datagram omits keep their last value.
///
/// Mapping: X-Plane's attitude and body rates are aerospace-standard
/// (+pitch nose-up, +roll right-wing-down, +heading clockwise; Q pitch, P
/// roll, R yaw) and so match OGS signs directly. The g-loads are
/// accelerometer-style load factors (gravity included): specific force =
/// (side, normal, axial) × g — axial assumed + forward, side + right
/// (unverified against the live sim — `flipAxial`/`flipSide` and the Euler
/// flips exist).
/// </summary>
public sealed class XPlaneSource : ILegacySource
{
    const float Gravity = 9.80665f;
    const double Deg = Math.PI / 180.0;
    const double Kt = 0.514444;
    const double Ft = 0.3048;

    OgsChannel _ias, _tas, _gs, _mach, _vvi, _aoa, _beta, _load, _lat, _lon, _alt, _agl, _rpm;
    double _sy = 1, _sp = 1, _sr = 1;
    float _axial = 1, _side = 1;

    // last value of every row we read — DATA datagrams carry only the selected rows
    float _pitch, _roll, _heading, _q, _p, _r, _gNormal = 1, _gAxial, _gSide;

    public string Id => "xplane";
    public string Title => "X-Plane 11/12 (DATA output)";
    public string Description =>
        "X-Plane Data Output over UDP (port 49100). In the sim: Settings → " +
        "Data Output, tick the Network column for rows 3, 4, 16, 17, 18, 20 " +
        "and 37, send to this machine's IP on port 49100.";
    public int DefaultPort => 49100;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "X-Plane" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("X-Plane 11/12", "X-Plane"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate heading + yaw-rate sign (bool, default false)"),
        ("flipPitch", "negate pitch + pitch-rate sign (bool, default false)"),
        ("flipRoll",  "negate roll + roll-rate sign (bool, default false)"),
        ("flipAxial", "negate the axial g-load (bool, default false)"),
        ("flipSide",  "negate the side g-load (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "flipAxial", "flipSide");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _axial = o.GetBool("flipAxial", false) ? -1 : 1;
        _side = o.GetBool("flipSide", false) ? -1 : 1;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("aircraft", Ogs.Telemetry.Schema.ObjectType.Aircraft, "X-Plane aircraft");
        session.DeclareGravity(Gravity);
        _ias  = session.AddChannel("air.indicatedAirspeed", "m/s", "Indicated airspeed");
        _tas  = session.AddChannel("air.trueAirspeed", "m/s", "True airspeed");
        _gs   = session.AddChannel("vehicle.speed", "m/s", "Ground speed");
        _mach = session.AddChannel("air.mach", "", "Mach number");
        _vvi  = session.AddChannel("air.verticalSpeed", "m/s", "Vertical speed");
        _aoa  = session.AddChannel("air.aoa", "rad", "Angle of attack");
        _beta = session.AddChannel("air.beta", "rad", "Sideslip angle");
        _load = session.AddChannel("air.loadFactor", "", "Normal load factor (g)");
        _lat  = session.AddChannel("position.lat", "rad", "Latitude");
        _lon  = session.AddChannel("position.lon", "rad", "Longitude");
        _alt  = session.AddChannel("position.altMsl", "m", "Altitude above mean sea level");
        _agl  = session.AddChannel("env.heightFromGround", "m", "Height above ground");
        _rpm  = session.AddChannel("engine.rpm", "rpm", "Engine 1 rpm");
    }

    public bool AcceptsDatagram(int length) => length >= 5 + 36 && (length - 5) % 36 == 0;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        if (d[0] != (byte)'D' || d[1] != (byte)'A' || d[2] != (byte)'T' || d[3] != (byte)'A') return;

        for (int row = 5; row + 36 <= d.Length; row += 36)
        {
            int v = row + 4; // first of the row's eight values
            switch (MemoryMarshal.Read<int>(d.Slice(row, 4)))
            {
                case 3:
                    session.SetChannel(_ias, F(d, v) * Kt);
                    session.SetChannel(_tas, F(d, v + 8) * Kt);
                    session.SetChannel(_gs, F(d, v + 12) * Kt);
                    break;
                case 4:
                    session.SetChannel(_mach, F(d, v));
                    session.SetChannel(_vvi, F(d, v + 8) * Ft / 60.0);
                    _gNormal = F(d, v + 16); _gAxial = F(d, v + 20); _gSide = F(d, v + 24);
                    session.SetChannel(_load, _gNormal);
                    break;
                case 16:
                    _q = F(d, v); _p = F(d, v + 4); _r = F(d, v + 8);
                    break;
                case 17:
                    _pitch = F(d, v); _roll = F(d, v + 4); _heading = F(d, v + 8);
                    break;
                case 18:
                    session.SetChannel(_aoa, F(d, v) * Deg);
                    session.SetChannel(_beta, F(d, v + 4) * Deg);
                    break;
                case 20:
                    session.SetChannel(_lat, F(d, v) * Deg);
                    session.SetChannel(_lon, F(d, v + 4) * Deg);
                    session.SetChannel(_alt, F(d, v + 8) * Ft);
                    session.SetChannel(_agl, F(d, v + 12) * Ft);
                    break;
                case 37:
                    session.SetChannel(_rpm, F(d, v));
                    break;
            }
        }

        session.SetPose(null, OgsMath.EulerToQuaternion(
            _sy * _heading * Deg, _sp * _pitch * Deg, _sr * _roll * Deg));
        session.SetBodyDynamics(
            new Vector3(_side * _gSide, _gNormal, _axial * _gAxial) * Gravity,
            new Vector3((float)(_sp * _q), (float)(_sy * _r), (float)(_sr * _p)));

        session.Tick(now);
    }
}
}
