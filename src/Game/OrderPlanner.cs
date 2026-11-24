using System;
using System.Collections.Generic;
using Godot;
using UnitFighting.Sim;

namespace UnitFighting.Game;

/// <summary>Where one battalion should go: formation centre, facing and (0 = keep) number of files.</summary>
public struct Placement
{
    public Battalion Unit;
    public Vector2 Center;
    public float Facing;
    public int Files;
}

/// <summary>
/// Turns a player's gesture into per-battalion destinations. Pure maths on sim data, so it can be tested
/// without a mouse. Total War conventions: a right-drag is the front rank of the new line; a right-click moves
/// the selection as a block, keeping its shape.
/// </summary>
public static class OrderPlanner
{
    public const float LineGap = 3f;      // metres between battalions placed by one drag
    public const float ShortMove = 6f;    // moves shorter than this keep the current facing
    public const float MinSegment = 1f;

    /// <summary>The facing a line dragged from a to b takes: the drag runs along the men's right-hand side.</summary>
    public static float LineFacing(Vector2 a, Vector2 b)
    {
        var dir = (b - a).Normalized();
        return MathUtil.Angle(new Vector2(dir.Y, -dir.X));
    }

    /// <summary>Which way a battalion is facing, or about to face if it is on the move.</summary>
    public static float IntendedFacing(Battalion u) => u.Order == OrderType.Move ? u.DestFacing : u.Facing;

    static int Men(Battalion u) => Math.Max(1, u.Formed);

    /// <summary>
    /// A drag from a to b is the front of a line. The battalions share it in equal segments with small gaps,
    /// taken in their current order along it, each facing perpendicular to it. Width sets the files.
    /// </summary>
    public static void Line(IReadOnlyList<Battalion> units, Vector2 a, Vector2 b, List<Placement> into)
    {
        into.Clear();
        var along = b - a;
        float len = along.Length();
        if (len < 1e-3f || units.Count == 0) return;
        var dir = along / len;
        var fwd = new Vector2(dir.Y, -dir.X);   // MathUtil.Right(fwd) == dir
        float facing = MathUtil.Angle(fwd);

        var sorted = new List<Battalion>(units);
        sorted.Sort((x, y) => x.Centroid.Dot(dir).CompareTo(y.Centroid.Dot(dir)));
        int n = sorted.Count;
        float seg = Math.Max(MinSegment, (len - LineGap * (n - 1)) / n);
        float total = seg * n + LineGap * (n - 1);
        var start = (a + b) * 0.5f - dir * (total * 0.5f);   // equals a unless the segments had to be widened

        for (int i = 0; i < n; i++)
        {
            var u = sorted[i];
            var mid = start + dir * (i * (seg + LineGap) + seg * 0.5f);
            int files = Formation.UsesFiles(u.Formation) ? Formation.FilesForWidth(u.Formation, seg, Men(u)) : 0;
            float depth = Formation.Extent(u.Formation, Men(u), files > 0 ? files : u.Files).Y;
            // The anchor is the formation's centre, so step back half the depth from the front line.
            into.Add(new Placement { Unit = u, Center = mid - fwd * (depth * 0.5f), Facing = facing, Files = files });
        }
    }

    /// <summary>
    /// A right-click on the ground. One battalion goes there (turning to face its way of travel unless the move
    /// is short); several move as a block, keeping their arrangement, the block rotated so that all of them face
    /// the way of travel. Short moves keep every facing and just shift the block.
    /// </summary>
    public static void Move(IReadOnlyList<Battalion> units, Vector2 dest, List<Placement> into)
    {
        into.Clear();
        if (units.Count == 0) return;
        if (units.Count == 1)
        {
            var u = units[0];
            var travel = dest - u.Anchor;
            float f = travel.Length() < ShortMove ? IntendedFacing(u) : MathUtil.Angle(travel);
            into.Add(new Placement { Unit = u, Center = dest, Facing = f });
            return;
        }

        var c = Vector2.Zero;
        var heading = Vector2.Zero;
        foreach (var u in units)
        {
            c += u.Anchor;
            heading += MathUtil.Dir(IntendedFacing(u));
        }
        c /= units.Count;
        float groupFacing = heading.LengthSquared() > 1e-4f ? MathUtil.Angle(heading) : IntendedFacing(units[0]);
        var move = dest - c;
        bool shortMove = move.Length() < ShortMove;
        float newFacing = shortMove ? groupFacing : MathUtil.Angle(move);

        foreach (var u in units)
        {
            var local = MathUtil.ToLocal(u.Anchor - c, groupFacing);
            into.Add(new Placement
            {
                Unit = u,
                Center = dest + MathUtil.FromLocal(local, newFacing),
                Facing = shortMove ? IntendedFacing(u) : newFacing,
            });
        }
    }

    public static void Apply(IReadOnlyList<Placement> plan, bool run)
    {
        foreach (var p in plan)
            p.Unit.OrderMove(p.Center, p.Facing, run, p.Files);
    }

    /// <summary>Ghost positions for every man in the plan.</summary>
    public static void Preview(IReadOnlyList<Placement> plan, List<Vector2> into)
    {
        into.Clear();
        foreach (var p in plan)
            p.Unit.PreviewSlots(p.Center, p.Facing, p.Files, into);
    }
}
