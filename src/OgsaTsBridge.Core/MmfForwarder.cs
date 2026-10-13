#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using OgsaTsBridge.Sources;

namespace OgsaTsBridge
{
/// <summary>
/// Shared-memory counterpart of <see cref="Forwarder"/>: polls the game's
/// memory-mapped telemetry page on a dedicated thread, hands a snapshot to
/// the source, and lets the session emit on change. Sources gate on their
/// own change token (e.g. AC's PacketId) inside Translate, so unchanged
/// polls cost one page read and no wire traffic.
///
/// Latency: a poll cycle adds at most PollIntervalMs (default 2 ms — under
/// the 3 ms write cadence of AC's 333 Hz physics). The page read itself is
/// a memcpy of SharedMemorySize bytes. Same syscall-to-syscall measurement
/// as the UDP forwarder: page-read -> translate -> send.
/// </summary>
public sealed class MmfForwarder : IForwarder
{
    readonly ILegacySource _source;
    readonly OgsaSession _session;
    readonly byte[] _buffer;
    readonly LatencyMeter _latency = new();

    public MmfForwarder(ILegacySource source, OgsaSession session)
    {
        _source = source;
        _session = session;
        _buffer = new byte[Math.Max(64, source.SharedMemorySize)];
    }

    public ForwarderStats Stats { get; } = new();
    public LatencyMeter Latency => _latency;

    public void Run(CancellationToken cancellation)
    {
        _session.Start(Now());
        Stats.Started = true;
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;

        while (!cancellation.IsCancellationRequested)
        {
            if (view is null)
            {
                // game not up / page not created yet — retry quietly
                try
                {
                    map = MemoryMappedFile.OpenExisting(_source.SharedMemoryName);
                    view = map.CreateViewAccessor(0, _buffer.Length);
                }
                catch (FileNotFoundException)
                {
                    if (cancellation.WaitHandle.WaitOne(250)) break;
                    continue;
                }
            }

            long t0 = Stopwatch.GetTimestamp();
            view.ReadArray(0, _buffer, 0, _buffer.Length);
            Stats.PacketsIn++;

            _source.Translate(_buffer, _session, Now());
            Stats.PacketsOut++;

            long t1 = Stopwatch.GetTimestamp();
            _latency.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);

            if (cancellation.WaitHandle.WaitOne(_source.PollIntervalMs)) break;
        }

        view?.Dispose();
        map?.Dispose();
    }

    static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
}
