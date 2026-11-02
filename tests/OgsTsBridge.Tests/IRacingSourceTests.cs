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
/// <summary>iRacing irsdk: the self-describing page read through a real named map.</summary>
public class IRacingSourceTests
{
    static void VarHeader(MemoryMappedViewAccessor v, int at, int type, int offset, int count, string name, string unit)
    {
        var h = new byte[144];
        BitConverter.GetBytes(type).CopyTo(h, 0);
        BitConverter.GetBytes(offset).CopyTo(h, 4);
        BitConverter.GetBytes(count).CopyTo(h, 8);
        Encoding.ASCII.GetBytes(name).CopyTo(h, 16);
        Encoding.ASCII.GetBytes(name + " desc").CopyTo(h, 48);
        Encoding.ASCII.GetBytes(unit).CopyTo(h, 112);
        v.WriteArray(at, h, 0, h.Length);
    }

    [Fact]
    public void IRacing_Reads_Self_Describing_Page()
    {
        var src = new IRacingSource();
        var (cap, session) = Start(src);
        using var _ = session;

        const int varHeaders = 112, yamlAt = 4096, bufAt = 8192, bufLen = 128;
        using var map = MemoryMappedFile.CreateNew(null, 16384);
        using var v = map.CreateViewAccessor();
        var vars = new (int Type, int Offset, int Count, string Name, string Unit)[]
        {
            (4, 0, 1, "RPM", "revs/min"), (4, 4, 1, "Speed", "m/s"), (2, 8, 1, "Gear", ""),
            (4, 12, 1, "Yaw", "rad"), (4, 16, 1, "Pitch", "rad"), (4, 20, 1, "Roll", "rad"),
            (4, 24, 1, "LatAccel", "m/s^2"), (4, 28, 1, "LongAccel", "m/s^2"), (4, 32, 1, "VertAccel", "m/s^2"),
            (4, 36, 1, "YawRate", "rad/s"), (4, 40, 1, "PitchRate", "rad/s"), (4, 44, 1, "RollRate", "rad/s"),
            (1, 48, 1, "IsOnTrack", ""), (4, 52, 1, "OilPress", "kPa"), (4, 56, 4, "TireTemps", "C"),
            (2, 72, 4, "CarIdxLap", ""), (4, 88, 1, "Mystery", "furlongs"), (4, 92, 1, "LFshockDefl", "m"),
        };
        for (int i = 0; i < vars.Length; i++)
            VarHeader(v, varHeaders + i * 144, vars[i].Type, vars[i].Offset, vars[i].Count, vars[i].Name, vars[i].Unit);
        var yaml = Encoding.ASCII.GetBytes("WeekendInfo:\n TrackDisplayName: Okayama\nDriverInfo:\n DriverCarIdx: 1\n Drivers:\n - CarIdx: 0\n   CarScreenName: Other\n - CarIdx: 1\n   CarScreenName: Mazda MX-5\n");
        v.WriteArray(yamlAt, yaml, 0, yaml.Length);
        v.Write(0, 2); v.Write(4, 1); v.Write(8, 60); v.Write(12, 1);
        v.Write(16, yaml.Length); v.Write(20, yamlAt); v.Write(24, vars.Length); v.Write(28, varHeaders);
        v.Write(32, 1); v.Write(36, bufLen);
        v.Write(48, 100); v.Write(52, bufAt);
        var data = new byte[bufLen];
        W(data, 0, 7300f); W(data, 4, 41f); W(data, 8, 4); W(data, 12, 0.5f);
        W(data, 24, 2f); W(data, 28, 3f); W(data, 32, 9.8f); W(data, 36, 0.3f);
        data[48] = 1; W(data, 52, 400f); W(data, 60, 85f); W(data, 88, 12f); W(data, 92, 0.04f);
        v.WriteArray(bufAt, data, 0, bufLen);

        var buffer = new byte[IRacingSource.MaxBuffer];
        int n = src.Gather(v, Array.Empty<MemoryMappedViewAccessor?>(), buffer);
        Assert.Equal(bufLen, n);
        src.Translate(buffer.AsSpan(0, n), session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertEx.ApproxEqual(7300f, cap.Channel(m, "engine.rpm"), 1e-2f);
        AssertEx.ApproxEqual(7300f, cap.Channel(m, "iracing.RPM"), 1e-2f);
        AssertEx.ApproxEqual(41f, cap.Channel(m, "vehicle.speed"), 1e-5f);
        AssertEx.ApproxEqual(4f, cap.Channel(m, "transmission.gear"), 1e-6f);
        AssertEx.ApproxEqual(400000f, cap.Channel(m, "iracing.OilPress"), 1e-1f);
        AssertEx.ApproxEqual(85f, cap.Channel(m, "iracing.TireTemps.1"), 1e-5f);
        AssertEx.ApproxEqual(12f, cap.Channel(m, "iracing.Mystery"), 1e-5f);  // unknown unit: raw
        Assert.DoesNotContain(SentFrames.Channels(cap.Sent), c => c.Name.StartsWith("iracing.CarIdx"));
        AssertQuat(OgsMath.EulerToQuaternion(-0.5, 0, 0), m);
        AssertEx.ApproxEqual(-2f, obj.BodyDynamics.SpecificForce.X, 1e-6f);
        AssertEx.ApproxEqual(9.8f, obj.BodyDynamics.SpecificForce.Y, 1e-6f);
        AssertEx.ApproxEqual(3f, obj.BodyDynamics.SpecificForce.Z, 1e-6f);
        AssertEx.ApproxEqual(-0.3f, obj.BodyDynamics.AngularVelocity.Y, 1e-6f);
        Assert.Equal(0f, cap.Channel(m, "session.paused"));
        Assert.Equal("Okayama", cap.Discovery().Objects[0].Location);
        Assert.Equal("Mazda MX-5", cap.Discovery().Objects[0].TypeLabel);
        Assert.False(src.Truncated);

        Assert.Equal(0, src.Gather(v, Array.Empty<MemoryMappedViewAccessor?>(), buffer)); // same tick
    }
}
}
