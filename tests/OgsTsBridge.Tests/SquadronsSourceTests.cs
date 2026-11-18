#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OgsTsBridge.Sources;
using Xunit;
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>Star Wars: Squadrons: the raw channels and the opt-in motion mapping.</summary>
public class SquadronsSourceTests
{
    // sample order: speed, acceleration, pitch, roll, yaw
    static byte[] Sample(params float[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) MemoryMarshal.Write(b.AsSpan(i * 4), v[i]);
        return b;
    }

    [Fact]
    public void Squadrons_Publishes_Raw_Channels()
    {
        var src = new SquadronsSource();
        var (cap, session) = Start(src);
        using var _ = session;
        Assert.Equal(new[] { "squadrons.speed", "squadrons.acceleration", "squadrons.pitch", "squadrons.roll", "squadrons.yaw" },
            SentFrames.Channels(cap.Sent).Select(c => c.Name).ToList());
        Assert.Equal(5 * 4, src.PollBufferSize);

        src.Translate(Sample(310f, 12f, 0.2f, -0.1f, 0.01f), session, Now());
        var m = cap.LastMotion();
        AssertEx.ApproxEqual(310f, cap.Channel(m, "squadrons.speed"), 1e-4f);
        AssertEx.ApproxEqual(12f, cap.Channel(m, "squadrons.acceleration"), 1e-4f);
    }

    [Fact]
    public void Squadrons_Motion_Integrates_Yaw_And_Maps_Surge()
    {
        var src = new SquadronsSource();
        var (cap, session) = Start(src, new() { ["motion"] = "true" });
        using var _ = session;

        src.Translate(Sample(0f, 500f, 0.2f, -0.1f, 0.1f), session, Now() + 1);
        src.Translate(Sample(0f, 500f, 0.2f, -0.1f, 0.1f), session, Now() + 2);
        var m = cap.LastMotion();
        AssertQuat(OgsMath.EulerToQuaternion(0.2, 0.2, -0.1), m);   // yaw accumulated twice
        AssertEx.ApproxEqual(100f, m.Objects[0].BodyDynamics.SpecificForce.Z, 1e-4f);   // clamped
    }

    [Fact]
    public void Squadrons_Without_Game_Does_Not_Connect()
    {
        Assert.False(new SquadronsSource().Connect());
    }
}
}
