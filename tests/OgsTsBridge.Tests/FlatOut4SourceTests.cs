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
/// <summary>Forged-input translation tests: FlatOut 4 VR JSON telemetry and its config patch.</summary>
public class FlatOut4SourceTests
{
    const double Deg = Math.PI / 180;

    [Fact]
    public void FlatOut4_Learns_Keys_And_Gates_On_Validity()
    {
        var src = new FlatOut4Source();
        var (cap, session) = Start(src);
        using var _ = session;

        var json = "{\"OutputActive\":1,\"Paused\":0,\"FrameValid\":1,\"SpeedMps\":22.5,\"RPM\":6100," +
                   "\"PitchDegrees\":2,\"RollDegrees\":-3,\"YawDegrees\":90,\"ImpactForce\":0.25}";
        src.Translate(Encoding.UTF8.GetBytes(json), session, Now());
        var m = cap.LastMotion();
        AssertQuat(OgsMath.EulerToQuaternion(90 * Deg, 2 * Deg, -3 * Deg), m);
        AssertEx.ApproxEqual(22.5f, cap.Channel(m, "vehicle.speed"), 1e-5f);
        AssertEx.ApproxEqual(6100f, cap.Channel(m, "engine.rpm"), 1e-3f);
        AssertEx.ApproxEqual(0.25f, cap.Channel(m, "flatout.ImpactForce"), 1e-6f);   // learned, raw

        int motions = cap.Motions;
        src.Translate(Encoding.UTF8.GetBytes(json.Replace("\"FrameValid\":1", "\"FrameValid\":0")), session, Now() + 1);
        Assert.Equal(motions, cap.Motions);
    }

    [Fact]
    public void FlatOut4_Patch_Writes_Every_Group()
    {
        var root = Path.Combine(Path.GetTempPath(), "bridgefo4-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        var cfg = Path.Combine(root, "Data", "flatout_telemetry.cfg");
        File.WriteAllText(cfg, "enabled = false\n");
        try
        {
            Assert.Single(FlatOut4Source.PatchInstall(root, 20777));
            var text = File.ReadAllText(cfg);
            Assert.Contains("enabled = true", text);
            Assert.Contains("send_acceleration = true", text);
            Assert.True(File.Exists(cfg + ".bak-ogs-ts-bridge"));
            Assert.Empty(FlatOut4Source.PatchInstall(root, 20777)); // already set
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new FlatOut4Source(), i => Encoding.UTF8.GetBytes(
            $"{{\"OutputActive\":1,\"FrameValid\":1,\"Paused\":0,\"SpeedMps\":{i % 50},\"YawDegrees\":{i % 360},\"PitchDegrees\":1,\"RollDegrees\":2}}"));
}
}
