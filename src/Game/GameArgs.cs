using System.Collections.Generic;
using System.Globalization;
using Godot;
using UnitFighting.Sim;

namespace UnitFighting.Game;

/// <summary>Command-line options, given after <c>--</c> on the Godot command line.</summary>
public sealed class GameArgs
{
    public int? Seed;
    public bool Auto;
    public float Time;
    public float? Speed;
    public string Screenshot;
    public int ShotFrames = 12;
    public string ShotView = "player";
    public int Select = -1;
    public bool Inspect;
    public bool HelpOverlay;
    public int QuitAfter = -1;
    public bool SelfTest;
    public bool Setup;
    public string Rome, Gauls;   // "Type:Count,Type:Count", e.g. "Legionaries:3,AuxSpearmen:2"
    public int? Men;             // men per battalion, both sides
    public int? RomeMen, GaulsMen;   // overrides --men for one side, e.g. an asymmetric 40 vs 30
    public int ReserveOf = -1, ReserveFront = -1;

    public const string Usage =
        "Unit Fighting options (after --):\n" +
        "  --seed=N            battle seed\n" +
        "  --auto              the AI commands both sides (autopilot)\n" +
        "  --time=T            fast-forward the battle T seconds before the first frame\n" +
        "  --speed=X           starting time scale (0.1, 0.25, 0.5, 1, 2, 4)\n" +
        "  --screenshot=PATH   save a PNG after --shot-frames frames (default 12), then quit\n" +
        "  --shot-view=V       player | overview | melee camera for the start\n" +
        "  --select=I          select the player's battalion I\n" +
        "  --inspect           inspect a soldier who is fighting\n" +
        "  --help-overlay      show the controls overlay\n" +
        "  --quit-after=N      quit after N frames\n" +
        "  --selftest          drive the controls with synthetic input, print the results, quit\n" +
        "  --setup             force the pre-battle setup screen, even with other flags above\n" +
        "  --rome=SPEC         start a custom battle: Rome's battalions, e.g. Legionaries:3,AuxSpearmen:2\n" +
        "  --gauls=SPEC        as --rome, for the Gauls, e.g. Warband:4,Axemen:2 (either given skips the screen)\n" +
        "  --men=N             men per battalion for a custom battle (both sides), default 20\n" +
        "  --rome-men=N        overrides --men for Rome only, e.g. an asymmetric 40 vs 30\n" +
        "  --gauls-men=N       overrides --men for the Gauls only\n" +
        "  --reserve=R:F       make the player's battalion R the reserve of his battalion F\n";

    public static GameArgs Parse(string[] args)
    {
        var a = new GameArgs();
        foreach (var raw in args)
        {
            string key = raw, val = null;
            int eq = raw.IndexOf('=');
            if (eq >= 0) { key = raw[..eq]; val = raw[(eq + 1)..]; }
            switch (key)
            {
                case "--seed": if (int.TryParse(val, out var seed)) a.Seed = seed; break;
                case "--auto": a.Auto = true; break;
                case "--time": a.Time = ParseFloat(val, 0f); break;
                case "--speed": a.Speed = ParseFloat(val, 1f); break;
                case "--screenshot": a.Screenshot = val; break;
                case "--shot-frames": if (int.TryParse(val, out var f)) a.ShotFrames = Mathf.Max(1, f); break;
                case "--shot-view": a.ShotView = (val ?? "player").ToLowerInvariant(); break;
                case "--select": if (int.TryParse(val, out var sel)) a.Select = sel; break;
                case "--inspect": a.Inspect = true; break;
                case "--help-overlay": a.HelpOverlay = true; break;
                case "--quit-after": if (int.TryParse(val, out var q)) a.QuitAfter = q; break;
                case "--selftest": a.SelfTest = true; break;
                case "--setup": a.Setup = true; break;
                case "--rome": a.Rome = val; break;
                case "--gauls": a.Gauls = val; break;
                case "--men": if (int.TryParse(val, out var men)) a.Men = men; break;
                case "--rome-men": if (int.TryParse(val, out var rm)) a.RomeMen = rm; break;
                case "--gauls-men": if (int.TryParse(val, out var gm)) a.GaulsMen = gm; break;
                case "--reserve":
                    var rf = (val ?? "").Split(':');
                    if (rf.Length == 2 && int.TryParse(rf[0], out var r) && int.TryParse(rf[1], out var fr)) { a.ReserveOf = r; a.ReserveFront = fr; }
                    break;
                case "--help": case "-h": GD.Print(Usage); break;
                default: GD.PushWarning($"Unknown option '{raw}'.\n{Usage}"); break;
            }
        }
        return a;
    }

    static float ParseFloat(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Parses a <c>--rome</c> / <c>--gauls</c> spec ("Legionaries:3,AuxSpearmen:2") into battalion groups.
    /// Names match the UnitTypes.* field names, not their display names. Bad entries warn and are skipped.</summary>
    public static List<(UnitType Type, int Count)> ParseGroups(string spec)
    {
        var list = new List<(UnitType, int)>();
        if (string.IsNullOrWhiteSpace(spec)) return list;
        foreach (var part in spec.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length == 2 && UnitTypes.ByName.TryGetValue(kv[0].Trim(), out var type) &&
                int.TryParse(kv[1].Trim(), out var count) && count > 0)
            {
                list.Add((type, count));
            }
            else
            {
                GD.PushWarning($"Bad unit group '{part}' in '{spec}'; expected Type:Count, e.g. Legionaries:3. " +
                                $"Known types: {string.Join(", ", UnitTypes.ByName.Keys)}");
            }
        }
        return list;
    }
}
