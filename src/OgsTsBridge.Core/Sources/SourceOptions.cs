#nullable enable
using System;
using System.Collections.Generic;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Typed view over the `--set key=value` pairs from the command line.
/// Sources pull their knobs with defaults; unknown keys error so typos
/// don't silently no-op.
/// </summary>
public sealed class SourceOptions
{
    readonly IReadOnlyDictionary<string, string> _raw;

    public SourceOptions(IReadOnlyDictionary<string, string> raw) => _raw = raw;

    public static SourceOptions Empty { get; } = new(new Dictionary<string, string>());

    public void EnsureKnown(params string[] known)
    {
        foreach (var key in _raw.Keys)
        {
            var ok = false;
            foreach (var k in known) if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) ok = true;
            if (!ok)
                throw new ArgumentException(
                    $"unknown source option '{key}' (known: {string.Join(", ", known)})");
        }
    }

    public string GetString(string key, string fallback) =>
        _raw.TryGetValue(key, out var v) ? v : fallback;

    public double GetDouble(string key, double fallback) =>
        _raw.TryGetValue(key, out var v) && double.TryParse(v, out var d) ? d : fallback;

    public bool GetBool(string key, bool fallback) =>
        _raw.TryGetValue(key, out var v)
            ? v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
              v.Equals("1", StringComparison.OrdinalIgnoreCase)
            : fallback;
}
}
