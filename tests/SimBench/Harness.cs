using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using UnitFighting.Sim;

namespace SimBench;

/// <summary>Test-only helpers for building small battles and forcing soldiers' attributes.</summary>
public static class H
{
    public const float Dt = Battle.FixedDt;
    public const float North = 0f, South = Mathf.Pi, East = Mathf.Pi / 2f, West = -Mathf.Pi / 2f;

    /// <summary>A copy of a unit type with a different head count (reflection, so new fields are carried over).</summary>
    public static UnitType Sized(UnitType t, int size)
    {
        var c = new UnitType();
        foreach (var f in typeof(UnitType).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!f.IsInitOnly) f.SetValue(c, f.GetValue(t));
        c.Size = size;
        return c;
    }

    /// <summary>Overwrite a man's rolled attributes with average ones and the given skill (derived stats recomputed as in Battalion.Roll).</summary>
    public static void SetMan(Soldier s, float skill, float nerve = 0.6f, bool noFlee = false)
    {
        s.Skill = skill;
        s.Strength = s.Agility = s.Endurance = 1f;
        s.Nerve = nerve;
        s.MaxHp = s.Hp = 100f * (0.85f + 0.15f * s.Strength);
        s.MaxStamina = s.Stamina = 100f * s.Endurance;
        s.Mass = 0.8f + 0.2f * s.Strength + 0.15f * s.Armor.Weight + 0.1f * (s.Shield?.Weight ?? 0f);
        s.BreakPoint = noFlee ? -1f : 10f + (1f - s.Nerve) * 32f;
    }

    public static void Place(Soldier s, Vector2 pos, float facing)
    {
        s.Pos = s.PrevPos = pos;
        s.Facing = s.PrevFacing = facing;
        s.Vel = Vector2.Zero;
    }

    public static void ResetMorale(Battalion u)
    {
        u.Morale = u.MoraleTarget = u.BaseMorale();
    }

    /// <summary>Attack order that is already closed: the men are loose and fighting (no charge run-up).</summary>
    public static void Release(Battalion u, Battalion target)
    {
        u.OrderAttack(target, false);
        u.Engaged = true;
    }

    public static int DeadIn(Battalion u) => u.Soldiers.Count(s => s.Dead);
    public static int EscapedIn(Battalion u) => u.Soldiers.Count(s => s.Escaped);

    public static float Pct(IEnumerable<float> xs, float p)
    {
        var a = xs.Where(x => !float.IsNaN(x)).OrderBy(x => x).ToArray();
        if (a.Length == 0) return float.NaN;
        float idx = p * (a.Length - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return a[lo] + (a[hi] - a[lo]) * (idx - lo);
    }

    public static float Median(IEnumerable<float> xs) => Pct(xs, 0.5f);

    public static float Mean(IEnumerable<float> xs)
    {
        var a = xs.Where(x => !float.IsNaN(x)).ToArray();
        return a.Length == 0 ? float.NaN : a.Average();
    }

    public static string F(float v, int w = 6, int dec = 1) =>
        float.IsNaN(v) ? "-".PadLeft(w) : v.ToString("F" + dec).PadLeft(w);

    public static string P(float frac, int w = 5) => float.IsNaN(frac) ? "-".PadLeft(w) : (100f * frac).ToString("F0").PadLeft(w - 1) + "%";
}

/// <summary>Counts combat events. The battle must have RecordEvents on; call Drain after every step.</summary>
public sealed class EventCounts
{
    public readonly long[] N = new long[Enum.GetValues<EventKind>().Length];

    public void Drain(Battle b)
    {
        foreach (var e in b.Events) N[(int)e.Kind]++;
        b.Events.Clear();
    }

    public long this[EventKind k] => N[(int)k];

    public void Add(EventCounts o)
    {
        lock (N)
            for (int i = 0; i < N.Length; i++) N[i] += o.N[i];
    }

    /// <summary>Of blows that reached the defender (not misses), the share turned by shield or weapon.</summary>
    public float DefendedRate
    {
        get
        {
            float def = this[EventKind.Block] + this[EventKind.Parry] + this[EventKind.GuardBreak];
            float all = def + this[EventKind.Hit] + this[EventKind.Glance] + this[EventKind.Kill];
            return all > 0 ? def / all : float.NaN;
        }
    }

    public string Summary()
    {
        long blk = this[EventKind.Block], par = this[EventKind.Parry], gb = this[EventKind.GuardBreak];
        long hit = this[EventKind.Hit], gl = this[EventKind.Glance], kill = this[EventKind.Kill], miss = this[EventKind.Miss];
        long total = blk + par + gb + hit + gl + kill + miss;
        if (total == 0) return "no blows";
        string p(long x) => (100.0 * x / total).ToString("F1") + "%";
        return $"blows {total}: shield-block {p(blk)}, parry {p(par)}, guard-break {p(gb)}, hit {p(hit)}, glance {p(gl)}, kill {p(kill)}, miss {p(miss)} | impacts {this[EventKind.Impact]}";
    }
}

/// <summary>
/// Invariants that must hold every step. Failures are collected (and printed loudly by Program);
/// the stats (longest overlap, longest unreachable target, longest flight) are reported either way.
/// </summary>
public sealed class Sanity
{
    public static readonly ConcurrentQueue<string> Failures = new();
    static readonly object Lock = new();
    public static float MaxOverlap, MaxStuck, MaxFlee;
    public static long StuckOver20, Checks;

    readonly string _ctx;
    float[] _overlap = new float[64], _stuck = new float[64], _flee = new float[64];
    float _maxOverlap, _maxStuck, _maxFlee;
    long _stuckOver20;
    bool _failed;
    readonly List<Soldier> _q = new();

    public const float OverlapDist = 0.3f, OverlapLimit = 2.0f, StuckLimit = 45f, FleeLimit = 120f;

    public Sanity(string ctx) => _ctx = ctx;

    void Fail(string msg)
    {
        if (_failed) return;   // one per run is enough
        _failed = true;
        Failures.Enqueue($"[{_ctx}] {msg}");
    }

    public void Check(Battle b, float dt)
    {
        if (_overlap.Length < b.NextSoldierId)
        {
            int n = b.NextSoldierId * 2;
            Array.Resize(ref _overlap, n);
            Array.Resize(ref _stuck, n);
            Array.Resize(ref _flee, n);
        }
        foreach (var s in b.Soldiers)
        {
            if (!float.IsFinite(s.Pos.X) || !float.IsFinite(s.Pos.Y) || !float.IsFinite(s.Facing) || !float.IsFinite(s.Vel.X) || !float.IsFinite(s.Hp))
                Fail($"t={b.Time:F2} soldier {s.Id} ({s.Unit.Name}) has non-finite state pos={s.Pos} vel={s.Vel} facing={s.Facing} hp={s.Hp}");
            int id = s.Id;
            if (!s.Active) { _overlap[id] = _stuck[id] = _flee[id] = 0f; continue; }

            // Overlap: another living man closer than 0.3 m, centre to centre.
            bool over = false;
            QueryNear(b, s.Pos, _q);
            foreach (var o in _q)
                if (o != s && o.Active && o.Pos.DistanceSquaredTo(s.Pos) < OverlapDist * OverlapDist) { over = true; break; }
            _overlap[id] = over ? _overlap[id] + dt : 0f;
            if (_overlap[id] > _maxOverlap) _maxOverlap = _overlap[id];
            if (_overlap[id] > OverlapLimit) Fail($"t={b.Time:F2} soldier {s.Id} ({s.Unit.Name}) overlapped another man for {_overlap[id]:F1}s at {s.Pos}");

            // A target he never gets to.
            var t = s.Target;
            bool stuck = t != null && !s.Fleeing && s.Pos.DistanceTo(t.Pos) > s.Weapon.Reach + 0.3f;
            float prev = _stuck[id];
            _stuck[id] = stuck ? _stuck[id] + dt : 0f;
            if (prev < 20f && _stuck[id] >= 20f) _stuckOver20++;
            if (_stuck[id] > _maxStuck) _maxStuck = _stuck[id];
            if (_stuck[id] > StuckLimit) Fail($"t={b.Time:F2} soldier {s.Id} ({s.Unit.Name}, order {s.Unit.Order}) has had an unreachable target for {_stuck[id]:F0}s (d={s.Pos.DistanceTo(t.Pos):F1})");

            // Routing men must get away or rally.
            _flee[id] = s.Fleeing ? _flee[id] + dt : 0f;
            if (_flee[id] > _maxFlee) _maxFlee = _flee[id];
            if (_flee[id] > FleeLimit) Fail($"t={b.Time:F2} soldier {s.Id} ({s.Unit.Name}) has been fleeing for {_flee[id]:F0}s at {s.Pos} without escaping or rallying");
        }
    }

    // The battle's grid was built at the start of the step; men have moved a little since, so search wider and filter.
    static void QueryNear(Battle b, Vector2 p, List<Soldier> into) => b.Grid.Query(p, 0.9f, into);

    public void Commit()
    {
        lock (Lock)
        {
            MaxOverlap = Math.Max(MaxOverlap, _maxOverlap);
            MaxStuck = Math.Max(MaxStuck, _maxStuck);
            MaxFlee = Math.Max(MaxFlee, _maxFlee);
            StuckOver20 += _stuckOver20;
            Checks++;
        }
    }

    public static string Report() =>
        $"sanity: {Checks} runs checked | longest overlap<{OverlapDist}m {MaxOverlap:F2}s (limit {OverlapLimit}) | longest unreachable target {MaxStuck:F1}s (limit {StuckLimit}), >20s episodes {StuckOver20} | longest flight {MaxFlee:F0}s (limit {FleeLimit}) | failures {Failures.Count}";
}
