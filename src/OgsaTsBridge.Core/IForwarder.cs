#nullable enable
namespace OgsaTsBridge
{
/// <summary>Common surface of the UDP and shared-memory forwarders for
/// hosts that display stats (GUI status card, console status line).</summary>
public interface IForwarder
{
    ForwarderStats Stats { get; }
    LatencyMeter Latency { get; }
}
}
