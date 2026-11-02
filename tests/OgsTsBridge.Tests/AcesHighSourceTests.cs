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
/// <summary>Forged-input translation tests: Aces High III flight data CSV.</summary>
public class AcesHighSourceTests
{
    const double Deg = Math.PI / 180;

    [Fact]
    public void AcesHigh_Maps_Documented_Fields()
    {
        var src = new AcesHighSource();
        var (cap, session) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = session;

        // Version,clock,SimType,x,y,z,xvel,yvel,zvel,xacc,yacc,zacc,roll,pitch,yaw,rollvel,pitchvel,yawvel,rollacc,pitchacc,yawacc
        var line = "30309,1234,1,0,1000,0,10,0,200,1,32.174,2,5,10,30,20,3,4,40,6,8\n";
        src.Translate(Encoding.ASCII.GetBytes(line), session, Now());
        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertQuat(OgsMath.EulerToQuaternion(-30 * Deg, 10 * Deg, 5 * Deg), m);
        AssertEx.ApproxEqual(0.3048f, obj.BodyDynamics.SpecificForce.X, 1e-5f);
        AssertEx.ApproxEqual(32.174f * 0.3048f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual((float)(3 * Deg), obj.BodyDynamics.AngularVelocity.X, 1e-6f);   // pitch
        AssertEx.ApproxEqual((float)(-4 * Deg), obj.BodyDynamics.AngularVelocity.Y, 1e-6f);  // yaw, + left → OGS + right
        AssertEx.ApproxEqual((float)(20 * Deg), obj.BodyDynamics.AngularVelocity.Z, 1e-6f);  // roll
        AssertEx.ApproxEqual(304.8f, cap.Channel(m, "position.altMsl"), 1e-3f);
        AssertEx.ApproxEqual(200 * 0.3048f, cap.Channel(m, "velocity.body.z"), 1e-4f);
        AssertEx.ApproxEqual((float)(-8 * Deg), cap.Channel(m, "aceshigh.angularAccel.y"), 1e-6f);

        // not in flight: no motion
        int motions = cap.Motions;
        src.Translate(Encoding.ASCII.GetBytes("30309,1235,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n"), session, Now() + 1);
        Assert.Equal(motions, cap.Motions);
        // malformed: dropped
        src.Translate(Encoding.ASCII.GetBytes("30309,x,1\n"), session, Now() + 2);
        Assert.Equal(motions, cap.Motions);
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new AcesHighSource(), i => Encoding.ASCII.GetBytes(
            $"30309,{i},1,0,1000,0,10,0,200,1,32.1,2,5,{i % 30},30,20,3,4,40,6,8\n"));
}
}
