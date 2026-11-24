using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitFighting.Sim;

namespace SimBench;

/// <summary>Timelines and blow breakdowns for looking inside a fight (not part of "all").</summary>
public static class Diag
{
    static int Mates(Battle b, Soldier d) => Combat.WallMates(b, d);

    public static void Lines(int seeds, UnitType hold = null, UnitType atk = null)
    {
        hold ??= UnitTypes.Legionaries;
        atk ??= UnitTypes.Legionaries;
        var byAngle = new Dictionary<string, long[]>();   // key -> [defended, hit, kill]
        void Count(string key, int k) { if (!byAngle.TryGetValue(key, out var a)) byAngle[key] = a = new long[3]; a[k]++; }
        for (int seed = 0; seed < seeds; seed++)
        {
            var b = new Battle(1000 + seed) { RecordEvents = true };
            var A = b.AddBattalion(0, hold, "Holder", new Vector2(0f, 0f), H.North);
            var B = b.AddBattalion(1, atk, "Attacker", new Vector2(0f, 32f), H.South);
            B.OrderAttack(A, false);
            float next = 0f;
            var ph = new Dictionary<Soldier, CombatPhase>();
            var riposte = new Dictionary<Soldier, bool>();
            if (seed < 3) Console.WriteLine($"-- seed {seed}:  t | dead H A | flee H A | fighting H A | morale H A | stamina H A | threat>1 H A | blows");
            int blows = 0;
            while (b.Time < 200f && (b.Winner < 0 || b.Time < b.EndTime + 10f))
            {
                foreach (var s in b.Soldiers) { ph[s] = s.Phase; riposte[s] = s.Riposte; }
                b.Step(H.Dt);
                foreach (var e in b.Events)
                {
                    if (e.Kind is EventKind.Block or EventKind.Parry or EventKind.GuardBreak or EventKind.Hit or EventKind.Glance or EventKind.Kill)
                    {
                        if (e.Attacker == null || e.Defender == null) continue;
                        blows++;
                        int k = e.Kind is EventKind.Kill ? 2 : e.Kind is EventKind.Hit or EventKind.Glance ? 1 : 0;
                        float rel = Mathf.Abs(MathUtil.RelativeAngle(e.Defender.Facing, e.Attacker.Pos - e.Defender.Pos));
                        string ang = rel < 0.6f ? "front" : rel < 1.4f ? "side" : rel < 2.0f ? "flank" : "rear";
                        char side = e.Defender.Unit.Name[0];
                        Count("angle " + ang, k);
                        Count($"def {side} angle " + ang, k);
                        Count("def threat " + Math.Min(3, e.Defender.ThreatCount), k);
                        Count($"def {side} threat " + Math.Min(3, e.Defender.ThreatCount), k);
                        Count("def fleeing " + e.Defender.Fleeing, k);
                        Count($"def {side} fleeing " + e.Defender.Fleeing, k);
                        Count("def watching attacker " + (e.Defender.Target == e.Attacker), k);
                        Count($"def {side} watching attacker " + (e.Defender.Target == e.Attacker), k);
                        Count("atk charge-bonus " + (b.Time < e.Attacker.ChargeBonusUntil), k);
                        if (b.Time < e.Attacker.ChargeBonusUntil)
                        {
                            Count($"cb def {side} phase(pre) " + ph.GetValueOrDefault(e.Defender), k);
                            Count($"cb def {side} wallmates " + Mates(b, e.Defender), k);
                            Count($"cb def {side} angle " + ang, k);
                        }
                        Count($"def wallmates {e.Defender.Unit.Name[0]} " + Mates(b, e.Defender), k);
                        Count("def phase(pre) " + ph.GetValueOrDefault(e.Defender), k);
                        Count("atk was riposting " + (e.Attacker.PhaseLength < e.Attacker.Weapon.Recover * 1.5f && riposte.GetValueOrDefault(e.Attacker)), k);
                    }
                }
                b.Events.Clear();
                if (seed < 3 && b.Time >= next)
                {
                    next += 2f;
                    int th(Battalion u) => u.Soldiers.Count(s => s.Active && s.ThreatCount > 1);
                    float st(Battalion u) => u.AvgStamina;
                    Console.WriteLine($"   {b.Time,5:F0} | {H.DeadIn(A),3} {H.DeadIn(B),3} | {A.FleeingCount,3} {B.FleeingCount,3} | {A.FightingCount,3} {B.FightingCount,3} | {A.Morale,4:F0} {B.Morale,4:F0} | {st(A),4:F2} {st(B),4:F2} | {th(A),3} {th(B),3} | {blows}{(A.Routing ? " H-ROUT" : "")}{(B.Routing ? " A-ROUT" : "")}");
                }
            }
        }
        Console.WriteLine("\nblows by situation: defended% hit% kill%  (n)");
        if (seeds >= 0) Console.WriteLine($"(of which blows total {byAngle.Where(k => k.Key.StartsWith("angle")).Sum(k => k.Value.Sum())})");
        foreach (var kv in byAngle.OrderBy(k => k.Key))
        {
            long n = kv.Value.Sum();
            Console.WriteLine($"  {kv.Key,-32} {100.0 * kv.Value[0] / n,5:F1} {100.0 * kv.Value[1] / n,5:F1} {100.0 * kv.Value[2] / n,5:F1}  ({n})");
        }
    }
}

public static class Snap
{
    /// <summary>ASCII picture of the field: H/h holders (upper = formed, lower = fighting), A/a attackers, f fleeing, x dead.</summary>
    public static void Print(Battle b, Vector2 center, float half = 9f, float cell = 0.5f)
    {
        int n = (int)(half * 2 / cell);
        var g = new char[n, n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) g[x, y] = '.';
        foreach (var s in b.Soldiers)
        {
            var l = s.Pos - center;
            int x = (int)((l.X + half) / cell), y = (int)((l.Y + half) / cell);
            if (x < 0 || y < 0 || x >= n || y >= n) continue;
            char c = s.Dead ? 'x' : s.Fleeing ? 'f' : s.Team == 0 ? 'H' : 'A';
            if (s.Active && !s.Fleeing && s.Target != null && s.Pos.DistanceTo(s.Target.Pos) < s.Weapon.Reach + 0.3f) c = char.ToLower(c);
            if (s.Active && s.Phase == CombatPhase.Stagger) c = s.Team == 0 ? '!' : '?';
            if (g[x, y] == '.' || g[x, y] == 'x') g[x, y] = c;
        }
        for (int y = n - 1; y >= 0; y--)
        {
            var sb = new System.Text.StringBuilder("   ");
            for (int x = 0; x < n; x++) sb.Append(g[x, y]);
            Console.WriteLine(sb);
        }
    }

    public static void Lines(int seed)
    {
        var b = new Battle(1000 + seed) { RecordEvents = false };
        var A = b.AddBattalion(0, Benches.HoldType, "Holder", new Vector2(0f, 0f), H.North);
        var B = b.AddBattalion(1, Benches.AtkType, "Attacker", new Vector2(0f, 32f), H.South);
        B.OrderAttack(A, false);
        float contact = -1f;
        var shots = new[] { 0f, 2f, 5f, 9f, 15f, 25f };
        int k = 0;
        while (b.Time < 120f && k < shots.Length && !A.Routing && !B.Routing)
        {
            b.Step(H.Dt);
            if (contact < 0 && (A.FightingCount > 0 || B.FightingCount > 0)) contact = b.Time;
            if (contact >= 0 && b.Time >= contact + shots[k])
            {
                Console.WriteLine($"t = contact + {shots[k]}s  dead H {H.DeadIn(A)} A {H.DeadIn(B)}  (north is up; H holds facing north)");
                Print(b, (A.Centroid + B.Centroid) * 0.5f);
                k++;
            }
        }
    }
}

public static class ChargeDiag
{
    public static void Run()
    {
        var b = new Battle(1000) { RecordEvents = true };
        var A = b.AddBattalion(0, Benches.HoldType, "Holder", new Vector2(0f, 0f), H.North);
        var B = b.AddBattalion(1, Benches.AtkType, "Attacker", new Vector2(0f, 32f), H.South);
        B.OrderAttack(A, false);
        float next = 0f;
        while (b.Time < 16f)
        {
            b.Step(H.Dt);
            foreach (var e in b.Events) if (e.Kind == EventKind.Impact) Console.WriteLine($"   impact t={b.Time:F2} ratio {e.Amount:F2}");
            b.Events.Clear();
            if (b.Time >= next)
            {
                next += 0.5f;
                int charging = B.Soldiers.Count(s => s.Charging), withT = B.Soldiers.Count(s => s.Charging && s.Target != null);
                float sp = B.Soldiers.Where(s => s.Charging).Select(s => s.Vel.Length()).DefaultIfEmpty(0).Max();
                float gapFront = B.Soldiers.Min(s => s.Pos.Y) - A.Soldiers.Max(s => s.Pos.Y);
                Console.WriteLine($" t={b.Time,5:F1} engaged={B.Engaged} charging={charging} withTarget={withT} maxSpeed={sp:F1} frontGap={gapFront:F1} targets={B.Soldiers.Count(s => s.Target != null)}");
            }
        }
    }
}

public static class ChargeTrace
{
    public static void Run()
    {
        var b = new Battle(1000) { RecordEvents = false };
        var A = b.AddBattalion(0, Benches.HoldType, "Holder", new Vector2(0f, 0f), H.North);
        var B = b.AddBattalion(1, Benches.AtkType, "Attacker", new Vector2(0f, 32f), H.South);
        B.OrderAttack(A, false);
        var s = B.Soldiers.OrderBy(x => x.Pos.Y).ThenBy(x => Math.Abs(x.Pos.X)).First();   // a front-rank man near the middle
        while (b.Time < 14f)
        {
            b.Step(H.Dt);
            if (b.Time < 10.5f) continue;
            Soldier near = null; float nd = 99f;
            foreach (var e in A.Soldiers) { float d = e.Pos.DistanceTo(s.Pos); if (d < nd) { nd = d; near = e; } }
            Console.WriteLine($" t={b.Time:F2} charging={s.Charging} target={(s.Target != null ? s.Target.Id : -1)} dT={(s.Target != null ? s.Pos.DistanceTo(s.Target.Pos) : -1):F2} vel=({s.Vel.X:F1},{s.Vel.Y:F1}) |v|={s.Vel.Length():F2} phase={s.Phase} nearestEnemy={near.Id} d={nd:F2} ev=({near.Vel.X:F1},{near.Vel.Y:F1}) ephase={near.Phase}");
            if (!s.Charging && b.Time > 12.5f) break;
        }
    }
}

public static class MoraleDiag
{
    public static void Run(int seed)
    {
        var b = new Battle(1000 + seed) { RecordEvents = false };
        var A = b.AddBattalion(0, Benches.HoldType, "Holder", new Vector2(0f, 0f), H.North);
        var B = b.AddBattalion(1, Benches.AtkType, "Attacker", new Vector2(0f, 32f), H.South);
        B.OrderAttack(A, false);
        float next = 11f;
        Console.WriteLine("   t   | H: dead flee fight  F/Fl/R att  stam  morale->target | A: dead flee fight  F/Fl/R att  stam  morale->target");
        string U(Battalion u) => $"{H.DeadIn(u),4} {u.FleeingCount,4} {u.FightingCount,5}  {u.FrontAttackers,2}/{u.FlankAttackers,2}/{u.RearAttackers,2}  {u.AvgStamina,4:F2}  {u.Morale,5:F0}->{u.MoraleTarget,3:F0}{(u.Routing ? " ROUT" : "")}";
        while (b.Time < 60f && b.Winner < 0)
        {
            b.Step(H.Dt);
            if (b.Time >= next) { next += 1f; Console.WriteLine($"  {b.Time,5:F1} | {U(A)} | {U(B)}"); }
        }
    }
}

public static class KillLog
{
    public static void Run(int seed)
    {
        var b = new Battle(1000 + seed) { RecordEvents = true };
        var A = b.AddBattalion(0, Benches.HoldType, "Holder", new Vector2(0f, 0f), H.North);
        var B = b.AddBattalion(1, Benches.AtkType, "Attacker", new Vector2(0f, 32f), H.South);
        B.OrderAttack(A, false);
        var phase = new Dictionary<Soldier, CombatPhase>();
        var hitsOn = new Dictionary<Soldier, List<string>>();
        while (b.Time < 40f && b.Winner < 0)
        {
            foreach (var s in b.Soldiers) phase[s] = s.Phase;
            b.Step(H.Dt);
            foreach (var e in b.Events)
            {
                if (e.Defender == null || e.Attacker == null) continue;
                if (!hitsOn.TryGetValue(e.Defender, out var l)) hitsOn[e.Defender] = l = new List<string>();
                float rel = MathUtil.RelativeAngle(e.Defender.Facing, e.Attacker.Pos - e.Defender.Pos);
                string tag = e.Kind switch { EventKind.Hit => $"H{e.Amount:F0}{e.Location.ToString()[0]}", EventKind.Glance => "g", EventKind.Block => "B", EventKind.Parry => "P", EventKind.Kill => $"K{e.Amount:F0}{e.Location.ToString()[0]}", EventKind.Impact => $"I{e.Amount:F1}", EventKind.GuardBreak => "GB", _ => "" };
                if (tag == "") continue;
                l.Add($"{b.Time:F1}:{tag}[{phase.GetValueOrDefault(e.Defender).ToString()[..2]},th{e.Defender.ThreatCount},{rel:F1}{(e.Defender.Target == e.Attacker ? "" : ",nw")}]");
                if (e.Kind == EventKind.Kill)
                    Console.WriteLine($"KILL t={b.Time:F1} {e.Defender.Unit.Name} #{e.Defender.Id} (skill {e.Defender.Skill:F2}) by #{e.Attacker.Id} (skill {e.Attacker.Skill:F2}) :: {string.Join(" ", l.TakeLast(12))}");
            }
            b.Events.Clear();
        }
    }
}

public static class EngageDiag
{
    /// <summary>In the default battle: how often a man with a target can reach him, how often men drop a living target, how crowded they stand.</summary>
    public static void Run(int seeds)
    {
        long withT = 0, inReach = 0, inReach1 = 0, supportT = 0, frontT = 0, frontReach = 0, switches = 0, manSeconds = 0;
        double crowd = 0; long crowdN = 0; double faceSpread = 0; long faceN = 0;
        for (int seed = 1; seed <= seeds; seed++)
        {
            var b = Scenarios.Default(seed, aiForPlayer: true);
            b.RecordEvents = false;
            var last = new Dictionary<Soldier, Soldier>();
            var q = new List<Soldier>();
            int step = 0;
            while (b.Winner < 0 && b.Time < 90f)
            {
                b.Step(H.Dt);
                step++;
                foreach (var s in b.Soldiers)
                {
                    if (!s.Active || s.Fleeing) { last.Remove(s); continue; }
                    var t = s.Target;
                    if (last.TryGetValue(s, out var prev) && prev != null && prev != t && prev.Active && !prev.Fleeing) switches++;
                    last[s] = t;
                    if (t == null) continue;
                    withT++;
                    float d = s.Pos.DistanceTo(t.Pos);
                    if (d <= s.Weapon.Reach) inReach++;
                    if (d <= s.Weapon.Reach + 1f) inReach1++;
                    bool sup = s.Slot >= 0 && s.Slot < s.Unit.Slots.Count && s.Unit.Slots[s.Slot].Rank > 0;
                    if (sup) supportT++; else { frontT++; if (d <= s.Weapon.Reach) frontReach++; }
                }
                if (step % 60 == 0)
                {
                    foreach (var s in b.Soldiers)
                    {
                        if (!s.Active || s.Fleeing) continue;
                        manSeconds++;
                        if (s.Team != 1 || s.Target == null) continue;
                        b.Grid.Query(s.Pos, 1.0f, q);
                        int c = 0; double sx = 0, sy = 0; int m = 0;
                        foreach (var o in q) if (o != s && o.Team == s.Team && o.Active && !o.Fleeing) { c++; }
                        crowd += c; crowdN++;
                        b.Grid.Query(s.Pos, 2.0f, q);
                        foreach (var o in q) if (o.Team == s.Team && o.Active && !o.Fleeing) { sx += Math.Sin(o.Facing); sy += Math.Cos(o.Facing); m++; }
                        if (m >= 3) { faceSpread += 1 - Math.Sqrt(sx * sx + sy * sy) / m; faceN++; }
                    }
                }
            }
        }
        Console.WriteLine($"with a target: in reach {100.0 * inReach / withT:F0}%, within reach+1m {100.0 * inReach1 / withT:F0}% | front-rank/loose men in reach {100.0 * frontReach / Math.Max(1, frontT):F0}% ({100.0 * frontT / withT:F0}% of targeted man-steps), supporting ranks {100.0 * supportT / withT:F0}%");
        Console.WriteLine($"switches away from a living target: {60.0 * switches / manSeconds:F2} per man per minute ({90.0 * switches / manSeconds:F1} per 90 s)");
        Console.WriteLine($"Gauls with a target: friends within 1 m {crowd / crowdN:F2}; facing spread within 2 m (0 = all same way, 1 = random) {faceSpread / faceN:F2}");
    }
}

public static class ContactDiag
{
    /// <summary>
    /// Hold v attack, averaged over seeds, second by second from first contact: damage and deaths taken, wall mates
    /// and two-on-one among the men fighting, staggers, and how many attackers are loose.
    /// </summary>
    public static void Run(int seeds, UnitType hold, UnitType atk)
    {
        const int T = 160;
        var dmg = new double[2, T]; var dead = new double[2, T]; var mates = new double[2, T]; var matesN = new double[2, T];
        var two = new double[2, T]; var stag = new double[2, T]; var loose = new double[T]; var running = new double[T]; var mor = new double[2, T]; var flk = new double[2, T]; var flee = new double[2, T]; var rearGap = new double[T]; var rearN = new double[T]; var rankDepth = new double[T]; var rankDepthN = new double[T]; var stam = new double[2, T];
        for (int seed = 0; seed < seeds; seed++)
        {
            var b = new Battle(1000 + seed) { RecordEvents = false };
            var A = b.AddBattalion(0, hold, "Holder", new Vector2(0f, 0f), H.North);
            var B = b.AddBattalion(1, atk, "Attacker", new Vector2(0f, 32f), H.South);
            B.OrderAttack(A, false);
            float contact = -1f;
            var hp = b.Soldiers.ToDictionary(s => s, s => s.Hp);
            while (b.Time < 200f && b.Winner < 0)
            {
                b.Step(H.Dt);
                if (contact < 0f && (A.FightingCount > 0 || B.FightingCount > 0)) contact = b.Time;
                if (contact < 0f) continue;
                int k = (int)(b.Time - contact);
                if (k >= T) break;
                running[k] += H.Dt;
                {
                    // Depth between the attacker's front rank and the rest, along its facing.
                    var fwd = MathUtil.Dir(B.Facing);
                    double fr = 0, rr = 0; int nf = 0, nr = 0;
                    foreach (var s in B.Soldiers)
                    {
                        if (!s.Active || s.Fleeing || s.Slot < 0 || s.Slot >= B.Slots.Count) continue;
                        if (B.Slots[s.Slot].Rank == 0) { fr += s.Pos.Dot(fwd); nf++; } else { rr += s.Pos.Dot(fwd); nr++; }
                    }
                    if (nf > 0 && nr > 0) { rankDepth[k] += (fr / nf - rr / nr) * H.Dt; rankDepthN[k] += H.Dt; }
                }
                foreach (var u in new[] { A, B })
                {
                    mor[u.Team, k] += u.Morale * H.Dt;
                    flk[u.Team, k] += (u.FlankAttackers + u.RearAttackers) * H.Dt;
                    flee[u.Team, k] += u.FleeingCount * H.Dt;
                }
                foreach (var s in b.Soldiers)
                {
                    int side = s.Team;
                    float lost = hp[s] - Math.Max(0f, s.Hp);
                    if (lost > 0f) dmg[side, k] += lost;
                    hp[s] = Math.Max(0f, s.Hp);
                    if (s.Dead && s.DeathTime > b.Time - H.Dt * 0.5f) dead[side, k]++;
                    if (!s.Active || s.Fleeing) continue;
                    var t = s.Target;
                    if (t != null && s.Pos.DistanceTo(t.Pos) <= s.Weapon.Reach + 0.3f)
                    {
                        mates[side, k] += Combat.WallMates(b, s) * H.Dt;
                        matesN[side, k] += H.Dt;
                        if (s.ThreatCount > 1) two[side, k] += H.Dt;
                    }
                    if (s.Phase is CombatPhase.Stagger or CombatPhase.Down) stag[side, k] += H.Dt;
                    if (side == 1 && s.Loose && s.Unit.Released && !SoldierAI.Supporting(s)) loose[k] += H.Dt;
                    if (side == 1 && SoldierAI.Supporting(s)) { rearGap[k] += s.Pos.DistanceTo(s.Unit.SlotWorld(s.Slot)) * H.Dt; rearN[k] += H.Dt; }
                    stam[side, k] += s.StaminaFrac * H.Dt / 20f;
                }
            }
        }
        Console.WriteLine($"{hold.Name} holding v {atk.Name} attacking, {seeds} seeds; from contact, mean per battle: damage and deaths so far,");
        Console.WriteLine("then in that second: man-seconds of wall mates (mean), fighting, 2-on-1, staggered, attackers loose; stamina");
        Console.WriteLine("   s | cum dmg H   A  | cum dead H   A | mates H   A  | fighting H  A | 2-on-1 H  A | stag H  A | loose A | stam H  A | running | morale H A | flank+rear att H A | fleeing H A | A rear ranks from slots m, front-to-rear depth m");
        double cdH = 0, cdA = 0, cxH = 0, cxA = 0;
        for (int k = 0; k < T; k++)
        {
            double n = seeds;
            cdH += dmg[0, k] / n; cdA += dmg[1, k] / n; cxH += dead[0, k] / n; cxA += dead[1, k] / n;
            if (k >= 10 && k % 10 != 9) continue;   // the first ten seconds one by one, then every tenth
            Console.WriteLine($"  {k,3} | {cdH,8:F0} {cdA,5:F0} | {cxH,8:F1} {cxA,4:F1} | {mates[0, k] / Math.Max(1e-9, matesN[0, k]),6:F2} {mates[1, k] / Math.Max(1e-9, matesN[1, k]),4:F2} | {matesN[0, k] / n,8:F1} {matesN[1, k] / n,4:F1} | {two[0, k] / n,6:F1} {two[1, k] / n,4:F1} | {stag[0, k] / n,4:F1} {stag[1, k] / n,4:F1} | {loose[k] / n,5:F1} | {stam[0, k] / n,4:F2} {stam[1, k] / n,4:F2} | {running[k] / n,4:F2} | {mor[0, k] / Math.Max(1e-9, running[k]),4:F0} {mor[1, k] / Math.Max(1e-9, running[k]),3:F0} | {flk[0, k] / Math.Max(1e-9, running[k]),5:F2} {flk[1, k] / Math.Max(1e-9, running[k]),4:F2} | {flee[0, k] / Math.Max(1e-9, running[k]),4:F1} {flee[1, k] / Math.Max(1e-9, running[k]),3:F1} | {rearGap[k] / Math.Max(1e-9, rearN[k]),4:F1} {rankDepth[k] / Math.Max(1e-9, rankDepthN[k]),4:F1}");
        }
    }
}
