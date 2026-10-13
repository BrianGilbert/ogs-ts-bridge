#nullable enable
using System;
using System.Numerics;
using OgsaTsBridge.Sources;

namespace OgsaTsBridge.Tests
{
/// <summary>Forged legacy packets with known field values.</summary>
static class Fixtures
{
    public const uint TimestampMS = 123_456u;
    public const float EngineMaxRpm = 8000f;
    public const float EngineIdleRpm = 800f;
    public const float CurrentEngineRpm = 4500f;
    public const float AccX = 2.5f, AccY = -0.4f, AccZ = 6.25f;
    public const float VelX = 1.5f, VelY = 0.25f, VelZ = 24.0f;
    public const float AngVelX = 0.1f, AngVelY = -0.35f, AngVelZ = 0.02f;
    public const float Yaw = 0.5f, Pitch = -0.1f, Roll = 0.25f;
    public const float CombinedSlipFL = 0.4f;
    public const float SuspMetersFL = 0.07f;
    public const uint NumCyl = 8u;

    public static float SpeedMagnitude() =>
        MathF.Sqrt(VelX * VelX + VelY * VelY + VelZ * VelZ);

    public static byte[] ForgeForzaSled(bool raceOn = true)
    {
        var p = new ForzaSledSource.Packet
        {
            IsRaceOn = raceOn ? 1u : 0u,
            TimestampMS = TimestampMS,
            EngineMaxRpm = EngineMaxRpm,
            EngineIdleRpm = EngineIdleRpm,
            CurrentEngineRpm = CurrentEngineRpm,
            AccX = AccX, AccY = AccY, AccZ = AccZ,
            VelX = VelX, VelY = VelY, VelZ = VelZ,
            AngVelX = AngVelX, AngVelY = AngVelY, AngVelZ = AngVelZ,
            Yaw = Yaw, Pitch = Pitch, Roll = Roll,
            SuspNormFL = 0.5f, SuspNormFR = 0.5f, SuspNormRL = 0.5f, SuspNormRR = 0.5f,
            SlipRatioFL = 0.1f, SlipRatioFR = 0.11f, SlipRatioRL = 0.12f, SlipRatioRR = 0.13f,
            WheelSpeedFL = 50f, WheelSpeedFR = 50f, WheelSpeedRL = 49.9f, WheelSpeedRR = 49.9f,
            RumbleFL = 1u, RumbleFR = 0u, RumbleRL = 0u, RumbleRR = 0u,
            PuddleFL = 0f, PuddleFR = 0f, PuddleRL = 0.2f, PuddleRR = 0f,
            SurfRumbleFL = 0.6f, SurfRumbleFR = 0f, SurfRumbleRL = 0f, SurfRumbleRR = 0f,
            SlipAngleFL = 0.05f, SlipAngleFR = 0.06f, SlipAngleRL = 0.07f, SlipAngleRR = 0.08f,
            CombinedSlipFL = CombinedSlipFL, CombinedSlipFR = 0.05f,
            CombinedSlipRL = 0.06f, CombinedSlipRR = 0.07f,
            SuspMetersFL = SuspMetersFL, SuspMetersFR = 0.04f,
            SuspMetersRL = 0.03f, SuspMetersRR = 0.02f,
            CarOrdinal = 1u, CarClass = 3u, CarPerformanceIndex = 500u,
            DrivetrainType = 2u, NumCylinders = NumCyl,
        };
        var buf = new byte[ForzaSledSource.PacketSize];
        System.Runtime.InteropServices.MemoryMarshal.Write(buf.AsSpan(), in p);
        return buf;
    }

    public static byte[] ForgeBeamNg(Vector3 pos, float roll, float pitch, float yaw,
                                     Vector3 vel = default, Vector3 acc = default)
    {
        var p = new BeamNgSource.Packet
        {
            Magic = 1f,
            PosX = pos.X, PosY = pos.Y, PosZ = pos.Z,
            VelX = vel.X, VelY = vel.Y, VelZ = vel.Z,
            AccX = acc.X, AccY = acc.Y, AccZ = acc.Z,
            Roll = roll, Pitch = pitch, Yaw = yaw,
        };
        var buf = new byte[BeamNgSource.PacketSize];
        System.Runtime.InteropServices.MemoryMarshal.Write(buf.AsSpan(), in p);
        return buf;
    }
}
}
