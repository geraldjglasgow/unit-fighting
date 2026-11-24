using System;
using Godot;

namespace UnitFighting.Sim;

public enum HitLocation { Head, Torso, Arm, Leg }

/// <summary>
/// One man's blow against another. Nothing here is a unit-level dice roll: whether a blow lands depends on
/// where it comes from relative to the defender's shield and eyes, the line it comes in on, what each man is doing
/// at that instant, their skill, how tired they are, and how many others are pressing the defender.
///
/// A man in a fight is never still for long. Between real blows he keeps his weapon moving: short probing jabs
/// that are mostly caught, feints to draw the other man's guard, a punch with the shield boss to shove him back.
/// Most of that comes to nothing, which is why a fight between two lines is a long grind in kills but not a
/// standing-still one.
/// </summary>
public static class Combat
{
    public static float Tempo(Soldier s) =>
        Mathf.Lerp(1.25f, 0.8f, s.Skill)
        * (1.3f - 0.3f * s.Agility)
        * (1f + 0.8f * (1f - s.StaminaFrac) * (1f - s.StaminaFrac))
        * (1f + 0.35f * (1f - s.HpFrac));

    // What he does when he has the initiative.
    enum Intent { Blow, Probe, Feint, Bash }

    /// <summary>view: a feint's windup is pulled back once PhaseTime reaches this share of its PhaseLength.</summary>
    public const float FeintPullback = 0.55f;
    const float FeintOpening = 0.55f;   // how long a man who bit on a feint has his guard in the wrong place
    const float GuardMemory = 0.6f;     // how long his guard stays where the last blow took it
    const float ShoveOpening = 0.45f;   // how long a man shoved back by a shield punch takes to get his shield square again
    const float BashReach = 1.2f;       // a shield punch needs him close
    const float ProbeWindup = 0.55f;    // a probe is a short jab from the guard, not a full blow
    const float ProbeGetsThrough = 0.4f;   // share of the blows a man would let through that still get through when it's only a probe
    const float ProbeDamage = 0.4f;     // a probe that does get through is a jab, not a blow
    const float FastWindup = 0.65f;     // a riposte or the next blow of a combination comes from where the last one left his arm
    const float LinePatience = 1.3f;    // a man in a line with shields beside him has no need to hurry
    const float WallCover = 0.45f;      // share of the blows from the front that get past him which each neighbour's shield stops
    const float HurtInLine = 0.5f;      // below this share of his health a man in a line fights behind his shield and waits to be relieved

    // ---------------------------------------------------------------- the knobs that set how long fights last

    /// <summary>
    /// How hard a blow that gets through wounds, as a share of the weapon's Damage. The main knob for the length of
    /// every fight: duels, line fights and routs alike (line fights last roughly in inverse proportion to it).
    /// </summary>
    public const float Lethality = 0.75f;

    /// <summary>
    /// How much a better man's guard gains from his skill over his opponent's. With it, how far skill decides a duel
    /// (0.7 against 0.5 wins about 80%); the longer fights last, the more a small edge tells, so the two go together.
    /// </summary>
    const float SkillEdge = 0.45f;

    /// <summary>
    /// How hard fighting is on the wind. Every blow, probe, feint, block, shove and step costs him stamina, and he
    /// gets a good part of it back between them (SoldierAI.UpdateStamina), so it is the pace of the exchange, the
    /// weight he carries and his fitness that wear him down. A fresh average man in continuous contact is down to
    /// about a third after two or three minutes, and it takes him about a minute out of the fight to get most of
    /// it back. The heavier the cost against the recovery, the more a side with men to spare can out-last one
    /// without.
    /// </summary>
    public const float Exertion = 0.5f;

    /// <summary>
    /// Armour is carried into every blow and every step: mail (a tenth or more of a man's weight, much of it on his
    /// shoulders and arms) makes him tire about a third faster. Any more and a mailed man loses to an unarmoured one
    /// on wind what his mail wins him in wounds.
    /// </summary>
    public static float ArmourLoad(Soldier s) => 1f + 0.3f * s.Armor.Weight;

    /// <summary>
    /// How long he waits before acting on his own initiative again: veterans press, the tired catch their breath.
    /// A man in a line with shields either side of him waits a little longer (and mostly probes, see ChooseIntent).
    /// </summary>
    static float NextDelay(Battle b, Soldier s)
    {
        float tired = 1f - s.StaminaFrac;
        float delay = Mathf.Lerp(0.9f, 0.4f, s.Skill) * b.Range(0.5f, 1.5f) * (1f + 2f * tired * tired);
        return InLine(b, s) ? delay * LinePatience : delay;
    }

    /// <summary>He stands in a line, shields beside him, within reach of the man he is fighting.</summary>
    static bool InLine(Battle b, Soldier s)
    {
        var t = s.Target;
        bool exposed = t == null || s.Pos.DistanceTo(t.Pos) <= t.Weapon.Reach + 0.2f;
        return exposed && WallMates(b, s) > 0;
    }

    static void Enter(Soldier s, CombatPhase phase, float length)
    {
        s.Phase = phase;
        s.PhaseTime = 0f;
        s.PhaseLength = length;
    }

    public static void Stagger(Soldier s, float length)
    {
        if (s.Phase == CombatPhase.Down) return;
        Enter(s, CombatPhase.Stagger, length);
        s.StrikeTarget = null;
        s.Riposte = s.FollowUp = false;
    }

    public static void KnockDown(Soldier s, float length)
    {
        if (s.Phase == CombatPhase.Down) return;   // already lying there
        Enter(s, CombatPhase.Down, length);
        s.StrikeTarget = null;
        s.Riposte = s.FollowUp = false;
        s.Charging = false;
    }

    public static void TickPhase(Battle b, Soldier s, float dt)
    {
        s.PhaseTime += dt;
        switch (s.Phase)
        {
            case CombatPhase.Ready:
            {
                s.ReadyDelay -= dt;
                var t = s.Target;
                if (s.Fleeing || s.Charging || t == null || !t.Active || !CanStrike(b, s, t)) break;   // a charging man runs in first
                // A man with a shield doesn't swing at a man charging in on him, to be caught with his arm out:
                // he sets himself, takes the charge and the blow that comes with it on the shield, and answers once
                // it is spent. (A spear meets the charge with its point instead.)
                if (s.Shield != null && !s.Weapon.Braces && (t.Target == s || t.StrikeTarget == s)
                    && (t.Charging || (b.Time < t.ChargeBonusUntil && t.LastStrikeTime < t.LastImpactTime))) break;
                if (s.Riposte || s.FollowUp)
                {
                    StartWindup(b, s, t, Intent.Blow);
                    break;
                }
                // An opening: he's reeling, he's turned away, he's still recovering from his own blow and there is
                // time to land one before his guard is back, or his guard has gone after a feint or a shove.
                bool opening = t.Phase is CombatPhase.Stagger or CombatPhase.Down
                               || (t.Phase == CombatPhase.Recover && t.PhaseLength - t.PhaseTime > s.Weapon.Windup * Tempo(s))
                               || Mathf.Abs(MathUtil.RelativeAngle(t.Facing, s.Pos - t.Pos)) > 1.4f
                               || b.Time - t.LastFeintBitTime < FeintOpening * 0.6f
                               || b.Time - t.LastShovedTime < ShoveOpening * 0.6f;
                bool breather = b.Time < s.BreatherUntil;
                if (opening && b.Rand() < dt * (1f + 12f * s.Skill) * (breather ? 0.4f : 1f))
                {
                    StartWindup(b, s, t, Intent.Blow);
                    break;
                }
                if (s.ReadyDelay > 0f) break;
                // Getting his breath back, waiting to be relieved, or hurt and standing in a line with shields beside
                // him: he keeps his guard up, and only a clear opening tempts him.
                if (breather || (s.HpFrac < HurtInLine && InLine(b, s))) { s.ReadyDelay = 0.25f; break; }
                StartWindup(b, s, t, ChooseIntent(b, s, t));
                break;
            }
            case CombatPhase.Windup:
            {
                var t = s.StrikeTarget;
                if (t == null || !t.Active || s.Fleeing)
                {
                    Enter(s, CombatPhase.Ready, 0f);
                    s.StrikeTarget = null;
                    s.ReadyDelay = 0.15f;
                    break;
                }
                if (s.Feint && s.PhaseTime >= FeintPullback * s.PhaseLength)
                {
                    PullFeint(b, s, t);
                    break;
                }
                if (s.PhaseTime >= s.PhaseLength)
                {
                    s.StrikeTarget = null;
                    if (s.Blow == BlowKind.Bash)
                    {
                        Enter(s, CombatPhase.Recover, 0.3f * Tempo(s));
                        ResolveBash(b, s, t);
                    }
                    else
                    {
                        Enter(s, CombatPhase.Recover, s.Weapon.Recover * Tempo(s) * Kind(s.Weapon, KindStat.Recover, s.Blow) * (s.Probe ? 0.5f : 1f));
                        ResolveStrike(b, s, t, 0f, 1f);
                    }
                }
                break;
            }
            case CombatPhase.Recover:
                if (s.PhaseTime >= s.PhaseLength)
                {
                    Enter(s, CombatPhase.Ready, 0f);
                    s.ReadyDelay = NextDelay(b, s);
                }
                break;
            case CombatPhase.Stagger:
                if (s.PhaseTime >= s.PhaseLength)
                {
                    Enter(s, CombatPhase.Ready, 0f);
                    s.ReadyDelay = NextDelay(b, s) * 0.5f;
                }
                break;
            case CombatPhase.Down:
                if (s.PhaseTime >= s.PhaseLength) Enter(s, CombatPhase.Stagger, 0.45f);   // getting up
                break;
        }
    }

    public static bool CanStrike(Battle b, Soldier s, Soldier t)
    {
        float d = s.Pos.DistanceTo(t.Pos);
        if (d > s.Weapon.Reach) return false;
        if (Mathf.Abs(MathUtil.RelativeAngle(s.Facing, t.Pos - s.Pos)) > 0.6f) return false;
        if (s.Stamina < 2f) return false;
        if (!s.Weapon.ReachesPastFriends && d > 1.0f && b.LineBlocked(s, t)) return false;
        return true;
    }

    /// <summary>
    /// What he does with the initiative. In the open a man goes for it; in a line, with shields either side of him
    /// and nothing to gain by exposing himself, he mostly probes and feints and punches with his shield, and saves
    /// the real blow for the opening those make. The better he is the more he feints; a novice hardly ever does.
    /// </summary>
    static Intent ChooseIntent(Battle b, Soldier s, Soldier t)
    {
        bool line = InLine(b, s);
        float sf = s.StaminaFrac;
        float blow = line ? 0.07f : 0.55f;
        float probe = line ? 0.5f : 0.22f;
        float feint = 0.7f * s.Skill * s.Skill * (0.4f + 0.6f * sf);
        float bash = CanBash(s, t) ? (line ? 0.22f : 0.12f) * (0.4f + 0.6f * s.Shield.Weight) * (0.6f + 0.4f * s.Strength) * sf : 0f;
        float r = b.Rand() * (blow + probe + feint + bash);
        if ((r -= blow) < 0f) return Intent.Blow;
        if ((r -= probe) < 0f) return Intent.Probe;
        if ((r -= feint) < 0f) return Intent.Feint;
        return Intent.Bash;
    }

    static bool CanBash(Soldier s, Soldier t) =>
        s.Shield != null && s.Pos.DistanceTo(t.Pos) <= BashReach
        && Mathf.Abs(MathUtil.RelativeAngle(s.Facing, t.Pos - s.Pos)) < 0.5f;

    static void StartWindup(Battle b, Soldier s, Soldier t, Intent intent)
    {
        bool fast = s.Riposte || s.FollowUp;
        if (!s.FollowUp) s.Combo = 0;
        var kind = intent == Intent.Bash ? BlowKind.Bash : ChooseKind(b, s, t, s.FollowUp);
        float len = intent == Intent.Bash ? 0.34f * Tempo(s) : s.Weapon.Windup * Tempo(s) * Kind(s.Weapon, KindStat.Windup, kind);
        if (intent == Intent.Probe) len *= ProbeWindup;
        if (fast) len *= FastWindup;
        if (b.Time < s.ChargeBonusUntil) len *= 0.75f;
        if (s.Pos.DistanceTo(t.Pos) < s.Weapon.MinRange) len *= 1.3f;
        Enter(s, CombatPhase.Windup, len);
        s.StrikeTarget = t;
        s.Riposte = s.FollowUp = false;
        s.Blow = kind;
        s.Feint = intent == Intent.Feint;
        s.Probe = intent == Intent.Probe;
        s.AttackStyle = kind == BlowKind.Thrust ? 1 : 0;
        float cost = intent switch
        {
            Intent.Blow => s.Weapon.StaminaCost * Kind(s.Weapon, KindStat.Cost, kind),
            Intent.Probe => 0.3f * s.Weapon.StaminaCost,
            Intent.Feint => 0.35f * s.Weapon.StaminaCost,
            _ => 4.5f,
        };
        s.Stamina = Math.Max(0f, s.Stamina - cost * ArmourLoad(s) * Exertion);
        if (intent is Intent.Blow or Intent.Probe) s.StrikesMade++;
        s.LastActionTime = b.Time;
    }

    // ---------------------------------------------------------------- the line of a blow

    /// <summary>
    /// Which line he brings the blow in on. Each weapon has its habits (a spear thrusts, an axe chops down or
    /// across, a gladius stabs and cuts). A man with some skill also reads what is open: the side the other man's
    /// shield isn't on, under a guard that has gone high, away from where the last feint or blow drew it.
    /// A follow-up comes in on a different line from the one before it.
    /// </summary>
    static BlowKind ChooseKind(Battle b, Soldier s, Soldier t, bool followUp)
    {
        float rel = MathUtil.RelativeAngle(t.Facing, s.Pos - t.Pos);
        bool guardFresh = b.Time - t.GuardLineTime < GuardMemory;
        float total = 0f;
        Span<float> w = stackalloc float[5];
        for (int k = 0; k < 5; k++)
        {
            var kind = (BlowKind)k;
            float habit = Habit(s.Weapon, kind);
            if (habit <= 0f || (followUp && kind == s.Blow)) { w[k] = 0f; continue; }
            float line = rel + KindShift(kind);
            float shield = ShieldChance(t, line) * ShieldVs(b, kind, t);
            float parry = ParryChance(t, line) * Kind(s.Weapon, KindStat.Parry, kind);
            float open = (1f - shield) * (1f - parry) * Kind(s.Weapon, KindStat.Damage, kind) / Kind(s.Weapon, KindStat.Windup, kind);   // a slow blow gives him time
            if (guardFresh) open /= GuardFactor(b, t, kind);
            // A novice throws what his arm knows; a skilled man throws what is open.
            w[k] = habit * Mathf.Pow(Mathf.Max(0.02f, open), 1.2f * s.Skill);
            total += w[k];
        }
        if (total <= 0f) return s.Weapon.Style == WeaponStyle.Spear ? BlowKind.Thrust : BlowKind.Cut;
        float r = b.Rand() * total;
        for (int k = 0; k < 5; k++)
            if ((r -= w[k]) < 0f && w[k] > 0f) return (BlowKind)k;
        return BlowKind.Cut;
    }

    /// <summary>How often each weapon's user reaches for each line.</summary>
    static float Habit(WeaponDef w, BlowKind k)
    {
        switch (w.Style)
        {
            case WeaponStyle.Spear:
                return k switch { BlowKind.Thrust => 0.78f, BlowKind.Overhead => 0.14f, BlowKind.Low => 0.08f, _ => 0f };
            case WeaponStyle.Axe:
                return k switch { BlowKind.Overhead => 0.4f, BlowKind.Cut => 0.33f, BlowKind.Backhand => 0.17f, BlowKind.Low => 0.1f, _ => 0f };
            default:
                bool shortSword = w.Reach < 1.45f;   // a gladius is made for the point; a longsword for the edge
                return shortSword
                    ? k switch { BlowKind.Thrust => 0.45f, BlowKind.Cut => 0.24f, BlowKind.Backhand => 0.1f, BlowKind.Overhead => 0.08f, BlowKind.Low => 0.13f, _ => 0f }
                    : k switch { BlowKind.Cut => 0.34f, BlowKind.Overhead => 0.24f, BlowKind.Backhand => 0.2f, BlowKind.Thrust => 0.09f, BlowKind.Low => 0.13f, _ => 0f };
        }
    }

    /// <summary>
    /// Where the blow arrives, relative to where he stands, as the defender sees it (positive = his shield side).
    /// A forehand cut comes round from the attacker's right onto the defender's left, into the shield; a backhand
    /// comes across onto his weapon side, where the shield covers less.
    /// </summary>
    static float KindShift(BlowKind k) => k switch { BlowKind.Cut => 0.3f, BlowKind.Backhand => -0.35f, _ => 0f };

    enum KindStat { Windup, Recover, Damage, Cost, Parry, Pierce }

    // What the line does to a blow, before it is evened out against the weapon's habits (see Kind): how long it
    // takes to wind up and to recover from, how hard it hits, what it costs him, how easily it is parried, and how
    // much more of the armour it gets through (a point finds the gaps in mail).
    static float RawKind(KindStat stat, BlowKind k) => stat switch
    {
        KindStat.Windup => k switch { BlowKind.Overhead => 1.2f, BlowKind.Backhand => 0.9f, BlowKind.Thrust => 0.9f, _ => 1f },
        KindStat.Recover => k switch { BlowKind.Overhead => 1.1f, BlowKind.Low => 1.1f, BlowKind.Thrust => 0.9f, _ => 1f },
        KindStat.Damage => k switch { BlowKind.Overhead => 1.15f, BlowKind.Backhand => 0.85f, BlowKind.Thrust => 0.95f, BlowKind.Low => 0.9f, _ => 1f },
        KindStat.Cost => k switch { BlowKind.Overhead => 1.25f, BlowKind.Backhand => 0.9f, BlowKind.Thrust => 0.85f, _ => 1f },
        KindStat.Parry => k switch { BlowKind.Overhead => 0.9f, BlowKind.Backhand => 1.05f, BlowKind.Thrust => 0.8f, BlowKind.Low => 0.75f, _ => 1f },
        _ => k == BlowKind.Thrust ? 0.1f : 0f,
    };

    /// <summary>
    /// How a blow on this line differs from the weapon's usual blow. A weapon's own numbers (Windup, Recover, Damage,
    /// StaminaCost, and how well it is parried) are for its blows taken together, so each line is scaled against the
    /// weapon's habits: a spear, which nearly always thrusts, isn't made quicker than its numbers by thrusting, but
    /// its rare blow from above is slow and heavy for a spear.
    /// </summary>
    static float Kind(WeaponDef w, KindStat stat, BlowKind k)
    {
        float sum = 0f, total = 0f;
        for (int i = 0; i < 5; i++)
        {
            float h = Habit(w, (BlowKind)i);
            sum += h * RawKind(stat, (BlowKind)i);
            total += h;
        }
        if (stat == KindStat.Pierce) return RawKind(stat, k) - sum / total;   // added to the weapon's own, not scaled
        return RawKind(stat, k) * total / sum;
    }

    /// <summary>
    /// How well the shield meets a blow on this line. It has to be lifted to take a heavy blow from above, and a
    /// blow at the legs goes under it unless it is a long one; worse when his guard has just gone high.
    /// </summary>
    static float ShieldVs(Battle b, BlowKind k, Soldier d)
    {
        if (k == BlowKind.Overhead) return 0.95f;
        if (k != BlowKind.Low || d.Shield == null) return 1f;
        float low = d.Shield.Shape switch { ShieldShape.Scutum => 0.92f, ShieldShape.Oval => 0.8f, _ => 0.68f };
        bool high = (b.Time - d.GuardLineTime < GuardMemory && d.GuardLine == BlowKind.Overhead)
                    || (d.Phase == CombatPhase.Windup && d.Blow == BlowKind.Overhead);
        return high ? low * 0.7f : low;
    }

    static bool Opposite(BlowKind a, BlowKind b) =>
        (a == BlowKind.Overhead && b == BlowKind.Low) || (a == BlowKind.Low && b == BlowKind.Overhead)
        || (a == BlowKind.Cut && b == BlowKind.Backhand) || (a == BlowKind.Backhand && b == BlowKind.Cut);

    /// <summary>
    /// His guard is still where the last blow he stopped, or the feint he bit on, took it: a blow on the same line
    /// finds it there, one on the other side finds it gone. Much more so after a feint he fell for.
    /// </summary>
    static float GuardFactor(Battle b, Soldier d, BlowKind k)
    {
        if (b.Time - d.GuardLineTime >= GuardMemory) return 1f;
        bool bit = d.LastFeintBitTime == d.GuardLineTime && b.Time - d.LastFeintBitTime < FeintOpening;
        if (k == d.GuardLine) return bit ? 0.95f : 1.03f;
        if (Opposite(k, d.GuardLine)) return bit ? 0.62f : 0.9f;
        return bit ? 0.78f : 0.96f;
    }

    // ---------------------------------------------------------------- feints and shoves

    /// <summary>
    /// The feint is pulled back before it lands. If the other man was watching him and took it for real, his guard
    /// goes to it, and the feinter goes straight in on another line. A skilled man reads a feint, and one he sees
    /// for what it is may catch the feinter with his arm still coming back.
    /// </summary>
    static void PullFeint(Battle b, Soldier a, Soldier d)
    {
        a.StrikeTarget = null;
        Enter(a, CombatPhase.Ready, 0f);
        a.ReadyDelay = NextDelay(b, a) * 0.6f;
        float rel = Mathf.Abs(MathUtil.RelativeAngle(d.Facing, a.Pos - d.Pos));
        if (d.Phase != CombatPhase.Ready || rel > 1.4f || d.Fleeing) return;   // busy with his own blow, or never saw it
        float bite = 0.6f * (0.5f + 0.7f * a.Skill) * (1.2f - 0.8f * d.Skill)
                     * Mathf.Clamp(1f + 0.8f * (a.Skill - d.Skill), 0.4f, 1.6f)
                     * (d.Target == a ? 1f : 0.55f)
                     * (1.3f - 0.3f * d.StaminaFrac);
        if (b.Rand() < bite)
        {
            d.LastFeintBitTime = d.GuardLineTime = b.Time;
            d.GuardLine = a.Blow;
            d.Stamina = Math.Max(0f, d.Stamina - 1f * Exertion);
            a.FollowUp = true;
        }
        else if (b.Rand() < 0.3f * d.Skill) d.Riposte = true;
    }

    /// <summary>
    /// A punch with the shield boss, shield against shield. It shoves him back: how far depends on the weight
    /// and strength behind it against his, how tired each is, and whether the man behind him holds him up.
    /// A light or blown man can be knocked off his feet for a moment, a man winding up has his blow spoiled,
    /// and either way his shield is knocked off line for the stab that follows.
    /// </summary>
    static void ResolveBash(Battle b, Soldier a, Soldier d)
    {
        a.LastStrikeTime = b.Time;
        a.LastActionTime = b.Time;
        if (!d.Active) return;
        var toD = d.Pos - a.Pos;
        float dist = toD.Length();
        if (dist > BashReach + 0.25f || dist < 1e-3f || Mathf.Abs(MathUtil.RelativeAngle(a.Facing, toD)) > 1.0f) return;   // he stepped out of it
        d.LastActionTime = b.Time;
        var dir = toD / dist;
        float rel = Mathf.Abs(MathUtil.RelativeAngle(d.Facing, -toD));
        // A man who sees it coming may step off it, if he's not hemmed in.
        float slip = rel < 1.4f && d.Phase == CombatPhase.Ready && d.Target == a
            ? (0.08f + 0.22f * d.Skill) * (0.6f + 0.4f * d.Agility) * d.StaminaFrac : 0f;
        if (b.Rand() < slip)
        {
            a.Vel += dir * 0.4f;
            return;
        }
        float power = a.Mass * (0.7f + 0.3f * a.Strength) * (0.6f + 0.4f * a.StaminaFrac);
        float brace = d.Mass * (0.75f + 0.25f * d.Strength) * (0.55f + 0.45f * d.StaminaFrac)
                      * (rel < 1.0f ? 1f : 0.65f) * (d.Phase == CombatPhase.Ready ? 1f : 0.8f);
        brace += 0.3f * b.AlliesBehind(d, dir);   // the rank behind holds him up
        float ratio = power / brace * Mathf.Exp(0.25f * MathUtil.Gaussian(b.Rng, 0f, 1f));
        d.Vel += dir * Mathf.Clamp(1.5f * ratio, 0.4f, 2.6f);
        d.LastShovedTime = b.Time;
        d.Stamina = Math.Max(0f, d.Stamina - 2.5f * ratio * Exertion);
        a.Vel += dir * 0.5f;   // he goes in behind it
        if (ratio > 1.35f && b.Rand() < ratio - 1.25f) Stagger(d, 0.35f + 0.25f * Mathf.Min(1f, ratio - 1.35f));
        else if (d.Phase == CombatPhase.Windup && d.Blow != BlowKind.Bash && b.Rand() < 0.5f)
        {
            Enter(d, CombatPhase.Ready, 0f);   // his blow is spoiled
            d.StrikeTarget = null;
            d.ReadyDelay = 0.2f;
        }
        // The point of the punch is the stab that comes after it.
        if (b.Rand() < (0.35f + 0.5f * a.Skill) * (InLine(b, a) ? 0.4f : 1f)) a.FollowUp = true;
    }

    // ---------------------------------------------------------------- the blow

    /// <summary>The blow lands, is turned, or misses.</summary>
    public static void ResolveStrike(Battle b, Soldier a, Soldier d, float qualityBonus, float damageMult)
    {
        a.LastStrikeTime = b.Time;
        a.LastActionTime = b.Time;
        if (!d.Active) return;
        d.LastActionTime = b.Time;

        var toA = a.Pos - d.Pos;
        float dist = toA.Length();
        if (dist > a.Weapon.Reach + 0.25f || Mathf.Abs(MathUtil.RelativeAngle(a.Facing, -toA)) > 1.1f)
        {
            b.Emit(EventKind.Miss, a, d, 0f);
            return;
        }

        var kind = a.Blow;
        float rel = MathUtil.RelativeAngle(d.Facing, toA);   // where the blow comes from, as the defender sees it
        float tired = 1f - a.StaminaFrac;
        float quality = Mathf.Clamp(0.3f + 0.45f * a.Skill + (b.Rand() - 0.5f) * 0.6f + qualityBonus - (a.Probe ? 0.2f : 0f) - 0.2f * tired * tired, 0.05f, 1f);
        if (dist < a.Weapon.MinRange) quality *= 0.6f;
        if (b.Time < a.ChargeBonusUntil) { quality = Math.Min(1f, quality + 0.1f); damageMult *= 1.25f; }

        float line = rel + KindShift(kind);
        float shield = ShieldChance(d, line) * ShieldVs(b, kind, d);
        float parry = ParryChance(d, line) * Kind(a.Weapon, KindStat.Parry, kind);
        float defense = DefenseChance(b, d, a, rel, quality, shield, parry);
        if (a.Probe) defense = 1f - (1f - defense) * ProbeGetsThrough;   // he wasn't really trying to get through
        // In a close line the shields of the men either side overlap his and take blows aimed at him from the front.
        // Not from the flank or rear: that is what makes a line so hard to break head-on and so easy to roll up.
        if (Mathf.Abs(rel) < 1.0f)
        {
            int mates = WallMates(b, d);
            if (mates > 0) defense = 1f - (1f - defense) * (1f - WallCover * mates);
        }
        if (b.Rand() < defense)
        {
            Blocked(b, a, d, toA, shield, parry);
            return;
        }
        Hit(b, a, d, toA, rel, quality, damageMult);
    }

    /// <summary>Friends standing close beside him, facing the same way with their shields up (0, 1 or 2).</summary>
    public static int WallMates(Battle b, Soldier d)
    {
        if (d.Fleeing) return 0;
        if (d.WallMatesAt == b.Time) return d.WallMatesCount;   // he is asked several times a step: once is enough
        d.WallMatesAt = b.Time;
        return d.WallMatesCount = CountWallMates(b, d);
    }

    static int CountWallMates(Battle b, Soldier d)
    {
        var f = MathUtil.Dir(d.Facing);
        var r = MathUtil.Right(f);
        b.Grid.Query(d.Pos, 1.5f, b.QueryB);
        bool left = false, right = false;
        foreach (var o in b.QueryB)
        {
            if (o == d || o.Team != d.Team || !o.Active || o.Fleeing || o.Shield == null) continue;
            if (o.Phase is CombatPhase.Stagger or CombatPhase.Down) continue;
            var rel = o.Pos - d.Pos;
            float along = rel.Dot(f), side = rel.Dot(r);
            if (Mathf.Abs(along) > 0.7f || Mathf.Abs(side) < 0.45f || Mathf.Abs(side) > 1.45f) continue;
            if (Mathf.Abs(MathUtil.AngleDiff(o.Facing, d.Facing)) > 0.9f) continue;
            if (side > 0f) right = true; else left = true;
        }
        return (left ? 1 : 0) + (right ? 1 : 0);
    }

    static float ShieldChance(Soldier d, float rel)
    {
        var sh = d.Shield;
        if (sh == null) return 0f;
        float abs = Mathf.Abs(rel);
        float cover = rel >= 0f ? sh.CoverLeft : sh.CoverRight;
        if (abs >= cover) return 0f;
        return sh.Block * (1f - 0.6f * Mathf.SmoothStep(cover * 0.5f, cover, abs));
    }

    static float ParryChance(Soldier d, float rel) =>
        Mathf.Abs(rel) < 1.0f ? d.Weapon.Parry * (0.4f + 1.2f * d.Skill) : 0f;

    static float DefenseChance(Battle b, Soldier d, Soldier a, float rel, float quality, float shield, float parry)
    {
        if (d.Phase is CombatPhase.Stagger or CombatPhase.Down) return 0f;
        float abs = Mathf.Abs(rel);
        // Can he see it coming? Nothing from behind; a little from the corner of his eye.
        float awareness = abs < 1.4f ? 1f : abs < 2.0f ? 0.35f : 0f;
        if (d.Fleeing) awareness *= 0.3f;
        if (awareness <= 0f) return 0f;

        float def = 1f - (1f - shield) * (1f - parry);
        // Committed to a blow of his own his guard is half gone; a probe or a feint commits him much less.
        def *= d.Phase switch
        {
            CombatPhase.Windup => d.Probe || d.Feint ? 0.85f : 0.6f,
            CombatPhase.Recover => d.Probe ? 0.75f : d.Blow == BlowKind.Bash ? 0.7f : 0.45f,
            _ => 1f,
        };
        def *= GuardFactor(b, d, a.Blow);
        if (b.Time - d.LastShovedTime < ShoveOpening) def *= 0.72f;   // his shield knocked off line
        def *= Mathf.Clamp(1f + SkillEdge * (d.Skill - a.Skill), 0.45f, 1.5f);
        def *= 0.5f + 0.5f * d.StaminaFrac;   // a blown man can barely hold his shield up
        if (d.ThreatCount > 1) def /= 1f + 0.3f * (d.ThreatCount - 1);   // can't watch two blades at once
        if (d.Target != a) def *= 0.8f;                                  // not the man he's watching
        def *= 1.15f - 0.45f * quality;
        def *= awareness;
        return Mathf.Clamp(def, 0f, 0.95f);
    }

    static void Blocked(Battle b, Soldier a, Soldier d, Vector2 toA, float shield, float parry)
    {
        bool onShield = shield > 0f && b.Rand() * (shield + parry) < shield;
        float heavy = (a.Weapon.Style == WeaponStyle.Axe ? 1.6f : 1f) * (a.Blow == BlowKind.Overhead ? 1.3f : 1f);
        float power = a.Probe ? 0.35f : 1f;
        d.Stamina -= ((onShield ? 1.2f : 1.8f) + 1.5f * a.Strength * heavy) * power * Exertion;
        a.Stamina = Math.Max(0f, a.Stamina - 0.5f * power * Exertion);
        d.Blocks++;
        d.LastBlockTime = b.Time;
        d.LastBlockWasShield = onShield;
        d.GuardLine = a.Blow;
        d.GuardLineTime = b.Time;
        var away = toA.LengthSquared() > 1e-6f ? -toA.Normalized() : Vector2.Zero;
        d.Vel += away * 0.6f * power * a.Strength / d.Mass;

        // A heavy blow can beat down the guard of a man too tired to hold it up; with nothing left he can't at all.
        bool beaten = !a.Probe && heavy > 1.2f && b.Rand() < 0.35f * (1f - d.StaminaFrac) * (1f - d.StaminaFrac) * a.Strength / d.Strength;
        if (d.Stamina <= 0f || beaten)
        {
            d.Stamina = Math.Max(0f, d.Stamina);
            Stagger(d, beaten ? 0.5f : 0.7f);
            b.Emit(EventKind.GuardBreak, a, d, 0f);
            return;
        }
        // A clean parry, or a good man behind his shield, opens the attacker up for a quick reply; less often in a
        // line, where a man has no need to go looking for it.
        bool dLine = InLine(b, d), aLine = InLine(b, a);
        if (d.Phase == CombatPhase.Ready && b.Rand() < (onShield ? 0.4f * d.Skill : 0.6f) * (a.Probe ? 0.3f : 1f) * (dLine ? 0.5f : 1f)) d.Riposte = true;
        // A skilled man doesn't stop at one: the blow is caught, and he goes straight in again on another line
        // while the guard is still there, once or twice, then breaks off.
        else if (!a.Probe && a.Phase == CombatPhase.Recover && a.Combo < (a.Skill > 0.65f ? 2 : 1)
                 && b.Rand() < 0.6f * Mathf.Max(0f, a.Skill - 0.25f) * a.StaminaFrac * (aLine ? 0.3f : 1f))
        {
            a.FollowUp = true;
            a.Combo++;
            a.PhaseLength *= 0.55f;
        }
        b.Emit(onShield ? EventKind.Block : EventKind.Parry, a, d, 0f);
    }

    // Where a blow on each line tends to land: head, torso, arm, leg.
    static readonly float[][] LocationOdds =
    {
        new[] { 0.12f, 0.43f, 0.28f, 0.17f },   // cut
        new[] { 0.3f, 0.3f, 0.32f, 0.08f },     // overhead: head and shoulders
        new[] { 0.1f, 0.42f, 0.3f, 0.18f },     // backhand
        new[] { 0.07f, 0.63f, 0.18f, 0.12f },   // thrust: the body
        new[] { 0f, 0.12f, 0.06f, 0.82f },      // low: the legs
        new[] { 0.1f, 0.6f, 0.2f, 0.1f },       // bash (not used)
    };

    static void Hit(Battle b, Soldier a, Soldier d, Vector2 toA, float rel, float quality, float damageMult)
    {
        var kind = a.Blow;
        var odds = LocationOdds[(int)kind];
        // Skilled men find the gaps: more blows to the head and body, fewer to the limbs.
        float head = odds[0] + (kind == BlowKind.Low ? 0f : 0.06f * a.Skill);
        float torso = odds[1] + 0.04f * a.Skill;
        float arm = Math.Max(0.02f, odds[2] - 0.05f * a.Skill);
        float r = b.Rand() * (head + torso + arm + Math.Max(0.02f, odds[3] - 0.05f * a.Skill));
        HitLocation loc;
        float locMult;
        if (r < head) { loc = HitLocation.Head; locMult = 1.9f; }
        else if (r < head + torso) { loc = HitLocation.Torso; locMult = 1f; }
        else if (r < head + torso + arm) { loc = HitLocation.Arm; locMult = 0.55f; }
        else { loc = HitLocation.Leg; locMult = 0.7f; }

        float pierce = Mathf.Clamp(a.Weapon.ArmorPierce + Kind(a.Weapon, KindStat.Pierce, kind), 0f, 1f);   // a point finds the gaps in mail
        float deflect = d.Armor.Deflect * (1.2f - quality) * (1f - pierce * 0.6f);
        if (loc == HitLocation.Head && d.Armor.Helmet) deflect += 0.15f;
        bool glance = b.Rand() < deflect;

        float dmg = Lethality * a.Weapon.Damage * (0.65f + 0.35f * a.Strength) * (0.55f + 0.9f * quality) * locMult * damageMult * Kind(a.Weapon, KindStat.Damage, kind);
        dmg *= 0.8f + 0.2f * a.StaminaFrac;   // a tired arm hits softer
        if (a.Probe) dmg *= ProbeDamage;
        dmg *= 1f - d.Armor.Protection * (1f - pierce);
        if (Mathf.Abs(rel) > 1.6f) dmg *= 1.15f;   // from behind: no chance to roll with it
        if (glance) dmg *= 0.2f;

        d.Hp -= dmg;
        d.LastHitTime = b.Time;
        a.HitsLanded++;
        a.DamageDealt += dmg;
        if (!glance) d.Wounds++;
        var away = toA.LengthSquared() > 1e-6f ? -toA.Normalized() : Vector2.Zero;

        if (d.Hp <= 0f)
        {
            Kill(b, d, a);
            b.Emit(EventKind.Kill, a, d, dmg, loc);
            return;
        }
        d.Vel += away * (glance ? 0.3f : 0.9f) * a.Strength / d.Mass;
        float staggerChance = dmg / 45f * (1.3f - 0.3f * d.Strength) * (loc == HitLocation.Leg ? 1.3f : 1f);
        if (!glance && !a.Probe && b.Rand() < staggerChance) Stagger(d, 0.35f + dmg / 100f);   // a jab doesn't knock a man off his feet
        b.Emit(glance ? EventKind.Glance : EventKind.Hit, a, d, dmg, loc);
    }

    public static void Kill(Battle b, Soldier d, Soldier killer)
    {
        d.Hp = 0f;
        d.Dead = true;
        d.DeathTime = b.Time;
        d.KilledBy = killer;
        var dir = killer != null ? d.Pos - killer.Pos : -MathUtil.Dir(d.Facing);
        d.DeathDir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : -MathUtil.Dir(d.Facing);
        d.Vel = Vector2.Zero;
        d.Target = null;
        d.StrikeTarget = null;
        d.Charging = false;
        d.Unit.OnSoldierLost(b, d);
        if (killer != null)
        {
            killer.Kills++;
            killer.Unit.OnKill(b);
        }
    }

    /// <summary>Two enemies have collided. If one was charging hard, the other may be bowled over.</summary>
    public static void CheckImpact(Battle b, Soldier x, Soldier y, Vector2 n)
    {
        float closing = (x.Vel - y.Vel).Dot(n);   // positive: x is running into y
        if (closing < 2.2f)
        {
            // Too slow to bowl anyone over: the charge just runs out against the enemy.
            x.Charging = y.Charging = false;
            return;
        }
        // Judge both men as they were at the moment they met, so that when two chargers collide
        // neither is favoured by which of them the collision loop happened to reach first.
        bool xCharging = x.Charging, yCharging = y.Charging;
        bool xHits = xCharging && x.Phase is not (CombatPhase.Stagger or CombatPhase.Down);
        bool yHits = yCharging && y.Phase is not (CombatPhase.Stagger or CombatPhase.Down);
        float rx = xHits ? ImpactRatio(b, x, y, closing, n) : 0f;
        float ry = yHits ? ImpactRatio(b, y, x, closing, -n) : 0f;
        if (xCharging) Impact(b, x, y, closing, n, xHits, rx, !yCharging);
        if (yCharging && y.Active && x.Active) Impact(b, y, x, closing, -n, yHits, ry, !xCharging);
    }

    /// <summary>How hard the charger hits against how well the other man is set to take it; above 1 he goes over.</summary>
    static float ImpactRatio(Battle b, Soldier c, Soldier v, float speed, Vector2 n)
    {
        float power = speed * c.Mass * (c.Unit.Formation == FormationType.Wedge ? 1.3f : 1f);
        float rel = Mathf.Abs(MathUtil.RelativeAngle(v.Facing, c.Pos - v.Pos));
        float brace = v.Mass
                      * (rel < 1.0f ? (v.Shield != null ? 1.5f : 1.1f) : 0.6f)
                      * (v.Phase == CombatPhase.Ready ? 1f : 0.75f)
                      * (0.7f + 0.3f * v.StaminaFrac);
        brace += 0.35f * b.AlliesBehind(v, n);   // ranks behind hold him up: deep formations take charges better
        // A spearman standing ready to meet it sets his spear, and the charger runs onto the point, not into him.
        if (v.Weapon.Braces && !v.Charging && rel < 0.8f && v.Phase is CombatPhase.Ready or CombatPhase.Windup) brace *= 1.4f;
        // Nothing is certain in a crush: how well each man's feet happen to be set.
        float footing = Mathf.Exp(0.4f * MathUtil.Gaussian(b.Rng, 0f, 1f));
        return power / (brace * 3.2f) * footing;
    }

    static void Impact(Battle b, Soldier c, Soldier v, float speed, Vector2 n, bool hits, float ratio, bool victimSet)
    {
        c.Charging = false;
        c.ChargeBonusUntil = b.Time + 1.5f;
        c.LastImpactTime = v.LastImpactTime = b.Time;
        if (!hits) return;

        float rel = Mathf.Abs(MathUtil.RelativeAngle(v.Facing, c.Pos - v.Pos));
        bool wasDown = v.Phase == CombatPhase.Down;
        if (wasDown) ratio = Math.Min(ratio, 0.99f);   // he's already on the ground: nothing more to knock over
        else if (ratio > 1.6f) { KnockDown(v, 1.4f); v.Hp -= 5f; }
        else if (ratio > 1.0f) Stagger(v, 0.6f);
        if (!wasDown) v.Vel += n * speed * 0.5f * c.Mass / v.Mass;
        c.Vel *= 0.3f;
        v.Unit.Shock(ratio > 1f ? 1.5f : 0.5f);
        b.Emit(EventKind.Impact, c, v, ratio);

        if (v.Hp <= 0f)
        {
            Kill(b, v, c);
            b.Emit(EventKind.Kill, c, v, 5f, HitLocation.Torso);
            return;
        }
        // A spearman standing to meet the charge sets his spear and lets the man run onto it.
        if (victimSet && v.Weapon.Braces && rel < 0.8f && v.Phase is CombatPhase.Ready or CombatPhase.Windup)
        {
            v.Blow = BlowKind.Thrust;
            v.AttackStyle = 1;
            v.Probe = v.Feint = false;
            ResolveStrike(b, v, c, 0.25f, 1.4f);
        }
    }
}
