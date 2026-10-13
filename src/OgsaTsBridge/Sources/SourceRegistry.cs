#nullable enable
using System;
using System.Collections.Generic;
using OgsaTsBridge.Sources;

namespace OgsaTsBridge
{
public static class SourceRegistry
{
    public static IReadOnlyList<ILegacySource> All() => new ILegacySource[]
    {
        new BeamNgSource(),
        new ForzaSledSource(),
    };

    public static ILegacySource Create(string id)
    {
        foreach (var s in All())
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return Activator.CreateInstance(s.GetType()) as ILegacySource
                       ?? throw new InvalidOperationException($"cannot create source {id}");
        throw new ArgumentException(
            $"unknown source '{id}' — run `ogsa-ts-bridge list` for the catalogue");
    }
}
}
