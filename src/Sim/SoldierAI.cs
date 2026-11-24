using System;
using Godot;

namespace UnitFighting.Sim;

/// <summary>
/// Each man's own decisions: who to fight, where to stand, which way to face.
/// In formation he keeps near his slot and fights whoever comes within a step of it.
/// Released by an attack order he goes after the enemy himself, and the better he is,
/// the more he looks for men who are busy with someone else, and works round to their open side.
/// </summary>
public static class SoldierAI
{
    const float HoldLeash = 1.4f;
    const float LineLeash = 2.25f;   // how far a man in an attacking line that has closed will go from his place in it
    const float StepInLeash = 3f;    // how far a man held in the line will go to get at someone who outreaches him
    const float GiveUpChase = 8f;    // seconds running after a man without a blow struck either way
    const float StandOff = 20f;      // seconds a rear-rank man waits for a man just out of his reach before looking elsewhere
    const float ChargeTime = 6f;     // longest a man keeps up a charge: from the order to charge, through the run-in, to the crash
    const float RearChargeTime = 2f; // how long the ranks behind keep pressing in after their front rank has hit
    const float FleeSpeed = 0.85f;   // routers are blown, hurt and in each other's way: a fresh pursuer gains on them
    const float BackStepSpeed = 0.55f;   // speed walking straight backwards, as a fraction of forwards
    const float GiveGround = 1f;     // how far he backs off to keep two attackers in front of him
    const float ReliefWalk = 2.5f;   // relieved from the front rank, how long he takes to get back down his file
    const float SwapPass = 1.3f;     // two men changing places in a file pass shoulder to shoulder for this long
    // Stamina per second, times his fitness (see UpdateStamina): between efforts in a fight, and resting (a breather,
    // or out of the fight; scaled up the more blown he is).
    const float ContactRegen = 1f, RestRegen = 1f;
    const float BreatherBelow = 0.3f;   // below this share of his wind a man in a fight starts to step back for breathers
    const float RestedAt = 0.7f;        // a man in a rear rank stays out of the fighting until he has this much of his wind back
    const float LineStep = 0.1f;        // in a line his footwork is a lean and a shuffle, not a step out of the shield wall
    const float Blown = 0.15f, JogSpeed = 2.0f;   // below this share of his wind he can't run, only jog (running is over 2.6 m/s)
    const float ShieldGap = 0.88f;      // closest two men with shields step to each other, centre to centre

    public static float Leash(Battalion u) => u.Released ? LineLeash : u.Order == OrderType.Move ? 0.8f : HoldLeash;

    /// <summary>
    /// A man in a rear rank of a close-order formation backs up the file in front of him: he keeps his place
    /// even when the unit is loosed at the enemy, fights only what comes to him (spears reach past the front man),
    /// and steps up when the man ahead falls. Only the front rank is loosed.
    /// </summary>
    public static bool Supporting(Soldier s)
    {
        var u = s.Unit;
        if (s.Slot < 0 || s.Slot >= u.Slots.Count || u.Slots[s.Slot].Rank == 0) return false;
        return u.Formation is FormationType.Line or FormationType.Deep or FormationType.Thin or FormationType.Column;
    }

    public static void Think(Battle b, Soldier s, float dt)
    {
        UpdateStamina(b, s, dt);
        Combat.TickPhase(b, s, dt);
        if (s.Fleeing) { Flee(b, s, dt); return; }

        var u = s.Unit;
        bool supporting = Supporting(s);
        var slotPos = s.Slot >= 0 ? u.SlotWorld(s.Slot) : s.Pos;
        // A closed attack settles into a fighting line: each front-rank man fights near his place in it, and only one
        // with no enemy in reach of that place is let loose to go and find one, so the ends of a longer line wrap round
        // the flanks. Nobody is loosed while the line is still running in, only once it has hit. When the enemy breaks,
        // the whole front rank is loosed to run them down. A man with nobody to fight looks again every step; one who
        // is fighting, when he next looks for a target.
        bool inLine = u.Released && !supporting && s.Slot >= 0;
        s.RetargetIn -= dt;
        if (inLine && (s.RetargetIn <= 0f || s.Target == null))
            s.Loose = u.AttackTarget is { Routing: true }
                      || ((!s.Charging || u.InMelee) && !b.EnemyWithin(slotPos, s.Team, LineLeash + s.Weapon.Reach + 0.3f));
        bool released = s.Slot < 0 || (inLine && s.Loose);
        float leash = released ? 99f : supporting && u.Released ? HoldLeash : Leash(u);
        // He won't stand in the line and be speared: if the man fighting him outreaches him, he closes with him.
        var foe = s.Target;
        if (!released && foe != null && foe.Target == s && foe.Active && !foe.Fleeing)
        {
            float d = s.Pos.DistanceTo(foe.Pos);
            if (d > s.Weapon.Reach && d <= foe.Weapon.Reach + 0.3f) leash = Mathf.Max(leash, StepInLeash);
        }

        // Relieved from the front rank, he backs down his file to his new place and doesn't look for a fight on the way.
        // Back in his rank, until he has his wind back (or if he is badly hurt) he leaves the fighting to the man in
        // front of him: he fights only a man who comes right up to him.
        bool resting = supporting && Recovering(s);
        if (supporting && b.Time - s.ReliefTime < ReliefWalk) s.Target = null;
        else if (resting)
        {
            if (s.RetargetIn <= 0f || s.Target is { Active: false })
            {
                ChooseTarget(b, s, slotPos, released, leash);
                s.RetargetIn = Mathf.Lerp(0.6f, 0.25f, s.Skill) * b.Range(0.8f, 1.2f);
            }
            if (s.Target is { } e && (e.Target != s || s.Pos.DistanceTo(e.Pos) > s.Weapon.Reach + 0.3f)) s.Target = null;
        }
        else if (s.RetargetIn <= 0f || !StillValid(b, s, slotPos, released, leash))
        {
            ChooseTarget(b, s, slotPos, released, leash);
            s.RetargetIn = Mathf.Lerp(0.6f, 0.25f, s.Skill) * b.Range(0.8f, 1.2f);
        }

        // The ranks behind run in with the charge at their places, and when the front rank hits they carry on into
        // any gap for a moment, onto the nearest enemy, so the charge lands with its weight behind it.
        if (supporting && s.Charging && u.ContactTime >= 0f && b.Time - u.ContactTime > RearChargeTime) s.Charging = false;
        var t = s.Target;
        float distT = t != null ? s.Pos.DistanceTo(t.Pos) : 999f;
        Vector2 desired;
        if (t != null)
        {
            var away = distT > 0.01f ? (s.Pos - t.Pos) / distT : -MathUtil.Dir(s.Facing);
            if (s.Charging)
            {
                desired = t.Pos - away * 2.5f;   // run through him: aim well past, so as not to ease up before the crash
                if (b.Time - s.ChargeStartTime > ChargeTime) s.Charging = false;   // otherwise it ends when he hits someone
            }
            else if (t.Fleeing && released)
            {
                desired = t.Pos + t.Vel * 0.5f;   // run him down: aim where he is going, not a sword's length behind him
            }
            else
            {
                if ((released || inLine) && t.Target != null && t.Target != s && s.Skill > 0.3f && !SwordSideCovered(b, t))
                {
                    // He's busy with someone else: work round to his sword side, where the shield isn't.
                    var tf = MathUtil.Dir(t.Facing);
                    var open = (MathUtil.Right(tf) - tf * 0.6f).Normalized();
                    away = away.Lerp(open, 0.5f * s.Skill).Normalized();
                }
                var engage = t.Pos + away * s.Weapon.IdealRange;
                // In a line his steps are taken square to it, forward and back and along it, so as to keep his place in it.
                if (distT < s.Weapon.Reach + 1.2f) engage += Footwork(b, s, t, released ? -away : MathUtil.Dir(u.SlotFacing(s.Slot)));
                // With a shield on each side a step in stops with the boards a hand's breadth apart: pressed together,
                // neither could get a blade past them (a punch with the shield is another matter, see Combat).
                if (s.Shield != null && t.Shield != null && (engage - t.Pos).LengthSquared() < ShieldGap * ShieldGap)
                    engage = t.Pos + ((engage - t.Pos).LengthSquared() > 1e-4f ? (engage - t.Pos).Normalized() : away) * ShieldGap;
                desired = released ? engage : slotPos + (engage - slotPos).LimitLength(leash);
            }
        }
        else
        {
            desired = slotPos;   // running in at his place in the line
            if (s.Charging && b.Time - s.ChargeStartTime > ChargeTime) s.Charging = false;
        }

        // Two men on him: give ground and turn so that both are in front of him, not one on each side.
        Vector2 threatBisector = Vector2.Zero;
        if (t != null && !s.Charging && s.ThreatCount >= 2 && distT < s.Weapon.Reach + 1.5f)
        {
            var toT = (t.Pos - s.Pos).Normalized();
            var near = b.QueryA;
            b.Grid.Query(s.Pos, 3.5f, near);
            Soldier other = null;
            float worst = 0f;
            foreach (var e in near)
            {
                if (e == t || e.Team == s.Team || !e.Active || e.Fleeing || e.Target != s) continue;
                float ang = Mathf.Abs(MathUtil.AngleDiff(MathUtil.Angle(toT), MathUtil.Angle(e.Pos - s.Pos)));
                if (ang > worst) { worst = ang; other = e; }
            }
            if (other != null && worst > 0.7f)
            {
                var toO = (other.Pos - s.Pos).Normalized();
                threatBisector = (toT + toO).LengthSquared() > 1e-4f ? (toT + toO).Normalized() : toT;
                var back = released ? GiveGround : Mathf.Min(GiveGround, 0.5f);
                desired = s.Pos - threatBisector * back;
                if (!released) desired = slotPos + (desired - slotPos).LimitLength(leash);
            }
        }

        if (resting && !s.Charging) desired = slotPos;   // and keeps his place

        // Changing places in the file: the two men pass shoulder to shoulder, one each side of it.
        if (b.Time - s.ReliefTime < SwapPass && s.Slot >= 0) desired += MathUtil.Right(MathUtil.Dir(u.Facing)) * s.SwapSide * 0.32f;

        float run = u.Type.RunSpeed * (0.9f + 0.1f * s.Agility) * (s.StaminaFrac < 0.2f ? 0.7f : 1f);
        if (s.StaminaFrac < Blown) run = Mathf.Min(run, JogSpeed);   // no wind left to run with
        float toDesired = s.Pos.DistanceTo(desired);
        float maxSpeed;
        if (s.Charging) maxSpeed = run;
        else if (t != null && t.Fleeing && released) maxSpeed = run;               // cutting down a running man means running
        else if (t != null && distT < s.Weapon.Reach + 1.5f) maxSpeed = 1.4f;   // footwork
        else if (released) maxSpeed = toDesired > 4f ? run : 2.2f;
        else if (u.Order != OrderType.Hold) maxSpeed = u.Running || u.RunningIn ? run : u.Type.WalkSpeed * 1.4f + (toDesired > 3f ? 0.8f : 0f);
        else maxSpeed = toDesired > 4f ? u.Type.WalkSpeed * 1.6f : 2.2f;
        maxSpeed *= s.Phase switch
        {
            CombatPhase.Windup => 0.5f,
            CombatPhase.Recover => 0.6f,
            CombatPhase.Stagger => 0.35f,
            CombatPhase.Down => 0f,
            _ => 1f,
        };
        maxSpeed *= 0.55f + 0.45f * s.HpFrac;
        // Nobody backs away as fast as he advances.
        var toward = desired - s.Pos;
        if (toward.LengthSquared() > 1e-4f)
        {
            float back = -toward.Normalized().Dot(MathUtil.Dir(s.Facing));
            if (back > 0f) maxSpeed *= 1f - (1f - BackStepSpeed) * back;
        }
        Steer(s, desired, maxSpeed, dt, b.Time - s.LastShovedTime < 0.35f);

        float want;
        if (t != null && distT < s.Weapon.Reach + 2.5f)
        {
            var toT = (t.Pos - s.Pos).Normalized();
            // Keep his man just inside his striking arc, turned toward the other.
            want = threatBisector != Vector2.Zero ? MathUtil.Angle(toT.Lerp(threatBisector, 0.55f)) : MathUtil.Angle(t.Pos - s.Pos);
        }
        else if (u.FallingBack && b.EnemyWithin(s.Pos, s.Team, 5f)) want = u.SlotFacing(s.Slot);   // stepping back out of a fight, he doesn't turn his back on them
        else if (s.Vel.LengthSquared() > 0.5f) want = MathUtil.Angle(s.Vel);
        else want = u.SlotFacing(s.Slot);
        float rate = 7f * (0.8f + 0.2f * s.Agility);
        rate = s.Phase switch
        {
            CombatPhase.Windup => rate * 0.5f,
            CombatPhase.Stagger => 1.5f,
            CombatPhase.Down => 0f,
            _ => rate,
        };
        // Relieved, he backs down his file still facing the enemy: he doesn't turn his back on the man he was fighting.
        if (t == null && s.Slot >= 0 && b.Time - s.ReliefTime < ReliefWalk) want = u.SlotFacing(s.Slot);
        s.Facing = MathUtil.TurnToward(s.Facing, want, rate * dt);
    }

    /// <summary>Blown or badly hurt: a man who has been relieved, or is waiting in a rear rank, stays out of it until he has his wind back.</summary>
    static bool Recovering(Soldier s) => s.StaminaFrac < RestedAt || s.HpFrac < Battalion.ReliefHp;

    /// <summary>Is a friend of his standing at his sword side, so that there is no way round to it?</summary>
    static bool SwordSideCovered(Battle b, Soldier t)
    {
        var f = MathUtil.Dir(t.Facing);
        var r = MathUtil.Right(f);
        b.Grid.Query(t.Pos + r * 0.9f, 0.9f, b.QueryB);
        foreach (var o in b.QueryB)
        {
            if (o == t || o.Team != t.Team || !o.Active || o.Fleeing) continue;
            var rel = o.Pos - t.Pos;
            float side = rel.Dot(r), along = rel.Dot(f);
            if (side > 0.3f && side < 1.6f && Mathf.Abs(along) < 0.9f) return true;
        }
        return false;
    }

    /// <summary>Walk toward <paramref name="desired"/>. A man just shoved back has to get his feet under him before he can step in again.</summary>
    static void Steer(Soldier s, Vector2 desired, float maxSpeed, float dt, bool shoved = false)
    {
        var to = desired - s.Pos;
        float d = to.Length();
        var want = d > 0.04f ? to / d * Mathf.Min(maxSpeed, d * 2.5f) : Vector2.Zero;
        s.Vel = s.Vel.MoveToward(want, (s.Phase == CombatPhase.Down ? 20f : shoved ? 3f : 9f) * dt);
    }

    /// <summary>
    /// Footwork. A man in a fight doesn't stand on one spot: every second or so he takes a half step in to press his
    /// man, a half step back, or a step aside to find an angle past his shield. Pressed hard he gives ground, and
    /// when he is blown he steps back out of reach to get his breath and comes on again when he has it. Two tired
    /// men facing each other both take the chance, so a long fight goes in bouts. In a shield wall the same choices
    /// are only a lean and a shuffle: a man who stepped out of it would lose the shields either side of him.
    /// Returns his step, as an offset from where he would otherwise stand.
    /// </summary>
    static Vector2 Footwork(Battle b, Soldier s, Soldier t, Vector2 toward)
    {
        if (b.Time >= s.NextStepTime && s.Phase is CombatPhase.Ready or CombatPhase.Recover) PickStep(b, s, t);
        return toward * s.StepOffset.Y + MathUtil.Right(toward) * s.StepOffset.X;
    }

    static void PickStep(Battle b, Soldier s, Soldier t)
    {
        float sf = s.StaminaFrac;
        // In a line he keeps his feet under him, so as not to leave the shields either side of him, and with a man at
        // each shoulder there is nowhere to step aside to.
        bool line = Combat.WallMates(b, s) > 0;
        float k = line ? LineStep : 1f;
        s.NextStepTime = b.Time + b.Range(0.7f, 1.8f) * (1f + 0.5f * (1f - sf));
        bool pressed = s.ThreatCount >= 2 || b.Time - s.LastShovedTime < 0.8f || b.Time - s.LastHitTime < 1f;
        bool theyRest = b.Time < t.BreatherUntil;
        float breathe = sf < BreatherBelow ? (BreatherBelow - sf) / BreatherBelow : 0f;   // none until he is well blown, certain when he has nothing left
        if (theyRest && sf < 0.65f) breathe += 0.5f;          // his man has stepped back to breathe: so does he
        Vector2 step;
        if (!pressed && b.Rand() < breathe * 0.6f)
        {
            s.BreatherUntil = b.Time + b.Range(1.5f, 3f) * (1.5f - sf);
            s.NextStepTime = s.BreatherUntil;
            step = new Vector2(b.Range(-0.15f, 0.15f) * k, -b.Range(0.8f, 1.1f) * k);
        }
        else if (pressed) step = new Vector2(0f, -b.Range(0.25f, 0.5f) * k);   // give ground
        else
        {
            float press = 0.3f + 0.6f * Mathf.Max(0f, sf - t.StaminaFrac) + (theyRest ? 0.5f : 0f);
            const float hold = 0.25f, back = 0.15f;
            float aside = line ? 0f : 0.2f + 0.3f * s.Skill;
            float r = b.Rand() * (press + hold + aside + back);
            if ((r -= press) < 0f) step = new Vector2(0f, b.Range(0.2f, 0.4f) * k);
            else if ((r -= hold) < 0f) step = Vector2.Zero;
            else if ((r -= aside) < 0f)
            {
                // The better he is, the more he works round toward the other man's weapon side, away from his shield
                // (the other man's right is our left).
                float side = b.Rand() < 0.5f + 0.4f * s.Skill ? -1f : 1f;
                step = new Vector2(side * b.Range(0.25f, 0.45f) * k, 0f);
            }
            else step = new Vector2(0f, -b.Range(0.15f, 0.3f) * k);
        }
        if (step.DistanceSquaredTo(s.StepOffset) > 0.01f * k * k)
        {
            s.LastStepTime = b.Time;
            s.Stamina = Math.Max(0f, s.Stamina - 0.4f * Combat.ArmourLoad(s) * Combat.Exertion);
        }
        s.StepOffset = step;
    }

    /// <summary>Not worth running after: too far ahead, or he's blown, or he's been chasing too long without landing a blow.</summary>
    static bool GaveUpChase(Battle b, Soldier s, float dist) =>
        dist > 8f || s.StaminaFrac < 0.2f || b.Time - s.LastActionTime > GiveUpChase;

    /// <summary>
    /// A running man: a man held in the line lets him go as soon as he is out of reach; one who is loose runs him down
    /// until he gives up the chase, even if he is only a step behind and can never quite close it, or is at his heels
    /// but too blown to strike.
    /// </summary>
    static bool LetHimGo(Battle b, Soldier s, float dist, bool released) =>
        released ? GaveUpChase(b, s, dist) : dist > s.Weapon.Reach + 0.5f;

    static bool StillValid(Battle b, Soldier s, Vector2 slotPos, bool released, float leash)
    {
        var t = s.Target;
        if (t == null || !t.Active) return false;
        float dist = s.Pos.DistanceTo(t.Pos);
        if (t.Fleeing && LetHimGo(b, s, dist, released)) return false;
        if (dist > s.Weapon.Reach && !s.Weapon.ReachesPastFriends && b.LineBlocked(s, t)) return false;   // a friend is in the way
        if (released) return dist < 16f;
        return t.Pos.DistanceTo(slotPos) <= leash + s.Weapon.Reach + 0.3f && !BeyondHim(b, s, t, slotPos, leash);   // held to his place, can he get to him?
    }

    /// <summary>
    /// A man in a rear rank can't get within reach of this man from his place, and nothing has come of waiting for
    /// him for a long while: two rear-rank men facing each other through a gap in the front ranks could otherwise
    /// stand a pace out of each other's reach for good. He looks for someone he can get at instead.
    /// </summary>
    static bool BeyondHim(Battle b, Soldier s, Soldier t, Vector2 slotPos, float leash) =>
        t.Pos.DistanceTo(slotPos) > leash + s.Weapon.Reach && b.Time - s.LastActionTime > StandOff && Supporting(s);

    static void ChooseTarget(Battle b, Soldier s, Vector2 slotPos, bool released, float leash)
    {
        var u = s.Unit;
        float radius = released ? 14f : leash + s.Weapon.Reach + 0.3f;
        var center = released ? s.Pos : slotPos;
        var list = b.QueryA;
        b.Grid.Query(center, radius, list);

        Soldier best = null;
        float bestScore = float.MaxValue;
        foreach (var e in list)
        {
            if (e.Team == s.Team || !e.Active) continue;
            float d = s.Pos.DistanceTo(e.Pos);
            if (e.Fleeing && LetHimGo(b, s, d, released)) continue;
            if (!released && BeyondHim(b, s, e, slotPos, leash)) continue;

            float score = d;
            score += 0.6f * Mathf.Abs(MathUtil.RelativeAngle(s.Facing, e.Pos - s.Pos));   // rather what's in front
            if (e.Target == s) score -= 1.2f;                                              // he's coming for me
            else if (e.Target != null) score -= 0.9f * s.Skill;                            // he's busy: an opening
            if (e.Fleeing && released) score -= 0.5f;
            score -= 0.8f * s.Skill * (1f - e.HpFrac);                                    // finish the wounded
            int crowd = e.TargetedBy - (s.Target == e ? 1 : 0);
            score += 0.5f * crowd;                                                         // spread along the line
            if (u.Order == OrderType.Attack && e.Unit == u.AttackTarget) score -= 1f;
            if (!s.Weapon.ReachesPastFriends && d > 1.0f && b.LineBlocked(s, e, 0.5f)) continue;   // won't shove past a friend
            if (e == s.Target) score -= 0.6f;                                              // don't dither
            if (score < bestScore) { bestScore = score; best = e; }
        }
        s.Target = best;
    }

    /// <summary>
    /// Stamina is his wind. Blows, blocks, shoves and steps each cost a good deal of it (in Combat and in his
    /// footwork), and he gets some back between efforts, so in a fight it runs down with every exchange and a man
    /// in heavy armour runs down fastest. Here is the rest: running costs it, more in mail; resting (a breather, or
    /// out of the fight in a rear rank) he gets it back quickest while he is most blown, and never slower than
    /// between efforts.
    /// </summary>
    static void UpdateStamina(Battle b, Soldier s, float dt)
    {
        float speed = s.Vel.Length();
        var t = s.Target;
        // In the fight: he has struck, or been struck at, a moment ago, or his man is within his reach. A man in a
        // rear rank with the enemy a step beyond the man in front of him is resting.
        bool contact = b.Time - s.LastActionTime < 1.5f || (t != null && t.Active && s.Pos.DistanceTo(t.Pos) <= s.Weapon.Reach + 0.3f);
        float fit = 0.5f + 0.5f * s.Endurance;   // a fit man gets his wind back quicker (and has more of it: MaxStamina)
        float d;
        if (speed > 2.6f) d = -3f * (1f + 0.4f * s.Armor.Weight);
        else if (contact && b.Time >= s.BreatherUntil) d = ContactRegen * fit;
        else d = Math.Max(ContactRegen, RestRegen * (0.5f + (1f - s.StaminaFrac))) * fit * (speed > 0.5f ? 0.75f : 1f);
        s.Stamina = Mathf.Clamp(s.Stamina + d * dt, 0f, s.MaxStamina);
    }

    static void Flee(Battle b, Soldier s, float dt)
    {
        s.Target = null;
        s.Charging = false;
        var away = Vector2.Zero;
        var list = b.QueryA;
        b.Grid.Query(s.Pos, 18f, list);
        foreach (var e in list)
        {
            if (e.Team == s.Team || !e.Active || e.Fleeing) continue;
            var d = s.Pos - e.Pos;
            float l2 = Math.Max(0.25f, d.LengthSquared());
            away += d / l2;   // the closer he is, the more he matters
        }
        if (away.LengthSquared() < 1e-6f)
        {
            var enemy = b.TeamCentroid(1 - s.Team);
            away = enemy.HasValue ? s.Pos - enemy.Value : -MathUtil.Dir(s.Unit.Facing);
        }
        var dir = away.Normalized();
        float speed = s.Unit.Type.RunSpeed * (s.StaminaFrac < 0.15f ? 0.65f : FleeSpeed) * (0.55f + 0.45f * s.HpFrac);
        if (s.StaminaFrac < Blown) speed = Mathf.Min(speed, JogSpeed);   // fear or no, a man with no wind left can't run
        if (s.Phase == CombatPhase.Down) speed = 0f;
        Steer(s, s.Pos + dir * 5f, speed, dt);
        var look = s.Vel.LengthSquared() > 0.1f ? s.Vel : dir;
        s.Facing = MathUtil.TurnToward(s.Facing, MathUtil.Angle(look), 8f * dt);

        if (Mathf.Abs(s.Pos.X) > b.FieldHalf || Mathf.Abs(s.Pos.Y) > b.FieldHalf)
        {
            s.Escaped = true;
            s.Vel = Vector2.Zero;
            s.Unit.OnSoldierLost(b, s);
        }
    }
}
