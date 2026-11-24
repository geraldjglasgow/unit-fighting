using System;
using Godot;

namespace UnitFighting.Sim;

/// <summary>
/// The simulation lives on the ground plane: a sim Vector2 (x, y) is world (x, 0, y).
/// An angle a points along (sin a, cos a), which is where a Node3D with rotation.y = a faces
/// if its model is built facing +Z.
/// </summary>
public static class MathUtil
{
    public static Vector2 Dir(float angle) => new(Mathf.Sin(angle), Mathf.Cos(angle));

    public static float Angle(Vector2 dir) => Mathf.Atan2(dir.X, dir.Y);

    /// <summary>The soldier's right-hand side when facing <paramref name="fwd"/>.</summary>
    public static Vector2 Right(Vector2 fwd) => new(-fwd.Y, fwd.X);

    /// <summary>The soldier's left-hand (shield) side when facing <paramref name="fwd"/>.</summary>
    public static Vector2 Left(Vector2 fwd) => new(fwd.Y, -fwd.X);

    /// <summary>Signed shortest turn from one angle to another, in (-PI, PI].</summary>
    public static float AngleDiff(float from, float to) => Mathf.PosMod(to - from + Mathf.Pi, Mathf.Tau) - Mathf.Pi;

    public static float TurnToward(float current, float target, float maxStep)
    {
        float d = AngleDiff(current, target);
        return Mathf.Abs(d) <= maxStep ? target : current + Mathf.Sign(d) * maxStep;
    }

    /// <summary>
    /// Where <paramref name="toOther"/> lies as seen by someone facing <paramref name="facing"/>:
    /// 0 is dead ahead, positive is the left (shield) side, +-PI is directly behind.
    /// </summary>
    public static float RelativeAngle(float facing, Vector2 toOther)
    {
        var f = Dir(facing);
        return Mathf.Atan2(toOther.Dot(Left(f)), toOther.Dot(f));
    }

    public static Vector2 ToLocal(Vector2 v, float facing)
    {
        var f = Dir(facing);
        return new Vector2(v.Dot(Right(f)), v.Dot(f));
    }

    public static Vector2 FromLocal(Vector2 local, float facing)
    {
        var f = Dir(facing);
        return Right(f) * local.X + f * local.Y;
    }

    public static float Gaussian(Random rng, float mean, float sd)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return mean + sd * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}
