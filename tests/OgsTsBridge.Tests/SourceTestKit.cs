#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OgsTsBridge.Sources;
using Ogs.Telemetry;
using Xunit;

namespace OgsTsBridge.Tests
{
/// <summary>Shared helpers for the source tests: a capture session read
/// back the way a receiver does (SentFrames), forged-page writers and
/// quaternion asserts.</summary>
internal static class SourceTestKit
{
    internal sealed class Capture
    {
        public readonly List<(FrameType type, byte[] datagram)> Sent = new();
        public OgsSession MakeSession(string game) => new((t, d) => { lock (Sent) Sent.Add((t, d)); }, game);

        /// <summary>Ticks sent: a tick in several parts counts once.</summary>
        public int Motions
        {
            get
            {
                lock (Sent)
                    return Sent.Where(x => x.type == FrameType.Motion).Count(x =>
                    {
                        Envelope.TryUnwrap(x.datagram, out _, out var payload);
                        return CapnpGen.MotionFrame.READER.create(Capnp.DeserializerState.CreateRoot(
                            MessageDecoder.DecodeMessage(payload.Span))).Part == 0;
                    });
            }
        }

        // pose, dynamics, drive points: part 0 of the latest tick
        public CapnpGen.MotionFrame.READER LastMotion() => SentFrames.Part0(Sent);

        // identity, environment: page 0 of the latest generation
        public CapnpGen.DiscoveryFrame.READER Discovery() => SentFrames.Page0(Sent);

        public string SessionText(string name)
        {
            foreach (var ch in SentFrames.Channels(Sent))
                if (ch.Name == name)
                    foreach (var v in SentFrames.SessionValues(Sent))
                        if (v.Id == ch.Id) return v.Text;
            throw new ArgumentException($"session text {name} missing");
        }

        /// <summary>The largest datagram sent so far, envelope included.</summary>
        public int LargestDatagram() => SentFrames.Sizes(Sent).Max(x => x.bytes);

        public int DiscoveryPages() => SentFrames.Pages(Sent).Count;

        /// <summary>A channel's value in the tick <paramref name="m"/> belongs to, across its parts.</summary>
        public float Channel(CapnpGen.MotionFrame.READER m, string name)
        {
            foreach (var ch in SentFrames.Channels(Sent))
                if (ch.Name == name)
                    foreach (var cv in SentFrames.TickValues(SentFrames.Tick(Sent, m.Timestamp)))
                        if (cv.Id == ch.Id) return cv.Number;
            throw new ArgumentException($"channel {name} missing");
        }
    }

    internal static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    internal static (Capture cap, OgsSession session) Start(ILegacySource src, Dictionary<string, string>? options = null)
    {
        var cap = new Capture();
        var session = cap.MakeSession(src.Id);
        src.Configure(options ?? new Dictionary<string, string>());
        src.Declare(session);
        session.Start(Now());
        return (cap, session);
    }

    internal static void W<T>(byte[] b, int off, T v) where T : struct => MemoryMarshal.Write(b.AsSpan(off), in v);

    internal static void AssertQuat(Quaternion expect, CapnpGen.MotionFrame.READER m, float tol = 1e-4f)
    {
        var o = m.Objects[0].Orientation;
        // q and −q are the same rotation
        float sign = MathF.Sign(expect.W * o.W + expect.X * o.X + expect.Y * o.Y + expect.Z * o.Z);
        AssertEx.ApproxEqual(expect.X, sign * o.X, tol);
        AssertEx.ApproxEqual(expect.Y, sign * o.Y, tol);
        AssertEx.ApproxEqual(expect.Z, sign * o.Z, tol);
        AssertEx.ApproxEqual(expect.W, sign * o.W, tol);
    }

    internal static void Text(byte[] b, int off, string text) => Encoding.UTF8.GetBytes(text).CopyTo(b, off);
}
}
