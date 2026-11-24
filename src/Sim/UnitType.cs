using System.Collections.Generic;

namespace UnitFighting.Sim;

/// <summary>Which army a unit type belongs to, so the setup screen can list each side's roster.</summary>
public enum Faction { Rome, Gauls }

/// <summary>A kind of battalion: its kit, and the distribution its men's attributes are rolled from.</summary>
public sealed class UnitType
{
    public string Name = "";
    public WeaponDef Weapon = Gear.Gladius;
    public ShieldDef Shield = Gear.Scutum;
    public ArmorDef Armor = Gear.Mail;
    public int Size = 20;
    public float SkillMean = 0.5f, SkillSpread = 0.15f;
    public float StrengthMean = 1f, AgilityMean = 1f, EnduranceMean = 1f;
    public float NerveMean = 0.55f, NerveSpread = 0.15f;
    public float WalkSpeed = 1.5f, RunSpeed = 4f;
    public FormationType Formation = FormationType.Line;
    public string[] FirstNames = UnitTypes.RomanFirst;
    public string[] LastNames = UnitTypes.RomanLast;
    public Faction Faction = Faction.Rome;
    public string Blurb = "";   // short flavour/stats line for the setup screen

    /// <summary>A copy of this type with a different battalion size (the setup screen and CLI build battalions
    /// this way, since <see cref="Size"/> on the shared UnitTypes.* instances must stay untouched).</summary>
    public UnitType Sized(int men) => new()
    {
        Name = Name, Weapon = Weapon, Shield = Shield, Armor = Armor, Size = men,
        SkillMean = SkillMean, SkillSpread = SkillSpread,
        StrengthMean = StrengthMean, AgilityMean = AgilityMean, EnduranceMean = EnduranceMean,
        NerveMean = NerveMean, NerveSpread = NerveSpread,
        WalkSpeed = WalkSpeed, RunSpeed = RunSpeed, Formation = Formation,
        FirstNames = FirstNames, LastNames = LastNames, Faction = Faction, Blurb = Blurb,
    };
}

public static class UnitTypes
{
    public static readonly string[] RomanFirst =
    {
        "Marcus", "Lucius", "Gaius", "Titus", "Quintus", "Decimus", "Publius", "Sextus", "Aulus",
        "Gnaeus", "Servius", "Tiberius", "Manius", "Numerius", "Spurius", "Appius", "Kaeso", "Vibius",
    };

    public static readonly string[] RomanLast =
    {
        "Varro", "Crispus", "Rufus", "Longus", "Naso", "Cotta", "Balbus", "Scaevola", "Brutus",
        "Dento", "Flaccus", "Lentulus", "Macer", "Paullus", "Silo", "Strabo", "Tullus", "Vitulus",
    };

    public static readonly string[] GaulFirst =
    {
        "Brennus", "Cotus", "Litavic", "Epidos", "Viridomar", "Cavaros", "Tasgetios", "Segomaros", "Boduos",
        "Cingetos", "Nertomaros", "Toutillos", "Andecos", "Ambiorix", "Carantos", "Divicos", "Ollos", "Suros",
    };

    public static readonly string[] GaulLast =
    {
        "the Bold", "the Tall", "Redhand", "Boarhelm", "Oakheart", "the Loud", "Stonefist", "the Young",
        "Wolfcloak", "the Grey", "Ironbrow", "the Swift", "Longarm", "the Grim", "Bearclaw", "the Fair",
    };

    public static readonly UnitType Legionaries = new()
    {
        Name = "Legionaries", Weapon = Gear.Gladius, Shield = Gear.Scutum, Armor = Gear.Mail,
        SkillMean = 0.5f, SkillSpread = 0.14f, EnduranceMean = 1.05f, NerveMean = 0.6f, NerveSpread = 0.12f,
        WalkSpeed = 1.5f, RunSpeed = 3.9f, Faction = Faction.Rome,
        Blurb = "Gladius and scutum, mail armour. Disciplined and well-drilled: the backbone of the line.",
    };

    public static readonly UnitType Veterans = new()
    {
        Name = "Veteran Legionaries", Weapon = Gear.Gladius, Shield = Gear.Scutum, Armor = Gear.Mail,
        SkillMean = 0.68f, SkillSpread = 0.1f, StrengthMean = 1.05f, EnduranceMean = 1.1f, NerveMean = 0.75f, NerveSpread = 0.1f,
        WalkSpeed = 1.5f, RunSpeed = 3.9f, Faction = Faction.Rome,
        Blurb = "The same kit as the Legionaries, hardened by experience: higher skill, endurance and nerve.",
    };

    public static readonly UnitType AuxSpearmen = new()
    {
        Name = "Auxiliary Spearmen", Weapon = Gear.Spear, Shield = Gear.Round, Armor = Gear.Leather,
        SkillMean = 0.45f, SkillSpread = 0.14f, NerveMean = 0.5f, NerveSpread = 0.15f,
        WalkSpeed = 1.55f, RunSpeed = 4.1f, Faction = Faction.Rome,
        Blurb = "Spear and round shield reach past the front rank; lighter armour than the legions.",
    };

    public static readonly UnitType Warband = new()
    {
        Name = "Gallic Warband", Weapon = Gear.Longsword, Shield = Gear.Oval, Armor = Gear.Cloth,
        SkillMean = 0.45f, SkillSpread = 0.2f, StrengthMean = 1.12f, AgilityMean = 1.05f, EnduranceMean = 0.95f,
        NerveMean = 0.5f, NerveSpread = 0.2f, WalkSpeed = 1.6f, RunSpeed = 4.3f,
        FirstNames = GaulFirst, LastNames = GaulLast, Faction = Faction.Gauls,
        Blurb = "Longsword and oval shield. Strong and aggressive, but unarmoured and their nerve can crack.",
    };

    public static readonly UnitType Axemen = new()
    {
        Name = "Gallic Axemen", Weapon = Gear.Axe, Shield = Gear.Round, Armor = Gear.Cloth,
        SkillMean = 0.42f, SkillSpread = 0.18f, StrengthMean = 1.15f, EnduranceMean = 0.95f,
        NerveMean = 0.5f, NerveSpread = 0.18f, WalkSpeed = 1.6f, RunSpeed = 4.2f,
        FirstNames = GaulFirst, LastNames = GaulLast, Faction = Faction.Gauls,
        Blurb = "Heavy axes punch through armour, at the cost of a slow, tiring swing.",
    };

    public static readonly UnitType ChosenSwordsmen = new()
    {
        Name = "Chosen Swordsmen", Weapon = Gear.Longsword, Shield = Gear.Oval, Armor = Gear.Mail,
        SkillMean = 0.65f, SkillSpread = 0.12f, StrengthMean = 1.12f, EnduranceMean = 1.05f,
        NerveMean = 0.72f, NerveSpread = 0.1f, WalkSpeed = 1.5f, RunSpeed = 4f,
        FirstNames = GaulFirst, LastNames = GaulLast, Faction = Faction.Gauls,
        Blurb = "The tribe's best fighters: mail-armoured and skilled, a match for veteran legionaries.",
    };

    public static readonly UnitType SpearLevy = new()
    {
        Name = "Spear Levy", Weapon = Gear.Spear, Shield = Gear.Round, Armor = Gear.Cloth,
        SkillMean = 0.3f, SkillSpread = 0.12f, EnduranceMean = 0.95f, NerveMean = 0.38f, NerveSpread = 0.15f,
        WalkSpeed = 1.55f, RunSpeed = 4.1f,
        FirstNames = GaulFirst, LastNames = GaulLast, Faction = Faction.Gauls,
        Blurb = "Levied spearmen with little training or resolve, but cheap in numbers.",
    };

    /// <summary>Every buildable unit type, Rome then Gauls, in the order the setup screen lists them.</summary>
    public static readonly UnitType[] All = { Legionaries, Veterans, AuxSpearmen, Warband, Axemen, ChosenSwordsmen, SpearLevy };

    /// <summary>Looked up by CLI flags (e.g. <c>--rome=Legionaries:3,AuxSpearmen:2</c>) by field name, not display name.</summary>
    public static readonly Dictionary<string, UnitType> ByName = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["Legionaries"] = Legionaries, ["Veterans"] = Veterans, ["AuxSpearmen"] = AuxSpearmen,
        ["Warband"] = Warband, ["Axemen"] = Axemen, ["ChosenSwordsmen"] = ChosenSwordsmen, ["SpearLevy"] = SpearLevy,
    };
}
