#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using OgsTsBridge.Sources;

// Windows-only by nature: the games that write these pages are
// Windows titles and MemoryMappedFile.OpenExisting for the game's page
// only exists there.
#pragma warning disable CA1416

namespace OgsTsBridge
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
    readonly OgsSession _session;
    readonly byte[] _buffer;
    readonly LatencyMeter _latency = new();
    readonly ISharedMemoryGather? _gather;
    readonly MemoryMappedFile?[] _auxMaps;
    readonly MemoryMappedViewAccessor?[] _auxViews;

    public MmfForwarder(ILegacySource source, OgsSession session)
    {
        _source = source;
        _session = session;
        _buffer = new byte[Math.Max(64, source.SharedMemorySize)];
        _gather = source as ISharedMemoryGather;
        int aux = _gather?.AuxiliaryMapNames.Count ?? 0;
        _auxMaps = new MemoryMappedFile?[aux];
        _auxViews = new MemoryMappedViewAccessor?[aux];
    }

    public ForwarderStats Stats { get; } = new();
    public LatencyMeter Latency => _latency;

    public void Run(CancellationToken cancellation)
    {
        _session.Start(Now());
        Stats.Started = true;
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        int auxPolls = AuxRetryPolls;

        while (!cancellation.IsCancellationRequested)
        {
            if (view is null)
            {
                // game not up / page not created yet — retry quietly
                map = OpenAny();
                if (map is null)
                {
                    if (cancellation.WaitHandle.WaitOne(250)) break;
                    continue;
                }
                // gather sources read anywhere in the map; prefix sources
                // only ever touch the first SharedMemorySize bytes
                view = _gather is null
                    ? map.CreateViewAccessor(0, _buffer.Length)
                    : map.CreateViewAccessor();
            }

            if (_gather is not null && ++auxPolls >= AuxRetryPolls)
            {
                auxPolls = 0;
                OpenMissingAuxiliary();
            }

            long t0 = Stopwatch.GetTimestamp();
            int length = _buffer.Length;
            if (_gather is null) view.ReadArray(0, _buffer, 0, _buffer.Length);
            else length = _gather.Gather(view, _auxViews, _buffer);
            Stats.PacketsIn++;

            if (length > 0)
                _source.Translate(_buffer.AsSpan(0, length), _session, Now());
            Stats.PacketsOut++;

            long t1 = Stopwatch.GetTimestamp();
            _latency.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);

            if (cancellation.WaitHandle.WaitOne(_source.PollIntervalMs)) break;
        }

        view?.Dispose();
        map?.Dispose();
        for (int i = 0; i < _auxViews.Length; i++)
        {
            _auxViews[i]?.Dispose();
            _auxMaps[i]?.Dispose();
            _auxViews[i] = null;
            _auxMaps[i] = null;
        }
    }

    /// <summary>Polls between attempts to open a missing auxiliary map
    /// (~0.25 s at a 2 ms poll) — a failed open throws, so not every poll.</summary>
    const int AuxRetryPolls = 128;

    /// <summary>Opens the source's map, falling back through its alternate
    /// names; null when none exists yet (game not up).</summary>
    MemoryMappedFile? OpenAny()
    {
        if (TryOpen(_source.SharedMemoryName) is { } primary) return primary;
        foreach (var name in _source.SharedMemoryAlternateNames)
            if (TryOpen(name) is { } alternate) return alternate;
        return null;
    }

    void OpenMissingAuxiliary()
    {
        var names = _gather!.AuxiliaryMapNames;
        for (int i = 0; i < _auxViews.Length; i++)
        {
            if (_auxViews[i] is not null) continue;
            if (TryOpen(names[i]) is not { } map) continue;
            _auxMaps[i] = map;
            _auxViews[i] = map.CreateViewAccessor();
        }
    }

    static MemoryMappedFile? TryOpen(string name)
    {
        try { return MemoryMappedFile.OpenExisting(name); }
        catch (FileNotFoundException) { return null; }
    }

    static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
}

#pragma warning restore CA1416
