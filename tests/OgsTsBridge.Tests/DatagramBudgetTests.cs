#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OgsTsBridge.Sources;
using Ogs.Telemetry;
using Xunit;
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>Every source keeps every datagram within the 1,400-byte
/// budget (implementation-udp.md § Datagram budget).</summary>
public class DatagramBudgetTests
{

    /// <summary>Every datagram, envelope included, is at most 1,400 bytes
    /// (implementation-udp.md § Datagram budget): discovery pages, a tick
    /// with every channel set goes out in parts.</summary>
    public static IEnumerable<object[]> SourceIds() =>
        SourceRegistry.All().Select(s => new object[] { s.Id });

    [Theory]
    [MemberData(nameof(SourceIds))]
    public void Every_Datagram_Fits_The_Budget(string id)
    {
        var src = SourceRegistry.Create(id);
        var cap = new Capture();
        using var session = cap.MakeSession(src.Id);
        src.Configure(new Dictionary<string, string>());
        src.Declare(session);
        // every channel set, as a fully mapped source does each tick
        for (int i = 0; i < OgsSession.MaxChannels; i++)
            session.SetChannel(new OgsChannel(i, ""), (double)float.MaxValue);
        session.Start(Now());
        session.Tick(Now() + 1.0);
        Assert.True(cap.LargestDatagram() <= 1400, $"{id}: a {cap.LargestDatagram()}-byte datagram");
    }

    [Theory]
    [InlineData("msfs")]
    [InlineData("r3e")]
    public void Large_Sources_Page_Their_Discovery(string id)
    {
        var src = SourceRegistry.Create(id);
        var (cap, session) = Start(src);
        using var _ = session;
        Assert.True(cap.DiscoveryPages() > 1, $"{id}: {cap.DiscoveryPages()} page(s)");
    }
}
}
