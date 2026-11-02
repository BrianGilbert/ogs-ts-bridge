#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ogs.Telemetry;
using Xunit;

namespace OgsTsBridge.Tests
{
/// <summary>OgsSession under the datagram budget (implementation-udp.md §
/// Datagram budget): paged discovery and its pacing, generations, motion
/// parts and the bounded refresh — read back the way a receiver does.</summary>
public class SessionPagingTests
{
    sealed class Rig
    {
        public readonly List<(FrameType type, byte[] datagram)> Sent = new();
        public readonly OgsSession Session;
        public readonly OgsChannel[] Channels;
        public readonly OgsChannel Track;

        public Rig(int channels, bool changedOnly = true)
        {
            Session = new OgsSession((t, d) => Sent.Add((t, d)), "paging-rig");
            Session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "first label");
            if (changedOnly) Session.SendChangedChannelsOnly();
            Channels = new OgsChannel[channels];
            for (int i = 0; i < channels; i++)
                Channels[i] = Session.AddChannel($"sim.channel{i:D3}", "m/s",
                    "A simulator channel with a realistic description");
            Track = Session.AddSessionText("session.track", "Track name");
        }

        public void Clear() => Sent.Clear();
        public int Discoveries => Sent.Count(x => x.type == FrameType.Discovery);
        public int MotionDatagrams => Sent.Count(x => x.type == FrameType.Motion);
        public int Largest => Sent.Max(x => x.datagram.Length);

        public DescriptorTables Tables()
        {
            var tables = new DescriptorTables();
            foreach (var page in SentFrames.Pages(Sent)) tables.Apply(page);
            return tables;
        }
    }

    [Fact]
    public void Large_Set_Pages_Within_Budget_And_Merges_Back()
    {
        var rig = new Rig(300);
        rig.Session.Start(0.0);
        int pages = rig.Session.DiscoveryPageCount;
        Assert.True(pages > 10, $"{pages} pages");
        Assert.True(rig.Largest <= 1400, $"{rig.Largest}-byte datagram");

        var gen = rig.Tables().Current!;
        Assert.True(gen.Complete);
        var car = gen.Objects["car"];
        Assert.Equal(301, car.Channels.Count);           // 300 streamed + the session text
        Assert.Equal("first label", car.TypeLabel);
        Assert.True(car.Primary);
    }

    [Fact]
    public void Periodic_Pages_Are_Spread_Across_The_Interval()
    {
        var rig = new Rig(150);
        rig.Session.Start(0.0);
        int pages = rig.Session.DiscoveryPageCount;
        Assert.True(pages > 3);
        Assert.Equal(2 * pages, rig.Discoveries);        // Start: every page, twice (warm-up)

        rig.Clear();
        rig.Session.Tick(1.0);
        Assert.Equal(0, rig.Discoveries);                // inside the interval
        rig.Session.Tick(2.0);
        Assert.Equal(1, rig.Discoveries);                // new cycle: page 0 only
        rig.Session.Tick(2.0 + 2.0 / pages * 2.5);
        Assert.Equal(3, rig.Discoveries);                // spread: pages 1 and 2 now due
    }

    [Fact]
    public void Text_Change_Resends_Only_The_Values_Page_Under_The_Same_Generation()
    {
        var rig = new Rig(150);
        rig.Session.Start(0.0);
        rig.Clear();
        rig.Session.SetSessionText(rig.Track, Encoding.UTF8.GetBytes("Spa"));
        rig.Session.Tick(0.1);
        var sent = SentFrames.Pages(rig.Sent);
        Assert.Single(sent);                             // the values page alone
        Assert.Equal(0u, sent[0].Generation);
        Assert.Equal("Spa", sent[0].Objects[0].Values.Single().Text);
    }

    [Fact]
    public void Identity_Change_Starts_A_New_Generation_And_Resends_Every_Page()
    {
        var rig = new Rig(150);
        rig.Session.Start(0.0);
        int pages = rig.Session.DiscoveryPageCount;
        rig.Clear();
        rig.Session.SetTypeLabel(Encoding.UTF8.GetBytes("second label"));
        rig.Session.Tick(0.1);
        Assert.Equal(1u, rig.Session.Generation);
        Assert.Equal(pages, rig.Discoveries);
        var tables = rig.Tables();
        Assert.Equal(1u, tables.Current!.Number);
        Assert.Equal("second label", tables.Current.Objects["car"].TypeLabel);
        Assert.All(SentFrames.Tick(rig.Sent), m => Assert.Equal(1u, m.Generation));
    }

    [Fact]
    public void Redeclare_Starts_A_New_Generation()
    {
        var rig = new Rig(4);
        rig.Session.Start(0.0);
        rig.Session.BeginRedeclare();
        rig.Session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "swapped");
        rig.Session.AddChannel("engine.rpm", "rpm");
        rig.Session.CommitRedeclare(0.5);
        Assert.Equal(1u, rig.Session.Generation);
        Assert.Equal("swapped", rig.Tables().Current!.Objects["car"].TypeLabel);
    }

    [Fact]
    public void Full_Tick_Goes_Out_In_Parts_With_The_Pose_In_Part_0()
    {
        var rig = new Rig(300, changedOnly: false);
        rig.Session.Start(0.0);
        rig.Session.SetPose(new System.Numerics.Vector3(1, 2, 3), System.Numerics.Quaternion.Identity);
        for (int i = 0; i < 300; i++) rig.Session.SetChannel(rig.Channels[i], i);
        rig.Clear();
        rig.Session.Tick(0.1);

        var tick = SentFrames.Tick(rig.Sent);
        Assert.True(tick.Count > 1, $"{tick.Count} parts");
        Assert.True(rig.Largest <= 1400, $"{rig.Largest}-byte datagram");
        Assert.All(tick, m => Assert.Equal(tick.Count, m.PartCount));
        Assert.Equal(1f, tick[0].Objects[0].Position.X);
        var values = SentFrames.TickValues(tick).ToDictionary(v => v.Id, v => v.Number);
        Assert.Equal(300, values.Count);
        Assert.Equal(299f, values[(ushort)rig.Channels[299].Id]);
    }

    [Fact]
    public void Bounded_Refresh_Resends_Every_Value_Within_The_Interval()
    {
        var rig = new Rig(300);
        for (int i = 0; i < 300; i++) rig.Session.SetChannel(rig.Channels[i], i);
        rig.Session.Start(0.0);
        rig.Session.Tick(0.01);                          // first send: every value
        rig.Clear();

        // nothing changes for one interval at 60 Hz
        var seen = new HashSet<ushort>();
        for (double t = 0.02; t < 2.0; t += 1.0 / 60)
        {
            rig.Session.Tick(t);
            foreach (var v in SentFrames.TickValues(SentFrames.Tick(rig.Sent))) seen.Add(v.Id);
            Assert.True(rig.Largest <= 1400);
        }
        Assert.Equal(300, seen.Count);                   // every value refreshed, none changed
    }
}
}
