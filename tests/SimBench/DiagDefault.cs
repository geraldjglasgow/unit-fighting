using System;
using System.Collections.Generic;
using System.Linq;
using UnitFighting.Sim;

namespace SimBench;

/// <summary>The default battle, battalion by battalion: when it first fought and first broke, and what it had suffered by then.</summary>
public static class DefaultDiag
{
    public static void Run(int seeds)
    {
        int nu = Scenarios.Default(1, true).Battalions.Count;
        var contact = new double[nu]; var rout = new double[nu]; var routN = new int[nu]; var deadAtRout = new double[nu];
        var flankAtRout = new double[nu]; var rearAtRout = new double[nu]; var fightAtRout = new double[nu]; var stamAtRout = new double[nu];
        var names = new string[nu];
        double end = 0; int rome = 0;
        for (int seed = 1; seed <= seeds; seed++)
        {
            var b = Scenarios.Default(seed, aiForPlayer: true);
            b.RecordEvents = false;
            var c = Enumerable.Repeat(-1f, nu).ToArray();
            var r = Enumerable.Repeat(-1f, nu).ToArray();
            var peakFlank = new int[nu]; var peakRear = new int[nu];
            while (b.Winner < 0 && b.Time < 400f)
            {
                b.Step(H.Dt);
                for (int i = 0; i < nu; i++)
                {
                    var u = b.Battalions[i];
                    names[i] = u.Name;
                    if (c[i] < 0 && u.FightingCount > 0) c[i] = b.Time;
                    if (r[i] < 0 && !u.Routing) { peakFlank[i] = Math.Max(peakFlank[i], u.FlankAttackers); peakRear[i] = Math.Max(peakRear[i], u.RearAttackers); }
                    if (r[i] < 0 && u.Routing)
                    {
                        r[i] = b.Time;
                        routN[i]++;
                        rout[i] += b.Time - c[i];
                        deadAtRout[i] += H.DeadIn(u);
                        flankAtRout[i] += peakFlank[i];
                        rearAtRout[i] += peakRear[i];
                        stamAtRout[i] += u.AvgStamina;
                    }
                }
            }
            for (int i = 0; i < nu; i++) contact[i] += c[i];
            end += b.EndTime;
            if (b.Winner == 0) rome++;
        }
        Console.WriteLine($"default battle, {seeds} seeds: Rome {100.0 * rome / seeds:F0}%, decided at {end / seeds:F0} s");
        Console.WriteLine("  battalion                  | first contact s | routed | contact->rout s | dead at rout | peak flank / rear attackers before rout | stamina at rout");
        for (int i = 0; i < nu; i++)
        {
            double k = Math.Max(1, routN[i]);
            Console.WriteLine($"  {names[i],-26} | {contact[i] / seeds,15:F1} | {100.0 * routN[i] / seeds,5:F0}% | {rout[i] / k,15:F1} | {deadAtRout[i] / k,12:F1} | {flankAtRout[i] / k,19:F1} / {rearAtRout[i] / k,4:F1} | {stamAtRout[i] / k,5:F2}");
        }
    }
}

/// <summary>How far an attacker's rear ranks trail its front rank, from the charge to a few seconds after contact.</summary>
public static class TrailDiag
{
    public static void Run(int seeds)
    {
        foreach (var (label, run, type) in new[] { ("Legionaries walk in (AI)", false, UnitTypes.Legionaries), ("Legionaries ordered to run", true, UnitTypes.Legionaries), ("Warband walk in (AI)", false, UnitTypes.Warband) })
        {
            var at = new[] { -2f, -1f, 0f, 1f, 2f, 3f, 5f };
            var depth = new double[at.Length];
            var rearFromSlot = new double[at.Length];
            for (int seed = 0; seed < seeds; seed++)
            {
                var b = new Battle(7000 + seed) { RecordEvents = false };
                var A = b.AddBattalion(0, UnitTypes.Legionaries, "Holder", new Godot.Vector2(0f, 0f), H.North);
                var B = b.AddBattalion(1, type, "Attacker", new Godot.Vector2(0f, 40f), H.South);
                B.OrderAttack(A, run);
                var hist = new List<(float t, float depth, float rear)>();
                float contact = -1f;
                while (b.Time < 60f)
                {
                    b.Step(H.Dt);
                    var fwd = UnitFighting.Sim.MathUtil.Dir(B.Facing);
                    double fr = 0, rr = 0, rs = 0; int nf = 0, nr = 0;
                    foreach (var s in B.Soldiers)
                    {
                        if (!s.Active || s.Fleeing || s.Slot < 0 || s.Slot >= B.Slots.Count) continue;
                        if (B.Slots[s.Slot].Rank == 0) { fr += s.Pos.Dot(fwd); nf++; }
                        else { rr += s.Pos.Dot(fwd); rs += s.Pos.DistanceTo(B.SlotWorld(s.Slot)); nr++; }
                    }
                    hist.Add((b.Time, nf > 0 && nr > 0 ? (float)(fr / nf - rr / nr) : float.NaN, nr > 0 ? (float)(rs / nr) : float.NaN));
                    if (contact < 0f && (A.FightingCount > 0 || B.FightingCount > 0)) contact = b.Time;
                    if (contact >= 0f && b.Time > contact + 6f) break;
                }
                for (int i = 0; i < at.Length; i++)
                {
                    var h = hist.OrderBy(x => Math.Abs(x.t - (contact + at[i]))).First();
                    depth[i] += h.depth;
                    rearFromSlot[i] += h.rear;
                }
            }
            Console.WriteLine($"{label}: front-rank to rear-rank depth (m; formed spacing 1.2) / rear ranks' distance from their slots, at contact + s");
            Console.WriteLine("   " + string.Join("  ", at.Select((t, i) => $"{t,3:+0;-0;0}s: {depth[i] / seeds,4:F1} / {rearFromSlot[i] / seeds,3:F1}")));
        }
    }
}
