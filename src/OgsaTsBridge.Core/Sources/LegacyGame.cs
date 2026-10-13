#nullable enable
namespace OgsaTsBridge.Sources
{
/// <summary>One game covered by a source — the user-facing identity.
/// Every executable a source watches must have an entry here so the GUI
/// can present games (not telemetry formats) and name the active one.</summary>
public sealed record LegacyGame(string DisplayName, string ProcessName);
}
