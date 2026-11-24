using System;
using System.Collections.Generic;
using Godot;
using UnitFighting.Sim;

namespace UnitFighting.Game;

/// <summary>
/// Everything on screen that isn't the battlefield: army strengths and the clock, one card per battalion,
/// the command panel, the soldier inspector, tooltips, the help overlay and the end-of-battle banner.
/// Built in code; call <see cref="Refresh"/> once a frame.
/// </summary>
public partial class Hud : CanvasLayer
{
    public static readonly Color[] TeamColor = { new(0.93f, 0.36f, 0.3f), new(0.4f, 0.66f, 0.98f) };
    static readonly string[] TeamTitle = { "ROME", "GAULS" };
    // Public: the setup screen (SetupScreen.cs) reuses these so it matches the Hud's look.
    public static readonly Color Text = new(0.93f, 0.93f, 0.9f);
    public static readonly Color Dim = new(0.66f, 0.68f, 0.72f);
    public static readonly Color Gold = new(1f, 0.8f, 0.32f);
    public static readonly Color Bad = new(1f, 0.38f, 0.32f);
    public static readonly Color Warn = new(1f, 0.66f, 0.25f);
    static readonly Color StaminaColor = new(0.38f, 0.68f, 0.98f);
    public static readonly Color ReserveColor = new(0.55f, 0.86f, 0.78f);
    static readonly string[] AttrNames = { "Skill", "Strength", "Agility", "Endurance", "Nerve" };
    const int BottomMargin = 30;   // the hint line lives below the bottom panels
    // Battalion cards: full width while they fit in one row beside the orders panel and can stay CardOneRowMin wide,
    // else two rows, narrowed down to CardMinWidth if need be (about 12 cards at 1280 wide); more than that wraps on.
    const int CardWidth = 196, CardOneRowMin = 160, CardMinWidth = 130, CardGap = 8;

    Main _main;
    PlayerInput P => _main.Controls;
    Battle _battle;
    readonly int[] _startMen = new int[2];
    Control _root;
    Font _bold;

    // Top bar.
    PanelContainer _topBar;
    readonly Label[] _armyCount = new Label[2], _armyDead = new Label[2];
    readonly Bar[] _armyBar = new Bar[2];
    Label _clock, _speed, _follow;
    PanelContainer _pausedBadge, _autoBadge;

    // Battalion cards.
    HFlowContainer _cardRow;
    readonly List<UnitCard> _cards = new();
    Vector2I _cardLayoutKey = new(-1, -1);   // (viewport width, card count) the cards were last sized for
    int _cardRows;
    StyleBoxFlat _cardStyle, _cardSelectedStyle, _cardHoverStyle;

    // Command panel.
    PanelContainer _cmd;
    Label _cmdTitle;
    readonly List<Button> _formButtons = new();
    Button _runButton, _relieveButton;
    StyleBoxFlat _btnNormal, _btnHover, _btnPressed, _btnLit, _btnLitHover;

    // Soldier inspector.
    PanelContainer _insp;
    Label _iName, _iVet, _iUnit, _iRating, _iHpText, _iStaText, _iState, _iOpp, _iKit, _iKilled;
    Bar _iHp, _iSta;
    readonly Bar[] _iAttr = new Bar[5];
    readonly Label[] _iAttrVal = new Label[5];
    readonly Label[] _iRecord = new Label[5];
    Button _iFollow;

    // Overlays.
    PanelContainer _tip;
    Label _tipTitle, _tipBody;
    PanelContainer _help;
    Label _hint;
    bool _helpVisible;
    ulong _helpHideAtMs;
    PanelContainer _end;
    Label _endTitle, _endSub, _endCasualties;
    GridContainer _heroGrid;
    ulong _nextHeroFillMs;
    Panel _box;
    VBoxContainer _msgBox;
    readonly List<(Label label, ulong born)> _msgs = new();

    sealed class UnitCard
    {
        public Battalion Unit;
        public PanelContainer Panel;
        public Label Men, Status, Form, Link;
        public PanelContainer LinkBadge;
        public Bar Morale, Stamina;
        public int Look = -1;
    }

    // ================================================================ building

    public void Init(Main main)
    {
        _main = main;
        Layer = 10;
        _bold = new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationEmbolden = 0.7f };
        MakeButtonStyles();

        _root = new Control { Name = "HudRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        BuildTopBar();
        BuildCardRow();
        BuildCommandPanel();
        BuildInspector();
        BuildMessages();
        BuildHelp();
        BuildEndBanner();
        BuildTooltip();

        _box = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        var boxStyle = new StyleBoxFlat { BgColor = new Color(0.5f, 0.8f, 1f, 0.12f), BorderColor = new Color(0.6f, 0.88f, 1f, 0.9f) };
        boxStyle.SetBorderWidthAll(1);
        _box.AddThemeStyleboxOverride("panel", boxStyle);
        _root.AddChild(_box);

        _hint = Lbl("F1 — controls", 13, Dim);
        _hint.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        _hint.AddThemeConstantOverride("shadow_offset_x", 1);
        _hint.AddThemeConstantOverride("shadow_offset_y", 1);
        Place(_hint, 0.5f, 1f, new Vector2(0, -6), Control.GrowDirection.Both, Control.GrowDirection.Begin);
        _root.AddChild(_hint);
    }

    void BuildTopBar()
    {
        var panel = _topBar = MakePanel(0.8f, 10, 14, 8);
        Place(panel, 0.5f, 0f, new Vector2(0, 10), Control.GrowDirection.Both, Control.GrowDirection.End);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        panel.AddChild(row);

        row.AddChild(ArmyBlock(0));
        row.AddChild(new VSeparator());

        var mid = new VBoxContainer { CustomMinimumSize = new Vector2(190, 0) };
        mid.AddThemeConstantOverride("separation", 2);
        _clock = Lbl("00:00", 24, Text, true);
        _clock.HorizontalAlignment = HorizontalAlignment.Center;
        mid.AddChild(_clock);
        var badges = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        badges.AddThemeConstantOverride("separation", 6);
        _speed = Lbl("1×", 15, Text, true);
        badges.AddChild(_speed);
        _pausedBadge = Badge("PAUSED", Gold, new Color(0.08f, 0.06f, 0.02f));
        badges.AddChild(_pausedBadge);
        _autoBadge = Badge("AUTOPILOT", new Color(0.35f, 0.75f, 0.55f), new Color(0.02f, 0.08f, 0.04f));
        badges.AddChild(_autoBadge);
        mid.AddChild(badges);
        _follow = Lbl("", 12, Gold);
        _follow.HorizontalAlignment = HorizontalAlignment.Center;
        mid.AddChild(_follow);
        row.AddChild(mid);

        row.AddChild(new VSeparator());
        row.AddChild(ArmyBlock(1));
        _root.AddChild(panel);
    }

    Control ArmyBlock(int team)
    {
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(230, 0) };
        box.AddThemeConstantOverride("separation", 3);
        var align = team == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        var title = Lbl(TeamTitle[team], 15, TeamColor[team], true);
        title.HorizontalAlignment = align;
        box.AddChild(title);
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 8);
        _armyBar[team] = new Bar(new Vector2(150, 10)) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _armyCount[team] = Lbl("0/0", 15, Text, true);
        if (team == 0) { line.AddChild(_armyBar[team]); line.AddChild(_armyCount[team]); }
        else { line.AddChild(_armyCount[team]); line.AddChild(_armyBar[team]); }
        box.AddChild(line);
        _armyDead[team] = Lbl("", 12, Dim);
        _armyDead[team].HorizontalAlignment = align;
        box.AddChild(_armyDead[team]);
        return box;
    }

    void BuildCardRow()
    {
        // Spans from the left edge to the orders panel (LayoutCards sets the right edge) and grows upwards as it wraps.
        _cardRow = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _cardRow.AddThemeConstantOverride("h_separation", CardGap);
        _cardRow.AddThemeConstantOverride("v_separation", CardGap);
        Place(_cardRow, 0f, 1f, new Vector2(12, -BottomMargin), Control.GrowDirection.End, Control.GrowDirection.Begin);
        _cardRow.AnchorRight = 1f;
        _root.AddChild(_cardRow);
        _cardStyle = PanelStyle(0.8f, 8, 10, 8);
        _cardHoverStyle = PanelStyle(0.85f, 8, 10, 8);
        _cardHoverStyle.BorderColor = new Color(1f, 1f, 1f, 0.35f);
        _cardHoverStyle.SetBorderWidthAll(1);
        _cardSelectedStyle = PanelStyle(0.88f, 8, 10, 8);
        _cardSelectedStyle.BgColor = new Color(0.16f, 0.14f, 0.08f, 0.9f);
        _cardSelectedStyle.BorderColor = Gold;
        _cardSelectedStyle.SetBorderWidthAll(2);
    }

    UnitCard MakeCard(Battalion u)
    {
        var c = new UnitCard { Unit = u };
        c.Panel = new PanelContainer { CustomMinimumSize = new Vector2(CardWidth, 0), MouseFilter = Control.MouseFilterEnum.Stop };
        c.Panel.AddThemeStyleboxOverride("panel", _cardStyle);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 2);
        c.Panel.AddChild(v);
        var name = Lbl(u.Name, 14, Text, true);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(name);
        // The text below clips rather than widening the card when LayoutCards narrows it.
        v.AddChild(Clipped(Lbl(u.Type.Name, 11, Dim)));
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 8);
        c.Men = Lbl("20/20", 18, Text, true);
        c.Status = Clipped(Lbl("Holding", 13, Text));
        c.Status.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        c.Status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        line.AddChild(c.Men);
        line.AddChild(c.Status);
        v.AddChild(line);
        c.Form = Clipped(Lbl("", 12, Dim));
        v.AddChild(c.Form);
        // Reserve links: "Reserve for ..." on the reserve's card, "Relieved by ..." on the card of the one it backs.
        c.LinkBadge = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        var linkStyle = new StyleBoxFlat { BgColor = new Color(ReserveColor, 0.16f), BorderColor = new Color(ReserveColor, 0.45f), ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 1, ContentMarginBottom = 1 };
        linkStyle.SetCornerRadiusAll(4);
        linkStyle.SetBorderWidthAll(1);
        c.LinkBadge.AddThemeStyleboxOverride("panel", linkStyle);
        c.Link = Lbl("", 11, ReserveColor, true);
        c.Link.ClipText = true;
        c.Link.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        c.LinkBadge.AddChild(c.Link);
        v.AddChild(c.LinkBadge);
        c.Morale = new Bar(new Vector2(40, 7)) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        c.Stamina = new Bar(new Vector2(40, 7)) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        v.AddChild(MeterRow("Morale", c.Morale, 58));
        v.AddChild(MeterRow("Stamina", c.Stamina, 58));
        c.Panel.GuiInput += ev => OnCardInput(c, ev);
        return c;
    }

    void OnCardInput(UnitCard c, InputEvent ev)
    {
        if (ev is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb) return;
        if (mb.DoubleClick) P.FocusUnit(c.Unit);
        else P.SelectUnit(c.Unit, mb.ShiftPressed);
        c.Panel.AcceptEvent();
    }

    void BuildCommandPanel()
    {
        _cmd = MakePanel(0.82f, 10, 12, 10);
        _cmd.MouseFilter = Control.MouseFilterEnum.Stop;
        Place(_cmd, 1f, 1f, new Vector2(-12, -BottomMargin), Control.GrowDirection.Begin, Control.GrowDirection.Begin);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        _cmd.AddChild(v);
        _cmdTitle = Lbl("Orders", 13, Dim);
        v.AddChild(_cmdTitle);
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 5);
        grid.AddThemeConstantOverride("v_separation", 5);
        for (int i = 0; i < Formation.All.Length; i++)
        {
            var type = Formation.All[i];
            var b = MakeButton($"{i + 1}  {Formation.Label(type)}", () => P.SetFormation(type));
            b.TooltipText = $"Formation: {Formation.Label(type)} (key {i + 1})";
            _formButtons.Add(b);
            grid.AddChild(b);
        }
        v.AddChild(grid);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 5);
        var halt = MakeButton("H  Halt", () => P.Halt());
        halt.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _runButton = MakeButton("R  Run", () => P.ToggleRun());
        _runButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _relieveButton = MakeButton("Relieve", () => P.Relieve());
        _relieveButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _relieveButton.TooltipText = "Swap in the reserve now: it takes over the line and this battalion falls back behind it\n(for a reserve: go in and relieve the battalion it backs)";
        row.AddChild(halt);
        row.AddChild(_runButton);
        row.AddChild(_relieveButton);
        v.AddChild(row);
        _root.AddChild(_cmd);
    }

    void BuildInspector()
    {
        _insp = MakePanel(0.84f, 10, 14, 12);
        _insp.MouseFilter = Control.MouseFilterEnum.Stop;
        _insp.CustomMinimumSize = new Vector2(310, 0);
        Place(_insp, 1f, 0f, new Vector2(-12, 12), Control.GrowDirection.Begin, Control.GrowDirection.End);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        _insp.AddChild(v);

        var head = new HBoxContainer();
        _iName = Lbl("", 19, Text, true);
        _iName.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _iName.ClipText = true;
        _iName.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        head.AddChild(_iName);
        var close = MakeButton("×", () => { P.Inspected = null; _main.Camera.Follow = null; });
        close.TooltipText = "Close (Esc)";
        close.CustomMinimumSize = new Vector2(26, 24);
        head.AddChild(close);
        v.AddChild(head);
        _iUnit = Lbl("", 13, Text);
        v.AddChild(_iUnit);
        var ratingRow = new HBoxContainer();
        ratingRow.AddThemeConstantOverride("separation", 8);
        _iRating = Lbl("", 14, Gold, true);
        ratingRow.AddChild(_iRating);
        _iVet = Lbl("VETERAN", 12, Gold, true);
        _iVet.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        ratingRow.AddChild(_iVet);
        v.AddChild(ratingRow);
        v.AddChild(new HSeparator());

        var attrs = new GridContainer { Columns = 3 };
        attrs.AddThemeConstantOverride("h_separation", 8);
        attrs.AddThemeConstantOverride("v_separation", 3);
        for (int i = 0; i < 5; i++)
        {
            attrs.AddChild(Lbl(AttrNames[i], 13, Dim, false, 78));
            _iAttr[i] = new Bar(new Vector2(150, 8)) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            attrs.AddChild(_iAttr[i]);
            _iAttrVal[i] = Lbl("", 13, Text, true, 30);
            _iAttrVal[i].HorizontalAlignment = HorizontalAlignment.Right;
            attrs.AddChild(_iAttrVal[i]);
        }
        v.AddChild(attrs);
        v.AddChild(new HSeparator());

        var vitals = new GridContainer { Columns = 3 };
        vitals.AddThemeConstantOverride("h_separation", 8);
        vitals.AddThemeConstantOverride("v_separation", 3);
        vitals.AddChild(Lbl("Health", 13, Dim, false, 78));
        _iHp = new Bar(new Vector2(120, 10)) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vitals.AddChild(_iHp);
        _iHpText = Lbl("", 13, Text, false, 60);
        _iHpText.HorizontalAlignment = HorizontalAlignment.Right;
        vitals.AddChild(_iHpText);
        vitals.AddChild(Lbl("Stamina", 13, Dim, false, 78));
        _iSta = new Bar(new Vector2(120, 10)) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vitals.AddChild(_iSta);
        _iStaText = Lbl("", 13, Text, false, 60);
        _iStaText.HorizontalAlignment = HorizontalAlignment.Right;
        vitals.AddChild(_iStaText);
        v.AddChild(vitals);
        v.AddChild(new HSeparator());

        var info = new GridContainer { Columns = 2 };
        info.AddThemeConstantOverride("h_separation", 8);
        info.AddChild(Lbl("State", 13, Dim, false, 78));
        _iState = Lbl("", 14, Text, true);
        info.AddChild(_iState);
        info.AddChild(Lbl("Opponent", 13, Dim, false, 78));
        _iOpp = Lbl("", 13, Text);
        _iOpp.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _iOpp.CustomMinimumSize = new Vector2(200, 0);
        info.AddChild(_iOpp);
        info.AddChild(Lbl("Kit", 13, Dim, false, 78));
        _iKit = Lbl("", 13, Text);
        _iKit.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _iKit.CustomMinimumSize = new Vector2(200, 0);
        info.AddChild(_iKit);
        v.AddChild(info);
        v.AddChild(new HSeparator());

        var rec = new GridContainer { Columns = 5 };
        rec.AddThemeConstantOverride("h_separation", 4);
        string[] recNames = { "Kills", "Hits", "Blocks", "Wounds", "Strikes" };
        for (int i = 0; i < 5; i++)
        {
            _iRecord[i] = Lbl("0", 18, i == 0 ? Gold : Text, true, 54);
            _iRecord[i].HorizontalAlignment = HorizontalAlignment.Center;
            rec.AddChild(_iRecord[i]);
        }
        for (int i = 0; i < 5; i++)
        {
            var l = Lbl(recNames[i], 11, Dim, false, 54);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            rec.AddChild(l);
        }
        v.AddChild(rec);

        _iKilled = Lbl("", 14, Bad, true);
        _iKilled.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_iKilled);

        _iFollow = MakeButton("F  Follow with camera", () => P.ToggleFollow());
        v.AddChild(_iFollow);
        _root.AddChild(_insp);
    }

    void BuildMessages()
    {
        _msgBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _msgBox.AddThemeConstantOverride("separation", 2);
        Place(_msgBox, 0.5f, 0f, new Vector2(0, 110), Control.GrowDirection.Both, Control.GrowDirection.End);
        _root.AddChild(_msgBox);
    }

    void BuildHelp()
    {
        _help = MakePanel(0.88f, 12, 22, 18);
        _help.MouseFilter = Control.MouseFilterEnum.Ignore;
        Place(_help, 0.5f, 0.5f, new Vector2(0, -20), Control.GrowDirection.Both, Control.GrowDirection.Both);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 8);
        _help.AddChild(v);
        var title = Lbl("CONTROLS", 20, Gold, true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(title);
        var grid = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 5);
        string[,] rows =
        {
            { "Camera", "W A S D / arrows pan  ·  Q E rotate  ·  wheel zoom  ·  middle-drag orbit" },
            { "Select", "Left-click a man or banner  ·  Shift+click adds  ·  drag a box  ·  Ctrl+A all" },
            { "", "Double-click a battalion (or its card) to centre the camera on it" },
            { "Move", "Right-click the ground  ·  double right-click to run" },
            { "Line up", "Right-drag: the drag is the new front rank, its width sets the files." },
            { "", "Drag left to right to face away from you, right to left to face towards you" },
            { "Attack", "Right-click an enemy man or banner  ·  double right-click to charge at a run" },
            { "Formation", "1 Line   2 Deep   3 Thin   4 Column   5 Wedge   6 Square   7 Loose" },
            { "Orders", "H / Backspace halt  ·  R run or walk" },
            { "Reserve", "Right-click a friendly battalion: the selected one becomes its reserve and follows it" },
            { "", "It relieves the front on its own when the front is spent, or at once with the Relieve button" },
            { "Soldiers", "Click any man, friend or foe, to inspect him  ·  F follow him with the camera" },
            { "Time", "Space pause  ·  + / - (or . and ,) speed, 0.1× to 4×" },
            { "Battle", "P autopilot  ·  F5 back to setup  ·  Enter back to setup once it is over" },
            { "", "Esc deselect and close the inspector  ·  F1 show or hide this" },
        };
        for (int r = 0; r < rows.GetLength(0); r++)
        {
            grid.AddChild(Lbl(rows[r, 0], 14, Gold, true));
            grid.AddChild(Lbl(rows[r, 1], 14, Text));
        }
        v.AddChild(grid);
        var foot = Lbl("Every man fights his own duel: blocks, parries, staggers, knockdowns. Hit them where their shields aren't.", 13, Dim);
        foot.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(foot);
        _root.AddChild(_help);
        _help.Visible = false;
    }

    void BuildEndBanner()
    {
        _end = MakePanel(0.9f, 14, 28, 20);
        _end.MouseFilter = Control.MouseFilterEnum.Stop;
        Place(_end, 0.5f, 0.42f, Vector2.Zero, Control.GrowDirection.Both, Control.GrowDirection.Both);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        _end.AddChild(v);
        _endTitle = Lbl("VICTORY", 46, Gold, true);
        _endTitle.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_endTitle);
        _endSub = Lbl("", 16, Text);
        _endSub.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_endSub);
        _endCasualties = Lbl("", 14, Dim);
        _endCasualties.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_endCasualties);
        v.AddChild(new HSeparator());
        var heroes = Lbl("HEROES OF THE BATTLE", 15, Gold, true);
        heroes.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(heroes);
        _heroGrid = new GridContainer { Columns = 4, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        _heroGrid.AddThemeConstantOverride("h_separation", 16);
        _heroGrid.AddThemeConstantOverride("v_separation", 4);
        v.AddChild(_heroGrid);
        v.AddChild(new HSeparator());
        var again = MakeButton("Enter  —  set up the next battle", () => _main.NewBattle());
        again.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        again.CustomMinimumSize = new Vector2(220, 34);
        v.AddChild(again);
        _root.AddChild(_end);
        _end.Visible = false;
    }

    void BuildTooltip()
    {
        _tip = MakePanel(0.88f, 6, 9, 6);
        _tip.MouseFilter = Control.MouseFilterEnum.Ignore;
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 1);
        _tip.AddChild(v);
        _tipTitle = Lbl("", 14, Text, true);
        _tipBody = Lbl("", 12, Dim);
        v.AddChild(_tipTitle);
        v.AddChild(_tipBody);
        _root.AddChild(_tip);
        _tip.Visible = false;
    }

    // ================================================================ battle lifecycle

    public void Bind(Battle battle, bool showHelp, bool pinHelp)
    {
        _battle = battle;
        _startMen[0] = _startMen[1] = 0;
        foreach (var u in battle.Battalions) _startMen[u.Team] += u.InitialCount;

        foreach (var c in _cards) c.Panel.QueueFree();
        _cards.Clear();
        foreach (var u in battle.Battalions)
        {
            if (u.Team != 0) continue;
            var card = MakeCard(u);
            _cards.Add(card);
            _cardRow.AddChild(card.Panel);
        }
        _cardLayoutKey = new Vector2I(-1, -1);
        LayoutCards();
        foreach (var c in _cards) RefreshCard(c, true);   // badges now, so OpenBand sees the cards' real height
        foreach (var (label, _) in _msgs) label.QueueFree();
        _msgs.Clear();
        _end.Visible = false;
        _helpVisible = showHelp;
        _helpHideAtMs = showHelp && !pinHelp ? Time.GetTicksMsec() + 10000 : 0;
    }

    public void ToggleHelp()
    {
        _helpVisible = !_helpVisible;
        _helpHideAtMs = 0;
    }

    public void HideHelp()
    {
        _helpVisible = false;
        _helpHideAtMs = 0;
    }

    public bool HelpVisible => _helpVisible;

    /// <summary>Screen rectangles of HUD controls, for the self-test's synthetic clicks.</summary>
    public Rect2 CardRect(int i) => i >= 0 && i < _cards.Count ? _cards[i].Panel.GetGlobalRect() : default;

    public int CardCount => _cards.Count;

    /// <summary>
    /// The screen band (top y, bottom y) left clear between the top bar and the battalion cards. Valid straight after
    /// <see cref="Bind"/>, before the first layout pass: Main frames the start camera in it.
    /// </summary>
    public Vector2 OpenBand()
    {
        float cardH = 0f;
        foreach (var c in _cards) cardH = Mathf.Max(cardH, c.Panel.GetCombinedMinimumSize().Y);
        float top = 10 + _topBar.GetCombinedMinimumSize().Y;
        float bottom = _root.GetViewportRect().Size.Y - BottomMargin - (_cardRows > 0 ? _cardRows * (cardH + CardGap) - CardGap : 0f);
        return new Vector2(top, bottom);
    }

    public Rect2 OrdersRect => _cmd.GetGlobalRect();

    public bool OrdersVisible => _cmd.Visible;

    public Rect2 FormationButtonRect(int i) => i >= 0 && i < _formButtons.Count ? _formButtons[i].GetGlobalRect() : default;

    public Rect2 RelieveButtonRect => _relieveButton.GetGlobalRect();

    public bool RelieveEnabled => _cmd.Visible && !_relieveButton.Disabled;

    /// <summary>The reserve badge on card i as shown ("" when hidden).</summary>
    public string CardLinkText(int i) => i >= 0 && i < _cards.Count && _cards[i].LinkBadge.Visible ? _cards[i].Link.Text : "";

    /// <summary>Call before the view drains the events: routs and rallies become messages.</summary>
    public void CollectEvents(Battle b)
    {
        foreach (var e in b.Events)
        {
            if (e.Unit == null) continue;
            if (e.Kind == EventKind.Rout)
                Message(e.Unit.Shattered ? $"{e.Unit.Name}: broken, they flee the field!" : $"{e.Unit.Name}: routing!",
                    e.Unit.Team == 0 ? Bad : Gold);
            else if (e.Kind == EventKind.Rally)
                Message($"{e.Unit.Name}: rallied", e.Unit.Team == 0 ? new Color(0.5f, 0.9f, 0.6f) : Warn);
        }
    }

    public void Message(string text, Color color)
    {
        var l = Lbl(text, 16, color, true);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        l.AddThemeConstantOverride("outline_size", 5);
        _msgBox.AddChild(l);
        _msgs.Add((l, Time.GetTicksMsec()));
        while (_msgs.Count > 4)
        {
            _msgs[0].label.QueueFree();
            _msgs.RemoveAt(0);
        }
    }

    // ================================================================ per frame

    public void Refresh()
    {
        if (_battle == null) return;
        ulong now = Time.GetTicksMsec();
        bool blink = now / 280 % 2 == 0;

        RefreshTopBar();
        LayoutCards();
        foreach (var c in _cards) RefreshCard(c, blink);
        RefreshCommandPanel();
        RefreshInspector();
        RefreshTooltip();

        if (_helpHideAtMs > 0 && now > _helpHideAtMs) { _helpVisible = false; _helpHideAtMs = 0; }
        _help.Visible = _helpVisible;
        _hint.Visible = !_helpVisible;

        _box.Visible = P.Boxing;
        if (P.Boxing)
        {
            var r = P.BoxRect;
            _box.Position = r.Position;
            _box.Size = r.Size;
        }

        for (int i = _msgs.Count - 1; i >= 0; i--)
        {
            float age = (now - _msgs[i].born) / 1000f;
            if (age > 6f) { _msgs[i].label.QueueFree(); _msgs.RemoveAt(i); continue; }
            _msgs[i].label.Modulate = new Color(1, 1, 1, Mathf.Clamp(6f - age, 0f, 1f));
        }

        RefreshEndBanner();
    }

    void RefreshTopBar()
    {
        for (int t = 0; t < 2; t++)
        {
            int men = _battle.Strength(t), start = Math.Max(1, _startMen[t]);
            _armyBar[t].Set(men / (float)start, TeamColor[t]);
            _armyCount[t].Text = $"{men}/{_startMen[t]}";
            int dead = _battle.Dead(t), fled = 0, running = 0;
            foreach (var s in _battle.Soldiers)
            {
                if (s.Team != t) continue;
                if (s.Escaped) fled++;
                else if (s.Active && s.Fleeing) running++;
            }
            string extra = (running > 0 ? $"  ·  {running} running" : "") + (fled > 0 ? $"  ·  {fled} fled" : "");
            _armyDead[t].Text = $"{dead} dead{extra}";
        }
        int secs = (int)_battle.Time;
        _clock.Text = $"{secs / 60:00}:{secs % 60:00}";
        _speed.Text = FormatSpeed(_main.TimeScale);
        _speed.AddThemeColorOverride("font_color", Mathf.IsEqualApprox(_main.TimeScale, 1f) ? Text : _main.TimeScale < 1f ? new Color(0.6f, 0.85f, 1f) : Warn);
        _pausedBadge.Visible = _main.Paused;
        _autoBadge.Visible = _main.Autopilot;
        var f = _main.Camera.Follow;
        _follow.Text = f != null ? $"following {f.Name}  (F to stop)" : "";
        _follow.Visible = f != null;
    }

    public static string FormatSpeed(float s) => (s < 1f ? s.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : s.ToString("0", System.Globalization.CultureInfo.InvariantCulture)) + "×";

    /// <summary>
    /// Fits the cards between the left edge and the orders panel (see <see cref="CardWidth"/>): one row at up to full
    /// width, else two rows. The panel's room is kept even while it is hidden, so selecting doesn't shuffle the cards.
    /// Redone only when the viewport width or the number of cards changes.
    /// </summary>
    void LayoutCards()
    {
        var key = new Vector2I((int)_root.GetViewportRect().Size.X, _cards.Count);
        if (key == _cardLayoutKey) return;
        _cardLayoutKey = key;
        _cardRows = 0;
        if (_cards.Count == 0) return;
        float right = _cmd.GetCombinedMinimumSize().X + 12 + CardGap;
        _cardRow.OffsetRight = -right;
        int n = _cards.Count;
        float room = key.X - 12 - right;
        float w = (room - (n - 1) * CardGap) / n;
        if (w < CardOneRowMin)
        {
            int perRow = (n + 1) / 2;
            w = (room - (perRow - 1) * CardGap) / perRow;
        }
        w = Mathf.Floor(Mathf.Clamp(w, CardMinWidth, CardWidth));
        foreach (var c in _cards) c.Panel.CustomMinimumSize = new Vector2(w, 0);
        int fit = Math.Max(1, (int)((room + CardGap) / (w + CardGap)));
        _cardRows = (n + fit - 1) / fit;
    }

    void RefreshCard(UnitCard c, bool blink)
    {
        var u = c.Unit;
        c.Men.Text = $"{u.Alive}/{u.InitialCount}";
        string status = u.Status;
        c.Status.Text = status;
        Color sc = u.Routing ? Bad : u.Wavering ? Warn : u.Destroyed ? Dim : u.InMelee ? Gold : Text;
        c.Status.AddThemeColorOverride("font_color", sc);
        c.Form.Text = u.Destroyed ? "" : u.Routing ? Formation.Label(u.Formation) :
            $"{Formation.Label(u.Formation)}{(Formation.UsesFiles(u.Formation) ? $", {Math.Min(u.Files, Math.Max(1, u.Formed))} wide" : "")}{(u.Running && u.Order != OrderType.Hold ? "  ·  running" : "")}";
        string link = LinkText(u);
        if (c.Link.Text != link) c.Link.Text = link;
        c.LinkBadge.Visible = link != "";
        var mc = MoraleColor(u.Morale);
        if ((u.Wavering || u.Routing) && !blink) mc = mc.Darkened(0.55f);
        c.Morale.Set(u.Morale / 100f, mc);
        c.Stamina.Set(u.AvgStamina, StaminaColor);

        int look = P.IsSelected(u) ? 2 : P.HoveredUnit == u ? 1 : 0;
        if (look != c.Look)
        {
            c.Look = look;
            c.Panel.AddThemeStyleboxOverride("panel", look == 2 ? _cardSelectedStyle : look == 1 ? _cardHoverStyle : _cardStyle);
        }
        c.Panel.Modulate = u.Destroyed ? new Color(1, 1, 1, 0.45f) : Colors.White;
    }

    /// <summary>"Reserve for X" and/or "Relieved by Y", one per line; "" if the battalion is in no reserve pair.</summary>
    public static string LinkText(Battalion u)
    {
        if (u.Destroyed) return "";
        string a = u.ReserveFor != null ? $"Reserve for {u.ReserveFor.Name}" : "";
        string b = u.ReservedBy != null ? $"Relieved by {u.ReservedBy.Name}" : "";
        return a != "" && b != "" ? a + "\n" + b : a + b;
    }

    public static Color MoraleColor(float morale)
    {
        float t = Mathf.Clamp((morale - 20f) / 55f, 0f, 1f);
        return Color.FromHsv(t * 0.33f, 0.78f, 0.92f);
    }

    void RefreshCommandPanel()
    {
        var units = P.Orderable();
        _cmd.Visible = units.Count > 0;
        if (units.Count == 0) return;
        _cmdTitle.Text = units.Count == 1 ? $"Orders  ·  {units[0].Name}" : $"Orders  ·  {units.Count} battalions";
        for (int i = 0; i < _formButtons.Count; i++)
        {
            var type = Formation.All[i];
            SetLit(_formButtons[i], units.Exists(u => u.Formation == type));
        }
        var moving = units.FindAll(u => u.Order != OrderType.Hold);
        SetLit(_runButton, moving.Count > 0 && moving.TrueForAll(u => u.Running));
        _runButton.Disabled = moving.Count == 0;
        // Relieve: a battalion with a reserve behind it calls it up; a reserve goes in for the one it backs.
        _relieveButton.Disabled = !units.Exists(u => u.ReservedBy != null || u.ReserveFor != null);
        SetLit(_relieveButton, units.Exists(u => u.FallingBack || u.Relief != ReliefPhase.None || u.ReservedBy is { Relief: not ReliefPhase.None }));
    }

    void RefreshInspector()
    {
        var s = P.Inspected;
        _insp.Visible = s != null;
        if (s == null) return;
        _iName.Text = s.Name;
        _iName.AddThemeColorOverride("font_color", s.Dead ? Dim : Text);
        _iUnit.Text = $"{s.Unit.Name}  ·  {s.Unit.Type.Name}";
        _iUnit.AddThemeColorOverride("font_color", TeamColor[s.Team]);
        _iRating.Text = $"Rating {Mathf.RoundToInt(s.Rating)}";
        _iVet.Visible = s.Veteran;

        float[] attrs =
        {
            s.Skill, Mult(s.Strength), Mult(s.Agility), Mult(s.Endurance), s.Nerve,
        };
        for (int i = 0; i < 5; i++)
        {
            float a = Mathf.Clamp(attrs[i], 0f, 1f);
            _iAttr[i].Set(a, new Color(0.45f, 0.55f, 0.68f).Lerp(Gold, a));
            _iAttrVal[i].Text = Mathf.RoundToInt(a * 100f).ToString();
        }

        _iHp.Set(s.Dead ? 0f : s.HpFrac, s.HpFrac > 0.5f ? new Color(0.4f, 0.82f, 0.4f) : s.HpFrac > 0.25f ? Warn : Bad);
        _iHpText.Text = $"{Hp(s)} / {Mathf.RoundToInt(s.MaxHp)}";
        _iSta.Set(s.StaminaFrac, StaminaColor);
        _iStaText.Text = $"{Mathf.RoundToInt(s.Stamina)} / {Mathf.RoundToInt(s.MaxStamina)}";

        _iState.Text = s.StateText;
        _iState.AddThemeColorOverride("font_color", s.Dead ? Bad : s.Fleeing ? Warn : s.Phase is CombatPhase.Stagger or CombatPhase.Down ? Warn : s.Target != null ? Gold : Text);
        var t = s.Active ? s.Target : null;
        _iOpp.Text = t != null ? $"{t.Name}\n{t.Unit.Name}  ·  skill {Mathf.RoundToInt(t.Skill * 100)}" : "—";
        _iKit.Text = $"{s.Weapon.Name}  ·  {s.Shield?.Name ?? "no shield"}  ·  {s.Armor.Name}";

        int[] rec = { s.Kills, s.HitsLanded, s.Blocks, s.Wounds, s.StrikesMade };
        for (int i = 0; i < 5; i++) _iRecord[i].Text = rec[i].ToString();

        _iKilled.Visible = s.Dead;
        _iKilled.Text = s.KilledBy != null ? $"Killed by {s.KilledBy.Name} of {s.KilledBy.Unit.Name}" : "Killed";
        _iFollow.Visible = s.Active;
        SetLit(_iFollow, _main.Camera.Follow == s);
    }

    /// <summary>Whole hit points, never showing 0 for a living man or more than his maximum.</summary>
    static int Hp(Soldier s) => s.Dead ? 0 : Math.Clamp(Mathf.CeilToInt(s.Hp), 1, Mathf.RoundToInt(s.MaxHp));

    /// <summary>Strength, agility and endurance are multipliers around 1 (about 0.7 to 1.35); show them 0..1.</summary>
    static float Mult(float v) => (v - 0.7f) / 0.65f;

    void RefreshTooltip()
    {
        var s = P.Hovered;
        var u = P.HoveredUnit;
        if (s != null)
        {
            _tipTitle.Text = s.Name + (s.Veteran ? "  (veteran)" : "");
            _tipTitle.AddThemeColorOverride("font_color", TeamColor[s.Team]);
            _tipBody.Text = $"{s.Unit.Name}\nSkill {Mathf.RoundToInt(s.Skill * 100)}%  ·  HP {Hp(s)}/{Mathf.RoundToInt(s.MaxHp)}  ·  {s.StateText}";
        }
        else if (u != null)
        {
            _tipTitle.Text = u.Name;
            _tipTitle.AddThemeColorOverride("font_color", TeamColor[u.Team]);
            string link = LinkText(u);
            _tipBody.Text = $"{u.Alive}/{u.InitialCount} men  ·  {u.Status}" + (link != "" ? "\n" + link : "");
        }
        _tip.Visible = s != null || u != null;
        if (!_tip.Visible) return;
        _tip.ResetSize();
        var vp = _root.GetViewportRect().Size;
        var pos = P.MousePosition + new Vector2(18, 20);
        var size = _tip.GetCombinedMinimumSize();
        if (pos.X + size.X > vp.X - 4) pos.X = P.MousePosition.X - size.X - 12;
        if (pos.Y + size.Y > vp.Y - 4) pos.Y = P.MousePosition.Y - size.Y - 12;
        _tip.Position = pos;
    }

    void RefreshEndBanner()
    {
        bool over = _battle.Winner >= 0;
        if (over && !_end.Visible) FillEndBanner();
        else if (over && Time.GetTicksMsec() > _nextHeroFillMs) FillHeroes();   // the pursuit can still add kills
        _end.Visible = over;
    }

    void FillEndBanner()
    {
        bool won = _battle.Winner == 0;
        _endTitle.Text = won ? "VICTORY" : "DEFEAT";
        _endTitle.AddThemeColorOverride("font_color", won ? Gold : Bad);
        int secs = (int)(_battle.EndTime >= 0 ? _battle.EndTime : _battle.Time);
        _endSub.Text = (won ? "The Gauls are broken and flee the field." : "The legion is broken.") + $"  ({secs / 60}:{secs % 60:00})";
        FillHeroes();
    }

    void FillHeroes()
    {
        _nextHeroFillMs = Time.GetTicksMsec() + 1000;
        int[] fled = new int[2];
        foreach (var s in _battle.Soldiers)
            if (s.Escaped) fled[s.Team]++;
        _endCasualties.Text = $"Rome lost {_battle.Dead(0)} dead, {fled[0]} fled   ·   the Gauls lost {_battle.Dead(1)} dead, {fled[1]} fled";

        var heroes = new List<Soldier>(_battle.Soldiers);
        heroes.Sort((a, b) => b.Kills != a.Kills ? b.Kills.CompareTo(a.Kills) : b.HitsLanded.CompareTo(a.HitsLanded));
        foreach (var child in _heroGrid.GetChildren()) child.QueueFree();
        for (int i = 0; i < Math.Min(3, heroes.Count); i++)
        {
            var h = heroes[i];
            if (h.Kills == 0) break;
            _heroGrid.AddChild(Lbl($"{i + 1}.", 15, Gold, true));
            _heroGrid.AddChild(Lbl(h.Name + (h.Dead ? "  (fell)" : ""), 15, TeamColor[h.Team], true));
            _heroGrid.AddChild(Lbl(h.Unit.Name, 13, Dim));
            _heroGrid.AddChild(Lbl($"{h.Kills} {(h.Kills == 1 ? "kill" : "kills")}, {h.HitsLanded} hits", 14, Text));
        }
    }

    // ================================================================ widgets

    public static void Place(Control c, float ax, float ay, Vector2 offset, Control.GrowDirection gh, Control.GrowDirection gv)
    {
        c.AnchorLeft = c.AnchorRight = ax;
        c.AnchorTop = c.AnchorBottom = ay;
        c.OffsetLeft = c.OffsetRight = offset.X;
        c.OffsetTop = c.OffsetBottom = offset.Y;
        c.GrowHorizontal = gh;
        c.GrowVertical = gv;
    }

    // Public: the setup screen (SetupScreen.cs) reuses these so it matches the Hud's look.
    public static StyleBoxFlat PanelStyle(float alpha, int radius, int padX, int padY)
    {
        var s = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.075f, 0.09f, alpha),
            BorderColor = new Color(1f, 1f, 1f, 0.07f),
            ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY,
            AntiAliasing = true,
        };
        s.SetCornerRadiusAll(radius);
        s.SetBorderWidthAll(1);
        return s;
    }

    public static PanelContainer MakePanel(float alpha, int radius, int padX, int padY)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        p.AddThemeStyleboxOverride("panel", PanelStyle(alpha, radius, padX, padY));
        return p;
    }

    PanelContainer Badge(string text, Color bg, Color fg)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var s = new StyleBoxFlat { BgColor = bg, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 1, ContentMarginBottom = 1 };
        s.SetCornerRadiusAll(4);
        p.AddThemeStyleboxOverride("panel", s);
        p.AddChild(Lbl(text, 13, fg, true));
        return p;
    }

    Label Lbl(string text, int size, Color color, bool bold = false, float minWidth = 0f)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        if (bold) l.AddThemeFontOverride("font", _bold);
        if (minWidth > 0f) l.CustomMinimumSize = new Vector2(minWidth, 0);
        return l;
    }

    static Label Clipped(Label l)
    {
        l.ClipText = true;
        l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return l;
    }

    Control MeterRow(string name, Bar bar, float labelWidth)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(Lbl(name, 11, Dim, false, labelWidth));
        row.AddChild(bar);
        return row;
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
        _btnPressed = B(new Color(0.32f, 0.3f, 0.22f, 1f), Gold);
        _btnLit = B(new Color(0.36f, 0.28f, 0.08f, 0.98f), Gold);
        _btnLitHover = B(new Color(0.45f, 0.36f, 0.12f, 1f), Gold);
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
        b.AddThemeColorOverride("font_color", Text);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeColorOverride("font_disabled_color", new Color(Dim, 0.5f));
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
