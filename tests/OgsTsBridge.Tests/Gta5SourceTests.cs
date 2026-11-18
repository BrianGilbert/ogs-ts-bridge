#nullable enable
using System;
using System.IO;
using System.Text;
using OgsTsBridge.Sources;
using Xunit;
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>Forged-input translation tests: the GTA V ScriptHookVDotNet script's text datagram.</summary>
public class Gta5SourceTests
{
    const double Deg = Math.PI / 180;

    // surge:sway:heave:pitch:roll:yaw:rotAccX:rotAccY:tracLoss:rpm:gear:speed:(12):(13):steer:wheelSpeed:maxTrac:thrPower:throttle: ... E
    static byte[] Packet(double pitch = 10, double roll = -5, double yaw = 30) =>
        Encoding.ASCII.GetBytes(
            $"S:0.002:0.001:0.0005:{pitch}:{roll}:{yaw}:0.1:0.2:1.5:0.6:3:25.5:::0.3:20:1.1:0.9:0.8:::::::::::E");

    [Fact]
    public void Gta5_Maps_The_Script_Fields()
    {
        var src = new Gta5Source();
        var (cap, session) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = session;

        src.Translate(Packet(), session, Now());
        var m = cap.LastMotion();
        AssertQuat(OgsMath.EulerToQuaternion(-30 * Deg, 10 * Deg, -5 * Deg), m);   // heading negated
        var f = m.Objects[0].BodyDynamics.SpecificForce;
        AssertEx.ApproxEqual(1f, f.X, 1e-4f);     // sway 0.001 × 1000
        AssertEx.ApproxEqual(0.5f, f.Y, 1e-4f);   // heave
        AssertEx.ApproxEqual(2f, f.Z, 1e-4f);     // surge
        AssertEx.ApproxEqual(25.5f, cap.Channel(m, "vehicle.speed"), 1e-4f);
        AssertEx.ApproxEqual(0.6f, cap.Channel(m, "gta5.rpm"), 1e-5f);
        AssertEx.ApproxEqual(3f, cap.Channel(m, "gta5.gear"), 1e-5f);
    }

    [Fact]
    public void Gta5_Drops_Out_Of_Vehicle_And_Malformed_Datagrams()
    {
        var src = new Gta5Source();
        var (cap, session) = Start(src);
        using var _ = session;
        int motions = cap.Motions;
        src.Translate(Packet(0, 0, 0), session, Now());                                  // on foot: all zeros
        src.Translate(Encoding.ASCII.GetBytes("S:1:2:3:E"), session, Now() + 1);          // short
        src.Translate(Encoding.ASCII.GetBytes("S:a:2:3:4:5:6:7:8:9:10:11:12:13:14:15:16:17:18:19:E"), session, Now() + 2);
        Assert.Equal(motions, cap.Motions);
    }

    [Fact]
    public void Gta5_Install_Needs_ScriptHook_And_A_Matching_Hash()
    {
        var game = Path.Combine(Path.GetTempPath(), "bridge-gta5-" + Path.GetRandomFileName());
        Directory.CreateDirectory(game);
        try
        {
            Assert.Throws<FileNotFoundException>(() => Gta5Source.InstallScript(game, () => throw new InvalidOperationException()));
            File.WriteAllText(Path.Combine(game, "ScriptHookV.dll"), "x");
            File.WriteAllText(Path.Combine(game, "ScriptHookVDotNet3.dll"), "x");
            Assert.Throws<InvalidDataException>(() => Gta5Source.InstallScript(game, () => new byte[] { 1 }));
            Assert.False(Directory.Exists(Path.Combine(game, "scripts")));
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new Gta5Source(), i => Packet(10 + i % 20));
}
}
