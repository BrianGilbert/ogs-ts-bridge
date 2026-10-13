#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using OgsaTsBridge.Sources;

namespace OgsaTsBridge
{
/// <summary>Engine configuration — output endpoint and forwarder tuning.</summary>
public sealed record EngineOptions(
    IPEndPoint Output,
    int Ttl = 1,
    double MaxHz = 1000,
    ThreadPriority Priority = ThreadPriority.AboveNormal,
    bool AutoWatch = true,
    IReadOnlyDictionary<string, string>? SourceOptions = null);

/// <summary>
/// The shared control surface over one translator engine instance:
/// process-watch auto activation, manual pinning, endpoint changes, and
/// live stats — for the console host and the GUI alike.
///
/// Latency contract: nothing here touches the forward path. State is
/// observed by reading the supervisor's immutable handles and the
/// forwarder's snapshot counters; commands serialize on the supervisor
/// gate, which the forwarder thread never takes. <see cref="StateChanged"/>
/// may fire on the watcher thread — marshals to your UI thread.
/// </summary>
public sealed class EngineHost : IDisposable
{
    readonly List<ILegacySource> _sources;
    EngineOptions _options;
    GameProcessWatcher? _watcher;
    AutoSupervisor? _supervisor;
    readonly object _gate = new();
    bool _manual;

    /// <summary>Raised on activation, deactivation, and endpoint changes.</summary>
    public event Action? StateChanged;

    public EngineHost(EngineOptions options)
    {
        _options = options;
        _sources = new List<ILegacySource>();
        foreach (var s in SourceRegistry.All())
        {
            s.Configure(options.SourceOptions ?? new Dictionary<string, string>());
            _sources.Add(s);
        }
    }

    public IReadOnlyList<ILegacySource> Sources => _sources;
    public EngineOptions Options => _options;
    public bool ManualActive { get { lock (_gate) return _manual; } }
    public bool AutoWatch => _options.AutoWatch;

    public ILegacySource? ActiveSource
    {
        get { lock (_gate) return _supervisor?.Current?.Source; }
    }

    public string? ActiveProcessLabel
    {
        get { lock (_gate) return _supervisor?.ActiveProcessName(); }
    }

    public Forwarder? ActiveForwarder
    {
        get { lock (_gate) return _supervisor?.Current?.Forwarder; }
    }

    /// <summary>Starts watching (auto mode) or sits idle (auto-watch off).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _supervisor = new AutoSupervisor(
                _options.Output, _options.Ttl, _options.MaxHz, _options.Priority);
            _watcher = new GameProcessWatcher(_sources)
            {
                ActiveProcessName = () =>
                {
                    lock (_gate) return _supervisor?.ActiveProcessName();
                },
            };
            _watcher.Changed += OnWatcherChanged;
            _watcher.Paused = !_options.AutoWatch;
            _watcher.Start();
        }
    }

    void OnWatcherChanged(object? sender, GameProcessWatcher.ChangeEventArgs e)
    {
        if (e.Started)
        {
            lock (_gate)
            {
                if (_supervisor is null || _manual) return;
                _supervisor.Start(e.Source, e.ProcessName);
            }
        }
        else
        {
            lock (_gate) _supervisor?.Stop();
        }
        StateChanged?.Invoke();
    }

    /// <summary>Pins one source manually (used when auto-detection finds
    /// nothing — e.g. an unusual executable). Pauses process watching for
    /// as long as the manual session runs. Returns false if a session is
    /// already active.</summary>
    public bool StartManual(ILegacySource source)
    {
        lock (_gate)
        {
            if (_supervisor?.Current is not null) return false;
            _manual = true;
            if (_watcher is not null) _watcher.Paused = true;
            var started = _supervisor?.Start(source, "(manual selection)") is not null;
            if (!started) _manual = false;
            return started;
        }
    }

    /// <summary>Stops the active session (manual or auto). Resumes process
    /// watching when auto-watch is on.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _supervisor?.Stop();
            _manual = false;
            if (_watcher is not null) _watcher.Paused = !_options.AutoWatch;
        }
        StateChanged?.Invoke();
    }

    public void SetAutoWatch(bool on)
    {
        lock (_gate)
        {
            _options = _options with { AutoWatch = on };
            if (_watcher is not null)
                _watcher.Paused = _manual || !on;
        }
        StateChanged?.Invoke();
    }

    /// <summary>Changes the OGSA-TS output endpoint. Stops the active
    /// session first — receivers identify a session by its discovery, so a
    /// restart is the honest signal.</summary>
    public void ApplyOutput(IPEndPoint endpoint, int ttl)
    {
        lock (_gate)
        {
            _supervisor?.Stop();
            _manual = false;
            _options = _options with { Output = endpoint, Ttl = ttl };
            _supervisor = new AutoSupervisor(
                _options.Output, _options.Ttl, _options.MaxHz, _options.Priority);
            if (_watcher is not null) _watcher.Paused = !_options.AutoWatch;
        }
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _supervisor?.Dispose();
            _supervisor = null;
        }
    }
}
}
