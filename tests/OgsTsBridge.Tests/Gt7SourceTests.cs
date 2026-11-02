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
/// <summary>GT7 Simulator Interface: the Salsa20 decryption and the packet mapping.</summary>
public class Gt7SourceTests
{
    [Fact]
    public void Salsa20_Matches_Reference_Vector()
    {
        // eSTREAM Salsa20/20, 256-bit key set 1 vector 0: key 80 00…00, IV 0
        var key = new uint[8];
        key[0] = 0x80;
        var stream = new byte[16];
        Gt7Source.Salsa20Xor(key, 0, 0, stream);
        Assert.Equal("E3BE8FDD8BECA2E3EA8EF9475B29A6E7", Convert.ToHexString(stream));
    }

    /// <summary>Encrypts a type-3 plaintext the way the console does; the
    /// IV seed at 0x40 travels in clear.</summary>
    internal static byte[] Gt7Encrypt(byte[] plain, uint iv1)
    {
        var p = (byte[])plain.Clone();
        W(p, 0x40, iv1);
        var keyWords = Enumerable.Range(0, 8)
            .Select(i => BitConverter.ToUInt32(Encoding.ASCII.GetBytes("Simulator Interface Packet GT7 v"), i * 4)).ToArray();
        Gt7Source.Salsa20Xor(keyWords, iv1 ^ 0x55FABB4F, iv1, p);
        W(p, 0x40, iv1);
        return p;
    }

    [Fact]
    public void Gt7_Decrypts_And_Maps_Type3()
    {
        var src = new Gt7Source();
        var (cap, session) = Start(src, new() { ["console"] = "192.168.1.50", ["gravIncluded"] = "true" });
        using var _ = session;
        Assert.Equal("192.168.1.50:33739", src.HeartbeatTarget!.ToString());
        Assert.Equal((byte)'~', src.HeartbeatPayload[0]);

        var p = new byte[0x158];
        W(p, 0, 0x47375330u);
        W(p, 0x04, 10f); W(p, 0x08, 2f); W(p, 0x0C, 30f);
        W(p, 0x28, 1f);                                     // quaternion w (identity)
        W(p, 0x3C, 7200f); W(p, 0x44, 40f); W(p, 0x48, 80f); W(p, 0x4C, 55f);
        W(p, 0x74, (short)3);
        W(p, 0x8E, (short)(1 | 2 | 2048));                  // on track, paused, TCS
        p[0x90] = 0x34; p[0x91] = 255; p[0x92] = 51;
        W(p, 0xA4, 100f);
        W(p, 0x130, 1.5f); W(p, 0x134, 9.81f); W(p, 0x138, -3f);
        src.Translate(Gt7Encrypt(p, 0x12345678), session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertEx.ApproxEqual(7200f, cap.Channel(m, "engine.rpm"), 1e-2f);
        AssertEx.ApproxEqual(55f, cap.Channel(m, "vehicle.speed"), 1e-4f);
        AssertEx.ApproxEqual(0.5f, cap.Channel(m, "fuel.level"), 1e-6f);
        AssertEx.ApproxEqual(1f, cap.Channel(m, "engine.throttle"), 1e-6f);
        AssertEx.ApproxEqual(0.2f, cap.Channel(m, "brakes.pedal"), 1e-6f);
        AssertEx.ApproxEqual(4f, cap.Channel(m, "gt7.currentGear"), 1e-6f);
        AssertEx.ApproxEqual(3f, cap.Channel(m, "gt7.suggestedGear"), 1e-6f);
        AssertEx.ApproxEqual(3f, cap.Channel(m, "race.lap"), 1e-6f);
        Assert.Equal(1f, cap.Channel(m, "session.paused"));
        Assert.Equal(1f, cap.Channel(m, "aids.tcs"));
        AssertEx.ApproxEqual(9.81f, obj.BodyDynamics.SpecificForce.Y, 1e-5f);
        AssertEx.ApproxEqual(30f, obj.Position.Z, 1e-5f);

        // a packet that does not decrypt to the magic is dropped
        int motions = cap.Motions;
        src.Translate(new byte[0x158], session, Now() + 1);
        Assert.Equal(motions, cap.Motions);
    }

    [Fact]
    public void Translate_Does_Not_Allocate() =>
        AssertNoAllocation(new Gt7Source(), i =>
        {
            var p = new byte[0x158];
            W(p, 0, 0x47375330u); W(p, 0x28, 1f); W(p, 0x3C, 3000f + i); W(p, 0x8E, (short)1);
            return Gt7Encrypt(p, (uint)(0x1000 + i));
        });
}
}
