#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OgsaTsBridge.Sources;
using Ogsa.Telemetry;
using Xunit;

namespace OgsaTsBridge.Tests
{
/// <summary>
/// End-to-end over real UDP sockets: a forged legacy packet goes into the
/// input port the Forwarder listens on; an OGSA-TS receiver joined to the
/// multicast group (a private group + port per test) must see the discovery
/// and the translated motion frame, and every accepted input packet must
/// produce exactly one motion frame (pass-through cadence).
/// </summary>
public sealed class LoopbackForwardTests
{
    static byte[] ForgeBeamNg(float x, float y, float z,
                              float roll, float pitch, float yaw)
    {
        var p = new BeamNgSource.Packet
        {
            Magic = 1f,
            PosX = x, PosY = y, PosZ = z,
            Roll = roll, Pitch = pitch, Yaw = yaw,
        };
        var buf = new byte[BeamNgSource.PacketSize];
        System.Runtime.InteropServices.MemoryMarshal.Write(
            buf.AsSpan(), ref p);
        return buf;
    }

    [Fact]
    public async Task BeamNg_Packet_Becomes_Ogsa_Motion_With_Pass_Through_Cadence()
    {
        const int inPort = 44544;               // away from real game defaults
        const int outPort = 48123;              // away from the default 40123
        const string group = "239.255.17.99";

        var source = new BeamNgSource();
        source.Configure(new Dictionary<string, string>());

        using var receiver = new OgsaReceiver(group, outPort);
        var discovery = new TaskCompletionSource<CapnpGen.DiscoveryFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var motions = new Channel<(CapnpGen.MotionFrame.READER frame, double arrivedUs)>();
        receiver.Discovery += d => discovery.TrySetResult(d);
        receiver.Motion += (m, atUs) => motions.Write((m, atUs));
        receiver.Start();

        using var input = new UdpInputSocket(new IPEndPoint(IPAddress.Any, inPort));
        using var sender = new TelemetrySender(group, outPort);
        using var session = new OgsaSession(sender, source.Title);
        source.Declare(session);
        using var cts = new CancellationTokenSource();
        using var registration = cts.Token.Register(() => input.Dispose());
        var forwarder = new Forwarder(source, input, session);
        var thread = new Thread(() =>
        {
            try { forwarder.Run(cts.Token); } catch (ObjectDisposedException) { }
        }) { IsBackground = true };
        thread.Start();

        // drive: roll 10°, yaw 90° (no pitch — keeps the forward-vector check trivial)
        var payload = ForgeBeamNg(10f, -20f, 30f, 0.1745f, 0f, 1.5708f);
        using var game = new UdpClient();
        const int packets = 50;
        var latencies = new List<double>();
        CapnpGen.MotionFrame.READER first = default;
        for (int i = 0; i < packets; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            await game.SendAsync(payload, payload.Length, "127.0.0.1", inPort);
            await Task.Delay(16); // ~60 Hz like the real game
            if (motions.TryRead(out var got, TimeSpan.FromMilliseconds(500)))
            {
                latencies.Add(got.arrivedUs - t0 * 1_000_000.0 / Stopwatch.Frequency);
                first = got.frame;
            }
            else { Assert.Fail("no motion frame within 500 ms of an input packet"); }
        }

        var disc = await discovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("BeamNG.drive", disc.GameName);
        Assert.Single(disc.Objects);
        Assert.True(disc.Objects[0].Primary);

        // 1:1 pass-through: every packet produced a frame (we drained 50)
        Assert.Equal(packets, (int)latencies.Count);

        var obj = first.Objects[0];
        Assert.Equal("car", obj.Name);

        // position: BeamNG (x,y,z)=(10,-20,30) -> OGSA (-y,z,x) = (20,30,10)
        Assert.Equal(20f, obj.Position.X, 1);
        Assert.Equal(30f, obj.Position.Y, 1);
        Assert.Equal(10f, obj.Position.Z, 1);

        // orientation: yaw 90° -> body forward (0,0,1) maps to world +X
        var fwd = Vector3.Transform(new Vector3(0, 0, 1),
            new Quaternion(obj.Orientation.X, obj.Orientation.Y,
                           obj.Orientation.Z, obj.Orientation.W));
        AssertEx.ApproxEqual(1f, fwd.X, 0.001f);
        AssertEx.ApproxEqual(0f, fwd.Y, 0.001f);
        AssertEx.ApproxEqual(0f, fwd.Z, 0.01f);

        cts.Cancel();
        thread.Join(2000);
    }

    [Fact]
    public async Task Forza_Sled_Packet_Becomes_Ogsa_Motion_With_Channels_And_Wheels()
    {
        const int inPort = 44545;
        const int outPort = 48124;
        const string group = "239.255.17.99";

        var source = new ForzaSledSource();
        source.Configure(new Dictionary<string, string>());

        using var receiver = new OgsaReceiver(group, outPort);
        var discovery = new TaskCompletionSource<CapnpGen.DiscoveryFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var motion = new TaskCompletionSource<CapnpGen.MotionFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Discovery += d => discovery.TrySetResult(d);
        receiver.Motion += (m, _) => motion.TrySetResult(m);
        receiver.Start();

        using var input = new UdpInputSocket(new IPEndPoint(IPAddress.Any, inPort));
        using var sender = new TelemetrySender(group, outPort);
        using var session = new OgsaSession(sender, source.Title);
        source.Declare(session);
        using var cts = new CancellationTokenSource();
        using var registration = cts.Token.Register(() => input.Dispose());
        var forwarder = new Forwarder(source, input, session);
        var thread = new Thread(() =>
        {
            try { forwarder.Run(cts.Token); } catch (ObjectDisposedException) { }
        }) { IsBackground = true };
        thread.Start();

        var payload = Fixtures.ForgeForzaSled();
        using var game = new UdpClient();
        await game.SendAsync(payload, payload.Length, "127.0.0.1", inPort);

        CapnpGen.DiscoveryFrame.READER disc;
        try
        {
            disc = await discovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"no discovery in 5s: receiver raw={receiver.RawDatagrams} " +
                        $"disc={receiver.DiscoveryFrames} mot={receiver.MotionFrames} " +
                        $"foreign={receiver.ForeignSkipped} decodeErr={receiver.DecodeErrors}; " +
                        $"forwarder in={forwarder.Stats.PacketsIn} out={forwarder.Stats.PacketsOut} " +
                        $"rej={forwarder.Stats.Rejected} started={forwarder.Stats.Started}");
            return; // unreachable
        }
        Assert.Equal("Forza (Sled format)", disc.GameName);
        var channels = new Dictionary<string, string>();
        foreach (var ch in disc.Objects[0].Channels)
            channels[ch.Name] = ch.Unit;
        Assert.Equal("rpm", channels["engine.rpm"]);
        Assert.Equal("m/s", channels["vehicle.speed"]);
        Assert.Equal(4, disc.Objects[0].DrivePoints.Count);

        var frame = await motion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var obj = frame.Objects[0];
        Assert.Equal("car", obj.Name);

        // body dynamics as forged (see Fixtures): specific force + rates
        AssertEx.ApproxEqual(Fixtures.AccX, obj.BodyDynamics.SpecificForce.X, 1e-3f);
        AssertEx.ApproxEqual(Fixtures.AccY, obj.BodyDynamics.SpecificForce.Y, 1e-3f);
        AssertEx.ApproxEqual(Fixtures.AccZ, obj.BodyDynamics.SpecificForce.Z, 1e-3f);
        AssertEx.ApproxEqual(Fixtures.AngVelX, obj.BodyDynamics.AngularVelocity.X, 1e-3f);
        AssertEx.ApproxEqual(Fixtures.AngVelY, obj.BodyDynamics.AngularVelocity.Y, 1e-3f);
        AssertEx.ApproxEqual(Fixtures.AngVelZ, obj.BodyDynamics.AngularVelocity.Z, 1e-3f);

        // wheels: forged combined slip / compression / rumble contact
        Assert.Equal(4, obj.DrivePoints.Count);
        AssertEx.ApproxEqual(Fixtures.CombinedSlipFL, obj.DrivePoints[0].Wheel.Slip, 1e-3f);
        AssertEx.ApproxEqual(Fixtures.SuspMetersFL, obj.DrivePoints[0].Wheel.Compression, 1e-3f);
        Assert.Equal(CapnpGen.ContactMedium.rumbleStrip, obj.DrivePoints[0].Wheel.Contact);
        Assert.Equal(CapnpGen.ContactMedium.asphalt, obj.DrivePoints[1].Wheel.Contact);

        // channels by discovery id
        var values = new Dictionary<ushort, float>();
        var booleans = new Dictionary<ushort, bool>();
        foreach (var cv in obj.Channels)
        {
            if (cv.which == CapnpGen.ChannelValue.WHICH.Boolean)
                booleans[cv.Id] = cv.Boolean;
            else values[cv.Id] = cv.Number;
        }
        ushort IdOf(string name) => FindId(disc, name);
        AssertEx.ApproxEqual(Fixtures.CurrentEngineRpm, values[IdOf("engine.rpm")], 0.1f);
        AssertEx.ApproxEqual(Fixtures.SpeedMagnitude(), values[IdOf("vehicle.speed")], 0.01f);
        Assert.True(booleans[IdOf("forza.raceOn")]);

        cts.Cancel();
        thread.Join(2000);
    }

    static ushort FindId(CapnpGen.DiscoveryFrame.READER disc, string name)
    {
        foreach (var ch in disc.Objects[0].Channels)
            if (ch.Name == name) return ch.Id;
        throw new ArgumentException($"channel {name} not in discovery");
    }
}

static class AssertEx
{
    public static void ApproxEqual(float expected, float actual, float tol) =>
        Assert.True(Math.Abs(expected - actual) <= tol,
            $"expected ~{expected}, got {actual} (tol {tol})");
}
}
