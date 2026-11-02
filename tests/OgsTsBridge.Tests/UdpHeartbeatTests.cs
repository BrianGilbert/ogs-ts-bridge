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
/// <summary>UDP sources that keep the game streaming with heartbeats.</summary>
public class UdpHeartbeatTests
{
    sealed class HeartbeatSource : ILegacySource, IUdpHeartbeat
    {
        public IPEndPoint? HeartbeatTarget { get; init; }
        public byte[] HeartbeatPayload => "~"u8.ToArray();
        public double HeartbeatIntervalSeconds => 0.05;
        public string Id => "hb";
        public string Title => "test";
        public string Description => "test";
        public int DefaultPort => 0;
        public IReadOnlyList<string> ProcessNames => Array.Empty<string>();
        public IReadOnlyList<LegacyGame> Games => Array.Empty<LegacyGame>();
        public IReadOnlyList<(string Key, string Help)> Options => Array.Empty<(string, string)>();
        public void Configure(IReadOnlyDictionary<string, string> options) { }
        public void Declare(OgsSession session) => session.DeclareObject("x", Ogs.Telemetry.Schema.ObjectType.Other);
        public bool AcceptsDatagram(int length) => true;
        public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now) => session.Tick(now);
    }

    [Fact]
    public void Forwarder_Sends_Heartbeats_From_The_Input_Port()
    {
        using var console = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        console.Client.ReceiveTimeout = 3000;
        var src = new HeartbeatSource { HeartbeatTarget = (IPEndPoint)console.Client.LocalEndPoint! };
        using var input = new UdpInputSocket(new IPEndPoint(IPAddress.Loopback, 0));
        using var session = new OgsSession((t, d) => { }, "hb");
        src.Declare(session);
        var fwd = new Forwarder(src, input, session);
        using var cts = new CancellationTokenSource();
        var thread = new Thread(() => fwd.Run(cts.Token)) { IsBackground = true };
        thread.Start();
        try
        {
            IPEndPoint? from = null;
            var first = console.Receive(ref from);
            var second = console.Receive(ref from);
            Assert.Equal((byte)'~', first[0]);
            Assert.Equal((byte)'~', second[0]);   // repeated on the interval
        }
        finally { cts.Cancel(); input.Dispose(); thread.Join(2000); }
    }
}
}
