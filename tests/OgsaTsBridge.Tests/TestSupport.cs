#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Ogsa.Telemetry;

namespace OgsaTsBridge.Tests
{
/// <summary>Single-reader channel with timed reads — test-side only.</summary>
sealed class Channel<T>
{
    readonly Queue<T> _items = new();
    readonly object _gate = new();

    public void Write(T item)
    {
        lock (_gate) { _items.Enqueue(item); Monitor.Pulse(_gate); }
    }

    public bool TryRead(out T item, TimeSpan timeout)
    {
        lock (_gate)
        {
            var deadline = Stopwatch.StartNew();
            while (_items.Count == 0)
            {
                var left = timeout - deadline.Elapsed;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left))
                {
                    item = default!;
                    return false;
                }
            }
            item = _items.Dequeue();
            return true;
        }
    }
}

/// <summary>
/// Minimal OGSA-TS multicast receiver for tests (pattern from the SDK's
/// TelemetryReceiver, plus a Stopwatch arrival timestamp per motion frame).
/// </summary>
sealed class OgsaReceiver : IDisposable
{
    readonly Socket _socket;
    readonly CancellationTokenSource _cts = new();
    Task? _loop;

    public event Action<CapnpGen.DiscoveryFrame.READER>? Discovery;
    public event Action<CapnpGen.MotionFrame.READER, double>? Motion;

    public OgsaReceiver(string group, int port)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(IPAddress.Parse(group)));
    }

    public void Start() => _loop ??= Task.Run(() => Loop(_cts.Token));

    public int RawDatagrams;
    public int ForeignSkipped;
    public int DecodeErrors;
    public int DiscoveryFrames;
    public int MotionFrames;

    void Loop(CancellationToken ct)
    {
        var buf = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            int len;
            try { len = _socket.ReceiveFrom(buf, ref from); }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }
            Interlocked.Increment(ref RawDatagrams);
            if (!Envelope.TryUnwrap(buf.AsMemory(0, len), out var type, out var payload))
            {
                ForeignSkipped++;
                continue;
            }
            try
            {
                switch (type)
                {
                    case FrameType.Discovery:
                        DiscoveryFrames++;
                        Discovery?.Invoke(CapnpGen.DiscoveryFrame.READER.create(Decode(payload)));
                        break;
                    case FrameType.Motion:
                        MotionFrames++;
                        Motion?.Invoke(CapnpGen.MotionFrame.READER.create(Decode(payload)),
                            (double)Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
                        break;
                }
            }
            catch (Capnp.DeserializationException) { DecodeErrors++; }
        }
    }

    static Capnp.DeserializerState Decode(ReadOnlyMemory<byte> payload)
        => Capnp.DeserializerState.CreateRoot(Envelope.DecodeMessage(payload.Span));

    public void Dispose()
    {
        _cts.Cancel();
        try { _socket.Close(); } catch (SocketException) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
}
