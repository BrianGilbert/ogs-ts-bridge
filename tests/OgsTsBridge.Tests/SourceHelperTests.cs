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
/// <summary>Source helpers: the JSON channel mapper and axis maps.</summary>
public class SourceHelperTests
{
    sealed class JsonSource : ILegacySource
    {
        public readonly JsonChannels Json = new("game", new[] { new JsonKey("speed", "vehicle.speed", "m/s", "Speed", 1 / 3.6) });
        public string Id => "json-test";
        public string Title => "test";
        public string Description => "test";
        public int DefaultPort => 0;
        public IReadOnlyList<string> ProcessNames => Array.Empty<string>();
        public IReadOnlyList<LegacyGame> Games => Array.Empty<LegacyGame>();
        public IReadOnlyList<(string Key, string Help)> Options => Array.Empty<(string, string)>();
        public void Configure(IReadOnlyDictionary<string, string> options) { }
        public void Declare(OgsSession session)
        {
            session.DeclareObject("x", Ogs.Telemetry.Schema.ObjectType.Other);
            Json.Declare(session);
        }
        public bool AcceptsDatagram(int length) => true;
        public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
        {
            if (Json.Read(d, session))
            {
                session.BeginRedeclare();
                Declare(session);
                session.CommitRedeclare(now);
                Json.Read(d, session);
            }
            session.Tick(now);
        }
    }

    [Fact]
    public void JsonChannels_Flattens_Paths_And_Learns_Keys()
    {
        var src = new JsonSource();
        int watch = src.Json.Watch("Rotation.Yaw");
        var (cap, session) = Start(src);
        using var _ = session;

        var doc = "{\"speed\": 36, \"Rotation\": {\"Yaw\": 1.25}, \"Wheels\": [{\"rpm\": 10}, {\"rpm\": 20}]," +
                  " \"name\": \"Coach\", \"flag\": true, \"n\": \"7\", \"nothing\": null}";
        src.Translate(Encoding.UTF8.GetBytes(doc), session, Now());
        var m = cap.LastMotion();
        AssertEx.ApproxEqual(10f, cap.Channel(m, "vehicle.speed"), 1e-5f);
        AssertEx.ApproxEqual(1.25f, cap.Channel(m, "game.Rotation.Yaw"), 1e-6f);
        AssertEx.ApproxEqual(20f, cap.Channel(m, "game.Wheels.1.rpm"), 1e-6f);
        Assert.Equal(1f, cap.Channel(m, "game.flag"));
        AssertEx.ApproxEqual(7f, cap.Channel(m, "game.n"), 1e-6f);           // numeric string
        Assert.Equal("Coach", cap.SessionText("game.name"));
        Assert.Equal(1.25, src.Json[watch]);
        Assert.Equal(7, src.Json.Count);

        // a document with only known keys learns nothing new
        Assert.False(src.Json.Read(Encoding.UTF8.GetBytes(doc), session));
        Assert.Equal("TAS_km_h", JsonChannels.Sanitize("TAS, km/h"));
    }

    [Theory]
    [InlineData("x,y,z")]
    [InlineData("z,y,-x")]
    [InlineData("-y,z,x")]
    [InlineData("-x,y,z")]   // a handedness change
    public void AxisMap_Keeps_Rotations_Consistent(string spec)
    {
        var map = AxisMap.Parse(spec);
        var q = Quaternion.Normalize(new Quaternion(0.2f, -0.4f, 0.1f, 0.9f));
        var v = new Vector3(1.5f, -0.5f, 2f);
        // rotating then mapping equals mapping then rotating by the mapped quaternion
        var a = map.Vector(Vector3.Transform(v, q));
        var b = Vector3.Transform(map.Vector(v), map.Rotation(q));
        AssertEx.ApproxEqual(a.X, b.X, 1e-5f);
        AssertEx.ApproxEqual(a.Y, b.Y, 1e-5f);
        AssertEx.ApproxEqual(a.Z, b.Z, 1e-5f);
    }

    [Fact]
    public void AxisMap_Rejects_Bad_Specs()
    {
        Assert.Throws<ArgumentException>(() => AxisMap.Parse("x,x,z"));
        Assert.Throws<ArgumentException>(() => AxisMap.Parse("x,y"));
        Assert.Throws<ArgumentException>(() => AxisMap.Parse("x,y,w"));
    }
}
}
