#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using OgsTsBridge.Sources;
using Ogs.Telemetry;
using Xunit;

namespace OgsTsBridge.Tests
{
/// <summary>Forged KartKraft FlatBuffers frames (schema: the developer's
/// open Frame.fbs) through the source.</summary>
public class KartKraftTests
{
    /// <summary>Minimal forward-layout FlatBuffers writer: every scalar
    /// takes a 4-byte slot, children are appended after their parent and
    /// patched in by relative offset.</summary>
    sealed class Fbb
    {
        public readonly List<byte> Buf = new();

        public int Table(int slots, IDictionary<int, uint> scalars, out Dictionary<int, int> fieldPos, params int[] links)
        {
            while (Buf.Count % 4 != 0) Buf.Add(0);
            int vt = Buf.Count;
            int vsize = 4 + 2 * slots;
            int tsize = 4 + 4 * slots;
            Buf.AddRange(BitConverter.GetBytes((ushort)vsize));
            Buf.AddRange(BitConverter.GetBytes((ushort)tsize));
            for (int i = 0; i < slots; i++)
                Buf.AddRange(BitConverter.GetBytes(
                    (ushort)(scalars.ContainsKey(i) || links.Contains(i) ? 4 + 4 * i : 0)));   // absent slot = 0
            while (Buf.Count % 4 != 0) Buf.Add(0);
            int pos = Buf.Count;
            Buf.AddRange(BitConverter.GetBytes(pos - vt));
            fieldPos = new Dictionary<int, int>();
            for (int i = 0; i < slots; i++)
            {
                fieldPos[i] = Buf.Count;
                Buf.AddRange(BitConverter.GetBytes(scalars.TryGetValue(i, out var v) ? v : 0u));
            }
            return pos;
        }

        public void Link(int fieldPos, int target) =>
            Patch(fieldPos, BitConverter.GetBytes(target - fieldPos));

        public void Patch(int at, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) Buf[at + i] = bytes[i]; }

        public int String(string s)
        {
            while (Buf.Count % 4 != 0) Buf.Add(0);
            int pos = Buf.Count;
            var u = Encoding.UTF8.GetBytes(s);
            Buf.AddRange(BitConverter.GetBytes(u.Length));
            Buf.AddRange(u);
            Buf.Add(0);
            return pos;
        }
    }

    static uint F(float v) => BitConverter.ToUInt32(BitConverter.GetBytes(v));

    static byte[] Forge(bool withDash = true, bool withMotion = true)
    {
        var b = new Fbb();
        b.Buf.AddRange(new byte[8]);                      // root offset, then the identifier
        b.Buf[4] = (byte)'K'; b.Buf[5] = (byte)'K'; b.Buf[6] = (byte)'F'; b.Buf[7] = (byte)'B';

        int frame = b.Table(7, new Dictionary<int, uint> { [0] = F(12.5f) }, out var fp,
            new[] { 5 }.Concat(withMotion ? new[] { 1 } : Array.Empty<int>()).Concat(withDash ? new[] { 2 } : Array.Empty<int>()).ToArray());
        b.Patch(0, BitConverter.GetBytes(frame));

        if (withMotion)
        {
            int motion = b.Table(20, new Dictionary<int, uint>
            {
                [0] = F(10f), [1] = F(20f), [2] = F(30f),            // pitch, roll, yaw (deg)
                [3] = F(1f), [4] = F(2f), [5] = F(3f),               // accel fwd, right, up
                [10] = F(90f), [11] = F(45f), [12] = F(180f),        // roll, pitch, yaw rate (deg/s)
            }, out var mp, 13);
            b.Link(fp[1], motion);

            int wheels = b.Buf.Count;
            b.Buf.AddRange(BitConverter.GetBytes(4));
            var slots = new int[4];
            for (int i = 0; i < 4; i++) { slots[i] = b.Buf.Count; b.Buf.AddRange(new byte[4]); }
            b.Link(mp[13], wheels);
            for (int i = 0; i < 4; i++)
            {
                int w = b.Table(2, new Dictionary<int, uint> { [0] = (uint)(i + 1), [1] = F(i * 2f) }, out _);
                b.Link(slots[i], w);
            }
        }

        if (withDash)
        {
            int dash = b.Table(12, new Dictionary<int, uint>
            {
                [0] = F(25f), [1] = F(9000f), [2] = F(-30f), [3] = F(0.8f), [4] = F(0.1f),
                [5] = 0xFF /* gear -1 */, [6] = 3, [10] = 4, [11] = 2,
            }, out _);
            b.Link(fp[2], dash);
        }

        int track = b.Table(5, new Dictionary<int, uint> { [1] = 3, [2] = F(1200f) }, out var tp);
        b.Link(fp[5], track);
        b.Link(tp[0], b.String("Brands Hatch"));
        return b.Buf.ToArray();
    }

    sealed class Capture
    {
        public readonly List<(FrameType type, byte[] datagram)> Sent = new();
        public OgsSession MakeSession() => new((t, d) => Sent.Add((t, d)), "KartKraft");
        public CapnpGen.MotionFrame.READER LastMotion()
        {
            var d = Sent.Last(x => x.type == FrameType.Motion).datagram;
            Envelope.TryUnwrap(d, out _, out var payload);
            return CapnpGen.MotionFrame.READER.create(
                Capnp.DeserializerState.CreateRoot(MessageDecoder.DecodeMessage(payload.Span)));
        }
        public CapnpGen.MotionFrame.READER Part0() => SentFrames.Part0(Sent);

        public float Channel(string name)
        {
            foreach (var ch in SentFrames.Channels(Sent))
                if (ch.Name == name)
                    foreach (var cv in SentFrames.TickValues(SentFrames.Tick(Sent)))
                        if (cv.Id == ch.Id) return cv.Number;
            throw new ArgumentException($"channel {name} missing");
        }
    }

    static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    static (Capture cap, OgsSession s, KartKraftSource src) Setup(Dictionary<string, string>? opts = null)
    {
        var cap = new Capture();
        var s = cap.MakeSession();
        var src = new KartKraftSource();
        src.Configure(opts ?? new());
        src.Declare(s);
        s.Start(Now());
        return (cap, s, src);
    }

    [Fact]
    public void Translates_Pose_Dynamics_And_Channels()
    {
        var (cap, s, src) = Setup(new() { ["gravIncluded"] = "true" });
        using var _ = s;
        src.Translate(Forge(), s, Now());
        var m = cap.LastMotion();
        var obj = m.Objects[0];

        const double D = Math.PI / 180;
        var q = OgsMath.EulerToQuaternion(30 * D, 10 * D, 20 * D);
        AssertEx.ApproxEqual(q.X, obj.Orientation.X, 1e-4f);
        AssertEx.ApproxEqual(q.Y, obj.Orientation.Y, 1e-4f);
        AssertEx.ApproxEqual(q.W, obj.Orientation.W, 1e-4f);

        // local accel (fwd 1, right 2, up 3) -> OGS (right, up, fwd)
        AssertEx.ApproxEqual(2f, obj.BodyDynamics.SpecificForce.X, 1e-4f);
        AssertEx.ApproxEqual(3f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual(1f, obj.BodyDynamics.SpecificForce.Z, 1e-4f);
        // rates (pitch, yaw, roll) from (X roll 90, Y pitch 45, Z yaw 180) deg/s
        AssertEx.ApproxEqual(45f * (float)D, obj.BodyDynamics.AngularVelocity.X, 1e-4f);
        AssertEx.ApproxEqual(180f * (float)D, obj.BodyDynamics.AngularVelocity.Y, 1e-4f);
        AssertEx.ApproxEqual(90f * (float)D, obj.BodyDynamics.AngularVelocity.Z, 1e-4f);

                AssertEx.ApproxEqual(25f, cap.Channel("vehicle.speed"), 1e-4f);
        AssertEx.ApproxEqual(9000f, cap.Channel("engine.rpm"), 1e-2f);
        AssertEx.ApproxEqual(-1f, cap.Channel("transmission.gear"), 1e-4f);
        AssertEx.ApproxEqual(3f, cap.Channel("race.position"), 1e-4f);
        AssertEx.ApproxEqual(4f, cap.Channel("lap.number"), 1e-4f);
        AssertEx.ApproxEqual(1200f, cap.Channel("track.length"), 1e-2f);
        AssertEx.ApproxEqual(3f, cap.Channel("kartkraft.wheel.fr.surface"), 1e-4f);   // third wheel: FR
        AssertEx.ApproxEqual(6f, cap.Channel("kartkraft.wheel.rr.slipAngle"), 1e-4f);
    }

    [Fact]
    public void Gravity_Is_Added_Unless_Included()
    {
        var (cap, s, src) = Setup();
        using var _ = s;
        src.Translate(Forge(), s, Now());
        var f = cap.LastMotion().Objects[0].BodyDynamics.SpecificForce;
        // the gravity reaction shifts the vector away from the raw (right 2, up 3, fwd 1)
        Assert.True(Math.Abs(f.Y - 3f) > 1f || Math.Abs(f.X - 2f) > 1f || Math.Abs(f.Z - 1f) > 1f);
    }

    [Fact]
    public void Foreign_And_Truncated_Datagrams_Never_Throw()
    {
        var (cap, s, src) = Setup();
        using var _ = s;
        var good = Forge();
        src.Translate(new byte[64], s, Now());                       // no identifier
        Assert.DoesNotContain(cap.Sent, x => x.type == FrameType.Motion);
        for (int len = 16; len < good.Length; len += 7)
            src.Translate(good.AsSpan(0, len), s, Now());            // truncations
    }

    [Fact]
    public void Dash_Only_Frame_Ticks_Once()
    {
        var (cap, s, src) = Setup();
        using var _ = s;
        int before = cap.Sent.Count(x => x.type == FrameType.Motion);
        src.Translate(Forge(withMotion: false), s, Now());
        Assert.Equal(before + 1, cap.Sent.Count(x => x.type == FrameType.Motion));
    }

    [Fact]
    public void Empty_Frame_Does_Not_Tick()
    {
        var (cap, s, src) = Setup();
        using var _ = s;
        int before = cap.Sent.Count(x => x.type == FrameType.Motion);
        src.Translate(Forge(withDash: false, withMotion: false), s, Now());
        Assert.Equal(before, cap.Sent.Count(x => x.type == FrameType.Motion));
    }
}
}
