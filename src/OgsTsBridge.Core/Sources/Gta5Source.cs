#nullable enable
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Grand Theft Auto V (single player) through the community ScriptHookVDotNet
/// script GTAScriptHookPlugin.dll from knaufinator/GTAV-Telemetry-Simtools-plugin.
/// The script is not ours to ship (the repository has no license):
/// `patch --source gta5` downloads the pinned build into the game's
/// `scripts` folder after checking its SHA-256, once ScriptHook V and
/// ScriptHookVDotNet 3 are installed.
///
/// The script sends one UTF-8 line per game tick to 127.0.0.1:20777:
///   S:f0:f1:...:f29:E   (unassigned fields are empty)
///   0 surge, 1 sway, 2 heave   — body acceleration, filtered, computed from
///                                 the vehicle's world velocity (see accScale)
///   3 pitch, 4 roll, 5 yaw     — the vehicle's Rotation X, Y, Z in degrees
///   6, 7                       — rotational acceleration X and Y
///   8 traction loss            — lateral velocity along the right vector
///   9 rpm (0..1), 10 gear, 11 speed (m/s)
///   14 steering angle, 15 wheel speed, 16 max traction, 17 throttle power,
///   18 throttle
/// While the player is out of a vehicle every field is 0, which is dropped.
///
/// The script divides the velocity change by elapsed *milliseconds*, so the
/// accelerations arrive as m/s per ms: `accScale` (default 1000) brings them
/// to m/s². Taken as gravity-free body acceleration (x right, y up, z
/// forward), with the specific force derived. The pose takes Rotation X as
/// pitch, Y as roll and Z as heading (counter-clockwise, so negated): all of
/// it unverified against the live game, with the usual `flip*` escape
/// hatches. The numbers are formatted in the player's culture, so a locale
/// with a decimal comma is not parsed.
/// </summary>
public sealed class Gta5Source : ILegacySource, IGamePatcher
{
    const double Deg = Math.PI / 180;
    internal const int FieldCount = 19;

    readonly double[] _v = new double[FieldCount];
    OgsChannel _speed, _rpm, _gear, _steer, _wheelSpeed, _maxTraction, _throttlePower,
        _throttle, _tracLoss, _rotAccX, _rotAccY;
    float _sy = -1, _sp = 1, _sr = 1;
    double _accScale = 1000;
    bool _gravIncluded;

    public string Id => "gta5";
    public string Title => "Grand Theft Auto V (ScriptHookVDotNet script)";
    public string Description =>
        "GTA V single player through the community GTAScriptHookPlugin.dll script (UDP 20777). " +
        "Needs ScriptHook V and ScriptHookVDotNet 3; `patch --source gta5` installs the script.";
    public int DefaultPort => 20777;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "GTA5" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Grand Theft Auto V", "GTA5"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate the heading sign (the default already negates GTA's counter-clockwise Z; true restores it)"),
        ("flipPitch", "negate the pitch sign (bool, default false)"),
        ("flipRoll",  "negate the roll sign (bool, default false)"),
        ("accScale",  "multiplier taking the script's acceleration to m/s² (default 1000: it divides by milliseconds)"),
        ("gravIncluded", "acceleration already includes gravity reaction (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "accScale", "gravIncluded");
        _sy = o.GetBool("flipYaw", false) ? 1 : -1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _accScale = o.GetDouble("accScale", 1000);
        _gravIncluded = o.GetBool("gravIncluded", false);
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("vehicle", Ogs.Telemetry.Schema.ObjectType.Vehicle, "GTA V vehicle");
        _speed         = session.AddChannel("vehicle.speed", "m/s", "Speed");
        _rpm           = session.AddChannel("gta5.rpm", "", "Engine rpm as a 0..1 fraction");
        _gear          = session.AddChannel("gta5.gear", "", "Current gear (raw)");
        _steer         = session.AddChannel("gta5.steeringAngle", "", "Steering angle as sent");
        _wheelSpeed    = session.AddChannel("gta5.wheelSpeed", "", "Wheel speed as sent");
        _maxTraction   = session.AddChannel("gta5.maxTraction", "", "Max traction as sent");
        _throttlePower = session.AddChannel("gta5.throttlePower", "", "Throttle power as sent");
        _throttle      = session.AddChannel("gta5.throttle", "", "Throttle as sent");
        _tracLoss      = session.AddChannel("gta5.tractionLoss", "", "Lateral velocity along the right vector (m/s)");
        _rotAccX       = session.AddChannel("gta5.rotAccel.x", "", "Rotational acceleration X as sent");
        _rotAccY       = session.AddChannel("gta5.rotAccel.y", "", "Rotational acceleration Y as sent");
    }

    public bool AcceptsDatagram(int length) => length >= FieldCount + 3;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        // "S:" ... ":E"
        if (d.Length < 2 || d[0] != (byte)'S' || d[1] != (byte)':') return;
        var rest = d.Slice(2);
        for (int i = 0; i < FieldCount; i++)
        {
            int sep = rest.IndexOf((byte)':');
            if (sep < 0) return; // short — drop
            var field = rest.Slice(0, sep);
            if (field.IsEmpty) _v[i] = 0;
            else if (!Utf8Parser.TryParse(field, out double value, out int used) || used != field.Length)
                return; // malformed — drop
            else _v[i] = value;
            rest = rest.Slice(sep + 1);
        }

        // out of a vehicle the script sends zeros
        if (_v[3] == 0 && _v[4] == 0 && _v[5] == 0) return;

        var q = OgsMath.EulerToQuaternion(_sy * _v[5] * Deg, _sp * _v[3] * Deg, _sr * _v[4] * Deg);
        session.SetPose(null, q);
        var accel = new Vector3((float)(_v[1] * _accScale), (float)(_v[2] * _accScale), (float)(_v[0] * _accScale));
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, q), Vector3.Zero);

        session.SetChannel(_speed, _v[11]);
        session.SetChannel(_rpm, _v[9]);
        session.SetChannel(_gear, _v[10]);
        session.SetChannel(_steer, _v[14]);
        session.SetChannel(_wheelSpeed, _v[15]);
        session.SetChannel(_maxTraction, _v[16]);
        session.SetChannel(_throttlePower, _v[17]);
        session.SetChannel(_throttle, _v[18]);
        session.SetChannel(_tracLoss, _v[8]);
        session.SetChannel(_rotAccX, _v[6]);
        session.SetChannel(_rotAccY, _v[7]);
        session.Tick(now);
    }

    // ---- IGamePatcher: the script into <GTA V>\scripts ----

    internal const string ScriptName = "GTAScriptHookPlugin.dll";

    // pinned to a commit and a hash: the script runs inside the game
    internal const string ScriptUrl =
        "https://raw.githubusercontent.com/knaufinator/GTAV-Telemetry-Simtools-plugin/" +
        "c20f99e607b731033f19119c1b954be3af2df44d/Compiled/GTAV%20Scripthookvdotnet%20Script/" + ScriptName;
    internal const string ScriptSha256 = "4246baeb5d11520ad7609450ef9a7de9d881b738f0ee2992e15c4c4e8018858b";

    static readonly string[] InstallFolders = { "Grand Theft Auto V" };

    public IList<string> PatchGameTelemetry(int port)
    {
        var dir = SteamLibrary.FindGame(InstallFolders);
        return dir is null ? new List<string>() : InstallScript(dir, Download);
    }

    static byte[] Download()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        return http.GetByteArrayAsync(ScriptUrl).GetAwaiter().GetResult();
    }

    /// <summary>Puts the script into &lt;game&gt;\scripts. ScriptHook V
    /// (ScriptHookV.dll) and ScriptHookVDotNet 3 (ScriptHookVDotNet3.dll)
    /// must already be in the game folder, or this throws and says which is
    /// missing. The download must match the pinned SHA-256 or nothing is
    /// written; an identical file already in place is left alone.</summary>
    public static IList<string> InstallScript(string gameDir, Func<byte[]> fetch)
    {
        foreach (var dep in new[] { "ScriptHookV.dll", "ScriptHookVDotNet3.dll" })
            if (!File.Exists(Path.Combine(gameDir, dep)))
                throw new FileNotFoundException(
                    $"{dep} not found in {gameDir} — install ScriptHook V (dev-c.com/gtav/scripthookv) and " +
                    "ScriptHookVDotNet 3 (github.com/scripthookvdotnet/scripthookvdotnet/releases) first.");

        var dir = Path.Combine(gameDir, "scripts");
        var path = Path.Combine(dir, ScriptName);
        var installed = new List<string>();
        if (File.Exists(path) && Hash(File.ReadAllBytes(path)) == ScriptSha256)
        {
            installed.Add(path);
            return installed;
        }
        var bytes = fetch();
        if (Hash(bytes) != ScriptSha256)
            throw new InvalidDataException(
                $"{ScriptName} did not match the expected SHA-256 — not installed (download corrupted or the file changed).");
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        installed.Add(path);
        return installed;
    }

    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
}
}
