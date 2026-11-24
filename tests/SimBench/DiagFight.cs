using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitFighting.Sim;

namespace SimBench;

/// <summary>
/// What a fight looks like from inside, for the men in the front rank: how long a man in contact stands with nothing
/// happening, what he does instead (blows by kind, probes, feints and how often they are bitten, shield bashes,
/// footwork), how his stamina runs down, and how often the rank behind relieves him. "diag-fight" (not part of "all").
/// </summary>
public static class FightDiag
{
    sealed class Tally
    {
        // Gaps between his actions while he stays in contact: his own (windups of any kind, footwork steps), the
        // exchange (his windups, his blocks and parries, being shoved), and both together.
        public readonly List<float> GapOwn = new(), GapExchange = new(), GapAll = new();
        public double ContactSec, FrontMeleeSec;
        public long Real, Probe, Feint, Bash, Defends, Steps, Shoved, Kills, Swaps, Feints, Bites, RealRiposte, RealFollow, RealOpening;
        public readonly Dictionary<string, long> KillBy = new(), HitBy = new(), BlowBy = new(), Victims = new();
        public readonly long[] HitsByMates = new long[3], BlowsByMates = new long[3];
        public readonly long[] Kinds = new long[Enum.GetValues<BlowKind>().Length];
        public readonly EventCounts Ev = new();
        public readonly List<float> Melee = new();
        // Mean stamina of the front rank (and of the rest) every 30 s from first contact, while the fight is undecided.
        public readonly double[] FrontSt = new double[Slots], RearSt = new double[Slots];
        public readonly int[] StN = new int[Slots];

        public const int Slots = 11;

        public void Add(Tally o)
        {
            lock (this)
            {
                GapOwn.AddRange(o.GapOwn); GapExchange.AddRange(o.GapExchange); GapAll.AddRange(o.GapAll);
                ContactSec += o.ContactSec; FrontMeleeSec += o.FrontMeleeSec;
                Real += o.Real; Probe += o.Probe; Feint += o.Feint; Bash += o.Bash; Defends += o.Defends; Steps += o.Steps;
                Shoved += o.Shoved; Kills += o.Kills; Swaps += o.Swaps; Feints += o.Feints; Bites += o.Bites;
                RealRiposte += o.RealRiposte; RealFollow += o.RealFollow; RealOpening += o.RealOpening;
                foreach (var kv in o.KillBy) KillBy[kv.Key] = KillBy.GetValueOrDefault(kv.Key) + kv.Value;
                foreach (var kv in o.Victims) Victims[kv.Key] = Victims.GetValueOrDefault(kv.Key) + kv.Value;
                foreach (var kv in o.HitBy) HitBy[kv.Key] = HitBy.GetValueOrDefault(kv.Key) + kv.Value;
                foreach (var kv in o.BlowBy) BlowBy[kv.Key] = BlowBy.GetValueOrDefault(kv.Key) + kv.Value;
                for (int i = 0; i < 3; i++) { HitsByMates[i] += o.HitsByMates[i]; BlowsByMates[i] += o.BlowsByMates[i]; }
                for (int i = 0; i < Kinds.Length; i++) Kinds[i] += o.Kinds[i];
                Ev.Add(o.Ev);
                Melee.AddRange(o.Melee);
                for (int i = 0; i < Slots; i++) { FrontSt[i] += o.FrontSt[i]; RearSt[i] += o.RearSt[i]; StN[i] += o.StN[i]; }
            }
        }
    }

    sealed class Track
    {
        public bool Contact;
        public float LastOwn, LastEx, LastAll, PhaseTime, Step = -99f, Shoved = -99f, Bit = -99f;
        public CombatPhase Phase;
        public int Rank = -1, Slot = -1, Kills;
        public float Hp = 1f;
        public bool Riposte, FollowUp;
        public float ReadyDelay;
        public string Source = "?";
    }

    static bool EndOfLine(Battalion u, int slot)
    {
        if (slot < 0 || slot >= u.Slots.Count) return false;
        float x = u.Slots[slot].Local.X;
        bool left = false, right = false;
        foreach (var sl in u.Slots)
            if (sl.Rank == 0) { left |= sl.Local.X < x - 0.1f; right |= sl.Local.X > x + 0.1f; }
        return !(left && right);
    }

    static int RankOf(Soldier s) =>
        s.Active && !s.Fleeing && s.Slot >= 0 && s.Slot < s.Unit.Slots.Count ? s.Unit.Slots[s.Slot].Rank : -1;

    /// <summary>Steps the battle to its decision (or the cap), watching every man; returns what it saw.</summary>
    static Tally Watch(Battle b, float cap, float stopAfterDecision)
    {
        var t = new Tally();
        var tr = new Dictionary<Soldier, Track>();
        foreach (var s in b.Soldiers) tr[s] = new Track { Phase = s.Phase, PhaseTime = s.PhaseTime, Rank = RankOf(s), Step = s.LastStepTime, Shoved = s.LastShovedTime, Bit = s.LastFeintBitTime };
        var defended = new HashSet<Soldier>();
        float contact = -1f;
        var san = new Sanity("diag-fight");
        int stepsSince = -1;
        while (b.Time < cap && (b.Winner < 0 || b.Time < b.EndTime + stopAfterDecision))
        {
            bool undecided = b.Winner < 0;   // before the step: the blow that decides it still counts
            b.Step(H.Dt);
            san.Check(b, H.Dt);
            float now = b.Time;
            defended.Clear();
            foreach (var e in b.Events)
            {
                if (e.Kind is EventKind.Block or EventKind.Parry or EventKind.GuardBreak && e.Defender != null) defended.Add(e.Defender);
                if (e.Kind is EventKind.Block or EventKind.Parry or EventKind.GuardBreak or EventKind.Hit or EventKind.Glance or EventKind.Kill && e.Defender != null && e.Attacker != null
                    && Mathf.Abs(MathUtil.RelativeAngle(e.Defender.Facing, e.Attacker.Pos - e.Defender.Pos)) < 1f)
                {
                    int mates = Combat.WallMates(b, e.Defender);
                    t.BlowsByMates[mates]++;
                    if (e.Kind is EventKind.Hit or EventKind.Glance or EventKind.Kill) t.HitsByMates[mates]++;
                }
            }
            foreach (var e in b.Events)
            {
                if (e.Attacker == null || e.Defender == null || !tr.TryGetValue(e.Attacker, out var am)) continue;
                if (e.Kind is not (EventKind.Block or EventKind.Parry or EventKind.GuardBreak or EventKind.Hit or EventKind.Glance or EventKind.Kill)) continue;
                string src = e.Attacker.Probe ? "probe" : am.Source;
                t.BlowBy[src] = t.BlowBy.GetValueOrDefault(src) + 1;
                if (e.Kind is EventKind.Hit or EventKind.Kill) t.HitBy[src] = t.HitBy.GetValueOrDefault(src) + 1;
                if (e.Kind == EventKind.Kill) t.KillBy[src] = t.KillBy.GetValueOrDefault(src) + 1;
                // Who dies, as he stood a step before: in the front rank (at an end of it or not), in a rank behind,
                // or out of the line altogether; and whether he was already badly hurt.
                if (e.Kind == EventKind.Kill && undecided && tr.TryGetValue(e.Defender, out var dm))
                {
                    string where = dm.Rank < 0 ? "out of line" : dm.Rank > 0 ? "rear ranks" : EndOfLine(e.Defender.Unit, dm.Slot) ? "front, at an end" : "front";
                    t.Victims[where] = t.Victims.GetValueOrDefault(where) + 1;
                    if (dm.Hp < 0.3f) t.Victims["hurt"] = t.Victims.GetValueOrDefault("hurt") + 1;
                }
            }
            t.Ev.Drain(b);
            if (contact < 0f && b.Battalions.Any(u => u.FightingCount > 0)) contact = now;
            if (contact >= 0f) stepsSince++;

            foreach (var s in b.Soldiers)
            {
                var m = tr[s];
                int rank = RankOf(s);
                bool front = rank == 0;
                bool inContact = front && s.Target != null && s.Target.Active && s.Pos.DistanceTo(s.Target.Pos) <= s.Weapon.Reach + 0.3f;
                bool wind = s.Active && s.Phase == CombatPhase.Windup && (m.Phase != CombatPhase.Windup || s.PhaseTime < m.PhaseTime);
                bool stepped = s.LastStepTime != m.Step, shoved = s.LastShovedTime != m.Shoved, bit = s.LastFeintBitTime != m.Bit;
                bool def = defended.Contains(s);
                if (s.Active && s.Phase == CombatPhase.Stagger && m.Phase is not (CombatPhase.Stagger or CombatPhase.Down) && undecided)
                {
                    string why = s.LastShovedTime == now ? "stagger:bash" : s.LastHitTime == now ? "stagger:hit" : s.LastBlockTime == now ? "stagger:guard" : s.LastImpactTime == now ? "stagger:charge" : "stagger:?";
                    t.BlowBy[why] = t.BlowBy.GetValueOrDefault(why) + 1;
                }
                if (wind)
                {
                    m.Source = s.Feint ? "feint" : s.Blow == BlowKind.Bash ? "bash" : s.Probe ? "probe" : m.Riposte ? "riposte" : m.FollowUp ? "follow-up" : m.ReadyDelay > H.Dt * 1.5f ? "opening" : "chosen";
                    var st = s.StrikeTarget;
                    if (m.Source == "opening" && st != null)
                        m.Source = st.Phase is CombatPhase.Stagger or CombatPhase.Down ? "open:reeling"
                                 : Mathf.Abs(MathUtil.RelativeAngle(st.Facing, s.Pos - st.Pos)) > 1.4f ? "open:turned"
                                 : st.Phase == CombatPhase.Recover ? "open:recover"
                                 : now - st.LastFeintBitTime < 0.4f ? "open:bit" : now - st.LastShovedTime < 0.4f ? "open:shoved" : "open:other";
                }
                if (wind && s.Feint) t.Feints++;
                if (bit) t.Bites++;
                if (undecided && front && s.Unit.InMelee) t.FrontMeleeSec += H.Dt;
                if (undecided && m.Rank == 0 && rank >= 1 && !s.Unit.Routing) t.Swaps++;

                if ((inContact || m.Contact) && undecided) t.Kills += s.Kills - m.Kills;   // his man is dead, so no longer "in contact"
                if (inContact && undecided)
                {
                    t.ContactSec += H.Dt;
                    if (wind)
                    {
                        if (s.Feint) t.Feint++;
                        else if (s.Blow == BlowKind.Bash) t.Bash++;
                        else if (s.Probe) t.Probe++;
                        else
                        {
                            t.Real++;
                            if (m.Riposte) t.RealRiposte++;
                            else if (m.FollowUp) t.RealFollow++;
                            else if (m.ReadyDelay > H.Dt * 1.5f) t.RealOpening++;
                        }
                        if (!s.Feint && s.Blow != BlowKind.Bash) t.Kinds[(int)s.Blow]++;
                    }
                    if (def) t.Defends++;
                    if (stepped) t.Steps++;
                    if (shoved) t.Shoved++;
                    bool own = wind || stepped, ex = wind || def || shoved;
                    if (!m.Contact) m.LastOwn = m.LastEx = m.LastAll = now;
                    else
                    {
                        if (own) { t.GapOwn.Add(now - m.LastOwn); m.LastOwn = now; }
                        if (ex) { t.GapExchange.Add(now - m.LastEx); m.LastEx = now; }
                        if (own || ex) { t.GapAll.Add(now - m.LastAll); m.LastAll = now; }
                    }
                }
                m.Contact = inContact && undecided;
                m.Phase = s.Phase;
                m.PhaseTime = s.PhaseTime;
                m.Rank = rank;
                m.Slot = s.Slot;
                m.Hp = s.HpFrac;
                m.Step = s.LastStepTime;
                m.Shoved = s.LastShovedTime;
                m.Bit = s.LastFeintBitTime;
                m.Kills = s.Kills;
                m.Riposte = s.Riposte;
                m.FollowUp = s.FollowUp;
                m.ReadyDelay = s.ReadyDelay;
            }

            if (contact >= 0f && undecided && stepsSince % 1800 == 0)
            {
                int k = stepsSince / 1800;
                if (k < Tally.Slots)
                {
                    var fr = b.Soldiers.Where(s => RankOf(s) == 0).Select(s => s.StaminaFrac).ToList();
                    var rr = b.Soldiers.Where(s => RankOf(s) >= 1).Select(s => s.StaminaFrac).ToList();
                    if (fr.Count > 0)
                    {
                        t.FrontSt[k] += fr.Average();
                        t.RearSt[k] += rr.Count > 0 ? rr.Average() : 0f;
                        t.StN[k]++;
                    }
                }
            }
        }
        san.Commit();
        if (b.Winner >= 0 && contact >= 0f) t.Melee.Add(b.EndTime - contact);
        return t;
    }

    static void Report(string title, Tally t, bool staminaCurve)
    {
        float min = (float)(t.ContactSec / 60.0);
        Console.WriteLine($"\n-- {title}: {t.ContactSec / 60.0:F0} man-minutes of front-rank contact" + (t.Melee.Count > 0 ? $", melee median {H.Median(t.Melee):F0} s (p10-90 {H.Pct(t.Melee, 0.1f):F0}-{H.Pct(t.Melee, 0.9f):F0})" : ""));
        string G(List<float> g) => g.Count == 0 ? "-" : $"median {H.Median(g):F2} s  p90 {H.Pct(g, 0.9f):F2} s  over 1.5 s {H.P(g.Count(x => x > 1.5f) / (float)g.Count).Trim()}  over 3 s {H.P(g.Count(x => x > 3f) / (float)g.Count).Trim()}";
        Console.WriteLine($"   idle gaps, all actions   : {G(t.GapAll)}");
        Console.WriteLine($"   idle gaps, exchange      : {G(t.GapExchange)}   (his windups, his blocks/parries, being shoved)");
        Console.WriteLine($"   idle gaps, own initiative: {G(t.GapOwn)}   (his windups and footwork steps)");
        float pm(long x) => x / Math.Max(1e-3f, min);
        Console.WriteLine($"   per man-minute: windups {pm(t.Real + t.Probe + t.Feint + t.Bash):F1} (real blows {pm(t.Real):F1}, probes {pm(t.Probe):F1}, feints {pm(t.Feint):F1}, bashes {pm(t.Bash):F1}) | defends {pm(t.Defends):F1} | steps {pm(t.Steps):F1} | shoved {pm(t.Shoved):F1} | kills {pm(t.Kills):F2}");
        long kinds = t.Kinds.Sum();
        if (kinds > 0) Console.WriteLine("   blow mix (real + probes): " + string.Join("  ", Enum.GetValues<BlowKind>().Where(k => k != BlowKind.Bash).Select(k => $"{k} {H.P(t.Kinds[(int)k] / (float)kinds).Trim()}")));
        Console.WriteLine($"   feints {t.Feints}, bitten {t.Bites} ({H.P(t.Feints > 0 ? t.Bites / (float)t.Feints : float.NaN).Trim()}) | real blows: ripostes {H.P(t.RealRiposte / (float)Math.Max(1, t.Real)).Trim()}, follow-ups {H.P(t.RealFollow / (float)Math.Max(1, t.Real)).Trim()}, taking an opening {H.P(t.RealOpening / (float)Math.Max(1, t.Real)).Trim()}");
        Console.WriteLine("   frontal blows by the defender's wall mates (share of blows, share that got through): " + string.Join("  ", Enumerable.Range(0, 3).Select(k => $"{k}: {H.P(t.BlowsByMates[k] / (float)Math.Max(1, t.BlowsByMates.Sum())).Trim()} / {H.P(t.HitsByMates[k] / (float)Math.Max(1, t.BlowsByMates[k])).Trim()}")));
        Console.WriteLine("   " + t.Ev.Summary());
        Console.WriteLine("   by source (blows / wounding hits / kills): " + string.Join("  ", t.BlowBy.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}/{t.HitBy.GetValueOrDefault(k.Key)}/{t.KillBy.GetValueOrDefault(k.Key)}")));
        if (!staminaCurve) return;
        long dead = t.Victims.Where(k => k.Key != "hurt").Sum(k => k.Value);
        if (dead > 0)
            Console.WriteLine("   who dies: " + string.Join("  ", new[] { "front", "front, at an end", "rear ranks", "out of line" }.Select(k => $"{k} {H.P(t.Victims.GetValueOrDefault(k) / (float)dead).Trim()}"))
                              + $" | already below 30% health {H.P(t.Victims.GetValueOrDefault("hurt") / (float)dead).Trim()}");
        Console.WriteLine($"   rotation: {t.Swaps} swaps, {t.Swaps / Math.Max(1e-3, t.FrontMeleeSec / 60.0):F2} per front-rank man per minute of melee");
        Console.WriteLine("   mean stamina from contact (front rank / rear ranks, n fights still undecided):");
        Console.WriteLine("     " + string.Join("  ", Enumerable.Range(0, Tally.Slots).Where(k => t.StN[k] > 0).Select(k => $"{30 * k}s {t.FrontSt[k] / t.StN[k]:F2}/{t.RearSt[k] / t.StN[k]:F2} ({t.StN[k]})")));
    }

    public static void Run(int seeds)
    {
        // A 20v20 line fight: one holds, the other attacks.
        var lines = new Tally();
        Parallel.For(0, seeds, i =>
        {
            var b = new Battle(1000 + i) { RecordEvents = true };
            b.AddBattalion(0, UnitTypes.Legionaries, "Holder", new Vector2(0f, 0f), H.North);
            var c = b.AddBattalion(1, UnitTypes.Legionaries, "Attacker", new Vector2(0f, 32f), H.South);
            c.OrderAttack(b.Battalions[0], false);
            lines.Add(Watch(b, 400f, 0f));
        });
        Report($"20v20 Legionaries line fight, {seeds} seeds", lines, true);

        // An equal 1v1 duel.
        var duel = new Tally();
        Parallel.For(0, seeds * 10, i =>
        {
            var b = new Battle(i * 7919 + 17) { RecordEvents = true };
            var ua = b.AddBattalion(0, H.Sized(UnitTypes.Legionaries, 1), "A", new Vector2(0f, 0f), H.North);
            var ub = b.AddBattalion(1, H.Sized(UnitTypes.Legionaries, 1), "B", new Vector2(0f, 3f), H.South);
            H.SetMan(ua.Soldiers[0], 0.5f, noFlee: true);
            H.SetMan(ub.Soldiers[0], 0.5f, noFlee: true);
            H.Place(ua.Soldiers[0], new Vector2(0f, 0f), H.North);
            H.Place(ub.Soldiers[0], new Vector2(0f, 3f), H.South);
            H.ResetMorale(ua);
            H.ResetMorale(ub);
            H.Release(ua, ub);
            H.Release(ub, ua);
            var t = Watch(b, 180f, 0f);
            float end = ua.Soldiers[0].Dead ? ua.Soldiers[0].DeathTime : ub.Soldiers[0].Dead ? ub.Soldiers[0].DeathTime : float.NaN;
            t.Melee.Add(end);
            duel.Add(t);
        });
        Report($"1v1 duel, 0.5 v 0.5, {seeds * 10} seeds (length = time to the kill)", duel, false);

        Fatigue(Math.Max(4, seeds / 2));
    }

    /// <summary>
    /// The pure fatigue curve: two single-rank lines of 10 average men (skill 0.5) in continuous contact, nobody can
    /// be hurt or break and there is no rank behind to relieve them. Endurance 0.75 / 1.0 / 1.3 in turn along the line.
    /// After 180 s the attackers walk 30 m back and both sides rest.
    /// </summary>
    public static void Fatigue(int seeds)
    {
        float[] ends = { 0.75f, 1f, 1.3f };
        const int nT = 7, nR = 4;
        var contactCurve = new double[3, nT];
        var restCurve = new double[2, nR];
        int runs = 0;
        var actionsPerMin = new List<float>();
        var lk = new object();
        Parallel.For(0, seeds, seed =>
        {
            var b = new Battle(6000 + seed) { RecordEvents = false };
            var A = b.AddBattalion(0, H.Sized(UnitTypes.Legionaries, 10), "A", new Vector2(0f, 0f), H.North);
            var B = b.AddBattalion(1, H.Sized(UnitTypes.Legionaries, 10), "B", new Vector2(0f, 16f), H.South);
            foreach (var u in new[] { A, B })
            {
                for (int i = 0; i < u.Soldiers.Count; i++)
                {
                    var s = u.Soldiers[i];
                    H.SetMan(s, 0.5f, noFlee: true);
                    s.Endurance = ends[i % 3];
                    s.MaxStamina = s.Stamina = 100f * s.Endurance;
                }
                H.ResetMorale(u);
            }
            B.OrderAttack(A, false);
            float contact = -1f;
            var c = new double[3, nT];
            var r = new double[2, nR];
            int windups = 0;
            var ph = A.Soldiers.Concat(B.Soldiers).ToDictionary(s => s, s => s.Phase);
            int n = -1;   // steps since contact; 1800 steps = 30 s
            while (b.Time < 400f)
            {
                b.Step(H.Dt);
                foreach (var s in b.Soldiers) { s.Hp = s.MaxHp; s.Wounds = 0; }
                if (contact < 0f && (A.FightingCount > 0 || B.FightingCount > 0)) contact = b.Time;
                if (contact < 0f) continue;
                n++;
                foreach (var s in b.Soldiers)
                {
                    if (n < 1800 * 6 && s.Phase == CombatPhase.Windup && ph[s] != CombatPhase.Windup) windups++;
                    ph[s] = s.Phase;
                }
                if (n % 1800 == 0 && n / 1800 < nT)
                    for (int g = 0; g < 3; g++)
                        c[g, n / 1800] = b.Soldiers.Where(s => Math.Abs(s.Endurance - ends[g]) < 0.01f).Average(s => s.StaminaFrac);
                if (n == 1800 * 6) B.OrderMove(B.Centroid + new Vector2(0f, 30f), H.South, false);
                if (n >= 1800 * 6 && (n - 1800 * 6) % 1800 == 0)
                {
                    int j = (n - 1800 * 6) / 1800;
                    if (j >= nR) break;
                    r[0, j] = A.Soldiers.Average(s => s.StaminaFrac);
                    r[1, j] = B.Soldiers.Average(s => s.StaminaFrac);
                }
            }
            lock (lk)
            {
                runs++;
                for (int g = 0; g < 3; g++) for (int k = 0; k < nT; k++) contactCurve[g, k] += c[g, k];
                for (int g = 0; g < 2; g++) for (int k = 0; k < nR; k++) restCurve[g, k] += r[g, k];
                actionsPerMin.Add(windups / 20f / 3f);
            }
        });
        Console.WriteLine($"\n-- fatigue: 10v10 single rank, average men who can't be hurt, continuous contact ({runs} seeds; windups per man-minute {H.Mean(actionsPerMin):F1})");
        Console.WriteLine("   stamina by endurance at 0/30/60/.../180 s of contact:");
        for (int g = 0; g < 3; g++)
            Console.WriteLine($"     End {ends[g]:F2}: " + string.Join("  ", Enumerable.Range(0, nT).Select(k => $"{contactCurve[g, k] / runs:F2}")));
        Console.WriteLine("   then resting (0/30/60/90 s): holders standing " + string.Join("  ", Enumerable.Range(0, nR).Select(k => $"{restCurve[0, k] / runs:F2}"))
                          + " | attackers walking 30 m back then standing " + string.Join("  ", Enumerable.Range(0, nR).Select(k => $"{restCurve[1, k] / runs:F2}")));
    }
}
