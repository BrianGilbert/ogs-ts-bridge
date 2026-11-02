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
/// <summary>Forged-input translation tests: RaceRoom shared memory.</summary>
public class R3eSourceTests
{

    static byte[] ForgeR3e(int ticks)
    {
        var b = new byte[R3eSource.PageSize];
        W(b, 0, 2);                                   // VersionMajor
        W(b, 36, ticks);                              // GameSimulationTicks
        W(b, 48, 10.0); W(b, 56, 2.0); W(b, 64, -30.0);  // position (+X left, +Z back)
        W(b, 144, 1.0); W(b, 152, 0.0); W(b, 160, -2.0); // local accel: 1 left, 2 forward
        W(b, 168, 0.1); W(b, 176, 0.2); W(b, 184, 0.3);  // pitch, yaw, roll
        W(b, 264, 0.4); W(b, 272, 0.5); W(b, 280, 0.6);  // local angular velocity
        W(b, 384, 0.02);                              // suspension deflection FL
        W(b, 416, 0.1);                               // suspension velocity FL
        Text(b, 576, "Spa-Francorchamps"); Text(b, 640, "Grand Prix");
        W(b, 956, 3);                                 // position
        W(b, 1036, 135.5f);                           // best lap
        W(b, 1052, -1f);                              // previous lap: N/A
        Text(b, 1156, "BMW M4 GT3"); Text(b, 1268, "Test Driver");
        W(b, 1336, 50f);                              // CarSpeed
        W(b, 1340, 600f);                             // EngineRps (rad/s)
        W(b, 1352, 4);                                // Gear
        W(b, 1400, 30f); W(b, 1404, 60f);             // fuel left / capacity
        W(b, 1424, 400f);                             // oil pressure kPa
        W(b, 1432, 0.8f);                             // throttle
        W(b, 1456, 0.5f); W(b, 1460, 20); W(b, 1464, 540); // steer raw, lock, wheel range
        W(b, 1468, 5);                                // ABS active
        W(b, 1564, 100f);                             // tyre rps FL
        W(b, 1612, 0.9f);                             // tyre wear FL
        W(b, 1644, 180f);                             // tyre pressure FL kPa
        W(b, 1676, 80f); W(b, 1680, 90f); W(b, 1684, 100f); // tread temps FL
        W(b, 1788, 450f);                             // brake temp FL
        W(b, 1852, 2f);                               // brake force FL kN
        W(b, 1884, 5);                                // FL on rumble
        W(b, 1900, 3000f);                            // tyre load FL
        return b;
    }

    [Fact]
    public void R3e_Translates_FullPage_And_Gates()
    {
        var src = new R3eSource();
        var (cap, session) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = session;

        src.Translate(ForgeR3e(10), session, Now());
        var m = cap.LastMotion();
        var obj = m.Objects[0];

        AssertQuat(OgsMath.EulerToQuaternion(0.2, -0.1, -0.3), m);
        AssertEx.ApproxEqual(-10f, obj.Position.X, 1e-4f);
        AssertEx.ApproxEqual(30f, obj.Position.Z, 1e-4f);
        AssertEx.ApproxEqual(-1f, obj.BodyDynamics.SpecificForce.X, 1e-5f);
        AssertEx.ApproxEqual(2f, obj.BodyDynamics.SpecificForce.Z, 1e-5f);
        AssertEx.ApproxEqual(-0.4f, obj.BodyDynamics.AngularVelocity.X, 1e-5f);
        AssertEx.ApproxEqual(0.5f, obj.BodyDynamics.AngularVelocity.Y, 1e-5f);
        AssertEx.ApproxEqual(-0.6f, obj.BodyDynamics.AngularVelocity.Z, 1e-5f);

        var fl = obj.DrivePoints[0].Wheel;
        AssertEx.ApproxEqual(0.02f, fl.Compression, 1e-6f);
        AssertEx.ApproxEqual((float)(100 * 60 / (2 * Math.PI)), fl.Rpm, 0.01f);
        Assert.Equal(CapnpGen.ContactMedium.rumbleStrip, fl.Contact);
        AssertEx.ApproxEqual((float)(0.5 * 20 * Math.PI / 180), fl.Extended.SteerAngle, 1e-5f);
        AssertEx.ApproxEqual(3000f, fl.Extended.LoadForce, 1e-3f);
        AssertEx.ApproxEqual(0.1f, fl.Extended.DamperVelocity, 1e-6f);
        AssertEx.ApproxEqual(180000f, fl.Extended.TyrePressure, 0.5f);
        AssertEx.ApproxEqual(90f, fl.Extended.TyreTemp, 1e-4f);
        AssertEx.ApproxEqual(0.9f, fl.Extended.TyreWear, 1e-6f);

        AssertEx.ApproxEqual((float)(600 * 60 / (2 * Math.PI)), cap.Channel(m, "engine.rpm"), 0.1f);
        AssertEx.ApproxEqual(50f, cap.Channel(m, "vehicle.speed"), 1e-4f);
        AssertEx.ApproxEqual(4f, cap.Channel(m, "transmission.gear"), 1e-6f);
        AssertEx.ApproxEqual(0.5f, cap.Channel(m, "fuel.level"), 1e-6f);
        AssertEx.ApproxEqual(400000f, cap.Channel(m, "engine.oilPressure"), 0.5f);
        AssertEx.ApproxEqual((float)(0.5 * 270 * Math.PI / 180), cap.Channel(m, "ctrl.steering.angle"), 1e-4f);
        AssertEx.ApproxEqual(3f, cap.Channel(m, "race.position"), 1e-6f);
        AssertEx.ApproxEqual(135.5f, cap.Channel(m, "lap.bestTime"), 1e-4f);
        AssertEx.ApproxEqual(450f, cap.Channel(m, "brakes.fl.temp"), 1e-4f);
        AssertEx.ApproxEqual(2000f, cap.Channel(m, "brakes.fl.force"), 1e-3f);
        AssertEx.ApproxEqual(80f, cap.Channel(m, "tyre.fl.tempLeft"), 1e-4f);
        Assert.Throws<ArgumentException>(() => cap.Channel(m, "lap.lastTime")); // -1 = N/A: never sent

        var disc = cap.Discovery();
        Assert.Equal("Spa-Francorchamps", disc.Objects[0].Location);
        Assert.Equal("BMW M4 GT3", disc.Objects[0].TypeLabel);
        Assert.Equal("Grand Prix", cap.SessionText("session.layout"));
        Assert.Equal("Test Driver", cap.SessionText("driver.name"));

        int before = cap.Motions;
        src.Translate(ForgeR3e(10), session, Now());     // same tick
        var paused = ForgeR3e(11); W(paused, 16, 1);
        src.Translate(paused, session, Now());           // paused
        Assert.Equal(before, cap.Motions);
        src.Translate(ForgeR3e(12), session, Now() + 1.0); // past the max-hz guard
        Assert.Equal(before + 1, cap.Motions);
    }

    [Fact]
    public void R3e_DerivesSpecificForce_AtRest()
    {
        var src = new R3eSource();
        var (cap, session) = Start(src);
        using var _ = session;
        var b = new byte[R3eSource.PageSize];
        W(b, 0, 2); W(b, 36, 1);
        src.Translate(b, session, Now());
        AssertEx.ApproxEqual(9.81f, cap.LastMotion().Objects[0].BodyDynamics.SpecificForce.Y, 1e-4f);
    }
}
}
