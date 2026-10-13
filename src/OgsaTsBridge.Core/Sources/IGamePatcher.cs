#nullable enable
using System.Collections.Generic;

namespace OgsaTsBridge.Sources
{
/// <summary>Optional capability for sources whose game needs a config-file
/// edit to start emitting telemetry (e.g. DiRT Rally 2 has no in-game
/// switch).</summary>
public interface IGamePatcher
{
    /// <summary>Enables the game's telemetry output for the given UDP
    /// port. Returns the files changed (empty = no config found — the
    /// game may need to run once to create them).</summary>
    IList<string> PatchGameTelemetry(int port);
}
}
