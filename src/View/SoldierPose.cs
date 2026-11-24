using Godot;
using UnitFighting.Sim;

namespace UnitFighting.View;

/// <summary>
/// A soldier's pose, in his torso frame (origin at the pelvis joint, +Z forward, +X his left, +Y up).
/// Arms are placed by hand position (two-bone IK does the elbows); the weapon by elevation/azimuth so that
/// interpolating between a raised and a lowered blade swings it over the top rather than through the body.
/// The body terms (hips, steps) are in his root frame at the feet.
/// </summary>
public struct SoldierPose
{
    public Vector3 HandR, PoleR, HandL, PoleL;
    public float Elev, Azim, Roll;      // weapon direction: elevation above horizontal, azimuth toward his left; roll turns the edge
    public float ShYaw, ShPitch;        // shield facing relative to the torso
    public float Twist, Lean, Side, Crouch, Head;
    public float HipX, HipZ;            // pelvis carried over one foot or the other (+X his left), forward into a blow (+Z)
    public float StepL, StepR;          // how far each foot's place moves forward (a lunge, a step in behind the shield)

    public static SoldierPose Lerp(in SoldierPose a, in SoldierPose b, float t)
    {
        return new SoldierPose
        {
            HandR = a.HandR.Lerp(b.HandR, t),
            PoleR = a.PoleR.Lerp(b.PoleR, t),
            HandL = a.HandL.Lerp(b.HandL, t),
            PoleL = a.PoleL.Lerp(b.PoleL, t),
            Elev = Mathf.Lerp(a.Elev, b.Elev, t),
            Azim = Mathf.Lerp(a.Azim, b.Azim, t),
            Roll = Mathf.Lerp(a.Roll, b.Roll, t),
            ShYaw = Mathf.Lerp(a.ShYaw, b.ShYaw, t),
            ShPitch = Mathf.Lerp(a.ShPitch, b.ShPitch, t),
            Twist = Mathf.Lerp(a.Twist, b.Twist, t),
            Lean = Mathf.Lerp(a.Lean, b.Lean, t),
            Side = Mathf.Lerp(a.Side, b.Side, t),
            Crouch = Mathf.Lerp(a.Crouch, b.Crouch, t),
            Head = Mathf.Lerp(a.Head, b.Head, t),
            HipX = Mathf.Lerp(a.HipX, b.HipX, t),
            HipZ = Mathf.Lerp(a.HipZ, b.HipZ, t),
            StepL = Mathf.Lerp(a.StepL, b.StepL, t),
            StepR = Mathf.Lerp(a.StepR, b.StepR, t),
        };
    }

    /// <summary>Only the weapon arm (and the body motion that goes with it) moves toward b.</summary>
    public static SoldierPose LerpRight(in SoldierPose a, in SoldierPose b, float t)
    {
        var r = a;
        r.HandR = a.HandR.Lerp(b.HandR, t);
        r.PoleR = a.PoleR.Lerp(b.PoleR, t);
        r.Elev = Mathf.Lerp(a.Elev, b.Elev, t);
        r.Azim = Mathf.Lerp(a.Azim, b.Azim, t);
        r.Roll = Mathf.Lerp(a.Roll, b.Roll, t);
        r.Twist = Mathf.Lerp(a.Twist, b.Twist, t);
        r.Lean = Mathf.Lerp(a.Lean, b.Lean, t);
        r.Crouch = Mathf.Lerp(a.Crouch, b.Crouch, t);
        r.HipX = Mathf.Lerp(a.HipX, b.HipX, t);
        r.HipZ = Mathf.Lerp(a.HipZ, b.HipZ, t);
        r.StepL = Mathf.Lerp(a.StepL, b.StepL, t);
        r.StepR = Mathf.Lerp(a.StepR, b.StepR, t);
        return r;
    }

    /// <summary>
    /// Move the weapon arm (and the body with it) by the difference between two poses, keeping whatever else is
    /// on it: a settled change of stance that doesn't still the life in his guard.
    /// </summary>
    public static void ShiftRight(ref SoldierPose p, in SoldierPose from, in SoldierPose to, float t)
    {
        p.HandR += (to.HandR - from.HandR) * t;
        p.PoleR += (to.PoleR - from.PoleR) * t;
        p.Elev += (to.Elev - from.Elev) * t;
        p.Azim += (to.Azim - from.Azim) * t;
        p.Roll += (to.Roll - from.Roll) * t;
        p.Twist += (to.Twist - from.Twist) * t;
        p.Lean += (to.Lean - from.Lean) * t;
        p.Crouch += (to.Crouch - from.Crouch) * t;
        p.HipX += (to.HipX - from.HipX) * t;
        p.HipZ += (to.HipZ - from.HipZ) * t;
        p.StepL += (to.StepL - from.StepL) * t;
        p.StepR += (to.StepR - from.StepR) * t;
    }

    /// <summary>The same for the shield arm.</summary>
    public static void ShiftLeft(ref SoldierPose p, in LeftArm from, in LeftArm to, float t)
    {
        p.HandL += (to.Hand - from.Hand) * t;
        p.PoleL += (to.Pole - from.Pole) * t;
        p.ShYaw += (to.Yaw - from.Yaw) * t;
        p.ShPitch += (to.Pitch - from.Pitch) * t;
    }

    public void SetLeft(in LeftArm l)
    {
        HandL = l.Hand;
        PoleL = l.Pole;
        ShYaw = l.Yaw;
        ShPitch = l.Pitch;
    }

    public readonly LeftArm Left => new(HandL, PoleL, ShYaw, ShPitch);

    public Vector3 WeaponDir => new(Mathf.Cos(Elev) * Mathf.Sin(Azim), Mathf.Sin(Elev), Mathf.Cos(Elev) * Mathf.Cos(Azim));

    /// <summary>
    /// The weapon's local frame: +Z along the blade, -Y the leading edge: of a downward cut at Roll 0, turned toward
    /// his left by a positive Roll (a forehand cut coming down and across) or his right by a negative one (a backhand).
    /// </summary>
    public Basis WeaponBasis
    {
        get
        {
            var z = WeaponDir;
            var axis = new Vector3(Mathf.Cos(Azim), 0f, -Mathf.Sin(Azim));
            var y = z.Cross(axis);
            var x = y.Cross(z);
            if (Roll != 0f)
            {
                float c = Mathf.Cos(Roll), s = Mathf.Sin(Roll);
                var x2 = x * c + y * s;
                y = y * c - x * s;
                x = x2;
            }
            return new Basis(x, y, z);
        }
    }
}

public struct LeftArm
{
    public Vector3 Hand, Pole;
    public float Yaw, Pitch;

    public LeftArm(Vector3 hand, Vector3 pole, float yaw, float pitch)
    {
        Hand = hand;
        Pole = pole;
        Yaw = yaw;
        Pitch = pitch;
    }

    public static LeftArm Lerp(in LeftArm a, in LeftArm b, float t) => new(a.Hand.Lerp(b.Hand, t), a.Pole.Lerp(b.Pole, t),
        Mathf.Lerp(a.Yaw, b.Yaw, t), Mathf.Lerp(a.Pitch, b.Pitch, t));
}

/// <summary>
/// The pose library: key poses per weapon, blended by the animator from the sim's phase clock. Each blow
/// (<see cref="BlowKind"/>) has a wind (where the blade is drawn back to) and a strike (where it ends up, with the
/// body behind it); the shield arm has a guard toward each line a blow can come in on.
/// </summary>
public sealed class PoseSet
{
    public SoldierPose Rest, Guard, Parry, Raised, Brace;
    public SoldierPose ParryHigh, ParryRight, ParryLow;   // the blade put across a blow coming at the head, the sword side, the legs
    public SoldierPose Spent;                             // blown: the point down, the arm hanging
    public readonly SoldierPose[] Winds = new SoldierPose[BlowCount], Strikes = new SoldierPose[BlowCount];
    public readonly SoldierPose[] Contacts = new SoldierPose[BlowCount];   // mid-swing, where the blade meets him (built from the two)

    public const int BlowCount = (int)BlowKind.Bash + 1;

    /// <summary>How far from the wind to the strike each blow is when it reaches the other man (the rest is follow-through).</summary>
    static readonly float[] ContactAt = { 0.62f, 0.68f, 0.6f, 0.9f, 0.7f, 0.92f };

    static SoldierPose R(float hx, float hy, float hz, float elev, float azim, float px, float py, float pz,
        float twist = 0f, float lean = 0f, float crouch = 0f) => new()
    {
        HandR = new Vector3(hx, hy, hz), PoleR = new Vector3(px, py, pz), Elev = elev, Azim = azim,
        Twist = twist, Lean = lean, Crouch = crouch,
        HandL = ShieldGuard.Hand, PoleL = ShieldGuard.Pole, ShYaw = ShieldGuard.Yaw, ShPitch = ShieldGuard.Pitch,
    };

    // Shield arm.
    public static readonly LeftArm ShieldRest = new(new Vector3(0.27f, 0.06f, 0.17f), new Vector3(0.5f, -0.2f, -1f), 0.3f, 0.04f);
    public static readonly LeftArm ShieldGuard = new(new Vector3(0.15f, 0.13f, 0.36f), new Vector3(0.8f, -0.8f, -0.3f), -0.15f, 0.06f);
    public static readonly LeftArm ShieldAttack = new(new Vector3(0.2f, 0.11f, 0.3f), new Vector3(0.8f, -0.6f, -0.5f), 0.05f, 0.04f);
    public static readonly LeftArm ShieldLoose = new(new Vector3(0.33f, 0.0f, 0.06f), new Vector3(0.5f, 0f, -1f), 0.75f, -0.25f);
    public static readonly LeftArm ShieldFlee = new(new Vector3(0.3f, 0.12f, -0.06f), new Vector3(0.4f, -0.3f, -1f), 1.3f, 0f);
    public static readonly LeftArm ShieldDead = new(new Vector3(0.6f, 0.28f, -0.02f), new Vector3(0.3f, 0.2f, -1f), 0.15f, 0f);
    public static readonly LeftArm ShieldBrace = new(new Vector3(0.13f, 0.17f, 0.33f), new Vector3(0.8f, -0.8f, -0.3f), -0.1f, 0.1f);
    public static readonly LeftArm ShieldDown = new(new Vector3(0.14f, 0.2f, 0.27f), new Vector3(0.8f, -0.5f, -0.3f), -0.05f, 0.1f);
    public static readonly LeftArm NoShield = new(new Vector3(0.25f, 0.08f, 0.12f), new Vector3(0.3f, -0.2f, -1f), 0f, 0f);
    // Blown between exchanges: the arm hangs and the rim all but rests on the ground in front of him.
    public static readonly LeftArm ShieldGrounded = new(new Vector3(0.19f, -0.04f, 0.3f), new Vector3(0.7f, -0.2f, -0.6f), 0.12f, -0.12f);
    // Punching with the boss: drawn in and cocked, then driven out with the shoulder behind it, the board square to him.
    public static readonly LeftArm ShieldBashWind = new(new Vector3(0.21f, 0.12f, 0.2f), new Vector3(0.9f, -0.5f, -0.5f), 0.2f, 0.04f);
    public static readonly LeftArm ShieldBashStrike = new(new Vector3(0.07f, 0.17f, 0.56f), new Vector3(0.7f, -0.9f, -0.1f), 0.45f, 0.1f);

    /// <summary>Where the shield goes to meet each kind of blow (indexed by <see cref="BlowKind"/>).</summary>
    public static readonly LeftArm[] ShieldMeets =
    {
        // Cut: a forehand comes in on his shield side, high.
        new(new Vector3(0.21f, 0.3f, 0.37f), new Vector3(0.9f, -0.6f, -0.3f), 0.38f, -0.08f),
        // Overhead: up over the head, the face tipped to the sky.
        new(new Vector3(0.09f, 0.44f, 0.33f), new Vector3(0.9f, -0.2f, -0.2f), -0.08f, -0.5f),
        // Backhand: across to the sword side.
        new(new Vector3(0.0f, 0.22f, 0.4f), new Vector3(0.9f, -0.8f, 0.1f), -0.55f, 0.02f),
        // Thrust: square on and pushed out to meet the point.
        new(new Vector3(0.11f, 0.17f, 0.45f), new Vector3(0.8f, -0.8f, -0.2f), -0.12f, 0.1f),
        // Low: dropped, the rim swept out over the legs.
        new(new Vector3(0.15f, -0.01f, 0.37f), new Vector3(0.8f, -0.4f, -0.3f), 0.0f, 0.38f),
        // Bash: set square and leaned into to take the shove.
        new(new Vector3(0.12f, 0.16f, 0.34f), new Vector3(0.8f, -0.8f, -0.3f), -0.12f, 0.12f),
    };

    // Right arm, whatever the weapon.
    public static readonly SoldierPose Loose = R(-0.33f, 0.0f, 0.08f, -0.7f, -0.5f, -0.3f, 0f, -1f, 0f, -0.28f, 0.04f);
    public static readonly SoldierPose Flee = R(-0.25f, 0.14f, 0.12f, 0.35f, 0.1f, -0.3f, -0.3f, -1f, 0f, 0.4f, 0.05f);
    public static readonly SoldierPose Dead = R(-0.46f, 0.64f, 0.02f, 0.93f, -1.57f, -1f, 0f, -0.3f, 0f, 0f, 0f);

    public static readonly PoseSet Sword = new()
    {
        Rest = R(-0.25f, 0.02f, 0.1f, -1.0f, 0.15f, -0.2f, -0.1f, -1f),
        Guard = R(-0.17f, 0.25f, 0.34f, 0.65f, 0.3f, -0.7f, -1f, -0.2f, -0.15f, 0.1f, 0.07f),
        Parry = R(0.0f, 0.42f, 0.34f, 0.55f, 1.25f, -1f, -0.4f, 0f, -0.1f, 0.06f, 0.08f),
        ParryHigh = R(-0.08f, 0.72f, 0.3f, 0.12f, 1.3f, -1f, 0.2f, 0.2f, -0.05f, -0.04f, 0.08f),
        ParryRight = R(-0.36f, 0.38f, 0.36f, 0.95f, -0.25f, -1f, -0.5f, 0f, -0.25f, 0.06f, 0.08f),
        ParryLow = R(-0.1f, 0.08f, 0.38f, -0.6f, 0.9f, -0.8f, -0.6f, 0f, 0.05f, 0.18f, 0.14f),
        Raised = R(-0.22f, 0.86f, 0.1f, 1.25f, 0f, -1f, 0f, 0.2f, -0.1f, 0.12f, 0f),
        Brace = R(-0.19f, 0.3f, 0.22f, 0.8f, 0.25f, -0.7f, -1f, -0.2f, -0.22f, 0.2f, 0.15f),
        Spent = R(-0.24f, 0.06f, 0.24f, -0.55f, 0.35f, -0.4f, -0.6f, -0.6f, -0.05f, 0.16f, 0.06f),
    };

    public static readonly PoseSet Axe = new()
    {
        Rest = R(-0.25f, 0.02f, 0.1f, -1.25f, 0.1f, -0.2f, -0.1f, -1f),
        Guard = R(-0.2f, 0.28f, 0.3f, 0.95f, 0.2f, -0.7f, -1f, -0.2f, -0.15f, 0.1f, 0.07f),
        Parry = R(0.0f, 0.42f, 0.32f, 0.6f, 1.2f, -1f, -0.4f, 0f, -0.1f, 0.06f, 0.08f),
        ParryHigh = R(-0.08f, 0.7f, 0.28f, 0.15f, 1.25f, -1f, 0.2f, 0.2f, -0.05f, -0.04f, 0.08f),
        ParryRight = R(-0.36f, 0.36f, 0.34f, 1.05f, -0.25f, -1f, -0.5f, 0f, -0.25f, 0.06f, 0.08f),
        ParryLow = R(-0.12f, 0.1f, 0.36f, -0.5f, 0.8f, -0.8f, -0.6f, 0f, 0.05f, 0.18f, 0.14f),
        Raised = R(-0.2f, 0.86f, 0.08f, 1.5f, 0f, -1f, 0f, 0.2f, -0.1f, 0.12f, 0f),
        Brace = R(-0.21f, 0.32f, 0.2f, 1.05f, 0.2f, -0.7f, -1f, -0.2f, -0.22f, 0.2f, 0.15f),
        Spent = R(-0.26f, 0.04f, 0.2f, -0.8f, 0.3f, -0.4f, -0.6f, -0.6f, -0.05f, 0.16f, 0.06f),
    };

    public static readonly PoseSet Spear = new()
    {
        Rest = R(-0.3f, 0.06f, 0.13f, 1.52f, 0f, -0.2f, -0.2f, -1f),
        Guard = R(-0.21f, 0.2f, 0.2f, -0.07f, 0.1f, -0.8f, -0.6f, -0.5f, -0.1f, 0.08f, 0.07f),
        Parry = R(-0.05f, 0.36f, 0.3f, 0.45f, 0.7f, -1f, -0.5f, 0f, -0.1f, 0.05f, 0.07f),
        ParryHigh = R(-0.1f, 0.5f, 0.28f, 0.5f, 0.6f, -1f, -0.2f, 0f, -0.1f, 0.0f, 0.07f),
        ParryRight = R(-0.3f, 0.32f, 0.3f, 0.35f, -0.35f, -1f, -0.5f, 0f, -0.2f, 0.05f, 0.07f),
        ParryLow = R(-0.12f, 0.14f, 0.3f, -0.35f, 0.5f, -0.8f, -0.6f, 0f, 0.0f, 0.12f, 0.12f),
        // Grounded to meet a charge: low, the point set at a running man's chest.
        Brace = R(-0.2f, 0.06f, 0.14f, 0.1f, 0.08f, -0.8f, -0.6f, -0.5f, -0.2f, 0.16f, 0.17f),
        Spent = R(-0.24f, 0.08f, 0.16f, -0.32f, 0.12f, -0.6f, -0.6f, -0.6f, -0.05f, 0.14f, 0.06f),
    };

    static PoseSet()
    {
        // ---- sword: every blow puts the hips and shoulders into it and ends with the weight forward
        var sw = Sword;
        // Cut: drawn up over the sword shoulder, then down and across to his left.
        sw.Winds[(int)BlowKind.Cut] = R(-0.34f, 0.64f, -0.08f, 0.95f, -2.3f, -1f, 0.1f, 0.1f, -0.52f, -0.04f, 0.05f) with { Roll = 0.7f, HipZ = -0.02f };
        sw.Strikes[(int)BlowKind.Cut] = R(0.1f, 0.16f, 0.46f, -0.5f, 0.95f, -0.8f, -0.6f, 0f, 0.46f, 0.24f, 0.12f) with { Roll = 0.7f, HipZ = 0.05f, StepL = 0.07f };
        // Overhead: right back behind the head, the back arched, then straight down with everything behind it.
        sw.Winds[(int)BlowKind.Overhead] = R(-0.12f, 0.84f, -0.1f, 2.55f, -0.1f, -1f, 0.2f, 0.4f, -0.3f, -0.12f, 0.03f) with { HipZ = -0.03f };
        sw.Strikes[(int)BlowKind.Overhead] = R(0.0f, 0.14f, 0.52f, -0.62f, 0.25f, -0.8f, -0.6f, 0f, 0.25f, 0.32f, 0.15f) with { HipZ = 0.07f, StepL = 0.1f };
        // Backhand: the arm across his chest, the blade over his shield shoulder, then swept out to his right.
        sw.Winds[(int)BlowKind.Backhand] = R(0.13f, 0.5f, 0.1f, 0.75f, 2.4f, -0.3f, -0.6f, 0.8f, 0.4f, 0.02f, 0.06f) with { Roll = -1.2f };
        sw.Strikes[(int)BlowKind.Backhand] = R(-0.4f, 0.3f, 0.4f, 0.02f, -1.1f, -1f, -0.3f, 0f, -0.36f, 0.18f, 0.1f) with { Roll = -1.2f, HipZ = 0.04f, StepR = 0.05f };
        // Thrust: drawn back to the hip, then a lunge off the back foot.
        sw.Winds[(int)BlowKind.Thrust] = R(-0.27f, 0.12f, -0.14f, 0.1f, 0.05f, -0.3f, -0.3f, -1f, -0.48f, 0.02f, 0.09f) with { HipZ = -0.04f };
        sw.Strikes[(int)BlowKind.Thrust] = R(-0.08f, 0.33f, 0.53f, 0.02f, 0.1f, -0.5f, -0.8f, 0f, 0.44f, 0.26f, 0.13f) with { HipZ = 0.12f, StepL = 0.2f };
        // Low: drawn back low behind the hip, knees bent, then swept round under the shield at the legs, bent right over.
        sw.Winds[(int)BlowKind.Low] = R(-0.32f, 0.12f, -0.14f, -0.2f, -2.5f, -0.8f, -0.2f, -0.6f, -0.5f, 0.16f, 0.13f) with { Roll = 1.0f, HipZ = -0.02f };
        sw.Strikes[(int)BlowKind.Low] = R(0.02f, -0.02f, 0.44f, -0.8f, 0.75f, -0.8f, -0.5f, 0.3f, 0.35f, 0.42f, 0.22f) with { Roll = 1.0f, HipZ = 0.06f, StepL = 0.1f };
        // Bash: the blade kept back and up, the shield (see ShieldBash*) driven out with a step in behind it.
        sw.Winds[(int)BlowKind.Bash] = R(-0.26f, 0.24f, 0.1f, 0.7f, 0.1f, -0.8f, -1f, -0.3f, 0.28f, 0.02f, 0.12f) with { HipZ = -0.04f };
        sw.Strikes[(int)BlowKind.Bash] = R(-0.24f, 0.28f, 0.2f, 0.75f, 0.15f, -0.8f, -1f, -0.3f, -0.5f, 0.32f, 0.12f) with { HipZ = 0.16f, StepL = 0.24f };

        // ---- axe: the same lines, drawn further back and coming down harder
        for (int k = 0; k < BlowCount; k++)
        {
            var w = sw.Winds[k];
            var st = sw.Strikes[k];
            if (k != (int)BlowKind.Thrust && k != (int)BlowKind.Bash)
            {
                w.Elev += k == (int)BlowKind.Overhead ? 0.3f : 0.15f;
                w.Twist *= 1.2f;
                w.Lean -= 0.04f;
                st.Elev -= 0.15f;
                st.Lean += 0.04f;
                st.Crouch += 0.02f;
            }
            Axe.Winds[k] = w;
            Axe.Strikes[k] = st;
        }
        // An axe is no stabbing weapon: his "thrust" is a short chop with the weight on the front foot.
        Axe.Winds[(int)BlowKind.Thrust] = R(-0.22f, 0.6f, 0.02f, 1.7f, -0.3f, -1f, 0f, 0.3f, -0.3f, 0.02f, 0.08f) with { HipZ = -0.03f };
        Axe.Strikes[(int)BlowKind.Thrust] = R(-0.04f, 0.22f, 0.5f, -0.35f, 0.2f, -0.8f, -0.6f, 0f, 0.28f, 0.26f, 0.13f) with { HipZ = 0.1f, StepL = 0.16f };

        // ---- spear: all point-work, underarm, overhand or low, with a lunge in
        var sp = Spear;
        sp.Winds[(int)BlowKind.Thrust] = R(-0.23f, 0.17f, -0.25f, -0.04f, 0.1f, -0.4f, -0.5f, -1f, -0.3f, 0f, 0.08f) with { HipZ = -0.04f };
        sp.Strikes[(int)BlowKind.Thrust] = R(-0.12f, 0.26f, 0.5f, -0.1f, 0.08f, -0.6f, -0.8f, 0f, 0.32f, 0.22f, 0.12f) with { HipZ = 0.1f, StepL = 0.2f };
        sp.Winds[(int)BlowKind.Cut] = R(-0.27f, 0.2f, -0.2f, 0.0f, 0.24f, -0.4f, -0.5f, -1f, -0.36f, 0f, 0.08f) with { HipZ = -0.03f };
        sp.Strikes[(int)BlowKind.Cut] = R(-0.07f, 0.3f, 0.48f, -0.08f, 0.02f, -0.6f, -0.8f, 0f, 0.36f, 0.2f, 0.11f) with { HipZ = 0.08f, StepL = 0.16f };
        // Overhand: the spear lifted over the shoulder and driven down at the face and neck over the shield.
        sp.Winds[(int)BlowKind.Overhead] = R(-0.24f, 0.66f, -0.18f, -0.22f, 0.08f, -1f, 0.6f, -0.3f, -0.4f, -0.05f, 0.04f) with { HipZ = -0.03f };
        sp.Strikes[(int)BlowKind.Overhead] = R(-0.1f, 0.6f, 0.42f, -0.34f, 0.06f, -1f, 0.3f, 0f, 0.34f, 0.24f, 0.1f) with { HipZ = 0.08f, StepL = 0.16f };
        sp.Winds[(int)BlowKind.Backhand] = R(-0.14f, 0.26f, -0.22f, 0.02f, 0.3f, -0.4f, -0.6f, -1f, -0.2f, 0f, 0.08f);
        sp.Strikes[(int)BlowKind.Backhand] = R(-0.22f, 0.28f, 0.5f, -0.08f, -0.22f, -0.8f, -0.6f, 0f, 0.3f, 0.2f, 0.11f) with { HipZ = 0.07f, StepL = 0.12f };
        sp.Winds[(int)BlowKind.Low] = R(-0.24f, 0.05f, -0.22f, -0.2f, 0.1f, -0.4f, -0.5f, -1f, -0.3f, 0.04f, 0.1f) with { HipZ = -0.03f };
        sp.Strikes[(int)BlowKind.Low] = R(-0.1f, 0.04f, 0.46f, -0.38f, 0.08f, -0.6f, -0.8f, 0f, 0.35f, 0.32f, 0.2f) with { HipZ = 0.08f, StepL = 0.16f };
        sp.Winds[(int)BlowKind.Bash] = R(-0.24f, 0.2f, 0.02f, 0.0f, 0.1f, -0.8f, -0.6f, -0.5f, 0.28f, 0.02f, 0.12f) with { HipZ = -0.04f };
        sp.Strikes[(int)BlowKind.Bash] = R(-0.24f, 0.22f, 0.12f, 0.02f, 0.12f, -0.8f, -0.6f, -0.5f, -0.5f, 0.32f, 0.12f) with { HipZ = 0.16f, StepL = 0.24f };
        sp.Raised = sp.Guard;

        foreach (var set in new[] { Sword, Axe, Spear })
            for (int k = 0; k < BlowCount; k++)
                set.Contacts[k] = SoldierPose.Lerp(set.Winds[k], set.Strikes[k], ContactAt[k]);
    }

    /// <summary>The parry that meets a blow of this kind (for a man turning it with his weapon rather than a shield).</summary>
    public ref readonly SoldierPose ParryFor(BlowKind k)
    {
        switch (k)
        {
            case BlowKind.Overhead: return ref ParryHigh;
            case BlowKind.Backhand: return ref ParryRight;
            case BlowKind.Low: return ref ParryLow;
            default: return ref Parry;
        }
    }
}
