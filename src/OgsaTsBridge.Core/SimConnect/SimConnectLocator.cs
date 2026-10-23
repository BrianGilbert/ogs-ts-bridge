#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace OgsaTsBridge.SimConnect
{
/// <summary>
/// Finds the SimConnect client library. Candidates, first hit wins:
/// an explicit path (the <c>simconnect</c> source option), the copy that
/// ships next to the Bridge, the folder of a running sim process (MSFS
/// 2020 installs one beside FlightSimulator.exe), then the MSFS SDKs.
/// </summary>
public static class SimConnectLocator
{
    public const string FileName = "SimConnect.dll";

    public static IEnumerable<string> Candidates(string? explicitPath, IEnumerable<string> processNames)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return Directory.Exists(explicitPath)
                ? Path.Combine(explicitPath, FileName) : explicitPath;

        yield return Path.Combine(AppContext.BaseDirectory, FileName);

        foreach (var name in processNames)
            foreach (var dir in ProcessDirectories(name))
                yield return Path.Combine(dir, FileName);

        foreach (var variable in new[] { "MSFS2024_SDK", "MSFS_SDK" })
        {
            var sdk = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(sdk))
                yield return Path.Combine(sdk, "SimConnect SDK", "lib", FileName);
        }
    }

    /// <summary>The first candidate that exists, or null.</summary>
    public static string? Find(string? explicitPath, IEnumerable<string> processNames)
    {
        foreach (var path in Candidates(explicitPath, processNames))
            if (File.Exists(path)) return path;
        return null;
    }

    static List<string> ProcessDirectories(string processName)
    {
        var dirs = new List<string>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (file is not null && Path.GetDirectoryName(file) is { } dir) dirs.Add(dir);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // packaged (Store) installs deny module access — other candidates apply
            }
            finally { p.Dispose(); }
        }
        return dirs;
    }
}
}
