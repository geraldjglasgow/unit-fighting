using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using SimBench;

// Usage: simbench [scenario] [--seeds N] [--set Owner.Field=value]... [--hold Type --atk Type]
//   scenarios in "all": duel kits 1v2 lines flank matchups default perf charges relief
//   extra: tune (one-screen summary for tuning), quick, duel-charge,
//          diagnostics: engage, morale, kills, snap, diag-lines, diag-contact, charge, chargetrace (--hold/--atk pick UnitTypes by field name),
//          diag-fight (front-rank rhythm, blow mix, feints, bashes, stamina over time, rotation), fatigue (the pure stamina curve)
// --set overrides a unit type or equipment number for this run, e.g. --set Warband.SkillMean=0.5 --set Longsword.Windup=0.58
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
string scen = "all";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--seeds" && i + 1 < args.Length) Benches.Seeds = int.Parse(args[++i]);
    else if (args[i] == "--set" && i + 1 < args.Length) Benches.Set(args[++i]);
    else if (args[i] == "--hold" && i + 1 < args.Length) Benches.HoldType = Benches.TypeByName(args[++i]);
    else if (args[i] == "--atk" && i + 1 < args.Length) Benches.AtkType = Benches.TypeByName(args[++i]);
    else if (!args[i].StartsWith("-")) scen = args[i];
}

var sw = Stopwatch.StartNew();
try
{
    bool all = scen == "all";
    if (all || scen == "duel") Benches.Duels();
    if (all || scen == "kits") Benches.KitDuels();
    if (scen == "duel-charge") Benches.Duels(charge: true);
    if (all || scen == "1v2") Benches.OneVsTwo();
    if (all || scen == "lines") Benches.Lines();
    if (all || scen == "flank") Benches.Flanking();
    if (all || scen == "matchups") Benches.Matchups();
    if (all || scen == "default") Benches.Default();
    if (all || scen == "perf") Benches.Perf();
    if (scen == "quick") Benches.Quick();
    if (all || scen == "charges") Benches.Charges();
    if (all || scen == "relief") Benches.Relief();
    if (scen == "tune") Benches.Tune();
    if (scen == "charge") ChargeDiag.Run();
    if (scen == "engage") EngageDiag.Run(Benches.Seeds > 0 ? Benches.Seeds : 10);
    if (scen == "kills") KillLog.Run(Benches.Seeds);
    if (scen == "morale") MoraleDiag.Run(Benches.Seeds);
    if (scen == "chargetrace") ChargeTrace.Run();
    if (scen == "snap") Snap.Lines(Benches.Seeds > 0 ? Benches.Seeds : 0);
    if (scen == "diag-contact") ContactDiag.Run(Benches.Seeds > 0 ? Benches.Seeds : 60, Benches.HoldType, Benches.AtkType);
    if (scen == "diag-default") DefaultDiag.Run(Benches.Seeds > 0 ? Benches.Seeds : 60);
    if (scen == "diag-trail") TrailDiag.Run(Benches.Seeds > 0 ? Benches.Seeds : 40);
    if (scen == "diag-lines") Diag.Lines(Benches.Seeds > 0 ? Benches.Seeds : 30, Benches.HoldType, Benches.AtkType);
    if (scen == "diag-fight") FightDiag.Run(Benches.Seeds > 0 ? Benches.Seeds : 16);
    if (scen == "fatigue") FightDiag.Fatigue(Benches.Seeds > 0 ? Benches.Seeds : 8);
}
catch (Exception e)
{
    Console.WriteLine("\n!!!!!!!! EXCEPTION IN SIM !!!!!!!!\n" + e);
    return 2;
}

Console.WriteLine();
#if SCRATCH
Console.WriteLine($"windups: riposte {UnitFighting.Sim.Tun.WRiposte} delay {UnitFighting.Sim.Tun.WDelay} opening {UnitFighting.Sim.Tun.WOpening} (recover {UnitFighting.Sim.Tun.WOpeningRecover}, stagger {UnitFighting.Sim.Tun.WOpeningStagger}, facing {UnitFighting.Sim.Tun.WOpeningFacing})");
#endif
Console.WriteLine(Sanity.Report());
if (!Sanity.Failures.IsEmpty)
{
    Console.WriteLine("!!!!!!!! SANITY FAILURES !!!!!!!!");
    int shown = 0;
    foreach (var f in Sanity.Failures)
    {
        if (shown++ >= 25) { Console.WriteLine($"  ... and {Sanity.Failures.Count - 25} more"); break; }
        Console.WriteLine("  " + f);
    }
    Console.WriteLine($"({sw.Elapsed.TotalSeconds:F1} s)");
    return 1;
}
Console.WriteLine($"all sanity checks passed ({sw.Elapsed.TotalSeconds:F1} s)");
return 0;
