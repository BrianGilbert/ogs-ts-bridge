#nullable enable
using System;
using System.Threading;

namespace OgsaTsBridge
{
/// <summary>
/// Fixed-ring latency sampler for the forward hot path. One Add() per
/// packet from the receive thread; snapshots taken at stats cadence (1 Hz).
/// The lock is only ever contended between one producer and a 1 Hz reader —
/// in practice uncontended (~20 ns).
/// </summary>
public sealed class LatencyMeter
{
    readonly double[] _samples;   // microseconds
    readonly int _mask;
    int _head;                    // next write index
    long _count;

    public LatencyMeter(int capacity = 4096)
    {
        if ((capacity & (capacity - 1)) != 0)
            throw new ArgumentException("capacity must be a power of two");
        _samples = new double[capacity];
        _mask = capacity - 1;
    }

    public void Add(double microseconds)
    {
        lock (_samples)
        {
            _samples[_head & _mask] = microseconds;
            _head++;
            _count++;
        }
    }

    public struct Snapshot
    {
        public long Count;
        public double P50, P95, P99, Max;
        public static Snapshot Empty => new() { P50 = double.NaN, P95 = double.NaN, P99 = double.NaN, Max = double.NaN };
    }

    /// <summary>Percentiles over the last full window (or fewer samples
    /// before the ring wraps). Allocates — stats cadence only, never the
    /// hot path.</summary>
    public Snapshot Take()
    {
        lock (_samples)
        {
            int n = (int)Math.Min(_count, _samples.Length);
            if (n == 0) return Snapshot.Empty;
            var copy = new double[n];
            int start = _head - n;
            for (int i = 0; i < n; i++) copy[i] = _samples[(start + i) & _mask];
            Array.Sort(copy);
            double P(double q) => copy[Math.Min(n - 1, (int)Math.Round(q * (n - 1)))];
            return new Snapshot
            {
                Count = _count,
                P50 = P(0.50), P95 = P(0.95), P99 = P(0.99), Max = copy[n - 1],
            };
        }
    }
}
}
