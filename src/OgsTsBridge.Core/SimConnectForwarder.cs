#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OgsTsBridge.SimConnect;
using OgsTsBridge.Sources;

namespace OgsTsBridge
{
/// <summary>
/// SimConnect counterpart of <see cref="Forwarder"/>: one thread opens a
/// SimConnect client, asks for the source's frame variables every sim
/// frame and waits on the event handle SimConnect signals when data is
/// queued. Each frame is repacked into the source's variable order and
/// translated, encoded and sent on the same thread.
///
/// Variables the sim does not know (names or units differ between sim
/// versions) come back as SimConnect exceptions keyed by send id; the
/// definition is rebuilt without them and they read NaN, so one unknown
/// variable never shifts the others.
///
/// Latency: measured from dispatch to send, like the other forwarders.
/// </summary>
public sealed unsafe class SimConnectForwarder : IForwarder
{
    const uint DefineFrame = 1, DefineAircraft = 2;
    const uint RequestFrame = 1, RequestAircraft = 2;
    const uint EventPause = 1, EventSim = 2;
    const int WaitMs = 20;              // idle tick cadence while no data arrives
    const double RetrySeconds = 1.0;    // connection attempts while the sim loads

    readonly ISimConnectSource _source;
    readonly OgsSession _session;
    readonly LatencyMeter _latency = new();
    readonly double[] _frame;
    readonly bool[] _frameRejected;
    readonly bool[] _aircraftRejected;
    readonly List<int> _frameAccepted = new();
    readonly List<int> _aircraftAccepted = new();
    readonly Dictionary<uint, (uint define, int index)> _sendIds = new();
    bool _rebuildFrame, _rebuildAircraft;
    uint _pauseFlags;
    bool _simRunning = true;

    public SimConnectForwarder(ISimConnectSource source, OgsSession session)
    {
        _source = source;
        _session = session;
        _frame = new double[source.FrameVars.Count];
        _frameRejected = new bool[source.FrameVars.Count];
        _aircraftRejected = new bool[source.AircraftVars.Count];
        Array.Fill(_frame, double.NaN);
    }

    public ForwarderStats Stats { get; } = new();
    public LatencyMeter Latency => _latency;

    public void Run(CancellationToken cancellation)
    {
        _session.Start(Now());
        Stats.Started = true;

        using var ready = new AutoResetEvent(false);
        var waits = new WaitHandle[] { ready, cancellation.WaitHandle };
        SimConnectNative? api = null;
        IntPtr handle = IntPtr.Zero;
        double nextAttempt = double.NegativeInfinity;

        while (!cancellation.IsCancellationRequested)
        {
            if (handle == IntPtr.Zero)
            {
                double now = Now();
                if (now >= nextAttempt)
                {
                    nextAttempt = now + RetrySeconds;
                    handle = TryConnect(ref api, ready);
                }
                if (handle == IntPtr.Zero)
                {
                    _session.DiscoveryKeepAlive(now);
                    if (cancellation.WaitHandle.WaitOne(100)) break;
                    continue;
                }
            }

            if (WaitHandle.WaitAny(waits, WaitMs) == 1) break;

            bool frame = Drain(api!, handle, out bool quit);
            if (quit)
            {
                api!.Close(handle);
                handle = IntPtr.Zero;
                Stats.Waiting = "simulator closed the connection — reconnecting";
                continue;
            }
            if (_rebuildFrame) RebuildFrame(api!, handle);
            if (_rebuildAircraft) RebuildAircraft(api!, handle);
            if (!frame)
            {
                double now = Now();
                _source.OnIdle(_session, now);
                _session.DiscoveryKeepAlive(now);
            }
        }

        if (handle != IntPtr.Zero) api!.Close(handle);
    }

    IntPtr TryConnect(ref SimConnectNative? api, AutoResetEvent ready)
    {
        if (api is null)
        {
            var path = SimConnectLocator.Find(_source.LibraryPath, _source.ProcessNames);
            if (path is null)
            {
                Stats.Waiting = "SimConnect.dll not found — set the simconnect option to its path";
                return IntPtr.Zero;
            }
            try { api = SimConnectNative.Load(path); }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                                           or EntryPointNotFoundException)
            {
                Stats.Waiting = $"cannot load {path}: {ex.Message}";
                return IntPtr.Zero;
            }
        }

        if (api.Open(out var handle, "OGS-TS Bridge", ready.SafeWaitHandle.DangerousGetHandle()) != 0
            || handle == IntPtr.Zero)
        {
            Stats.Waiting = "waiting for the simulator to accept SimConnect clients";
            return IntPtr.Zero;
        }

        Stats.Waiting = null;
        _sendIds.Clear();
        _pauseFlags = 0;
        _simRunning = true;
        _source.OnSimState(_pauseFlags, _simRunning);
        api.SubscribeToSystemEvent(handle, EventPause, "Pause_EX1");
        api.SubscribeToSystemEvent(handle, EventSim, "Sim");
        RegisterFrame(api, handle);
        RegisterAircraft(api, handle);
        return handle;
    }

    void RegisterFrame(SimConnectNative api, IntPtr handle)
    {
        _frameAccepted.Clear();
        var vars = _source.FrameVars;
        for (int i = 0; i < vars.Count; i++)
        {
            if (_frameRejected[i]) continue;
            api.AddToDataDefinition(handle, DefineFrame, vars[i].Name, vars[i].Unit,
                                    SimConnectNative.DataTypeFloat64);
            _sendIds[api.GetLastSentPacketId(handle)] = (DefineFrame, i);
            _frameAccepted.Add(i);
        }
        api.RequestDataOnSimObject(handle, RequestFrame, DefineFrame, SimConnectNative.PeriodSimFrame, 0);
    }

    void RegisterAircraft(SimConnectNative api, IntPtr handle)
    {
        _aircraftAccepted.Clear();
        var vars = _source.AircraftVars;
        for (int i = 0; i < vars.Count; i++)
        {
            if (_aircraftRejected[i]) continue;
            api.AddToDataDefinition(handle, DefineAircraft, vars[i].Name, vars[i].Unit, DataType(vars[i].Kind));
            _sendIds[api.GetLastSentPacketId(handle)] = (DefineAircraft, i);
            _aircraftAccepted.Add(i);
        }
        api.RequestDataOnSimObject(handle, RequestAircraft, DefineAircraft,
                                   SimConnectNative.PeriodSecond, SimConnectNative.FlagChanged);
    }

    void NoteRejected()
    {
        var names = new List<string>();
        for (int i = 0; i < _frameRejected.Length; i++)
            if (_frameRejected[i]) names.Add(_source.FrameVars[i].Name);
        for (int i = 0; i < _aircraftRejected.Length; i++)
            if (_aircraftRejected[i]) names.Add(_source.AircraftVars[i].Name);
        Stats.Note = $"simulator rejected {names.Count} variable(s): {string.Join(", ", names)}";
    }

    void RebuildFrame(SimConnectNative api, IntPtr handle)
    {
        _rebuildFrame = false;
        NoteRejected();
        api.RequestDataOnSimObject(handle, RequestFrame, DefineFrame, SimConnectNative.PeriodNever, 0);
        api.ClearDataDefinition(handle, DefineFrame);
        for (int i = 0; i < _frame.Length; i++) if (_frameRejected[i]) _frame[i] = double.NaN;
        RegisterFrame(api, handle);
    }

    void RebuildAircraft(SimConnectNative api, IntPtr handle)
    {
        _rebuildAircraft = false;
        NoteRejected();
        api.RequestDataOnSimObject(handle, RequestAircraft, DefineAircraft, SimConnectNative.PeriodNever, 0);
        api.ClearDataDefinition(handle, DefineAircraft);
        RegisterAircraft(api, handle);
    }

    static uint DataType(SimVarKind kind) => kind switch
    {
        SimVarKind.Text => SimConnectNative.DataTypeString256,
        SimVarKind.Xyz => SimConnectNative.DataTypeXyz,
        _ => SimConnectNative.DataTypeFloat64,
    };

    static int Size(SimVarKind kind) => kind switch
    {
        SimVarKind.Text => 256,
        SimVarKind.Xyz => 24,
        _ => 8,
    };

    /// <summary>Handles every queued message; true when a frame was sent.</summary>
    bool Drain(SimConnectNative api, IntPtr handle, out bool quit)
    {
        quit = false;
        bool frame = false;
        while (api.GetNextDispatch(handle, out var data, out var size))
        {
            byte* p = (byte*)data;
            switch (U32(p, SimConnectNative.RecvIdOffset))
            {
                case SimConnectNative.RecvQuit:
                    quit = true;
                    return frame;

                case SimConnectNative.RecvException:
                    if (_sendIds.TryGetValue(U32(p, SimConnectNative.ExceptionSendIdOffset), out var sent))
                    {
                        if (sent.define == DefineFrame) { _frameRejected[sent.index] = true; _rebuildFrame = true; }
                        else { _aircraftRejected[sent.index] = true; _rebuildAircraft = true; }
                    }
                    break;

                case SimConnectNative.RecvEvent:
                    uint id = U32(p, SimConnectNative.EventIdOffset);
                    uint value = U32(p, SimConnectNative.EventDataOffset);
                    if (id == EventPause) _pauseFlags = value;
                    else if (id == EventSim) _simRunning = value != 0;
                    _source.OnSimState(_pauseFlags, _simRunning);
                    break;

                case SimConnectNative.RecvSimObjectData:
                    uint request = U32(p, SimConnectNative.DataRequestIdOffset);
                    int count = (int)U32(p, SimConnectNative.DataDefineCountOffset);
                    if (request == RequestFrame)
                        frame |= OnFrame(p, size, count);
                    else if (request == RequestAircraft)
                        OnAircraft(p, size, count);
                    break;
            }
        }
        return frame;
    }

    bool OnFrame(byte* p, uint size, int count)
    {
        long t0 = Stopwatch.GetTimestamp();
        Stats.PacketsIn++;
        // a block from a definition being rebuilt has a different shape
        if (_rebuildFrame || count != _frameAccepted.Count ||
            size < SimConnectNative.DataOffset + 8u * (uint)count)
        {
            Stats.Rejected++;
            return false;
        }
        byte* d = p + SimConnectNative.DataOffset;
        for (int k = 0; k < count; k++)
            _frame[_frameAccepted[k]] = Unsafe.ReadUnaligned<double>(d + 8 * k);

        _source.Translate(MemoryMarshal.AsBytes(_frame.AsSpan()), _session, Now());
        Stats.PacketsOut++;
        _latency.Add((Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency);
        return true;
    }

    void OnAircraft(byte* p, uint size, int count)
    {
        if (_rebuildAircraft || count != _aircraftAccepted.Count) return;
        var vars = _source.AircraftVars;
        var values = new SimVarValues(vars.Count);
        uint offset = SimConnectNative.DataOffset;
        foreach (int i in _aircraftAccepted)
        {
            int n = Size(vars[i].Kind);
            if (offset + n > size) return;
            byte* d = p + offset;
            switch (vars[i].Kind)
            {
                case SimVarKind.Text:
                    int len = 0;
                    while (len < 256 && d[len] != 0) len++;
                    values.Texts[i] = Encoding.UTF8.GetString(d, len);
                    break;
                case SimVarKind.Xyz:
                    values.Vectors[i] = new Vector3(
                        (float)Unsafe.ReadUnaligned<double>(d),
                        (float)Unsafe.ReadUnaligned<double>(d + 8),
                        (float)Unsafe.ReadUnaligned<double>(d + 16));
                    break;
                default:
                    values.Numbers[i] = Unsafe.ReadUnaligned<double>(d);
                    break;
            }
            offset += (uint)n;
        }
        _source.OnAircraft(values, _session, Now());
    }

    static uint U32(byte* p, int offset) => Unsafe.ReadUnaligned<uint>(p + offset);

    static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
}
