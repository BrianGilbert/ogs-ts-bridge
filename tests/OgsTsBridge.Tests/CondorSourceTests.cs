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
/// <summary>Forged-input translation tests: Condor 2 key=value output.</summary>
public class CondorSourceTests
{

    [Fact]
    public void Condor_Learns_Every_Key()
    {
        var src = new CondorSource();
        var (cap, session) = Start(src);
        using var _ = session;

        var text = "time=12.5\r\nairspeed=28.5\r\naltitude=1200\r\nvario=1.5\r\nyaw=1.0\r\npitch=0.05\r\n" +
                   "bank=-0.3\r\nrollrate=0.1\r\npitchrate=0.2\r\nyawrate=0.3\r\ngforce=1.4\r\nturbulencestrength=0.7\r\n";
        src.Translate(Encoding.ASCII.GetBytes(text), session, Now());
        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertQuat(OgsMath.EulerToQuaternion(1.0, 0.05, -0.3), m);
        AssertEx.ApproxEqual(1.4f * 9.80665f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual(0.2f, obj.BodyDynamics.AngularVelocity.X, 1e-6f);
        AssertEx.ApproxEqual(0.3f, obj.BodyDynamics.AngularVelocity.Y, 1e-6f);
        AssertEx.ApproxEqual(0.1f, obj.BodyDynamics.AngularVelocity.Z, 1e-6f);
        AssertEx.ApproxEqual(28.5f, cap.Channel(m, "air.indicatedAirspeed"), 1e-4f);
        AssertEx.ApproxEqual(1200f, cap.Channel(m, "position.altMsl"), 1e-3f);
        AssertEx.ApproxEqual(0.7f, cap.Channel(m, "condor.turbulencestrength"), 1e-5f);
        AssertEx.ApproxEqual(12.5f, cap.Channel(m, "condor.time"), 1e-5f);

        // a key appearing later grows the channel set
        src.Translate(Encoding.ASCII.GetBytes(text + "wheelheight=0.4\n"), session, Now() + 1.0);
        AssertEx.ApproxEqual(0.4f, cap.Channel(cap.LastMotion(), "condor.wheelheight"), 1e-5f);
    }
}
}
