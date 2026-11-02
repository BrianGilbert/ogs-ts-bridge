#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace OgsTsBridge.Sources
{
/// <summary>
/// The Crew 2 physics output ("phys_out_sim"), switched on by an
/// ExtraConfig.xml in Documents\The Crew 2 (the patcher writes it). Cars
/// and motorbikes only.
///
/// Datagram (68 bytes, little-endian): u32 time @0, angular velocity
/// (pitch, roll, yaw) rad/s @4/8/12, orientation (yaw, pitch, roll) rad
/// @16/20/24, acceleration (lateral, longitudinal, vertical) @28/32/36,
/// velocity xyz @40/44/48, position xyz @52/56/60 (float, or u32 in older
/// readers), game id @64.
///
/// Mapping: Euler and rates assumed in the OGS senses (+pitch up, +yaw
/// right, +roll right) and the acceleration taken as m/s², gravity-free,
/// with the specific force derived — all unverified against the live game
/// (flip options and `gravIncluded` exist). Velocity and position are
/// published as channels in the game's frame.
/// </summary>
public sealed class TheCrew2Source : ILegacySource, IGamePatcher
{
    internal const int PacketSize = 68;

    OgsChannel _time, _vx, _vy, _vz, _px, _py, _pz, _gameId, _speed;
    double _sy = 1, _sp = 1, _sr = 1;
    bool _gravIncluded;

    public string Id => "thecrew2";
    public string Title => "The Crew 2 (physics output)";
    public string Description =>
        "The Crew 2 phys_out_sim UDP (port 1337). The patch writes " +
        "Documents\\The Crew 2\\ExtraConfig.xml pointing it at this machine.";
    public int DefaultPort => 1337;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "TheCrew2" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("The Crew 2", "TheCrew2"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("flipYaw",      "negate yaw + yaw-rate sign (bool, default false)"),
        ("flipPitch",    "negate pitch + pitch-rate sign (bool, default false)"),
        ("flipRoll",     "negate roll + roll-rate sign (bool, default false)"),
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
        session.DeclareObject("vehicle", Ogs.Telemetry.Schema.ObjectType.Vehicle, "The Crew 2 vehicle");
        _time   = session.AddChannel("thecrew2.time", "", "Packet time (raw u32)");
        _vx     = session.AddChannel("thecrew2.velocity.x", "m/s", "Velocity X (game frame)");
        _vy     = session.AddChannel("thecrew2.velocity.y", "m/s", "Velocity Y (game frame)");
        _vz     = session.AddChannel("thecrew2.velocity.z", "m/s", "Velocity Z (game frame)");
        _speed  = session.AddChannel("vehicle.speed", "m/s", "Speed (velocity magnitude)");
        _px     = session.AddChannel("thecrew2.position.x", "m", "Position X (game frame)");
        _py     = session.AddChannel("thecrew2.position.y", "m", "Position Y (game frame)");
        _pz     = session.AddChannel("thecrew2.position.z", "m", "Position Z (game frame)");
        _gameId = session.AddChannel("thecrew2.gameId", "", "Game id (raw u32)");
    }

    public bool AcceptsDatagram(int length) => length >= PacketSize;

    static float F(ReadOnlySpan<byte> d, int off) => MemoryMarshal.Read<float>(d.Slice(off, 4));

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        var q = OgsMath.EulerToQuaternion(_sy * F(d, 16), _sp * F(d, 20), _sr * F(d, 24));
        session.SetPose(null, q);
        var accel = new Vector3(F(d, 28), F(d, 36), F(d, 32));
        var rates = new Vector3((float)(_sp * F(d, 4)), (float)(_sy * F(d, 12)), (float)(_sr * F(d, 8)));
        session.SetBodyDynamics(_gravIncluded ? accel : OgsMath.SpecificForceFromLinear(accel, q), rates);

        var v = new Vector3(F(d, 40), F(d, 44), F(d, 48));
        session.SetChannel(_time, MemoryMarshal.Read<uint>(d));
        session.SetChannel(_vx, v.X);
        session.SetChannel(_vy, v.Y);
        session.SetChannel(_vz, v.Z);
        session.SetChannel(_speed, v.Length());
        session.SetChannel(_px, F(d, 52));
        session.SetChannel(_py, F(d, 56));
        session.SetChannel(_pz, F(d, 60));
        session.SetChannel(_gameId, MemoryMarshal.Read<uint>(d.Slice(64, 4)));
        session.Tick(now);
    }

    // ---- IGamePatcher: Documents\The Crew 2\ExtraConfig.xml ----

    public IList<string> PatchGameTelemetry(int port) =>
        PatchDocuments(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), port);

    /// <summary>Sets phys_out_sim="127.0.0.1:port" on the ExtraConfig
    /// element of Documents\The Crew 2\ExtraConfig.xml (created when
    /// absent, other attributes kept, one-time .bak-ogs-ts-bridge backup).
    /// Needs the game's Documents folder (run the game once).</summary>
    public static IList<string> PatchDocuments(string documentsRoot, int port)
    {
        var patched = new List<string>();
        var dir = Path.Combine(documentsRoot, "The Crew 2");
        if (!Directory.Exists(dir)) return patched;
        var path = Path.Combine(dir, "ExtraConfig.xml");
        var target = $"127.0.0.1:{port}";

        XDocument doc;
        if (File.Exists(path))
        {
            doc = XDocument.Load(path);
            if (doc.Root?.Name.LocalName != "ExtraConfig") return patched; // not ours to rewrite
            if ((string?)doc.Root.Attribute("phys_out_sim") == target) return patched;
            var backup = path + ".bak-ogs-ts-bridge";
            if (!File.Exists(backup)) File.Copy(path, backup);
        }
        else doc = new XDocument(new XElement("ExtraConfig"));

        doc.Root!.SetAttributeValue("phys_out_sim", target);
        doc.Save(path);
        patched.Add(path);
        return patched;
    }
}
}
