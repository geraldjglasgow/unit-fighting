using System;
using System.Collections.Generic;
using Godot;
using UnitFighting.Sim;

namespace UnitFighting.Game;

/// <summary>
/// The pre-battle setup screen: pick how many battalions of each type Rome (the player) and the Gauls (the AI)
/// field, how many men per battalion, the seed, and whether the AI plays both sides ("watch"). Shown on a plain
/// launch, forced with --setup, and shown again after a battle (F5, or Enter once it's over) with whatever was
/// last chosen still in place, since this node lives for the whole session and just hides/shows.
/// Built in code to match the Hud's look; reuses its colours and panel/button styling (see Hud.cs).
/// </summary>
public partial class SetupScreen : CanvasLayer
{
    const int MinMen = 10, MaxMen = 60, MenStep = 5;
    const int MaxCount = 20;      // a generous cap per unit type; the perf warning below kicks in well before this
    const int PerfWarnMen = 1000;

    sealed class Row
    {
        public UnitType Type;
        public int Count;
        public Label CountLbl;
        public Button Minus, Plus;
    }

    Main _main;
    Font _bold;
    StyleBoxFlat _btnNormal, _btnHover, _btnPressed, _btnLit, _btnLitHover;

    readonly List<Row> _rome = new(), _gauls = new();
    int _romeMen = 20, _gaulsMen = 20;
    int _seed;
    bool _autopilot;

    Label _totals, _warning, _seedLbl, _romeMenLbl, _gaulsMenLbl;
    Button _autopilotButton, _startButton, _defaultButton;

    public void Init(Main main)
    {
        _main = main;
        Layer = 9;
        _bold = new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationEmbolden = 0.7f };
        MakeButtonStyles();

        foreach (var t in UnitTypes.All)
            (t.Faction == Faction.Rome ? _rome : _gauls).Add(new Row { Type = t, Count = DefaultCount(t) });
        _seed = main.Seed;

        Build();
        RefreshTotals();
    }

    /// <summary>Battalion counts that reproduce Default()'s composition (names and layout differ, counts don't).</summary>
    static int DefaultCount(UnitType t) => t.Name switch
    {
        "Legionaries" => 1, "Veteran Legionaries" => 1, "Auxiliary Spearmen" => 2,
        "Gallic Warband" => 2, "Gallic Axemen" => 1, "Chosen Swordsmen" => 1, "Spear Levy" => 1,
        _ => 0,
    };

    // ================================================================ building

    void Build()
    {
        var root = new Control { Name = "SetupRoot" };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var backdrop = new ColorRect { Color = new Color(0.03f, 0.035f, 0.045f, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(backdrop);

        // The content is a fixed size (a few unit-type rows per side; picking bigger counts doesn't add rows),
        // so plain centring is enough — no need for a scrolling container here.
        var panel = Hud.MakePanel(0.93f, 16, 28, 20);
        Hud.Place(panel, 0.5f, 0.5f, Vector2.Zero, Control.GrowDirection.Both, Control.GrowDirection.Both);
        panel.CustomMinimumSize = new Vector2(940, 0);
        root.AddChild(panel);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        panel.AddChild(v);

        var title = Lbl("SET UP THE BATTLE", 26, Hud.Gold, true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(title);
        var sub = Lbl("Choose how many battalions of each kind fight, and how many men in each.", 13, Hud.Dim);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(sub);

        var sides = new HBoxContainer();
        sides.AddThemeConstantOverride("separation", 24);
        sides.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        v.AddChild(sides);
        sides.AddChild(BuildSide(0));
        sides.AddChild(new VSeparator());
        sides.AddChild(BuildSide(1));

        v.AddChild(new HSeparator());

        var mid = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        mid.AddThemeConstantOverride("separation", 20);
        v.AddChild(mid);
        mid.AddChild(BuildSeedRow());
        _autopilotButton = MakeButton("Watch: AI controls both sides", ToggleAutopilot);
        mid.AddChild(_autopilotButton);

        _totals = Lbl("", 16, Hud.Text, true);
        _totals.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_totals);
        _warning = Lbl("", 13, Hud.Warn, true);
        _warning.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_warning);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 14);
        v.AddChild(buttons);
        _startButton = MakeButton("Start battle   (Enter)", StartCustom);
        _startButton.CustomMinimumSize = new Vector2(220, 36);
        buttons.AddChild(_startButton);
        _defaultButton = MakeButton("Default battle", StartDefaultPreset);
        _defaultButton.CustomMinimumSize = new Vector2(180, 36);
        buttons.AddChild(_defaultButton);
    }

    Control BuildSide(int team)
    {
        bool rome = team == 0;
        var rows = rome ? _rome : _gauls;
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(430, 0) };
        box.AddThemeConstantOverride("separation", 6);
        box.AddChild(Lbl(rome ? "ROME  (you)" : "GAULS  (AI)", 17, Hud.TeamColor[team], true));
        foreach (var row in rows) box.AddChild(BuildTypeRow(row));

        box.AddChild(new HSeparator());
        var menRow = new HBoxContainer();
        menRow.AddThemeConstantOverride("separation", 8);
        menRow.AddChild(Lbl("Men per battalion", 13, Hud.Dim, false, 160));
        var menLbl = Lbl(MenOf(team).ToString(), 15, Hud.Text, true, 30);
        menLbl.HorizontalAlignment = HorizontalAlignment.Center;
        if (rome) _romeMenLbl = menLbl; else _gaulsMenLbl = menLbl;
        menRow.AddChild(MakeButton("-", () => StepMen(team, -MenStep)));
        menRow.AddChild(menLbl);
        menRow.AddChild(MakeButton("+", () => StepMen(team, +MenStep)));
        box.AddChild(menRow);
        return box;
    }

    Control BuildTypeRow(Row row)
    {
        var card = Hud.MakePanel(0.7f, 8, 10, 6);
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        card.AddChild(h);

        var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        info.AddThemeConstantOverride("separation", 1);
        info.AddChild(Lbl(row.Type.Name, 14, Hud.Text, true));
        info.AddChild(Lbl(StatLine(row.Type), 11, Hud.Dim));
        var blurb = Lbl(row.Type.Blurb, 11, Hud.Dim);
        blurb.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        blurb.CustomMinimumSize = new Vector2(240, 0);
        info.AddChild(blurb);
        h.AddChild(info);

        var stepper = new HBoxContainer();
        stepper.AddThemeConstantOverride("separation", 6);
        row.Minus = MakeButton("-", () => StepCount(row, -1));
        stepper.AddChild(row.Minus);
        row.CountLbl = Lbl(row.Count.ToString(), 16, Hud.Text, true, 26);
        row.CountLbl.HorizontalAlignment = HorizontalAlignment.Center;
        stepper.AddChild(row.CountLbl);
        row.Plus = MakeButton("+", () => StepCount(row, +1));
        stepper.AddChild(row.Plus);
        h.AddChild(stepper);
        return card;
    }

    static string StatLine(UnitType t) =>
        $"{t.Weapon.Name} & {(t.Shield?.Name ?? "no shield")}, {t.Armor.Name}  ·  skill {t.SkillMean:0.00}, nerve {t.NerveMean:0.00}";

    Control BuildSeedRow()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(Lbl("Seed", 13, Hud.Dim));
        _seedLbl = Lbl(_seed.ToString(), 15, Hud.Text, true, 70);
        row.AddChild(_seedLbl);
        row.AddChild(MakeButton("Random seed", () =>
        {
            _seed = (int)(GD.Randi() % 100000);
            _seedLbl.Text = _seed.ToString();
        }));
        return row;
    }

    // ================================================================ state

    int MenOf(int team) => team == 0 ? _romeMen : _gaulsMen;

    void StepMen(int team, int delta)
    {
        int v = Math.Clamp(MenOf(team) + delta, MinMen, MaxMen);
        if (team == 0) { _romeMen = v; _romeMenLbl.Text = v.ToString(); }
        else { _gaulsMen = v; _gaulsMenLbl.Text = v.ToString(); }
        RefreshTotals();
    }

    void StepCount(Row row, int delta)
    {
        row.Count = Math.Clamp(row.Count + delta, 0, MaxCount);
        row.CountLbl.Text = row.Count.ToString();
        RefreshTotals();
    }

    void ToggleAutopilot()
    {
        _autopilot = !_autopilot;
        SetLit(_autopilotButton, _autopilot);
    }

    /// <summary>Recomputes the live totals line and the perf warning. Public: Main calls it when the screen reappears.</summary>
    public void RefreshTotals()
    {
        int romeB = 0, romeM = 0, gaulB = 0, gaulM = 0;
        foreach (var r in _rome) if (r.Count > 0) { romeB += r.Count; romeM += r.Count * _romeMen; }
        foreach (var r in _gauls) if (r.Count > 0) { gaulB += r.Count; gaulM += r.Count * _gaulsMen; }
        _totals.Text = $"Rome {romeB} battalion{(romeB == 1 ? "" : "s")} · {romeM} men    vs    " +
                        $"Gauls {gaulB} battalion{(gaulB == 1 ? "" : "s")} · {gaulM} men";
        int total = romeM + gaulM;
        _warning.Text = total > PerfWarnMen ? $"{total} men on the field — expect a performance hit above ~{PerfWarnMen}." : "";
    }

    void RefreshAllLabels()
    {
        foreach (var r in _rome) r.CountLbl.Text = r.Count.ToString();
        foreach (var r in _gauls) r.CountLbl.Text = r.Count.ToString();
        _romeMenLbl.Text = _romeMen.ToString();
        _gaulsMenLbl.Text = _gaulsMen.ToString();
        _seedLbl.Text = _seed.ToString();
        SetLit(_autopilotButton, _autopilot);
        RefreshTotals();
    }

    static void ApplyCounts(List<Row> rows, List<(UnitType Type, int Count)> groups)
    {
        foreach (var r in rows) r.Count = 0;
        foreach (var (type, count) in groups)
        {
            var row = rows.Find(r => r.Type.Name == type.Name);
            if (row != null) row.Count = count;
        }
    }

    /// <summary>Applies any --rome/--gauls/--men/--seed given alongside --setup, on top of the built-in defaults.</summary>
    public void ApplyArgs(GameArgs args, int seed)
    {
        _seed = seed;
        _autopilot = args.Auto;
        if (args.Rome != null) ApplyCounts(_rome, GameArgs.ParseGroups(args.Rome));
        if (args.Gauls != null) ApplyCounts(_gauls, GameArgs.ParseGroups(args.Gauls));
        if (args.Men is { } m) _romeMen = _gaulsMen = Math.Clamp(m, MinMen, MaxMen);
        if (args.RomeMen is { } rm) _romeMen = Math.Clamp(rm, MinMen, MaxMen);
        if (args.GaulsMen is { } gm) _gaulsMen = Math.Clamp(gm, MinMen, MaxMen);
        RefreshAllLabels();
    }

    /// <summary>Keeps the screen in sync after a battle starts, so F5 shows what actually went in.</summary>
    public void SyncFromDefault(int seed, bool autopilot)
    {
        foreach (var r in _rome) r.Count = DefaultCount(r.Type);
        foreach (var r in _gauls) r.Count = DefaultCount(r.Type);
        _romeMen = _gaulsMen = 20;
        _seed = seed;
        _autopilot = autopilot;
        RefreshAllLabels();
    }

    public void SyncFromSetup(BattleSetup setup)
    {
        ApplyCounts(_rome, setup.Rome);
        ApplyCounts(_gauls, setup.Gauls);
        _romeMen = setup.RomeMen;
        _gaulsMen = setup.GaulsMen;
        _seed = setup.Seed;
        _autopilot = setup.Autopilot;
        RefreshAllLabels();
    }

    // ================================================================ actions

    BattleSetup BuildSetup()
    {
        var setup = new BattleSetup { Seed = _seed, Autopilot = _autopilot, RomeMen = _romeMen, GaulsMen = _gaulsMen };
        foreach (var r in _rome) if (r.Count > 0) setup.Rome.Add((r.Type, r.Count));
        foreach (var r in _gauls) if (r.Count > 0) setup.Gauls.Add((r.Type, r.Count));
        return setup;
    }

    void StartCustom()
    {
        var setup = BuildSetup();
        if (setup.Rome.Count == 0 && setup.Gauls.Count == 0) return;   // nothing to fight with
        _main.BeginCustomBattle(setup);
    }

    void StartDefaultPreset()
    {
        foreach (var r in _rome) r.Count = DefaultCount(r.Type);
        foreach (var r in _gauls) r.Count = DefaultCount(r.Type);
        _romeMen = _gaulsMen = 20;
        RefreshAllLabels();
        _main.BeginDefaultBattle(_seed, 0f);
    }

    // ================================================================ input

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible) return;
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter })
        {
            StartCustom();
            GetViewport().SetInputAsHandled();
        }
    }

    // ================================================================ self-test helpers

    public int RomeCount(int index) => _rome[index].Count;
    public int GaulsCount(int index) => _gauls[index].Count;
    public string RomeTypeName(int index) => _rome[index].Type.Name;
    public string GaulsTypeName(int index) => _gauls[index].Type.Name;
    public Rect2 StartButtonRect => _startButton.GetGlobalRect();
    public Rect2 DefaultButtonRect => _defaultButton.GetGlobalRect();
    public Rect2 CountButtonRect(int team, int index, bool plus)
    {
        var row = (team == 0 ? _rome : _gauls)[index];
        return (plus ? row.Plus : row.Minus).GetGlobalRect();
    }

    // ================================================================ widgets (mirrors Hud.cs's, kept local so
    // this screen doesn't need instance access into the Hud, which is owned by other agents this wave)

    Label Lbl(string text, int size, Color color, bool bold = false, float minWidth = 0f)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        if (bold) l.AddThemeFontOverride("font", _bold);
        if (minWidth > 0f) l.CustomMinimumSize = new Vector2(minWidth, 0);
        return l;
    }

    void MakeButtonStyles()
    {
        StyleBoxFlat B(Color bg, Color border)
        {
            var s = new StyleBoxFlat
            {
                BgColor = bg, BorderColor = border,
                ContentMarginLeft = 9, ContentMarginRight = 9, ContentMarginTop = 4, ContentMarginBottom = 4,
                AntiAliasing = true,
            };
            s.SetCornerRadiusAll(6);
            s.SetBorderWidthAll(1);
            return s;
        }
        _btnNormal = B(new Color(0.17f, 0.18f, 0.21f, 0.95f), new Color(1, 1, 1, 0.1f));
        _btnHover = B(new Color(0.25f, 0.27f, 0.32f, 0.98f), new Color(1, 1, 1, 0.25f));
        _btnPressed = B(new Color(0.32f, 0.3f, 0.22f, 1f), Hud.Gold);
        _btnLit = B(new Color(0.36f, 0.28f, 0.08f, 0.98f), Hud.Gold);
        _btnLitHover = B(new Color(0.45f, 0.36f, 0.12f, 1f), Hud.Gold);
    }

    Button MakeButton(string text, Action onPressed)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, MouseFilter = Control.MouseFilterEnum.Stop };
        b.AddThemeFontSizeOverride("font_size", 13);
        b.AddThemeStyleboxOverride("normal", _btnNormal);
        b.AddThemeStyleboxOverride("hover", _btnHover);
        b.AddThemeStyleboxOverride("pressed", _btnPressed);
        b.AddThemeStyleboxOverride("disabled", _btnNormal);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", Hud.Text);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.CustomMinimumSize = new Vector2(0, 28);
        b.Pressed += onPressed;
        b.SetMeta("lit", false);
        return b;
    }

    void SetLit(Button b, bool lit)
    {
        if ((bool)b.GetMeta("lit") == lit) return;
        b.SetMeta("lit", lit);
        b.AddThemeStyleboxOverride("normal", lit ? _btnLit : _btnNormal);
        b.AddThemeStyleboxOverride("hover", lit ? _btnLitHover : _btnHover);
    }
}
