using System;
using System.Collections.Generic;
using Godot;

namespace UnitFighting.Sim;

/// <summary>
/// Node-free description of a battle to build: which battalion types and counts each side fields, and how many
/// men are in each battalion. The setup screen and CLI flags both build one of these; see <see cref="Scenarios.Custom"/>.
/// </summary>
public sealed class BattleSetup
{
    public int Seed;
    public bool Autopilot;   // "watch: AI controls both sides"
    public readonly List<(UnitType Type, int Count)> Rome = new();
    public readonly List<(UnitType Type, int Count)> Gauls = new();
    public int RomeMen = 20, GaulsMen = 20;
}

public static class Scenarios
{
    /// <summary>
    /// Four Roman battalions (team 0) at the south end facing north, five Gallic ones (team 1) facing them.
    /// The Gallic line is longer, so the player has to deal with an overlapping flank.
    /// </summary>
    public static Battle Default(int seed, bool aiForPlayer = false)
    {
        var b = new Battle(seed);
        const float romeY = -32f, gaulY = 32f;
        const float north = 0f, south = Mathf.Pi;

        // Facing north, the Romans' right is -x. Listed left to right as the player sees them.
        b.AddBattalion(0, UnitTypes.AuxSpearmen, "I Auxilia", new Vector2(24f, romeY), north);
        b.AddBattalion(0, UnitTypes.Legionaries, "II Cohort", new Vector2(8f, romeY), north);
        b.AddBattalion(0, UnitTypes.Veterans, "I Cohort (Veterans)", new Vector2(-8f, romeY), north);
        b.AddBattalion(0, UnitTypes.AuxSpearmen, "II Auxilia", new Vector2(-24f, romeY), north);

        b.AddBattalion(1, UnitTypes.SpearLevy, "Levy of the Senones", new Vector2(32f, gaulY), south);
        b.AddBattalion(1, UnitTypes.Warband, "Warband of Brennus", new Vector2(16f, gaulY), south);
        b.AddBattalion(1, UnitTypes.ChosenSwordsmen, "Chosen of the Arverni", new Vector2(0f, gaulY), south);
        b.AddBattalion(1, UnitTypes.Axemen, "Axemen of the Aedui", new Vector2(-16f, gaulY), south);
        b.AddBattalion(1, UnitTypes.Warband, "Warband of Cotus", new Vector2(-32f, gaulY), south);

        b.AIs.Add(new BattleAI(1));
        if (aiForPlayer) b.AIs.Add(new BattleAI(0) { AdvanceAfter = 20f });
        return b;
    }

    const int MaxLineBattalions = 6;         // more battalions than this on a side and it forms a second line
    const float LineGap = 4f;                // clearance between neighbouring battalions' formation edges
    const float RomeY = -32f, GaulY = 32f;   // same distance apart as Default
    const float SecondLineOffset = 20f;      // how far behind the front line the second one forms

    /// <summary>Builds a battle from the setup screen's (or the CLI's) choice of battalions per side. Rome deploys
    /// south facing north and the Gauls north facing south, same as <see cref="Default"/>.</summary>
    public static Battle Custom(BattleSetup setup)
    {
        var b = new Battle(setup.Seed);
        DeploySide(b, 0, setup.Rome, setup.RomeMen, RomeY, 0f, -1f);
        DeploySide(b, 1, setup.Gauls, setup.GaulsMen, GaulY, Mathf.Pi, +1f);
        b.AIs.Add(new BattleAI(1));
        if (setup.Autopilot) b.AIs.Add(new BattleAI(0) { AdvanceAfter = 20f });
        return b;
    }

    /// <summary>Expands (type, count) groups into individual battalions and lays them out in lines of up to
    /// <see cref="MaxLineBattalions"/>, each further one <see cref="SecondLineOffset"/> behind the last (away
    /// from the enemy: <paramref name="behindSign"/> is the sign of "away" for this side's y axis).</summary>
    static void DeploySide(Battle b, int team, List<(UnitType Type, int Count)> groups, int men, float frontY, float facing, float behindSign)
    {
        var list = new List<UnitType>();
        foreach (var (type, count) in groups)
            for (int i = 0; i < count; i++) list.Add(type);
        if (list.Count == 0) return;

        var lines = new List<List<UnitType>>();
        for (int i0 = 0; i0 < list.Count;)
        {
            int n = Math.Min(MaxLineBattalions, list.Count - i0);
            lines.Add(list.GetRange(i0, n));
            i0 += n;
        }

        var nameIndex = new Dictionary<string, int>();
        List<Battalion> ahead = null;
        for (int li = 0; li < lines.Count; li++)
        {
            float y = frontY + behindSign * SecondLineOffset * li;
            var placed = PlaceLine(b, team, lines[li], men, y, facing, nameIndex);
            if (ahead != null) AssignLineReserves(b, placed, ahead);
            ahead = placed;
        }
    }

    /// <summary>
    /// Each battalion of a line behind the first becomes the reserve of the one directly in front of it: the nearest
    /// sideways that overlaps it and has no reserve yet (so a third line backs the second). One that overlaps nobody
    /// free stays a plain battalion. BattleAI.AssignReserves uses the same overlap test and skips battalions already
    /// linked, so for an AI side it only fills in what this leaves, and for the player's side (no AI unless autopilot)
    /// this is what gives his second line its reserve role.
    /// </summary>
    static void AssignLineReserves(Battle b, List<Battalion> line, List<Battalion> ahead)
    {
        foreach (var r in line)
        {
            Battalion best = null;
            float bestSide = float.MaxValue;
            foreach (var f in ahead)
            {
                if (f.ReservedBy != null) continue;
                float side = Math.Abs(f.Anchor.X - r.Anchor.X);
                if (side > (f.Width + r.Width) * 0.5f || side >= bestSide) continue;
                bestSide = side;
                best = f;
            }
            if (best != null) b.AssignReserve(r, best);
        }
    }

    /// <summary>Lays one line of battalions out side by side, centred on x = 0, spaced by their actual formation
    /// width (which depends on their man count) plus <see cref="LineGap"/>. Returns them in order of x.</summary>
    static List<Battalion> PlaceLine(Battle b, int team, List<UnitType> types, int men, float y, float facing, Dictionary<string, int> nameIndex)
    {
        var placed = new List<Battalion>(types.Count);
        var sized = new UnitType[types.Count];
        var widths = new float[types.Count];
        float total = types.Count > 1 ? LineGap * (types.Count - 1) : 0f;
        for (int i = 0; i < types.Count; i++)
        {
            sized[i] = types[i].Sized(men);
            int files = Formation.DefaultFiles(sized[i].Formation, sized[i].Size);
            widths[i] = Formation.Extent(sized[i].Formation, sized[i].Size, files).X;
            total += widths[i];
        }
        float x = -total * 0.5f;
        for (int i = 0; i < sized.Length; i++)
        {
            x += widths[i] * 0.5f;
            int idx = nameIndex.TryGetValue(types[i].Name, out var n) ? n : 0;
            nameIndex[types[i].Name] = idx + 1;
            placed.Add(b.AddBattalion(team, sized[i], NameFor(types[i], idx), new Vector2(x, y), facing));
            x += widths[i] * 0.5f + LineGap;
        }
        return placed;
    }

    static readonly string[] Tribes =
    {
        "Senones", "Arverni", "Aedui", "Bituriges", "Carnutes", "Sequani", "Nervii", "Helvetii", "Boii", "Treveri",
    };

    /// <summary>Flavour names in the style of <see cref="Default"/>'s, numbered for repeats of the same type.</summary>
    static string NameFor(UnitType type, int index) => type.Name switch
    {
        "Legionaries" => $"{RomanNumeral(index + 1)} Cohort",
        "Veteran Legionaries" => $"{RomanNumeral(index + 1)} Cohort (Veterans)",
        "Auxiliary Spearmen" => $"{RomanNumeral(index + 1)} Auxilia",
        "Gallic Warband" => $"Warband of {UnitTypes.GaulFirst[index % UnitTypes.GaulFirst.Length]}",
        "Gallic Axemen" => $"Axemen of the {Tribes[index % Tribes.Length]}",
        "Chosen Swordsmen" => $"Chosen of the {Tribes[(index + 3) % Tribes.Length]}",
        "Spear Levy" => $"Levy of the {Tribes[(index + 6) % Tribes.Length]}",
        _ => $"{RomanNumeral(index + 1)} {type.Name}",
    };

    static string RomanNumeral(int n)
    {
        string[] sym = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
        int[] val = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string s = "";
        for (int i = 0; i < val.Length && n > 0; i++)
            while (n >= val[i]) { s += sym[i]; n -= val[i]; }
        return s;
    }
}
