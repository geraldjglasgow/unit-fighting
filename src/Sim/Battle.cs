using System;
using System.Collections.Generic;
using Godot;

namespace UnitFighting.Sim;

public enum EventKind { Block, Parry, Hit, Glance, Kill, Miss, Impact, GuardBreak, Rout, Rally }

public struct CombatEvent
{
    public EventKind Kind;
    public Soldier Attacker, Defender;
    public Battalion Unit;       // Rout / Rally
    public Vector2 Pos;
    public float Amount;         // damage, or impact ratio
    public HitLocation Location;
}

/// <summary>
/// The whole battle as plain data. Step it at a fixed rate; it knows nothing about nodes or rendering.
/// Views read soldiers directly and drain <see cref="Events"/> for sparks, blood and sounds.
/// </summary>
public sealed class Battle
{
    public const float FixedDt = 1f / 60f;

    public readonly List<Battalion> Battalions = new();
    public readonly List<Soldier> Soldiers = new();
    public readonly List<BattleAI> AIs = new();
    public readonly List<CombatEvent> Events = new();
    public readonly SpatialGrid Grid = new(3f, 180f);
    public readonly Random Rng;
    public readonly string[] TeamNames = { "Rome", "Gauls" };

    public float FieldHalf = 150f;
    public bool RecordEvents = true;
    public float Time;
    public int NextSoldierId, NextUnitId;
    public int Winner = -1;
    public float EndTime = -1f;

    // Scratch lists for grid queries. Single-threaded; A and B must never be in use by the same caller twice.
    public readonly List<Soldier> QueryA = new(), QueryB = new();

    public Battle(int seed) => Rng = new Random(seed);

    public float Rand() => (float)Rng.NextDouble();
    public float Range(float a, float b) => a + Rand() * (b - a);

    public Battalion AddBattalion(int team, UnitType type, string name, Vector2 center, float facing)
    {
        var u = new Battalion(NextUnitId++, team, type, name);
        Battalions.Add(u);
        u.Deploy(this, center, facing);
        return u;
    }

    public void Step(float dt)
    {
        Time += dt;
        foreach (var s in Soldiers)
        {
            s.PrevPos = s.Pos;
            s.PrevFacing = s.Facing;
            s.ThreatCount = 0;
            s.TargetedBy = 0;
        }
        Grid.Rebuild(Soldiers);
        foreach (var s in Soldiers)
        {
            var t = s.Target;
            if (!s.Active || t == null) continue;
            if (!t.Active) { s.Target = null; continue; }
            t.TargetedBy++;
            if (s.Pos.DistanceTo(t.Pos) <= s.Weapon.Reach + 1f) t.ThreatCount++;
        }

        UpdateReserves(dt);
        foreach (var u in Battalions) u.Update(this, dt);
        foreach (var s in Soldiers)
            if (s.Active) SoldierAI.Think(this, s, dt);
        foreach (var s in Soldiers)
            if (s.Active) s.Pos += s.Vel * dt;
        ResolveCollisions();
        foreach (var ai in AIs) ai.Update(this, dt);
        CheckVictory();
        if (!RecordEvents) Events.Clear();
        else if (Events.Count > 4000) Events.RemoveRange(0, Events.Count - 4000);
    }

    void ResolveCollisions()
    {
        for (int iter = 0; iter < 2; iter++)
        {
            foreach (var a in Soldiers)
            {
                if (!a.Active) continue;
                Grid.Query(a.Pos, 0.8f, QueryA);
                foreach (var o in QueryA)
                {
                    if (o.Id <= a.Id || !o.Active) continue;
                    bool friends = o.Team == a.Team;
                    bool passing = friends && a.Unit != o.Unit && a.Unit.PassingWith == o.Unit;
                    float min = friends ? (a.Fleeing || o.Fleeing ? 0.45f : passing ? PassSpacing : 0.58f) : 0.72f;
                    var diff = o.Pos - a.Pos;
                    float d2 = diff.LengthSquared();
                    if (d2 >= min * min) continue;
                    float d = Mathf.Sqrt(d2);
                    var n = d > 1e-4f ? diff / d : MathUtil.Dir(a.Id * 2.39996f);
                    if (iter == 0 && !friends)
                    {
                        Combat.CheckImpact(this, a, o, n);
                        if (!a.Active || !o.Active) continue;
                    }
                    float ma = a.Phase == CombatPhase.Down ? a.Mass * 2f : a.Mass;
                    float mo = o.Phase == CombatPhase.Down ? o.Mass * 2f : o.Mass;
                    float wa = mo / (ma + mo);
                    float push = (min - d) * 0.9f;
                    a.Pos -= n * push * wa;
                    o.Pos += n * push * (1f - wa);
                }
            }
        }
    }

    void CheckVictory()
    {
        if (Winner >= 0) return;
        for (int team = 0; team < 2; team++)
        {
            bool any = false, beaten = true;
            foreach (var u in Battalions)
            {
                if (u.Team != team) continue;
                any = true;
                if (!u.Destroyed && !u.Routing) { beaten = false; break; }
            }
            if (any && beaten)
            {
                Winner = 1 - team;
                EndTime = Time;
                return;
            }
        }
    }

    // ---------------------------------------------------------------- reserves

    // A reserve battalion keeps station behind its front and relieves it when the front is spent, as the Roman
    // principes relieved the hastati: it closes up behind the front with its files between the front's files, steps
    // forward into the line between the front's men, and the front steps back through it, facing the enemy, and
    // re-forms behind as the reserve of the one that relieved it, so the two can take turns.
    //
    // The passage is through opened files rather than round a flank: shifted half a file, every reserve man walks up
    // the gap between two of the front's files and every front man back down the gap between two of the reserve's,
    // so nobody has to shove through anybody (men of the two lines may pass 0.4 m apart rather than the usual 0.58
    // while they do), the line is never left open, and for a moment it is doubled with fresh men already fighting
    // before the tired ones step back. A sidestep would leave the front's place empty while the reserve wheels into
    // it, and needs room on a flank that a line of battalions doesn't have. It takes about 8 s.
    //
    // The front is spent, while it is fighting, when any of these holds: it is worn out, ReliefWorn of its front rank
    // due to be relieved with nobody fit behind them to do it (Battalion.Worn: the ranks have been changing places
    // man by man, and the men behind are now as blown or hurt as the ones in front, so that no longer keeps the front
    // fresh; about two minutes into an even fight); it has lost more than 1 - ReliefStrength of its men;
    // ReliefFleeing of them have started to run; or it is shaken, its morale ReliefShaken below what its men's nerve
    // and skill give it when fresh and whole (a couple of men down with the losses still fresh), well before the
    // faint-hearted start to break (from about 45 down). Relieving it early is the point: a front already breaking
    // can't step back through anyone. The reserve only goes in if it is fresh and steady itself (ReserveStamina,
    // ReserveMorale), steadier than the front by ReserveLead, not fighting itself and has at least half as many men
    // formed as the front (and 6). After a relief the pair won't swap back on their own for ReliefCooldown seconds.
    // RelieveNow skips all this. The thresholds are public so the bench can tune them (--set Battle.ReliefWorn=0.4).
    public static float ReliefWorn = 0.3f;
    public static float ReliefStrength = 0.6f;
    public static int ReliefFleeing = 2;
    public static float ReliefShaken = 15f;
    public static float ReserveStamina = 0.65f;
    public static float ReserveMorale = 55f;
    public static float ReserveLead = 10f;
    public static float ReliefCooldown = 30f;
    public const float ReserveGap = 12f;        // metres from the front's rear rank to the reserve's front rank
    public const float PassGap = 1.5f;          // how close behind the front the reserve comes before the lines pass
    public const float PassSpacing = 0.4f;      // how close men of two lines passing through each other can squeeze
    public const float PassLinger = 4f;         // seconds they keep giving way after the relief, to untangle
    const int ReserveMinMen = 6;
    const float ReserveTick = 0.25f;
    float _reserveTimer;

    /// <summary>Make <paramref name="reserve"/> the reserve behind <paramref name="front"/> (same team). Breaks any links either had.</summary>
    public void AssignReserve(Battalion reserve, Battalion front)
    {
        if (reserve == null) return;
        if (front == null) { ClearReserve(reserve); return; }
        if (reserve == front || reserve.Team != front.Team || reserve.Destroyed || front.Destroyed || reserve.Routing || front.Routing) return;
        if (reserve.ReserveFor == front) return;
        // No loops: if the front already stands somewhere behind the reserve, that link goes (the roles are swapped round).
        for (var f = front; f.ReserveFor != null; f = f.ReserveFor)
            if (f.ReserveFor == reserve) { ClearReserve(f); break; }
        ClearReserve(reserve);
        if (front.ReservedBy != null) ClearReserve(front.ReservedBy);
        if (reserve.FallingBack) reserve.ReservedBy?.CancelRelief();
        reserve.FallingBack = false;
        reserve.LinkReserve(front);
        reserve.NextReliefTime = Time + 5f;   // let it get into place first
    }

    /// <summary>No longer a reserve: it stands where it is (or finishes the move it was making).</summary>
    public void ClearReserve(Battalion reserve)
    {
        if (reserve?.ReserveFor == null) return;
        reserve.UnlinkReserve();
    }

    /// <summary>Manual relief: the front's reserve comes up and takes over now, however fresh either of them is.</summary>
    public void RelieveNow(Battalion front)
    {
        var r = front?.ReservedBy;
        if (r == null || r.Relief != ReliefPhase.None || front.FallingBack || front.ReserveFor != null) return;
        if (front.Destroyed || front.Routing || r.Destroyed || r.Routing || r.Formed == 0) return;
        StartRelief(r, front);
    }

    /// <summary>Can this reserve go in? Not busy fighting itself, fresh enough, steady enough and enough of them.</summary>
    public static bool ReserveReady(Battalion r, Battalion front) =>
        !r.Routing && !r.Destroyed && !r.InMelee && r.Formed >= Math.Max(Math.Min(ReserveMinMen, front.Formed), front.Formed / 2)
        && r.AvgStamina >= ReserveStamina && r.Morale >= Math.Max(ReserveMorale, front.Morale + ReserveLead);

    /// <summary>Is the front spent, while still fighting: worn out, badly thinned, some of its men already running, or shaken?</summary>
    public static bool FrontSpent(Battalion f) =>
        !f.Routing && f.InMelee && f.ReserveFor == null
        && (f.Worn >= ReliefWorn || f.Alive < f.InitialCount * ReliefStrength || f.FleeingCount >= ReliefFleeing
            || f.Morale < f.BaseMorale() - ReliefShaken);

    void UpdateReserves(float dt)
    {
        _reserveTimer -= dt;
        if (_reserveTimer > 0f) return;
        _reserveTimer += ReserveTick;
        foreach (var u in Battalions)
        {
            if (u.PassTime <= 0f) continue;
            u.PassTime -= ReserveTick;
            if (u.PassTime <= 0f && u.Relief == ReliefPhase.None && !u.FallingBack) u.PassingWith = null;
        }
        foreach (var r in Battalions)
        {
            var f = r.ReserveFor;
            if (f == null) continue;
            // A reserve whose front breaks or is gone stands where it is, and is nobody's reserve any more.
            if (r.Destroyed || r.Routing || f.Destroyed || f.Routing) { ClearReserve(r); continue; }
            switch (r.Relief)
            {
                case ReliefPhase.None:
                    if (Time >= r.NextReliefTime && !f.FallingBack && FrontSpent(f) && ReserveReady(r, f)) StartRelief(r, f);
                    else if (!r.InMelee) KeepStation(r, f);   // set upon itself, it stands and fights
                    break;
                case ReliefPhase.Closing: StepClosing(r, f); break;
                case ReliefPhase.Joining: StepJoining(r, f); break;
                case ReliefPhase.Passing: StepPassing(r, f); break;
            }
        }
    }

    /// <summary>The reserve follows its front: behind it, facing the same way and as wide. If the front is marching it goes to where the front is going.</summary>
    static void KeepStation(Battalion r, Battalion f)
    {
        bool marching = f.Order == OrderType.Move;
        var at = marching ? f.Dest : f.Anchor;
        float facing = marching ? f.DestFacing : f.Facing;
        int files = r.FilesToMatch(f);
        var station = at - MathUtil.Dir(facing) * (f.Depth * 0.5f + ReserveGap + r.DepthWith(files) * 0.5f);
        var goal = r.Order == OrderType.Move ? r.Dest : r.Anchor;
        float goalFacing = r.Order == OrderType.Move ? r.DestFacing : r.Facing;
        if (station.DistanceTo(goal) > 1.5f || Mathf.Abs(MathUtil.AngleDiff(facing, goalFacing)) > 0.15f || (files > 0 && files != r.Files))
            r.MoveTo(station, facing, r.Anchor.DistanceTo(station) > 20f, files);
    }

    void StartRelief(Battalion r, Battalion f)
    {
        r.Relief = ReliefPhase.Closing;
        r.ReliefStartTime = Time;
        StepClosing(r, f);
    }

    /// <summary>
    /// Sideways shift that puts the reserve's men between the front's men rather than straight behind them, so the
    /// two can pass through each other: half a file if both front ranks have the same parity.
    /// </summary>
    static float InterleaveOffset(Battalion r, Battalion f, int files)
    {
        if (!Formation.UsesFiles(r.Formation) || !Formation.UsesFiles(f.Formation)) return 0f;
        int nf = Math.Min(f.Files, Math.Max(1, f.Formed)), nr = Math.Min(files > 0 ? files : r.Files, Math.Max(1, r.Formed));
        return (nr - nf) % 2 == 0 ? 0.5f * Formation.Spacing(r.Formation) : 0f;
    }

    /// <summary>Relief, first part: the reserve comes up close behind the front, files between its files.</summary>
    void StepClosing(Battalion r, Battalion f)
    {
        var fwd = MathUtil.Dir(f.Facing);
        int files = r.FilesToMatch(f);
        float off = InterleaveOffset(r, f, files);
        var at = f.Anchor - fwd * (f.Depth * 0.5f + PassGap + r.DepthWith(files) * 0.5f) + MathUtil.Right(fwd) * off;
        var goal = r.Order == OrderType.Move ? r.Dest : r.Anchor;
        // At the double: a spent front can't wait long.
        bool run = r.Anchor.DistanceTo(at) > 4f;
        if (at.DistanceTo(goal) > 0.4f || Mathf.Abs(MathUtil.AngleDiff(f.Facing, r.Order == OrderType.Move ? r.DestFacing : r.Facing)) > 0.08f
            || (files > 0 && files != r.Files) || (run && !r.Running))
            r.MoveTo(at, f.Facing, run, files);
        bool there = r.Anchor.DistanceTo(at) < 1f && Mathf.Abs(MathUtil.AngleDiff(r.Facing, f.Facing)) < 0.2f && r.SlotError() < 1.5f;
        if (there) BeginJoining(r, f, files, off);
        else if (Time - r.ReliefStartTime > 45f)
        {
            r.CancelRelief();   // can't get there (the front keeps moving, say); try again later
            r.NextReliefTime = Time + ReliefCooldown;
        }
    }

    /// <summary>
    /// Relief, second part: the reserve steps forward into the front's line, its men between the front's men, so that
    /// for a moment the line is doubled and the fresh men take up the fight. The front stands until they are in.
    /// </summary>
    void BeginJoining(Battalion r, Battalion f, int files, float off)
    {
        var fwd = MathUtil.Dir(f.Facing);
        r.Relief = ReliefPhase.Joining;
        r.ReliefStartTime = Time;
        r.ReliefAt = f.Anchor + MathUtil.Right(fwd) * off;
        r.MoveTo(r.ReliefAt, f.Facing, false, files);
        r.PassingWith = f;
        f.PassingWith = r;
        r.PassTime = f.PassTime = 0f;
    }

    void StepJoining(Battalion r, Battalion f)
    {
        var fwd = MathUtil.Dir(f.Facing);
        float line = f.Anchor.Dot(fwd) + f.Depth * 0.5f, sum = 0f;
        int n = 0;
        foreach (var s in r.Soldiers)
        {
            if (!s.Active || s.Fleeing || s.Slot < 0 || s.Slot >= r.Slots.Count || r.Slots[s.Slot].Rank != 0) continue;
            sum += s.Pos.Dot(fwd);
            n++;
        }
        bool level = n > 0 && sum / n > line - 0.8f;
        if (level || r.Order == OrderType.Hold || r.FightingCount >= Math.Max(1, f.FightingCount / 2) || Time - r.ReliefStartTime > 8f)
            BeginPassing(r, f);
    }

    /// <summary>
    /// Relief, last part: the front steps back through the reserve's files, still facing the enemy, and re-forms
    /// behind it, while the reserve holds the line.
    /// </summary>
    void BeginPassing(Battalion r, Battalion f)
    {
        var fwd = MathUtil.Dir(f.Facing);
        r.Relief = ReliefPhase.Passing;
        r.ReliefStartTime = Time;
        r.ReliefTarget = f.Order == OrderType.Attack && f.AttackTarget is { Destroyed: false, Routing: false } ? f.AttackTarget : null;
        var behind = r.ReliefAt - fwd * (r.DepthWith(0) * 0.5f + ReserveGap + f.Depth * 0.5f);
        f.MoveTo(behind, f.Facing, false);
        f.FallingBack = true;
    }

    void StepPassing(Battalion r, Battalion f)
    {
        // The front is through once every man of it is behind the reserve's rear rank.
        var fwd = MathUtil.Dir(r.Facing);
        float rear = (r.Anchor - fwd * (r.Depth * 0.5f)).Dot(fwd);
        bool clear = true;
        foreach (var s in f.Soldiers)
            if (s.Active && !s.Fleeing && s.Pos.Dot(fwd) > rear - 0.5f) { clear = false; break; }
        bool inPlace = r.Order == OrderType.Hold || r.Anchor.DistanceTo(r.ReliefAt) < 1.5f;
        if ((clear && inPlace) || Time - r.ReliefStartTime > 30f) CompleteRelief(r, f);
    }

    /// <summary>They have changed places: the reserve is the front now, and the relieved battalion its reserve.</summary>
    void CompleteRelief(Battalion r, Battalion f)
    {
        var target = r.ReliefTarget;
        var behind = r.ReservedBy;   // a second reserve behind the one that went in
        f.FallingBack = false;       // it carries on back to its new station
        r.CancelRelief();
        r.UnlinkReserve();
        if (behind != null) behind.UnlinkReserve();
        f.LinkReserve(r);
        if (behind != null) behind.LinkReserve(f);
        f.NextReliefTime = Time + ReliefCooldown;
        if (target is { Destroyed: false, Routing: false }) r.TakeOverAttack(target);
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Is there a friend of <paramref name="s"/> standing in the way of a blow at <paramref name="t"/>?</summary>
    public bool LineBlocked(Soldier s, Soldier t) => LineBlocked(s, t, 0.35f);

    /// <summary>As <see cref="LineBlocked(Soldier, Soldier)"/>, with a given clearance either side of the line.</summary>
    public bool LineBlocked(Soldier s, Soldier t, float clearance)
    {
        var seg = t.Pos - s.Pos;
        float len2 = seg.LengthSquared();
        if (len2 < 1e-4f) return false;
        Grid.Query((s.Pos + t.Pos) * 0.5f, Mathf.Sqrt(len2) * 0.5f + 0.4f, QueryB);
        foreach (var a in QueryB)
        {
            if (a == s || a.Team != s.Team || !a.Active || a.Fleeing) continue;
            float k = (a.Pos - s.Pos).Dot(seg) / len2;
            if (k < 0.15f || k > 0.95f) continue;
            if ((s.Pos + seg * k).DistanceSquaredTo(a.Pos) < clearance * clearance) return true;
        }
        return false;
    }

    /// <summary>
    /// Friends standing behind <paramref name="v"/> in his file, relative to a push along <paramref name="n"/>:
    /// up to three ranks deep, so a deep formation braces better than a line, and a line better than a thin one.
    /// </summary>
    public int AlliesBehind(Soldier v, Vector2 n)
    {
        Grid.Query(v.Pos + n * 1.4f, 1.6f, QueryB);
        var side = new Vector2(-n.Y, n.X);
        int count = 0;
        foreach (var a in QueryB)
        {
            if (a == v || a.Team != v.Team || !a.Active || a.Fleeing) continue;
            var d = a.Pos - v.Pos;
            float along = d.Dot(n);
            if (along > 0.3f && along < 3f && Mathf.Abs(d.Dot(side)) < 0.6f) count++;
        }
        return Math.Min(count, 3);
    }

    public bool EnemyWithin(Vector2 p, int team, float r)
    {
        Grid.Query(p, r, QueryB);
        foreach (var e in QueryB)
            if (e.Team != team && e.Active && !e.Fleeing) return true;
        return false;
    }

    public Vector2? TeamCentroid(int team)
    {
        var sum = Vector2.Zero;
        int n = 0;
        foreach (var s in Soldiers)
        {
            if (s.Team != team || !s.Active || s.Fleeing) continue;
            sum += s.Pos;
            n++;
        }
        return n > 0 ? sum / n : null;
    }

    public Battalion NearestEnemyUnit(Battalion u)
    {
        Battalion best = null;
        float bestD = float.MaxValue;
        foreach (var o in Battalions)
        {
            if (o.Team == u.Team || o.Destroyed || o.Routing) continue;
            float d = o.Centroid.DistanceSquaredTo(u.Centroid);
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    /// <summary>Men still on the field and not running.</summary>
    public int Strength(int team)
    {
        int n = 0;
        foreach (var s in Soldiers)
            if (s.Team == team && s.Active && !s.Fleeing) n++;
        return n;
    }

    public int Dead(int team)
    {
        int n = 0;
        foreach (var s in Soldiers)
            if (s.Team == team && s.Dead) n++;
        return n;
    }

    // ---------------------------------------------------------------- events

    public void Emit(EventKind kind, Soldier a, Soldier d, float amount, HitLocation loc = HitLocation.Torso)
    {
        if (!RecordEvents) return;
        Events.Add(new CombatEvent
        {
            Kind = kind, Attacker = a, Defender = d, Unit = d?.Unit, Amount = amount, Location = loc,
            Pos = d != null && a != null ? (a.Pos + d.Pos) * 0.5f : d?.Pos ?? a?.Pos ?? Vector2.Zero,
        });
    }

    public void EmitUnit(EventKind kind, Battalion u)
    {
        if (!RecordEvents) return;
        Events.Add(new CombatEvent { Kind = kind, Unit = u, Pos = u.Centroid });
    }
}
