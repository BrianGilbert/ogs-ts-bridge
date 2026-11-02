#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Finds installed Steam games for patchers that edit a file in the game
/// folder: the Steam root from the registry (or the default path), then
/// every library listed in steamapps\libraryfolders.vdf. Cold path.
/// </summary>
public static class SteamLibrary
{
    /// <summary>Every steamapps\common folder of every library.</summary>
    public static IReadOnlyList<string> CommonFolders(string? steamRoot = null)
    {
        var roots = new List<string>();
        steamRoot ??= SteamRoot();
        if (steamRoot is null) return roots;

        var libraries = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var lib in libraries)
        {
            var common = Path.Combine(lib, "steamapps", "common");
            if (Directory.Exists(common) && !roots.Exists(r => string.Equals(r, common, StringComparison.OrdinalIgnoreCase)))
                roots.Add(common);
        }
        return roots;
    }

    /// <summary>The first existing install folder with one of the given
    /// names (steamapps\common\&lt;name&gt;), or null.</summary>
    public static string? FindGame(IEnumerable<string> folderNames, string? steamRoot = null)
    {
        foreach (var common in CommonFolders(steamRoot))
            foreach (var name in folderNames)
            {
                var dir = Path.Combine(common, name);
                if (Directory.Exists(dir)) return dir;
            }
        return null;
    }

    static string? SteamRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
                    return path.Replace('/', '\\');
            }
            catch (System.Security.SecurityException) { }
        }
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }
}
}
