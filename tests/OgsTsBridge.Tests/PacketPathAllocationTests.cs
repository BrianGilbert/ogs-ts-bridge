#nullable enable
using OgsTsBridge.Sources;
using Xunit;
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>The allocation probe itself (<see cref="SourceTestKit.AssertNoAllocation"/>),
/// on an existing allocation-free source; each source's tests run it on
/// their own packets.</summary>
public class PacketPathAllocationTests
{
    [Fact]
    public void Probe_Passes_An_Allocation_Free_Source() =>
        AssertNoAllocation(new Il2Source(), i =>
        {
            var b = new byte[44];
            W(b, 0, Il2Source.MotionId);
            W(b, 8, 0.001f * i); W(b, 36, 9.8f);
            return b;
        });
}
}
