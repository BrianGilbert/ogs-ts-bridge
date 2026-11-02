#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using OgsTsBridge.Sources;

namespace OgsTsBridge
{
/// <summary>
/// Request-driven counterpart of <see cref="MmfForwarder"/>: for games
/// that answer requests (a local HTTP or TCP server) or expose only their
/// process memory. The source performs each fetch itself
/// (<see cref="IPolledSource.Read"/>); this loop connects, polls at
/// PollIntervalMs, translates and keeps discovery alive while the game is
/// away.
///
/// Latency: a sample waits at most PollIntervalMs plus the request round
/// trip — the floor these games set, since they publish nothing on their
/// own. Measured from the start of the fetch to the end of the send.
/// </summary>
public sealed class PollForwarder : IForwarder
{
    readonly IPolledSource _source;
    readonly OgsSession _session;
    readonly byte[] _buffer;
    readonly LatencyMeter _latency = new();

    public PollForwarder(IPolledSource source, OgsSession session)
    {
        _source = source;
        _session = session;
        _buffer = new byte[Math.Max(64, source.PollBufferSize)];
    }

    public ForwarderStats Stats { get; } = new();
    public LatencyMeter Latency => _latency;

    public void Run(CancellationToken cancellation)
    {
        _session.Start(Now());
        Stats.Started = true;
        bool connected = false;

        while (!cancellation.IsCancellationRequested)
        {
            if (!connected)
            {
                connected = TryConnect();
                if (!connected)
                {
                    Stats.Waiting = $"waiting for {_source.PollTarget}";
                    _session.DiscoveryKeepAlive(Now());
                    if (cancellation.WaitHandle.WaitOne(500)) break;
                    continue;
                }
                Stats.Waiting = null;
            }

            long t0 = Stopwatch.GetTimestamp();
            int length = _source.Read(_buffer);
            if (length < 0)
            {
                _source.Disconnect();
                connected = false;
                continue;
            }
            Stats.PacketsIn++;
            if (length > 0)
            {
                _source.Translate(_buffer.AsSpan(0, length), _session, Now());
                Stats.PacketsOut++;
                long t1 = Stopwatch.GetTimestamp();
                _latency.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
            }
            else _session.DiscoveryKeepAlive(Now());

            if (cancellation.WaitHandle.WaitOne(_source.PollIntervalMs)) break;
        }

        if (connected) _source.Disconnect();
    }

    bool TryConnect()
    {
        try { return _source.Connect(); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or System.IO.IOException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
}
