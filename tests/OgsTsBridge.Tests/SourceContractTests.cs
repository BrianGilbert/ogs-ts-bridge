#nullable enable
using System.Linq;
using OgsTsBridge.Sources;
using Xunit;

namespace OgsTsBridge.Tests
{
public class SourceContractTests
{
    /// <summary>Every executable the watcher matches must have a Games
    /// entry — the GUI's game picker and active-game naming rely on it.</summary>
    [Fact]
    public void EveryWatchedProcessHasAGameEntry()
    {
        foreach (var source in SourceRegistry.All())
        {
            foreach (var process in source.ProcessNames)
            {
                Assert.True(source.Games.Any(g => g.ProcessName == process),
                    $"{source.Id}: process '{process}' has no Games entry");
            }
        }
    }

    /// <summary>A source is a format: a game that speaks a covered format
    /// joins that source's watch list instead of getting a second source,
    /// so no executable may be claimed twice (auto mode would otherwise
    /// pick whichever source happens to be listed first).</summary>
    [Fact]
    public void EveryProcessBelongsToExactlyOneSource()
    {
        var dupes = SourceRegistry.All()
            .SelectMany(s => s.ProcessNames.Select(p => (source: s.Id, process: p.ToLowerInvariant())))
            .GroupBy(x => x.process)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.source))}")
            .ToList();
        Assert.True(dupes.Count == 0, "processes claimed by several sources: " + string.Join("; ", dupes));
    }

    [Fact]
    public void SourceIdsAreUnique()
    {
        var ids = SourceRegistry.All().Select(s => s.Id.ToLowerInvariant()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>The GUI dropdown is game-per-item; display names must be
    /// unique so type-ahead resolves to exactly one game.</summary>
    [Fact]
    public void GameDisplayNamesAreUnique()
    {
        var all = SourceRegistry.All().SelectMany(s => s.Games).ToList();
        Assert.Equal(all.Count, all.Select(g => g.DisplayName).Distinct().Count());
    }
}
}
