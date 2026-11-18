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
/// Aerofly FS 2 through the community telemetry DLL
/// (Aerofly_FS_2_GamePlugin_Telemetry.dll from ashupp/AeroflyFS2TelemetryProvider,
/// GPL-3.0; the Bridge does not ship it: `patch --source aerofly` downloads
/// the pinned release into the game's external_dll folder after checking its
/// SHA-256, or the user places it there by hand). The DLL sends one UTF-8 line per datagram to
/// 127.0.0.1:4123, fields separated by ';' (at least 11):
///   0 pitch, 1 roll, 2 yaw     — radians × 1000
///   3 sway, 8 heave            — × 1000 (heave sign: positive is down)
///   5 surge                    — as sent
///   9 air speed, 10 ground speed
/// Fields 4, 6 and 7 are not used.
///
/// The angles map direct as OGS yaw / pitch / roll (signs unverified, with
/// the usual `flip*` escape hatches). Sway, heave (negated to up) and surge
/// are taken as body-frame linear acceleration in m/s², gravity-free, with
/// the specific force derived (unverified: `gravIncluded` overrides). The
/// speeds ride channels raw, units unconfirmed.
/// </summary>
public sealed class AeroflySource : ILegacySource, IGamePatcher
{
    internal const int FieldCount = 11;

    readonly double[] _v = new double[FieldCount];
    OgsChannel _airSpeed, _groundSpeed;
    float _sy = 1, _sp = 1, _sr = 1;
    bool _gravIncluded;

    public string Id => "aerofly";
    public string Title => "Aerofly FS 2 (telemetry DLL)";
    public string Description =>
        "Aerofly FS 2 through the community Aerofly_FS_2_GamePlugin_Telemetry.dll " +
        "(github.com/ashupp/AeroflyFS2TelemetryProvider), placed in Documents\\Aerofly FS 2\\external_dll.";
    public int DefaultPort => 4123;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "aerofly_fs_2" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Aerofly FS 2", "aerofly_fs_2"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",   "negate the yaw sign (bool, default false)"),
        ("flipPitch", "negate the pitch sign (bool, default false)"),
        ("flipRoll",  "negate the roll sign (bool, default false)"),
        ("gravIncluded", "acceleration already includes gravity reaction (bool, default false)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "gravIncluded");
        _sy = o.GetBool("flipYaw", false) ? -1 : 1;
        _sp = o.GetBool("flipPitch", false) ? -1 : 1;
        _sr = o.GetBool("flipRoll", false) ? -1 : 1;
        _gravIncluded = o.GetBool("gravIncluded", false);
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("aircraft", Ogs.Telemetry.Schema.ObjectType.Aircraft, "Aerofly FS 2 aircraft");
        _airSpeed    = session.AddChannel("aerofly.airSpeed", "", "Air speed as sent (unit unconfirmed)");
        _groundSpeed = session.AddChannel("aerofly.groundSpeed", "", "Ground speed as sent (unit unconfirmed)");
    }

    public bool AcceptsDatagram(int length) => length >= FieldCount * 2 - 1;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        var rest = d;
        for (int i = 0; i < FieldCount; i++)
        {
            int sep = rest.IndexOf((byte)';');
            var field = sep < 0 ? rest : rest.Slice(0, sep);
            // the last field may carry a line ending
            while (!field.IsEmpty && (field[^1] == (byte)'\r' || field[^1] == (byte)'\n' || field[^1] == 0))
                field = field.Slice(0, field.Length - 1);
            if (!Utf8Parser.TryParse(field, out double value, out int used) || used != field.Length)
                return; // malformed — drop
            _v[i] = value;
            if (sep < 0) { if (i != FieldCount - 1) return; break; }
            rest = rest.Slice(sep + 1);
        }

        var q = OgsMath.EulerToQuaternion(_sy * _v[2] / 1000, _sp * _v[0] / 1000, _sr * _v[1] / 1000);
        session.SetPose(null, q);
        var accel = new Vector3((float)(_v[3] / 1000), (float)(-_v[8] / 1000), (float)_v[5]);
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, q), Vector3.Zero);
        session.SetChannel(_airSpeed, _v[9]);
        session.SetChannel(_groundSpeed, _v[10]);
        session.Tick(now);
    }

    // ---- IGamePatcher: the telemetry DLL into Documents\Aerofly FS 2\external_dll ----

    internal const string DllName = "Aerofly_FS_2_GamePlugin_Telemetry.dll";

    // pinned to a commit and a hash: the DLL is loaded into the game, so a
    // moved branch or a swapped file must not be picked up silently
    internal const string DllUrl =
        "https://raw.githubusercontent.com/ashupp/AeroflyFS2TelemetryProvider/" +
        "b4677400e68322a8205e84f2495c50c527526add/" + DllName;
    internal const string DllSha256 = "755dc84583b9f177e1f8943e4ad0ae55f2d2075e82bbaabb1e3e94d20e8eef16";

    public IList<string> PatchGameTelemetry(int port) =>
        InstallDll(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Download);

    static byte[] Download()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        return http.GetByteArrayAsync(DllUrl).GetAwaiter().GetResult();
    }

    /// <summary>Puts the telemetry DLL into Documents\Aerofly FS 2\external_dll
    /// (the folder is created; the game's Documents folder must exist, so
    /// run the game once). The download must match the pinned SHA-256 or
    /// nothing is written; an identical file already in place is left alone.
    /// Returns the DLL's path, or nothing when the game's folder is missing.</summary>
    public static IList<string> InstallDll(string documentsRoot, Func<byte[]> fetch)
    {
        var installed = new List<string>();
        var game = Path.Combine(documentsRoot, "Aerofly FS 2");
        if (!Directory.Exists(game)) return installed;
        var dir = Path.Combine(game, "external_dll");
        var path = Path.Combine(dir, DllName);

        if (File.Exists(path) && Hash(File.ReadAllBytes(path)) == DllSha256)
        {
            installed.Add(path);
            return installed;
        }
        var bytes = fetch();
        if (Hash(bytes) != DllSha256)
            throw new InvalidDataException(
                $"{DllName} did not match the expected SHA-256 — not installed (download corrupted or the file changed).");
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
