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
/// <summary>The poll transport: the forwarder's connect/reconnect loop and the local HTTP client.</summary>
public class PollTransportTests
{
    sealed class FakePolled : IPolledSource
    {
        public int Connects, Reads, Translated, Disconnects;
        public string Id => "poll-test";
        public string Title => "test";
        public string Description => "test";
        public int DefaultPort => 0;
        public IReadOnlyList<string> ProcessNames => Array.Empty<string>();
        public IReadOnlyList<LegacyGame> Games => Array.Empty<LegacyGame>();
        public IReadOnlyList<(string Key, string Help)> Options => Array.Empty<(string, string)>();
        public void Configure(IReadOnlyDictionary<string, string> options) { }
        public void Declare(OgsSession session) => session.DeclareObject("x", Ogs.Telemetry.Schema.ObjectType.Other);
        public bool AcceptsDatagram(int length) => true;
        public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
        {
            Assert.Equal(42, d[0]);
            Interlocked.Increment(ref Translated);
            session.Tick(now);
        }
        public SourceTransport Transport => SourceTransport.Poll;
        public int PollIntervalMs => 1;
        public int PollBufferSize => 8;
        public string PollTarget => "the fake";
        public bool Connect() => Interlocked.Increment(ref Connects) > 1; // first attempt fails
        public void Disconnect() => Interlocked.Increment(ref Disconnects);
        public int Read(byte[] buffer)
        {
            int n = Interlocked.Increment(ref Reads);
            if (n == 3) return -1;           // the game went away once
            if (n % 2 == 0) return 0;        // nothing new
            buffer[0] = 42;
            return 1;
        }
    }

    [Fact]
    public void PollForwarder_Retries_Connects_And_Reconnects()
    {
        var src = new FakePolled();
        using var session = new OgsSession((t, d) => { }, "poll");
        src.Declare(session);
        var fwd = (PollForwarder)Forwarders.Create(src, session, () => throw new InvalidOperationException(), out var input);
        Assert.Null(input);
        Assert.Equal("polling the fake", Forwarders.DescribeInput(src));
        using var cts = new CancellationTokenSource();
        var thread = new Thread(() => fwd.Run(cts.Token)) { IsBackground = true };
        thread.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (src.Translated < 3 && DateTime.UtcNow < deadline) Thread.Sleep(5);
        cts.Cancel();
        thread.Join(2000);
        Assert.True(src.Translated >= 3, $"translated {src.Translated}");
        Assert.True(src.Connects >= 3, $"connects {src.Connects}");   // failed, ok, then after the drop
        Assert.True(src.Disconnects >= 1);
    }

    [Fact]
    public void LocalHttp_Reads_Length_And_Chunked_Bodies_On_One_Connection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        int connections = 0;
        var server = new Thread(() =>
        {
            using var client = listener.AcceptTcpClient();
            Interlocked.Increment(ref connections);
            var stream = client.GetStream();
            var buf = new byte[4096];
            for (int r = 0; r < 2; r++)
            {
                int got = 0;
                while (Encoding.ASCII.GetString(buf, 0, got).IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                    got += stream.Read(buf, got, buf.Length - got);
                var reply = r == 0
                    ? "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 11\r\n\r\n{\"a\": 1.5}\n"
                    : "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\n{\"b\":\r\n4\r\n 2}\n\r\n0\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(reply);
                stream.Write(bytes, 0, bytes.Length);
            }
        }) { IsBackground = true };
        server.Start();
        try
        {
            using var http = new LocalHttp(endpoint);
            var body = new byte[256];
            int n = http.Get(LocalHttp.Request("/state", endpoint), body);
            Assert.Equal("{\"a\": 1.5}\n", Encoding.ASCII.GetString(body, 0, n));
            n = http.Get(LocalHttp.Request("/indicators", endpoint), body);
            Assert.Equal("{\"b\": 2}\n", Encoding.ASCII.GetString(body, 0, n));
            Assert.Equal(1, connections); // keep-alive
        }
        finally { listener.Stop(); server.Join(2000); }
    }

    [Fact]
    public void LocalHttp_Reports_A_Missing_Server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();   // nothing listens there now
        using var http = new LocalHttp(endpoint);
        Assert.Equal(-1, http.Get(LocalHttp.Request("/state", endpoint), new byte[16]));
    }
}
}
