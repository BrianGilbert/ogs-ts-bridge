#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using OgsTsBridge.Sources;
using Ogs.Telemetry;
using Xunit;

namespace OgsTsBridge.Tests
{
/// <summary>Forged SimConnect frames through the msfs source: each test
/// fills the frame variables by SimVar name (packed doubles, the
/// SimConnectForwarder's hand-off format) and asserts the decoded
/// OGS-TS output.</summary>
public class MsfsSourceTests
{
    sealed class Rig
    {
        public readonly List<(FrameType type, byte[] datagram)> Sent = new();
        public readonly MsfsSource Source = new();
        public readonly OgsSession Session;
        public readonly double[] Frame;
        public double Clock = 100;

        public Rig(Dictionary<string, string>? options = null)
        {
            Session = new OgsSession((t, d) => Sent.Add((t, d)), "MSFS");
            Source.Configure(options ?? new Dictionary<string, string>());
            Source.Declare(Session);
            Session.Start(Clock);
            Frame = new double[Source.FrameVars.Count];
        }

        public void Set(string simVar, double value)
        {
            for (int i = 0; i < Source.FrameVars.Count; i++)
                if (Source.FrameVars[i].Name == simVar) { Frame[i] = value; return; }
            throw new ArgumentException($"no frame variable {simVar}");
        }

        public void Send()
        {
            Clock += 1.0 / 60;
            Source.Translate(MemoryMarshal.AsBytes(Frame.AsSpan()), Session, Clock);
        }

        public void Aircraft(string title, int engines, int engineType, Vector3? eye = null)
        {
            var a = new SimVarValues(Source.AircraftVars.Count);
            for (int i = 0; i < Source.AircraftVars.Count; i++)
            {
                switch (Source.AircraftVars[i].Name)
                {
                    case "TITLE": a.Texts[i] = title; break;
                    case "NUMBER OF ENGINES": a.Numbers[i] = engines; break;
                    case "ENGINE TYPE": a.Numbers[i] = engineType; break;
                    case "EYEPOINT POSITION": a.Vectors[i] = eye; break;
                }
            }
            Clock += 0.001;
            Source.OnAircraft(a, Session, Clock);
        }

        static Capnp.DeserializerState Root(byte[] datagram)
        {
            Envelope.TryUnwrap(datagram, out _, out var payload);
            return Capnp.DeserializerState.CreateRoot(MessageDecoder.DecodeMessage(payload.Span));
        }

        public CapnpGen.DiscoveryFrame.READER Discovery() =>
            CapnpGen.DiscoveryFrame.READER.create(Root(Sent.Last(x => x.type == FrameType.Discovery).datagram));

        public CapnpGen.MotionFrame.READER Motion() =>
            CapnpGen.MotionFrame.READER.create(Root(Sent.Last(x => x.type == FrameType.Motion).datagram));

        public int MotionCount => Sent.Count(x => x.type == FrameType.Motion);

        ushort Id(string name) =>
            Discovery().Objects[0].Channels.First(c => c.Name == name).Id;

        public CapnpGen.ChannelValue.READER? Value(string name)
        {
            var id = Id(name);
            foreach (var cv in Motion().Objects[0].Channels)
                if (cv.Id == id) return cv;
            return null;
        }

        public float Number(string name) =>
            Value(name)?.Number ?? throw new ArgumentException($"{name} not in the last frame");

        public bool Boolean(string name) =>
            Value(name)?.Boolean ?? throw new ArgumentException($"{name} not in the last frame");
    }

    static Rig Level()
    {
        var rig = new Rig();
        rig.Set("PLANE LATITUDE", 0.8);
        rig.Set("PLANE LONGITUDE", 0.2);
        rig.Set("PLANE ALTITUDE", 1000);
        rig.Set("AMBIENT DENSITY", 1.1);
        rig.Set("AMBIENT TEMPERATURE", 8);
        rig.Set("AMBIENT PRESSURE", 900);
        return rig;
    }

    [Fact]
    public void Level_Rest_Reads_Gravity_Reaction_Up()
    {
        var rig = Level();
        rig.Set("PLANE HEADING DEGREES TRUE", Math.PI / 2);
        rig.Send();

        var obj = rig.Motion().Objects[0];
        Assert.Equal(CapnpGen.MotionObject.WHICH.BodyDynamics, obj.which);
        AssertEx.ApproxEqual(0f, obj.BodyDynamics.SpecificForce.X, 1e-3f);
        AssertEx.ApproxEqual(9.80665f, obj.BodyDynamics.SpecificForce.Y, 1e-3f);
        AssertEx.ApproxEqual(0f, obj.BodyDynamics.SpecificForce.Z, 1e-3f);
        // discovery declares the gravity the reaction was computed with
        Assert.Equal(9.80665f, rig.Discovery().Environment.Gravity);

        // heading 90° true: the nose points east (+x)
        var q = new Quaternion(obj.Orientation.X, obj.Orientation.Y, obj.Orientation.Z, obj.Orientation.W);
        var forward = Vector3.Transform(Vector3.UnitZ, q);
        AssertEx.ApproxEqual(1f, forward.X, 1e-4f);
    }

    [Fact]
    public void Msfs_Pitch_And_Bank_Signs_Map_To_C4()
    {
        var rig = Level();
        rig.Set("PLANE PITCH DEGREES", -0.2);   // MSFS: negative = nose up
        rig.Set("PLANE BANK DEGREES", 0.3);     // MSFS: positive = left wing down
        rig.Set("ROTATION VELOCITY BODY X", -0.1);
        rig.Set("ROTATION VELOCITY BODY Y", 0.05);
        rig.Set("ROTATION VELOCITY BODY Z", 0.2);
        rig.Send();

        var obj = rig.Motion().Objects[0];
        var expect = OgsMath.EulerToQuaternion(0, 0.2, -0.3);
        AssertEx.ApproxEqual(expect.X, obj.Orientation.X, 1e-5f);
        AssertEx.ApproxEqual(expect.Y, obj.Orientation.Y, 1e-5f);
        AssertEx.ApproxEqual(expect.Z, obj.Orientation.Z, 1e-5f);
        AssertEx.ApproxEqual(expect.W, obj.Orientation.W, 1e-5f);
        AssertEx.ApproxEqual(0.1f, obj.BodyDynamics.AngularVelocity.X, 1e-5f);
        AssertEx.ApproxEqual(0.05f, obj.BodyDynamics.AngularVelocity.Y, 1e-5f);
        AssertEx.ApproxEqual(-0.2f, obj.BodyDynamics.AngularVelocity.Z, 1e-5f);
    }

    [Fact]
    public void FlipOptions_Negate_Euler_And_Rate_Together()
    {
        var rig = new Rig(new Dictionary<string, string> { ["flipPitch"] = "true" });
        rig.Set("PLANE PITCH DEGREES", -0.2);
        rig.Set("ROTATION VELOCITY BODY X", -0.1);
        rig.Send();

        var obj = rig.Motion().Objects[0];
        var expect = OgsMath.EulerToQuaternion(0, -0.2, 0);
        AssertEx.ApproxEqual(expect.X, obj.Orientation.X, 1e-5f);
        AssertEx.ApproxEqual(-0.1f, obj.BodyDynamics.AngularVelocity.X, 1e-5f);
    }

    [Fact]
    public void Position_Is_Local_Tangent_And_Reorigins_On_Teleport()
    {
        var rig = Level();
        rig.Send();
        var p = rig.Motion().Objects[0].Position;
        Assert.Equal(0f, p.X); Assert.Equal(0f, p.Y); Assert.Equal(0f, p.Z);

        rig.Set("PLANE LATITUDE", 0.8 + 10 / 6371008.8);   // 10 m north
        rig.Set("PLANE ALTITUDE", 1005);
        rig.Send();
        p = rig.Motion().Objects[0].Position;
        AssertEx.ApproxEqual(0f, p.X, 1e-3f);
        AssertEx.ApproxEqual(5f, p.Y, 1e-3f);
        AssertEx.ApproxEqual(10f, p.Z, 1e-2f);

        rig.Set("PLANE LONGITUDE", 0.3);                   // hundreds of km: a teleport
        rig.Send();
        p = rig.Motion().Objects[0].Position;
        Assert.Equal(0f, p.X); Assert.Equal(0f, p.Z);
    }

    [Fact]
    public void Channels_Convert_To_Standard_Units()
    {
        var rig = Level();
        rig.Set("AIRSPEED INDICATED", 61.7);
        rig.Set("COM ACTIVE FREQUENCY:1", 118.5);           // MHz
        rig.Set("TRANSPONDER CODE:1", 0x1200);              // BCO16
        rig.Set("NAV CDI:1", -63.5);
        rig.Set("FLAPS HANDLE INDEX", 2);
        rig.Set("FLAPS NUM HANDLE POSITIONS", 4);
        rig.Set("LIGHT LANDING", 1);
        rig.Set("FUEL TOTAL QUANTITY", 30);
        rig.Set("FUEL TOTAL CAPACITY", 60);
        rig.Set("BRAKE LEFT POSITION", 0.4);
        rig.Set("BRAKE RIGHT POSITION", 0.6);
        rig.Send();

        AssertEx.ApproxEqual(61.7f, rig.Number("air.indicatedAirspeed"), 1e-3f);
        AssertEx.ApproxEqual(118.5e6f, rig.Number("nav.com1.active"), 10f);
        Assert.Equal(1200f, rig.Number("nav.xpdr.code"));
        AssertEx.ApproxEqual(-0.5f, rig.Number("nav.locDeflection"), 1e-4f);
        AssertEx.ApproxEqual(0.5f, rig.Number("ctrl.flaps"), 1e-6f);
        AssertEx.ApproxEqual(0.5f, rig.Number("fuel.level"), 1e-6f);
        AssertEx.ApproxEqual(0.5f, rig.Number("brakes.pedal"), 1e-6f);
        Assert.True(rig.Boolean("light.landing"));

        var disc = rig.Discovery();
        var com = disc.Objects[0].Channels.First(c => c.Name == "nav.com1.active");
        Assert.Equal("Hz", com.Unit);
        AssertEx.ApproxEqual(90000f, disc.Environment.Pressure, 1e-2f);
    }

    [Fact]
    public void Unchanged_Channels_Are_Not_Resent_But_Paused_Always_Is()
    {
        var rig = Level();
        rig.Set("AIRSPEED INDICATED", 50);
        rig.Send();
        Assert.NotNull(rig.Value("air.indicatedAirspeed"));

        rig.Send();                                         // nothing changed
        Assert.Null(rig.Value("air.indicatedAirspeed"));
        Assert.False(rig.Boolean("session.paused"));

        rig.Set("AIRSPEED INDICATED", 51);
        rig.Send();
        AssertEx.ApproxEqual(51f, rig.Number("air.indicatedAirspeed"), 1e-4f);
        Assert.True(rig.Motion().Objects[0].Channels.Count < 5);

        rig.Clock += 2.5;                                   // discovery re-send → full refresh
        rig.Send();
        AssertEx.ApproxEqual(51f, rig.Number("air.indicatedAirspeed"), 1e-4f);
        Assert.NotNull(rig.Value("light.landing"));
    }

    [Fact]
    public void Pause_Sends_NoDynamics_And_Keeps_Streaming_When_Frames_Stop()
    {
        var rig = Level();
        rig.Send();
        Assert.False(rig.Boolean("session.paused"));

        rig.Source.OnSimState(pauseFlags: 1, simRunning: true);
        rig.Send();
        Assert.True(rig.Boolean("session.paused"));
        Assert.Equal(CapnpGen.MotionObject.WHICH.NoDynamics, rig.Motion().Objects[0].which);

        int frames = rig.MotionCount;
        rig.Clock += 0.05;
        rig.Source.OnIdle(rig.Session, rig.Clock);          // the sim stopped sending
        Assert.Equal(frames + 1, rig.MotionCount);
        Assert.True(rig.Boolean("session.paused"));

        rig.Source.OnSimState(pauseFlags: 0, simRunning: true);
        rig.Clock += 0.05;
        rig.Source.OnIdle(rig.Session, rig.Clock);          // running: idle sends nothing
        Assert.Equal(frames + 1, rig.MotionCount);
        rig.Send();
        Assert.False(rig.Boolean("session.paused"));
        Assert.Equal(CapnpGen.MotionObject.WHICH.BodyDynamics, rig.Motion().Objects[0].which);
    }

    [Fact]
    public void Menu_Camera_State_Counts_As_Paused()
    {
        var rig = Level();
        rig.Set("CAMERA STATE", 12);
        rig.Send();
        Assert.True(rig.Boolean("session.paused"));
    }

    [Fact]
    public void Aircraft_Redeclares_Engines_As_Drive_Points_And_Channels()
    {
        var rig = Level();
        rig.Aircraft("Test Jet", engines: 2, engineType: 1, eye: new Vector3(0, 1.2f, 3f));

        var obj = rig.Discovery().Objects[0];
        Assert.Equal("Test Jet", obj.TypeLabel);
        Assert.Equal(CapnpGen.ObjectType.aircraft, obj.Type);
        Assert.Equal("pilot", obj.NamedPoints[0].Name);
        AssertEx.ApproxEqual(3f, obj.NamedPoints[0].Position.Z, 1e-6f);
        var jets = obj.DrivePoints.Where(d => d.which == CapnpGen.DrivePointDescriptor.WHICH.Jet).ToList();
        Assert.Equal(new[] { "engine[1].jet", "engine[2].jet" }, jets.Select(j => j.Name));
        Assert.Contains(obj.Channels, c => c.Name == "engine[2].n1");
        Assert.DoesNotContain(obj.Channels, c => c.Name == "engine[3].n1");

        rig.Set("GENERAL ENG THROTTLE LEVER POSITION:2", 0.8);
        rig.Set("TURB ENG N1:1", 0.9);
        rig.Send();
        var m = rig.Motion().Objects[0];
        var jet2 = m.DrivePoints.First(d => d.Id == jets[1].Id);
        AssertEx.ApproxEqual(0.8f, jet2.Jet.Throttle, 1e-6f);
        AssertEx.ApproxEqual(0.9f, rig.Number("engine[1].n1"), 1e-6f);

        int discoveries = rig.Sent.Count(x => x.type == FrameType.Discovery);
        rig.Aircraft("Test Jet", engines: 2, engineType: 1, eye: new Vector3(0, 1.2f, 3f));
        Assert.Equal(discoveries, rig.Sent.Count(x => x.type == FrameType.Discovery)); // unchanged: no re-send
    }

    [Fact]
    public void Piston_Engines_Are_Propellers()
    {
        var rig = Level();
        rig.Aircraft("Test Single", engines: 1, engineType: 0);
        rig.Set("PROP RPM:1", 2400);
        rig.Send();

        var prop = rig.Discovery().Objects[0].DrivePoints.Single(
            d => d.which == CapnpGen.DrivePointDescriptor.WHICH.Propeller);
        var state = rig.Motion().Objects[0].DrivePoints.First(d => d.Id == prop.Id);
        AssertEx.ApproxEqual(2400f, state.Propeller.Rpm, 1e-3f);
    }

    [Fact]
    public void Wheels_Carry_Rpm_Contact_And_Steer()
    {
        var rig = Level();
        rig.Set("CENTER WHEEL RPM", 300);
        rig.Set("CONTACT POINT IS ON GROUND:0", 1);
        rig.Set("SURFACE TYPE", 1);                         // grass
        rig.Set("GEAR CENTER STEER ANGLE", 0.1);
        rig.Send();

        var center = rig.Motion().Objects[0].DrivePoints[0];
        AssertEx.ApproxEqual(300f, center.Wheel.Rpm, 1e-3f);
        Assert.Equal(CapnpGen.ContactMedium.grass, center.Wheel.Contact);
        AssertEx.ApproxEqual(0.1f, center.Wheel.Extended.SteerAngle, 1e-6f);
        Assert.Equal(CapnpGen.ContactMedium.air, rig.Motion().Objects[0].DrivePoints[1].Wheel.Contact);
    }

    [Fact]
    public void Touchdown_Fires_Once_On_The_Ground_Contact_Edge()
    {
        var rig = Level();
        rig.Set("VERTICAL SPEED", -2.5);
        rig.Send();
        Assert.False(rig.Boolean("impact.touchdown"));

        rig.Set("SIM ON GROUND", 1);
        rig.Send();
        Assert.True(rig.Boolean("impact.touchdown"));
        AssertEx.ApproxEqual(-2.5f, rig.Number("impact.verticalSpeed"), 1e-6f);

        rig.Send();
        Assert.False(rig.Boolean("impact.touchdown"));
    }

    [Fact]
    public void Rejected_Variables_Read_NaN_And_Are_Skipped()
    {
        var rig = Level();
        rig.Set("AIRSPEED TRUE", double.NaN);
        rig.Set("PLANE PITCH DEGREES", double.NaN);
        rig.Send();
        Assert.Null(rig.Value("air.trueAirspeed"));
        Assert.True(float.IsFinite(rig.Motion().Objects[0].Orientation.W));
    }
}
}
