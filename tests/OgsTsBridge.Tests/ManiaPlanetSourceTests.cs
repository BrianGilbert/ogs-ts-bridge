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
/// <summary>Forged-page translation tests: ManiaPlanet telemetry shared memory.</summary>
public class ManiaPlanetSourceTests
{
    [Fact]
    public void ManiaPlanet_Maps_Page_And_Gates_On_Update()
    {
        var src = new ManiaPlanetSource();
        var (cap, session) = Start(src);
        using var _ = session;
        var b = new byte[ManiaPlanetSource.PageSize];
        Text(b, 0, "ManiaPlanet_Telemetry");
        W(b, 32, 3u); W(b, 36, (uint)b.Length); W(b, 40, 10u);
        W(b, 44, 2u);                                    // running
        Text(b, 48, "StadiumCar"); Text(b, 176, "A01-Race");
        W(b, 564, 12345u); W(b, 572, 2u); W(b, 576, 4000u); W(b, 580, 9000u);
        W(b, 1108, 100u);
        W(b, 1116, 1f);                                  // quaternion w (identity)
        W(b, 1132, 5f); W(b, 1136, 1f); W(b, 1140, 20f); // translation (+x left)
        W(b, 1144, 2f); W(b, 1148, 0f); W(b, 1152, 30f); // velocity
        W(b, 1212, 9000f); W(b, 1216, 4); W(b, 1228, 1u); W(b, 1288, 180u);
        src.Translate(b, session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertEx.ApproxEqual(-5f, obj.Position.X, 1e-6f);
        AssertEx.ApproxEqual(20f, obj.Position.Z, 1e-6f);
        AssertEx.ApproxEqual(-2f, obj.Kinematics.Velocity.X, 1e-6f);
        AssertEx.ApproxEqual(50f, cap.Channel(m, "vehicle.speed"), 1e-4f);
        AssertEx.ApproxEqual(9000f, cap.Channel(m, "engine.rpm"), 1e-3f);
        AssertEx.ApproxEqual(4f, cap.Channel(m, "transmission.gear"), 1e-6f);
        AssertEx.ApproxEqual(9f, cap.Channel(m, "maniaplanet.checkpointTime2"), 1e-5f);
        AssertEx.ApproxEqual(12.345f, cap.Channel(m, "race.time"), 1e-5f);
        Assert.Equal(1f, cap.Channel(m, "maniaplanet.wheel.fl.groundContact"));
        Assert.Equal("StadiumCar", cap.Discovery().Objects[0].TypeLabel);
        Assert.Equal("A01-Race", cap.Discovery().Objects[0].Location);

        int motions = cap.Motions;
        src.Translate(b, session, Now() + 0.01);         // same UpdateNumber
        Assert.Equal(motions, cap.Motions);
        W(b, 40, 11u); W(b, 44, 1u);                      // menus
        src.Translate(b, session, Now() + 0.02);
        Assert.Equal(motions, cap.Motions);
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new ManiaPlanetSource(), i =>
        {
            var b = new byte[ManiaPlanetSource.PageSize];
            Text(b, 0, "ManiaPlanet_Telemetry"); W(b, 40, (uint)i); W(b, 44, 2u); W(b, 1116, 1f);
            Text(b, 48, "StadiumCar"); Text(b, 176, "Map"); W(b, 1108, (uint)(i * 10)); W(b, 1212, 1000f + i);
            return b;
        });
}
}
