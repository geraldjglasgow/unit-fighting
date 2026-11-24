using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using UnitFighting.Sim;

namespace SimBench;

public static class Benches
{
    public static int Seeds = 0;   // 0: each bench's default
    public static UnitType HoldType = UnitTypes.Legionaries, AtkType = UnitTypes.Legionaries;

    /// <summary>"Owner.Field=value": Owner is a UnitTypes or Gear static field (e.g. Warband, Longsword, Scutum, Mail).</summary>
    public static void Set(string spec)
    {
        var eq = spec.IndexOf('=');
        var dot = spec.IndexOf('.');
        if (eq < 0 || dot < 0 || dot > eq) throw new ArgumentException($"bad --set {spec}");
        string owner = spec[..dot], field = spec[(dot + 1)..eq], val = spec[(eq + 1)..];
        // A public static field of a sim class (tuning knobs such as Battle.ReliefWorn).
        var st = typeof(Battle).Assembly.GetType("UnitFighting.Sim." + owner)?.GetField(field, BindingFlags.Public | BindingFlags.Static);
        if (st != null && !st.IsLiteral && !st.IsInitOnly)
        {
            st.SetValue(null, st.FieldType == typeof(float) ? float.Parse(val, System.Globalization.CultureInfo.InvariantCulture)
                            : st.FieldType == typeof(bool) ? bool.Parse(val) : int.Parse(val));
            Console.WriteLine($"(set {owner}.{field} = {st.GetValue(null)})");
            return;
        }
        object target = typeof(UnitTypes).GetField(owner)?.GetValue(null) ?? typeof(Gear).GetField(owner)?.GetValue(null)
                        ?? throw new ArgumentException($"no UnitTypes/Gear field {owner}");
        var f = target.GetType().GetField(field) ?? throw new ArgumentException($"{owner} has no field {field}");
        object v = f.FieldType == typeof(float) ? float.Parse(val, System.Globalization.CultureInfo.InvariantCulture)
                 : f.FieldType == typeof(int) ? int.Parse(val)
                 : f.FieldType == typeof(bool) ? bool.Parse(val)
                 : throw new ArgumentException($"can't set {f.FieldType.Name}");
        f.SetValue(target, v);
        Console.WriteLine($"(set {owner}.{field} = {v})");
    }

    public static UnitType TypeByName(string name) =>
        (UnitType)typeof(UnitTypes).GetField(name)?.GetValue(null) ?? throw new ArgumentException($"no UnitTypes.{name}");
    static int N(int dflt) => Seeds > 0 ? Seeds : dflt;

    static T[] Par<T>(int n, Func<int, T> f)
    {
        var r = new T[n];
        Parallel.For(0, n, i => r[i] = f(i));
        return r;
    }

    // ================================================================ a. duels

    sealed class DuelRun
    {
        public int Winner = -1;   // 0 = A, 1 = B, -1 = nobody died
        public float Time;
        public int Strikes;
        public readonly EventCounts Ev = new();
    }

    static DuelRun Duel(int seed, UnitType kit, float skillA, float skillB, bool charge) => Duel(seed, kit, kit, skillA, skillB, charge);

    static DuelRun Duel(int seed, UnitType kitA, UnitType kitB, float skillA, float skillB, bool charge)
    {
        var b = new Battle(seed * 7919 + 17) { RecordEvents = true };
        var ua = b.AddBattalion(0, H.Sized(kitA, 1), "A", new Vector2(0f, 0f), H.North);
        var ub = b.AddBattalion(1, H.Sized(kitB, 1), "B", new Vector2(0f, 3f), H.South);
        var a = ua.Soldiers[0];
        var d = ub.Soldiers[0];
        H.SetMan(a, skillA, noFlee: true);
        H.SetMan(d, skillB, noFlee: true);
        H.Place(a, new Vector2(0f, 0f), H.North);
        H.Place(d, new Vector2(0f, 3f), H.South);
        H.ResetMorale(ua);
        H.ResetMorale(ub);
        if (charge) { ua.OrderAttack(ub, true); ub.OrderAttack(ua, true); }
        else { H.Release(ua, ub); H.Release(ub, ua); }

        var r = new DuelRun();
        var san = new Sanity($"duel {skillA}v{skillB} seed {seed}");
        while (b.Time < 180f && a.Active && d.Active)
        {
            b.Step(H.Dt);
            r.Ev.Drain(b);
            san.Check(b, H.Dt);
        }
        san.Commit();
        r.Winner = !a.Active ? 1 : !d.Active ? 0 : -1;
        r.Time = r.Winner == 1 ? a.DeathTime : r.Winner == 0 ? d.DeathTime : b.Time;
        r.Strikes = a.StrikesMade + d.StrikesMade;
        return r;
    }

    public static void Duels(bool charge = false)
    {
        int n = N(600);
        Console.WriteLine($"\n== a. 1v1 duels, Legionary kit, equal attributes except skill ({n} seeds each{(charge ? ", charging in" : "")}) ==");
        Console.WriteLine("  skill A v B |  A win | draw | median s |  p90 s | blows/kill | hits/kill | defended");
        var pairs = new (float, float)[] { (0.5f, 0.5f), (0.6f, 0.5f), (0.7f, 0.5f), (0.9f, 0.5f), (0.3f, 0.7f), (0.2f, 0.2f), (0.8f, 0.8f) };
        foreach (var (sa, sb) in pairs)
        {
            var runs = Par(n, i => Duel(i, UnitTypes.Legionaries, sa, sb, charge));
            var ev = new EventCounts();
            foreach (var r in runs) ev.Add(r.Ev);
            float win = runs.Count(r => r.Winner == 0) / (float)n;
            float draw = runs.Count(r => r.Winner < 0) / (float)n;
            var times = runs.Where(r => r.Winner >= 0).Select(r => r.Time).ToList();
            float blowsPerKill = runs.Where(r => r.Winner >= 0).Sum(r => r.Strikes) / (float)Math.Max(1, times.Count);
            float hits = ev[EventKind.Hit] + ev[EventKind.Glance] + ev[EventKind.Kill];
            float hitsPerKill = hits / Math.Max(1, times.Count);
            Console.WriteLine($"  {sa:F1} v {sb:F1}   | {H.P(win, 6)} | {H.P(draw, 4)} | {H.F(H.Median(times), 8)} | {H.F(H.Pct(times, 0.9f), 6)} | {H.F(blowsPerKill, 10)} | {H.F(hitsPerKill, 9)} | {H.P(ev.DefendedRate, 8)} |");
        }
        // Event breakdown for the equal pair.
        var eq = Par(n, i => Duel(i, UnitTypes.Legionaries, 0.5f, 0.5f, charge));
        var e2 = new EventCounts();
        foreach (var r in eq) e2.Add(r.Ev);
        Console.WriteLine("  0.5v0.5 " + e2.Summary());
    }

    /// <summary>One line for parameter sweeps: equal duel, skilled duel, and the 20v20 line fight.</summary>
    public static void Quick()
    {
        int n = N(300);
        var eq = Par(n, i => Duel(i, UnitTypes.Legionaries, 0.5f, 0.5f, false));
        var sk = Par(n, i => Duel(i, UnitTypes.Legionaries, 0.7f, 0.5f, false));
        var s9 = Par(n, i => Duel(i, UnitTypes.Legionaries, 0.9f, 0.5f, false));
        var lines = Par(Math.Max(40, n / 5), i =>
        {
            var (b, _, _) = HoldVsAttack(1000 + i, UnitTypes.Legionaries, UnitTypes.Legionaries);
            return Run(b, i, "quick", 400f, true);
        });
        var done = lines.Where(r => r.Winner >= 0).ToArray();
        var melee = done.Select(r => r.LoserRout - r.Contact).ToList();
        float routShare = done.Sum(r => r.LoserDeathsRouting) / (float)Math.Max(1, done.Sum(r => r.LoserDeaths));
        Console.WriteLine($"duel .5v.5 med {H.Median(eq.Select(r => r.Time)):F1}s p90 {H.Pct(eq.Select(r => r.Time), 0.9f):F1} | .7v.5 win {H.P(sk.Count(r => r.Winner == 0) / (float)n)} | .9v.5 win {H.P(s9.Count(r => r.Winner == 0) / (float)n)} || lines melee med {H.Median(melee):F1}s p10-90 {H.Pct(melee, 0.1f):F0}-{H.Pct(melee, 0.9f):F0} hold {H.P(lines.Count(r => r.Winner == 0) / (float)lines.Length)} deadAtRout {H.Mean(done.Select(r => (float)r.DeadAtRout[1 - r.Winner])):F1}/{H.Mean(done.Select(r => (float)r.DeadAtRout[r.Winner])):F1} (loser/winner) end {H.Mean(done.Select(r => (float)r.DeadEnd[1 - r.Winner])):F1}/{H.Mean(done.Select(r => (float)r.DeadEnd[r.Winner])):F1} routShare {H.P(routShare)} unresolved {lines.Length - done.Length}");
    }

    /// <summary>Equal men (skill 0.5, average build) with different kit: does the gear alone decide it?</summary>
    public static void KitDuels()
    {
        int n = N(400);
        Console.WriteLine($"\n== kit duels: equal men (skill 0.5, average build), different equipment, {n} seeds ==");
        Console.WriteLine("  A kit            v B kit            |  A win | median s | blows/kill");
        var kits = new[] { UnitTypes.Legionaries, UnitTypes.Warband, UnitTypes.Axemen, UnitTypes.AuxSpearmen, UnitTypes.ChosenSwordsmen };
        for (int i = 0; i < kits.Length; i++)
        for (int j = i + 1; j < kits.Length; j++)
        {
            var (ka, kb) = (kits[i], kits[j]);
            var runs = Par(n, k => Duel(k, ka, kb, 0.5f, 0.5f, false));
            var times = runs.Where(r => r.Winner >= 0).Select(r => r.Time).ToList();
            Console.WriteLine($"  {Short(ka),-16} v {Short(kb),-16} | {H.P(runs.Count(r => r.Winner == 0) / (float)n, 6)} | {H.F(H.Median(times), 8)} | {H.F(runs.Where(r => r.Winner >= 0).Sum(r => r.Strikes) / (float)Math.Max(1, times.Count), 10)}");
        }
    }

    /// <summary>Every headline metric in a few lines, for tuning.</summary>
    public static void Tune()
    {
        int n = N(200);
        var eq = Par(n, i => Duel(i, UnitTypes.Legionaries, 0.5f, 0.5f, false));
        var s7 = Par(n, i => Duel(i, UnitTypes.Legionaries, 0.7f, 0.5f, false));
        var s9 = Par(n, i => Duel(i, UnitTypes.Legionaries, 0.9f, 0.5f, false));
        var ov = Par(n, i => OneVTwo(i, 0.8f, -1f));
        Console.WriteLine($"DUEL .5v.5 med {H.Median(eq.Select(r => r.Time)):F1} p90 {H.Pct(eq.Select(r => r.Time), 0.9f):F1} | .7v.5 {H.P(s7.Count(r => r.Winner == 0) / (float)n)} | .9v.5 {H.P(s9.Count(r => r.Winner == 0) / (float)n)} | 1v2(.8) win {H.P(ov.Count(r => r.HeroWon) / (float)n)} lasts {H.Median(ov.Select(r => r.Lasted)):F1}s");

        int m = Math.Max(40, n / 4);
        var lines = Par(m, i => { var (b, _, _) = HoldVsAttack(1000 + i, UnitTypes.Legionaries, UnitTypes.Legionaries); return Run(b, i, "tune lines", 400f, true); });
        var done = lines.Where(r => r.Winner >= 0).ToArray();
        var melee = done.Select(r => r.LoserRout - r.Contact).ToList();
        float routShare = done.Sum(r => r.LoserDeathsRouting) / (float)Math.Max(1, done.Sum(r => r.LoserDeaths));
        var men = lines.SelectMany(r => r.Men).ToList();
        float Surv(IEnumerable<ManRec> g) { var l = g.ToList(); return l.Count == 0 ? float.NaN : l.Count(x => !x.DeadAtEnd) / (float)l.Count; }
        Console.WriteLine($"LINES melee {H.Median(melee):F0}s ({H.Pct(melee, 0.1f):F0}-{H.Pct(melee, 0.9f):F0}) hold {H.P(lines.Count(r => r.Winner == 0) / (float)m)} | dead@rout L/W {H.Mean(done.Select(r => (float)r.DeadAtRout[1 - r.Winner])):F1}/{H.Mean(done.Select(r => (float)r.DeadAtRout[r.Winner])):F1} end {H.Mean(done.Select(r => (float)r.DeadEnd[1 - r.Winner])):F1}/{H.Mean(done.Select(r => (float)r.DeadEnd[r.Winner])):F1} | rout-share {H.P(routShare)} | skill Q1 {H.P(Surv(men.Where(x => x.Rank == 0)))} Q4 {H.P(Surv(men.Where(x => x.Rank == 3)))} | unresolved {m - done.Length}");

        var fl = new List<string>();
        foreach (var mode in new[] { Reinforce.None, Reinforce.Front, Reinforce.Rear })
        {
            var runs = Par(m, i => FlankRun(i, mode, 20f));
            var toRout = runs.Where(r => r.BRout >= 0f).Select(r => mode == Reinforce.None ? r.BRout - (r.Contact + 20f) : r.CContact >= 0f ? r.BRout - r.CContact : float.NaN).ToList();
            fl.Add($"{mode} B-routs {H.P(runs.Count(r => r.BRout >= 0f) / (float)m)} in {H.Median(toRout):F1}s C-dead {H.Mean(runs.Select(r => (float)r.CDead)):F1}");
        }
        Console.WriteLine("FLANK " + string.Join(" | ", fl));

        var mu = new List<string>();
        foreach (var (hold, atk) in new (UnitType, UnitType)[] {
            (UnitTypes.Legionaries, UnitTypes.Warband), (UnitTypes.Warband, UnitTypes.Legionaries), (UnitTypes.Veterans, UnitTypes.ChosenSwordsmen),
            (UnitTypes.AuxSpearmen, UnitTypes.Warband), (UnitTypes.Legionaries, UnitTypes.AuxSpearmen), (UnitTypes.AuxSpearmen, UnitTypes.Legionaries),
            (UnitTypes.SpearLevy, UnitTypes.Legionaries), (UnitTypes.Legionaries, UnitTypes.Axemen) })
        {
            var runs = Par(m, i => { var (b, _, _) = HoldVsAttack(3000 + i, hold, atk); return Run(b, i, "tune mu", 400f, true); });
            var d2 = runs.Where(r => r.Winner >= 0).Select(r => r.LoserRout - r.Contact).ToList();
            mu.Add($"{Short(hold)[..4]}<{Short(atk)[..4]} {H.P(runs.Count(r => r.Winner == 0) / (float)m).Trim()} {H.Median(d2):F0}s");
        }
        Console.WriteLine("MATCH (holder win) " + string.Join(" | ", mu));
        var mm = new List<string>();
        foreach (var (x, y) in new (UnitType, UnitType)[] {
            (UnitTypes.Legionaries, UnitTypes.Warband), (UnitTypes.Veterans, UnitTypes.ChosenSwordsmen), (UnitTypes.AuxSpearmen, UnitTypes.Warband),
            (UnitTypes.Legionaries, UnitTypes.AuxSpearmen), (UnitTypes.Legionaries, UnitTypes.SpearLevy), (UnitTypes.Legionaries, UnitTypes.Axemen), (UnitTypes.AuxSpearmen, UnitTypes.Axemen) })
        {
            var runs = Par(m, i => { var (b, a, c) = HoldVsAttack(4000 + i, x, y); a.OrderAttack(c, false); return Run(b, i, "tune mm", 400f, true); });
            var d2 = runs.Where(r => r.Winner >= 0).Select(r => r.LoserRout - r.Contact).ToList();
            mm.Add($"{Short(x)[..4]}-{Short(y)[..4]} {H.P(runs.Count(r => r.Winner == 0) / (float)m).Trim()} {H.Median(d2):F0}s");
        }
        Console.WriteLine("MUTUAL (first wins) " + string.Join(" | ", mm));

        var df = Par(Math.Max(30, n / 6), i =>
        {
            var b = Scenarios.Default(i + 1, aiForPlayer: true);
            b.RecordEvents = false;
            while (b.Winner < 0 && b.Time < 400f) b.Step(H.Dt);
            return (b.Winner, b.EndTime, b.Dead(0), b.Dead(1));
        });
        Console.WriteLine($"DEFAULT Rome {H.P(df.Count(r => r.Winner == 0) / (float)df.Length)} unfinished {df.Count(r => r.Winner < 0)} dur {H.Median(df.Where(r => r.Winner >= 0).Select(r => r.EndTime)):F0}s dead R/G {df.Average(r => r.Item3):F0}/{df.Average(r => r.Item4):F0}");
    }

    /// <summary>
    /// A battalion charges a holding one from about 28 m. Counts every impact: how hard (ratio: above 1 staggers,
    /// above 1.6 knocks down) and how fast the men were closing.
    /// </summary>
    public static void Charges()
    {
        int n = N(40);
        Console.WriteLine($"\n== charge impacts: attacker charges a holding line from ~28 m ({n} seeds each) ==");
        Console.WriteLine("  holder (formation) <- charger (formation, from) | impacts/charge | closing m/s med | ratio med  p90 | staggered/charge | knocked down/charge | holder win | charger's ranks strung out at impact, m");
        var cases = new (UnitType hold, FormationType hf, UnitType atk, FormationType af, string from)[]
        {
            (UnitTypes.Legionaries, FormationType.Line, UnitTypes.Warband, FormationType.Line, "front"),
            (UnitTypes.Legionaries, FormationType.Deep, UnitTypes.Warband, FormationType.Line, "front"),
            (UnitTypes.Legionaries, FormationType.Thin, UnitTypes.Warband, FormationType.Line, "front"),
            (UnitTypes.Legionaries, FormationType.Line, UnitTypes.Warband, FormationType.Wedge, "front"),
            (UnitTypes.Legionaries, FormationType.Line, UnitTypes.Warband, FormationType.Line, "flank"),
            (UnitTypes.Legionaries, FormationType.Line, UnitTypes.Warband, FormationType.Line, "rear"),
            (UnitTypes.AuxSpearmen, FormationType.Line, UnitTypes.Axemen, FormationType.Line, "front"),
            (UnitTypes.Legionaries, FormationType.Line, UnitTypes.Legionaries, FormationType.Line, "front"),
        };
        foreach (var c in cases)
        {
            var ratios = new List<float>();
            var closing = new List<float>();
            int staggers = 0, downs = 0, holdWins = 0;
            var spread = new List<float>();
            var runs = Par(n, i =>
            {
                var b = new Battle(5000 + i) { RecordEvents = true };
                var a = b.AddBattalion(0, c.hold, "Holder", Vector2.Zero, H.North);
                if (c.hf != FormationType.Line) a.SetFormation(c.hf);
                var at = c.from switch { "flank" => new Vector2(28f, 0f), "rear" => new Vector2(0f, -28f), _ => new Vector2(0f, 28f) };
                var u = b.AddBattalion(1, c.atk, "Charger", at, MathUtil.Angle(-at));
                if (c.af != FormationType.Line) u.SetFormation(c.af);
                // Formations re-shape around the men where they stand; give them a moment to settle.
                for (int k = 0; k < 120; k++) b.Step(H.Dt);
                u.OrderAttack(a, true);
                var rs = new List<(float ratio, float closing)>();
                float strung = float.NaN;
                while (b.Time < 300f && b.Winner < 0)
                {
                    var before = b.Soldiers.ToDictionary(s => s, s => s.Vel);
                    b.Step(H.Dt);
                    foreach (var e in b.Events)
                        if (e.Kind == EventKind.Impact && e.Attacker != null && e.Defender != null)
                        {
                            var nrm = (e.Defender.Pos - e.Attacker.Pos).Normalized();
                            rs.Add((e.Amount, (before[e.Attacker] - before[e.Defender]).Dot(nrm)));
                            if (float.IsNaN(strung)) strung = StrungOut(u);
                        }
                    b.Events.Clear();
                }
                return (rs, win: b.Winner == 0, strung);
            });
            foreach (var (rs, win, strung) in runs)
            {
                spread.Add(strung);
                foreach (var (r, cl) in rs) { ratios.Add(r); closing.Add(cl); if (r > 1.6f) downs++; else if (r > 1f) staggers++; }
                if (win) holdWins++;
            }
            string label = $"{Short(c.hold)} ({c.hf}) <- {Short(c.atk)} ({c.af}, {c.from})";
            Console.WriteLine($"  {label,-48} | {ratios.Count / (float)n,14:F1} | {H.F(H.Median(closing), 15)} | {H.F(H.Median(ratios), 9, 2)} {H.F(H.Pct(ratios, 0.9f), 4, 2)} | {staggers / (float)n,16:F1} | {downs / (float)n,19:F1} | {H.P(holdWins / (float)n, 10)} | {H.F(H.Median(spread), 5)} (p90 {H.F(H.Pct(spread, 0.9f), 3)})");
        }
    }

    /// <summary>
    /// How far a formation's rear rank has fallen behind its front rank beyond their places in the formation (0 = the
    /// ranks are as close as when formed), measured along its facing.
    /// </summary>
    static float StrungOut(Battalion u)
    {
        var fwd = MathUtil.Dir(u.Facing);
        float front = 0f, rear = 0f;
        int nf = 0, nr = 0, last = 0;
        foreach (var s in u.Soldiers)
            if (s.Active && !s.Fleeing && s.Slot >= 0 && s.Slot < u.Slots.Count) last = Math.Max(last, u.Slots[s.Slot].Rank);
        if (last == 0) return 0f;
        foreach (var s in u.Soldiers)
        {
            if (!s.Active || s.Fleeing || s.Slot < 0 || s.Slot >= u.Slots.Count) continue;
            int rank = u.Slots[s.Slot].Rank;
            if (rank == 0) { front += s.Pos.Dot(fwd); nf++; }
            else if (rank == last) { rear += s.Pos.Dot(fwd); nr++; }
        }
        return nf > 0 && nr > 0 ? front / nf - rear / nr - last * Formation.RankSpacing : float.NaN;
    }

    // ================================================================ b. one good man against two

    sealed class OneVTwoRun
    {
        public bool HeroWon, Timeout;
        public float Lasted;
        public int HeroKills;
    }

    static OneVTwoRun OneVTwo(int seed, float heroSkill, float side)
    {
        var b = new Battle(seed * 104729 + 3) { RecordEvents = false };
        var uh = b.AddBattalion(0, H.Sized(UnitTypes.Legionaries, 1), "Hero", new Vector2(0f, 0f), H.North);
        var up = b.AddBattalion(1, H.Sized(UnitTypes.Legionaries, 2), "Pair", new Vector2(0f, 3f), H.South);
        var hero = uh.Soldiers[0];
        H.SetMan(hero, heroSkill, noFlee: true);
        H.Place(hero, Vector2.Zero, H.North);
        H.SetMan(up.Soldiers[0], 0.5f, noFlee: true);
        H.Place(up.Soldiers[0], new Vector2(0f, 3f), H.South);
        H.SetMan(up.Soldiers[1], 0.5f, noFlee: true);
        // side +1: on his left (shield side); -1: on his right (sword side).
        H.Place(up.Soldiers[1], new Vector2(side * 3f, 0f), side > 0 ? H.West : H.East);
        H.ResetMorale(uh);
        H.ResetMorale(up);
        H.Release(uh, up);
        H.Release(up, uh);
        var san = new Sanity($"1v2 seed {seed}");
        while (b.Time < 240f && hero.Active && up.Alive > 0)
        {
            b.Step(H.Dt);
            san.Check(b, H.Dt);
        }
        san.Commit();
        return new OneVTwoRun
        {
            HeroWon = hero.Active && up.Alive == 0,
            Timeout = hero.Active && up.Alive > 0,
            Lasted = hero.Active ? b.Time : hero.DeathTime,
            HeroKills = hero.Kills,
        };
    }

    public static void OneVsTwo()
    {
        int n = N(600);
        Console.WriteLine($"\n== b. one man vs two 0.5 men (one in front, one on a flank), {n} seeds ==");
        Console.WriteLine("  hero skill | flank side   | hero wins | median lasted s | p90 s | mean kills");
        foreach (var skill in new[] { 0.8f, 0.5f, 0.95f })
        foreach (var side in new[] { -1f, 1f })
        {
            var runs = Par(n, i => OneVTwo(i, skill, side));
            float win = runs.Count(r => r.HeroWon) / (float)n;
            var lasted = runs.Select(r => r.Lasted).ToList();
            Console.WriteLine($"  {skill,10:F2} | {(side < 0 ? "sword (R)" : "shield (L)"),-12} | {H.P(win, 9)} | {H.F(H.Median(lasted), 15)} | {H.F(H.Pct(lasted, 0.9f), 5)} | {H.F(runs.Average(r => (float)r.HeroKills), 10, 2)}");
        }
    }

    // ================================================================ c/d/e. unit battles

    public sealed class ManRec
    {
        public float Skill;
        public int Team, Kills, Rank;   // Rank: skill quartile within his unit, 0 = best
        public bool DeadAtRout, DeadAtEnd;
    }

    public sealed class BattleRun
    {
        public int Seed, Winner = -1;
        public float Contact = -1f, LoserRout = -1f, End;
        public int[] DeadAtRout = new int[2], DeadEnd = new int[2], EscEnd = new int[2], Initial = new int[2];
        public int LoserDeaths, LoserDeathsRouting;
        public bool Rallied, Annihilated;
        public float CContact = -1f, BRout = -1f;   // flank test
        public int CDead, CDeadAtBRout, ADeadAtBRout, BDeadAtBRout;
        public readonly List<ManRec> Men = new();
        public EventCounts Ev;
    }

    /// <summary>
    /// Steps a battle until the loser has left the field (or 90 s after it broke, or the cap).
    /// If <paramref name="pursue"/>, a winning battalion with nothing to do is sent after the routing enemy,
    /// as a player would. <paramref name="hook"/> runs after every step.
    /// </summary>
    static BattleRun Run(Battle b, int seed, string ctx, float cap, bool pursue, Action<Battle, BattleRun> hook = null, EventCounts ev = null)
    {
        var r = new BattleRun { Seed = seed, Ev = ev };
        var routStart = new Dictionary<Battalion, float>();
        var deadAtRout = new Dictionary<Soldier, bool>();
        var san = new Sanity(ctx);
        bool atRoutTaken = false;
        while (b.Time < cap)
        {
            b.Step(H.Dt);
            if (ev != null) ev.Drain(b);
            san.Check(b, H.Dt);
            foreach (var u in b.Battalions)
            {
                if (r.Contact < 0f && u.FightingCount > 0) r.Contact = b.Time;
                if (u.Routing && !routStart.ContainsKey(u)) routStart[u] = b.Time;
                if (!u.Routing && routStart.ContainsKey(u) && !u.Destroyed) r.Rallied = true;
            }
            hook?.Invoke(b, r);
            if (b.Winner >= 0 && !atRoutTaken)
            {
                atRoutTaken = true;
                r.Winner = b.Winner;
                r.LoserRout = b.EndTime;
                for (int t = 0; t < 2; t++) r.DeadAtRout[t] = b.Dead(t);
                foreach (var s in b.Soldiers) deadAtRout[s] = s.Dead;
            }
            if (b.Winner >= 0)
            {
                int loser = 1 - b.Winner;
                if (pursue)
                    foreach (var u in b.Battalions)
                    {
                        if (u.Team != b.Winner || u.Routing || u.Destroyed) continue;
                        if (u.Order == OrderType.Attack && u.AttackTarget is { Destroyed: false }) continue;
                        Battalion best = null;
                        float bd = 60f;
                        foreach (var o in b.Battalions)
                        {
                            if (o.Team != loser || o.Destroyed) continue;
                            float d = o.Centroid.DistanceTo(u.Centroid);
                            if (d < bd) { bd = d; best = o; }
                        }
                        if (best != null && u.Order != OrderType.Attack) u.OrderAttack(best, true);
                    }
                bool gone = b.Battalions.Where(u => u.Team == loser).All(u => u.Destroyed);
                if (gone || b.Time > b.EndTime + 90f) break;
            }
        }
        san.Commit();
        r.End = b.Time;
        for (int t = 0; t < 2; t++)
        {
            r.DeadEnd[t] = b.Dead(t);
            r.EscEnd[t] = b.Soldiers.Count(s => s.Team == t && s.Escaped);
            r.Initial[t] = b.Soldiers.Count(s => s.Team == t);
        }
        if (r.Winner >= 0)
        {
            int loser = 1 - r.Winner;
            r.LoserDeaths = r.DeadEnd[loser];
            r.LoserDeathsRouting = b.Soldiers.Count(s => s.Team == loser && s.Dead && routStart.TryGetValue(s.Unit, out var rt) && s.DeathTime >= rt);
            r.Annihilated = r.DeadEnd[loser] == r.Initial[loser];
        }
        foreach (var u in b.Battalions)
        {
            var men = u.Soldiers.OrderByDescending(s => s.Skill).ToList();
            for (int i = 0; i < men.Count; i++)
            {
                var s = men[i];
                r.Men.Add(new ManRec
                {
                    Skill = s.Skill, Team = s.Team, Kills = s.Kills, Rank = i * 4 / men.Count,
                    DeadAtRout = deadAtRout.TryGetValue(s, out var dr) ? dr : s.Dead, DeadAtEnd = s.Dead,
                });
            }
        }
        return r;
    }

    /// <summary>Team 0 holds at the origin facing north; team 1 stands <paramref name="gap"/> m north and attacks it.</summary>
    static (Battle, Battalion, Battalion) HoldVsAttack(int seed, UnitType hold, UnitType atk, float gap = 32f)
    {
        var b = new Battle(seed) { RecordEvents = false };
        var a = b.AddBattalion(0, hold, "Holder", new Vector2(0f, 0f), H.North);
        var c = b.AddBattalion(1, atk, "Attacker", new Vector2(0f, gap), H.South);
        c.OrderAttack(a, false);
        return (b, a, c);
    }

    static void PrintBattleHeader() =>
        Console.WriteLine("  matchup (holder v attacker)          | hold win | contact s | melee med | p10-p90   | dead H/A at rout | dead H/A end | loser alive | loser dead routing | annihil | rallied");

    static void PrintBattleRow(string label, BattleRun[] runs)
    {
        int n = runs.Length;
        var done = runs.Where(r => r.Winner >= 0).ToArray();
        float holdWin = runs.Count(r => r.Winner == 0) / (float)n;
        var melee = done.Select(r => r.LoserRout - r.Contact).ToList();
        string deadRout = $"{H.F(H.Mean(done.Select(r => (float)r.DeadAtRout[0])), 4)}/{H.F(H.Mean(done.Select(r => (float)r.DeadAtRout[1])), 4)}";
        string deadEnd = $"{H.F(H.Mean(runs.Select(r => (float)r.DeadEnd[0])), 4)}/{H.F(H.Mean(runs.Select(r => (float)r.DeadEnd[1])), 4)}";
        float loserAlive = H.Mean(done.Select(r => 1f - r.DeadEnd[1 - r.Winner] / (float)r.Initial[1 - r.Winner]));
        float routShare = done.Sum(r => r.LoserDeathsRouting) / (float)Math.Max(1, done.Sum(r => r.LoserDeaths));
        float annih = done.Count(r => r.Annihilated) / (float)Math.Max(1, done.Length);
        float rallied = runs.Count(r => r.Rallied) / (float)n;
        string unresolved = done.Length < n ? $"  ({n - done.Length} unresolved)" : "";
        Console.WriteLine($"  {label,-36} | {H.P(holdWin, 8)} | {H.F(H.Median(runs.Select(r => r.Contact)), 9)} | {H.F(H.Median(melee), 9)} | {H.F(H.Pct(melee, 0.1f), 4, 0)}-{H.F(H.Pct(melee, 0.9f), 4, 0)} | {deadRout,16} | {deadEnd,12} | {H.P(loserAlive, 11)} | {H.P(routShare, 18)} | {H.P(annih, 7)} | {H.P(rallied, 7)}{unresolved}");
    }

    public static void Lines()
    {
        int n = N(60);
        Console.WriteLine($"\n== c. 20v20 Legionaries: one side holds, the other attacks; the winner pursues ({n} seeds) ==");
        PrintBattleHeader();
        var ev = new EventCounts();
        var runs = Par(n, i =>
        {
            var (b, a, c) = HoldVsAttack(1000 + i, UnitTypes.Legionaries, UnitTypes.Legionaries);
            b.RecordEvents = true;
            var e = new EventCounts();
            var r = Run(b, i, $"lines seed {i}", 400f, true, null, e);
            ev.Add(e);
            return r;
        });
        PrintBattleRow("Legionaries v Legionaries", runs);
        Console.WriteLine("  " + ev.Summary());
        SkillReport(runs);
    }

    /// <summary>Does a good man last longer inside his unit? Survival and kills by skill quartile within each battalion.</summary>
    static void SkillReport(BattleRun[] runs)
    {
        var men = runs.SelectMany(r => r.Men.Select(m => (r, m))).ToList();
        Console.WriteLine("  by skill quartile within unit | mean skill | alive at rout | alive at end | kills/man   (all men, both sides)");
        for (int q = 0; q < 4; q++)
        {
            var g = men.Where(x => x.m.Rank == q).ToList();
            Console.WriteLine($"    Q{q + 1} {(q == 0 ? "(best) " : q == 3 ? "(worst)" : "       ")}                | {H.F(g.Average(x => x.m.Skill), 10, 2)} | {H.P(g.Count(x => !x.m.DeadAtRout) / (float)g.Count, 13)} | {H.P(g.Count(x => !x.m.DeadAtEnd) / (float)g.Count, 12)} | {H.F((float)g.Average(x => x.m.Kills), 8, 2)}");
        }
        foreach (var (label, won) in new[] { ("winners", true), ("losers", false) })
        {
            var g = men.Where(x => x.r.Winner >= 0 && (x.m.Team == x.r.Winner) == won).ToList();
            if (g.Count == 0) continue;
            float top = g.Where(x => x.m.Rank == 0).Count(x => !x.m.DeadAtEnd) / (float)Math.Max(1, g.Count(x => x.m.Rank == 0));
            float bot = g.Where(x => x.m.Rank == 3).Count(x => !x.m.DeadAtEnd) / (float)Math.Max(1, g.Count(x => x.m.Rank == 3));
            Console.WriteLine($"    {label}: Q1 alive at end {H.P(top)} vs Q4 {H.P(bot)}");
        }
    }

    // ---------------------------------------------------------------- d. flanking

    public enum Reinforce { None, Front, Flank, Rear }

    static BattleRun FlankRun(int seed, Reinforce mode, float delay)
    {
        var (b, a, bb) = HoldVsAttack(2000 + seed, UnitTypes.Legionaries, UnitTypes.Legionaries);
        Battalion c = null;
        int cFirstId = -1;
        var r0 = default(BattleRun);
        Action<Battle, BattleRun> hook = (bt, r) =>
        {
            r0 = r;
            if (mode != Reinforce.None && c == null && r.Contact >= 0f && bt.Time >= r.Contact + delay - 4f)
            {
                // Spawn C about 12 m out so its men make contact near contact + delay.
                Vector2 pos;
                float facing;
                var bc = bb.Centroid;
                var bf = MathUtil.Dir(bb.Facing);   // B faces south, towards A
                switch (mode)
                {
                    case Reinforce.Rear: pos = bc - bf * 13f; facing = bb.Facing; break;
                    case Reinforce.Flank: pos = bc + MathUtil.Right(bf) * 14f; facing = MathUtil.Angle(-MathUtil.Right(bf)); break;
                    default: pos = a.Centroid - MathUtil.Dir(a.Facing) * 11f; facing = a.Facing; break;   // behind A's line
                }
                cFirstId = bt.NextSoldierId;
                c = bt.AddBattalion(0, UnitTypes.Legionaries, "C", pos, facing);
                c.OrderAttack(bb, true);
            }
            if (c != null && r.CContact < 0f && c.FightingCount > 0) r.CContact = bt.Time;
            if (bb.Routing && r.BRout < 0f)
            {
                r.BRout = bt.Time;
                r.ADeadAtBRout = H.DeadIn(a);
                r.BDeadAtBRout = H.DeadIn(bb);
                r.CDeadAtBRout = c != null ? H.DeadIn(c) : 0;
            }
        };
        var res = Run(b, seed, $"flank {mode} seed {seed}", 400f, true, hook);
        res.CDead = c != null ? H.DeadIn(c) : 0;
        return res;
    }

    public static void Flanking()
    {
        int n = N(60);
        const float delay = 20f;
        Console.WriteLine($"\n== d. flanking: A (20 Leg) holds v B (20 Leg) attacking; C (20 Leg) joins ~{delay:F0}s after contact ({n} seeds) ==");
        Console.WriteLine("  C arrives    | B routs | C contact (s after A-B contact) | C contact->B rout med | p10-p90 | dead A / C / B at B rout | dead A+C / B at end");
        foreach (Reinforce mode in Enum.GetValues<Reinforce>())
        {
            var runs = Par(n, i => FlankRun(i, mode, delay));
            float bRouts = runs.Count(r => r.BRout >= 0f) / (float)n;
            var cOff = runs.Where(r => r.CContact >= 0f).Select(r => r.CContact - r.Contact).ToList();
            // With no C, measure from the moment C would have arrived.
            var toRout = runs.Where(r => r.BRout >= 0f).Select(r => mode == Reinforce.None ? r.BRout - (r.Contact + delay)
                                                                     : r.CContact >= 0f ? r.BRout - r.CContact : float.NaN).ToList();
            var withRout = runs.Where(r => r.BRout >= 0f).ToArray();
            string atRout = withRout.Length == 0 ? "-" :
                $"{H.F(H.Mean(withRout.Select(r => (float)r.ADeadAtBRout)), 4)} /{H.F(H.Mean(withRout.Select(r => (float)r.CDeadAtBRout)), 4)} /{H.F(H.Mean(withRout.Select(r => (float)r.BDeadAtBRout)), 4)}";
            string atEnd = $"{H.F(H.Mean(runs.Select(r => (float)r.DeadEnd[0])), 4)} /{H.F(H.Mean(runs.Select(r => (float)r.DeadEnd[1])), 4)}";
            Console.WriteLine($"  {mode,-12} | {H.P(bRouts, 7)} | {H.F(H.Median(cOff), 31)} | {H.F(H.Median(toRout), 21)} | {H.F(H.Pct(toRout, 0.1f), 3, 0)}-{H.F(H.Pct(toRout, 0.9f), 3, 0)} | {atRout,24} | {atEnd}");
        }
    }

    // ---------------------------------------------------------------- e. matchups

    public static void Matchups()
    {
        MatchupsHoldAttack();
        MatchupsMutual();
    }

    public static void MatchupsMutual()
    {
        int n = N(40);
        Console.WriteLine($"\n== e2. unit matchups, both sides ordered to attack each other ({n} seeds each; 'hold win' = first unit wins) ==");
        PrintBattleHeader();
        var list = new (UnitType, UnitType)[]
        {
            (UnitTypes.Legionaries, UnitTypes.Warband), (UnitTypes.Veterans, UnitTypes.ChosenSwordsmen), (UnitTypes.AuxSpearmen, UnitTypes.Warband),
            (UnitTypes.Legionaries, UnitTypes.SpearLevy), (UnitTypes.Legionaries, UnitTypes.Axemen), (UnitTypes.Legionaries, UnitTypes.AuxSpearmen),
            (UnitTypes.AuxSpearmen, UnitTypes.Axemen), (UnitTypes.Legionaries, UnitTypes.ChosenSwordsmen), (UnitTypes.Veterans, UnitTypes.Warband),
        };
        foreach (var (x, y) in list)
        {
            var runs = Par(n, i =>
            {
                var (b, a, c) = HoldVsAttack(4000 + i, x, y);
                a.OrderAttack(c, false);
                return Run(b, i, $"{x.Name} v {y.Name} (mutual) seed {i}", 400f, true);
            });
            PrintBattleRow($"{Short(x)} v {Short(y)}", runs);
        }
    }

    public static void MatchupsHoldAttack()
    {
        int n = N(40);
        Console.WriteLine($"\n== e. unit matchups, 20 v 20, the first holds and the second attacks ({n} seeds each) ==");
        PrintBattleHeader();
        var list = new (UnitType hold, UnitType atk)[]
        {
            (UnitTypes.Legionaries, UnitTypes.Warband),
            (UnitTypes.Warband, UnitTypes.Legionaries),
            (UnitTypes.Veterans, UnitTypes.ChosenSwordsmen),
            (UnitTypes.ChosenSwordsmen, UnitTypes.Veterans),
            (UnitTypes.AuxSpearmen, UnitTypes.Warband),
            (UnitTypes.Warband, UnitTypes.AuxSpearmen),
            (UnitTypes.SpearLevy, UnitTypes.Legionaries),
            (UnitTypes.Legionaries, UnitTypes.SpearLevy),
            (UnitTypes.Legionaries, UnitTypes.Axemen),
            (UnitTypes.Axemen, UnitTypes.Legionaries),
            (UnitTypes.AuxSpearmen, UnitTypes.Legionaries),
            (UnitTypes.Legionaries, UnitTypes.AuxSpearmen),
        };
        foreach (var (hold, atk) in list)
        {
            var runs = Par(n, i =>
            {
                var (b, _, _) = HoldVsAttack(3000 + i, hold, atk);
                return Run(b, i, $"{hold.Name} v {atk.Name} seed {i}", 400f, true);
            });
            PrintBattleRow($"{Short(hold)} v {Short(atk)}", runs);
        }
    }

    static string Short(UnitType t) => t.Name.Replace("Gallic ", "").Replace("Veteran Legionaries", "Veterans").Replace("Auxiliary ", "Aux ");

    // ---------------------------------------------------------------- f. the default battle

    public static void Default()
    {
        int n = Seeds > 0 ? Math.Max(Seeds, 30) : 60;
        Console.WriteLine($"\n== f. Scenarios.Default(seed, aiForPlayer: true), AI v AI, {n} seeds, 400 s cap ==");
        var runs = Par(n, i =>
        {
            var b = Scenarios.Default(i + 1, aiForPlayer: true);
            b.RecordEvents = false;
            var san = new Sanity($"default seed {i + 1}");
            while (b.Winner < 0 && b.Time < 400f)
            {
                b.Step(H.Dt);
                san.Check(b, H.Dt);
            }
            san.Commit();
            var perUnit = b.Battalions.Select(u => (u.Name, u.Team, dead: H.DeadIn(u), u.Routing, u.Routs)).ToArray();
            return (winner: b.Winner, end: b.Winner >= 0 ? b.EndTime : float.NaN, dead0: b.Dead(0), dead1: b.Dead(1),
                    esc0: b.Soldiers.Count(s => s.Team == 0 && s.Escaped), esc1: b.Soldiers.Count(s => s.Team == 1 && s.Escaped), perUnit);
        });
        int rome = runs.Count(r => r.winner == 0), gauls = runs.Count(r => r.winner == 1), none = runs.Count(r => r.winner < 0);
        var ends = runs.Where(r => r.winner >= 0).Select(r => r.end).ToList();
        Console.WriteLine($"  Rome {rome}  Gauls {gauls}  unfinished {none}   ({H.P(rome / (float)n)} Rome)");
        Console.WriteLine($"  duration median {H.F(H.Median(ends), 0)} s, p10 {H.F(H.Pct(ends, 0.1f), 0)}, p90 {H.F(H.Pct(ends, 0.9f), 0)}, max {H.F(ends.Count > 0 ? ends.Max() : float.NaN, 0)}");
        Console.WriteLine($"  dead at decision: Rome {H.F(H.Mean(runs.Select(r => (float)r.dead0)), 0)}/80  Gauls {H.F(H.Mean(runs.Select(r => (float)r.dead1)), 0)}/100   escaped: Rome {H.F(H.Mean(runs.Select(r => (float)r.esc0)), 0)}  Gauls {H.F(H.Mean(runs.Select(r => (float)r.esc1)), 0)}");
        Console.WriteLine("  per battalion: mean dead | routed at decision");
        var units = runs[0].perUnit;
        for (int k = 0; k < units.Length; k++)
        {
            float dead = (float)runs.Average(r => r.perUnit[k].dead);
            float routed = runs.Count(r => r.perUnit[k].Routing || r.perUnit[k].Routs > 0) / (float)n;
            Console.WriteLine($"    {units[k].Name,-24} {H.F(dead, 5)} | {H.P(routed)}");
        }
        if (none > 0) Sanity.Failures.Enqueue($"[default] {none} of {n} battles did not finish within 400 s");
    }

    // ---------------------------------------------------------------- h. reserves

    /// <summary>One relief: from the reserve starting to close up to the two having changed places (or giving up).</summary>
    sealed class Swap
    {
        public Battalion Front, Reserve;
        public float Start, End = -1f;
        public int DeadA0, DeadB0, DeadA, DeadB;
        public bool Done, FrontRouted;
        public string Trigger;
    }

    sealed class ReliefRun
    {
        public int Winner = -1;
        public float Contact = -1f, End;
        public readonly List<Swap> Swaps = new();
        public float[] StamFront = new float[StamAt.Length], StamReserve = new float[StamAt.Length];
        public int DeadA, DeadB;
    }

    static readonly float[] StamAt = { 0f, 60f, 120f, 180f, 240f };

    /// <summary>What made the front spent, by the rule in <see cref="Battle.FrontSpent"/> ("manual" if none of it).</summary>
    static string SpentBy(Battalion f) =>
        f.Worn >= Battle.ReliefWorn ? "worn" : f.Alive < f.InitialCount * Battle.ReliefStrength ? "strength"
        : f.FleeingCount >= Battle.ReliefFleeing ? "running" : f.Morale < f.BaseMorale() - Battle.ReliefShaken ? "morale" : "manual";

    /// <summary>
    /// Two 20-man Legionary battalions a side, one 14 m behind the other. Team 0 (A): A2 is A1's reserve, or (reserve
    /// false) a plain second line. Team 1 (B): B2 is always a plain second line. A plain second line goes in when a
    /// friend in front of it breaks, as a player would send it. <paramref name="aHolds"/>: B1 attacks A1; else A1
    /// attacks B1. So with reserve false the two sides are the same, and the difference the reserve makes is the
    /// relief.
    /// </summary>
    static ReliefRun ReliefBattle(int seed, bool reserve, bool aHolds)
    {
        var b = new Battle(8000 + seed) { RecordEvents = false };
        var a1 = b.AddBattalion(0, UnitTypes.Legionaries, "A1", new Vector2(0f, 0f), H.North);
        var a2 = b.AddBattalion(0, UnitTypes.Legionaries, "A2", new Vector2(0f, -14f), H.North);
        var b1 = b.AddBattalion(1, UnitTypes.Legionaries, "B1", new Vector2(0f, 32f), H.South);
        var b2 = b.AddBattalion(1, UnitTypes.Legionaries, "B2", new Vector2(0f, 46f), H.South);
        if (reserve) b.AssignReserve(a2, a1);
        if (aHolds) b1.OrderAttack(a1, false);
        else a1.OrderAttack(b1, false);

        var r = new ReliefRun();
        var san = new Sanity($"relief seed {seed}");
        var phase = new Dictionary<Battalion, ReliefPhase> { [a1] = ReliefPhase.None, [a2] = ReliefPhase.None };
        var open = new Dictionary<Battalion, Swap>();
        int si = 0;
        while (b.Time < 600f && b.Winner < 0)
        {
            b.Step(H.Dt);
            san.Check(b, H.Dt);
            if (r.Contact < 0f && (a1.FightingCount > 0 || b1.FightingCount > 0)) r.Contact = b.Time;
            foreach (var u in new[] { a1, a2 })
            {
                if (phase[u] == ReliefPhase.None && u.Relief != ReliefPhase.None)
                {
                    var sw = new Swap { Front = u.ReserveFor, Reserve = u, Start = b.Time, DeadA0 = b.Dead(0), DeadB0 = b.Dead(1), Trigger = SpentBy(u.ReserveFor) };
                    r.Swaps.Add(sw);
                    open[u] = sw;
                }
                else if (phase[u] != ReliefPhase.None && u.Relief == ReliefPhase.None && open.Remove(u, out var sw))
                {
                    sw.End = b.Time;
                    sw.Done = sw.Front.ReserveFor == u;
                    sw.DeadA = b.Dead(0) - sw.DeadA0;
                    sw.DeadB = b.Dead(1) - sw.DeadB0;
                }
                phase[u] = u.Relief;
            }
            foreach (var sw in r.Swaps)
                if (sw.Front.Routing && b.Time <= (sw.End < 0f ? b.Time : sw.End + 20f)) sw.FrontRouted = true;
            // A plain second line goes in when a friend breaks.
            foreach (var u in b.Battalions)
            {
                if (u.Routing || u.Destroyed || u.ReserveFor != null || u.Order != OrderType.Hold || u.InMelee) continue;
                if (!b.Battalions.Exists(o => o.Team == u.Team && o.Routing)) continue;
                var enemy = b.NearestEnemyUnit(u);
                if (enemy != null) u.OrderAttack(enemy, false);
            }
            if (r.Contact >= 0f)
                while (si < StamAt.Length && b.Time - r.Contact >= StamAt[si])
                {
                    var front = a1.ReserveFor == null ? a1 : a2;
                    var back = front == a1 ? a2 : a1;
                    r.StamFront[si] = front.Destroyed || !front.InMelee ? float.NaN : front.FightingStamina;
                    r.StamReserve[si] = back.Destroyed || back.InMelee ? float.NaN : back.AvgStamina;
                    si++;
                }
        }
        for (; si < StamAt.Length; si++) r.StamFront[si] = r.StamReserve[si] = float.NaN;
        san.Commit();
        r.Winner = b.Winner;
        r.End = b.Winner >= 0 ? b.EndTime : b.Time;
        r.DeadA = b.Dead(0);
        r.DeadB = b.Dead(1);
        return r;
    }

    /// <summary>
    /// AI against AI, each side two 20-man Legionary battalions side by side with two more 14 m behind them. Team 0's
    /// general makes its second line reserves of the first if <paramref name="aReserves"/>, team 1's if
    /// <paramref name="bReserves"/>. A general whose front has a second enemy battalion on it sends its reserve in.
    /// </summary>
    static ReliefRun AiLines(int seed, bool aReserves, bool bReserves = false)
    {
        var b = new Battle(9000 + seed) { RecordEvents = false };
        foreach (var (team, y, f, sign) in new[] { (0, 0f, H.North, -1f), (1, 36f, H.South, 1f) })
            foreach (var x in new[] { -6f, 6f })
            {
                b.AddBattalion(team, UnitTypes.Legionaries, $"{team}F{x}", new Vector2(x, y), f);
                b.AddBattalion(team, UnitTypes.Legionaries, $"{team}R{x}", new Vector2(x, y + sign * 14f), f);
            }
        b.AIs.Add(new BattleAI(0) { UseReserves = aReserves });
        b.AIs.Add(new BattleAI(1) { UseReserves = bReserves });
        var r = new ReliefRun();
        var san = new Sanity($"ai lines seed {seed}");
        var was = new Dictionary<Battalion, ReliefPhase>();
        while (b.Time < 600f && b.Winner < 0)
        {
            b.Step(H.Dt);
            san.Check(b, H.Dt);
            if (r.Contact < 0f && b.Battalions.Exists(u => u.FightingCount > 0)) r.Contact = b.Time;
            foreach (var u in b.Battalions)
            {
                if (u.Relief != ReliefPhase.None && was.GetValueOrDefault(u) == ReliefPhase.None) r.Swaps.Add(new Swap { Start = b.Time });
                was[u] = u.Relief;
            }
        }
        san.Commit();
        r.Winner = b.Winner;
        r.End = b.Winner >= 0 ? b.EndTime : b.Time;
        r.DeadA = b.Dead(0);
        r.DeadB = b.Dead(1);
        return r;
    }

    /// <summary>
    /// A custom battle as the setup screen builds it, AI both sides: eight battalions a side, so each forms a front
    /// line of six with two reserves behind it (Scenarios.Custom). Counts reliefs by what made the front spent.
    /// </summary>
    static (int winner, float end, List<string> triggers) CustomLines(int seed)
    {
        var setup = new BattleSetup { Seed = 100 + seed, Autopilot = true };
        setup.Rome.Add((UnitTypes.AuxSpearmen, 4));
        setup.Rome.Add((UnitTypes.Legionaries, 2));
        setup.Rome.Add((UnitTypes.Veterans, 2));
        setup.Gauls.Add((UnitTypes.Warband, 4));
        setup.Gauls.Add((UnitTypes.Axemen, 2));
        setup.Gauls.Add((UnitTypes.ChosenSwordsmen, 2));
        var b = Scenarios.Custom(setup);
        b.RecordEvents = false;
        var san = new Sanity($"custom 8v8 seed {seed}");
        var was = new Dictionary<Battalion, ReliefPhase>();
        var triggers = new List<string>();
        while (b.Time < 600f && b.Winner < 0)
        {
            b.Step(H.Dt);
            san.Check(b, H.Dt);
            foreach (var u in b.Battalions)
            {
                if (u.Relief != ReliefPhase.None && was.GetValueOrDefault(u) == ReliefPhase.None) triggers.Add(SpentBy(u.ReserveFor));
                was[u] = u.Relief;
            }
        }
        san.Commit();
        return (b.Winner, b.Winner >= 0 ? b.EndTime : b.Time, triggers);
    }

    public static void Relief()
    {
        int n = N(100);
        Console.WriteLine($"\n== h. reserves: 2 x 20 Legionaries a side, the second 14 m behind the first; A's second is its reserve or a plain second line, B's always a plain one ({n} seeds each) ==");
        Console.WriteLine($"   the front is spent when {Battle.ReliefWorn:P0} of its front rank are due relief with nobody fit behind them (worn), it has < {Battle.ReliefStrength:P0} of its men, {Battle.ReliefFleeing}+ are running or morale is {Battle.ReliefShaken:F0} below its fresh level;" +
                          $" the reserve needs stamina >= {Battle.ReserveStamina:F2}, morale >= {Battle.ReserveMorale:F0} and {Battle.ReserveLead:F0} more than the front's");
        Console.WriteLine("  variant                         | A wins | melee med | dead A/B  | swaps/battle | battles w/ one | 1st swap after contact | swap takes s med (p10-p90) | done | deaths A/B per swap | front broke <=20 s after | spent by worn/str/run/morale");
        foreach (var (label, reserve, aHolds) in new[]
        {
            ("A holds, A2 in reserve", true, true),
            ("A holds, A2 a plain 2nd line", false, true),
            ("A attacks, A2 in reserve", true, false),
            ("A attacks, A2 a plain 2nd line", false, false),
        })
        {
            var runs = Par(n, i => ReliefBattle(i, reserve, aHolds));
            var melee = runs.Where(x => x.Winner >= 0 && x.Contact >= 0f).Select(x => x.End - x.Contact).ToList();
            string head = $"  {label,-31} | {H.P(runs.Count(x => x.Winner == 0) / (float)n, 6)} | {H.F(H.Median(melee), 9)} | {runs.Average(x => (float)x.DeadA),4:F1}/{runs.Average(x => (float)x.DeadB),4:F1}";
            if (!reserve) { Console.WriteLine(head); continue; }
            var swaps = runs.SelectMany(x => x.Swaps).ToList();
            var done = swaps.Where(x => x.Done).ToList();
            var first = runs.Where(x => x.Swaps.Count > 0).Select(x => x.Swaps[0].Start - x.Contact).ToList();
            var took = done.Select(x => x.End - x.Start).ToList();
            string spent = swaps.Count == 0 ? "-" : string.Join("/", new[] { "worn", "strength", "running", "morale" }.Select(t => swaps.Count(x => x.Trigger == t)));
            Console.WriteLine(head + $" | {swaps.Count / (float)n,12:F2} | {H.P(first.Count / (float)n, 14)} | {H.F(H.Median(first), 22)} | {H.F(H.Median(took), 12)} ({H.F(H.Pct(took, 0.1f), 4)}-{H.F(H.Pct(took, 0.9f), 4)})   | {H.P(done.Count / (float)Math.Max(1, swaps.Count), 4)} | {H.F(H.Mean(done.Select(x => (float)x.DeadA)), 9, 2)}/{H.F(H.Mean(done.Select(x => (float)x.DeadB)), 4, 2)}      | {H.P(swaps.Count(x => x.FrontRouted) / (float)Math.Max(1, swaps.Count), 24)} | {spent}");
            string Row(Func<ReliefRun, float[]> sel) => string.Join(" ", Enumerable.Range(0, StamAt.Length).Select(k => H.F(H.Mean(runs.Select(x => sel(x)[k])), 5, 2)));
            Console.WriteLine($"      mean stamina at contact + {string.Join(" ", StamAt.Select(t => $"{t,4:F0}s"))}:  the front's fighting men {Row(x => x.StamFront)}  |  the reserve {Row(x => x.StamReserve)}");
        }
        foreach (var (label, ra, rb) in new[] { ("AI v AI, 2 lines a side, A's second line in reserve", true, false), ("AI v AI, 2 lines a side, neither", false, false),
                                                 ("AI v AI, 2 lines a side, both in reserve", true, true) })
        {
            var runs = Par(n, i => AiLines(i, ra, rb));
            var melee = runs.Where(x => x.Winner >= 0 && x.Contact >= 0f).Select(x => x.End - x.Contact).ToList();
            Console.WriteLine($"  {label,-52} | A wins {H.P(runs.Count(x => x.Winner == 0) / (float)n, 5)} | melee med {H.F(H.Median(melee), 5)} | dead A/B {runs.Average(x => (float)x.DeadA),4:F1}/{runs.Average(x => (float)x.DeadB),4:F1} | reliefs/battle {runs.Average(x => (float)x.Swaps.Count):F2} | unfinished {runs.Count(x => x.Winner < 0)}");
        }
        {
            int m = Math.Max(20, n / 2);
            var runs = Par(m, CustomLines);
            var all = runs.SelectMany(x => x.triggers).ToList();
            string spent = string.Join("/", new[] { "worn", "strength", "running", "morale" }.Select(t => all.Count(x => x == t)));
            Console.WriteLine($"  {"Custom 8 v 8 (6 in front, 2 in reserve), AI both sides",-52} | Rome {H.P(runs.Count(x => x.winner == 0) / (float)m, 5)} | length med {H.F(H.Median(runs.Select(x => x.end)), 5)} | reliefs/battle {all.Count / (float)m:F2} (spent by worn/str/run/morale {spent}) | unfinished {runs.Count(x => x.winner < 0)}");
        }
    }

    // ---------------------------------------------------------------- g. performance

    static Battle Triple(int seed)
    {
        var b = new Battle(seed);
        const float romeY = -32f, gaulY = 32f;
        foreach (float ox in new[] { -95f, 0f, 95f })
        {
            b.AddBattalion(0, UnitTypes.AuxSpearmen, "I Auxilia", new Vector2(ox + 24f, romeY), H.North);
            b.AddBattalion(0, UnitTypes.Legionaries, "II Cohort", new Vector2(ox + 8f, romeY), H.North);
            b.AddBattalion(0, UnitTypes.Veterans, "I Cohort (Veterans)", new Vector2(ox - 8f, romeY), H.North);
            b.AddBattalion(0, UnitTypes.AuxSpearmen, "II Auxilia", new Vector2(ox - 24f, romeY), H.North);
            b.AddBattalion(1, UnitTypes.SpearLevy, "Levy", new Vector2(ox + 32f, gaulY), H.South);
            b.AddBattalion(1, UnitTypes.Warband, "Warband", new Vector2(ox + 16f, gaulY), H.South);
            b.AddBattalion(1, UnitTypes.ChosenSwordsmen, "Chosen", new Vector2(ox, gaulY), H.South);
            b.AddBattalion(1, UnitTypes.Axemen, "Axemen", new Vector2(ox - 16f, gaulY), H.South);
            b.AddBattalion(1, UnitTypes.Warband, "Warband", new Vector2(ox - 32f, gaulY), H.South);
        }
        b.AIs.Add(new BattleAI(1));
        b.AIs.Add(new BattleAI(0) { AdvanceAfter = 20f });
        return b;
    }

    public static void Perf()
    {
        Console.WriteLine("\n== g. performance (single thread, Release, RecordEvents off) ==");
        foreach (var (label, make) in new (string, Func<int, Battle>)[] { ("default (180 men)", s => Scenarios.Default(s, true)), ("3x default (540 men)", Triple) })
        {
            // Warm up the JIT.
            var w = make(99);
            for (int i = 0; i < 600; i++) w.Step(H.Dt);
            long steps = 0;
            double worst = 0, meleeTicks = 0;
            long meleeSteps = 0;
            var total = Stopwatch.StartNew();
            var one = new Stopwatch();
            for (int seed = 1; seed <= 3; seed++)
            {
                var b = make(seed);
                b.RecordEvents = false;
                while (b.Winner < 0 && b.Time < 400f)
                {
                    one.Restart();
                    b.Step(H.Dt);
                    one.Stop();
                    steps++;
                    double ms = one.Elapsed.TotalMilliseconds;
                    if (ms > worst) worst = ms;
                    bool melee = b.Battalions.Any(u => u.InMelee);
                    if (melee) { meleeTicks += ms; meleeSteps++; }
                }
            }
            double sps = steps / total.Elapsed.TotalSeconds;
            double meleeSps = meleeSteps > 0 ? 1000.0 * meleeSteps / meleeTicks : double.NaN;
            Console.WriteLine($"  {label,-22}: {sps,8:F0} steps/s overall, {meleeSps,8:F0} steps/s in melee, worst step {worst:F2} ms  -> {sps / 240.0:F0}x the 240 steps/s that 4x speed at 60 Hz needs");
        }
    }
}
