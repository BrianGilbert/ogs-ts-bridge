#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using OgsaTsBridge.Sources;

namespace OgsaTsBridge
{
/// <summary>
/// Watches for known game executables and reports source start/stop:
/// each source declares the process names it belongs to (their PROCESS_NAME), a
/// background watcher matches the running process list against them.
/// Works for non-Steam games because it never touches Steam — just
/// executable names.
///
/// This is a cold-start concern only (game boot → menus → race is tens of
/// seconds); the 2 s poll granularity costs nothing on the forward path,
/// which has its own thread.
/// </summary>
public sealed class GameProcessWatcher : IDisposable
{
    readonly IReadOnlyList<ILegacySource> _sources;
    readonly TimeSpan _pollInterval;
    readonly Thread _thread;
    readonly CancellationTokenSource _cts = new();

    public sealed class ChangeEventArgs : EventArgs
    {
        public ChangeEventArgs(ILegacySource source, string processName, bool started)
        { Source = source; ProcessName = processName; Started = started; }
        public ILegacySource Source { get; }
        public string ProcessName { get; }
        public bool Started { get; }
    }

    /// <summary>Raised when a source's game process appears (no source
    /// active) or when the active source's game process exits.</summary>
    public event EventHandler<ChangeEventArgs>? Changed;

    /// <summary>Excludes these process names — set from the supervisor so
    /// the active source is not re-detected as a new start.</summary>
    public Func<string?> ActiveProcessName { get; set; } = static () => null;

    /// <summary>When true, scanning and exit detection stop entirely —
    /// set while a manually pinned session runs (it ends when the user
    /// stops it, not when a process exits) or when auto-watch is off.</summary>
    public volatile bool Paused;

    public GameProcessWatcher(IReadOnlyList<ILegacySource> sources,
                              TimeSpan? pollInterval = null)
    {
        _sources = sources;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _thread = new Thread(WatchLoop)
        {
            Name = "ogsa-ts-bridge.process-watch",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
    }

    public void Start() => _thread.Start();

    /// <summary>One snapshot now — returns the first source whose process
    /// is already running (app started while the game was open).</summary>
    public (ILegacySource source, string process)? CheckOnce()
    {
        var running = Snapshot();
        foreach (var s in _sources)
            foreach (var name in s.ProcessNames)
                if (running.Contains(name))
                    return (s, name);
        return null;
    }

    void WatchLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!Paused)
            {
                try
                {
                    var active = ActiveProcessName();
                    if (active is null)
                    {
                        var hit = CheckOnce();
                        if (hit is { } found)
                            Changed?.Invoke(this,
                                new ChangeEventArgs(found.source, found.process, started: true));
                    }
                    else if (!Snapshot().Contains(active))
                    {
                        // game process gone — supervisor stops the source; it
                        // will re-arm detection once it clears ActiveProcessName
                        Changed?.Invoke(this, new ChangeEventArgs(null!, active, started: false));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or SystemException)
                {
                    // process list hiccups (access denied on some system procs) — keep watching
                }
            }
            if (_cts.Token.WaitHandle.WaitOne(_pollInterval)) break;
        }
    }

    static HashSet<string> Snapshot()
    {
        // one OS snapshot, name set only — ~a few hundred strings per poll
        // on the supervisor thread, never on the forward path
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); }
            catch (InvalidOperationException) { /* process exited mid-scan */ }
            finally { p.Dispose(); }
        }
        return names;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (_thread.IsAlive) _thread.Join(3000); } catch { }
        _cts.Dispose();
    }
}
}
