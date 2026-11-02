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
/// <summary>Forged-input translation tests: IL-2 motion and telemetry devices.</summary>
public class Il2SourceTests
{

    [Fact]
    public void Il2_Motion_Ticks_Telemetry_Feeds_Channels()
    {
        var src = new Il2Source();
        var (cap, session) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = session;

        // telemetry: rpm (2 engines), IAS, unknown type 7, AGL
        var t = new List<byte>();
        t.AddRange(BitConverter.GetBytes(Il2Source.TelemetryId));
        t.AddRange(BitConverter.GetBytes((ushort)0)); t.AddRange(BitConverter.GetBytes(1u));
        t.Add(4);
        void Rec(ushort type, params float[] v)
        {
            t.AddRange(BitConverter.GetBytes(type)); t.Add((byte)v.Length);
            foreach (var f in v) t.AddRange(BitConverter.GetBytes(f));
        }
        Rec(0, 2400f, 2300f); Rec(6, 80f); Rec(7, 0.3f); Rec(10, 150f);
        src.Translate(t.ToArray(), session, Now());
        Assert.Equal(0, cap.Motions);

        var b = new byte[44];
        W(b, 0, Il2Source.MotionId);
        W(b, 8, 0.1f); W(b, 12, 0.2f); W(b, 16, 0.3f);
        W(b, 20, 0.4f); W(b, 24, 0.5f); W(b, 28, 0.6f);
        W(b, 32, 3f); W(b, 36, 9f); W(b, 40, 1f);      // fwd, up, right
        src.Translate(b, session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertQuat(OgsMath.EulerToQuaternion(0.1, 0.2, 0.3), m);
        AssertEx.ApproxEqual(1f, obj.BodyDynamics.SpecificForce.X, 1e-6f);
        AssertEx.ApproxEqual(9f, obj.BodyDynamics.SpecificForce.Y, 1e-6f);
        AssertEx.ApproxEqual(3f, obj.BodyDynamics.SpecificForce.Z, 1e-6f);
        AssertEx.ApproxEqual(0.6f, obj.BodyDynamics.AngularVelocity.X, 1e-6f);
        AssertEx.ApproxEqual(-0.5f, obj.BodyDynamics.AngularVelocity.Y, 1e-6f);
        AssertEx.ApproxEqual(0.4f, obj.BodyDynamics.AngularVelocity.Z, 1e-6f);
        AssertEx.ApproxEqual(2400f, cap.Channel(m, "engine.rpm"), 1e-3f);
        AssertEx.ApproxEqual(80f, cap.Channel(m, "air.indicatedAirspeed"), 1e-4f);
        AssertEx.ApproxEqual(150f, cap.Channel(m, "env.heightFromGround"), 1e-4f);
    }
}
}
