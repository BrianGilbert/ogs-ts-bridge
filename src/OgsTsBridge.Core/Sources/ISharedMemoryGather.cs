#nullable enable
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Optional capability for shared-memory sources whose data is too large
/// to snapshot as a prefix, or spans several maps (e.g. a per-vehicle
/// telemetry array plus a scoring page naming the player's slot).
///
/// Instead of the forwarder copying the first SharedMemorySize bytes, it
/// opens the primary map whole, opens each auxiliary map when present,
/// and calls <see cref="Gather"/> to copy just the bytes Translate needs
/// into the snapshot buffer. Gather runs on the forwarder thread: read
/// with the accessors' Read/ReadArray, no allocation.
/// </summary>
public interface ISharedMemoryGather
{
    /// <summary>Maps opened alongside the primary one, in this order.
    /// Missing maps are passed as null and retried.</summary>
    IReadOnlyList<string> AuxiliaryMapNames { get; }

    /// <summary>Copies what Translate needs into <paramref name="buffer"/>
    /// (SharedMemorySize bytes) and returns the byte count, or 0 when there
    /// is nothing new to translate.</summary>
    int Gather(MemoryMappedViewAccessor primary,
               IReadOnlyList<MemoryMappedViewAccessor?> auxiliary, byte[] buffer);
}
}
