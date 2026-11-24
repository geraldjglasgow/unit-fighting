using System;
using System.Collections.Generic;
using Godot;
using UnitFighting.Sim;

namespace UnitFighting.Game;

/// <summary>
/// <c>--selftest</c>: drives the controls with synthetic mouse and key events pushed through the viewport,
/// the same path real input takes (HUD first, then the world), and checks the orders that come out.
/// Steps are spaced in real time so single clicks aren't mistaken for double clicks.
/// </summary>
public sealed class SelfTest
{
    readonly Main _m;
    readonly List<(string name, ulong delayMs, Action act)> _steps = new();
    int _next;
    ulong _dueMs;
    int _pass, _fail;

    PlayerInput P => _m.Controls;
    Battle B => _m.Battle;
    List<Battalion> Own => B.Battalions.FindAll(u => u.Team == 0);

    public SelfTest(Main m)
    {
        _m = m;
        P.Synthetic = true;
        Build();
        _dueMs = Time.GetTicksMsec() + 400;
    }

    public void Step()
    {
        if (_next >= _steps.Count || Time.GetTicksMsec() < _dueMs) return;
        var (name, _, act) = _steps[_next++];
        GD.Print($"[selftest] -- {name}");
        try { act(); }
        catch (Exception e) { Fail($"{name} threw {e}"); }
        _dueMs = Time.GetTicksMsec() + (_next < _steps.Count ? _steps[_next].delayMs : 0);
    }

    void Add(string name, Action act, ulong delayMs = 420) => _steps.Add((name, delayMs, act));

    void Build()
    {
        Add("setup: pause, player camera", () =>
        {
            if (!_m.Paused) _m.TogglePause();
            PlaceCamera();
            var vp = _m.GetViewport().GetVisibleRect().Size;
            Log($"viewport {vp}, camera at {_m.Camera.GlobalPosition}, display {DisplayServer.GetName()}");
            Check(vp.X > 100 && vp.Y > 100, "viewport has a size");
        }, 0);

        Soldier clicked = null;
        Add("LMB click a man of battalion 0", () =>
        {
            var u = Own[0];
            clicked = u.Soldiers.Find(s => s.Slot == 0) ?? u.Soldiers[0];
            Click(MouseButton.Left, Scr(PlayerInput.ChestOf(clicked)));
        });
        Add("check selection", () =>
        {
            Check(P.Selected.Count == 1 && P.Selected[0] == Own[0], $"selected = [{Names(P.Selected)}]");
            Check(P.Inspected != null && P.Inspected.Unit == Own[0], $"inspecting {P.Inspected?.Name} of {P.Inspected?.Unit.Name}");
        }, 50);

        Add("RMB line drag (10,-20) -> (-10,-20)", () =>
        {
            Press(MouseButton.Right, Ground(10, -20));
            MoveTo(Ground(3, -20));
            MoveTo(Ground(-10, -20));
        });
        Add("check preview while dragging, then release", () =>
        {
            Check(P.LineDragging && P.CurrentPlan.Count == 1, $"dragging={P.LineDragging}, plan={P.CurrentPlan.Count}");
            Release(MouseButton.Right, Ground(-10, -20));
            var u = Own[0];
            Report(u);
            var dir = MathUtil.Dir(u.DestFacing);
            Check(u.Order == OrderType.Move, "order is Move");
            Check(dir.DistanceTo(new Vector2(0, 1)) < 0.02f, $"Dir(facing) = {dir:F3} ~ (0, 1), north");
            Check(u.Dest.DistanceTo(new Vector2(0, -20)) < 0.3f, $"dest {u.Dest:F2} ~ (0, -20)");
            Check(u.Files == 20, $"files {u.Files} == 20 for a 20 m front");
        }, 60);

        Add("RMB line drag the other way faces south", () =>
        {
            Press(MouseButton.Right, Ground(-10, -20));
            MoveTo(Ground(10, -20));
        });
        Add("release", () =>
        {
            Release(MouseButton.Right, Ground(10, -20));
            var dir = MathUtil.Dir(Own[0].DestFacing);
            Check(dir.DistanceTo(new Vector2(0, -1)) < 0.02f, $"Dir(facing) = {dir:F3} ~ (0, -1)");
        }, 60);

        Add("Ctrl+A", () => Key(Godot.Key.A, ctrl: true));
        Add("check all selected", () => Check(P.Selected.Count == 4, $"selected {P.Selected.Count} == 4"), 50);

        Add("RMB line drag (20,-10) -> (-20,-10) with four battalions", () =>
        {
            Press(MouseButton.Right, Ground(20, -10));
            MoveTo(Ground(-20, -10));
        });
        Add("release and check the line", () =>
        {
            Release(MouseButton.Right, Ground(-20, -10));
            float lastX = float.MaxValue;
            bool ordered = true;
            foreach (var u in Own)   // listed left to right as the player sees them: +x to -x
            {
                Report(u);
                if (u.Dest.X > lastX) ordered = false;
                lastX = u.Dest.X;
                Check(MathUtil.Dir(u.DestFacing).DistanceTo(new Vector2(0, 1)) < 0.02f, $"{u.Name} faces north");
                float front = u.Dest.Y + u.Depth * 0.5f;
                Check(Mathf.Abs(front - (-10f)) < 0.35f, $"{u.Name} front rank at y {front:F2} ~ -10");
            }
            Check(ordered, "battalions keep their left-to-right order along the line");
            Check(Own[0].Files == 9, $"files {Own[0].Files} == 9 for a 7.75 m segment");
        }, 60);

        Add("key 2 (Deep)", () => Key(Godot.Key.Key2));
        Add("check Deep", () => Check(Own.TrueForAll(u => u.Formation == FormationType.Deep), "all Deep"), 50);
        Add("key 6 (Square)", () => Key(Godot.Key.Key6));
        Add("check Square", () => Check(Own.TrueForAll(u => u.Formation == FormationType.Square), "all Square"), 50);
        Add("key 1 (Line)", () => Key(Godot.Key.Key1));
        Add("check Line", () => Check(Own.TrueForAll(u => u.Formation == FormationType.Line), "all Line"), 50);

        Battalion enemy = null;
        Vector2 enemyScreen = default;
        Add("RMB an enemy man", () =>
        {
            enemy = B.Battalions.Find(u => u.Team == 1 && u.Name.StartsWith("Chosen"));
            var s = enemy.Soldiers.Find(x => x.Slot == 0) ?? enemy.Soldiers[0];
            enemyScreen = Scr(PlayerInput.ChestOf(s));
            Click(MouseButton.Right, enemyScreen);
        });
        Add("check attack, then RMB again (double = run)", () =>
        {
            Check(Own.TrueForAll(u => u.Order == OrderType.Attack && u.AttackTarget == enemy), $"all attack {enemy.Name}");
            Check(Own.TrueForAll(u => !u.Running), "walking");
            Click(MouseButton.Right, enemyScreen);
        }, 60);
        Add("check running", () => Check(Own.TrueForAll(u => u.Running), "double right-click: running"), 60);

        Add("H halts", () => Key(Godot.Key.H));
        Add("check halt", () => Check(Own.TrueForAll(u => u.Order == OrderType.Hold), "all Hold"), 50);

        Add("LMB battalion 1, then RMB ground 14 m to its right", () =>
        {
            var u = Own[1];
            Click(MouseButton.Left, Scr(PlayerInput.ChestOf(u.Soldiers[0])));
        });
        Add("move", () =>
        {
            var u = Own[1];
            Check(P.Selected.Count == 1 && P.Selected[0] == u, $"selected [{Names(P.Selected)}]");
            // (Straight back would be under the HUD's battalion cards, which rightly swallow the click. Well clear of
            // the next battalion's men too: a right-click on a friendly battalion makes this one its reserve.)
            Click(MouseButton.Right, Ground(u.Anchor.X - 14f, u.Anchor.Y + 6f));
        }, 60);
        float travel = MathUtil.Angle(new Vector2(-14f, 6f));
        Add("check long move faces the way of travel, then a short move", () =>
        {
            var u = Own[1];
            Report(u);
            Check(u.Order == OrderType.Move && Mathf.Abs(MathUtil.AngleDiff(u.DestFacing, travel)) < 0.05f,
                $"faces the way of travel ({u.DestFacing:F2} ~ {travel:F2})");
            Click(MouseButton.Right, Ground(u.Anchor.X + 2f, u.Anchor.Y - 1.5f));
        }, 60);
        Add("check short move keeps facing", () =>
        {
            var u = Own[1];
            Report(u);
            Check(u.Dest.DistanceTo(u.Anchor + new Vector2(2f, -1.5f)) < 0.3f, "short move goes where clicked");
            Check(Mathf.Abs(MathUtil.AngleDiff(u.DestFacing, travel)) < 0.05f, "short move keeps the facing");
        }, 60);

        var before = new Dictionary<Battalion, Vector2>();
        Vector2 centre = default;
        Add("Ctrl+A, H, RMB 40 m to the side (group move)", () =>
        {
            Key(Godot.Key.A, ctrl: true);
            Key(Godot.Key.H);   // everyone facing north again, so the block's frame is plain north
            centre = Vector2.Zero;
            foreach (var u in Own) centre += u.Anchor;
            centre /= Own.Count;
            foreach (var u in Own) before[u] = MathUtil.ToLocal(u.Anchor - centre, 0f);
            Click(MouseButton.Right, Ground(centre.X + 40f, centre.Y));
        });
        Add("check group keeps its shape, rotated", () =>
        {
            var dest = centre + new Vector2(40f, 0f);
            float f = MathUtil.Angle(new Vector2(1, 0));
            foreach (var u in Own)
            {
                Report(u);
                var local = MathUtil.ToLocal(u.Dest - dest, f);
                Check(local.DistanceTo(before[u]) < 0.3f, $"{u.Name} keeps its place in the block ({before[u]:F1} -> {local:F1})");
                Check(Mathf.Abs(MathUtil.AngleDiff(u.DestFacing, f)) < 0.05f, $"{u.Name} faces the way of travel");
            }
        }, 60);

        Add("box select across battalions 1 and 2", () =>
        {
            Key(Godot.Key.Escape);
            var a = Own[1].Centroid;
            var b = Own[2].Centroid;
            Press(MouseButton.Left, Ground(Mathf.Max(a.X, b.X) + 5.5f, a.Y + 2.5f));
            MoveTo(Ground(Mathf.Min(a.X, b.X) - 5.5f, a.Y - 3.5f));
        });
        Add("release box", () =>
        {
            var a = Own[1].Centroid;
            var b = Own[2].Centroid;
            Release(MouseButton.Left, Ground(Mathf.Min(a.X, b.X) - 5.5f, a.Y - 3.5f));
            Check(P.Selected.Count == 2 && P.Selected.Contains(Own[1]) && P.Selected.Contains(Own[2]), $"box selected [{Names(P.Selected)}]");
        }, 60);

        Battalion gauls = null;
        Add("select an enemy and try to order it", () =>
        {
            gauls = B.Battalions.Find(u => u.Team == 1);
            Click(MouseButton.Left, Scr(PlayerInput.ChestOf(gauls.Soldiers[0])));
        });
        Add("enemy is selectable but not orderable", () =>
        {
            Check(P.Selected.Count == 1 && P.Selected[0] == gauls, $"selected [{Names(P.Selected)}]");
            Check(P.Inspected?.Team == 1, $"inspecting enemy {P.Inspected?.Name}");
            var dest = gauls.Dest;
            Click(MouseButton.Right, Ground(0, 0));
            Key(Godot.Key.H);
            Key(Godot.Key.Key5);
            Check(gauls.Order == OrderType.Hold && gauls.Dest == dest && gauls.Formation != FormationType.Wedge, "enemy ignored the orders");
        }, 60);

        Add("hover a man", () => MoveTo(Scr(PlayerInput.ChestOf(Own[3].Soldiers[3]))));
        Add("check hover", () => Check(P.Hovered?.Unit == Own[3], $"hovered {P.Hovered?.Name}"), 80);

        int speed0 = 0;
        Add("keys: space . , P F F1", () =>
        {
            speed0 = _m.SpeedIndex;
            Key(Godot.Key.Space);
            Check(!_m.Paused, "Space unpauses");
            Key(Godot.Key.Space);
            Check(_m.Paused, "Space pauses");
            Key(Godot.Key.Period);
            Check(_m.SpeedIndex == Math.Min(speed0 + 1, Main.Speeds.Length - 1), $"'.' speeds up to {_m.TimeScale}");
            Key(Godot.Key.Comma);
            Key(Godot.Key.Minus);
            Check(_m.SpeedIndex == Math.Max(speed0 - 1, 0), $"', -' slow down to {_m.TimeScale}");
            Key(Godot.Key.Equal);
            Check(_m.SpeedIndex == speed0, "'=' back");
            Key(Godot.Key.P);
            Check(_m.Autopilot && _m.Pilot != null, "P: autopilot on");
            Key(Godot.Key.P);
            Check(!_m.Autopilot && _m.Pilot == null, "P: autopilot off");
            Key(Godot.Key.F);
            Check(_m.Camera.Follow == P.Inspected && P.Inspected != null, "F follows the inspected man");
            Key(Godot.Key.F);
            Check(_m.Camera.Follow == null, "F again stops");
            Key(Godot.Key.F1);
            Check(_m.Hud.HelpVisible, "F1 shows help");
            Key(Godot.Key.F1);
            Check(!_m.Hud.HelpVisible, "F1 hides help");
        });

        Add("click a battalion card in the HUD", () =>
        {
            Key(Godot.Key.Escape);
            Check(P.Selected.Count == 0 && P.Inspected == null, "Esc clears selection and inspector");
            Click(MouseButton.Left, _m.Hud.CardRect(2).GetCenter());
        });
        Add("check card selection, then click the Deep button", () =>
        {
            Check(P.Selected.Count == 1 && P.Selected[0] == Own[2], $"card selected [{Names(P.Selected)}]");
            Click(MouseButton.Left, _m.Hud.FormationButtonRect(1).GetCenter());
        }, 80);
        Add("check button", () =>
        {
            Check(Own[2].Formation == FormationType.Deep, $"{Own[2].Name} formation {Own[2].Formation}");
            Check(Own[1].Formation != FormationType.Deep, "others untouched");
        }, 80);

        // Autopilot must leave a battalion the player orders himself alone until it has carried the order out.
        Vector2 moveTo = default;
        Add("autopilot on, move battalion 0", () =>
        {
            Key(Godot.Key.P);
            _m.Pilot.AI.Advancing = true;   // don't wait for the armies to close
            Key(Godot.Key.Escape);
            Click(MouseButton.Left, _m.Hud.CardRect(0).GetCenter());
        });
        Add("order a move, run the battle 1.5 s", () =>
        {
            var u = Own[0];
            Check(P.Selected.Count == 1 && P.Selected[0] == u, $"card selected [{Names(P.Selected)}]");
            moveTo = u.Anchor + new Vector2(0f, 12f);
            Click(MouseButton.Right, Ground(moveTo.X, moveTo.Y));
            Check(u.Order == OrderType.Move && _m.Pilot.IsManual(u), $"{u.Name}: order {u.Order}, manual {_m.Pilot.IsManual(u)}");
            RunBattle(1.5f);
            Report(u);
            Check(u.Order == OrderType.Move && u.Dest.DistanceTo(moveTo) < 0.5f, "autopilot left the player's move alone");
            Check(Own.FindAll(o => o != u).TrueForAll(o => o.Order == OrderType.Attack), "autopilot sent the other battalions in");
        }, 80);
        Add("halt it, run 1.5 s", () =>
        {
            var u = Own[0];
            Key(Godot.Key.H);
            RunBattle(1.5f);
            Check(u.Order == OrderType.Hold && _m.Pilot.IsManual(u), $"halted battalion stays put ({u.Order})");
        });
        Add("a short move, run until it arrives", () =>
        {
            var u = Own[0];
            Click(MouseButton.Right, Ground(u.Anchor.X, u.Anchor.Y + 1f));
            Check(u.Order == OrderType.Move, $"moving ({u.Order})");
            for (int i = 0; i < 12 && u.Order == OrderType.Move; i++) RunBattle(0.5f);
            RunBattle(1.1f);
            Report(u);
            Check(!_m.Pilot.IsManual(u) && u.Order == OrderType.Attack, "once there, the autopilot takes it back");
            Key(Godot.Key.P);
            Check(!_m.Autopilot, "P: autopilot off");
        });

        // Reserves: right-click a friendly battalion to make the selected one its reserve. The badges, the ground line
        // and the Relieve button follow the link; the reserve keeps station behind, a relief swaps the two over, and
        // any plain order takes a battalion out of the reserve role again.
        Add("reserve: halt everyone, look at battalions 2 and 3", () =>
        {
            Key(Godot.Key.A, ctrl: true);
            Key(Godot.Key.H);
            Key(Godot.Key.Escape);
            var c = (Own[2].Centroid + Own[3].Centroid) * 0.5f;
            _m.Camera.SnapTo(new Vector3(c.X, 0f, c.Y), Main.StartYaw, Main.StartPitch, Main.StartDistance);
        });
        Add("select battalion 3, RMB a man of battalion 2", () =>
        {
            Click(MouseButton.Left, _m.Hud.CardRect(3).GetCenter());
            Check(P.Selected.Count == 1 && P.Selected[0] == Own[3], $"card selected [{Names(P.Selected)}]");
            var man = Own[2].Soldiers.Find(x => x.Active && x.Slot == 0) ?? Own[2].Soldiers.Find(x => x.Active);
            Click(MouseButton.Right, Scr(PlayerInput.ChestOf(man)));
        });
        Add("check the link, badges, ground line and Relieve button", () =>
        {
            var (r, f) = (Own[3], Own[2]);
            Check(r.ReserveFor == f && f.ReservedBy == r, $"{r.Name} reserve for {r.ReserveFor?.Name}, {f.Name} relieved by {f.ReservedBy?.Name}");
            Check(r.Order != OrderType.Attack && f.Order == OrderType.Hold, $"no attack or move on the friend ({r.Order}, {f.Order})");
            Check(_m.Hud.CardLinkText(3) == $"Reserve for {f.Name}", $"card 3 badge '{_m.Hud.CardLinkText(3)}'");
            Check(_m.Hud.CardLinkText(2) == $"Relieved by {r.Name}", $"card 2 badge '{_m.Hud.CardLinkText(2)}'");
            Check(_m.Hud.CardLinkText(1) == "", $"card 1 has no badge '{_m.Hud.CardLinkText(1)}'");
            Check(Links?.Count == 1, $"one reserve line on the ground ({Links?.Count})");
            Check(_m.Hud.RelieveEnabled, "Relieve enabled for a reserve");
            Click(MouseButton.Left, _m.Hud.CardRect(1).GetCenter());
        }, 120);
        Add("Relieve button follows the selection", () =>
        {
            Check(!_m.Hud.RelieveEnabled, "Relieve disabled for a battalion with no reserve");
            Click(MouseButton.Left, _m.Hud.CardRect(2).GetCenter());
        }, 120);
        Add("the reserve takes station behind its front", () =>
        {
            Check(_m.Hud.RelieveEnabled, "Relieve enabled for a battalion with a reserve");
            var (r, f) = (Own[3], Own[2]);
            for (int i = 0; i < 80 && (r.Order != OrderType.Hold || i < 4); i++) RunBattle(0.5f);
            Report(r);
            var fwd = MathUtil.Dir(f.Facing);
            float gap = (f.Anchor - r.Anchor).Dot(fwd) - f.Depth * 0.5f - r.Depth * 0.5f;
            float side = Mathf.Abs((r.Anchor - f.Anchor).Dot(MathUtil.Right(fwd)));
            Check(gap > 10f && gap < 20f, $"{gap:F1} m between the front's rear rank and the reserve's front rank");
            Check(side < 1.5f, $"straight behind it ({side:F2} m to the side)");
            Check(Mathf.Abs(MathUtil.AngleDiff(r.Facing, f.Facing)) < 0.1f, "facing the same way");
        });
        Add("click Relieve: the two swap over", () =>
        {
            var (r, f) = (Own[3], Own[2]);
            Click(MouseButton.Left, _m.Hud.RelieveButtonRect.GetCenter());
            Check(r.Relief != ReliefPhase.None, $"the reserve moves up ({r.Relief})");
            var last = new Dictionary<Soldier, Vector2>();
            float jump = 0f;
            for (int i = 0; i < 120 && f.ReserveFor != r; i++)
            {
                foreach (var s in r.Soldiers) last[s] = s.Pos;
                foreach (var s in f.Soldiers) last[s] = s.Pos;
                RunBattle(0.25f);
                foreach (var (s, p) in last) if (s.Active) jump = Mathf.Max(jump, s.Pos.DistanceTo(p));
            }
            var fwd = MathUtil.Dir(r.Facing);
            float rear = r.Anchor.Dot(fwd) - r.Depth * 0.5f, back = float.MinValue;
            foreach (var s in f.Soldiers) if (s.Active) back = Mathf.Max(back, s.Pos.Dot(fwd) - rear);
            Check(f.ReserveFor == r && r.ReservedBy == f && r.ReserveFor == null, $"links flipped: {f.Name} reserve for {f.ReserveFor?.Name}");
            Check(back < 0f, $"all of {f.Name} is behind {r.Name}'s rear rank ({back:F1} m)");
            Check(jump < 1.6f, $"nobody teleported (the most any man moved in 0.25 s: {jump:F2} m)");
            for (int i = 0; i < 80 && (f.Order != OrderType.Hold || i < 4); i++) RunBattle(0.5f);
            Report(r);
            Report(f);
            float gap = (r.Anchor - f.Anchor).Dot(fwd) - f.Depth * 0.5f - r.Depth * 0.5f;
            Check(gap > 10f && gap < 20f, $"the old front re-formed {gap:F1} m behind as the new reserve");
        });
        Add("badges flipped; H on the new reserve", () =>
        {
            var (r, f) = (Own[3], Own[2]);
            Check(_m.Hud.CardLinkText(2) == $"Reserve for {r.Name}" && _m.Hud.CardLinkText(3) == $"Relieved by {f.Name}",
                $"badges '{_m.Hud.CardLinkText(2)}' / '{_m.Hud.CardLinkText(3)}'");
            Click(MouseButton.Left, _m.Hud.CardRect(2).GetCenter());
            Key(Godot.Key.H);
            Check(f.ReserveFor == null && r.ReservedBy == null, "a halt order takes it out of the reserve role");
        }, 120);
        Add("badges and line gone", () =>
        {
            Check(_m.Hud.CardLinkText(2) == "" && _m.Hud.CardLinkText(3) == "", "no badges");
            Check(Links?.Count == 0, $"no reserve line ({Links?.Count})");
        }, 120);

        Battle old = null;
        Add("F5 returns to the setup screen", () =>
        {
            old = B;
            Key(Godot.Key.F5);
            Check(_m.Screen == Main.GameScreen.Setup && B == null && P.Selected.Count == 0, $"screen {_m.Screen}, battle {(B == null ? "null" : "set")}");
        });

        // Set up screen -> Start must build the chosen battalion counts and men (item 5 of the wave-2 setup task).
        int wantRomeCount = 0, wantMen = 20, romeTotal = 0;
        Add("bump a Rome unit's battalion count on the setup screen", () =>
        {
            wantRomeCount = _m.Setup.RomeCount(0) + 3;
            for (int i = 0; i < 3; i++) Click(MouseButton.Left, _m.Setup.CountButtonRect(0, 0, true).GetCenter());
            Check(_m.Setup.RomeCount(0) == wantRomeCount, $"Rome row 0 ({_m.Setup.RomeTypeName(0)}) count {_m.Setup.RomeCount(0)} == {wantRomeCount}");
        }, 100);
        Add("click Start battle", () => Click(MouseButton.Left, _m.Setup.StartButtonRect.GetCenter()), 600);
        Add("check the custom battle matches the setup screen", () =>
        {
            Check(_m.Screen == Main.GameScreen.Battle && B != null && B != old, "a fresh custom battle");
            int gotRomeCount = B.Battalions.FindAll(u => u.Team == 0 && u.Type.Name == _m.Setup.RomeTypeName(0)).Count;
            Check(gotRomeCount == wantRomeCount, $"battalions of {_m.Setup.RomeTypeName(0)}: {gotRomeCount} == {wantRomeCount}");
            Check(B.Battalions.TrueForAll(u => u.InitialCount == wantMen), $"every battalion has {wantMen} men");
            // Past 6 battalions a side forms a second line, and each second-line battalion is the reserve of the one
            // directly in front of it (Scenarios.Custom).
            romeTotal = Own.Count;
            for (int team = 0; team < 2; team++)
            {
                var side = B.Battalions.FindAll(u => u.Team == team);
                int front = Math.Min(6, side.Count), linked = 0;
                for (int i = front; i < side.Count; i++)
                    if (side[i].ReserveFor != null && side.IndexOf(side[i].ReserveFor) < front) linked++;
                Check(linked == side.Count - front, $"team {team}: {linked} of {side.Count - front} second-line battalions are reserves of the front line");
            }
        }, 100);

        // The end screen's button (same as Enter) must also return to the setup screen.
        Add("fast-forward the custom battle to a decision", () =>
        {
            for (int i = 0; i < 400 && B.Winner < 0; i++) RunBattle(1f);
            Check(B.Winner >= 0, $"battle ended, winner {B.Winner}, time {B.Time:0.0}");
        }, 200);
        Add("Enter on the end screen returns to the setup screen", () =>
        {
            Key(Godot.Key.Enter);
            Check(_m.Screen == Main.GameScreen.Setup && _m.Battle == null, "back to the setup screen");
        }, 300);

        // Twelve battalions: the cards wrap into two rows beside the orders panel, at 1600x900 and at 1280x720.
        Add("back on the setup screen, make it 12 Roman battalions and start", () =>
        {
            for (int i = romeTotal; i < 12; i++) Click(MouseButton.Left, _m.Setup.CountButtonRect(0, 0, true).GetCenter());
            Click(MouseButton.Left, _m.Setup.StartButtonRect.GetCenter());
        }, 300);
        Add("select all so the orders panel shows", () =>
        {
            Check(_m.Screen == Main.GameScreen.Battle && Own.Count == 12, $"a battle with {Own.Count} Roman battalions");
            Key(Godot.Key.A, ctrl: true);
        }, 600);
        Add("check the card rows at the start size", CheckCardRows, 300);
        Vector2I startSize = default;
        Add("shrink the window to 1280x720", () =>
        {
            startSize = _m.GetWindow().Size;
            _m.GetWindow().Size = new Vector2I(1280, 720);
        });
        Add("check the card rows at 1280x720", () =>
        {
            var vp = _m.GetViewport().GetVisibleRect().Size;
            Check(vp == new Vector2(1280, 720), $"viewport is {vp}");
            CheckCardRows();
            _m.GetWindow().Size = startSize;
        }, 800);

        Add("done", () =>
        {
            GD.Print($"[selftest] {_pass} passed, {_fail} failed");
            _m.GetTree().Quit(_fail == 0 ? 0 : 1);
        }, 300);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Every battalion card on screen, none overlapping another or the orders panel, in at most two rows.</summary>
    void CheckCardRows()
    {
        var hud = _m.Hud;
        var screen = _m.GetViewport().GetVisibleRect();
        var orders = hud.OrdersRect;
        var rows = new HashSet<float>();
        int bad = 0;
        for (int i = 0; i < hud.CardCount; i++)
        {
            var r = hud.CardRect(i);
            rows.Add(Mathf.Round(r.Position.Y));
            bool ok = screen.Encloses(r) && !r.Intersects(orders);
            for (int j = 0; j < i; j++) ok &= !r.Intersects(hud.CardRect(j));
            if (!ok) { bad++; Log($"card {i} at {r} (orders panel {orders})"); }
        }
        Check(hud.CardCount == Own.Count && bad == 0, $"{hud.CardCount} cards on a {screen.Size} screen, {bad} off screen or overlapping, card 0 is {hud.CardRect(0).Size.X} wide");
        Check(rows.Count <= 2 && hud.OrdersVisible, $"{rows.Count} rows, orders panel {(hud.OrdersVisible ? "showing" : "hidden")}");
    }

    void PlaceCamera()
    {
        var cam = _m.Camera;
        cam.Follow = null;
        cam.SnapTo(Main.StartTarget, Main.StartYaw, Main.StartPitch, Main.StartDistance);
        if (cam.GlobalPosition.LengthSquared() < 1e-4f)
        {
            // Stub camera: put it where the real one would be.
            var fwd = new Vector3(Mathf.Sin(Main.StartYaw), 0f, Mathf.Cos(Main.StartYaw));
            var eye = Main.StartTarget - fwd * Mathf.Cos(Main.StartPitch) * Main.StartDistance
                      + Vector3.Up * Mathf.Sin(Main.StartPitch) * Main.StartDistance;
            cam.LookAtFromPosition(eye, Main.StartTarget, Vector3.Up);
        }
    }

    /// <summary>Step the battle (and the autopilot) while the game stays paused.</summary>
    void RunBattle(float seconds)
    {
        int steps = Mathf.RoundToInt(seconds / Battle.FixedDt);
        for (int i = 0; i < steps; i++) _m.StepBattle();
    }

    UnitFighting.View.ReserveLinks Links => _m.View.GetNodeOrNull<UnitFighting.View.ReserveLinks>("ReserveLinks");

    Vector2 Scr(Vector3 world) => _m.Camera.UnprojectPosition(world);
    Vector2 Ground(float x, float y) => Scr(new Vector3(x, 0f, y));

    void Push(InputEvent e) => _m.GetViewport().PushInput(e);

    void MoveTo(Vector2 pos) => Push(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });

    void Press(MouseButton b, Vector2 pos, bool shift = false)
    {
        MoveTo(pos);
        Push(new InputEventMouseButton { ButtonIndex = b, Pressed = true, Position = pos, GlobalPosition = pos, ShiftPressed = shift });
    }

    void Release(MouseButton b, Vector2 pos, bool shift = false)
    {
        MoveTo(pos);
        Push(new InputEventMouseButton { ButtonIndex = b, Pressed = false, Position = pos, GlobalPosition = pos, ShiftPressed = shift });
    }

    void Click(MouseButton b, Vector2 pos, bool shift = false)
    {
        Press(b, pos, shift);
        Release(b, pos, shift);
    }

    void Key(Key k, bool ctrl = false)
    {
        Push(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true, CtrlPressed = ctrl });
        Push(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = false, CtrlPressed = ctrl });
    }

    void Check(bool ok, string what)
    {
        if (ok) { _pass++; GD.Print($"[selftest]    ok   {what}"); }
        else Fail(what);
    }

    void Fail(string what)
    {
        _fail++;
        GD.Print($"[selftest]    FAIL {what}");
    }

    static void Log(string s) => GD.Print($"[selftest]    {s}");

    static void Report(Battalion u) => Log(
        $"{u.Name}: order {u.Order}, dest ({u.Dest.X:F2}, {u.Dest.Y:F2}), facing {u.DestFacing:F3} " +
        $"dir {MathUtil.Dir(u.DestFacing):F2}, formation {u.Formation}, files {u.Files}, depth {u.Depth:F1}, running {u.Running}");

    static string Names(List<Battalion> list) => string.Join(", ", list.ConvertAll(u => u.Name));
}
