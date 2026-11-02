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
/// <summary>Forged-input translation tests: X-Plane DATA rows.</summary>
public class XPlaneSourceTests
{

    static void Row(List<byte> d, int index, params float[] values)
    {
        d.AddRange(BitConverter.GetBytes(index));
        for (int i = 0; i < 8; i++) d.AddRange(BitConverter.GetBytes(i < values.Length ? values[i] : -999f));
    }

    [Fact]
    public void XPlane_Translates_DataRows()
    {
        var src = new XPlaneSource();
        var (cap, session) = Start(src);
        using var _ = session;

        var d = new List<byte>(Encoding.ASCII.GetBytes("DATA*"));
        Row(d, 3, 100f, 0f, 110f, 120f);
        Row(d, 4, 0.2f, 0f, 600f, 0f, 1.1f, 0.2f, -0.1f);
        Row(d, 16, 0.01f, 0.02f, 0.03f);
        Row(d, 17, 5f, 10f, 90f, 88f);
        Row(d, 99, 1f);                               // unknown row ignored
        var pkt = d.ToArray();
        Assert.True(src.AcceptsDatagram(pkt.Length));

        src.Translate(pkt, session, Now());
        var m = cap.LastMotion();
        var obj = m.Objects[0];
        const double deg = Math.PI / 180;
        AssertQuat(OgsMath.EulerToQuaternion(90 * deg, 5 * deg, 10 * deg), m);
        AssertEx.ApproxEqual(-0.1f * 9.80665f, obj.BodyDynamics.SpecificForce.X, 1e-4f);
        AssertEx.ApproxEqual(1.1f * 9.80665f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual(0.2f * 9.80665f, obj.BodyDynamics.SpecificForce.Z, 1e-4f);
        AssertEx.ApproxEqual(0.01f, obj.BodyDynamics.AngularVelocity.X, 1e-6f); // Q
        AssertEx.ApproxEqual(0.03f, obj.BodyDynamics.AngularVelocity.Y, 1e-6f); // R
        AssertEx.ApproxEqual(0.02f, obj.BodyDynamics.AngularVelocity.Z, 1e-6f); // P
        AssertEx.ApproxEqual(100f * 0.514444f, cap.Channel(m, "air.indicatedAirspeed"), 1e-3f);
        AssertEx.ApproxEqual(120f * 0.514444f, cap.Channel(m, "vehicle.speed"), 1e-3f);
        AssertEx.ApproxEqual(600f * 0.3048f / 60f, cap.Channel(m, "air.verticalSpeed"), 1e-4f);

        Assert.False(src.AcceptsDatagram(5 + 35));
    }
}
}
