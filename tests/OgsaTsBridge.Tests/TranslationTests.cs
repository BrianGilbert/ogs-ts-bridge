#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using OgsaTsBridge.Sources;
using Ogsa.Telemetry;
using Xunit;

namespace OgsaTsBridge.Tests
{
/// <summary>Locks OgsaMath.EulerToQuaternion to the C-4 rotation-sense
/// definitions: +yaw nose-right, +pitch nose-up, +roll right-side-down.</summary>
public class OgsaEulerTests
{
    const float Tol = 1e-4f;

    static Vector3 Apply(Quaternion q, Vector3 v) => Vector3.Transform(v, q);

    [Fact]
    public void PositiveYaw_TurnsNoseRight()
    {
        var q = OgsaMath.EulerToQuaternion(Math.PI / 2, 0, 0);
        var fwd = Apply(q, new Vector3(0, 0, 1));
        AssertEx.ApproxEqual(1f, fwd.X, Tol);
        AssertEx.ApproxEqual(0f, fwd.Y, Tol);
        AssertEx.ApproxEqual(0f, fwd.Z, Tol);
        var up = Apply(q, new Vector3(0, 1, 0));
        AssertEx.ApproxEqual(0f, up.X, Tol);
        AssertEx.ApproxEqual(1f, up.Y, Tol);
        AssertEx.ApproxEqual(0f, up.Z, Tol);
    }

    [Fact]
    public void PositivePitch_RaisesNose()
    {
        var q = OgsaMath.EulerToQuaternion(0, Math.PI / 2, 0);
        var fwd = Apply(q, new Vector3(0, 0, 1));
        AssertEx.ApproxEqual(0f, fwd.X, Tol);
        AssertEx.ApproxEqual(1f, fwd.Y, Tol);
        AssertEx.ApproxEqual(0f, fwd.Z, Tol);
    }

    [Fact]
    public void PositiveRoll_PutsRightSideDown()
    {
        var q = OgsaMath.EulerToQuaternion(0, 0, Math.PI / 2);
        var up = Apply(q, new Vector3(0, 1, 0));       // body up tips right…
        AssertEx.ApproxEqual(1f, up.X, Tol);
        AssertEx.ApproxEqual(0f, up.Y, Tol);
        AssertEx.ApproxEqual(0f, up.Z, Tol);
        var right = Apply(q, new Vector3(1, 0, 0));    // …body right dips down
        AssertEx.ApproxEqual(0f, right.X, Tol);
        AssertEx.ApproxEqual(-1f, right.Y, Tol);
        AssertEx.ApproxEqual(0f, right.Z, Tol);
    }

    [Fact]
    public void YawThenPitch_ComposesIntrinsically()
    {
        var q = OgsaMath.EulerToQuaternion(Math.PI / 2, Math.PI / 2, 0);
        var fwd = Apply(q, new Vector3(0, 0, 1));      // nose-up, then yawed
        AssertEx.ApproxEqual(0f, fwd.X, Tol);
        AssertEx.ApproxEqual(1f, fwd.Y, Tol);
        AssertEx.ApproxEqual(0f, fwd.Z, Tol);
    }

    [Fact]
    public void ZeroEuler_IsIdentity()
    {
        var q = OgsaMath.EulerToQuaternion(0, 0, 0);
        var fwd = Apply(q, new Vector3(0, 0, 1));
        AssertEx.ApproxEqual(0f, fwd.X, Tol);
        AssertEx.ApproxEqual(0f, fwd.Y, Tol);
        AssertEx.ApproxEqual(1f, fwd.Z, Tol);
    }
}

/// <summary>Translate forged packets through a capture-sink session and
/// assert the wire-level output decodes back to the expected values.</summary>
public class TranslationTests
{
    sealed class Capture : IDisposable
    {
        public readonly List<(FrameType type, byte[] datagram)> Sent = new();
        public OgsaSession MakeSession(string game, double maxHz = 1000) =>
            new((t, d) => Sent.Add((t, d)), game, maxHz);

        public CapnpGen.MotionFrame.READER LastMotion()
        {
            var d = Sent.Last(x => x.type == FrameType.Motion).datagram;
            Assert.True(Envelope.TryUnwrap(d, out var type, out var payload));
            Assert.Equal(FrameType.Motion, type);
            return CapnpGen.MotionFrame.READER.create(
                Capnp.DeserializerState.CreateRoot(MessageDecoder.DecodeMessage(payload.Span)));
        }

        public CapnpGen.DiscoveryFrame.READER SingleDiscovery()
        {
            var d = Sent.Where(x => x.type == FrameType.Discovery).Last().datagram;
            Assert.True(Envelope.TryUnwrap(d, out var type, out var payload));
            return CapnpGen.DiscoveryFrame.READER.create(
                Capnp.DeserializerState.CreateRoot(MessageDecoder.DecodeMessage(payload.Span)));
        }

        public void Dispose() { }
    }

    static double Monotonic() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    [Fact]
    public void Forza_Motion_DecodesWithBodyDynamicsWheelsChannels()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("Forza (Sled format)");
        var source = new ForzaSledSource();
        source.Configure(new Dictionary<string, string>());
        source.Declare(session);
        session.Start(Monotonic());

        source.Translate(Fixtures.ForgeForzaSled(), session, Monotonic());

        var disc = cap.SingleDiscovery();
        Assert.Equal(1u, disc.SchemaVersion);
        Assert.Equal("Forza (Sled format)", disc.GameName);
        Assert.Equal(4, disc.Objects[0].DrivePoints.Count);
        var names = disc.Objects[0].Channels.Select(c => c.Name).ToList();
        Assert.Contains("engine.rpm", names);
        Assert.Contains("vehicle.speed", names);
        // MTU budget: discovery must stay well under one Ethernet MTU
        Assert.True(cap.Sent.First(x => x.type == FrameType.Discovery).datagram.Length < 1400,
            $"discovery datagram {cap.Sent.First(x => x.type == FrameType.Discovery).datagram.Length} B exceeds the MTU budget");

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertEx.ApproxEqual(Fixtures.AccX, obj.BodyDynamics.SpecificForce.X, 1e-3f);
        AssertEx.ApproxEqual(Fixtures.AngVelY, obj.BodyDynamics.AngularVelocity.Y, 1e-3f);
        Assert.Equal(CapnpGen.MotionObject.WHICH.BodyDynamics, obj.which);

        // orientation matches a from-scratch EulerToQuaternion of the packet
        var expect = OgsaMath.EulerToQuaternion(Fixtures.Yaw, Fixtures.Pitch, Fixtures.Roll);
        AssertEx.ApproxEqual(expect.X, obj.Orientation.X, 1e-5f);
        AssertEx.ApproxEqual(expect.Y, obj.Orientation.Y, 1e-5f);
        AssertEx.ApproxEqual(expect.Z, obj.Orientation.Z, 1e-5f);
        AssertEx.ApproxEqual(expect.W, obj.Orientation.W, 1e-5f);
    }

    [Fact]
    public void Forza_MenuPackets_ProduceNoMotionFrames()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("Forza (Sled format)");
        var source = new ForzaSledSource();
        source.Configure(new Dictionary<string, string>());
        source.Declare(session);
        session.Start(Monotonic());

        source.Translate(Fixtures.ForgeForzaSled(raceOn: false), session, Monotonic());

        Assert.DoesNotContain(cap.Sent, x => x.type == FrameType.Motion);
    }

    [Fact]
    public void Forza_EulerDegrees_OptionIsHonoured()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("Forza (Sled format)");
        var source = new ForzaSledSource();
        source.Configure(new Dictionary<string, string> { ["euler"] = "deg" });
        source.Declare(session);
        session.Start(Monotonic());

        // fixture carries radian-scale numbers; with euler=deg they are read
        // as degrees, so orientation must differ from the radian reading.
        source.Translate(Fixtures.ForgeForzaSled(), session, Monotonic());
        var m = cap.LastMotion();
        var expect = OgsaMath.EulerToQuaternion(
            Fixtures.Yaw * Math.PI / 180, Fixtures.Pitch * Math.PI / 180,
            Fixtures.Roll * Math.PI / 180);
        AssertEx.ApproxEqual(expect.X, m.Objects[0].Orientation.X, 1e-4f);
    }

    [Fact]
    public void BeamNg_Motion_DecodesWithKinematicsAndMappedWorld()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("BeamNG.drive");
        var source = new BeamNgSource();
        source.Configure(new Dictionary<string, string>());
        source.Declare(session);
        session.Start(Monotonic());

        source.Translate(
            Fixtures.ForgeBeamNg(new Vector3(10f, -20f, 30f), 0.1f, 0.2f, 0.3f,
                                 vel: new Vector3(1f, 2f, 3f), acc: new Vector3(0.5f, 1f, 1.5f)),
            session, Monotonic());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        Assert.Equal(CapnpGen.MotionObject.WHICH.Kinematics, obj.which);
        AssertEx.ApproxEqual(-2f, obj.Kinematics.Velocity.X, 1e-4f);  // (x,y,z)->(-y,z,x)
        AssertEx.ApproxEqual(3f, obj.Kinematics.Velocity.Y, 1e-4f);
        AssertEx.ApproxEqual(1f, obj.Kinematics.Velocity.Z, 1e-4f);
        AssertEx.ApproxEqual(20f, obj.Position.X, 1e-3f);
        AssertEx.ApproxEqual(30f, obj.Position.Y, 1e-3f);
        AssertEx.ApproxEqual(10f, obj.Position.Z, 1e-3f);
    }

    [Fact]
    public void AcceptsDatagram_FiltersForeignLengths()
    {
        Assert.True(new BeamNgSource().AcceptsDatagram(BeamNgSource.PacketSize));
        Assert.False(new BeamNgSource().AcceptsDatagram(87));
        Assert.True(new ForzaSledSource().AcceptsDatagram(ForzaSledSource.PacketSize));
        Assert.False(new ForzaSledSource().AcceptsDatagram(311)); // dash format: not v1
    }

    [Fact]
    public void EncodeHotPath_StaysFast()
    {
        using var cap = new Capture();
        using var session = cap.MakeSession("Forza (Sled format)", maxHz: 0); // no cap
        var source = new ForzaSledSource();
        source.Configure(new Dictionary<string, string>());
        source.Declare(session);
        session.Start(Monotonic());

        var payload = Fixtures.ForgeForzaSled();
        // warm-up (JIT + first-alloc sizing)
        for (int i = 0; i < 200; i++)
            source.Translate(payload, session, Monotonic());

        const int n = 5000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
            source.Translate(payload, session, Monotonic());
        sw.Stop();
        double avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
        // generous CI budget: a translate+encode+capture round is tens of µs
        // on commodity hardware; 1 ms would mean something is badly wrong
        Assert.True(avgUs < 1000.0, $"avg translate+encode {avgUs:F0} µs — over budget");
    }
}
}
