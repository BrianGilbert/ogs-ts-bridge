#nullable enable
namespace OgsaTsBridge
{
/// <summary>Common surface of the UDP and shared-memory forwarders for
/// hosts that display stats (GUI status card, console status line).</summary>
public interface IForwarder
{
    ForwarderStats Stats { get; }
    LatencyMeter Latency { get; }

    /// <summary>The forward loop; blocks on the calling thread until
    /// cancelled.</summary>
    void Run(System.Threading.CancellationToken cancellation);
}

/// <summary>Builds the forwarder that matches a source's transport.</summary>
public static class Forwarders
{
    /// <summary>UDP sources get <paramref name="bindInput"/>'s socket,
    /// returned in <paramref name="input"/> so the caller owns it.</summary>
    public static IForwarder Create(Sources.ILegacySource source, OgsaSession session,
                                    System.Func<UdpInputSocket> bindInput, out UdpInputSocket? input)
    {
        input = null;
        switch (source.Transport)
        {
            case Sources.SourceTransport.SharedMemory:
                return new MmfForwarder(source, session);
            case Sources.SourceTransport.SimConnect:
                return new SimConnectForwarder((Sources.ISimConnectSource)source, session);
            default:
                input = bindInput();
                return new Forwarder(source, input, session);
        }
    }

    /// <summary>One-line description of where a source's input comes
    /// from, for status displays.</summary>
    public static string DescribeInput(Sources.ILegacySource source) => source.Transport switch
    {
        Sources.SourceTransport.SharedMemory => $"shared memory {source.SharedMemoryName}",
        Sources.SourceTransport.SimConnect => "SimConnect",
        _ => $"listening 0.0.0.0:{source.DefaultPort}",
    };
}
}
