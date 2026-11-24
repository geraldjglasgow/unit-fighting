using System;
using System.Collections.Generic;
using Godot;
using UnitFighting.Sim;
using UnitFighting.View;

namespace UnitFighting.Game;

/// <summary>
/// Total War style mouse and keyboard command. Presses arrive through _UnhandledInput, so the HUD gets first
/// dibs on them; the release that ends a gesture is caught in _Input wherever the mouse is by then.
/// Picking is in screen space: the nearest man's chest (or a banner) under the cursor.
/// </summary>
public partial class PlayerInput : Node
{
    public const float SoldierPickPx = 16f;
    public const float BannerPickPx = 40f;
    public const float BoxThresholdPx = 6f;
    public const float LineDragMinMetres = 2f;
    public const ulong DoubleClickMs = 350;
    public const float ChestHeight = 1.2f;

    public static readonly Color PreviewColor = new(0.45f, 0.85f, 1f, 0.75f);

    public readonly List<Battalion> Selected = new();
    public Soldier Inspected, Hovered;
    public Battalion HoveredUnit;

    /// <summary>Set by the self-test, whose synthetic presses the hardware never saw.</summary>
    public bool Synthetic;

    Main _main;
    Battle _battle;
    BattleView _view;
    RtsCamera Cam => _main.Camera;
    int Team => _view?.PlayerTeam ?? 0;

    Vector2 _mouse;

    bool _lmbDown, _boxing, _lmbShift;
    Vector2 _lmbStart;
    ulong _lastLmbMs;
    Battalion _lastLmbUnit;

    bool _rmbDown, _lineDragging;
    Vector2 _rmbStartScreen;
    Vector2? _rmbStartGround;
    ulong _lastRmbMs;
    Vector2 _lastRmbScreen;
    Battalion _lastAttackTarget;
    readonly List<Placement> _plan = new(), _lastPlan = new();
    readonly List<Vector2> _previewPts = new();
    ulong _flashUntilMs;

    public Vector2 MousePosition => _mouse;
    public bool Boxing => _boxing;
    public Rect2 BoxRect => new Rect2(_lmbStart, _mouse - _lmbStart).Abs();
    public bool LineDragging => _lineDragging;
    public IReadOnlyList<Placement> CurrentPlan => _plan;

    public void Init(Main main) => _main = main;

    public void Bind(Battle battle, BattleView view)
    {
        _battle = battle;
        _view = view;
        Selected.Clear();
        Inspected = Hovered = null;
        HoveredUnit = null;
        _lmbDown = _boxing = _rmbDown = _lineDragging = false;
        _lastLmbUnit = _lastAttackTarget = null;
        _lastPlan.Clear();
        _plan.Clear();
        _flashUntilMs = 0;
    }

    public bool IsSelected(Battalion u) => Selected.Contains(u);

    /// <summary>Selected battalions the player may order: his own, still standing and not running away.</summary>
    public List<Battalion> Orderable()
    {
        var list = new List<Battalion>();
        foreach (var u in Selected)
            if (u.Team == Team && !u.Destroyed && !u.Routing) list.Add(u);
        return list;
    }

    // ---------------------------------------------------------------- per frame

    public void Tick()
    {
        if (_battle == null) return;
        Selected.RemoveAll(u => u.Destroyed);
        if (Inspected != null && _main.Camera.Follow == Inspected && !Inspected.Active) _main.Camera.Follow = null;

        if (!Synthetic)
        {
            // A release we never saw (focus lost mid-drag): drop the gesture.
            if (_lmbDown && !Input.IsMouseButtonPressed(MouseButton.Left)) { _lmbDown = _boxing = false; }
            if (_rmbDown && !Input.IsMouseButtonPressed(MouseButton.Right)) CancelRightDrag();
        }

        if (_lmbDown && !_boxing && _mouse.DistanceTo(_lmbStart) > BoxThresholdPx) _boxing = true;
        if (_rmbDown) UpdateLineDrag(_mouse);

        bool overUi = GetViewport().GuiGetHoveredControl() != null;
        if (overUi || _boxing || _lineDragging)
        {
            Hovered = null;
            HoveredUnit = null;
        }
        else
        {
            Hovered = PickSoldier(_mouse, -1);
            HoveredUnit = Hovered?.Unit ?? PickBanner(_mouse, -1);
        }

        if (_flashUntilMs > 0 && Time.GetTicksMsec() > _flashUntilMs && !_lineDragging)
        {
            _flashUntilMs = 0;
            _view.ClearPreview();
        }

        _view.Hovered = Hovered;
        _view.Inspected = Inspected;
        _view.HoveredUnit = HoveredUnit;
    }

    // ---------------------------------------------------------------- input events

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouse m) _mouse = m.Position;
        if (e is InputEventMouseButton { Pressed: false } mb)
        {
            if (mb.ButtonIndex == MouseButton.Left && _lmbDown)
            {
                FinishLeft(mb.Position);
                GetViewport().SetInputAsHandled();
            }
            else if (mb.ButtonIndex == MouseButton.Right && _rmbDown)
            {
                FinishRight(mb.Position);
                GetViewport().SetInputAsHandled();
            }
        }
        else if (e is InputEventMouseMotion && _rmbDown)
        {
            UpdateLineDrag(_mouse);
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_battle == null) return;
        if (e is InputEventMouseButton { Pressed: true } mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                _lmbDown = true;
                _boxing = false;
                _lmbStart = _mouse = mb.Position;
                _lmbShift = mb.ShiftPressed;
                GetViewport().SetInputAsHandled();
            }
            else if (mb.ButtonIndex == MouseButton.Right)
            {
                _rmbDown = true;
                _lineDragging = false;
                _rmbStartScreen = _mouse = mb.Position;
                _rmbStartGround = GroundAt(mb.Position);
                GetViewport().SetInputAsHandled();
            }
        }
        else if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (HandleKey(k.Keycode, k.ShiftPressed, k.CtrlPressed || k.MetaPressed))
                GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>All keyboard commands. Never binds W A S D, arrows, Q or E: those belong to the camera.</summary>
    public bool HandleKey(Key key, bool shift, bool ctrl)
    {
        int formation = key switch
        {
            >= Key.Key1 and <= Key.Key7 => (int)(key - Key.Key1),
            >= Key.Kp1 and <= Key.Kp7 => (int)(key - Key.Kp1),
            _ => -1,
        };
        if (formation >= 0 && !ctrl)
        {
            SetFormation(Formation.All[formation]);
            return true;
        }
        switch (key)
        {
            case Key.H or Key.Backspace: Halt(); return true;
            case Key.R when !ctrl: ToggleRun(); return true;
            case Key.Space: _main.TogglePause(); return true;
            case Key.Plus or Key.Equal or Key.KpAdd or Key.Period: _main.ChangeSpeed(+1); return true;
            case Key.Minus or Key.KpSubtract or Key.Comma: _main.ChangeSpeed(-1); return true;
            case Key.A when ctrl: SelectAll(); return true;
            case Key.Escape:
                Selected.Clear();
                Inspected = null;
                Cam.Follow = null;
                _main.Hud.HideHelp();
                return true;
            case Key.F when !ctrl: ToggleFollow(); return true;
            case Key.F1: _main.Hud.ToggleHelp(); return true;
            case Key.P when !ctrl: _main.SetAutopilot(!_main.Autopilot); return true;
            case Key.F5: _main.NewBattle(); return true;
            case Key.Enter or Key.KpEnter:
                if (_battle.Winner < 0) return false;
                _main.NewBattle();
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- left button: selection

    void FinishLeft(Vector2 pos)
    {
        _lmbDown = false;
        _mouse = pos;
        if (_boxing || pos.DistanceTo(_lmbStart) > BoxThresholdPx)
        {
            _boxing = false;
            BoxSelect(new Rect2(_lmbStart, pos - _lmbStart).Abs(), _lmbShift);
            return;
        }
        ClickSelect(pos, _lmbShift);
    }

    public void ClickSelect(Vector2 pos, bool shift)
    {
        var s = PickSoldier(pos, -1);
        var u = s?.Unit ?? PickBanner(pos, -1);
        if (u == null)
        {
            if (!shift) Selected.Clear();
            _lastLmbUnit = null;
            return;
        }
        if (s != null) Inspected = s;
        SelectUnit(u, shift);

        ulong now = Time.GetTicksMsec();
        if (!shift && u == _lastLmbUnit && now - _lastLmbMs < DoubleClickMs)
        {
            FocusUnit(u);
            _lastLmbUnit = null;
        }
        else
        {
            _lastLmbUnit = u;
            _lastLmbMs = now;
        }
    }

    /// <summary>Plain: select just this one. Shift: toggle it in the selection.</summary>
    public void SelectUnit(Battalion u, bool shift)
    {
        if (u == null) return;
        if (shift)
        {
            if (!Selected.Remove(u)) Selected.Add(u);
        }
        else
        {
            Selected.Clear();
            Selected.Add(u);
        }
    }

    void BoxSelect(Rect2 rect, bool shift)
    {
        if (!shift) Selected.Clear();
        foreach (var u in _battle.Battalions)
        {
            if (u.Team != Team || u.Destroyed || Selected.Contains(u)) continue;
            bool inside = false;
            foreach (var s in u.Soldiers)
            {
                if (!s.Active) continue;
                if (ScreenPos(ChestOf(s), out var sp) && rect.HasPoint(sp)) { inside = true; break; }
            }
            if (!inside && BannerScreen(u, out var bp) && rect.HasPoint(bp)) inside = true;
            if (inside) Selected.Add(u);
        }
    }

    public void SelectAll()
    {
        Selected.Clear();
        foreach (var u in _battle.Battalions)
            if (u.Team == Team && !u.Destroyed) Selected.Add(u);
    }

    public void FocusUnit(Battalion u)
    {
        if (u == null) return;
        Cam.FocusOn(new Vector3(u.Centroid.X, 0f, u.Centroid.Y));
    }

    public void ToggleFollow()
    {
        if (Cam.Follow != null) Cam.Follow = null;
        else if (Inspected is { Active: true }) Cam.Follow = Inspected;
    }

    // ---------------------------------------------------------------- right button: orders

    void UpdateLineDrag(Vector2 screen)
    {
        if (_rmbStartGround is not { } a) return;
        var units = Orderable();
        if (units.Count == 0) return;
        var g = GroundAt(screen);
        if (g is not { } b) return;
        if (!_lineDragging && a.DistanceTo(b) <= LineDragMinMetres) return;
        _lineDragging = true;
        OrderPlanner.Line(units, a, b, _plan);
        OrderPlanner.Preview(_plan, _previewPts);
        _view.SetPreview(_previewPts, PreviewColor);
        _flashUntilMs = 0;
    }

    void CancelRightDrag()
    {
        _rmbDown = false;
        if (_lineDragging) _view.ClearPreview();
        _lineDragging = false;
    }

    void FinishRight(Vector2 pos)
    {
        _rmbDown = false;
        _mouse = pos;
        var units = Orderable();
        ulong now = Time.GetTicksMsec();

        if (_lineDragging)
        {
            UpdateLineDrag(pos);   // the plan as of the release point
            _lineDragging = false;
            _view.ClearPreview();
            if (units.Count > 0 && _plan.Count > 0)
            {
                Apply(_plan, false);
                _lastPlan.Clear();
                _lastPlan.AddRange(_plan);
            }
            _lastRmbMs = 0;
            _lastAttackTarget = null;
            return;
        }
        if (units.Count == 0) return;

        bool dbl = now - _lastRmbMs < DoubleClickMs && pos.DistanceTo(_lastRmbScreen) < 24f;
        _lastRmbMs = now;
        _lastRmbScreen = pos;

        var enemy = PickSoldier(pos, 1 - Team)?.Unit ?? PickBanner(pos, 1 - Team);
        if (enemy != null && !enemy.Destroyed)
        {
            IssueAttack(units, enemy, dbl && enemy == _lastAttackTarget);
            _lastAttackTarget = enemy;
            _lastPlan.Clear();
            return;
        }
        _lastAttackTarget = null;

        // A friendly battalion under the cursor: the selected battalion becomes its reserve.
        var friend = PickSoldier(pos, Team)?.Unit ?? PickBanner(pos, Team);
        if (friend != null && units.Count == 1 && friend != units[0] && !friend.Destroyed && !friend.Routing)
        {
            MakeReserve(units[0], friend);
            _lastPlan.Clear();
            return;
        }

        if (dbl && _lastPlan.Count > 0)
        {
            Apply(_lastPlan, true);   // the same orders again, at the double
            return;
        }
        if (GroundAt(pos) is not { } dest) return;
        OrderPlanner.Move(units, dest, _plan);
        Apply(_plan, false);
        _lastPlan.Clear();
        _lastPlan.AddRange(_plan);
        // A brief flash of where they are going.
        OrderPlanner.Preview(_plan, _previewPts);
        _view.SetPreview(_previewPts, PreviewColor);
        _flashUntilMs = Time.GetTicksMsec() + 600;
    }

    void Apply(List<Placement> plan, bool run)
    {
        OrderPlanner.Apply(plan, run);
        foreach (var p in plan) _main.PlayerOrdered(p.Unit);
    }

    public void IssueAttack(List<Battalion> units, Battalion target, bool run)
    {
        foreach (var u in units)
        {
            u.OrderAttack(target, run);
            _main.PlayerOrdered(u);
        }
    }

    /// <summary>Make <paramref name="reserve"/> the reserve behind <paramref name="front"/>: it follows it and relieves it when it is spent.</summary>
    public void MakeReserve(Battalion reserve, Battalion front)
    {
        _battle.AssignReserve(reserve, front);
        if (reserve.ReserveFor == front) _main.Hud.Message($"{reserve.Name}: reserve for {front.Name}", Hud.ReserveColor);
    }

    /// <summary>
    /// Relieve now: a selected battalion with a reserve behind it has the reserve take over its line; a selected
    /// reserve goes in and relieves the battalion it backs.
    /// </summary>
    public void Relieve()
    {
        foreach (var u in Orderable())
        {
            var front = u.ReserveFor ?? u;
            if (front.ReservedBy != null) _battle.RelieveNow(front);
        }
    }

    public void SetFormation(FormationType t)
    {
        foreach (var u in Orderable()) u.SetFormation(t);
    }

    public void Halt()
    {
        foreach (var u in Orderable())
        {
            u.OrderHalt();
            _main.PlayerOrdered(u);
        }
    }

    public void ToggleRun()
    {
        var moving = Orderable().FindAll(u => u.Order != OrderType.Hold);
        bool run = moving.Exists(u => !u.Running);
        foreach (var u in moving) u.SetRunning(run);
    }

    // ---------------------------------------------------------------- picking

    public static Vector3 ChestOf(Soldier s) => new(s.Pos.X, ChestHeight, s.Pos.Y);

    bool ScreenPos(Vector3 world, out Vector2 screen)
    {
        screen = default;
        if (Cam == null || !Cam.IsInsideTree() || Cam.IsPositionBehind(world)) return false;
        screen = Cam.UnprojectPosition(world);
        return true;
    }

    bool BannerScreen(Battalion u, out Vector2 screen)
    {
        screen = default;
        if (_view == null || u.Destroyed) return false;
        var p = _view.BannerPosition(u);
        return p != Vector3.Zero && ScreenPos(p, out screen);   // Zero: no banner (yet)
    }

    /// <summary>The active man whose chest is nearest the cursor, within a few pixels. team -1 = either side.</summary>
    public Soldier PickSoldier(Vector2 pos, int team)
    {
        if (_battle == null) return null;
        Soldier best = null;
        float bestD = SoldierPickPx * SoldierPickPx;
        foreach (var s in _battle.Soldiers)
        {
            if (!s.Active || (team >= 0 && s.Team != team)) continue;
            if (!ScreenPos(ChestOf(s), out var sp)) continue;
            float d = sp.DistanceSquaredTo(pos);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    public Battalion PickBanner(Vector2 pos, int team)
    {
        if (_battle == null) return null;
        Battalion best = null;
        float bestD = BannerPickPx * BannerPickPx;
        foreach (var u in _battle.Battalions)
        {
            if (team >= 0 && u.Team != team) continue;
            if (!BannerScreen(u, out var sp)) continue;
            float d = sp.DistanceSquaredTo(pos);
            if (d < bestD) { bestD = d; best = u; }
        }
        return best;
    }

    /// <summary>Where the ray under the cursor meets the ground (y = 0), if it does.</summary>
    public Vector2? GroundAt(Vector2 screen)
    {
        if (Cam == null || !Cam.IsInsideTree()) return null;
        var o = Cam.ProjectRayOrigin(screen);
        var d = Cam.ProjectRayNormal(screen);
        if (d.Y > -1e-4f) return null;
        float t = -o.Y / d.Y;
        var p = o + d * t;
        float half = _battle?.FieldHalf ?? 150f;
        return new Vector2(Mathf.Clamp(p.X, -half, half), Mathf.Clamp(p.Z, -half, half));
    }
}
