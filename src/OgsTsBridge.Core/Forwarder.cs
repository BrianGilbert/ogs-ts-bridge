#nullable enable
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OgsTsBridge.Sources;

namespace OgsTsBridge
{
/// <summary>
/// The translate-and-forward loop. One thread, start to finish:
///
///   kernel wake-up on datagram arrival
///     -> zero-copy struct parse (source.Translate)
///        -> cached-graph rewrite + Cap'n Proto encode (OgsSession.Tick)
///           -> UDP SendTo
///
/// Nothing between the receive syscall and the send syscall: no queues, no
/// Tasks, no locks on values, no rate shaping. The only work added to the
/// path is the per-tick latency sample (Stopwatch, two reads, ring write).
/// </summary>
public sealed class Forwarder : IForwarder
{
    readonly ILegacySource _source;
    readonly UdpInputSocket _input;
    readonly OgsSession _session;
    // the largest IPv4 UDP payload: text formats (JSON) run past an MTU
    readonly byte[] _buffer = new byte[65536];
    readonly LatencyMeter _latency = new();
    readonly IUdpHeartbeat? _heartbeat;
    double _lastHeartbeat = double.NegativeInfinity;
    readonly IUdpRequests? _requests;
    double _lastRequests = double.NegativeInfinity;

    public Forwarder(ILegacySource source, UdpInputSocket input, OgsSession session)
    {
        _source = source;
        _input = input;
        _session = session;
        if (source.MulticastGroup.Length > 0)
            input.JoinMulticast(IPAddress.Parse(source.MulticastGroup));
        if (source is IUdpHeartbeat { HeartbeatTarget: not null } heartbeat)
            _heartbeat = heartbeat;
        if (source is IUdpRequests { RequestTarget: not null } requests)
            _requests = requests;
    }

    /// <summary>Sends the source's subscription requests when they are due.</summary>
    void Requests()
    {
        if (_requests is null) return;
        double now = Now();
        if (now - _lastRequests < _requests.RequestIntervalSeconds) return;
        _lastRequests = now;
        foreach (var payload in _requests.RequestPayloads)
            _input.Send(payload, _requests.RequestTarget!);
    }

    void StopRequests()
    {
        if (_requests is null || _input.Closed) return;
        try
        {
            foreach (var payload in _requests.StopPayloads)
                _input.Send(payload, _requests.RequestTarget!);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
    }

    /// <summary>Sends the source's heartbeat when one is due. Checked on
    /// every wake-up; the receive timeout bounds the gap while the game is
    /// silent.</summary>
    void Heartbeat()
    {
        if (_heartbeat is null) return;
        double now = Now();
        if (now - _lastHeartbeat < _heartbeat.HeartbeatIntervalSeconds) return;
        _lastHeartbeat = now;
        _input.Send(_heartbeat.HeartbeatPayload, _heartbeat.HeartbeatTarget!);
    }

    public ForwarderStats Stats { get; } = new();
    public LatencyMeter Latency => _latency;

    /// <summary>Packet->send processing latency, measured from receive-syscall
    /// return to send-syscall return. Blocks until Ctrl+C.</summary>
    public void Run(CancellationToken cancellation)
    {
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        _session.Start(Now());
        _input.ReceiveTimeoutMs = 500;
        Stats.Started = true;

        try { RunLoop(cancellation, from); }
        finally { StopRequests(); }
    }

    void RunLoop(CancellationToken cancellation, EndPoint from)
    {
        while (!cancellation.IsCancellationRequested)
        {
            Heartbeat();
            Requests();
            int len;
            try { len = _input.Receive(_buffer, ref from); }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                // interrupted by Dispose / transient socket error — either
                // we were cancelled or the socket is gone: both mean stop
                if (cancellation.IsCancellationRequested || _input.Closed) break;
                throw;
            }
            if (len == 0)
            {
                // receive timeout: no game traffic — keep discovery alive
                _session.DiscoveryKeepAlive(Now());
                continue;
            }

            long t0 = Stopwatch.GetTimestamp();
            Stats.PacketsIn++;

            if (!_source.AcceptsDatagram(len))
            {
                Stats.Rejected++;
                continue;
            }

            _source.Translate(_buffer.AsSpan(0, len), _session, Now());
            Stats.PacketsOut++;

            long t1 = Stopwatch.GetTimestamp();
            _latency.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
        }
    }

    static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}

public sealed class ForwarderStats
{
    public bool Started;
    public long PacketsIn;
    public long PacketsOut;
    public long Rejected;
    public long Translated => PacketsOut;
    /// <summary>Why no telemetry is flowing yet (e.g. waiting for the sim
    /// to accept a SimConnect client), or null.</summary>
    public volatile string? Waiting;
    /// <summary>Source diagnostics worth showing once (e.g. variables the
    /// simulator rejected), or null.</summary>
    public volatile string? Note;
}
}
