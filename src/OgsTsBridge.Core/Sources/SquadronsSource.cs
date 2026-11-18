#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Star Wars: Squadrons by reading the game's process memory: the game has
/// no telemetry output. Five pointer chains (speed, forward acceleration,
/// pitch, roll, yaw) are built in for the final game build, which no longer
/// updates; each is read as a float from the main module (see
/// <see cref="ProcessMemory"/>).
///
/// Every value is published raw as "squadrons.&lt;name&gt;". With `motion`
/// on, pitch and roll are taken as radians, yaw as a per-sample turn
/// increment (the game exposes no absolute heading, so it is integrated
/// and wraps), and the forward acceleration as surge in m/s² — all
/// unverified against the live game, so off by default.
///
/// Read-only. Easy Anti-Cheat refuses reads of a protected process; the
/// Bridge never disables it, so a game started with it active simply never
/// connects.
/// </summary>
public sealed class SquadronsSource : IPolledSource
{
    static readonly string[] Names = { "speed", "acceleration", "pitch", "roll", "yaw" };

    static readonly long[][] Chains =
    {
        new long[] { 0x0423A270, 0xA0, 0x48, 0x10, 0x230, 0x10, 0x58, 0x28C },
        new long[] { 0x042478B0, 0x18, 0x20, 0x0, 0x40 },
        new long[] { 0x03E9FA18, 0x40, 0xC0, 0x88, 0x68, 0x3C0 },
        new long[] { 0x03E9FA18, 0x40, 0xC0, 0x88, 0x68, 0x3CC },
        new long[] { 0x03E9FA18, 0x40, 0xC0, 0x88, 0x68, 0x3C4 },
    };

    const int Speed = 0, Accel = 1, Pitch = 2, Roll = 3, Yaw = 4;

    readonly ProcessMemory _mem = new();
    OgsChannel[] _channels = Array.Empty<OgsChannel>();
    bool _motion;
    int _pollMs = 20;
    double _yawAccum;

    public string Id => "squadrons";
    public string Title => "Star Wars: Squadrons (process memory)";
    public string Description =>
        "Star Wars: Squadrons read from process memory through built-in pointer " +
        "chains for the final game build. Needs the game running without Easy Anti-Cheat.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "starwarssquadrons", "starwarssquadrons_launcher" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Star Wars: Squadrons", "starwarssquadrons"),
        new LegacyGame("Star Wars: Squadrons (launcher-swapped)", "starwarssquadrons_launcher"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("motion", "drive pose and surge from pitch/roll/yaw/acceleration (bool, default false; unverified)"),
        ("pollMs", "poll interval in ms (default 20)"),
    };

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("motion", "pollMs");
        _motion = o.GetBool("motion", false);
        _pollMs = Math.Clamp((int)o.GetDouble("pollMs", 20), 2, 1000);
    }

    public SourceTransport Transport => SourceTransport.Poll;
    public int PollIntervalMs => _pollMs;
    public int PollBufferSize => Chains.Length * 4;
    public string PollTarget => "starwarssquadrons.exe";

    public bool Connect()
    {
        Disconnect();
        _yawAccum = 0;
        return _mem.Open("starwarssquadrons", "starwarssquadrons_launcher");
    }

    public void Disconnect() => _mem.Close();

    /// <summary>One float per chain, in <see cref="Names"/> order (NaN where
    /// a chain does not resolve right now, e.g. in menus).</summary>
    public int Read(byte[] buffer)
    {
        if (!_mem.IsAlive()) return -1;
        for (int i = 0; i < Chains.Length; i++)
            MemoryMarshal.Write(buffer.AsSpan(i * 4), _mem.Resolve(Chains[i]));
        return Chains.Length * 4;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("ship", Ogs.Telemetry.Schema.ObjectType.Spacecraft, "Star Wars: Squadrons starfighter");
        session.SendChangedChannelsOnly();
        _channels = new OgsChannel[Names.Length];
        for (int i = 0; i < Names.Length; i++)
            _channels[i] = session.AddChannel("squadrons." + Names[i], "", $"'{Names[i]}' read from game memory (raw)");
    }

    public bool AcceptsDatagram(int length) => length >= Chains.Length * 4;

    static float V(ReadOnlySpan<byte> d, int index) =>
        index * 4 + 4 <= d.Length ? MemoryMarshal.Read<float>(d.Slice(index * 4, 4)) : float.NaN;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        for (int i = 0; i < _channels.Length; i++)
        {
            float v = V(d, i);
            if (!float.IsNaN(v)) session.SetChannel(_channels[i], v);
        }
        if (!_motion) { session.Tick(now); return; }

        float pitch = V(d, Pitch), roll = V(d, Roll), yaw = V(d, Yaw);
        if (!float.IsNaN(pitch) && !float.IsNaN(roll))
        {
            if (!float.IsNaN(yaw) && Math.Abs(yaw) < 1f)
            {
                _yawAccum += yaw;
                if (_yawAccum > Math.PI) _yawAccum -= 2 * Math.PI;
                else if (_yawAccum < -Math.PI) _yawAccum += 2 * Math.PI;
            }
            session.SetPose(null, OgsMath.EulerToQuaternion(_yawAccum, pitch, roll));
            float accel = V(d, Accel);
            if (!float.IsNaN(accel))
                session.SetBodyDynamics(new Vector3(0, 0, Math.Clamp(accel, -100f, 100f)), Vector3.Zero);
        }
        session.Tick(now);
    }
}
}
