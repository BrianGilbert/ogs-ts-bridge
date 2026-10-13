#nullable enable
using System.Collections.Generic;
using OgsaTsBridge;

namespace OgsaTsBridge.Sources
{
/// <summary>
/// One legacy game's telemetry input, translated to OGSA-TS.
///
/// Lifecycle: Configure (apply options) -> Declare (register object,
/// wheels, channels with the session) -> the Forwarder then calls
/// AcceptsDatagram + Translate on its receive thread for every arriving
/// datagram. Translate must not allocate: write into the session with
/// SetPose/SetKinematics/SetBodyDynamics/SetChannel/SetWheel and finish
/// with session.Tick(now) exactly once per forwarded packet.
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

    /// <summary>Parses and translates one datagram, then ticks the session.
    /// Called from the receive thread only.</summary>
    void Translate(System.ReadOnlySpan<byte> datagram, OgsaSession session, double now);
}
}
