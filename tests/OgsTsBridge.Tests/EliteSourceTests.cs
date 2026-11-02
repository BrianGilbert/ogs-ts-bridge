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
/// <summary>Elite Dangerous: the offsets file and the published entries.</summary>
public class EliteSourceTests
{
    const string OffsetsXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <pointers>
          <Horizons><Speed Offsets="2" Offset_0="10" Offset_1="4" /></Horizons>
          <Odyssey>
            <Speed Offsets="5" Offset_0="05FADAB0" Offset_1="8" Offset_2="2A0" Offset_3="420" Offset_4="650" />
            <Yaw Offsets="3" Offset_0="05FADAB0" Offset_1="8" Offset_2="90C" />
            <Pitch Offsets="3" Offset_0="05FADAB0" Offset_1="8" Offset_2="908" />
            <Roll Offsets="3" Offset_0="05FADAB0" Offset_1="8" Offset_2="910" />
            <Sway Offsets="3" Offset_0="05FADAB0" Offset_1="8" Offset_2="8E8" />
            <Heave Offsets="3" Offset_0="05FADAB0" Offset_1="8" Offset_2="8EC" />
            <Surge Offsets="3" Offset_0="05FADAB0" Offset_1="8" Offset_2="8F0" />
            <Broken Offsets="2" Offset_0="zz" Offset_1="1" />
          </Odyssey>
        </pointers>
        """;

    [Fact]
    public void Elite_Publishes_Offsets_File_Entries()
    {
        var path = Path.Combine(Path.GetTempPath(), "bridge-elite-" + Path.GetRandomFileName() + ".xml");
        File.WriteAllText(path, OffsetsXml);
        try
        {
            var src = new EliteSource();
            var (cap, session) = Start(src, new() { ["offsets"] = path, ["motion"] = "true" });
            using var _ = session;
            var names = SentFrames.Channels(cap.Sent).Select(c => c.Name).ToList();
            Assert.Equal(new[] { "elite.Speed", "elite.Yaw", "elite.Pitch", "elite.Roll", "elite.Sway", "elite.Heave", "elite.Surge" },
                names);   // the malformed entry is skipped
            Assert.Equal(7 * 4, src.PollBufferSize);

            var b = new byte[7 * 4];
            float[] v = { 120f, 0.5f, 0.1f, -0.2f, 1f, 9.8f, -3f };
            for (int i = 0; i < v.Length; i++) W(b, i * 4, v[i]);
            src.Translate(b, session, Now());
            var m = cap.LastMotion();
            AssertEx.ApproxEqual(120f, cap.Channel(m, "elite.Speed"), 1e-4f);
            AssertQuat(OgsMath.EulerToQuaternion(0.5, 0.1, -0.2), m);
            AssertEx.ApproxEqual(9.8f, m.Objects[0].BodyDynamics.SpecificForce.Y, 1e-5f);

            var horizons = new EliteSource();
            horizons.Configure(new Dictionary<string, string> { ["offsets"] = path, ["mode"] = "horizons" });
            Assert.Equal(4, horizons.PollBufferSize);   // the Horizons section's one entry
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Elite_Without_Offsets_Waits_And_Says_Why()
    {
        var src = new EliteSource();
        src.Configure(new Dictionary<string, string> { ["offsets"] = Path.Combine(Path.GetTempPath(), "missing.xml") });
        Assert.False(src.Connect());
        Assert.Contains("EliteDangerous64_Offsets.xml", src.PollTarget);
    }
}
}
