#nullable enable
using System;
using System.Numerics;

namespace OgsTsBridge
{
/// <summary>
/// A signed axis permutation from a game's frame to OGS (C-1: x right,
/// y up, z forward), written as the game component feeding each OGS axis:
/// "z,y,-x" means OGS x = game z, OGS y = game y, OGS z = −game x. For
/// formats whose frame is undocumented, sources expose it as an option so
/// a live check can fix it without code.
///
/// Vectors map by the permutation; angular velocities (axial vectors) and
/// quaternion vector parts also take the determinant, so a handedness
/// change keeps rotations right. No allocation.
/// </summary>
public readonly struct AxisMap
{
    readonly int _ax, _ay, _az;      // source component per OGS axis
    readonly float _sx, _sy, _sz;    // its sign
    readonly float _det;

    public static AxisMap Identity { get; } = Parse("x,y,z");

    AxisMap(int ax, float sx, int ay, float sy, int az, float sz)
    {
        _ax = ax; _ay = ay; _az = az;
        _sx = sx; _sy = sy; _sz = sz;
        // determinant of a signed permutation: the signs times the parity
        float parity = (ax, ay, az) switch
        {
            (0, 1, 2) or (1, 2, 0) or (2, 0, 1) => 1,
            _ => -1,
        };
        _det = sx * sy * sz * parity;
    }

    /// <summary>Parses "a,b,c" with each of x/y/z once, optionally signed.</summary>
    public static AxisMap Parse(string spec)
    {
        var parts = spec.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) throw new ArgumentException($"axes '{spec}': expected three comma-separated axes, e.g. z,y,-x");
        Span<int> axis = stackalloc int[3];
        Span<float> sign = stackalloc float[3];
        int seen = 0;
        for (int i = 0; i < 3; i++)
        {
            var p = parts[i];
            sign[i] = 1;
            if (p.StartsWith('-')) { sign[i] = -1; p = p[1..]; }
            else if (p.StartsWith('+')) p = p[1..];
            axis[i] = p switch
            {
                "x" or "X" => 0,
                "y" or "Y" => 1,
                "z" or "Z" => 2,
                _ => throw new ArgumentException($"axes '{spec}': '{parts[i]}' is not x, y or z"),
            };
            seen |= 1 << axis[i];
        }
        if (seen != 7) throw new ArgumentException($"axes '{spec}': each of x, y and z must appear once");
        return new AxisMap(axis[0], sign[0], axis[1], sign[1], axis[2], sign[2]);
    }

    static float C(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    /// <summary>A position, velocity or acceleration.</summary>
    public Vector3 Vector(Vector3 v) => new(_sx * C(v, _ax), _sy * C(v, _ay), _sz * C(v, _az));

    /// <summary>An angular velocity (axial vector).</summary>
    public Vector3 Angular(Vector3 w) => _det * Vector(w);

    /// <summary>An orientation quaternion expressed in the game frame.</summary>
    public Quaternion Rotation(Quaternion q)
    {
        var v = Angular(new Vector3(q.X, q.Y, q.Z));
        return new Quaternion(v, q.W);
    }
}
}
