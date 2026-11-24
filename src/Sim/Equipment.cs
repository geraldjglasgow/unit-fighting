namespace UnitFighting.Sim;

public enum WeaponStyle { Sword, Spear, Axe }
public enum ShieldShape { Scutum, Round, Oval }

/// <summary>What a soldier fights with. Distances are metres centre to centre; times are seconds.</summary>
public sealed class WeaponDef
{
    public string Name = "";
    public WeaponStyle Style;
    public float Reach;              // furthest a blow can land
    public float IdealRange;         // distance the wielder tries to keep from his man
    public float MinRange;           // closer than this the weapon is cramped (spears)
    public float Windup;             // from starting an attack to the blow landing
    public float Recover;            // after the blow, before he can act again
    public float Damage;
    public float StaminaCost;
    public float Parry;              // how well the weapon itself turns a blow aside (0..1)
    public float ArmorPierce;        // 0..1, how much armour it ignores
    public bool ReachesPastFriends;  // can strike past an ally in front (spears from the second rank)
    public bool Braces;              // gets a free thrust at a charging man it is facing
    public float Length;             // visual only
}

public sealed class ShieldDef
{
    public string Name = "";
    public ShieldShape Shape;
    public float Block;       // chance to stop a blow it covers
    public float CoverLeft;   // radians off the nose it still covers on the shield side
    public float CoverRight;  // ...and on the weapon side
    public float Weight;
}

public sealed class ArmorDef
{
    public string Name = "";
    public float Protection;  // fraction of a blow's damage soaked
    public float Deflect;     // chance a blow glances off
    public float Weight;
    public bool Mail;
    public bool Helmet;
}

public static class Gear
{
    public static readonly WeaponDef Gladius = new()
    {
        Name = "Gladius", Style = WeaponStyle.Sword, Reach = 1.35f, IdealRange = 0.95f, MinRange = 0.4f,
        Windup = 0.5f, Recover = 0.45f, Damage = 36f, StaminaCost = 4.5f, Parry = 0.35f, ArmorPierce = 0.35f, Length = 0.6f,
    };

    public static readonly WeaponDef Longsword = new()
    {
        Name = "Longsword", Style = WeaponStyle.Sword, Reach = 1.55f, IdealRange = 1.1f, MinRange = 0.55f,
        Windup = 0.59f, Recover = 0.55f, Damage = 44f, StaminaCost = 6f, Parry = 0.35f, ArmorPierce = 0.2f, Length = 0.85f,
    };

    public static readonly WeaponDef Spear = new()
    {
        Name = "Spear", Style = WeaponStyle.Spear, Reach = 2.6f, IdealRange = 1.75f, MinRange = 1.3f,
        Windup = 0.55f, Recover = 0.55f, Damage = 32f, StaminaCost = 4.5f, Parry = 0.25f, ArmorPierce = 0.45f,
        ReachesPastFriends = true, Braces = true, Length = 2.5f,
    };

    public static readonly WeaponDef Axe = new()
    {
        Name = "Axe", Style = WeaponStyle.Axe, Reach = 1.45f, IdealRange = 1.0f, MinRange = 0.5f,
        Windup = 0.72f, Recover = 0.68f, Damage = 52f, StaminaCost = 7f, Parry = 0.15f, ArmorPierce = 0.5f, Length = 0.75f,
    };

    public static readonly ShieldDef Scutum = new() { Name = "Scutum", Shape = ShieldShape.Scutum, Block = 0.8f, CoverLeft = 1.75f, CoverRight = 0.7f, Weight = 1f };
    public static readonly ShieldDef Round = new() { Name = "Round shield", Shape = ShieldShape.Round, Block = 0.68f, CoverLeft = 1.5f, CoverRight = 0.8f, Weight = 0.6f };
    public static readonly ShieldDef Oval = new() { Name = "Oval shield", Shape = ShieldShape.Oval, Block = 0.72f, CoverLeft = 1.6f, CoverRight = 0.75f, Weight = 0.8f };

    public static readonly ArmorDef Mail = new() { Name = "Mail", Protection = 0.35f, Deflect = 0.25f, Weight = 1f, Mail = true, Helmet = true };
    public static readonly ArmorDef Leather = new() { Name = "Leather", Protection = 0.15f, Deflect = 0.1f, Weight = 0.4f, Helmet = true };
    public static readonly ArmorDef Cloth = new() { Name = "Tunic", Protection = 0.02f, Deflect = 0.03f, Weight = 0f };
}
