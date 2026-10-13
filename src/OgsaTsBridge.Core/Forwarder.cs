#nullable enable
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OgsaTsBridge.Sources;

namespace OgsaTsBridge
{
/// <summary>
/// The translate-and-forward loop. One thread, start to finish:
///
///   kernel wake-up on datagram arrival
///     -> zero-copy struct parse (source.Translate)
///        -> cached-graph rewrite + Cap'n Proto encode (OgsaSession.Tick)
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
    readonly OgsaSession _session;
    readonly byte[] _buffer = new byte[2048];
    readonly LatencyMeter _latency = new();

    public Forwarder(ILegacySource source, UdpInputSocket input, OgsaSession session)
    {
        _source = source;
        _input = input;
        _session = session;
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

        while (!cancellation.IsCancellationRequested)
        {
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
}
}
