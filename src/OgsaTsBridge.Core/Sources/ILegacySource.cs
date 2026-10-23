#nullable enable
using System.Collections.Generic;
using OgsaTsBridge;

namespace OgsaTsBridge.Sources
{
/// <summary>How a source receives telemetry from the game.</summary>
public enum SourceTransport
{
    /// <summary>The game pushes UDP datagrams — event-driven, the forwarder
    /// thread wakes on arrival (zero added latency).</summary>
    Udp,
    /// <summary>The game writes a Windows memory-mapped file — the
    /// forwarder polls it at <see cref="PollIntervalMs"/> (sub-tick added
    /// latency, bounded by the poll interval).</summary>
    SharedMemory,
    /// <summary>The source is a SimConnect client (<see cref="ISimConnectSource"/>):
    /// the sim pushes data per frame and signals an event handle, so the
    /// forwarder thread wakes on arrival.</summary>
    SimConnect,
}

/// <summary>
/// One legacy game's telemetry input, translated to OGSA-TS.
///
/// Lifecycle: Configure (apply options) -> Declare (register object,
/// wheels, channels with the session) -> the Forwarder then calls
/// AcceptsDatagram + Translate on its receive thread for every arriving
/// datagram. Translate must not allocate: write into the session with
/// SetPose/SetKinematics/SetBodyDynamics/SetChannel/SetWheel and finish
/// with session.Tick(now) exactly once per forwarded packet.
///
/// See docs/PLUGINS.md for the full guide, including the shared-memory
/// variant and the testing pattern.
/// </summary>
public interface ILegacySource
{
    string Id { get; }
    string Title { get; }
    string Description { get; }

    /// <summary>Default UDP port the game sends to.</summary>
    int DefaultPort { get; }

    /// <summary>Executable names (without .exe) that indicate this game is
    /// running — used by the auto supervisor's process watch (works for
    /// non-Steam games; no telemetry probing).</summary>
    IReadOnlyList<string> ProcessNames { get; }

    /// <summary>User-facing game catalogue — one entry per covered game,
    /// each binding a display name to the executable the watcher matches.
    /// Must cover every <see cref="ProcessNames"/> entry (verified by
    /// tests) so the GUI can offer a game-name picker and name the active
    /// game on detection.</summary>
    IReadOnlyList<LegacyGame> Games { get; }

    /// <summary>Source-specific options and their help text, shown by
    /// `ogsa-ts-bridge list &lt;id&gt;` and set with `--set key=value`.</summary>
    IReadOnlyList<(string Key, string Help)> Options { get; }

    void Configure(IReadOnlyDictionary<string, string> options);

    /// <summary>Registers the object, drive points, and channels that make
    /// up the DiscoveryFrame. Called once before the run loop starts.</summary>
    void Declare(OgsaSession session);

    /// <summary>Cheap datagram validation (length / magic). Anything that
    /// fails is counted and dropped.</summary>
    bool AcceptsDatagram(int length);

    /// <summary>Parses and translates one packet, then ticks the session.
    /// Called from the forwarder thread only. Shared-memory sources receive
    /// a snapshot of the mapped page and should return without ticking
    /// when the page has not changed since the last call.</summary>
    void Translate(System.ReadOnlySpan<byte> datagram, OgsaSession session, double now);

    // ---- transport (defaults keep UDP sources one-concern) ----

    /// <summary>Input transport for this game. UDP sources leave this at
    /// the default; shared-memory sources override it and the two map
    /// names below.</summary>
    SourceTransport Transport => SourceTransport.Udp;

    /// <summary>Memory-mapped file name for SharedMemory sources (e.g.
    /// "Local\acpmf_physics").</summary>
    string SharedMemoryName => "";

    /// <summary>Bytes of the mapped page to snapshot per poll (the prefix
    /// this source reads).</summary>
    int SharedMemorySize => 0;

    /// <summary>Shared-memory poll interval in milliseconds. Should be
    /// comfortably below the game's own write cadence (e.g. 2 ms against
    /// AC's 333 Hz physics).</summary>
    int PollIntervalMs => 2;
}
}
