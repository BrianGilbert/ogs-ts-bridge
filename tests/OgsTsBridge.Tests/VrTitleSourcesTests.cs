#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using OgsTsBridge.Sources;
using Ogs.Telemetry;
using Xunit;

namespace OgsTsBridge.Tests
{
/// <summary>Forged-packet tests for the SRS motion family, Distance,
/// New Star GP, Iron Rebellion, X-Wing Alliance and Fernbus.</summary>
public class VrTitleSourcesTests
{
    const float Deg = MathF.PI / 180;

    sealed class Cap
    {
        public readonly List<(FrameType type, byte[] datagram)> Sent = new();
        public OgsSession Session(string game) => new((t, d) => Sent.Add((t, d)), game);
        public int Ticks => Sent.Count(x => x.type == FrameType.Motion);
        public CapnpGen.MotionFrame.READER Part0() => SentFrames.Part0(Sent);

        public float Channel(string name)
        {
            foreach (var ch in SentFrames.Channels(Sent))
                if (ch.Name == name)
                    foreach (var cv in SentFrames.TickValues(SentFrames.Tick(Sent)))
                        if (cv.Id == ch.Id) return cv.Number;
            throw new ArgumentException($"channel {name} missing");
        }
    }

    static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    static (Cap cap, OgsSession s) Start(ILegacySource src, Dictionary<string, string>? opts = null)
    {
        var cap = new Cap();
        var s = cap.Session(src.Title);
        src.Configure(opts ?? new());
        src.Declare(s);
        s.Start(Now());
        return (cap, s);
    }

    static void Put(byte[] b, int off, float v) => MemoryMarshal.Write(b.AsSpan(off, 4), ref v);
    static void Put(byte[] b, int off, int v) => MemoryMarshal.Write(b.AsSpan(off, 4), ref v);

    // ---------- SRS ----------

    static byte[] ForgeApi(float pitch, float roll, float yaw)
    {
        var b = new byte[0xEC];
        Encoding.ASCII.GetBytes("api").CopyTo(b, 0);
        Put(b, 4, 102);
        Encoding.ASCII.GetBytes("Whitewater").CopyTo(b, 0x3A);
        Encoding.ASCII.GetBytes("Raft").CopyTo(b, 0x3A);
        Encoding.ASCII.GetBytes("Rapids").CopyTo(b, 0x6C);
        Put(b, 0xA0, 8f); Put(b, 0xA4, 3000f); Put(b, 0xA8, 7000f); Put(b, 0xAC, 2);
        Put(b, 0xB0, pitch); Put(b, 0xB4, roll); Put(b, 0xB8, yaw);
        Put(b, 0xC0, 1f); Put(b, 0xC4, 2f); Put(b, 0xC8, 3f);           // sway, heave, surge
        Put(b, 0xDC, 5); Put(b, 0xE8, 6);
        return b;
    }

    [Fact]
    public void Srs_Api_Packet_Translates_Pose_Force_And_Derived_Rates()
    {
        var src = new SrsSource();
        var (cap, s) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = s;
        double t = Now();
        src.Translate(ForgeApi(0, 0, 0), s, t);
        src.Translate(ForgeApi(10, 20, 30), s, t + 0.1);
        var obj = cap.Part0().Objects[0];

        var q = OgsMath.EulerToQuaternion(30 * Deg, 10 * Deg, 20 * Deg);
        AssertEx.ApproxEqual(q.X, obj.Orientation.X, 1e-4f);
        AssertEx.ApproxEqual(q.W, obj.Orientation.W, 1e-4f);
        // sway, heave, surge -> OGS (right, up, forward)
        AssertEx.ApproxEqual(1f, obj.BodyDynamics.SpecificForce.X, 1e-4f);
        AssertEx.ApproxEqual(2f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual(3f, obj.BodyDynamics.SpecificForce.Z, 1e-4f);
        // 10 / 30 / 20 degrees over 0.1 s -> (pitch, yaw, roll) rates
        AssertEx.ApproxEqual(100f * Deg, obj.BodyDynamics.AngularVelocity.X, 1e-3f);
        AssertEx.ApproxEqual(300f * Deg, obj.BodyDynamics.AngularVelocity.Y, 1e-3f);
        AssertEx.ApproxEqual(200f * Deg, obj.BodyDynamics.AngularVelocity.Z, 1e-3f);

        AssertEx.ApproxEqual(3000f, cap.Channel("engine.rpm"), 0.1f);
        AssertEx.ApproxEqual(2f, cap.Channel("transmission.gear"), 1e-4f);
        AssertEx.ApproxEqual(5f, cap.Channel("srs.terrain.fl"), 1e-4f);
        AssertEx.ApproxEqual(6f, cap.Channel("srs.terrain.rr"), 1e-4f);
    }

    [Fact]
    public void Srs_Motion_Block_Reads_The_Last_32_Bytes()
    {
        var src = new SrsSource();
        var (cap, s) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = s;
        var b = new byte[40 + 32];                                // 40-byte header, then the block
        int at = 40;
        Put(b, at, 2f); Put(b, at + 4, 1f); Put(b, at + 8, 3f);   // heave, sway, surge
        Put(b, at + 12, 30f); Put(b, at + 16, 10f); Put(b, at + 20, 20f);   // yaw, pitch, roll
        Put(b, at + 24, 0.5f);
        src.Translate(b, s, Now());
        var obj = cap.Part0().Objects[0];

        var q = OgsMath.EulerToQuaternion(30 * Deg, 10 * Deg, 20 * Deg);
        AssertEx.ApproxEqual(q.Y, obj.Orientation.Y, 1e-4f);
        AssertEx.ApproxEqual(1f, obj.BodyDynamics.SpecificForce.X, 1e-4f);
        AssertEx.ApproxEqual(2f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual(0.5f, cap.Channel("srs.extra1"), 1e-4f);
    }

    [Fact]
    public void Srs_Ignores_Non_Finite_Angles()
    {
        var src = new SrsSource();
        var (cap, s) = Start(src);
        using var _ = s;
        int before = cap.Ticks;
        src.Translate(ForgeApi(float.NaN, 0, 0), s, Now());
        Assert.Equal(before, cap.Ticks);
    }

    // ---------- Distance ----------

    [Fact]
    public void Distance_Translates_Pose_Rates_And_Gates_On_Active_Car()
    {
        var src = new DistanceSource();
        var (cap, s) = Start(src);
        using var _ = s;
        var b = new byte[DistanceSource.PacketSize];
        Put(b, 8, 72f);                                           // km/h
        Put(b, 12, 10f); Put(b, 16, 30f); Put(b, 20, 20f);        // pitch, yaw, roll
        Put(b, 24, 0.1f); Put(b, 28, 0.2f); Put(b, 32, 0.3f);
        Put(b, 64, 1);                                            // boost
        Put(b, 96, 0.5f);

        int before = cap.Ticks;
        src.Translate(b, s, Now());                               // car not active
        Assert.Equal(before, cap.Ticks);

        Put(b, 80, 1);
        src.Translate(b, s, Now());
        var obj = cap.Part0().Objects[0];
        var q = OgsMath.EulerToQuaternion(30 * Deg, 10 * Deg, 20 * Deg);
        AssertEx.ApproxEqual(q.X, obj.Orientation.X, 1e-4f);
        AssertEx.ApproxEqual(0.1f, obj.BodyDynamics.AngularVelocity.X, 1e-5f);
        AssertEx.ApproxEqual(0.2f, obj.BodyDynamics.AngularVelocity.Y, 1e-5f);
        AssertEx.ApproxEqual(20f, cap.Channel("vehicle.speed"), 1e-3f);
        AssertEx.ApproxEqual(0.5f, cap.Channel("distance.tyre.fl"), 1e-5f);
    }

    // ---------- New Star GP ----------

    [Fact]
    public void NewStarGp_Translates_And_Gates_On_Driving()
    {
        var src = new NewStarGpSource();
        var (cap, s) = Start(src);
        using var _ = s;
        var b = new byte[NewStarGpSource.PacketSize];
        Put(b, 0, 10f); Put(b, 4, 30f); Put(b, 8, 20f);
        Put(b, 16, 0.4f); Put(b, 52, 33f); Put(b, 56, 8000f); Put(b, 60, 4);

        int before = cap.Ticks;
        src.Translate(b, s, Now());                               // allowDriving = 0
        Assert.Equal(before, cap.Ticks);

        Put(b, 88, 1);
        src.Translate(b, s, Now());
        var obj = cap.Part0().Objects[0];
        var q = OgsMath.EulerToQuaternion(30 * Deg, 10 * Deg, 20 * Deg);
        AssertEx.ApproxEqual(q.Z, obj.Orientation.Z, 1e-4f);
        AssertEx.ApproxEqual(0.4f, obj.BodyDynamics.AngularVelocity.Y, 1e-5f);
        AssertEx.ApproxEqual(8000f, cap.Channel("engine.rpm"), 0.1f);
        AssertEx.ApproxEqual(4f, cap.Channel("transmission.gear"), 1e-4f);

        Put(b, 96, 1);                                            // event over
        before = cap.Ticks;
        src.Translate(b, s, Now());
        Assert.Equal(before, cap.Ticks);
    }

    // ---------- Iron Rebellion ----------

    [Fact]
    public void IronRebellion_Translates_Pose_Events_And_Speed()
    {
        var src = new IronRebellionSource();
        var (cap, s) = Start(src);
        using var _ = s;
        var b = new byte[IronRebellionSource.PacketSize];
        Put(b, 12, 0.1f); Put(b, 16, 0.2f); Put(b, 20, 0.3f);
        Put(b, 24, 10f); Put(b, 28, 30f); Put(b, 32, 20f);        // x pitch, y yaw, z roll
        Put(b, 60, 1);                                            // stomped
        Put(b, 72, -1f); Put(b, 76, 4.5f);

        src.Translate(b, s, Now());
        var obj = cap.Part0().Objects[0];
        var q = OgsMath.EulerToQuaternion(30 * Deg, 10 * Deg, 20 * Deg);
        AssertEx.ApproxEqual(q.X, obj.Orientation.X, 1e-4f);
        AssertEx.ApproxEqual(0.3f, obj.BodyDynamics.AngularVelocity.Z, 1e-5f);
        AssertEx.ApproxEqual(4.5f, cap.Channel("vehicle.speed"), 1e-4f);
        AssertEx.ApproxEqual(-1f, cap.Channel("ironrebellion.stompedFoot"), 1e-4f);
    }

    // ---------- X-Wing Alliance ----------

    [Fact]
    public void Xwa_Maps_Json_To_Pose_And_Prefixed_Channels()
    {
        var src = new XwaSource();
        var (cap, s) = Start(src);
        using var _ = s;
        var json = Encoding.UTF8.GetBytes(
            "{\"XWA.player.abs_yaw\":30,\"XWA.player.abs_pitch\":10,\"XWA.player.abs_roll\":20," +
            "\"XWA.player.speed\":120,\"XWA.player.laserfired\":1,\"XWA.status.location\":\"space\"}");
        src.Translate(json, s, Now());
        var obj = cap.Part0().Objects[0];

        var q = OgsMath.EulerToQuaternion(30 * Deg, 10 * Deg, 20 * Deg);
        AssertEx.ApproxEqual(q.X, obj.Orientation.X, 1e-4f);
        AssertEx.ApproxEqual(q.W, obj.Orientation.W, 1e-4f);
        AssertEx.ApproxEqual(120f, cap.Channel("xwa.speed"), 1e-3f);
        Assert.Contains(SentFrames.Channels(cap.Sent), c => c.Name == "xwa.status.location" || c.Name == "xwa.laserfired");
    }

    [Fact]
    public void Xwa_Without_Attitude_Does_Not_Tick()
    {
        var src = new XwaSource();
        var (cap, s) = Start(src);
        using var _ = s;
        int before = cap.Ticks;
        src.Translate(Encoding.UTF8.GetBytes("{\"XWA.player.speed\":5}"), s, Now());
        Assert.Equal(before, cap.Ticks);
    }

    // ---------- Fernbus ----------

    [Fact]
    public void Fernbus_Maps_Json_Document()
    {
        var src = new FernbusSource();
        var (cap, s) = Start(src);
        using var _ = s;
        var json = Encoding.UTF8.GetBytes(
            "{\"Speed\":55.5,\"Rotation\":{\"Yaw\":30,\"Pitch\":10,\"Roll\":20},\"RPM\":1200,\"MaxRPM\":2400," +
            "\"Steering\":0.25,\"Throttle\":0.5,\"Brake\":0.1,\"EngineStarted\":true,\"IsOffroad\":false}");
        src.Translate(json, s, Now());
        var obj = cap.Part0().Objects[0];

        var q = OgsMath.EulerToQuaternion(30 * Deg, 10 * Deg, 20 * Deg);
        AssertEx.ApproxEqual(q.X, obj.Orientation.X, 1e-4f);
        AssertEx.ApproxEqual(1200f, cap.Channel("engine.rpm"), 0.1f);
        AssertEx.ApproxEqual(0.5f, cap.Channel("engine.throttle"), 1e-4f);
        AssertEx.ApproxEqual(55.5f, cap.Channel("fernbus.Speed"), 1e-3f);
    }

    // ---------- JsonChannels.KnownOnly ----------

    [Fact]
    public void KnownOnly_Skips_Unlisted_Keys()
    {
        var json = new JsonChannels("t", new[] { new JsonKey("a", "t.a", "", "A") }) { KnownOnly = true };
        var cap = new Cap();
        using var s = cap.Session("t");
        s.DeclareObject("o", Ogs.Telemetry.Schema.ObjectType.Vehicle, "o");
        var doc = Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2,\"c\":{\"d\":3}}");
        json.Read(doc, s);                                        // learns keys
        Assert.Equal(1, json.Count);
    }
}
}
