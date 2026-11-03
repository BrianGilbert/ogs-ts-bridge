#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Ogs.Telemetry;

namespace OgsTsBridge.Tests
{
/// <summary>
/// Reads captured datagrams the way a receiver does under the datagram
/// budget (implementation-udp.md): discovery as the pages of the latest
/// generation (the last copy of each page), motion as the parts of one tick
/// (same timestamp). Identity, drive points and environment come from page
/// 0; pose, dynamics and drive points from part 0; channels and values from
/// every page or part.
/// </summary>
static class SentFrames
{
    static Capnp.DeserializerState Root(byte[] datagram)
    {
        Envelope.TryUnwrap(datagram, out _, out var payload);
        return Capnp.DeserializerState.CreateRoot(MessageDecoder.DecodeMessage(payload.Span));
    }

    static List<byte[]> Of(IEnumerable<(FrameType type, byte[] datagram)> sent, FrameType type)
    {
        lock (sent) return sent.Where(x => x.type == type).Select(x => x.datagram).ToList();
    }

    /// <summary>The latest generation's pages in page order.</summary>
    public static List<CapnpGen.DiscoveryFrame.READER> Pages(IEnumerable<(FrameType, byte[])> sent)
    {
        var all = Of(sent, FrameType.Discovery).Select(d => CapnpGen.DiscoveryFrame.READER.create(Root(d))).ToList();
        if (all.Count == 0) throw new InvalidOperationException("no discovery sent");
        uint generation = all[^1].Generation;
        var byPage = new SortedDictionary<int, CapnpGen.DiscoveryFrame.READER>();
        foreach (var d in all.Where(d => d.Generation == generation))
            byPage[d.PageCount > 1 ? d.Page : 0] = d;
        return byPage.Values.ToList();
    }

    public static CapnpGen.DiscoveryFrame.READER Page0(IEnumerable<(FrameType, byte[])> sent) => Pages(sent)[0];

    public static IEnumerable<CapnpGen.ChannelDescriptor.READER> Channels(IEnumerable<(FrameType, byte[])> sent) =>
        Pages(sent).SelectMany(p => p.Objects.SelectMany(o => o.Channels));

    public static IEnumerable<CapnpGen.TextValue.READER> SessionValues(IEnumerable<(FrameType, byte[])> sent) =>
        Pages(sent).SelectMany(p => p.Objects.SelectMany(o => o.Texts));

    /// <summary>The parts of the tick at <paramref name="timestamp"/>
    /// (default: the latest tick), part 0 first.</summary>
    public static List<CapnpGen.MotionFrame.READER> Tick(IEnumerable<(FrameType, byte[])> sent, double? timestamp = null)
    {
        var all = Of(sent, FrameType.Motion).Select(d => CapnpGen.MotionFrame.READER.create(Root(d))).ToList();
        if (all.Count == 0) throw new InvalidOperationException("no motion sent");
        double t = timestamp ?? all[^1].Timestamp;
        // the latest run of datagrams with that timestamp is the tick
        int end = all.FindLastIndex(m => m.Timestamp == t);
        int start = end;
        while (start > 0 && all[start - 1].Timestamp == t && all[start - 1].Part < all[start].Part) start--;
        return all.GetRange(start, end - start + 1).OrderBy(m => m.Part).ToList();
    }

    public static CapnpGen.MotionFrame.READER Part0(IEnumerable<(FrameType, byte[])> sent) => Tick(sent)[0];

    public static IEnumerable<CapnpGen.ChannelValue.READER> TickValues(IEnumerable<CapnpGen.MotionFrame.READER> tick) =>
        tick.SelectMany(m => m.Objects.SelectMany(o => o.Channels));

    /// <summary>Every datagram's size, envelope included.</summary>
    public static IEnumerable<(FrameType type, int bytes)> Sizes(IEnumerable<(FrameType type, byte[] datagram)> sent)
    {
        lock (sent) return sent.Select(x => (x.type, x.datagram.Length)).ToList();
    }
}
}
