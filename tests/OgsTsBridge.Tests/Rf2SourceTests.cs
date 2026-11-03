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
/// <summary>Forged-input translation tests: rFactor 2 / Le Mans Ultimate shared memory.</summary>
public class Rf2SourceTests
{

    static byte[] ForgeRf2Vehicle(int id)
    {
        var v = new byte[1888];
        W(v, 0, id);
        W(v, 4, 0.01);                                      // delta time
        Text(v, 32, "Oreca 07");
        W(v, 160, 5.0); W(v, 168, 1.0); W(v, 176, -20.0);   // world position (+X left, +Z back)
        W(v, 184, 0.0); W(v, 192, 0.0); W(v, 200, -40.0);   // local velocity 40 m/s forward
        W(v, 208, -1.0); W(v, 216, 0.0); W(v, 224, -3.0);   // local accel: 1 right, 3 fwd
        // identity orientation rows
        W(v, 232, 1.0); W(v, 264, 1.0); W(v, 296, 1.0);
        W(v, 304, 0.1); W(v, 312, 0.2); W(v, 320, 0.3);     // local rot
        W(v, 352, 3);                                       // gear
        W(v, 356, 8000.0);                                  // rpm
        W(v, 372, 110.0);                                   // oil temp
        W(v, 404, 0.5);                                     // steering
        W(v, 524, 20.0); W(v, 608, 80.0);                   // fuel / capacity
        W(v, 600, unchecked((int)0x80000002));              // sector 2, in pit lane
        Text(v, 620, "Soft");
        W(v, 692, 540f);                                    // physical wheel range (deg)
        int w = 848;                                        // FL wheel
        W(v, w, 0.03);                                      // deflection
        W(v, w + 24, 600.0);                                // brake temp
        W(v, w + 40, -10.0);                                // rotation rad/s
        W(v, w + 104, 4000.0);                              // tyre load
        W(v, w + 120, 170.0);                               // pressure kPa
        W(v, w + 128, 353.15);                              // tread left 80 °C
        W(v, w + 152, 0.95);                                // wear
        v[w + 176] = 5;                                     // rumble strip
        W(v, w + 196, 0.05);                                // toe
        W(v, w + 204, 363.15);                              // carcass 90 °C
        return v;
    }

    static MemoryMappedViewAccessor Map(string name, long size, out MemoryMappedFile file)
    {
        file = MemoryMappedFile.CreateNew(name, size);
        return file.CreateViewAccessor();
    }

    [Fact]
    public void Rf2_Gathers_PlayerSlot_Scoring_Extended_And_Translates()
    {
        var tag = Guid.NewGuid().ToString("N");
        using var tview = Map("ogs-test-rf2-tel-" + tag, 16 + 3L * 1888, out var tfile);
        using var sview = Map("ogs-test-rf2-sco-" + tag, 560 + 3L * 584, out var sfile);
        using var xview = Map("ogs-test-rf2-ext-" + tag, 8 + 10016, out var xfile);
        using var fview = Map("ogs-test-rf2-ffb-" + tag, 16, out var ffile);
        using var f1 = tfile; using var f2 = sfile; using var f3 = xfile; using var f4 = ffile;

        // telemetry: three cars, player (id 42) in slot 2
        tview.Write(0, 7u); tview.Write(4, 7u); tview.Write(12, 3);
        for (int i = 0; i < 3; i++)
        {
            var v = ForgeRf2Vehicle(i == 2 ? 42 : 100 + i);
            tview.WriteArray(16 + i * 1888L, v, 0, v.Length);
        }
        // scoring: track name, player flagged in scoring slot 1, in realtime
        var track = Encoding.UTF8.GetBytes("Le Mans");
        sview.WriteArray(12, track, 0, track.Length);
        sview.Write(12 + 228, 21.5);                        // ambient temp
        sview.Write(127, (byte)1); sview.Write(116, 3);
        for (int i = 0; i < 3; i++) sview.Write(560 + i * 584L, i == 1 ? 42 : 100 + i);
        sview.Write(560 + 584L + 196, (byte)1);
        sview.Write(560 + 584L + 199, (byte)4);             // place
        var driver = Encoding.UTF8.GetBytes("Test Driver");
        sview.WriteArray(560 + 584L + 4, driver, 0, driver.Length);
        // extended: TC level 2, damage for id 42, status message; FFB value
        xview.Write(8 + 16, (byte)2);
        xview.Write(8 + 56 + 42 * 16L, 12.5);
        var status = Encoding.UTF8.GetBytes("Pit lane open");
        xview.WriteArray(8 + 9316, status, 0, status.Length);
        fview.Write(8, -0.35);

        var src = new Rf2Source();
        var (cap, session) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = session;
        var buffer = new byte[src.SharedMemorySize];
        var aux = new MemoryMappedViewAccessor?[] { sview, xview, fview };

        int n = src.Gather(tview, aux, buffer);
        Assert.Equal(src.SharedMemorySize, n);
        Assert.Equal(42, MemoryMarshal.Read<int>(buffer.AsSpan(Rf2Source.V)));

        src.Translate(buffer.AsSpan(0, n), session, Now());
        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertQuat(OgsMath.EulerToQuaternion(0, 0, 0), m);
        AssertEx.ApproxEqual(-5f, obj.Position.X, 1e-4f);
        AssertEx.ApproxEqual(20f, obj.Position.Z, 1e-4f);
        AssertEx.ApproxEqual(1f, obj.BodyDynamics.SpecificForce.X, 1e-5f);
        AssertEx.ApproxEqual(3f, obj.BodyDynamics.SpecificForce.Z, 1e-5f);
        AssertEx.ApproxEqual(-0.1f, obj.BodyDynamics.AngularVelocity.X, 1e-5f);
        AssertEx.ApproxEqual(0.2f, obj.BodyDynamics.AngularVelocity.Y, 1e-5f);
        AssertEx.ApproxEqual(-0.3f, obj.BodyDynamics.AngularVelocity.Z, 1e-5f);

        var fl = obj.DrivePoints[0].Wheel;
        Assert.Equal(CapnpGen.ContactMedium.rumbleStrip, fl.Contact);
        AssertEx.ApproxEqual((float)(10 * 60 / (2 * Math.PI)), fl.Rpm.Value, 0.01f);
        AssertEx.ApproxEqual(0.05f, fl.SteerAngle.Value, 1e-6f);
        AssertEx.ApproxEqual(4000f, fl.LoadForce.Value, 1e-3f);
        AssertEx.ApproxEqual(170000f, fl.Tyre.Pressure, 0.5f);
        AssertEx.ApproxEqual(90f, fl.Tyre.Temperature, 1e-3f);
        AssertEx.ApproxEqual(0.95f, fl.Tyre.Wear, 1e-6f);

        AssertEx.ApproxEqual(8000f, cap.Channel(m, "engine.rpm"), 0.1f);
        AssertEx.ApproxEqual(110f, cap.Channel(m, "engine.oilTemp"), 1e-4f);
        AssertEx.ApproxEqual(40f, cap.Channel(m, "vehicle.speed"), 1e-4f);
        AssertEx.ApproxEqual(0.25f, cap.Channel(m, "fuel.level"), 1e-6f);
        AssertEx.ApproxEqual((float)(0.5 * 270 * Math.PI / 180), cap.Channel(m, "ctrl.steering.angle"), 1e-4f);
        AssertEx.ApproxEqual(2f, cap.Channel(m, "lap.sector"), 1e-6f);
        AssertEx.ApproxEqual(600f, cap.Channel(m, "brakes.fl.temp"), 1e-3f);
        AssertEx.ApproxEqual(80f, cap.Channel(m, "tyre.fl.tempLeft"), 1e-3f);
        AssertEx.ApproxEqual(21.5f, cap.Channel(m, "weather.ambientTemp"), 1e-4f);
        AssertEx.ApproxEqual(4f, cap.Channel(m, "race.position"), 1e-6f);
        AssertEx.ApproxEqual(2f, cap.Channel(m, "aids.tcLevel"), 1e-6f);
        AssertEx.ApproxEqual(12.5f, cap.Channel(m, "damage.maxImpact"), 1e-4f);
        AssertEx.ApproxEqual(-0.35f, cap.Channel(m, "ctrl.ffb.output"), 1e-6f);

        var disc = cap.Discovery();
        Assert.Equal("Le Mans", disc.Objects[0].Location);
        Assert.Equal("Oreca 07", disc.Objects[0].TypeLabel);
        Assert.Equal("Test Driver", cap.SessionText("driver.name"));
        Assert.Equal("Soft", cap.SessionText("tyre.compoundFront"));
        Assert.Equal("Pit lane open", cap.SessionText("message.status"));

        // unchanged version -> nothing; mid-write version block -> nothing
        Assert.Equal(0, src.Gather(tview, aux, buffer));
        tview.Write(0, 8u);
        Assert.Equal(0, src.Gather(tview, aux, buffer));
        tview.Write(4, 8u);
        Assert.Equal(src.SharedMemorySize, src.Gather(tview, aux, buffer));
    }

    [Fact]
    public void Rf2_EmitsNothing_OutsideRealtime()
    {
        var src = new Rf2Source();
        var (cap, session) = Start(src);
        using var _ = session;
        var b = new byte[src.SharedMemorySize];
        b[4] = 0;
        src.Translate(b, session, Now());
        Assert.Equal(0, cap.Motions);
    }
}
}
