using Godot;

namespace UnitFighting.Sim;

/// <summary>
/// Ready: guard up, looking for an opening. Windup: committed to a blow (weaker guard).
/// Recover: the blow has landed or missed, arm out of position (weakest guard).
/// Stagger: off balance from a heavy hit or a broken guard; cannot defend. Down: knocked flat by a charge.
/// </summary>
public enum CombatPhase { Ready, Windup, Recover, Stagger, Down }

/// <summary>
/// The line a blow comes in on. Cut: a forehand slash from his weapon side. Overhead: down onto the head and
/// shoulders, slow but heavy. Backhand: from his shield side, across. Thrust: straight in with the point.
/// Low: at the legs, under the shield. Bash: a punch or shove with the shield boss.
/// </summary>
public enum BlowKind { Cut, Overhead, Backhand, Thrust, Low, Bash }

public sealed class Soldier
{
    public const float Radius = 0.3f;

    public readonly int Id;
    public readonly Battalion Unit;
    public string Name = "";

    // Who he is. Skill is technique (0..1); the rest are multipliers around 1.
    public float Skill, Strength, Agility, Endurance, Nerve;
    public float BreakPoint;   // unit morale below this and he runs
    public float Mass;

    // Where he is. Prev* are last step's values, for render interpolation.
    public Vector2 Pos, PrevPos, Vel;
    public float Facing, PrevFacing;

    public float Hp, MaxHp = 100f, Stamina, MaxStamina = 100f;
    public bool Dead, Fleeing, Escaped;

    public int Slot = -1;

    // The fight he is in.
    public CombatPhase Phase;
    public float PhaseTime, PhaseLength;
    public Soldier Target;        // the man he is fighting
    public Soldier StrikeTarget;  // the man his current blow is aimed at
    public float RetargetIn, ReadyDelay;
    public bool Riposte;          // just turned a blow; his next one comes fast
    public bool FollowUp;         // his next blow comes straight away: the next of a combination, the stab after a shield punch, going in after a feint
    public int Combo;             // blows he has followed up with since he last started afresh
    public BlowKind GuardLine;    // where his guard last went: the line of the last blow he stopped or the feint he bit on
    public float GuardLineTime = -99f;
    public float BreatherUntil = -99f;   // view: until then he keeps his guard up and starts nothing new: blown and stepped back for a breather, or spent and waiting to be relieved
    public Vector2 StepOffset;    // his current footwork step, relative to the line to his man: X to his right, Y toward him
    public float NextStepTime;
    public float ReliefTime = -99f;      // view: when he last changed places with the man behind or in front of him in his file (line relief)
    public float SwapSide;        // which side of the file he passes the other man on while they change places (+1 his right)
    public float WallMatesAt = -1f;   // when WallMatesCount was last counted (Combat.WallMates)
    public int WallMatesCount;
    public bool Charging;
    public bool Loose;            // in a line that has closed, nobody to fight near his place in it: free to go and find someone
    public float ChargeStartTime, ChargeBonusUntil;
    public float LastActionTime = -99f;
    public int ThreatCount;       // enemies close enough to strike him who are fighting him
    public int TargetedBy;        // enemies who have picked him
    public int AttackStyle;       // 0 cut, 1 thrust (visual variety for swords); kept in step with Blow
    public BlowKind Blow;         // the blow he is winding up / just threw; set when a Windup starts, kept through Recover
    public bool Feint;            // the current Windup is a feint: it will be pulled back before it lands
    public bool Probe;            // view: the current Windup is a probing jab: short, low-commitment, mostly blocked

    // His record.
    public int Kills, HitsLanded, Blocks, Wounds, StrikesMade;
    public float DamageDealt;
    public Soldier KilledBy;
    public float DeathTime = -1f;
    public Vector2 DeathDir;

    // When things last happened to him, in battle time, for the renderer.
    public float LastBlockTime = -99f, LastHitTime = -99f, LastStrikeTime = -99f, LastImpactTime = -99f;
    public bool LastBlockWasShield;
    public float LastShovedTime = -99f;     // he was shoved back by a shield bash (not necessarily staggered)
    public float LastFeintBitTime = -99f;   // he reacted to a feint (flinched his guard toward it): a brief opening
    public float LastStepTime = -99f;       // view: he started a footwork step (a half step in or back, or aside)

    public Soldier(int id, Battalion unit)
    {
        Id = id;
        Unit = unit;
    }

    public int Team => Unit.Team;
    public WeaponDef Weapon => Unit.Type.Weapon;
    public ShieldDef Shield => Unit.Type.Shield;
    public ArmorDef Armor => Unit.Type.Armor;
    public bool Active => !Dead && !Escaped;
    public float HpFrac => Hp / MaxHp;
    public float StaminaFrac => Stamina / MaxStamina;
    public bool Veteran => Skill >= 0.7f;

    /// <summary>A rough 0..100 fighting rating, for display.</summary>
    public float Rating => 100f * Mathf.Clamp(
        0.55f * Skill + 0.15f * (Strength - 0.7f) / 0.6f + 0.15f * (Agility - 0.7f) / 0.6f + 0.15f * (Endurance - 0.7f) / 0.6f, 0f, 1f);

    public string StateText
    {
        get
        {
            if (Escaped) return "Fled the field";
            if (Dead) return KilledBy != null ? $"Killed by {KilledBy.Name}" : "Dead";
            if (Fleeing) return "Fleeing";
            return Phase switch
            {
                CombatPhase.Windup => Feint ? "Feinting" : Blow == BlowKind.Bash ? "Shield punch" : Probe ? "Probing" : "Attacking",
                CombatPhase.Recover => "Recovering",
                CombatPhase.Stagger => "Staggered",
                CombatPhase.Down => "Knocked down",
                _ => Target != null ? (BreatherUntil > LastActionTime ? "Catching his breath" : "Fighting") : Charging ? "Charging" : "In formation",
            };
        }
    }
}
