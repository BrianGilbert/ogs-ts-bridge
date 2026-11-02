#nullable enable
using System.Collections.Generic;

namespace OgsTsBridge.Sources
{
/// <summary>
/// A UDP source that subscribes to its game's data with several request
/// datagrams (X-Plane RREF: one per dataref). The forwarder sends every
/// <see cref="RequestPayloads"/> datagram from the input socket to
/// <see cref="RequestTarget"/> at start and every
/// <see cref="RequestIntervalSeconds"/> (re-subscribing after a game
/// restart), so replies come back to the port it listens on, and sends
/// <see cref="StopPayloads"/> when it stops.
/// </summary>
public interface IUdpRequests
{
    /// <summary>Where to send, or null when not configured (no requests).</summary>
    System.Net.IPEndPoint? RequestTarget { get; }

    IReadOnlyList<byte[]> RequestPayloads { get; }

    /// <summary>Datagrams that cancel the subscriptions; may be empty.</summary>
    IReadOnlyList<byte[]> StopPayloads { get; }

    double RequestIntervalSeconds { get; }
}
}
