#nullable enable

namespace OgsTsBridge.Sources
{
/// <summary>
/// A UDP source whose game only streams while it hears from the receiver
/// (GT7 stops after ~10 s without a heartbeat). The forwarder sends
/// <see cref="HeartbeatPayload"/> from the input socket to
/// <see cref="HeartbeatTarget"/> at start and every
/// <see cref="HeartbeatIntervalSeconds"/>, so replies come back to the
/// port it listens on.
/// </summary>
public interface IUdpHeartbeat
{
    /// <summary>Where to send, or null when not configured (no heartbeat).</summary>
    System.Net.IPEndPoint? HeartbeatTarget { get; }

    byte[] HeartbeatPayload { get; }

    double HeartbeatIntervalSeconds { get; }
}
}
