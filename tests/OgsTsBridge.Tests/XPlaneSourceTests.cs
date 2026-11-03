#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OgsTsBridge.Sources;
using Ogs.Telemetry;
using Xunit;
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>Forged-input translation tests: X-Plane RREF dataref replies.</summary>
public class XPlaneSourceTests
{
    const double Deg = Math.PI / 180;

    /// <summary>An RREF reply: "RREF," then (index, value) pairs.</summary>
    static byte[] Reply(XPlaneSource src, params (string Path, float Value)[] values)
    {
        var d = new List<byte>(Encoding.ASCII.GetBytes("RREF,"));
        foreach (var (path, value) in values)
        {
            int index = src.IndexOf(path);
            Assert.True(index >= 0, $"not subscribed: {path}");
            d.AddRange(BitConverter.GetBytes(index));
            d.AddRange(BitConverter.GetBytes(value));
        }
        return d.ToArray();
    }

    [Fact]
    public void Requests_Subscribe_Every_Dataref_And_Stop_Cancels()
    {
        var src = new XPlaneSource();
        Assert.Equal(src.SubscriptionCount, src.RequestPayloads.Count);
        Assert.Equal(src.SubscriptionCount, src.StopPayloads.Count);
        var req = src.RequestPayloads[0];
        Assert.Equal(413, req.Length);
        Assert.Equal("RREF\0", Encoding.ASCII.GetString(req, 0, 5));
        Assert.Equal(60, BitConverter.ToInt32(req, 5));          // Hz
        Assert.Equal(0, BitConverter.ToInt32(req, 9));           // index
        Assert.Equal(src.Dataref(0), Encoding.ASCII.GetString(req, 13, 400).TrimEnd('\0'));
        Assert.Equal(0, BitConverter.ToInt32(src.StopPayloads[0], 5)); // frequency 0 = stop
        Assert.Equal("127.0.0.1:49000", src.RequestTarget!.ToString());

        src.Configure(new Dictionary<string, string> { ["host"] = "192.168.1.5" });
        Assert.Equal("192.168.1.5:49000", src.RequestTarget!.ToString());
        // a datagram that is not a whole number of (index, value) pairs is refused
        Assert.False(src.AcceptsDatagram(5 + 7));
        Assert.True(src.AcceptsDatagram(5 + 16));
    }

    [Fact]
    public void Translates_Pose_Dynamics_And_Channels()
    {
        var src = new XPlaneSource();
        var (cap, session) = Start(src);
        using var _ = session;

        var pkt = Reply(src,
            ("sim/flightmodel/position/local_x", 100f),
            ("sim/flightmodel/position/local_y", 50f),
            ("sim/flightmodel/position/local_z", 200f),
            ("sim/flightmodel/position/psi", 90f),
            ("sim/flightmodel/position/theta", 5f),
            ("sim/flightmodel/position/phi", 10f),
            ("sim/flightmodel/position/Prad", 0.02f),
            ("sim/flightmodel/position/Qrad", 0.01f),
            ("sim/flightmodel/position/Rrad", 0.03f),
            ("sim/flightmodel/forces/g_side", -0.1f),
            ("sim/flightmodel/forces/g_nrml", 1.1f),
            ("sim/flightmodel/forces/g_axil", 0.2f),
            ("sim/flightmodel/position/indicated_airspeed", 100f),
            ("sim/flightmodel/position/groundspeed", 61.7f),
            ("sim/flightmodel/position/latitude", 47.5f),
            ("sim/cockpit2/radios/actuators/com1_frequency_hz_833", 121500f),
            ("sim/cockpit2/switches/landing_lights_on", 1f),
            ("sim/cockpit2/controls/left_brake_ratio", 0.25f),
            ("sim/cockpit2/controls/right_brake_ratio", 0.5f));
        Assert.True(src.AcceptsDatagram(pkt.Length));
        src.Translate(pkt, session, Now());

        var m = cap.LastMotion();
        var obj = m.Objects[0];
        // OpenGL (x east, y up, z south) → OGS-TS (x, y, −z)
        Assert.Equal(100f, obj.Position.X);
        Assert.Equal(50f, obj.Position.Y);
        Assert.Equal(-200f, obj.Position.Z);
        AssertQuat(OgsMath.EulerToQuaternion(90 * Deg, 5 * Deg, 10 * Deg), m);
        AssertEx.ApproxEqual(-0.1f * 9.80665f, obj.BodyDynamics.SpecificForce.X, 1e-4f);
        AssertEx.ApproxEqual(1.1f * 9.80665f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual(0.2f * 9.80665f, obj.BodyDynamics.SpecificForce.Z, 1e-4f);
        AssertEx.ApproxEqual(0.01f, obj.BodyDynamics.AngularVelocity.X, 1e-6f); // Q: pitch
        AssertEx.ApproxEqual(0.03f, obj.BodyDynamics.AngularVelocity.Y, 1e-6f); // R: yaw
        AssertEx.ApproxEqual(0.02f, obj.BodyDynamics.AngularVelocity.Z, 1e-6f); // P: roll
        AssertEx.ApproxEqual(100f * 0.514444f, cap.Channel(m, "air.indicatedAirspeed"), 1e-3f);
        AssertEx.ApproxEqual(61.7f, cap.Channel(m, "vehicle.speed"), 1e-3f);
        AssertEx.ApproxEqual((float)(47.5 * Deg), cap.Channel(m, "position.lat"), 1e-6f);
        AssertEx.ApproxEqual(121_500_000f, cap.Channel(m, "nav.com1.active"), 1f);
        Assert.Equal(1f, cap.Channel(m, "light.landing"));
        AssertEx.ApproxEqual(0.5f, cap.Channel(m, "brakes.pedal"), 1e-6f);

        // declared value types ride discovery
        var types = SentFrames.Channels(cap.Sent).ToDictionary(c => c.Name, c => c.Type);
        Assert.Equal(CapnpGen.ChannelType.boolean, types["session.paused"]);
        Assert.Equal(CapnpGen.ChannelType.number, types["engine.rpm"]);
    }

    [Fact]
    public void Paused_Sends_No_Dynamics()
    {
        var src = new XPlaneSource();
        var (cap, session) = Start(src);
        using var _ = session;
        src.Translate(Reply(src, ("sim/time/paused", 1f), ("sim/flightmodel/forces/g_nrml", 1f)), session, Now());
        var m = cap.LastMotion();
        Assert.Equal(CapnpGen.MotionObject.WHICH.NoDynamics, m.Objects[0].which);
        Assert.Equal(1f, cap.Channel(m, "session.paused"));
    }

    [Fact]
    public void Aircraft_Layout_Redeclares_Wheels_And_Engines()
    {
        var src = new XPlaneSource();
        var (cap, session) = Start(src);
        using var _ = session;
        Assert.Empty(cap.Discovery().Objects[0].DrivePoints);

        // a twin turboprop on tricycle gear: every configuration dataref
        var config = new List<(string, float)>
        {
            ("sim/aircraft/engine/acf_num_engines", 2f),
            ("sim/aircraft/weight/acf_m_fuel_tot", 1000f),
        };
        for (int e = 0; e < XPlaneSource.MaxEngines; e++)
        {
            config.Add(($"sim/aircraft/prop/acf_en_type[{e}]", 9f));
            config.Add(($"sim/aircraft/prop/acf_num_blades[{e}]", 4f));
            config.Add(($"sim/flightmodel2/engines/location_x_mtr[{e}]", e == 0 ? -3f : 3f));
            config.Add(($"sim/flightmodel2/engines/location_y_mtr[{e}]", 0.5f));
            config.Add(($"sim/flightmodel2/engines/location_z_mtr[{e}]", -1f));
        }
        for (int g = 0; g < XPlaneSource.MaxGear; g++)
        {
            config.Add(($"sim/aircraft/parts/acf_gear_type[{g}]", g < 3 ? 1f : 0f));
            config.Add(($"sim/flightmodel/parts/tire_x_no_deflection[{g}]", g == 1 ? -2f : g == 2 ? 2f : 0f));
            config.Add(($"sim/flightmodel/parts/tire_y_no_deflection[{g}]", -1.5f));
            config.Add(($"sim/flightmodel/parts/tire_z_no_deflection[{g}]", g == 0 ? -4f : 1f));
            config.Add(($"sim/flightmodel2/gear/tire_radius_mtrs[{g}]", 0.3f));
            config.Add(($"sim/aircraft/overflow/acf_gear_steers[{g}]", g == 0 ? 1f : 0f));
        }
        config.Add(("sim/flightmodel2/gear/on_ground[0]", 1f));
        config.Add(("sim/flightmodel/ground/surface_texture_type", 4f)); // grass
        config.Add(("sim/flightmodel2/engines/prop_rotation_speed_rad_sec[0]", (float)(2000 * 2 * Math.PI / 60)));
        config.Add(("sim/cockpit2/engine/indicators/N1_percent[1]", 85f));
        config.Add(("sim/flightmodel/weight/m_fuel_total", 226.796f));
        src.Translate(Reply(src, config.ToArray()), session, Now());

        var d = cap.Discovery();
        var points = d.Objects[0].DrivePoints;
        Assert.Equal(5, points.Count); // three wheels, two propellers
        var nose = points.Single(p => p.Name == "gear[1]");
        Assert.Equal(CapnpGen.DrivePointDescriptor.WHICH.Wheel, nose.which);
        Assert.True(nose.Wheel.Steered);
        Assert.Equal(4f, nose.Offset.Z); // aircraft z aft → OGS-TS z forward
        Assert.Equal(4, points.Single(p => p.Name == "engine[1]").Propeller.Blades);

        var m = cap.LastMotion();
        AssertEx.ApproxEqual(0.85f, cap.Channel(m, "engine[2].n1"), 1e-6f);
        AssertEx.ApproxEqual(0.5f, cap.Channel(m, "fuel.level"), 1e-3f);    // 500 lb of 1,000
        var wheel = m.Objects[0].DrivePoints.Single(p => p.Id == nose.Id).Wheel;
        Assert.Equal(CapnpGen.ContactMedium.grass, wheel.Contact);
        var prop = m.Objects[0].DrivePoints.Single(p => p.Id == points.Single(x => x.Name == "engine[1]").Id);
        AssertEx.ApproxEqual(2000f, prop.Propeller.Rpm, 1e-2f);
        Assert.DoesNotContain(SentFrames.Channels(cap.Sent), c => c.Name.StartsWith("engine[3]"));
    }
}
}
