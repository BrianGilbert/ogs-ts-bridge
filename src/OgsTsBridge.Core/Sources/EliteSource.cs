#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Elite Dangerous (Odyssey or Horizons) by reading the game's process
/// memory through community-maintained pointer chains: the game has no
/// telemetry output. The chains come from a user-supplied offsets file
/// (EliteDangerous64_Offsets.xml, the community format: one element per
/// value under &lt;Odyssey&gt; / &lt;Horizons&gt;, with Offsets="n" and hex
/// Offset_0..Offset_{n-1}); a game update breaks them until the file is
/// refreshed.
///
/// A chain resolves as: p = *(base + Offset_0), then p = *(p + Offset_i)
/// for the middle offsets, and the value is the float at p + last offset
/// (base = the EliteDangerous64.exe module).
///
/// Every entry of the selected section is published as "elite.&lt;name&gt;"
/// (raw — the file gives no units). With `motion` on, the ship's Yaw,
/// Pitch and Roll entries (or SRV_* while they read non-zero) drive the
/// pose, taken as radians, and Sway, Heave and Surge the specific force,
/// taken as m/s² — unverified against the live game, so off by default.
///
/// Read-only: the process is opened with PROCESS_VM_READ and
/// PROCESS_QUERY_LIMITED_INFORMATION only.
/// </summary>
public sealed class EliteSource : IPolledSource
{
    internal const string OffsetsFileName = "EliteDangerous64_Offsets.xml";

    sealed record Chain(string Name, long[] Offsets);

    readonly List<Chain> _chains = new();
    OgsChannel[] _channels = Array.Empty<OgsChannel>();
    string _section = "Odyssey";
    string? _offsetsPath;
    bool _motion;
    IntPtr _process;
    long _base;
    int _yaw = -1, _pitch = -1, _roll = -1, _sway = -1, _heave = -1, _surge = -1,
        _srvYaw = -1, _srvPitch = -1, _srvRoll = -1;
    string _loadError = "";

    public string Id => "elite";
    public string Title => "Elite Dangerous (process memory, offsets file)";
    public string Description =>
        "Elite Dangerous read from process memory via a community offsets " +
        "file (EliteDangerous64_Offsets.xml beside the Bridge, in " +
        "%APPDATA%\\OgsTsBridge, or --set offsets=path). Breaks on game updates.";
    public int DefaultPort => 0;

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "EliteDangerous64" };

    public IReadOnlyList<LegacyGame> Games { get; } = new[]
    {
        new LegacyGame("Elite Dangerous (Odyssey / Horizons)", "EliteDangerous64"),
    };

    public IReadOnlyList<(string Key, string Help)> Options { get; } = new[]
    {
        ("offsets", "path to EliteDangerous64_Offsets.xml (default: beside the Bridge, then %APPDATA%\\OgsTsBridge)"),
        ("mode",    "offsets section: odyssey or horizons (default odyssey)"),
        ("motion",  "drive pose and dynamics from Yaw/Pitch/Roll and Sway/Heave/Surge (bool, default false)"),
        ("pollMs",  "poll interval in ms (default 10)"),
    };

    int _pollMs = 10;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("offsets", "mode", "motion", "pollMs");
        _section = o.GetString("mode", "odyssey").ToLowerInvariant() switch
        {
            "odyssey" => "Odyssey",
            "horizons" => "Horizons",
            var m => throw new ArgumentException($"mode '{m}': expected odyssey or horizons"),
        };
        var path = o.GetString("offsets", "");
        _offsetsPath = path.Length > 0 ? path : null;
        _motion = o.GetBool("motion", false);
        _pollMs = Math.Clamp((int)o.GetDouble("pollMs", 10), 2, 1000);
        LoadOffsets();
    }

    /// <summary>The offsets file in use, or null when none was found.</summary>
    internal string? OffsetsPath => _offsetsPath ?? FindOffsets();

    static string? FindOffsets()
    {
        foreach (var dir in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OgsTsBridge"),
                 })
        {
            var p = Path.Combine(dir, OffsetsFileName);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    void LoadOffsets()
    {
        _chains.Clear();
        _loadError = "";
        var path = OffsetsPath;
        if (path is null || !File.Exists(path)) { _loadError = $"no {OffsetsFileName} found"; return; }
        try { _chains.AddRange(Parse(XDocument.Load(path), _section)); }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or IOException)
        {
            _loadError = $"{OffsetsFileName}: {ex.Message}";
        }
        int Index(string name) => _chains.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        _yaw = Index("Yaw"); _pitch = Index("Pitch"); _roll = Index("Roll");
        _sway = Index("Sway"); _heave = Index("Heave"); _surge = Index("Surge");
        _srvYaw = Index("SRV_Yaw"); _srvPitch = Index("SRV_Pitch"); _srvRoll = Index("SRV_Roll");
    }

    static IEnumerable<Chain> Parse(XDocument doc, string section)
    {
        var node = doc.Root?.Element(section) ?? throw new FormatException($"no <{section}> section");
        foreach (var e in node.Elements())
        {
            if (!int.TryParse((string?)e.Attribute("Offsets"), out int n) || n < 2) continue;
            var offsets = new long[n];
            bool ok = true;
            for (int i = 0; i < n && ok; i++)
                ok = long.TryParse(((string?)e.Attribute($"Offset_{i}"))?.Trim(),
                    System.Globalization.NumberStyles.HexNumber, null, out offsets[i]);
            if (ok) yield return new Chain(e.Name.LocalName, offsets);
        }
    }

    public SourceTransport Transport => SourceTransport.Poll;
    public int PollIntervalMs => _pollMs;
    public int PollBufferSize => Math.Max(4, _chains.Count * 4);
    public string PollTarget => _loadError.Length > 0
        ? $"Elite Dangerous ({_loadError})"
        : "EliteDangerous64.exe";

    public bool Connect()
    {
        Disconnect();
        if (_chains.Count == 0) return false;
        var procs = Process.GetProcessesByName("EliteDangerous64");
        try
        {
            if (procs.Length == 0) return false;
            var p = procs[0];
            _base = p.MainModule?.BaseAddress.ToInt64() ?? 0;
            if (_base == 0) return false;
            _process = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, p.Id);
            return _process != IntPtr.Zero;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    public void Disconnect()
    {
        if (_process != IntPtr.Zero) CloseHandle(_process);
        _process = IntPtr.Zero;
    }

    /// <summary>One float per chain, in file order (NaN where a chain does
    /// not resolve right now — e.g. the SRV chains while flying the ship).</summary>
    public int Read(byte[] buffer)
    {
        if (_process == IntPtr.Zero) return -1;
        if (!GetExitCodeProcess(_process, out uint code) || code != StillActive) return -1;
        for (int i = 0; i < _chains.Count; i++)
            MemoryMarshal.Write(buffer.AsSpan(i * 4), Resolve(_chains[i].Offsets));
        return _chains.Count * 4;
    }

    unsafe float Resolve(long[] offsets)
    {
        long p;
        if (!ReadProcessMemory(_process, new IntPtr(_base + offsets[0]), &p, 8, out _)) return float.NaN;
        for (int i = 1; i < offsets.Length - 1; i++)
        {
            if (p == 0) return float.NaN;
            if (!ReadProcessMemory(_process, new IntPtr(p + offsets[i]), &p, 8, out _)) return float.NaN;
        }
        if (p == 0) return float.NaN;
        float v;
        return ReadProcessMemory(_process, new IntPtr(p + offsets[^1]), &v, 4, out _) ? v : float.NaN;
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("ship", Ogs.Telemetry.Schema.ObjectType.Spacecraft, "Elite Dangerous ship");
        session.SendChangedChannelsOnly();
        _channels = new OgsChannel[_chains.Count];
        for (int i = 0; i < _chains.Count; i++)
            _channels[i] = session.AddChannel("elite." + _chains[i].Name, "",
                $"'{_chains[i].Name}' from the offsets file (raw)");
    }

    public bool AcceptsDatagram(int length) => length > 0;

    static float V(ReadOnlySpan<byte> d, int index) =>
        index >= 0 && index * 4 + 4 <= d.Length ? MemoryMarshal.Read<float>(d.Slice(index * 4, 4)) : float.NaN;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        for (int i = 0; i < _channels.Length; i++)
        {
            float v = V(d, i);
            if (!float.IsNaN(v)) session.SetChannel(_channels[i], v);
        }
        if (!_motion) { session.Tick(now); return; }

        // the SRV chains resolve only while driving the SRV
        bool srv = !float.IsNaN(V(d, _srvYaw)) && V(d, _srvYaw) != 0;
        float yaw = V(d, srv ? _srvYaw : _yaw), pitch = V(d, srv ? _srvPitch : _pitch), roll = V(d, srv ? _srvRoll : _roll);
        if (!float.IsNaN(yaw) && !float.IsNaN(pitch) && !float.IsNaN(roll))
        {
            var q = OgsMath.EulerToQuaternion(yaw, pitch, roll);
            session.SetPose(null, q);
            float sway = V(d, _sway), heave = V(d, _heave), surge = V(d, _surge);
            if (!float.IsNaN(sway) && !float.IsNaN(heave) && !float.IsNaN(surge))
                session.SetBodyDynamics(new Vector3(sway, heave, surge), Vector3.Zero);
        }
        session.Tick(now);
    }

    const uint ProcessVmRead = 0x0010, ProcessQueryLimitedInformation = 0x1000, StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern unsafe bool ReadProcessMemory(IntPtr process, IntPtr address, void* buffer, nint size, out nint read);
}
}
