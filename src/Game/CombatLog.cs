using System;
using Godot;
using UnitFighting.Sim;

namespace UnitFighting.Game;

/// <summary>
/// Every man's recent fighting, for the inspector: a small ring of entries per soldier, written from
/// <see cref="Battle.Events"/> after every sim step, so each entry carries the exact battle time and the state the
/// blow left the men in. The view drains and clears that list once a frame; this only reads what has been added since
/// it last looked, so tell it (<see cref="Drained"/>) whenever the list is cleared. Men breaking, rallying and escaping
/// have no events of their own; a per-man state byte spots them. Storage is flat arrays sized once per battle, so
/// nothing is allocated while the battle runs.
/// </summary>
public sealed class CombatLog
{
    public const int PerMan = 12;
    const float MergeWindow = 8f;   // repeated blocks or glances between the same two men within this many seconds share a line

    public enum Kind : byte
    {
        Blocked, WasBlocked, Hit, WasHit, Glanced, GlancedOff, Killed, KilledBy,
        BrokeGuard, GuardBroken, Charged, ChargedBy, Broke, Rallied, Escaped,
    }

    [Flags]
    public enum Flag : ushort
    {
        None = 0,
        Shield = 1,     // turned on the shield rather than parried
        Stagger = 2,    // it left the man on the receiving end reeling
        Down = 4,       // ...or knocked him flat
        Flank = 8,      // it came at the receiving man from the flank
        Rear = 16,      // ...or from behind
        Left = 32,      // it came from his left (for which arm or leg)
        Thrust = 64,    // a thrust rather than a cut
        Charge = 128,   // a kill by the impact of a charge
        Rout = 256,     // he ran with his battalion's rout rather than on his own
    }

    public enum Tone : byte { Quiet, Normal, Good, Warn, Bad }

    public struct Entry
    {
        public float Time;
        public Kind Kind;
        public Flag Flags;
        public HitLocation Location;
        public byte Count;       // repeats folded into this line
        public float Amount;     // a wound as a share of the wounded man's health, or a charge's impact ratio
        public Soldier Other;
    }

    readonly Battle _battle;
    Entry[] _entries = Array.Empty<Entry>();
    int[] _head = Array.Empty<int>(), _count = Array.Empty<int>(), _version = Array.Empty<int>(), _woundsDealt = Array.Empty<int>();
    byte[] _state = Array.Empty<byte>();   // 0 in the ranks, 1 running, 2 escaped, 3 dead
    int _seen;

    public CombatLog(Battle battle)
    {
        _battle = battle;
        Grow(battle.NextSoldierId);
        foreach (var s in battle.Soldiers) _state[s.Id] = State(s);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Changes whenever something is added to his log (so the inspector only rebuilds when it has to).</summary>
    public int Version(Soldier s) => s.Id < _version.Length ? _version[s.Id] : 0;

    public int Count(Soldier s) => s.Id < _count.Length ? _count[s.Id] : 0;

    /// <summary>His k-th most recent entry, k = 0 the newest (k &lt; <see cref="Count"/>).</summary>
    public ref readonly Entry Recent(Soldier s, int k) => ref _entries[s.Id * PerMan + (_head[s.Id] - 1 - k + 2 * PerMan) % PerMan];

    /// <summary>Blows he has landed that drew blood (glances don't count; killing blows do).</summary>
    public int WoundsDealt(Soldier s) => s.Id < _woundsDealt.Length ? _woundsDealt[s.Id] : 0;

    // ---------------------------------------------------------------- writing

    /// <summary>Call after every sim step: logs the events added since the last call.</summary>
    public void Collect()
    {
        var events = _battle.Events;
        if (_seen > events.Count) _seen = 0;   // cleared without our being told
        for (int i = _seen; i < events.Count; i++)
        {
            var e = events[i];
            Soldier a = e.Attacker, d = e.Defender;
            if (d == null) continue;   // a battalion's rout or rally: each man's own running and rallying is picked up below
            switch (e.Kind)
            {
                case EventKind.Block or EventKind.Parry when a != null:
                {
                    var f = Blow(a) | Side(a, d) | (e.Kind == EventKind.Block ? Flag.Shield : 0);
                    Add(d, Kind.Blocked, a, f);
                    Add(a, Kind.WasBlocked, d, f);
                    break;
                }
                case EventKind.GuardBreak when a != null:
                {
                    var f = Blow(a) | Side(a, d) | Flag.Stagger;
                    Add(d, Kind.GuardBroken, a, f);
                    Add(a, Kind.BrokeGuard, d, f);
                    break;
                }
                case EventKind.Hit when a != null:
                {
                    var f = Blow(a) | Side(a, d) | Reeling(d);
                    float share = e.Amount / Mathf.Max(1f, d.MaxHp);
                    Add(a, Kind.Hit, d, f, e.Location, share);
                    Add(d, Kind.WasHit, a, f, e.Location, share);
                    _woundsDealt[a.Id]++;
                    break;
                }
                case EventKind.Glance when a != null:
                {
                    var f = Blow(a) | Side(a, d);
                    Add(a, Kind.Glanced, d, f, e.Location);
                    Add(d, Kind.GlancedOff, a, f, e.Location);
                    break;
                }
                case EventKind.Kill:
                {
                    // A charge that kills emits its impact first, then the kill.
                    bool trampled = i > 0 && events[i - 1].Kind == EventKind.Impact && events[i - 1].Defender == d;
                    var f = a == null ? Flag.None : trampled ? Flag.Charge | Side(a, d) : Blow(a) | Side(a, d);
                    if (a != null)
                    {
                        Add(a, Kind.Killed, d, f, e.Location);
                        if (!trampled) _woundsDealt[a.Id]++;
                    }
                    Add(d, Kind.KilledBy, a, f, e.Location);
                    break;
                }
                case EventKind.Impact when a != null:
                {
                    var f = Side(a, d) | Reeling(d);
                    Add(a, Kind.Charged, d, f, amount: e.Amount);
                    Add(d, Kind.ChargedBy, a, f, amount: e.Amount);
                    break;
                }
            }
        }
        _seen = events.Count;
        ScanNerve();
    }

    /// <summary>Battle.Events has just been cleared.</summary>
    public void Drained() => _seen = 0;

    static byte State(Soldier s) => s.Dead ? (byte)3 : s.Escaped ? (byte)2 : s.Fleeing ? (byte)1 : (byte)0;

    /// <summary>Who has broken, rallied or got away since the last step.</summary>
    void ScanNerve()
    {
        var list = _battle.Soldiers;
        if (_state.Length < _battle.NextSoldierId) Grow(_battle.NextSoldierId);
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            byte was = _state[s.Id], now = State(s);
            if (now == was) continue;
            _state[s.Id] = now;
            if (now == 1) Add(s, Kind.Broke, null, s.Unit.Routing ? Flag.Rout : Flag.None);
            else if (now == 0) Add(s, Kind.Rallied, null, Flag.None);
            else if (now == 2) Add(s, Kind.Escaped, null, Flag.None);
        }
    }

    static Flag Blow(Soldier a) => a.Weapon.Style == WeaponStyle.Spear || (a.Weapon.Style == WeaponStyle.Sword && a.AttackStyle == 1) ? Flag.Thrust : Flag.None;

    /// <summary>Where the blow came from as the man on the receiving end saw it (the sim's own arcs, roughly).</summary>
    static Flag Side(Soldier a, Soldier d)
    {
        float rel = MathUtil.RelativeAngle(d.Facing, a.Pos - d.Pos);
        float abs = Mathf.Abs(rel);
        var f = rel > 0f ? Flag.Left : Flag.None;
        if (abs >= 2.0f) f |= Flag.Rear;        // out of sight: he never saw it coming
        else if (abs >= 1.2f) f |= Flag.Flank;
        return f;
    }

    /// <summary>Did what just happened knock him flat or leave him reeling? (Entered this step.)</summary>
    static Flag Reeling(Soldier d)
    {
        if (d.PhaseTime > Battle.FixedDt * 1.5f) return Flag.None;
        return d.Phase switch { CombatPhase.Down => Flag.Down, CombatPhase.Stagger => Flag.Stagger, _ => Flag.None };
    }

    static bool Mergeable(Kind k) => k is Kind.Blocked or Kind.WasBlocked or Kind.Glanced or Kind.GlancedOff;

    void Add(Soldier s, Kind kind, Soldier other, Flag flags, HitLocation loc = HitLocation.Torso, float amount = 0f)
    {
        int id = s.Id;
        if (id >= _head.Length) Grow(id + 1);
        float now = _battle.Time;
        int b = id * PerMan;
        if (_count[id] > 0 && Mergeable(kind))
        {
            ref var last = ref _entries[b + (_head[id] + PerMan - 1) % PerMan];
            if (last.Kind == kind && last.Other == other && now - last.Time < MergeWindow && last.Count < 99)
            {
                last.Count++;
                last.Time = now;
                last.Flags |= flags;
                _version[id]++;
                return;
            }
        }
        _entries[b + _head[id]] = new Entry { Time = now, Kind = kind, Flags = flags, Location = loc, Count = 1, Amount = amount, Other = other };
        _head[id] = (_head[id] + 1) % PerMan;
        if (_count[id] < PerMan) _count[id]++;
        _version[id]++;
    }

    void Grow(int men)
    {
        int n = Math.Max(64, (men + 63) / 64 * 64);
        if (n <= _head.Length) return;
        Array.Resize(ref _entries, n * PerMan);
        Array.Resize(ref _head, n);
        Array.Resize(ref _count, n);
        Array.Resize(ref _version, n);
        Array.Resize(ref _woundsDealt, n);
        Array.Resize(ref _state, n);
    }

    // ---------------------------------------------------------------- wording

    /// <summary>
    /// One line of his log, from his point of view: the text before the other man's name and the text after it
    /// (<paramref name="named"/> false when there is nobody to name).
    /// </summary>
    public static void Describe(in Entry e, Soldier self, out string before, out string after, out bool named)
    {
        var o = e.Other;
        var f = e.Flags;
        named = o != null;
        string dir = (f & Flag.Rear) != 0 ? " from behind" : (f & Flag.Flank) != 0 ? " from the flank" : "";
        string times = e.Count > 1 ? $" ×{e.Count}" : "";
        string where = Where(e.Location, f);
        switch (e.Kind)
        {
            case Kind.Blocked:
                if (e.Count > 1) { before = "blocked "; after = "'s blows" + times; }
                else if ((f & Flag.Shield) != 0) { before = "blocked "; after = $"'s {BlowName(o, f)} (shield)"; }
                else { before = "parried "; after = $"'s {BlowName(o, f)}"; }
                return;
            case Kind.WasBlocked:
                if (e.Count > 1) { before = "blows blocked by "; after = times; }
                else if ((f & Flag.Shield) != 0) { before = $"{BlowName(self, f)} blocked by "; after = "'s shield"; }
                else { before = $"{BlowName(self, f)} parried by "; after = ""; }
                return;
            case Kind.Hit:
                before = (f & Flag.Down) != 0 ? "floored " : (f & Flag.Stagger) != 0 ? "staggered " : "hit ";
                after = $"{dir} — {where}, {Severity(e.Amount)}";
                return;
            case Kind.WasHit:
                before = (f & Flag.Stagger) != 0 ? "staggered by " : "hit by ";
                after = $"{dir} — {where}, {Severity(e.Amount)}";
                return;
            case Kind.Glanced:
                before = e.Count > 1 ? "blows glanced off " : $"{BlowName(self, f)} glanced off ";
                after = $"'s {Armour(o)}{times}";
                return;
            case Kind.GlancedOff:
                before = "";
                after = (e.Count > 1 ? "'s blows" : $"'s {BlowName(o, f)}") + $" glanced off his {Armour(self)}{times}";
                return;
            case Kind.Killed:
                if ((f & Flag.Charge) != 0) { before = "trampled "; after = " in the charge"; }
                else { before = "killed "; after = $"{dir} — {where}"; }
                return;
            case Kind.KilledBy:
                if (o == null) { before = "killed"; after = ""; }
                else if ((f & Flag.Charge) != 0) { before = "trampled in "; after = "'s charge"; }
                else { before = "killed by "; after = $"{dir} — {where}"; }
                return;
            case Kind.BrokeGuard:
                before = "broke ";
                after = "'s guard";
                return;
            case Kind.GuardBroken:
                before = "guard broken by ";
                after = ", staggered";
                return;
            case Kind.Charged:
                if ((f & Flag.Down) != 0) { before = "knocked "; after = $" flat{dir}"; }
                else if ((f & Flag.Stagger) != 0) { before = "charge staggered "; after = dir; }
                else { before = "charged into "; after = dir; }
                return;
            case Kind.ChargedBy:
                if ((f & Flag.Down) != 0) { before = "knocked flat by "; after = $"'s charge{dir}"; }
                else if ((f & Flag.Stagger) != 0) { before = "staggered by "; after = $"'s charge{dir}"; }
                else { before = "held "; after = $"'s charge{dir}"; }
                return;
            case Kind.Broke:
                before = (f & Flag.Rout) != 0 ? "ran with the rout" : "lost his nerve and ran";
                after = "";
                return;
            case Kind.Rallied:
                before = "rallied";
                after = "";
                return;
            default:
                before = "fled the field";
                after = "";
                return;
        }
    }

    /// <summary>How much a line matters to him: kills and knockdowns he dealt stand out, his own wounds warn.</summary>
    public static Tone ToneOf(in Entry e) => e.Kind switch
    {
        Kind.Killed or Kind.BrokeGuard => Tone.Good,
        Kind.Charged when (e.Flags & (Flag.Down | Flag.Stagger)) != 0 => Tone.Good,
        Kind.KilledBy => Tone.Bad,
        Kind.WasHit when e.Amount >= 0.35f => Tone.Bad,
        Kind.ChargedBy when (e.Flags & Flag.Down) != 0 => Tone.Bad,
        Kind.WasHit or Kind.GuardBroken or Kind.Broke or Kind.Escaped => Tone.Warn,
        Kind.ChargedBy when (e.Flags & Flag.Stagger) != 0 => Tone.Warn,
        Kind.Hit or Kind.Charged or Kind.Rallied => Tone.Normal,
        _ => Tone.Quiet,
    };

    static string BlowName(Soldier a, Flag f) => a?.Weapon.Style switch
    {
        WeaponStyle.Axe => "axe blow",
        WeaponStyle.Spear => "spear thrust",
        _ => (f & Flag.Thrust) != 0 ? "thrust" : "cut",
    };

    /// <summary>The sim rolls head, body, arm or leg; the side is the side the blow came in from.</summary>
    static string Where(HitLocation loc, Flag f) => loc switch
    {
        HitLocation.Head => "head",
        HitLocation.Arm => (f & Flag.Left) != 0 ? "left arm" : "right arm",
        HitLocation.Leg => (f & Flag.Left) != 0 ? "left leg" : "right leg",
        _ => "body",
    };

    static string Severity(float share) => share < 0.15f ? "light" : share < 0.35f ? "heavy" : "grave";

    static string Armour(Soldier s) => s?.Armor.Name.ToLowerInvariant() ?? "armour";
}
