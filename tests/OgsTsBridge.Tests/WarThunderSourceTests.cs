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
/// <summary>War Thunder localhost:8111: the /state and /indicators mapping.</summary>
public class WarThunderSourceTests
{
    [Fact]
    public void WarThunder_Maps_State_And_Indicators()
    {
        var src = new WarThunderSource();
        var (cap, session) = Start(src);
        using var _ = session;
        var state = "{\"valid\": true, \"H, m\": 1500, \"TAS, km/h\": 360, \"AoA, deg\": 4.5, \"Ny\": 2.0," +
                    " \"Wx, deg/s\": 10, \"power 1, hp\": 1000, \"compressor stage 1\": 2}";
        var ind = "{\"valid\": true, \"type\": \"spitfire_mk9\", \"compass\": 90, \"aviahorizon_pitch\": 5," +
                  " \"aviahorizon_roll\": -20, \"rpm\": 2650}";
        src.Translate(Encoding.UTF8.GetBytes(state + "\0" + ind), session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        const double deg = Math.PI / 180;
        AssertQuat(OgsMath.EulerToQuaternion(90 * deg, 5 * deg, 20 * deg), m);
        AssertEx.ApproxEqual(2f * 9.80665f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual((float)(10 * deg), obj.BodyDynamics.AngularVelocity.Z, 1e-6f);
        AssertEx.ApproxEqual(1500f, cap.Channel(m, "position.altMsl"), 1e-3f);
        AssertEx.ApproxEqual(100f, cap.Channel(m, "air.trueAirspeed"), 1e-4f);
        AssertEx.ApproxEqual((float)(4.5 * deg), cap.Channel(m, "air.aoa"), 1e-6f);
        AssertEx.ApproxEqual(745700f, cap.Channel(m, "warthunder.power_1"), 1f);           // hp → W
        AssertEx.ApproxEqual(2f, cap.Channel(m, "warthunder.compressor_stage_1"), 1e-6f);  // raw
        AssertEx.ApproxEqual(2650f, cap.Channel(m, "warthunder.ind.rpm"), 1e-3f);
        Assert.Equal("spitfire_mk9", cap.Discovery().Objects[0].TypeLabel);

        int motions = cap.Motions;
        src.Translate(Encoding.UTF8.GetBytes("{\"valid\": false}\0{\"valid\": false}"), session, Now() + 1);
        Assert.Equal(motions, cap.Motions);
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new WarThunderSource(), i => Encoding.UTF8.GetBytes(
            $"{{\"valid\": true, \"TAS, km/h\": {i % 500}, \"Ny\": 1.0}}\0{{\"valid\": true, \"type\": \"yak_3\", \"compass\": {i % 360}}}"));
}
}
