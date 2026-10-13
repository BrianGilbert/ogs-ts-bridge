#nullable enable
using System;
using System.Numerics;

namespace OgsaTsBridge
{
/// <summary>
/// Frame math for the OGSA-TS conventions (CONVENTIONS C-1/C-2/C-4):
/// world frame left-handed, +X right, +Y up, +Z forward; orientation is a
/// unit quaternion rotating world axes into body axes; positive rotations
/// (pitch nose-up about x, yaw nose-right about y, roll right-side-down
/// about z) appear clockwise when looking along the axis.
///
/// System.Numerics quaternions are Hamilton and Vector3.Transform applies
/// the standard active rotation, so each OGSA-positive axis rotation is
/// encoded as the matching negative right-hand rotation:
///   yaw   +ψ : (0,  sin(ψ/2), 0, cos(ψ/2))
///   pitch +θ : (−sin(θ/2), 0, 0, cos(θ/2))
///   roll  +φ : (0, 0, −sin(φ/2), cos(φ/2))
/// composed intrinsically yaw→pitch→roll: q = qy ⊗ qx ⊗ qz.
/// OgsaEulerTests locks this against the C-4 definitions.
/// </summary>
public static class OgsaMath
{
    public static Quaternion EulerToQuaternion(double yawRad, double pitchRad, double rollRad)
    {
        double hy = yawRad * 0.5, hp = pitchRad * 0.5, hr = rollRad * 0.5;
        var qy = new Quaternion(0f, (float)Math.Sin(hy), 0f, (float)Math.Cos(hy));
        var qx = new Quaternion(-(float)Math.Sin(hp), 0f, 0f, (float)Math.Cos(hp));
        var qz = new Quaternion(0f, 0f, -(float)Math.Sin(hr), (float)Math.Cos(hr));
        return Quaternion.Normalize(qy * qx * qz);
    }

    /// <summary>BeamNG world axes (right-handed, Z up, X forward, Y left)
    /// to OGSA world axes (left-handed, Y up, Z forward, X right). The
    /// mapping negates one axis so the handedness flips: ogsa = (−y, z, x).
    /// Applies to any world-space vector (position, velocity, acceleration).</summary>
    public static Vector3 BeamNgWorldToOgsa(float x, float y, float z) => new(-y, z, x);
}
}
