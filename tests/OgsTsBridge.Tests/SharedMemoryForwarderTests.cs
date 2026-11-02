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

namespace OgsTsBridge.Tests
{
/// <summary>The shared-memory forwarder: alternate map names and the
/// multi-map gather path, over real named maps.</summary>
public class SharedMemoryForwarderTests
{

    sealed class AltPageSource : ILegacySource
    {
        public string Primary = "", Alternate = "";
        public int Translated;
        public string Id => "alt-page";
        public string Title => "test";
        public string Description => "test";
        public int DefaultPort => 0;
        public IReadOnlyList<string> ProcessNames => Array.Empty<string>();
        public IReadOnlyList<LegacyGame> Games => Array.Empty<LegacyGame>();
        public IReadOnlyList<(string Key, string Help)> Options => Array.Empty<(string, string)>();
        public void Configure(IReadOnlyDictionary<string, string> options) { }
        public void Declare(OgsSession session) => session.DeclareObject("x", Ogs.Telemetry.Schema.ObjectType.Other);
        public bool AcceptsDatagram(int length) => true;
        int _last = -1;
        public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
        {
            int token = MemoryMarshal.Read<int>(d);
            if (token == _last || token == 0) return;
            _last = token;
            Interlocked.Increment(ref Translated);
            session.Tick(now);
        }
        public SourceTransport Transport => SourceTransport.SharedMemory;
        public string SharedMemoryName => Primary;
        public IReadOnlyList<string> SharedMemoryAlternateNames => new[] { Alternate };
        public int SharedMemorySize => 64;
        public int PollIntervalMs => 1;
    }

    static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (condition()) return true; Thread.Sleep(5); }
        return condition();
    }

    [Fact]
    public void MmfForwarder_Opens_AlternateName()
    {
        var tag = Guid.NewGuid().ToString("N");
        var src = new AltPageSource { Primary = "ogs-test-primary-" + tag, Alternate = "ogs-test-alt-" + tag };
        using var file = MemoryMappedFile.CreateNew(src.Alternate, 64);
        using var view = file.CreateViewAccessor();
        view.Write(0, 1);

        using var session = new OgsSession((t, d) => { }, "alt");
        src.Declare(session);
        var fwd = new MmfForwarder(src, session);
        using var cts = new CancellationTokenSource();
        var thread = new Thread(() => fwd.Run(cts.Token)) { IsBackground = true };
        thread.Start();
        try
        {
            Assert.True(WaitFor(() => src.Translated >= 1), "alternate page never read");
            view.Write(0, 2);
            Assert.True(WaitFor(() => src.Translated >= 2), "page change not seen");
        }
        finally { cts.Cancel(); thread.Join(2000); }
    }

    sealed class GatherSource : ILegacySource, ISharedMemoryGather
    {
        public string Primary = "", Aux = "";
        public volatile bool SawAux;
        public int Translated;
        public string Id => "gather";
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
            Assert.Equal(8, d.Length);
            if (MemoryMarshal.Read<int>(d.Slice(4)) == 77) SawAux = true;
            Interlocked.Increment(ref Translated);
            session.Tick(now);
        }
        public SourceTransport Transport => SourceTransport.SharedMemory;
        public string SharedMemoryName => Primary;
        public int SharedMemorySize => 16;
        public int PollIntervalMs => 1;
        public IReadOnlyList<string> AuxiliaryMapNames => new[] { Aux };
        public int Gather(MemoryMappedViewAccessor primary, IReadOnlyList<MemoryMappedViewAccessor?> auxiliary, byte[] buffer)
        {
            // read deep into the primary map (beyond SharedMemorySize) to prove it is mapped whole
            int deep = primary.ReadInt32(4096);
            MemoryMarshal.Write(buffer.AsSpan(0, 4), in deep);
            int aux = auxiliary[0]?.ReadInt32(0) ?? 0;
            MemoryMarshal.Write(buffer.AsSpan(4, 4), in aux);
            return 8;
        }
    }

    [Fact]
    public void MmfForwarder_Gathers_Across_Maps()
    {
        var tag = Guid.NewGuid().ToString("N");
        var src = new GatherSource { Primary = "ogs-test-gp-" + tag, Aux = "ogs-test-ga-" + tag };
        using var primary = MemoryMappedFile.CreateNew(src.Primary, 8192);
        using var pview = primary.CreateViewAccessor();
        pview.Write(4096, 5);

        using var session = new OgsSession((t, d) => { }, "gather");
        src.Declare(session);
        var fwd = new MmfForwarder(src, session);
        using var cts = new CancellationTokenSource();
        var thread = new Thread(() => fwd.Run(cts.Token)) { IsBackground = true };
        thread.Start();
        try
        {
            Assert.True(WaitFor(() => src.Translated >= 1), "gather never translated");
            // auxiliary map appears later — the forwarder must pick it up
            using var aux = MemoryMappedFile.CreateNew(src.Aux, 64);
            using var aview = aux.CreateViewAccessor();
            aview.Write(0, 77);
            Assert.True(WaitFor(() => src.SawAux), "auxiliary map never opened");
        }
        finally { cts.Cancel(); thread.Join(2000); }
    }
}
}
