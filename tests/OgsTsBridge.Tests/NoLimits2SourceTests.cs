#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OgsTsBridge.Sources;
using Xunit;
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>NoLimits 2 telemetry server: the reply mapping and frame gating.</summary>
public class NoLimits2SourceTests
{
    [Fact]
    public void NoLimits2_Maps_Reply_And_Gates_On_Frame()
    {
        var src = new NoLimits2Source();
        var (cap, session) = Start(src);
        using var _ = session;
        var d = new byte[NoLimits2Source.DataSize];
        void I(int off, int v) => BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(off), v);
        void F(int off, float v) => BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(off), v);
        I(0, 1 | 8); I(4, 500); I(12, 2); I(28, 3);
        F(32, 25f); F(36, 1f); F(40, 30f); F(44, -2f);
        F(60, 1f);                            // quaternion w
        F(64, 0.1f); F(68, 3.5f); F(72, -0.4f);
        src.Translate(d, session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertEx.ApproxEqual(30f, obj.Position.Y, 1e-5f);
        AssertEx.ApproxEqual(3.5f * 9.80665f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual(25f, cap.Channel(m, "vehicle.speed"), 1e-5f);
        AssertEx.ApproxEqual(3f, cap.Channel(m, "nolimits2.seat"), 1e-6f);
        Assert.Equal(1f, cap.Channel(m, "nolimits2.vrHeadset"));

        int motions = cap.Motions;
        src.Translate(d, session, Now() + 0.01);   // same frame: nothing new
        Assert.Equal(motions, cap.Motions);
        I(4, 501);
        src.Translate(d, session, Now() + 0.02);
        Assert.Equal(motions + 1, cap.Motions);
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new NoLimits2Source(), i =>
        {
            var b = new byte[NoLimits2Source.DataSize];
            BinaryPrimitives.WriteInt32BigEndian(b, 1);
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), i);
            BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(60), 1f);
            return b;
        });
}
}
