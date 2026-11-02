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
/// <summary>Forged-packet translation tests: Project Motor Racing UDP.</summary>
public class PmrSourceTests
{
    static void Str(List<byte> b, string s) { var u = Encoding.UTF8.GetBytes(s); b.Add((byte)u.Length); b.AddRange(u); }

    static void F(List<byte> b, params float[] v) { foreach (var f in v) b.AddRange(BitConverter.GetBytes(f)); }

    internal static byte[] PmrTelemetry(int vehicleId, int wheels, int gears, int loads, float rpm)
    {
        var b = new List<byte> { 2 };
        b.AddRange(BitConverter.GetBytes((ushort)2));
        b.AddRange(BitConverter.GetBytes(vehicleId));
        b.Add((byte)wheels);
        for (int w = 0; w < wheels; w++)
        {
            b.AddRange(BitConverter.GetBytes(1000 + w));            // contact material
            var f = new float[30];
            f[0] = 50f + w;                                       // angVel rad/s
            f[14] = 0.05f;                                        // slip ratio
            f[24] = 0.01f * (w + 1);                              // spring strain
            F(b, f);
        }
        F(b, 1, 2, 3);                    // pos
        F(b, 0, 0, 0, 1);                 // quat (identity)
        F(b, 0, 0, 0);                    // ang vel world
        F(b, 0.1f, 0.2f, 0.3f);           // ang vel local
        F(b, 0, 0, 0, 0, 0, 0, 0, 0, 0);  // vel world, vel local, acc world
        F(b, 1f, 9.8f, -2f);              // acc local
        F(b, 33f, 32f, 0.01f);            // overall, forward, sideslip
        var drive = new float[28];
        drive[0] = rpm; drive[3] = 150f;  // rpm, power kW
        F(b, drive);
        b.AddRange(new byte[] { 0, 1, 0, 1, 1, 0, 0 });            // flags
        b.Add((byte)gears);
        for (int g = 0; g < gears; g++) F(b, 7000f + g, 4000f);
        b.Add((byte)loads);
        for (int l = 0; l < loads; l++) F(b, 3000f + l);
        F(b, 0.5f);                                               // load bias
        F(b, -0.2f, 0.8f, 0.1f, 0f, 0f);                          // inputs
        b.AddRange(BitConverter.GetBytes(3));                     // gear
        F(b, 0.56f, 1, 2, 0, 0); b.Add(2); b.Add(3);              // setup
        F(b, 0, 0.3f, 0, 0.4f, 1200f, 0, 0, 0, 0, 0); b.Add(1);   // general
        F(b, new float[18]); b.Add((byte)wheels); b.Add(6); b.Add(1); b.Add(0);
        return b.ToArray();
    }

    [Fact]
    public void Pmr_Maps_Race_Participant_And_Telemetry()
    {
        var src = new PmrSource();
        var (cap, session) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = session;
        Assert.Equal("224.0.0.150", src.MulticastGroup);

        var info = new List<byte> { 0 };
        info.AddRange(BitConverter.GetBytes((ushort)1));
        foreach (var s in new[] { "Brands Hatch", "Indy", "Summer", "Clear", "Race", "Career" }) Str(info, s);
        F(info, 1929f, 1200f, 0f, 28f);
        info.Add(1); info.Add(1); info.Add(20);
        src.Translate(info.ToArray(), session, Now());

        var part = new List<byte> { 1 };
        part.AddRange(BitConverter.GetBytes((ushort)1));
        part.AddRange(BitConverter.GetBytes(7));
        part.Add(1);
        Str(part, "Ginetta G55");
        Str(part, "Driver One");
        src.Translate(part.ToArray(), session, Now());

        src.Translate(PmrTelemetry(9, 4, 6, 2, 5000f), session, Now()); // another car: ignored
        Assert.Equal(0, cap.Motions);
        src.Translate(PmrTelemetry(7, 4, 6, 2, 6500f), session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertEx.ApproxEqual(6500f, cap.Channel(m, "engine.rpm"), 1e-3f);
        AssertEx.ApproxEqual(150000f, cap.Channel(m, "engine.power"), 1e-1f);
        AssertEx.ApproxEqual(33f, cap.Channel(m, "vehicle.speed"), 1e-5f);
        AssertEx.ApproxEqual(3f, cap.Channel(m, "transmission.gear"), 1e-6f);
        AssertEx.ApproxEqual(7005f, cap.Channel(m, "pmr.gear6.upshiftRpm"), 1e-2f);
        AssertEx.ApproxEqual(3001f, cap.Channel(m, "pmr.suspensionLoad1"), 1e-2f);
        AssertEx.ApproxEqual(28f, cap.Channel(m, "pmr.trackTemperature"), 1e-5f);
        // axes z,y,-x: OGS (game z, game y, −game x)
        AssertEx.ApproxEqual(-2f, obj.BodyDynamics.SpecificForce.X, 1e-5f);
        AssertEx.ApproxEqual(9.8f, obj.BodyDynamics.SpecificForce.Y, 1e-5f);
        AssertEx.ApproxEqual(-1f, obj.BodyDynamics.SpecificForce.Z, 1e-5f);
        AssertEx.ApproxEqual(3f, obj.Position.X, 1e-5f);
        AssertEx.ApproxEqual(-1f, obj.Position.Z, 1e-5f);
        Assert.Equal(4, obj.DrivePoints.Count);
        Assert.Equal("Ginetta G55", cap.Discovery().Objects[0].TypeLabel);
        Assert.Equal("Brands Hatch", cap.Discovery().Objects[0].Location);
        Assert.Equal("Driver One", cap.SessionText("pmr.driver"));
        Assert.True(cap.LargestDatagram() <= 1400);
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new PmrSource(), i => PmrTelemetry(7, 4, 6, 2, 5000f + i));
}
}
