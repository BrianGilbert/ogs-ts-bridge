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
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>Forged-input translation tests: SCS truck games shared memory.</summary>
public class ScsSourceTests
{

    [Fact]
    public void Scs_Translates_FullPage_And_Gates()
    {
        var src = new ScsSource();
        var (cap, session) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = session;

        var b = new byte[ScsSource.PageSize];
        W(b, 0, 1000u); W(b, 8, 10u);                 // time, plugin revision
        W(b, 24, -12f);                               // speed (reversing)
        W(b, 28, 0.5f); W(b, 32, 0f); W(b, 36, -2f);  // accel: right 0.5, forward 2
        W(b, 40, 100f); W(b, 44, 5f); W(b, 48, 200f); // world placement (Z back)
        W(b, 52, 0.25f); W(b, 56, 0.01f); W(b, 60, 0.02f); // heading (left), pitch, roll
        W(b, 64, 5); W(b, 80, 1400f); W(b, 84, 2500f);
        W(b, 88, 300f); W(b, 92, 600f);
        W(b, 124, 0.6f);
        Text(b, 236, "Machinery");
        b[580] = 1;                                   // cruise control
        W(b, 616, 100f);                              // air pressure psi
        W(b, 680, 12345f);                            // odometer km
        Text(b, 816, "FH16");
        W(b, 1008, 22.2f);                            // speed limit

        src.Translate(b, session, Now());
        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertQuat(OgsMath.EulerToQuaternion(-Math.PI / 2, 0.02 * Math.PI, -0.04 * Math.PI), m);
        AssertEx.ApproxEqual(-200f, obj.Position.Z, 1e-4f);
        AssertEx.ApproxEqual(0.5f, obj.BodyDynamics.SpecificForce.X, 1e-5f);
        AssertEx.ApproxEqual(2f, obj.BodyDynamics.SpecificForce.Z, 1e-5f);
        AssertEx.ApproxEqual(12f, cap.Channel(m, "vehicle.speed"), 1e-5f);
        AssertEx.ApproxEqual(5f, cap.Channel(m, "transmission.gear"), 1e-6f);
        AssertEx.ApproxEqual(0.5f, cap.Channel(m, "fuel.level"), 1e-6f);
        AssertEx.ApproxEqual(689475.7f, cap.Channel(m, "brakes.airPressure"), 1f);
        AssertEx.ApproxEqual(12345000f, cap.Channel(m, "vehicle.odometer"), 1f);
        AssertEx.ApproxEqual(22.2f, cap.Channel(m, "nav.speedLimit"), 1e-4f);
        Assert.Equal("FH16", cap.Discovery().Objects[0].TypeLabel);
        Assert.Equal("Machinery", cap.SessionText("trailer.name"));

        int before = cap.Motions;
        src.Translate(b, session, Now());             // same time stamp
        W(b, 0, 2000u); W(b, 4, 1u);
        src.Translate(b, session, Now());             // paused
        Assert.Equal(before, cap.Motions);
    }
}
}
