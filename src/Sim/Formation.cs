using System;
using System.Collections.Generic;
using Godot;

namespace UnitFighting.Sim;

public enum FormationType { Line, Deep, Thin, Column, Wedge, Square, Loose }

/// <summary>
/// One place in a formation, relative to the formation's centre: x to the right, y forward.
/// Slots are listed in priority order (front rank first), so when men fall the rear ranks step up.
/// </summary>
public struct SlotDef
{
    public Vector2 Local;
    public float FacingOffset;
    public int Rank;
}

public static class Formation
{
    public const float FileSpacing = 1.0f;
    public const float RankSpacing = 1.2f;
    public const float LooseSpacing = 2.0f;

    public static readonly FormationType[] All =
    {
        FormationType.Line, FormationType.Deep, FormationType.Thin, FormationType.Column,
        FormationType.Wedge, FormationType.Square, FormationType.Loose,
    };

    public static string Label(FormationType t) => t switch
    {
        FormationType.Line => "Line",
        FormationType.Deep => "Deep",
        FormationType.Thin => "Thin line",
        FormationType.Column => "Column",
        FormationType.Wedge => "Wedge",
        FormationType.Square => "Square",
        FormationType.Loose => "Loose",
        _ => t.ToString(),
    };

    /// <summary>Formations whose width the player can set by dragging.</summary>
    public static bool UsesFiles(FormationType t) => t is FormationType.Line or FormationType.Deep
        or FormationType.Thin or FormationType.Column or FormationType.Loose;

    public static int DefaultFiles(FormationType t, int count) => Math.Max(1, t switch
    {
        FormationType.Line => Math.Min(count, 10),
        FormationType.Deep => Math.Min(count, 5),
        FormationType.Column => Math.Min(count, 2),
        FormationType.Loose => Math.Min(count, 10),
        _ => count,
    });

    public static float Spacing(FormationType t) => t == FormationType.Loose ? LooseSpacing : FileSpacing;

    /// <summary>How many files a line of the given width (front rank, end man to end man) holds.</summary>
    public static int FilesForWidth(FormationType t, float width, int count) =>
        Math.Clamp(Mathf.RoundToInt(width / Spacing(t)) + 1, 1, Math.Max(1, count));

    public static void Layout(FormationType type, int count, int files, List<SlotDef> slots)
    {
        slots.Clear();
        if (count <= 0) return;
        switch (type)
        {
            case FormationType.Wedge: LayoutWedge(count, slots); break;
            case FormationType.Square: LayoutSquare(count, slots); break;
            default:
            {
                float sx = type == FormationType.Loose ? LooseSpacing : FileSpacing;
                float sy = type == FormationType.Loose ? LooseSpacing : RankSpacing;
                LayoutRect(count, Math.Clamp(files, 1, count), sx, sy, slots);
                break;
            }
        }
    }

    /// <summary>Front-rank width and total depth of a formation, in metres.</summary>
    public static Vector2 Extent(FormationType type, int count, int files)
    {
        if (count <= 0) return Vector2.Zero;
        var tmp = new List<SlotDef>(count);
        Layout(type, count, files, tmp);
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var s in tmp)
        {
            minX = Math.Min(minX, s.Local.X); maxX = Math.Max(maxX, s.Local.X);
            minY = Math.Min(minY, s.Local.Y); maxY = Math.Max(maxY, s.Local.Y);
        }
        return new Vector2(maxX - minX, maxY - minY);
    }

    static void LayoutRect(int count, int files, float sx, float sy, List<SlotDef> slots)
    {
        int ranks = (count + files - 1) / files;
        float frontY = (ranks - 1) * sy * 0.5f;
        for (int r = 0; r < ranks; r++)
        {
            int inRank = r < ranks - 1 ? files : count - files * (ranks - 1);
            for (int c = 0; c < inRank; c++)
                slots.Add(new SlotDef { Local = new Vector2((c - (inRank - 1) * 0.5f) * sx, frontY - r * sy), Rank = r });
        }
    }

    static void LayoutWedge(int count, List<SlotDef> slots)
    {
        const float sx = 0.95f, sy = 1.05f;
        int ranks = 0;
        for (int placed = 0; placed < count; ranks++) placed += 2 * ranks + 1;
        float frontY = (ranks - 1) * sy * 0.5f;
        int left = count;
        for (int r = 0; r < ranks; r++)
        {
            int inRank = Math.Min(2 * r + 1, left);
            left -= inRank;
            for (int c = 0; c < inRank; c++)
                slots.Add(new SlotDef { Local = new Vector2((c - (inRank - 1) * 0.5f) * sx, frontY - r * sy), Rank = r });
        }
    }

    static void LayoutSquare(int count, List<SlotDef> slots)
    {
        // An orb: men on concentric rings, every one facing outward. No flank, no rear.
        int outer = count <= 8 ? count : (int)Math.Ceiling(count * 0.65f);
        int inner = count - outer;
        float r1 = Math.Max(1.6f, outer * 1.05f / Mathf.Tau);
        AddRing(outer, r1, 0f, 0, slots);
        if (inner > 0) AddRing(inner, Math.Max(0.6f, r1 - 1.15f), Mathf.Pi / inner, 1, slots);
    }

    static void AddRing(int n, float radius, float phase, int rank, List<SlotDef> slots)
    {
        for (int i = 0; i < n; i++)
        {
            float a = phase + i * Mathf.Tau / n;
            // Local x is the formation's right, which is angle -PI/2, hence the minus.
            slots.Add(new SlotDef { Local = new Vector2(-Mathf.Sin(a), Mathf.Cos(a)) * radius, FacingOffset = a, Rank = rank });
        }
    }
}
