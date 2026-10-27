#nullable enable
using System.Collections.Generic;
using System.Numerics;

namespace OgsTsBridge.Sources
{
/// <summary>How a SimConnect variable is requested.</summary>
public enum SimVarKind
{
    /// <summary>FLOAT64 in the given unit.</summary>
    Number,
    /// <summary>STRING256 (unit ignored).</summary>
    Text,
    /// <summary>SIMCONNECT_DATA_XYZ, three FLOAT64 in the given unit.</summary>
    Xyz,
}

/// <summary>One simulation variable: SimConnect name (with any ":index"),
/// the unit SimConnect converts it to, and how it is requested.</summary>
public sealed record SimVar(string Name, string Unit, SimVarKind Kind = SimVarKind.Number);

/// <summary>Cold-path snapshot of a SimConnect data block, one slot per
/// declared variable. A variable the sim rejected (unknown name or unit)
/// reads NaN / null.</summary>
public sealed class SimVarValues
{
    public SimVarValues(int count)
    {
        Numbers = new double[count];
        Texts = new string?[count];
        Vectors = new Vector3?[count];
        System.Array.Fill(Numbers, double.NaN);
    }

    public double[] Numbers { get; }
    public string?[] Texts { get; }
    public Vector3?[] Vectors { get; }
}

/// <summary>
/// A source fed by SimConnect rather than datagrams. The forwarder
/// registers <see cref="FrameVars"/> (all <see cref="SimVarKind.Number"/>)
/// per sim frame and hands each block to <see cref="ILegacySource.Translate"/>
/// as packed little-endian doubles in declaration order — NaN where the
/// sim rejected a variable. <see cref="AircraftVars"/> are requested once a
/// second, on change, and arrive through <see cref="OnAircraft"/>.
/// </summary>
public interface ISimConnectSource : ILegacySource
{
    IReadOnlyList<SimVar> FrameVars { get; }
    IReadOnlyList<SimVar> AircraftVars { get; }

    /// <summary>Explicit SimConnect.dll path (file or folder), or null to
    /// use the standard search (<c>SimConnectLocator</c>).</summary>
    string? LibraryPath { get; }

    /// <summary>Aircraft identity / geometry changed (or first arrived).
    /// May redeclare the session. Forwarder thread.</summary>
    void OnAircraft(SimVarValues aircraft, OgsSession session, double now);

    /// <summary>Sim state from system events: the Pause_EX1 flags (0 =
    /// running) and the Sim event (false = no flight running).</summary>
    void OnSimState(uint pauseFlags, bool simRunning);

    /// <summary>No sim-frame data arrived this wait (paused sims may stop
    /// sending) — keep the stream alive per CONVENTIONS C-11.</summary>
    void OnIdle(OgsSession session, double now);
}
}
