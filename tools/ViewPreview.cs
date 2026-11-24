using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using UnitFighting.Sim;
using UnitFighting.View;

namespace UnitFighting.Tools;

/// <summary>
/// Visuals test bench, independent of the game layer: world + camera + the default battle on autopilot.
/// Fast-forwards the sim, runs a few live frames so effects exist, frames a shot and saves a screenshot.
///
///   Godot --path . res://tools/view_preview.tscn -- --screenshot=out.png --time=40 --shot-view=melee
///
/// Options: --seed=N --time=T (fast-forward seconds) --shot-view=overview|melee|close|duel|line
///          --shot-frames=N (live frames before the shot) --speed=S (sim speed during live frames, e.g. 0.1)
///          --burst=K --burst-every=N (K shots N frames apart: out_0.png, out_1.png, ...)
///          --yaw= --pitch= --dist= --target=x,z (camera overrides) --select=I (select battalion I)
///          --hover-unit=I --inspect --frames=N (quit after N frames when not taking a screenshot)
///          --scenario=default|duel|spears|chosen|big|charge|line --find=death|down|impact|press|shove|feint (replay to just before it)
///          --track (keep the camera side-on to the subject and his man as they circle)
///          --motionstats (how much the fighting men move: share of frames each is all but still)
///          --tire=F (cap every man's stamina at F, 0..1, from the first live frame on) --camlog (camera position per shot)
///          --scenario=gallery --blows=Cut,feint:Cut,... --outcome=block|parry|hit|miss (scripted blows, one at a time)
/// </summary>
public partial class ViewPreview : Node3D
{
    readonly Dictionary<string, string> _args = new();
    Battle _battle;
    BattleView _view;
    RtsCamera _cam;
    float _acc, _speed = 1f;
    int _frame, _shotFrames = 20, _burst = 1, _burstEvery = 3, _shotsTaken, _quitAfter = 240;
    string _shot;
    bool _busy;
    Soldier _subject;
    Battalion _selected;
    int _deathId = -1;
    long _syncTicks;

    public override void _Ready()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            var s = a.TrimStart('-');
            int eq = s.IndexOf('=');
            if (eq < 0) _args[s] = "1";
            else _args[s[..eq]] = s[(eq + 1)..];
        }
        _shot = Str("screenshot", null);
        _shotFrames = Int("shot-frames", 20);
        _speed = Float("speed", 1f);
        _burst = Int("burst", 1);
        _burstEvery = Int("burst-every", 3);
        _quitAfter = Int("frames", 240);
        if (_shot != null && !_shot.StartsWith("/") && !_shot.StartsWith("res://") && !_shot.StartsWith("user://"))
            _shot = System.IO.Path.GetFullPath(_shot);

        WorldView.Fog = Int("fog", 1) != 0;
        AddChild(new WorldView { Name = "World" });
        _cam = new RtsCamera { Name = "Camera" };
        AddChild(_cam);
        if (!_args.ContainsKey("camtest"))
        {
            // Keys typed into whatever window has focus would otherwise move the camera between burst frames.
            _cam.KeyboardEnabled = false;
            _cam.SetProcessUnhandledInput(false);
            _cam.SetProcessInput(false);
        }
        _cam.MakeCurrent();

        int seed = Int("seed", 7);
        float ff = Float("time", 0f);
        _deathId = -1;
        string find = _args.ContainsKey("find-death") ? "death" : Str("find", null);
        if (find != null)
        {
            // The sim is deterministic: run a scratch copy to find the next man to die (or be knocked flat, hit by a
            // charging man, or pressed shield to shield with his man), then replay the real one to just before it.
            var scratch = MakeBattle(seed);
            float when = 0f;
            while (scratch.Time < ff + 90f && _deathId < 0)
            {
                scratch.Step(Battle.FixedDt);
                if (find == "impact" && scratch.Time >= ff)
                {
                    foreach (var e in scratch.Events)
                        if (e.Kind == EventKind.Impact) { _deathId = e.Defender.Id; when = scratch.Time; break; }
                }
                scratch.Events.Clear();
                if (scratch.Time < ff || find == "impact") continue;
                foreach (var s in scratch.Soldiers)
                {
                    bool hit = find switch
                    {
                        "down" => s.Active && s.Phase == CombatPhase.Down && s.PhaseTime <= Battle.FixedDt * 1.5f,
                        "shove" => s.Active && s.LastShovedTime >= scratch.Time - Battle.FixedDt * 0.5f,
                        "feint" => s.Active && s.LastFeintBitTime >= scratch.Time - Battle.FixedDt * 0.5f,
                        "press" => Pressed(s),
                        _ => s.Dead && s.DeathTime >= ff,
                    };
                    if (hit) { _deathId = s.Id; when = find == "death" ? s.DeathTime : scratch.Time; break; }
                }
            }
            if (_deathId >= 0)
            {
                GD.Print($"[preview] next {find}: #{_deathId} at t={when:0.00}");
                ff = when - Float("lead", 0.3f);
            }
        }
        if (_args.ContainsKey("simstats")) SimStats(seed);
        _battle = MakeBattle(seed);
        int steps = Mathf.RoundToInt(ff / Battle.FixedDt);
        for (int i = 0; i < steps; i++)
        {
            SimStep();
            if (_battle.Events.Count > 1000) _battle.Events.Clear();
        }
        _battle.Events.Clear();
        _tire = Float("tire", -1f);
        if (_tire >= 0f) Tire();

        _view = new BattleView { PlayerTeam = 0 };
        AddChild(_view);
        _view.Build(_battle);
        int sel = Int("select", -1);
        if (sel >= 0 && sel < _battle.Battalions.Count) _selected = _battle.Battalions[sel];
        _view.IsSelected = u => u == _selected;
        int hov = Int("hover-unit", -1);
        if (hov >= 0 && hov < _battle.Battalions.Count) _view.HoveredUnit = _battle.Battalions[hov];

        if (_args.ContainsKey("preview") && _selected != null)
        {
            // Ghost markers where the selected battalion would stand 12 m to its front-left.
            var pts = new List<Vector2>();
            _selected.PreviewSlots(_selected.Centroid + new Vector2(-6f, 12f), _selected.Facing + 0.3f, 0, pts);
            _view.SetPreview(pts, new Color(0.5f, 1f, 0.55f, 0.8f));
        }
        if (_args.ContainsKey("hover-man"))
        {
            foreach (var s in _battle.Soldiers)
                if (s.Active && s.Unit != _selected && s.Unit.Team == 0) { _view.Hovered = s; break; }
        }
        PlaceCamera(Str("shot-view", "overview"));
        if (_args.ContainsKey("inspect")) _view.Inspected = _subject ?? PickFighter();
        GD.Print($"[preview] seed {seed}, t={_battle.Time:0.0}s, {_battle.Soldiers.Count} men, dead {_battle.Dead(0)}/{_battle.Dead(1)}, " +
                 $"camera {_cam.Target} yaw {_cam.Yaw:0.00} pitch {_cam.Pitch:0.00} dist {_cam.Distance:0.0}");
    }

    float _tire = -1f;

    /// <summary>--tire=F: nobody has more than F of his stamina (for looking at tired men without a long fight first).</summary>
    void Tire()
    {
        foreach (var s in _battle.Soldiers) s.Stamina = Mathf.Min(s.Stamina, _tire * s.MaxStamina);
    }

    /// <summary>Two shieldmen squared up to each other, chest to chest (for checking that their shields don't meet).</summary>
    static bool Pressed(Soldier s)
    {
        var t = s.Target;
        if (!s.Active || t == null || !t.Active || t.Target != s || s.Shield == null || t.Shield == null) return false;
        if (s.Phase == CombatPhase.Down || t.Phase == CombatPhase.Down || s.Pos.DistanceTo(t.Pos) > 0.78f) return false;
        return Mathf.Abs(MathUtil.RelativeAngle(s.Facing, t.Pos - s.Pos)) < 0.3f && Mathf.Abs(MathUtil.RelativeAngle(t.Facing, s.Pos - t.Pos)) < 0.3f;
    }

    Battle MakeBattle(int seed) => Str("scenario", "default") switch
    {
        "gallery" => Gallery(seed),
        "duel" => Duel(seed, UnitTypes.Legionaries, UnitTypes.Warband),
        "spears" => Duel(seed, UnitTypes.AuxSpearmen, UnitTypes.Axemen),
        "chosen" => Duel(seed, UnitTypes.Veterans, UnitTypes.ChosenSwordsmen),
        "big" => Big(seed, Int("copies", 4)),
        "charge" => Charge(seed),
        "line" => Line(seed),
        _ => Scenarios.Default(seed, aiForPlayer: true),
    };

    /// <summary>
    /// A holding line and an attacking one, walked into each other: the grind of a line fight, for close-ups of
    /// the front ranks. --holder= and --attacker= pick the unit types, --men= the size of each.
    /// </summary>
    Battle Line(int seed)
    {
        var battle = new Battle(seed);
        var holder = Clone(TypeNamed(Str("holder", "legionaries")));
        var attacker = Clone(TypeNamed(Str("attacker", "warband")));
        attacker.NerveMean = holder.NerveMean = 0.8f;   // a long fight, not an early rout
        holder.Size = attacker.Size = Int("men", 20);
        holder.Formation = attacker.Formation = FormationType.Line;
        var h = battle.AddBattalion(0, holder, "Holders", new Vector2(0f, 0f), 0f);
        var a = battle.AddBattalion(1, attacker, "Attackers", new Vector2(0f, Float("gap", 12f)), Mathf.Pi);
        a.OrderAttack(h, false);
        return battle;
    }

    static UnitType Clone(UnitType t) => new()
    {
        Name = t.Name, Weapon = t.Weapon, Shield = t.Shield, Armor = t.Armor, Size = t.Size,
        SkillMean = t.SkillMean, SkillSpread = t.SkillSpread, StrengthMean = t.StrengthMean, AgilityMean = t.AgilityMean,
        EnduranceMean = t.EnduranceMean, NerveMean = t.NerveMean, NerveSpread = t.NerveSpread, WalkSpeed = t.WalkSpeed, RunSpeed = t.RunSpeed,
        Formation = t.Formation, FirstNames = t.FirstNames, LastNames = t.LastNames,
    };

    static UnitType TypeNamed(string name) => name switch
    {
        "legionaries" => UnitTypes.Legionaries,
        "veterans" => UnitTypes.Veterans,
        "aux" or "spears" => UnitTypes.AuxSpearmen,
        "levy" => UnitTypes.SpearLevy,
        "chosen" => UnitTypes.ChosenSwordsmen,
        "axemen" => UnitTypes.Axemen,
        _ => UnitTypes.Warband,
    };

    /// <summary>
    /// A battalion charging a holding one at the run, for impact close-ups. --charger= and --holder= pick the
    /// unit types (warband, axemen, chosen, legionaries, veterans, aux, levy), --formation= the holders' formation
    /// (line, deep, thin, ...), --men= the size of each, --gap= the metres between them at the start.
    /// </summary>
    Battle Charge(int seed)
    {
        var battle = new Battle(seed);
        var holder = Clone(TypeNamed(Str("holder", "legionaries")));
        if (Enum.TryParse<FormationType>(Str("formation", "line"), true, out var f)) holder.Formation = f;
        var charger = Clone(TypeNamed(Str("charger", "warband")));
        charger.NerveMean = 0.95f;   // they come on whatever happens, so the impact is the story
        holder.Size = charger.Size = Int("men", 20);
        float gap = Float("gap", 30f);
        var h = battle.AddBattalion(0, holder, "Holders", new Vector2(0f, 0f), 0f);
        var c = battle.AddBattalion(1, charger, "Chargers", new Vector2(0f, gap), Mathf.Pi);
        c.OrderAttack(h, true);
        return battle;
    }

    /// <summary>The default armies several times over, side by side: a load test.</summary>
    static Battle Big(int seed, int copies)
    {
        var b = new Battle(seed);
        for (int k = 0; k < copies; k++)
        {
            float ox = (k - (copies - 1) * 0.5f) * 80f;
            b.AddBattalion(0, UnitTypes.AuxSpearmen, "I Auxilia", new Vector2(ox + 24f, -32f), 0f);
            b.AddBattalion(0, UnitTypes.Legionaries, "II Cohort", new Vector2(ox + 8f, -32f), 0f);
            b.AddBattalion(0, UnitTypes.Veterans, "I Cohort", new Vector2(ox - 8f, -32f), 0f);
            b.AddBattalion(0, UnitTypes.AuxSpearmen, "II Auxilia", new Vector2(ox - 24f, -32f), 0f);
            b.AddBattalion(1, UnitTypes.SpearLevy, "Levy", new Vector2(ox + 32f, 32f), Mathf.Pi);
            b.AddBattalion(1, UnitTypes.Warband, "Warband", new Vector2(ox + 16f, 32f), Mathf.Pi);
            b.AddBattalion(1, UnitTypes.ChosenSwordsmen, "Chosen", new Vector2(ox, 32f), Mathf.Pi);
            b.AddBattalion(1, UnitTypes.Axemen, "Axemen", new Vector2(ox - 16f, 32f), Mathf.Pi);
            b.AddBattalion(1, UnitTypes.Warband, "Warband", new Vector2(ox - 32f, 32f), Mathf.Pi);
        }
        b.AIs.Add(new BattleAI(1));
        b.AIs.Add(new BattleAI(0) { AdvanceAfter = 20f });
        return b;
    }

    /// <summary>Two little units a few paces apart, sent at each other: clean single duels for close-ups.</summary>
    Battle Duel(int seed, UnitType a, UnitType b)
    {
        UnitType Small(UnitType t)
        {
            var c = Clone(t);
            c.Size = Int("men", 2);
            c.NerveMean = 0.95f;
            c.NerveSpread = 0.02f;
            c.Formation = FormationType.Loose;
            return c;
        }
        var battle = new Battle(seed);
        float gap = Float("gap", 5f);
        var ua = battle.AddBattalion(0, Small(a), "Rome", new Vector2(0f, -gap * 0.5f), 0f);
        var ub = battle.AddBattalion(1, Small(b), "Gauls", new Vector2(0f, gap * 0.5f), Mathf.Pi);
        ua.OrderAttack(ub, false);
        ub.OrderAttack(ua, false);
        return battle;
    }

    // ---------------------------------------------------------------- gallery

    /// <summary>One scripted move for the gallery: a blow (or a feint or probe on that line) and how it ends.</summary>
    struct Move
    {
        public BlowKind Kind;
        public bool Feint, Probe;
    }

    readonly List<Move> _moves = new();
    Soldier _ga, _gb;
    Vector2 _gbHome;
    int _gMove = -1;
    float _gUntil;
    string _gOutcome;

    /// <summary>
    /// The gallery: no sim, one man (--attacker=) throws each blow in turn at another (--holder=) who stands his
    /// ground and takes it (--outcome=block|parry|hit|miss), so each blow can be looked at on its own in a burst.
    /// --blows= lists them (Cut,Overhead,Backhand,Thrust,Low,Bash; feint:Cut, probe:Thrust; none for just the guard);
    /// --gap= is how far apart they stand. Each move is 0.6 s of guard, then the windup and recovery at his tempo.
    /// </summary>
    Battle Gallery(int seed)
    {
        var battle = new Battle(seed);
        UnitType One(string name)
        {
            var t = Clone(TypeNamed(name));
            t.Size = 1;
            t.Formation = FormationType.Loose;
            return t;
        }
        var ua = battle.AddBattalion(1, One(Str("attacker", "warband")), "Gauls", new Vector2(0f, 0f), 0f);
        var ub = battle.AddBattalion(0, One(Str("holder", "legionaries")), "Rome", new Vector2(0f, Float("gap", 1.05f)), Mathf.Pi);
        _ga = ua.Soldiers[0];
        _gb = ub.Soldiers[0];
        _ga.Pos = _ga.PrevPos = Vector2.Zero;
        _gb.Pos = _gb.PrevPos = _gbHome = new Vector2(0f, Float("gap", 1.05f));
        _ga.Facing = _ga.PrevFacing = 0f;
        _gb.Facing = _gb.PrevFacing = Mathf.Pi;
        _ga.Target = _gb;
        _gb.Target = _ga;
        _ga.Skill = _gb.Skill = Float("skill", 0.6f);
        ua.Order = ub.Order = OrderType.Attack;
        _gOutcome = Str("outcome", "block");
        string list = Str("blows", "Cut,Overhead,Backhand,Thrust,Low,Bash,feint:Cut,probe:Thrust");
        if (list != "none")
            foreach (var item in list.Split(','))
            {
                var m = new Move();
                var name = item;
                if (name.StartsWith("feint:")) { m.Feint = true; name = name[6..]; }
                else if (name.StartsWith("probe:")) { m.Probe = true; name = name[6..]; }
                m.Kind = Enum.Parse<BlowKind>(name, true);
                _moves.Add(m);
            }
        return battle;
    }

    static float GalleryWindup(BlowKind k) => k switch { BlowKind.Overhead => 1.2f, BlowKind.Backhand => 0.9f, BlowKind.Thrust => 0.9f, _ => 1f };
    static float GalleryRecover(BlowKind k) => k switch { BlowKind.Overhead => 1.1f, BlowKind.Low => 1.1f, BlowKind.Thrust => 0.9f, _ => 1f };

    /// <summary>One fixed step of the gallery script, in place of Battle.Step.</summary>
    void GalleryStep()
    {
        var b = _battle;
        float dt = Battle.FixedDt;
        b.Time += dt;
        foreach (var s in b.Soldiers)
        {
            s.PrevPos = s.Pos;
            s.PrevFacing = s.Facing;
            s.PhaseTime += dt;
        }
        b.Grid.Rebuild(b.Soldiers);
        var a = _ga;
        var d = _gb;
        // The man who takes the blows is pushed back by a shove and steps back up to his place.
        var off = d.Pos - _gbHome;
        var newPos = _gbHome + off * Mathf.Exp(-2.5f * dt);
        if (b.Time - d.LastShovedTime < 0.3f) newPos += new Vector2(0f, 1.1f * dt);
        d.Vel = (newPos - d.Pos) / dt;
        d.Pos = newPos;
        if (_moves.Count == 0) return;

        float tempo = Combat.Tempo(a);
        if (_gMove < 0) { _gMove = 0; _gUntil = b.Time + 0.6f; a.Phase = CombatPhase.Ready; a.PhaseTime = 0f; }
        var m = _moves[_gMove % _moves.Count];
        switch (a.Phase)
        {
            case CombatPhase.Ready when b.Time >= _gUntil:
                a.Phase = CombatPhase.Windup;
                a.PhaseTime = 0f;
                a.PhaseLength = (m.Kind == BlowKind.Bash ? 0.34f : a.Weapon.Windup * GalleryWindup(m.Kind)) * tempo * (m.Probe ? 0.55f : 1f);
                a.Blow = m.Kind;
                a.Feint = m.Feint;
                a.Probe = m.Probe;
                a.AttackStyle = m.Kind == BlowKind.Thrust ? 1 : 0;
                a.StrikeTarget = d;
                a.LastActionTime = b.Time;
                break;
            case CombatPhase.Windup when a.Feint && a.PhaseTime >= Combat.FeintPullback * a.PhaseLength:
                a.Phase = CombatPhase.Ready;
                a.PhaseTime = 0f;
                a.PhaseLength = 0f;
                a.StrikeTarget = null;
                d.LastFeintBitTime = d.GuardLineTime = b.Time;
                d.GuardLine = a.Blow;
                NextMove();
                break;
            case CombatPhase.Windup when a.PhaseTime >= a.PhaseLength:
            {
                a.Phase = CombatPhase.Recover;
                a.PhaseTime = 0f;
                a.PhaseLength = (m.Kind == BlowKind.Bash ? 0.3f : a.Weapon.Recover * GalleryRecover(m.Kind) * (m.Probe ? 0.5f : 1f)) * tempo;
                a.StrikeTarget = null;
                a.LastStrikeTime = a.LastActionTime = b.Time;
                if (m.Kind == BlowKind.Bash)
                {
                    d.LastShovedTime = b.Time;
                    break;
                }
                switch (_gOutcome)
                {
                    case "hit":
                        d.LastHitTime = b.Time;
                        b.Emit(EventKind.Hit, a, d, 20f);
                        break;
                    case "miss":
                        b.Emit(EventKind.Miss, a, d, 0f);
                        break;
                    default:
                        bool shield = _gOutcome != "parry" && d.Shield != null;
                        d.LastBlockTime = b.Time;
                        d.LastBlockWasShield = shield;
                        d.GuardLine = a.Blow;
                        d.GuardLineTime = b.Time;
                        b.Emit(shield ? EventKind.Block : EventKind.Parry, a, d, 0f);
                        break;
                }
                break;
            }
            case CombatPhase.Recover when a.PhaseTime >= a.PhaseLength:
                a.Phase = CombatPhase.Ready;
                a.PhaseTime = 0f;
                a.PhaseLength = 0f;
                NextMove();
                break;
        }
        void NextMove()
        {
            _gMove++;
            _gUntil = b.Time + 0.6f;
        }
    }

    /// <summary>Numbers on how the men move, for spotting sim oddities the eye catches in the renders.</summary>
    void SimStats(int seed)
    {
        var b = MakeBattle(seed);
        int n = b.Soldiers.Count;
        var lastFacing = new float[n];
        var path = new float[n];
        var start = new Vector2[n];
        int bigTurns = 0, steps = 0, friendOverlap = 0, enemyOverlap = 0, samples = 0;
        int jitterMen = 0, windows = 0, targetSwitches = 0, fightingSamples = 0, idleNearEnemy = 0, withinReach = 0, readySamples = 0;
        var lastTarget = new Soldier[n];
        float maxTurn = 0f;
        for (float t = 0f; t < 90f; t += Battle.FixedDt)
        {
            for (int i = 0; i < n; i++) lastFacing[i] = b.Soldiers[i].Facing;
            b.Step(Battle.FixedDt);
            b.Events.Clear();
            steps++;
            for (int i = 0; i < n; i++)
            {
                var s = b.Soldiers[i];
                if (!s.Active) continue;
                float turn = Mathf.Abs(MathUtil.AngleDiff(lastFacing[i], s.Facing));
                maxTurn = Mathf.Max(maxTurn, turn);
                if (turn > 0.2f) bigTurns++;
                path[i] += (s.Pos - s.PrevPos).Length();
                if (s.Target != lastTarget[i] && s.Target != null && lastTarget[i] != null && lastTarget[i].Active) targetSwitches++;
                lastTarget[i] = s.Target;
                if (s.Target != null && !s.Fleeing)
                {
                    fightingSamples++;
                    float d = s.Pos.DistanceTo(s.Target.Pos);
                    if (d <= s.Weapon.Reach) withinReach++;
                    if (s.Phase == CombatPhase.Ready && d <= s.Weapon.Reach) readySamples++;
                }
            }
            if (steps % 60 == 0)
            {
                // Over the last second: men who walked a lot but got nowhere are jittering.
                for (int i = 0; i < n; i++)
                {
                    var s = b.Soldiers[i];
                    if (s.Active && path[i] > 0.8f && s.Pos.DistanceTo(start[i]) < 0.25f * path[i]) jitterMen++;
                    if (s.Active) windows++;
                    path[i] = 0f;
                    start[i] = s.Pos;
                }
                for (int i = 0; i < n; i++)
                {
                    var s = b.Soldiers[i];
                    if (!s.Active) continue;
                    if (!s.Fleeing && s.Target == null && b.EnemyWithin(s.Pos, s.Team, 2.5f)) idleNearEnemy++;
                    for (int j = i + 1; j < n; j++)
                    {
                        var o = b.Soldiers[j];
                        if (!o.Active) continue;
                        float d = s.Pos.DistanceTo(o.Pos);
                        if (o.Team == s.Team && d < 0.4f) friendOverlap++;
                        if (o.Team != s.Team && d < 0.6f) enemyOverlap++;
                    }
                    samples++;
                }
            }
            if (Mathf.Abs(t - 45f) < Battle.FixedDt * 0.5f)
                GD.Print($"[simstats] t=45: dead {b.Dead(0)}/{b.Dead(1)}, strength {b.Strength(0)}/{b.Strength(1)}");
        }
        GD.Print($"[simstats] 90 s: step turns > 0.2 rad: {bigTurns} (max single-step turn {maxTurn:0.00} rad)");
        GD.Print($"[simstats] jitter (walked > 0.8 m in a second but netted < 25%): {jitterMen} of {windows} man-seconds");
        GD.Print($"[simstats] overlaps sampled 1/s: friends < 0.4 m: {friendOverlap}, enemies < 0.6 m: {enemyOverlap} (over {samples} man-samples)");
        GD.Print($"[simstats] target switches while old target alive: {targetSwitches}; man-seconds idle with an enemy within 2.5 m: {idleNearEnemy}");
        GD.Print($"[simstats] with a target: {100f * withinReach / Mathf.Max(1, fightingSamples):0}% of the time within reach, {100f * readySamples / Mathf.Max(1, fightingSamples):0}% in reach but Ready (waiting)");
        GD.Print($"[simstats] end t={b.Time:0}: dead {b.Dead(0)}/{b.Dead(1)}, winner {b.Winner}");
    }

    SoldierPose[] _lastPose;
    float[] _stillFor, _quietFor;
    int _msFrames, _msSamples, _msSlow, _msStill, _msPauses, _msQuiets;
    double _msMotion, _msPauseTime, _msQuietTime;

    /// <summary>
    /// How alive the fighting looks, in numbers. For every man with an enemy in reach: how fast his hands, hips and
    /// shoulders move (pose space, m/s); the share of frames he is nearly still (under 0.25 m/s) or quite still
    /// (under 0.1); and his pauses, stretches of a quarter second or more quite still, the "swing... wait" the eye
    /// catches. Also his quiet spells: a second or more with nothing but the slow sway of his guard (under 0.4 m/s),
    /// no blow, beat, step or reaction. Printed every 120 frames.
    /// </summary>
    void MotionStats()
    {
        var list = _battle.Soldiers;
        _lastPose ??= new SoldierPose[list.Count];
        _stillFor ??= new float[list.Count];
        _quietFor ??= new float[list.Count];
        float dt = Battle.FixedDt * _speed;
        for (int i = 0; i < list.Count && i < _lastPose.Length; i++)
        {
            var s = list[i];
            var v = _view.ViewOf(s);
            if (v == null) continue;
            var p = v.Pose;
            var q = _lastPose[i];
            _lastPose[i] = p;
            bool fighting = s.Active && !s.Fleeing && s.Target != null && s.Pos.DistanceTo(s.Target.Pos) <= s.Weapon.Reach + 0.3f;
            if (_msFrames == 0 || !fighting)
            {
                _stillFor[i] = _quietFor[i] = 0f;
                continue;
            }
            float d = (p.HandR - q.HandR).Length() + (p.HandL - q.HandL).Length()
                      + new Vector2(p.HipX - q.HipX, p.HipZ - q.HipZ).Length()
                      + 0.3f * (Mathf.Abs(p.Twist - q.Twist) + Mathf.Abs(p.Lean - q.Lean)) + Mathf.Abs(p.Crouch - q.Crouch);
            float speed = d / dt;
            _msSamples++;
            _msMotion += speed;
            if (speed < 0.25f) _msSlow++;
            if (speed < 0.1f)
            {
                _msStill++;
                _stillFor[i] += dt;
            }
            else
            {
                if (_stillFor[i] >= 0.25f) { _msPauses++; _msPauseTime += _stillFor[i]; }
                _stillFor[i] = 0f;
            }
            if (speed < 0.4f) _quietFor[i] += dt;
            else
            {
                if (_quietFor[i] >= 1f) { _msQuiets++; _msQuietTime += _quietFor[i]; }
                _quietFor[i] = 0f;
            }
        }
        _msFrames++;
        if (_msFrames % 120 == 0 && _msSamples > 0)
        {
            float secs = _msSamples * dt;
            GD.Print($"[motion] frame {_msFrames}: {secs:0} man-seconds in reach, mean pose speed {_msMotion / _msSamples:0.00} m/s, " +
                     $"nearly still {100.0 * _msSlow / _msSamples:0.0}%, still {100.0 * _msStill / _msSamples:0.0}%, " +
                     $"pauses {_msPauses / secs * 60f:0.0}/man-min averaging {(_msPauses > 0 ? _msPauseTime / _msPauses : 0):0.00} s, " +
                     $"quiet spells {_msQuiets / secs * 60f:0.0}/man-min ({100.0 * _msQuietTime / secs:0.0}% of the time)");
        }
    }

    Vector3 _camProbe;

    /// <summary>--track: keep the camera side-on to the subject and his man as they circle (on whichever side it is already).</summary>
    void Track()
    {
        var s = _subject;
        var o = s?.Target;
        if (s == null || o == null || !s.Active || !o.Active) return;
        var mid = (s.Pos + o.Pos) * 0.5f;
        float a = MathUtil.Angle(o.Pos - s.Pos);
        float y1 = a + Mathf.Pi * 0.5f, y2 = a - Mathf.Pi * 0.5f;
        float yaw = Mathf.Abs(MathUtil.AngleDiff(_cam.Yaw, y1)) < Mathf.Abs(MathUtil.AngleDiff(_cam.Yaw, y2)) ? y1 : y2;
        _cam.Target = new Vector3(mid.X, 0f, mid.Y);
        _cam.Yaw = _cam.Yaw + MathUtil.AngleDiff(_cam.Yaw, yaw) * 0.05f;   // turn slowly, so the frames can be compared
    }

    /// <summary>Feeds the camera real input events and checks it responds as intended.</summary>
    void CamTest()
    {
        var at = new Vector2(1200f, 300f);
        switch (_frame)
        {
            case 10:
                _cam.GroundUnder(at, out _camProbe);
                GD.Print($"[camtest] start: target {_cam.Target} dist {_cam.Distance:0.0}, ground under {at} = {_camProbe}");
                for (int i = 0; i < 3; i++)
                    Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = at, Factor = 1f });
                break;
            case 70:
            {
                _cam.GroundUnder(at, out var p);
                GD.Print($"[camtest] after 3 wheel-ups: target {_cam.Target} dist {_cam.Distance:0.0}, ground under cursor now {p} (drift {p.DistanceTo(_camProbe):0.00} m)");
                Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = true });
                break;
            }
            case 100:
                Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = false });
                GD.Print($"[camtest] after 0.5 s of W: target {_cam.Target} (yaw {_cam.Yaw:0.00}: should have moved along ({Mathf.Sin(_cam.Yaw):0.00}, {Mathf.Cos(_cam.Yaw):0.00}))");
                Input.ParseInputEvent(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Middle, Relative = new Vector2(100f, 40f), Position = at });
                break;
            case 130:
                GD.Print($"[camtest] after middle-drag (100, 40): yaw {_cam.Yaw:0.00} pitch {_cam.Pitch:0.00}");
                break;
        }
    }

    string Describe(Soldier s)
    {
        if (s == null) return "";
        float t = _battle.Time;
        return $" | #{s.Id} {(s.Dead ? "DEAD" : s.Phase.ToString())} {s.PhaseTime:0.00}/{s.PhaseLength:0.00}" +
               $" blk{(t - s.LastBlockTime < 0.5f ? (s.LastBlockWasShield ? "S" : "W") + $"{t - s.LastBlockTime:0.00}" : "-")}" +
               $" hit{(t - s.LastHitTime < 0.5f ? $"{t - s.LastHitTime:0.00}" : "-")} hp{s.Hp:0} st{100f * s.StaminaFrac:0}" +
               $" {s.Blow}{(s.Feint ? " feint" : s.Probe ? " probe" : "")}" +
               (s.Target != null ? $" d{s.Pos.DistanceTo(s.Target.Pos):0.00}" : "");
    }

    string Str(string k, string def) => _args.TryGetValue(k, out var v) ? v : def;
    int Int(string k, int def) => _args.TryGetValue(k, out var v) && int.TryParse(v, out var r) ? r : def;
    float Float(string k, float def) => _args.TryGetValue(k, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : def;

    Soldier PickFighter()
    {
        // The thick of the fighting: the man with the most other fighters around him.
        Soldier best = null;
        float bestScore = float.MaxValue;
        foreach (var s in _battle.Soldiers)
        {
            if (!s.Active || s.Fleeing || s.Target == null) continue;
            int crowd = 0;
            foreach (var o in _battle.Soldiers)
                if (o != s && o.Active && !o.Fleeing && o.Target != null && o.Pos.DistanceSquaredTo(s.Pos) < 36f) crowd++;
            float score = -0.8f * crowd;
            if (s.Phase == CombatPhase.Windup) score -= 6f;
            else if (s.Phase == CombatPhase.Recover) score -= 3f;
            // A proper duel: his man is fighting him back.
            if (s.Target.Target == s) score -= 4f;
            if (s.Pos.DistanceTo(s.Target.Pos) > s.Weapon.Reach) score += 5f;
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    void PlaceCamera(string view)
    {
        float yaw = 0f, pitch = 1.05f, dist = 105f;
        var target = Vector3.Zero;
        var c0 = _battle.TeamCentroid(0);
        var c1 = _battle.TeamCentroid(1);
        if (c0.HasValue && c1.HasValue)
        {
            var mid = (c0.Value + c1.Value) * 0.5f;
            target = new Vector3(mid.X, 0f, mid.Y);
        }
        if (view is "overview" or "army")
        {
            // From behind the Roman line, looking north across the field.
            target = new Vector3(target.X, 0f, target.Z - 6f);
            yaw = 0f;
            pitch = view == "army" ? 0.42f : 0.72f;
            dist = view == "army" ? 40f : 95f;
        }
        else if (view is "melee" or "close" or "duel" or "line")
        {
            int id = _deathId >= 0 ? _deathId : Int("subject", -1);
            _subject = id >= 0 && id < _battle.Soldiers.Count ? _battle.Soldiers[id] : PickFighter();
            if (_subject != null) GD.Print($"[preview] subject #{_subject.Id} {_subject.Name} ({_subject.Unit.Name}) {_subject.Phase} vs #{_subject.Target?.Id} at {_subject.Pos:F1}");
            if (_subject != null)
            {
                var s = _subject;
                var other = s.Target ?? s;
                var mid = (s.Pos + other.Pos) * 0.5f;
                var line = other.Pos - s.Pos;
                // Side-on to the duel, so both men and their blades read in profile; for a line, from behind his shoulder
                // at an angle, so his neighbours don't hide him.
                yaw = MathUtil.Angle(line) + (view == "line" ? 0.75f : Mathf.Pi * 0.5f + (view == "melee" ? 0.35f : 0.15f));
                target = new Vector3(mid.X, 0f, mid.Y);
                pitch = view == "melee" ? 0.5f : view == "close" ? 0.34f : view == "line" ? 0.5f : 0.26f;
                dist = view == "melee" ? 12f : view == "close" ? 6.5f : view == "line" ? 7f : 5f;
                if (_args.ContainsKey("follow")) _cam.Follow = s;
            }
        }
        yaw = Float("yaw", yaw);
        pitch = Float("pitch", pitch);
        dist = Float("dist", dist);
        var t = Str("target", null);
        if (t != null)
        {
            var parts = t.Split(',');
            target = new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), 0f, float.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        _cam.SnapTo(target, yaw, pitch, dist);
    }

    void SimStep()
    {
        if (_ga != null) GalleryStep();
        else _battle.Step(Battle.FixedDt);
    }

    public override void _Process(double delta)
    {
        if (_busy) return;
        _acc += Battle.FixedDt * _speed;
        while (_acc >= Battle.FixedDt)
        {
            SimStep();
            _acc -= Battle.FixedDt;
            if (_tire >= 0f) Tire();
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _view.Sync(_acc / Battle.FixedDt, Battle.FixedDt * _speed);
        _syncTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _frame++;
        if (_args.ContainsKey("track")) Track();
        if (_args.ContainsKey("camtest")) CamTest();
        if (_args.ContainsKey("motionstats")) MotionStats();
        if (_frame % 60 == 0 && _args.ContainsKey("perf"))
        {
            double ms = _syncTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 60.0;
            GD.Print($"[perf] frame {_frame}: Sync {ms:0.00} ms avg, {Engine.GetFramesPerSecond():0} fps, " +
                     $"{_battle.Soldiers.Count} men, draw calls {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)}, " +
                     $"prims {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame) / 1000}k");
            _syncTicks = 0;
            var t = _view.Timings;
            GD.Print($"[perf]   last frame: events {t.events:0.00} pose {t.pose:0.00} upload {t.upload:0.00} overlay {t.overlay:0.00} fx {t.fx:0.00} ms");
        }

        if (_shot != null)
        {
            int due = _shotFrames + _shotsTaken * _burstEvery;
            if (_frame >= due) Capture();
        }
        else if (_frame >= _quitAfter)
        {
            GD.Print($"[preview] ran {_frame} frames to t={_battle.Time:0.0}s, dead {_battle.Dead(0)}/{_battle.Dead(1)}; quitting");
            GetTree().Quit();
        }
    }

    async void Capture()
    {
        _busy = true;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string path = _shot;
        if (_burst > 1)
        {
            var ext = System.IO.Path.GetExtension(_shot);
            path = _shot[..^ext.Length] + "_" + _shotsTaken + ext;
        }
        if (DisplayServer.GetName() != "headless")
        {
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(path);
            GD.Print($"[preview] saved {path} at t={_battle.Time:0.00}s{Describe(_subject)}{Describe(_subject?.Target)}" +
                     (_args.ContainsKey("camlog") ? $" | cam {_cam.GlobalPosition:F2}" : ""));
        }
        _shotsTaken++;
        _busy = false;
        if (_shotsTaken >= _burst) GetTree().Quit();
    }
}
