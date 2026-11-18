#nullable enable
using System;
using System.IO;
using System.Text;
using OgsTsBridge.Sources;
using Xunit;
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>Forged-input translation tests: the Aerofly FS 2 telemetry DLL's text datagram.</summary>
public class AeroflySourceTests
{
    // pitch;roll;yaw;sway;(4);surge;(6);(7);heave;airSpeed;groundSpeed
    const string Line = "200;-100;500;2000;0;3.5;0;0;1000;60;55";

    [Fact]
    public void Aerofly_Maps_The_Documented_Fields()
    {
        var src = new AeroflySource();
        var (cap, session) = Start(src, new() { ["gravIncluded"] = "true" });
        using var _ = session;

        src.Translate(Encoding.ASCII.GetBytes(Line), session, Now());
        var m = cap.LastMotion();
        AssertQuat(OgsMath.EulerToQuaternion(0.5, 0.2, -0.1), m);
        var f = m.Objects[0].BodyDynamics.SpecificForce;
        AssertEx.ApproxEqual(2f, f.X, 1e-5f);     // sway / 1000
        AssertEx.ApproxEqual(-1f, f.Y, 1e-5f);    // heave: positive is down on the wire
        AssertEx.ApproxEqual(3.5f, f.Z, 1e-5f);   // surge as sent
        AssertEx.ApproxEqual(60f, cap.Channel(m, "aerofly.airSpeed"), 1e-4f);
        AssertEx.ApproxEqual(55f, cap.Channel(m, "aerofly.groundSpeed"), 1e-4f);
    }

    [Fact]
    public void Aerofly_Drops_Short_Or_Malformed_Datagrams()
    {
        var src = new AeroflySource();
        var (cap, session) = Start(src);
        using var _ = session;
        int motions = cap.Motions;
        src.Translate(Encoding.ASCII.GetBytes("1;2;3"), session, Now());
        src.Translate(Encoding.ASCII.GetBytes("a;2;3;4;5;6;7;8;9;10;11"), session, Now() + 1);
        Assert.Equal(motions, cap.Motions);
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new AeroflySource(), i => Encoding.ASCII.GetBytes(
            $"{200 + i};-100;500;2000;0;3.5;0;0;1000;60;55"));

    [Fact]
    public void Aerofly_Install_Rejects_A_Wrong_Hash_And_Skips_Without_The_Game_Folder()
    {
        var docs = Path.Combine(Path.GetTempPath(), "bridge-aerofly-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(docs, "Aerofly FS 2"));
        try
        {
            Assert.Throws<InvalidDataException>(() => AeroflySource.InstallDll(docs, () => new byte[] { 1, 2, 3 }));
            Assert.False(File.Exists(Path.Combine(docs, "Aerofly FS 2", "external_dll", AeroflySource.DllName)));

            // game never run: no Documents folder, nothing to do
            Assert.Empty(AeroflySource.InstallDll(Path.Combine(docs, "none"), () => throw new InvalidOperationException()));
        }
        finally { Directory.Delete(docs, true); }
    }
}
}
