#nullable enable

namespace OgsTsBridge.Sources
{
/// <summary>
/// A source that fetches each sample itself (<see cref="SourceTransport.Poll"/>):
/// the game answers requests (a local HTTP or TCP server) or exposes
/// nothing at all (process memory). The poll forwarder calls
/// <see cref="Connect"/> until the game is reachable, then
/// <see cref="Read"/> every <see cref="ILegacySource.PollIntervalMs"/>,
/// handing each sample to <see cref="ILegacySource.Translate"/>.
///
/// Connect and Disconnect are cold paths and may allocate. Read is the
/// packet path: it reuses its sockets, handles and buffers and must not
/// allocate once connected.
/// </summary>
public interface IPolledSource : ILegacySource
{
    /// <summary>Bytes of one sample (the buffer passed to <see cref="Read"/>).</summary>
    int PollBufferSize { get; }

    /// <summary>What the forwarder waits for while <see cref="Connect"/>
    /// fails, for status displays (e.g. "War Thunder on 127.0.0.1:8111").</summary>
    string PollTarget { get; }

    /// <summary>Tries to reach the game; false when it is not up yet (the
    /// forwarder retries every ~0.5 s).</summary>
    bool Connect();

    /// <summary>Fetches one sample: its length (&gt; 0) to translate, 0 for
    /// nothing new, or −1 when the game went away (the forwarder then
    /// calls <see cref="Disconnect"/> and reconnects).</summary>
    int Read(byte[] buffer);

    void Disconnect();
}
}
