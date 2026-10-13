#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using OgsaTsBridge.Sources;
using Ogsa.Telemetry;
using Xunit;

namespace OgsaTsBridge.Tests
{
/// <summary>Forged-packet translation tests for the generic format
/// sources (EGO, SMS, F1, WRC, LFS, acpmf). Each forges one packet with
/// known field values and asserts the decoded OGSA-TS output.</summary>
public class SourcesBatchTests
{
    sealed class Capture : IDisposable
    {
        public readonly List<(FrameType type, byte[] datagram)> Sent = new();
        public OgsaSession MakeSession(string game) =>
            new((t, d) => Sent.Add((t, d)), game);

        public CapnpGen.MotionFrame.READER LastMotion()
        {
            var d = Sent.Last(x => x.type == FrameType.Motion).datagram;
            Envelope.TryUnwrap(d, out var type, out var payload);
            return CapnpGen.MotionFrame.READER.create(
                Capnp.DeserializerState.CreateRoot(Envelope.DecodeMessage(payload.Span)));
        }

        public CapnpGen.DiscoveryFrame.READER Discovery()
        {
            var d = Sent.First(x => x.type == FrameType.Discovery).datagram;
            Envelope.TryUnwrap(d, out _, out var payload);
            return CapnpGen.DiscoveryFrame.READER.create(
                Capnp.DeserializerState.CreateRoot(Envelope.DecodeMessage(payload.Span)));
        }

        public float Channel(CapnpGen.DiscoveryFrame.READER disc, CapnpGen.MotionFrame.READER m, string name)
        {
            foreach (var ch in disc.Objects[0].Channels)
                if (ch.Name == name)
                    foreach (var cv in m.Objects[0].Channels)
                        if (cv.Id == ch.Id) return cv.Number;
            throw new ArgumentException($"channel {name} missing");
        }

        public void Dispose() { }
    }

    static double Monotonic() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    static Dictionary<string, string> O() => new();

    // ---------- EGO (Dirt Rally 2 LE / DiRT 4 BE) ----------

    static byte[] ForgeEgo(bool bigEndian)
    {
        var b = new byte[232];
        void F(int off, float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            if (bigEndian) System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(off, 4), bits);
            else MemoryMarshal.Write(b.AsSpan(off, 4), ref bits);
        }
        F(28, 30f);                                    // speed m/s
        F(32, 1f); F(36, 0f); F(40, 0f);               // velocity dir (+X east)
        F(44, 0f); F(48, 0f); F(52, 1f);               // right vec
        F(56, 1f); F(60, 0f); F(64, 0f);               // forward vec (+X)
        F(76, 0.05f); F(80, 0.04f); F(68, 0.03f); F(72, 0.02f); // susp fl,fr,bl,br
        F(136, 0.5f); F(140, -1.5f);                   // g lat / long
        F(148, 6000f);                                 // rpm (raw)
        return b;
    }

    [Theory]
    [InlineData(false, "ref")]      // default formulas
    [InlineData(false, "fwdRight")] // basis-vector map
    [InlineData(true, "ref")]       // DiRT 4, big-endian (auto-detected)
    public void Ego_Translates_Pose_Kinematics_Channels(bool bigEndian, string euler)
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("EGO");
        var src = new EgoSource();
        src.Configure(new Dictionary<string, string> { ["euler"] = euler });
        src.Declare(session);
        session.Start(Monotonic());

        src.Translate(ForgeEgo(bigEndian), session, Monotonic());
        var m = cap.LastMotion();
        var obj = m.Objects[0];

        // forged packet: fwd=+X (raw), right=+Z (raw)
        // - ref:      yaw = atan2(fY+fX, fZ) = atan2(1, 0) = 90°
        // - fwdRight: mapped basis is axis-aligned -> yaw 0
        var expect = euler == "ref"
            ? OgsaMath.EulerToQuaternion(Math.PI / 2, 0, 0)
            : OgsaMath.EulerToQuaternion(0, 0, 0);
        AssertEx.ApproxEqual(expect.X, obj.Orientation.X, 1e-4f);
        AssertEx.ApproxEqual(expect.Y, obj.Orientation.Y, 1e-4f);
        AssertEx.ApproxEqual(expect.W, obj.Orientation.W, 1e-4f);

        // world velocity = dir * speed -> OGSA (z,y,x) of (30,0,0) = (0,0,30)
        AssertEx.ApproxEqual(30f, obj.Kinematics.Velocity.Z, 1e-3f);

        var disc = cap.Discovery();
        AssertEx.ApproxEqual(200f, cap.Channel(disc, m, "engine.rpm"), 0.1f);  // 6000/30
        AssertEx.ApproxEqual(30f, cap.Channel(disc, m, "vehicle.speed"), 1e-2f);
        AssertEx.ApproxEqual(0.5f, cap.Channel(disc, m, "ego.gForceLateral"), 1e-4f);
        AssertEx.ApproxEqual(0.05f, obj.DrivePoints[0].Wheel.Compression, 1e-5f); // wheel.fl
    }

    // ---------- SMS (pCARS2 / AMS2 / pCARS3) ----------

    [Fact]
    public void Sms_Translates_Pose_BodyDynamics_Channels()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("SMS");
        var src = new SmsSource();
        src.Configure(O());
        src.Declare(session);
        session.Start(Monotonic());

        var b = new byte[152];
        void F(int off, float v) => MemoryMarshal.Write(b.AsSpan(off, 4), ref v);
        void U(int off, ushort v) => MemoryMarshal.Write(b.AsSpan(off, 2), ref v);
        F(36, 0.75f); F(40, 55.5f); U(44, 5100); U(46, 9200);
        F(52, 0.1f); F(56, 0.2f); F(60, 0.3f);        // pitch, yaw, roll (rad)
        F(88, 0.1f); F(92, 0.2f); F(96, 0.3f);        // angular velocity
        F(100, 1f); F(104, 2f); F(108, 3f);           // local acceleration (g)

        src.Translate(b, session, Monotonic());
        var m = cap.LastMotion();
        var obj = m.Objects[0];

        var expect = OgsaMath.EulerToQuaternion(0.2, 0.1, 0.3);
        AssertEx.ApproxEqual(expect.X, obj.Orientation.X, 1e-5f);
        AssertEx.ApproxEqual(expect.Z, obj.Orientation.Z, 1e-5f);

        AssertEx.ApproxEqual(1f * 9.81f, obj.BodyDynamics.SpecificForce.X, 0.01f);
        AssertEx.ApproxEqual(3f * 9.81f, obj.BodyDynamics.SpecificForce.Z, 0.01f);
        AssertEx.ApproxEqual(0.2f, obj.BodyDynamics.AngularVelocity.Y, 1e-5f);

        var disc = cap.Discovery();
        AssertEx.ApproxEqual(5100f, cap.Channel(disc, m, "engine.rpm"), 0.1f);
        AssertEx.ApproxEqual(55.5f, cap.Channel(disc, m, "vehicle.speed"), 1e-3f);
    }

    // ---------- F1 (Codemasters 2020-2024) ----------

    static byte[] ForgeF1Motion(ushort format)
    {
        int hs = format >= 2023 ? 29 : 24;
        var b = new byte[hs + 60];
        MemoryMarshal.Write(b.AsSpan(0, 2), ref format);
        b[format >= 2023 ? 6 : 5] = 0;              // packetId = motion
        b[format >= 2023 ? 27 : 22] = 0;            // playerCarIndex
        void F(int off, float v) => MemoryMarshal.Write(b.AsSpan(hs + off, 4), ref v);
        F(36, 1f); F(40, 2f); F(44, 3f);            // g lat / long / vert
        F(48, 0.1f); F(52, 0.2f); F(56, 0.3f);      // yaw / pitch / roll
        return b;
    }

    [Theory]
    [InlineData(2024)]
    [InlineData(2023)]
    [InlineData(2020)]
    public void F1_Motion_Translates_Pose_And_BodyDynamics(ushort format)
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("F1");
        var src = new F1Source();
        src.Configure(O());
        src.Declare(session);
        session.Start(Monotonic());

        src.Translate(ForgeF1Motion(format), session, Monotonic());
        var m = cap.LastMotion();
        var obj = m.Objects[0];

        var expect = OgsaMath.EulerToQuaternion(0.1, 0.2, 0.3);
        AssertEx.ApproxEqual(expect.X, obj.Orientation.X, 1e-5f);
        AssertEx.ApproxEqual(expect.Y, obj.Orientation.Y, 1e-5f);

        // gForces (lat, long, vert) -> body (x, y, z) in m/s^2
        AssertEx.ApproxEqual(1f * 9.81f, obj.BodyDynamics.SpecificForce.X, 0.01f);
        AssertEx.ApproxEqual(3f * 9.81f, obj.BodyDynamics.SpecificForce.Y, 0.01f);
        AssertEx.ApproxEqual(2f * 9.81f, obj.BodyDynamics.SpecificForce.Z, 0.01f);
    }

    [Fact]
    public void F1_Telemetry2023_Feeds_Channels()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("F1");
        var src = new F1Source();
        src.Configure(O());
        src.Declare(session);
        session.Start(Monotonic());

        const int hs = 29;
        var b = new byte[hs + 60];
        ushort format = 2024;
        MemoryMarshal.Write(b.AsSpan(0, 2), ref format);
        b[6] = 6;                                   // packetId = telemetry
        b[27] = 0;                                  // playerCarIndex
        ushort speed = 100;                          // km/h
        MemoryMarshal.Write(b.AsSpan(hs, 2), ref speed);
        float throttle = 0.8f, brake = 0.3f;
        MemoryMarshal.Write(b.AsSpan(hs + 4, 4), ref throttle);
        MemoryMarshal.Write(b.AsSpan(hs + 12, 4), ref brake);
        b[hs + 17] = 5;                              // gear
        ushort rpm = 11000;
        MemoryMarshal.Write(b.AsSpan(hs + 18, 2), ref rpm);
        b[hs + 20] = 1;                              // drs

        src.Translate(b, session, Monotonic());          // channels cached
        src.Translate(ForgeF1Motion(2024), session, Monotonic()); // motion ticks
        var m = cap.LastMotion();
        var disc = cap.Discovery();

        AssertEx.ApproxEqual(11000f, cap.Channel(disc, m, "engine.rpm"), 0.1f);
        AssertEx.ApproxEqual(100f / 3.6f, cap.Channel(disc, m, "vehicle.speed"), 0.01f);
        AssertEx.ApproxEqual(0.8f, cap.Channel(disc, m, "engine.throttle"), 1e-4f);
        AssertEx.ApproxEqual(0.3f, cap.Channel(disc, m, "brakes.pedal"), 1e-4f);
    }

    // ---------- WRC ----------

    [Fact]
    public void Wrc_Translates_Pose_Kinematics_Channels()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("WRC");
        var src = new WrcSource();
        src.Configure(O());
        src.Declare(session);
        session.Start(Monotonic());

        var b = new byte[236];
        void F(int off, float v) => MemoryMarshal.Write(b.AsSpan(off, 4), ref v);
        F(40, 44.4f);                                 // speed m/s
        F(48, 100f); F(52, -5f); F(56, 20f);          // position
        F(60, 1f); F(64, 2f); F(68, 3f);              // velocity world
        F(72, 0.5f); F(76, 0f); F(80, 0f);            // acceleration world
        F(84, 1f); F(88, 0f); F(92, 0f);              // left dir (+X)
        F(96, 0f); F(100, 0f); F(104, 1f);            // forward dir (+Z)
        F(184, 9500f); F(192, 6500f);                 // rpm max / current
        F(196, 0.9f); F(200, 0.1f);                   // throttle / brake
        b[37] = 3;                                    // gear index
        F(212, 1f);                                   // handbrake

        src.Translate(b, session, Monotonic());
        var m = cap.LastMotion();
        var obj = m.Objects[0];

        // forward +Z (EGO-style world) -> OGSA +X -> yaw 90°
        var expect = OgsaMath.EulerToQuaternion(Math.PI / 2, 0, 0);
        AssertEx.ApproxEqual(expect.Y, obj.Orientation.Y, 1e-4f);

        AssertEx.ApproxEqual(3f, obj.Kinematics.Velocity.X, 1e-4f);  // (z,y,x) of (1,2,3)
        AssertEx.ApproxEqual(2f, obj.Kinematics.Velocity.Y, 1e-4f);
        AssertEx.ApproxEqual(1f, obj.Kinematics.Velocity.Z, 1e-4f);

        var disc = cap.Discovery();
        AssertEx.ApproxEqual(6500f, cap.Channel(disc, m, "engine.rpm"), 0.1f);
        AssertEx.ApproxEqual(44.4f, cap.Channel(disc, m, "vehicle.speed"), 1e-3f);
        AssertEx.ApproxEqual(0.9f, cap.Channel(disc, m, "engine.throttle"), 1e-4f);
    }

    // ---------- LFS (OutSim) ----------

    [Fact]
    public void Lfs_Translates_Pose_Rates_DerivedSpecificForce()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("LFS");
        var src = new LfsSource();
        src.Configure(new Dictionary<string, string> { ["gravIncluded"] = "true" });
        src.Declare(session);
        session.Start(Monotonic());

        var b = new byte[44];
        void F(int off, float v) => MemoryMarshal.Write(b.AsSpan(off, 4), ref v);
        F(4, 0.1f); F(8, 0.2f); F(12, 0.3f);          // ang vel (x,y,z)
        F(16, 0.1f); F(20, 0.2f); F(24, 0.3f);        // heading / pitch / roll
        F(28, 1f); F(32, 0f); F(36, 0f);              // accel (g): X fwd

        src.Translate(b, session, Monotonic());
        var m = cap.LastMotion();
        var obj = m.Objects[0];

        var expect = OgsaMath.EulerToQuaternion(0.1, 0.2, 0.3);
        AssertEx.ApproxEqual(expect.X, obj.Orientation.X, 1e-5f);

        // rates: (-y, -z, +x)
        AssertEx.ApproxEqual(-0.2f, obj.BodyDynamics.AngularVelocity.X, 1e-5f);
        AssertEx.ApproxEqual(-0.3f, obj.BodyDynamics.AngularVelocity.Y, 1e-5f);
        AssertEx.ApproxEqual(0.1f, obj.BodyDynamics.AngularVelocity.Z, 1e-5f);

        // gravIncluded -> specific force is the raw mapped accel: 1g forward
        AssertEx.ApproxEqual(9.81f, obj.BodyDynamics.SpecificForce.Z, 0.02f);
    }

    // ---------- acpmf (Assetto Corsa / ACC) ----------

    [Fact]
    public void AcFamily_Translates_And_Gates_On_PacketId()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("ac-family");
        var src = new AcFamilySource();
        src.Configure(O());
        src.Declare(session);
        session.Start(Monotonic());

        var b = new byte[220];
        void F(int off, float v) => MemoryMarshal.Write(b.AsSpan(off, 4), ref v);
        void I(int off, int v) => MemoryMarshal.Write(b.AsSpan(off, 4), ref v);
        I(0, 1);                                      // PacketId
        F(4, 0.7f);                                   // gas
        F(8, 0.2f);                                   // brake
        I(16, 3);                                     // gear
        I(20, 7200);                                  // rpms
        F(28, 144f);                                  // speed km/h
        F(44, 0.5f); F(48, 1.5f); F(52, 1.0f);        // AccG lat, long, vert
        F(56, 0.2f);                                  // wheel slip FL
        F(184, 0.05f);                                // susp travel FL
        F(208, 1.57f); F(212, 0.05f); F(216, 0.1f);   // heading, pitch, roll

        src.Translate(b, session, Monotonic());
        var m = cap.LastMotion();
        var obj = m.Objects[0];

        var expect = OgsaMath.EulerToQuaternion(1.57, 0.05, 0.1);
        AssertEx.ApproxEqual(expect.Y, obj.Orientation.Y, 1e-4f);

        AssertEx.ApproxEqual(0.5f * 9.81f, obj.BodyDynamics.SpecificForce.X, 0.01f);
        AssertEx.ApproxEqual(1.0f * 9.81f, obj.BodyDynamics.SpecificForce.Y, 0.01f);
        AssertEx.ApproxEqual(1.5f * 9.81f, obj.BodyDynamics.SpecificForce.Z, 0.01f);

        AssertEx.ApproxEqual(0.05f, obj.DrivePoints[0].Wheel.Compression, 1e-5f);
        AssertEx.ApproxEqual(0.2f, obj.DrivePoints[0].Wheel.Slip, 1e-5f);

        var disc = cap.Discovery();
        AssertEx.ApproxEqual(7200f, cap.Channel(disc, m, "engine.rpm"), 0.1f);
        AssertEx.ApproxEqual(40f, cap.Channel(disc, m, "vehicle.speed"), 0.01f);

        // unchanged page (same PacketId) emits nothing new
        int motionsBefore = cap.Sent.Count(x => x.type == FrameType.Motion);
        src.Translate(b, session, Monotonic());
        Assert.Equal(motionsBefore, cap.Sent.Count(x => x.type == FrameType.Motion));
    }
}
}
