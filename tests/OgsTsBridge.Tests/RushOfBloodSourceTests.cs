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
/// <summary>Forged-packet translation tests: Rush of Blood cart telemetry (BloodRush shadPS4).</summary>
public class RushOfBloodSourceTests
{
    [Fact]
    public void RushOfBlood_Ticks_Only_In_Play()
    {
        var src = new RushOfBloodSource();
        var (cap, session) = Start(src);
        using var _ = session;
        var b = new byte[RushOfBloodSource.PacketSize];
        W(b, 0, 4.5f);
        // quaternion for yaw 0.5 about Z: (0, 0, sin .25, cos .25)
        W(b, 24, MathF.Sin(0.25f)); W(b, 28, MathF.Cos(0.25f));
        W(b, 32, 0.2f); W(b, 36, 1.0f); W(b, 40, -0.3f);
        W(b, 44, 0u); // not in play
        src.Translate(b, session, Now());
        Assert.Equal(0, cap.Motions);

        W(b, 44, 1u | 2u | 4u);
        src.Translate(b, session, Now());
        var m = cap.LastMotion();
        AssertQuat(OgsMath.EulerToQuaternion(0.5, 0, 0), m);
        AssertEx.ApproxEqual(9.80665f, m.Objects[0].BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual(-0.3f * 9.80665f, m.Objects[0].BodyDynamics.SpecificForce.Z, 1e-4f);
        Assert.Equal(1f, cap.Channel(m, "session.paused"));
        Assert.Equal(1f, cap.Channel(m, "rushofblood.braking"));
        AssertEx.ApproxEqual(4.5f, cap.Channel(m, "rushofblood.speed"), 1e-6f);
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new RushOfBloodSource(), i => { var b = new byte[52]; W(b, 28, 1f); W(b, 0, i * 0.1f); W(b, 44, 1u); return b; });
}
}
