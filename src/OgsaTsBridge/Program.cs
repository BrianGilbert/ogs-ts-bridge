#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using OgsaTsBridge.Sources;
using Ogsa.Telemetry;

namespace OgsaTsBridge
{
/// <summary>
/// ogsa-ts-bridge — translate legacy game telemetry into OGSA-TS UDP.
///
///   ogsa-ts-bridge list [id]
///   ogsa-ts-bridge run [--source <id>] [--in-port <n>]
///                      [--out 239.255.17.17:40123] [--ttl 1]
///                      [--max-hz 1000] [--priority above]
///                      [--set key=value ...]
///
/// Without --source the app watches for known game executables and activates
/// the matching source when a game launches, stopping it when the game
/// exits. No telemetry probing, no Steam dependency — plain process names,
/// so non-Steam games work identically.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return LaunchGui(); // no args = the desktop app
        if (args[0] is "--help" or "-h" or "help")
            return Usage();

        switch (args[0])
        {
            case "list":
                return List(args.Length > 1 ? args[1] : null);
            case "run":
                return Run(args.AsSpan(1).ToArray());
            case "patch":
                return Patch(args.AsSpan(1).ToArray());
            default:
                Console.Error.WriteLine($"unknown command '{args[0]}'");
                return Usage();
        }
    }

    /// <summary>No-args launch = the GUI (same folder). Falls back to
    /// help text when the GUI binary isn't present (e.g. `dotnet run`).
    /// </summary>
    static int LaunchGui()
    {
        var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "OgsaTsBridge.Gui.exe");
        if (System.IO.File.Exists(exe))
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            return 0;
        }
        Console.Error.WriteLine(
            "OgsaTsBridge.Gui.exe not found next to this executable — " +
            "publish both projects to one folder (see README) or use " +
            "`ogsa-ts-bridge run`.");
        return Usage();
    }

    static int Usage()
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev";
        Console.WriteLine(
$"""
ogsa-ts-bridge <version {version}> — legacy game telemetry -> OGSA-TS UDP

  list [id]            list sources (or one source's options)
  run [--source <id>]  translate + forward (Ctrl+C to stop)

  Without --source: auto mode — watch for known game executables
  (process names; non-Steam games included) and
  activate the matching source for as long as the game runs.

run options:
  --source <id>        pin one source manually (skips process watching)
  --in-port <n>        UDP port the game sends to        (source default)
  --out <ip:port>      OGSA-TS endpoint                   (239.255.17.17:40123)
  --ttl <n>            multicast TTL                     (1)
  --max-hz <n>         motion frame cap; input cadence   (1000)
  --priority <p>       forwarder thread: normal|above|high (above)
  --no-stats           disable the 1 Hz status line
  --set k=v            source-specific option (see `list <id>`)
""");
        return 0;
    }

    static int List(string? id)
    {
        if (id is null)
        {
            Console.WriteLine("sources:");
            foreach (var s in SourceRegistry.All())
            {
                var input = s.Transport == SourceTransport.SharedMemory
                    ? $"shared memory {s.SharedMemoryName}"
                    : $"port {s.DefaultPort}";
                Console.WriteLine($"  {s.Id,-12} {s.Title} — {input}");
                Console.WriteLine($"               watches: {string.Join(", ", s.ProcessNames)}");
                Console.WriteLine($"               {s.Description}");
            }
            return 0;
        }
        var src = SourceRegistry.Create(id);
        Console.WriteLine($"{src.Id} — {src.Title}");
        if (src.Transport == SourceTransport.SharedMemory)
            Console.WriteLine($"  input     : shared memory {src.SharedMemoryName} (poll {src.PollIntervalMs} ms)");
        else
            Console.WriteLine($"  default port: {src.DefaultPort}");
        Console.WriteLine($"  watches processes: {string.Join(", ", src.ProcessNames)}");
        Console.WriteLine($"  {src.Description}");
        Console.WriteLine("  options (--set key=value):");
        foreach (var (key, help) in src.Options)
            Console.WriteLine($"    {key,-10} {help}");
        return 0;
    }

    sealed class RunOptions
    {
        public string? SourceId;
        public int InPort = -1;
        public IPEndPoint Endpoint = new(IPAddress.Parse("239.255.17.17"), 40123);
        public int Ttl = 1;
        public double MaxHz = 1000;
        public ThreadPriority Priority = ThreadPriority.AboveNormal;
        public bool Stats = true;
        public readonly Dictionary<string, string> SourceOptions = new();
    }

    static RunOptions ParseRunArgs(string[] args)
    {
        var o = new RunOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {args[i]}");
            switch (args[i])
            {
                case "--source": o.SourceId = Next(); break;
                case "--in-port": o.InPort = int.Parse(Next()); break;
                case "--out":
                    var parts = Next().Split(':');
                    o.Endpoint = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
                    break;
                case "--ttl": o.Ttl = int.Parse(Next()); break;
                case "--max-hz": o.MaxHz = double.Parse(Next()); break;
                case "--priority":
                    o.Priority = Next() switch
                    {
                        "normal" => ThreadPriority.Normal,
                        "high" => ThreadPriority.Highest,
                        _ => ThreadPriority.AboveNormal,
                    };
                    break;
                case "--no-stats": o.Stats = false; break;
                case var kv when kv.StartsWith("--set"):
                    var set = kv == "--set" ? Next() : kv.Substring(5);
                    var eq = set.IndexOf('=');
                    if (eq <= 0) throw new ArgumentException($"bad --set '{set}' (expected key=value)");
                    o.SourceOptions[set[..eq]] = set[(eq + 1)..];
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        return o;
    }

    static int Run(string[] args)
    {
        RunOptions o;
        try { o = ParseRunArgs(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (o.SourceId is not null)
            return RunPinned(o, cts.Token);
        return RunAuto(o, cts.Token);
    }

    /// <summary>Manual mode: one pinned source, no process watching.</summary>
    static UdpInputSocket? TryBind(IPEndPoint local, RunOptions o, out bool failed)
    {
        failed = false;
        try { return new UdpInputSocket(local); }
        catch (InputPortException ex)
        {
            Console.Error.WriteLine(ex.Message);
            failed = true;
            return null;
        }
    }

    static int RunPinned(RunOptions o, CancellationToken ct)
    {
        ILegacySource source;
        try
        {
            source = SourceRegistry.Create(o.SourceId!);
            source.Configure(o.SourceOptions);
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        int inPort = o.InPort >= 0 ? o.InPort : source.DefaultPort;

        Console.WriteLine($"ogsa-ts-bridge: source {source.Id} ({source.Title}) [pinned]");
        Console.WriteLine($"  listening : 0.0.0.0:{inPort} (UDP)");
        Console.WriteLine($"  forwarding: {o.Endpoint} ttl {o.Ttl}, motion cap {o.MaxHz} Hz");
        Console.WriteLine("  Ctrl+C to stop");

        using var input = TryBind(new IPEndPoint(IPAddress.Any, inPort), o, out bool bindFailed);
        if (bindFailed) return 2;
        using var sender = new TelemetrySender(
            o.Endpoint.Address.ToString(), o.Endpoint.Port, o.Ttl, loopback: true);
        using var session = new OgsaSession(sender, source.Title, o.MaxHz);
        source.Declare(session);

        var forwarder = new Forwarder(source, input, session);
        var thread = new Thread(() => forwarder.Run(ct))
        {
            Name = "ogsa-ts-bridge.forward",
            IsBackground = true,
            Priority = o.Priority,
        };
        thread.Start();

        if (o.Stats) StatsLoop(() => forwarder, ct);
        thread.Join();
        Console.WriteLine("stopped.");
        return 0;
    }


    /// <summary>Enables a game's telemetry output (config edit) for
    /// sources implementing IGamePatcher. Run with the game closed.</summary>
    static int Patch(string[] args)
    {
        string? sourceId = null;
        int port = -1;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--source" && i + 1 < args.Length) sourceId = args[++i];
            else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else { Console.Error.WriteLine($"unknown patch option '{args[i]}'"); return 2; }
        }
        if (sourceId is null)
        {
            Console.Error.WriteLine("patch --source <id> [--port n] — see `ogsa-ts-bridge list`");
            return 2;
        }
        ILegacySource source;
        try { source = SourceRegistry.Create(sourceId); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        if (source is not IGamePatcher patcher)
        {
            Console.Error.WriteLine(
                $"{source.Id} has no config patch — enable its telemetry in-game " +
                "(see `ogsa-ts-bridge list " + source.Id + "` for setup steps).");
            return 2;
        }

        // the game rewrites these files on exit: patch with it closed
        foreach (var name in source.ProcessNames)
        {
            var running = System.Diagnostics.Process.GetProcessesByName(name);
            if (running.Length > 0)
            {
                foreach (var proc in running) proc.Dispose();
                Console.Error.WriteLine(
                    $"{name} is running — close the game first (it rewrites the config on exit), then re-run patch.");
                return 2;
            }
        }

        if (port < 0) port = source.DefaultPort;
        var files = patcher.PatchGameTelemetry(port);
        if (files.Count == 0)
        {
            Console.Error.WriteLine(
                "no game config files found — start the game at least once so they exist, then re-run patch.");
            return 1;
        }
        foreach (var f in files)
            Console.WriteLine($"patched: {f} (backup saved alongside as .bak-ogsa-ts-bridge)");
        Console.WriteLine($"telemetry enabled: UDP 127.0.0.1:{port} — start the game and drive.");
        return 0;
    }

    /// <summary>Auto mode: watch game processes, activate the matching
    /// source for as long as the game runs.</summary>
    static int RunAuto(RunOptions o, CancellationToken ct)
    {
        using var host = new EngineHost(new EngineOptions(
            o.Endpoint, o.Ttl, o.MaxHz, o.Priority));

        Console.WriteLine($"ogsa-ts-bridge: auto mode — watching for {host.Sources.Count} source(s):");
        foreach (var s in host.Sources)
            Console.WriteLine($"  {s.Id,-12} {string.Join(", ", s.ProcessNames)}");
        Console.WriteLine($"  forwarding: {o.Endpoint} ttl {o.Ttl}, motion cap {o.MaxHz} Hz");
        Console.WriteLine("  Ctrl+C to stop");

        host.StateChanged += () =>
        {
            var active = host.ActiveSource;
            if (active is not null)
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} forwarding {active.Title} " +
                    $"({host.ActiveProcessLabel}) (0.0.0.0:{active.DefaultPort} -> {host.Options.Output})");
            else if (host.LastError is { } error)
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} activation failed:\n{error}");
            else
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} source stopped, watching again");
        };
        host.Start();

        if (o.Stats) StatsLoop(() => host.ActiveForwarder, ct);
        else ct.WaitHandle.WaitOne();
        host.Stop();
        Console.WriteLine("stopped.");
        return 0;
    }

    delegate IForwarder? ForwarderLookup();

    static void StatsLoop(ForwarderLookup lookup, CancellationToken ct)
    {
        long lastIn = 0, lastOut = 0, lastRejected = 0;
        var window = TimeSpan.FromSeconds(1);
        while (!ct.WaitHandle.WaitOne(window))
        {
            var f = lookup();
            if (f is null)
            {
                Console.WriteLine("waiting for a known game…");
                continue;
            }
            var s = f.Stats;
            var l = f.Latency.Take();
            double inRate = s.PacketsIn - lastIn;
            double outRate = s.PacketsOut - lastOut;
            long rejected = s.Rejected - lastRejected;
            lastIn = s.PacketsIn; lastOut = s.PacketsOut; lastRejected = s.Rejected;
            if (s.PacketsIn == 0)
            {
                Console.WriteLine($"source active, waiting for game telemetry… (in {inRate:F0}/s)");
                continue;
            }
            Console.WriteLine(
                $"in {inRate,5:F0}/s  out {outRate,5:F0}/s  rej {rejected}" +
                $"  |  fwd us p50 {l.P50,7:F0}  p95 {l.P95,7:F0}  p99 {l.P99,7:F0}  max {l.Max,7:F0}");
        }
    }
}
}
