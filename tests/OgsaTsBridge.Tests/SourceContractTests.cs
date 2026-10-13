#nullable enable
using System.Linq;
using OgsaTsBridge.Sources;
using Xunit;

namespace OgsaTsBridge.Tests
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
