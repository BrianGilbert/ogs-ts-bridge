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
/// <summary>Forged-packet translation tests: The Crew 2 phys_out_sim and its ExtraConfig patch.</summary>
public class TheCrew2SourceTests
{
    [Fact]
    public void TheCrew2_Maps_Packet()
    {
        var src = new TheCrew2Source();
        var (cap, session) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = session;
        var b = new byte[TheCrew2Source.PacketSize];
        W(b, 0, 777u);
        W(b, 4, 0.1f); W(b, 8, 0.2f); W(b, 12, 0.3f);       // pitch, roll, yaw rates
        W(b, 16, 1.0f); W(b, 20, 0.05f); W(b, 24, -0.1f);   // yaw, pitch, roll
        W(b, 28, 2f); W(b, 32, 3f); W(b, 36, 9.8f);          // lateral, longitudinal, vertical
        W(b, 40, 3f); W(b, 44, 0f); W(b, 48, 4f);
        W(b, 52, 10f); W(b, 56, 20f); W(b, 60, 30f); W(b, 64, 5u);
        src.Translate(b, session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertQuat(OgsMath.EulerToQuaternion(1.0, 0.05, -0.1), m);
        AssertEx.ApproxEqual(2f, obj.BodyDynamics.SpecificForce.X, 1e-6f);
        AssertEx.ApproxEqual(9.8f, obj.BodyDynamics.SpecificForce.Y, 1e-6f);
        AssertEx.ApproxEqual(3f, obj.BodyDynamics.SpecificForce.Z, 1e-6f);
        AssertEx.ApproxEqual(0.1f, obj.BodyDynamics.AngularVelocity.X, 1e-6f);
        AssertEx.ApproxEqual(0.3f, obj.BodyDynamics.AngularVelocity.Y, 1e-6f);
        AssertEx.ApproxEqual(0.2f, obj.BodyDynamics.AngularVelocity.Z, 1e-6f);
        AssertEx.ApproxEqual(5f, cap.Channel(m, "vehicle.speed"), 1e-5f);
        AssertEx.ApproxEqual(20f, cap.Channel(m, "thecrew2.position.y"), 1e-5f);
        AssertEx.ApproxEqual(777f, cap.Channel(m, "thecrew2.time"), 1e-3f);
    }

    [Fact]
    public void TheCrew2_Patch_Sets_Output_And_Keeps_Attributes()
    {
        var root = Path.Combine(Path.GetTempPath(), "bridgetc2-" + Path.GetRandomFileName());
        var dir = Path.Combine(root, "The Crew 2");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ExtraConfig.xml");
        File.WriteAllText(path, "<ExtraConfig phys_out_sim_rate=\"3\" other=\"x\" />");
        try
        {
            Assert.Single(TheCrew2Source.PatchDocuments(root, 1337));
            var e = XDocument.Load(path).Root!;
            Assert.Equal("127.0.0.1:1337", (string?)e.Attribute("phys_out_sim"));
            Assert.Equal("3", (string?)e.Attribute("phys_out_sim_rate"));
            Assert.Equal("x", (string?)e.Attribute("other"));
            Assert.Empty(TheCrew2Source.PatchDocuments(root, 1337));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new TheCrew2Source(), i => { var b = new byte[68]; W(b, 16, 0.01f * i); return b; });
}
}
