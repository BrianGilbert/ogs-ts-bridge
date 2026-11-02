#nullable enable
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>Binary encoding of one telemetry field.</summary>
public enum FieldKind : byte
{
    F32, F64, I32, U32, I16, U16, I8, U8,
    /// <summary>Boolean channel from a non-zero 32-bit int.</summary>
    Bool32,
    /// <summary>Boolean channel from a non-zero byte.</summary>
    Bool8,
}

/// <summary>
/// One fixed-offset field published as a channel: value = raw × Scale +
/// Bias. A field whose raw value equals <see cref="NotAvailable"/> (the
/// format's "N/A" sentinel, e.g. R3E's −1) is left unset that tick, so a
/// changed-only session never sends it.
/// </summary>
public readonly record struct PageField(
    string Name, string Unit, string Description, int Offset, FieldKind Kind,
    double Scale = 1, double Bias = 0, double NotAvailable = double.NaN)
{
    public bool IsBoolean => Kind is FieldKind.Bool32 or FieldKind.Bool8;
}

/// <summary>
/// Table-driven channel mapping for fixed-layout pages and packets: the
/// table declares every field once, Write reads them all with offset
/// readers (no allocation) on the forwarder thread. Sources keep the
/// fields that need logic (pose, dynamics, wheels) hand-written.
/// </summary>
public sealed class PageChannels
{
    readonly PageField[] _fields;
    readonly OgsChannel[] _channels;
    readonly bool _bigEndian;

    public PageChannels(PageField[] fields, bool bigEndian = false)
    {
        _fields = fields;
        _channels = new OgsChannel[fields.Length];
        _bigEndian = bigEndian;
    }

    public int Count => _fields.Length;

    /// <summary>Bytes a page must hold for every field (with
    /// <paramref name="baseOffset"/> = 0).</summary>
    public int RequiredLength
    {
        get
        {
            int max = 0;
            foreach (var f in _fields) max = Math.Max(max, f.Offset + Size(f.Kind));
            return max;
        }
    }

    public void Declare(OgsSession session)
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            var f = _fields[i];
            _channels[i] = f.IsBoolean
                ? session.AddBooleanChannel(f.Name, f.Description)
                : session.AddChannel(f.Name, f.Unit, f.Description);
        }
    }

    /// <summary>Reads every field at <paramref name="baseOffset"/> + its
    /// offset; fields beyond the span are skipped.</summary>
    public void Write(ReadOnlySpan<byte> d, OgsSession session, int baseOffset = 0)
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            var f = _fields[i];
            int at = baseOffset + f.Offset;
            if (at + Size(f.Kind) > d.Length) continue;
            double raw = Read(d.Slice(at), f.Kind);
            if (raw.Equals(f.NotAvailable)) continue;
            if (f.IsBoolean) session.SetChannel(_channels[i], raw != 0);
            else session.SetChannel(_channels[i], raw * f.Scale + f.Bias);
        }
    }

    static int Size(FieldKind k) => k switch
    {
        FieldKind.F64 => 8,
        FieldKind.F32 or FieldKind.I32 or FieldKind.U32 or FieldKind.Bool32 => 4,
        FieldKind.I16 or FieldKind.U16 => 2,
        _ => 1,
    };

    double Read(ReadOnlySpan<byte> d, FieldKind k)
    {
        if (_bigEndian)
            return k switch
            {
                FieldKind.F32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(d)),
                FieldKind.F64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(d)),
                FieldKind.I32 or FieldKind.Bool32 => BinaryPrimitives.ReadInt32BigEndian(d),
                FieldKind.U32 => BinaryPrimitives.ReadUInt32BigEndian(d),
                FieldKind.I16 => BinaryPrimitives.ReadInt16BigEndian(d),
                FieldKind.U16 => BinaryPrimitives.ReadUInt16BigEndian(d),
                FieldKind.I8 => (sbyte)d[0],
                _ => d[0],
            };
        return k switch
        {
            FieldKind.F32 => MemoryMarshal.Read<float>(d),
            FieldKind.F64 => MemoryMarshal.Read<double>(d),
            FieldKind.I32 or FieldKind.Bool32 => MemoryMarshal.Read<int>(d),
            FieldKind.U32 => MemoryMarshal.Read<uint>(d),
            FieldKind.I16 => MemoryMarshal.Read<short>(d),
            FieldKind.U16 => MemoryMarshal.Read<ushort>(d),
            FieldKind.I8 => (sbyte)d[0],
            _ => d[0],
        };
    }
}
}
