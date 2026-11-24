using System;
using System.Collections.Generic;

namespace UnitFighting.Sim;

/// <summary>
/// A simple general. Holds until the enemy comes close or it runs out of patience, then sends each
/// battalion at the nearest enemy, preferring ones already locked in melee with a friend (so it flanks),
/// and spreading its battalions across targets rather than piling on one. A battalion drawn up close behind
/// another becomes its reserve: it follows it in and relieves it when it is spent (see Battle.UpdateReserves),
/// rather than being sent at the enemy on its own. But a reserve is no use standing behind a front that two enemy
/// battalions are on: then it is released and goes in like the rest, where it is most needed.
/// </summary>
public sealed class BattleAI
{
    public readonly int Team;
    public float AdvanceAfter = 12f;
    public float AdvanceRange = 45f;
    public bool Advancing;
    public bool UseReserves = true;   // make second-line battalions reserves of the ones in front of them
    float _timer;
    bool _reservesSet;

    public BattleAI(int team) => Team = team;

    public void Update(Battle b, float dt)
    {
        _timer -= dt;
        if (_timer > 0f) return;
        _timer = 1f;

        var mine = new List<Battalion>();
        var theirs = new List<Battalion>();
        foreach (var u in b.Battalions)
        {
            if (u.Destroyed || u.Routing) continue;
            (u.Team == Team ? mine : theirs).Add(u);
        }
        if (mine.Count == 0 || theirs.Count == 0) return;
        if (!_reservesSet && UseReserves)
        {
            _reservesSet = true;
            AssignReserves(b, mine);
        }

        if (!Advancing)
        {
            float closest = float.MaxValue;
            foreach (var m in mine)
            foreach (var t in theirs)
                closest = Math.Min(closest, m.Centroid.DistanceTo(t.Centroid));
            if (b.Time < AdvanceAfter && closest > AdvanceRange) return;
            Advancing = true;
        }

        foreach (var u in mine)
        {
            if (u.ReserveFor != null && Outnumbered(b, u.ReserveFor) && u.Relief == ReliefPhase.None) b.ClearReserve(u);
            if (u.ReserveFor != null || u.FallingBack) continue;   // a reserve follows its front and goes in when it relieves it
            if (u.Order == OrderType.Attack && u.AttackTarget is { Destroyed: false, Routing: false }) continue;
            Battalion best = null;
            float bestScore = float.MaxValue;
            foreach (var t in theirs)
            {
                float score = u.Centroid.DistanceTo(t.Centroid);
                if (t.InMelee) score -= 15f;
                foreach (var m in mine)
                    if (m != u && m.AttackTarget == t) score += 12f;
                if (score < bestScore) { bestScore = score; best = t; }
            }
            u.OrderAttack(best, false);
        }
    }

    /// <summary>
    /// More than one enemy battalion has men fighting <paramref name="f"/>'s. Being ordered at it isn't enough: a
    /// second line sent at the same front stands behind its own first line and can't get at it until it works round.
    /// </summary>
    static bool Outnumbered(Battle b, Battalion f)
    {
        Battalion first = null;
        foreach (var e in b.Battalions)
        {
            if (e.Team == f.Team || e.Routing || e.Destroyed || e == first) continue;
            int men = 0;
            foreach (var s in e.Soldiers)
                if (s.Active && !s.Fleeing && s.Target is { } t && t.Unit == f && s.Pos.DistanceTo(t.Pos) <= s.Weapon.Reach + 0.8f && ++men >= EngagedMen) break;
            if (men < EngagedMen) continue;
            if (first != null) return true;
            first = e;
        }
        return false;
    }

    const int EngagedMen = 3;   // men of a battalion fighting ours for it to count as on it

    /// <summary>
    /// Once, at the start: each battalion standing 6 to 40 m behind another of ours, facing the same way and overlapping
    /// it, becomes the reserve of the nearest such one that has none yet (so a third line backs the second).
    /// </summary>
    static void AssignReserves(Battle b, List<Battalion> mine)
    {
        foreach (var u in mine)
        {
            if (u.ReserveFor != null) continue;
            var fwd = MathUtil.Dir(u.Facing);
            Battalion best = null;
            float bestAhead = float.MaxValue;
            foreach (var f in mine)
            {
                if (f == u || f.ReservedBy != null || MathF.Abs(MathUtil.AngleDiff(f.Facing, u.Facing)) > 0.5f) continue;
                var d = f.Centroid - u.Centroid;
                float ahead = d.Dot(fwd), side = MathF.Abs(d.Dot(MathUtil.Right(fwd)));
                if (ahead < 6f || ahead > 40f || side > (f.Width + u.Width) * 0.5f) continue;
                if (ahead < bestAhead) { bestAhead = ahead; best = f; }
            }
            if (best != null) b.AssignReserve(u, best);
        }
    }
}
