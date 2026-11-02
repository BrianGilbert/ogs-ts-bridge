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
/// <summary>Forged-snapshot translation tests: Falcon BMS shared memory.</summary>
public class FalconBmsSourceTests
{
    [Fact]
    public void FalconBms_Maps_Three_Pages()
    {
        var src = new FalconBmsSource();
        var (cap, session) = Start(src);
        using var _ = session;
        var b = new byte[FalconBmsSource.SnapshotSize];
        W(b, 0, 1000f); W(b, 4, 2000f); W(b, 8, -5000f);    // N, E, D (ft)
        W(b, 12, 300f); W(b, 16, 0f); W(b, 20, -10f);       // NED rates
        W(b, 24, 5f);                                      // alpha deg
        W(b, 36, 0.1f); W(b, 40, 0.2f); W(b, 44, 1.5f);     // pitch, roll, yaw rad
        W(b, 52, 300f); W(b, 84, 95f);                      // kias, rpm %
        Text(b, 236, "UHF 305.00  STPT  8");
        W(b, 764, 2); W(b, 768, 7); W(b, 768 + 4, 9); W(b, 1088 + 4, 1u);
        W(b, FalconBmsSource.Fd2 + 1032, 41.5f);
        Text(b, FalconBmsSource.Fd2 + 609, "Viper1");
        b[FalconBmsSource.Iv + 15] = 1;                     // in 3D
        b[FalconBmsSource.Iv + 16] = 1;                     // paused
        b[FalconBmsSource.Present] = 3;
        src.Translate(b, session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertEx.ApproxEqual(2000 * 0.3048f, obj.Position.X, 1e-2f);
        AssertEx.ApproxEqual(5000 * 0.3048f, obj.Position.Y, 1e-2f);
        AssertEx.ApproxEqual(1000 * 0.3048f, obj.Position.Z, 1e-2f);
        AssertEx.ApproxEqual(300 * 0.3048f, obj.Kinematics.Velocity.Z, 1e-3f);
        AssertQuat(OgsMath.EulerToQuaternion(1.5, 0.1, 0.2), m);
        AssertEx.ApproxEqual(5000 * 0.3048f, cap.Channel(m, "position.altMsl"), 1e-2f);
        AssertEx.ApproxEqual(300 * 0.514444f, cap.Channel(m, "air.indicatedAirspeed"), 1e-3f);
        AssertEx.ApproxEqual(0.95f, cap.Channel(m, "falcon.rpm"), 1e-5f);
        AssertEx.ApproxEqual((float)(41.5 * Math.PI / 180), cap.Channel(m, "position.lat"), 1e-5f);
        AssertEx.ApproxEqual(9f, cap.Channel(m, "falcon.rwr2.symbol"), 1e-6f);
        AssertEx.ApproxEqual(1f, cap.Channel(m, "falcon.rwr2.flags"), 1e-6f);
        Assert.Equal(1f, cap.Channel(m, "session.paused"));
        Assert.Equal("UHF 305.00  STPT  8", cap.SessionText("falcon.dedLine1"));
        Assert.Equal("Viper1", cap.SessionText("falcon.pilot1.callsign"));

        int motions = cap.Motions;
        b[FalconBmsSource.Iv + 15] = 0;                     // left 3D
        src.Translate(b, session, Now() + 0.1);
        Assert.Equal(motions, cap.Motions);
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new FalconBmsSource(), i =>
        {
            var b = new byte[FalconBmsSource.SnapshotSize];
            W(b, 52, 300f + i); W(b, 44, 0.01f * i);
            b[FalconBmsSource.Iv + 15] = 1; b[FalconBmsSource.Present] = 3;
            return b;
        });
}
}
