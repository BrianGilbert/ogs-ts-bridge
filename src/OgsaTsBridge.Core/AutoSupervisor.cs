#nullable enable
using System;
using System.Net;
using System.Threading;
using OgsaTsBridge.Sources;
using Ogsa.Telemetry;

namespace OgsaTsBridge
{
/// <summary>
/// Owns the lifecycle of whichever source the process watcher activates:
/// builds the input socket, sender, session and forwarder thread when a
/// game appears; tears them all down when it exits. One active source at
/// a time (two games racing for telemetry is not a real session).
/// </summary>
public sealed class AutoSupervisor : IDisposable
{
    readonly IPEndPoint _output;
    readonly int _ttl;
    readonly double _maxHz;
    readonly ThreadPriority _priority;

    readonly object _gate = new();
    ILegacySource? _source;
    string? _processName;
    UdpInputSocket? _input;
    TelemetrySender? _sender;
    OgsaSession? _session;
    IForwarder? _forwarder;
    Thread? _thread;
    CancellationTokenSource? _cts;

    public sealed class ActiveSource
    {
        public ActiveSource(ILegacySource source, IForwarder forwarder)
        { Source = source; Forwarder = forwarder; }
        public ILegacySource Source { get; }
        public IForwarder Forwarder { get; }
    }

    public AutoSupervisor(IPEndPoint output, int ttl, double maxHz,
                          ThreadPriority priority)
    {
        _output = output;
        _ttl = ttl;
        _maxHz = maxHz;
        _priority = priority;
    }

    /// <summary>The executable name of the active source's game, or null
    /// when idle — consumed by the GameProcessWatcher.</summary>
    public string? ActiveProcessName()
    {
        lock (_gate) return _processName;
    }

    public ActiveSource? Start(ILegacySource source, string processName)
    {
        lock (_gate)
        {
            if (_source is not null) return null; // one game at a time
            _source = source;
            _processName = processName;
            try
            {
                Build(source);
            }
            catch
            {
                // construction failed (e.g. input port unavailable):
                // unwind ALL half-state — otherwise the watcher sees an
                // "active" process label with no session and stops
                // retrying, wedging the app until the game exits.
                Unwind();
                throw;
            }
            _thread!.Start();
            return new ActiveSource(source, _forwarder!);
        }
    }

    void Build(ILegacySource source)
    {
            _cts = new CancellationTokenSource();
            _sender = new TelemetrySender(
                _output.Address.ToString(), _output.Port, _ttl, loopback: true);
            _session = new OgsaSession(_sender, source.Title, _maxHz);
            source.Declare(_session);

            if (source.Transport == SourceTransport.SharedMemory)
            {
                var mmf = new MmfForwarder(source, _session);
                _forwarder = mmf;
                _thread = new Thread(() => mmf.Run(_cts.Token))
                {
                    Name = $"ogsa-ts-bridge.poll.{source.Id}",
                    IsBackground = true,
                    Priority = _priority,
                };
            }
            else
            {
                _input = new UdpInputSocket(
                    new IPEndPoint(IPAddress.Any, source.DefaultPort));
                var udp = new Forwarder(source, _input, _session);
                _forwarder = udp;
                _thread = new Thread(() => udp.Run(_cts.Token))
                {
                    Name = $"ogsa-ts-bridge.forward.{source.Id}",
                    IsBackground = true,
                    Priority = _priority,
                };
            }
    }

    /// <summary>Disposes partial construction state and clears every
    /// field — shared by Stop and failed Start.</summary>
    void Unwind()
    {
        _cts?.Cancel();
        _input?.Dispose();
        if (_thread?.IsAlive == true) { try { _thread.Join(2000); } catch { } }
        _session?.Dispose();     // also disposes the sender
        _cts?.Dispose();
        _source = null;
        _processName = null;
        _input = null;
        _sender = null;
        _session = null;
        _forwarder = null;
        _thread = null;
        _cts = null;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_source is null) return;
            Unwind();
        }
    }

    public ActiveSource? Current
    {
        get
        {
            lock (_gate)
                return _source is null || _forwarder is null
                    ? null : new ActiveSource(_source, _forwarder);
        }
    }

    public void Dispose() => Stop();
}
}
