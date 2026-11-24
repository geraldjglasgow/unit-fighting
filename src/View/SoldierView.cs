using System;
using System.Collections.Generic;
using Godot;
using UnitFighting.Sim;
using static UnitFighting.View.MeshBuilder;
using static UnitFighting.View.SoldierAssets;

namespace UnitFighting.View;

/// <summary>
/// One soldier's look (rolled once from his id) and his animation state. Plain data, not a node:
/// BattleView calls <see cref="Animate"/> for every man each frame, which poses a procedural skeleton from the
/// sim state and writes each body part into the shared part batches.
/// </summary>
public sealed class SoldierView
{
    public readonly Soldier S;

    // ---- look
    readonly Color _skin, _tunic, _legs, _torso, _sleeve, _hair, _boots, _belt, _helmet, _crest, _paint, _design, _gold, _blade, _wood;
    readonly Part _torsoPart, _sleevePart, _headTop, _weapon1, _weapon2, _board, _boss;
    readonly Part _trim = Part.Count, _designPart = Part.Count;
    readonly bool _crestOn, _goldOn, _hasShield;
    readonly PoseSet _set;
    readonly bool _spear;
    readonly float _scale, _seed, _deathTwist, _deathSpin, _fallDur, _fallMax;
    readonly bool _romanLegs;

    // ---- his manner in a fight (rolled once), so that a line of men doesn't move as one
    readonly float _ph0, _ph1, _ph2, _ph3, _swayRate, _fidget, _stanceW, _stanceL, _crouchBias, _hurtSide, _beatEvery;

    // ---- animation state
    SoldierPose _cur;
    bool _hasPose;
    float _speed, _walkPhase, _alert, _brace;
    Vector3 _moveLocal = Vector3.Back;
    CombatPhase _lastPhase;
    float _sway, _breath, _engage, _rest, _hurt, _retire;   // restless-motion clocks, and how much he is fighting / resting / hurt / backing out
    float _tired;                                            // how blown he is (0 fresh .. 1 spent), for the feet
    BlowKind _anticKind;                                     // the line of the last blow he moved his guard to meet

    // ---- feet: planted on the ground and stepped when the body has moved off them, so they never slide
    struct Foot
    {
        public Vector2 Plant, From;   // world ground position where it stands (or last stood, while stepping)
        public float Yaw, FromYaw;    // world yaw of the foot
        public float T;               // progress of the step it is taking (0..1), or -1 planted
    }

    Foot _footL, _footR;
    bool _feetValid;
    Vector3 _footPosL, _footPosR;   // this frame's feet in his root frame, for Emit
    float _footYawL, _footYawR;
    bool _feetPlanted;

    // ---- knocked down: he lies along _fallDir, on his back or (_onFace) his face; _lastFall is how far over he was last frame
    bool _wasDown, _onFace, _landedDown;
    Vector2 _fallDir, _lastFallDir;
    float _downFrom, _upStart = -1f, _upLen, _lastFall, _deathFrom = -1f;

    // ---- set from combat events
    public float BlockRel;          // where the last blocked blow came from, as he saw it (0 ahead, + his left)
    public Vector2 HitPush;         // world direction he was pushed by the last blow
    public float WoundTime = -99f;  // last time a blow drew blood (glances don't flash)
    public Vector2 ImpactPush;      // world direction of the last charge impact
    public float ShoveTime = -99f;  // when a charging man last crashed into him,
    public float Shove;             // and how hard (the impact ratio: over 1 staggers him or bowls him over)
    public float RamTime = -99f;    // when he last crashed into someone at the run
    public float HitAmount;         // damage of the last blow that reached him (heavier ones rock him further)
    public float MissTime = -99f;   // a blow at him just went past
    public Vector2 MissFrom;        // world direction it came from
    public float StoppedTime = -99f;   // his last blow was stopped by a shield or a blade: it jars back instead of following through
    public float MissedTime = -99f;    // his last blow met nothing: it carries him on after it
    public BlowKind BlockKind;      // the line of the last blow he turned

    // ---- set by the battle view each frame
    public bool StandTo;            // an enemy charge is bearing down on his battalion: guard up, feet set
    public float FrontRoom = 9f;    // how far forward (metres) his shield can reach before it meets the enemy in front of him

    /// <summary>His current (smoothed) pose, for tools that measure the animation.</summary>
    public SoldierPose Pose => _cur;

    // ---- published for rings / markers
    public Vector3 GroundPos;
    public float HeadHeight = 1.8f;
    public bool Visible;
    public bool JustLanded;         // his body hit the ground this frame (for a dust puff)
    public Vector2 LandDir;         // which way from his feet he lies
    bool _landed;

    static readonly Color[] GaulTunics =
    {
        new(0.26f, 0.36f, 0.18f), new(0.22f, 0.3f, 0.44f), new(0.6f, 0.46f, 0.2f), new(0.45f, 0.31f, 0.19f),
        new(0.33f, 0.4f, 0.24f), new(0.24f, 0.38f, 0.38f), new(0.55f, 0.52f, 0.42f), new(0.38f, 0.26f, 0.32f),
    };

    static readonly Color[] GaulTrousers =
    {
        new(0.36f, 0.27f, 0.17f), new(0.3f, 0.3f, 0.28f), new(0.48f, 0.38f, 0.2f), new(0.24f, 0.3f, 0.2f),
        new(0.4f, 0.24f, 0.14f),
    };

    static readonly Color[] Hairs =
    {
        new(0.82f, 0.66f, 0.36f), new(0.56f, 0.38f, 0.2f), new(0.3f, 0.19f, 0.1f), new(0.1f, 0.08f, 0.07f),
        new(0.62f, 0.27f, 0.1f), new(0.72f, 0.55f, 0.3f),
    };

    static readonly Color[] GaulShields =
    {
        new(0.2f, 0.32f, 0.56f), new(0.24f, 0.44f, 0.22f), new(0.84f, 0.8f, 0.68f), new(0.78f, 0.58f, 0.18f),
        new(0.14f, 0.14f, 0.14f), new(0.5f, 0.35f, 0.2f), new(0.18f, 0.46f, 0.46f),
    };

    static readonly Color[] GaulDesigns =
    {
        new(0.92f, 0.88f, 0.76f), new(0.85f, 0.65f, 0.15f), new(0.12f, 0.12f, 0.14f), new(0.2f, 0.35f, 0.7f),
    };

    public SoldierView(Soldier s)
    {
        S = s;
        int id = s.Id * 7 + 13;
        float H(int k) => Hash(id, k);
        Color Pick(Color[] pal, int k) => pal[(int)(H(k) * pal.Length) % pal.Length];
        Color Vary(Color c, float amount, int k) => new Color(
            c.R * (1f + (H(k) - 0.5f) * amount), c.G * (1f + (H(k + 1) - 0.5f) * amount), c.B * (1f + (H(k + 2) - 0.5f) * amount));

        bool roman = s.Team == 0;
        var armor = s.Armor;
        _seed = H(1) * 100f;
        _scale = 0.95f + 0.1f * H(2);
        _deathTwist = (H(3) - 0.5f) * 0.5f;
        _deathSpin = (H(4) - 0.5f) * 0.6f;
        _fallDur = 0.45f + 0.2f * H(5);
        _fallMax = 1.5f + 0.06f * H(6);
        _ph0 = H(70) * Mathf.Tau;
        _ph1 = H(71) * Mathf.Tau;
        _ph2 = H(72) * Mathf.Tau;
        _ph3 = H(73) * Mathf.Tau;
        _swayRate = 1.25f + 0.8f * H(74);
        // A raw man fidgets and bounces; a veteran moves less and wastes nothing.
        _fidget = (0.75f + 0.5f * H(75)) * (1.3f - 0.6f * s.Skill);
        _stanceW = 0.03f * H(76);
        _stanceL = 0.06f * H(77) - 0.02f;
        _crouchBias = 0.035f + 0.04f * H(78) + (s.Weapon.Style == WeaponStyle.Sword ? 0f : 0.015f);
        _hurtSide = H(79) < 0.5f ? -1f : 1f;
        _beatEvery = 0.75f + 0.6f * H(80);

        float tone = H(10);
        _skin = Lin(0.78f - 0.16f * tone, 0.55f - 0.14f * tone, 0.41f - 0.11f * tone);
        if (!roman) _skin = Lin(0.82f - 0.1f * tone, 0.6f - 0.09f * tone, 0.47f - 0.08f * tone);
        _hair = Vary(Pick(Hairs, 11), 0.15f, 12).SrgbToLinear();
        _boots = Vary(roman ? new Color(0.36f, 0.23f, 0.12f) : new Color(0.28f, 0.2f, 0.13f), 0.2f, 15).SrgbToLinear();

        if (roman)
        {
            _tunic = Vary(new Color(0.66f, 0.1f, 0.08f), 0.12f, 20).SrgbToLinear();
            _legs = _skin;
            _romanLegs = true;
        }
        else
        {
            _tunic = Vary(Pick(GaulTunics, 20), 0.18f, 21).SrgbToLinear();
            _legs = Vary(Pick(GaulTrousers, 24), 0.15f, 25).SrgbToLinear();
        }

        if (armor.Mail)
        {
            _torsoPart = Part.TorsoMail;
            _sleevePart = Part.SleeveMail;
            _torso = _sleeve = Vary(new Color(0.4f, 0.4f, 0.41f), 0.08f, 30).SrgbToLinear();
        }
        else if (armor.Protection > 0.1f)
        {
            // Leather: a brown cuirass over the tunic.
            _torsoPart = Part.Torso;
            _sleevePart = Part.Sleeve;
            _torso = Vary(new Color(0.46f, 0.3f, 0.16f), 0.12f, 30).SrgbToLinear();
            _sleeve = _tunic;
        }
        else
        {
            _torsoPart = Part.Torso;
            _sleevePart = Part.Sleeve;
            _torso = _sleeve = _tunic;
        }
        _belt = roman ? Lin(0.62f, 0.46f, 0.22f) : Vary(new Color(0.25f, 0.16f, 0.09f), 0.2f, 33).SrgbToLinear();

        _headTop = armor.Helmet ? (roman ? Part.HelmetRoman : Part.HelmetGaul) : roman ? Part.Hair : Part.HairLong;
        _helmet = roman
            ? (armor.Mail ? Vary(new Color(0.8f, 0.6f, 0.3f), 0.1f, 40) : Vary(new Color(0.62f, 0.62f, 0.64f), 0.08f, 40)).SrgbToLinear()
            : Vary(new Color(0.6f, 0.6f, 0.62f), 0.08f, 40).SrgbToLinear();
        _crestOn = s.Veteran && armor.Helmet;
        _goldOn = s.Veteran && !armor.Helmet;
        _crest = roman ? Lin(0.85f, 0.08f, 0.06f) : Lin(0.95f, 0.93f, 0.86f);
        _gold = Lin(1.0f, 0.76f, 0.3f);
        _blade = Colors.White;
        _wood = Vary(new Color(0.5f, 0.35f, 0.2f), 0.2f, 60).SrgbToLinear();

        var w = s.Weapon;
        _spear = w.Style == WeaponStyle.Spear;
        switch (w.Style)
        {
            case WeaponStyle.Spear:
                _set = PoseSet.Spear;
                _weapon1 = Part.SpearShaft;
                _weapon2 = Part.SpearHead;
                break;
            case WeaponStyle.Axe:
                _set = PoseSet.Axe;
                _weapon1 = Part.AxeHaft;
                _weapon2 = Part.AxeHead;
                break;
            default:
                _set = PoseSet.Sword;
                _weapon1 = w.Length > 0.7f ? Part.Longsword : Part.Gladius;
                _weapon2 = Part.Count;
                break;
        }

        var sh = s.Shield;
        _hasShield = sh != null;
        _board = _boss = Part.Count;
        if (sh != null)
        {
            switch (sh.Shape)
            {
                case ShieldShape.Scutum:
                    _board = Part.Scutum; _boss = Part.BossScutum; _trim = Part.ScutumTrim; _designPart = Part.ScutumDesign;
                    break;
                case ShieldShape.Round:
                    _board = Part.Round; _boss = Part.BossRound; _trim = Part.RoundRim; _designPart = Part.RoundDesign;
                    break;
                default:
                    _board = Part.Oval; _boss = Part.BossOval; _trim = Part.OvalSpine; _designPart = Part.OvalDesign;
                    break;
            }
            if (roman)
            {
                _paint = Vary(new Color(0.7f, 0.1f, 0.08f), 0.1f, 50).SrgbToLinear();
                _design = Lin(0.9f, 0.7f, 0.22f);
            }
            else
            {
                var paint = Pick(GaulShields, 50);
                _paint = Vary(paint, 0.12f, 51).SrgbToLinear();
                var design = Pick(GaulDesigns, 55);
                if (Mathf.Abs(design.R - paint.R) + Mathf.Abs(design.G - paint.G) + Mathf.Abs(design.B - paint.B) < 0.5f)
                    design = GaulDesigns[((int)(H(55) * GaulDesigns.Length) + 1) % GaulDesigns.Length];
                _design = design.SrgbToLinear();
                if (H(56) < 0.25f) _designPart = Part.Count;   // some shields are plain
            }
        }
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>
    /// Pose him from the sim state and write his parts. <paramref name="now"/> is render time in battle seconds.
    /// </summary>
    public void Animate(PartBatch[] batches, float alpha, float simDt, float now, bool emit)
    {
        var s = S;
        var u = s.Unit;
        float fdt = Battle.FixedDt;
        var pos2 = s.PrevPos.Lerp(s.Pos, alpha);
        float yaw = Mathf.LerpAngle(s.PrevFacing, s.Facing, alpha);
        GroundPos = new Vector3(pos2.X, 0f, pos2.Y);
        float phaseT = s.PhaseTime + alpha * fdt;
        if (s.PhaseLength > 0f) phaseT = Mathf.Min(phaseT, s.PhaseLength);

        // ---- locomotion
        float rawSpeed = s.Dead || s.Phase == CombatPhase.Down ? 0f : s.Vel.Length();
        float k10 = 1f - Mathf.Exp(-10f * simDt);
        _speed += (rawSpeed - _speed) * k10;
        if (rawSpeed > 0.15f)
        {
            var l = MathUtil.ToLocal(s.Vel, s.Facing) / rawSpeed;
            var want = new Vector3(-l.X, 0f, l.Y);
            _moveLocal = _moveLocal.Lerp(want, k10).Normalized();
            if (_moveLocal.LengthSquared() < 0.5f) _moveLocal = want;
        }
        _walkPhase += _speed * simDt / (1.3f + 0.18f * _speed) * Mathf.Tau;
        if (_walkPhase > 1000f) _walkPhase -= 150f * Mathf.Tau;
        float moving = Clamp01(_speed / 0.6f);

        bool alertNow = !s.Fleeing && !s.Dead && (s.Target != null || s.Charging || u.InMelee || u.Order == OrderType.Attack
                                                  || s.Phase != CombatPhase.Ready || StandTo);
        if (!_hasPose)
        {
            // First sight of him (the battle may have been fast-forwarded): no fade-in from a default state.
            _alert = alertNow ? 1f : 0f;
            _speed = rawSpeed;
        }
        _alert = Mathf.MoveToward(_alert, alertNow ? 1f : 0f, simDt * 3f);
        float alert = SmoothStep01(_alert);

        // ---- the man he is fighting, how hard he is at it, and what it has cost him
        var t = s.Target;
        bool live = !s.Dead && !s.Fleeing;
        float distT = 99f, relT = 0f;
        if (t != null && t.Active && live)
        {
            var to = t.Pos - s.Pos;
            distT = to.Length();
            relT = MathUtil.RelativeAngle(s.Facing, to);
        }
        else t = null;
        bool close = t != null && distT < s.Weapon.Reach + 1.3f;
        // A man in a rear rank with nobody in reach stands ready behind his file, but calmer.
        bool supporting = live && !close && alertNow && SoldierAI.Supporting(s);
        float engageNow = close ? 1f : alertNow ? (supporting ? 0.4f : 0.3f) : 0f;
        if (!_hasPose) _engage = engageNow;
        _engage = Mathf.MoveToward(_engage, engageNow, simDt * 2.5f);
        float en = SmoothStep01(_engage);
        float tired = s.Dead ? 0f : 1f - SmoothStep01((s.StaminaFrac - 0.08f) / 0.62f);
        _tired = tired;
        float hurtNow = s.Dead ? 0f : 1f - SmoothStep01((s.HpFrac - 0.25f) / 0.55f);
        _hurt = _hasPose ? Mathf.MoveToward(_hurt, hurtNow, simDt * 0.8f) : hurtNow;
        _sway += simDt * _swayRate * (0.5f + 1.1f * en);
        _breath += simDt * Mathf.Lerp(1.5f, 4.6f, tired) * (0.85f + 0.3f * en);
        if (_sway > 4000f) _sway -= 600f * Mathf.Tau;   // 600 turns: the 0.61 and 1.43 harmonics wrap whole too
        if (_breath > 4000f) _breath -= 600f * Mathf.Tau;

        // ---- target pose
        var set = _set;
        var p = SoldierPose.Lerp(set.Rest, set.Guard, alert);
        var left = _hasShield ? LeftArm.Lerp(PoseSet.ShieldRest, PoseSet.ShieldGuard, alert) : PoseSet.NoShield;
        p.SetLeft(left);

        // Marching: arms swing.
        float sw = Mathf.Sin(_walkPhase);
        p.HandR.Z += 0.07f * sw * moving * (1f - alert);
        p.HandL.Z -= 0.04f * sw * moving * (1f - alert);

        // A fighting man is never still. His weight goes from foot to foot and he rocks onto the front foot and back;
        // his shoulders work; blade and shield follow the man in front of him.
        if (en > 0.01f && live)
        {
            float s1 = Mathf.Sin(_sway + _ph0), s2 = Mathf.Sin(0.61f * _sway + _ph1), s3 = Mathf.Sin(1.43f * _sway + _ph2);
            float s4 = Mathf.Sin(2.3f * _sway + _ph3), s5 = Mathf.Sin(1.9f * _sway + _ph0 + _ph2);
            float amp = en * _fidget * (1f - 0.35f * tired);
            p.HipX += 0.05f * amp * s1;
            p.HipZ += 0.035f * amp * s2;
            p.Side += 0.06f * amp * s1;
            p.Twist += 0.1f * amp * s3;
            p.Lean += en * 0.04f + 0.04f * amp * s2;
            // Knees working: he never locks them, a heavy man less than a light one.
            p.Crouch += en * _crouchBias + 0.022f * amp * (0.6f * s4 + 0.5f * s2 + 0.5f * s1 * s1);
            // The point of his weapon is never still: it circles, looking for a way in (a spear's much less, it is long).
            float tip = _spear ? 0.4f : 1f;
            p.HandR += new Vector3(0.025f * s3, 0.03f * s5, 0.035f * s1) * amp;
            p.Elev += 0.11f * amp * tip * s4;
            p.Azim += 0.12f * amp * tip * s5;
            p.HandL += new Vector3(0.012f * s1, 0.02f * s3, 0.02f * s2) * amp;
            p.ShYaw += 0.06f * amp * s5;
            // He changes his guard now and then, the point higher at the face or lower at the belly.
            float gh = Mathf.Sin(0.29f * _sway + 2f * _ph1) * en;
            p.HandR.Y += 0.06f * gh;
            p.Elev -= (_spear ? 0.06f : 0.2f) * gh;
            p.HandL.Y += 0.025f * gh;
            if (t != null)
            {
                float rel = Mathf.Clamp(relT, -0.9f, 0.9f);
                p.Azim += en * rel * (_spear ? 0.85f : 0.6f);
                p.ShYaw += en * rel * 0.45f;
                p.HandL.X += en * 0.05f * rel;
                // The point goes where he means to put it: lower as the other man closes; a spear at his chest.
                p.Elev += en * (_spear ? Mathf.Clamp(0.12f - 0.08f * distT, -0.15f, 0.05f) : Mathf.Clamp(0.25f * (distT - 1.1f), -0.15f, 0.12f));
            }
            // Between the real blows, the small business of a fight that is never thrown home: a flick of the point,
            // a nudge of the shield, a dip of the shoulder, a half step in and back, a look over the rim. A man waiting
            // behind his file does the same, smaller. Not while a blow is coming at him: then his guard goes to meet it.
            bool threatened = t != null && t.Phase == CombatPhase.Windup && t.StrikeTarget == s;
            if (s.Phase == CombatPhase.Ready && (close || supporting) && !threatened && now >= s.BreatherUntil)
                Beat(ref p, now, en * (1f - 0.5f * tired));
        }
        // Breathing: deep and quick when he is blown, the shoulders heaving.
        if (!s.Dead)
        {
            float br = Mathf.Sin(_breath + _ph3) * (0.35f + 1.6f * tired);
            p.HandR.Y += 0.01f * br;
            p.HandL.Y += 0.01f * br;
            p.Lean -= 0.015f * br;
            p.Head -= 0.04f * br * tired;
            p.Crouch -= 0.006f * br;
        }

        // A holding line with a charge coming at it sets its feet behind the shields; spearmen ground their spears.
        bool braceNow = StandTo && u.Order == OrderType.Hold && s.Phase == CombatPhase.Ready && !s.Charging && !s.Fleeing && !s.Dead
                        && (s.Target == null || s.Target.Charging);
        _brace = Mathf.MoveToward(_brace, braceNow ? 1f : 0f, simDt * 4f);
        if (_brace > 0f)
        {
            float bw = SmoothStep01(_brace);
            p = SoldierPose.LerpRight(p, set.Brace, bw);
            if (_hasShield)
            {
                left = LeftArm.Lerp(left, PoseSet.ShieldBrace, bw);
                p.SetLeft(left);
            }
        }

        float rate = 14f * (1f - 0.4f * tired);
        float fallAngle = 0f, crouchExtra = 0f, lift = 0f, yawExtra = 0f;
        Vector2 fallDir = Vector2.Zero;

        if (s.Dead)
        {
            if (_deathFrom < 0f) _deathFrom = _hasPose ? _lastFall : 0f;
            float k = Clamp01((now - s.DeathTime) / _fallDur);
            p = PoseSet.Dead;
            p.SetLeft(_hasShield ? PoseSet.ShieldDead : PoseSet.NoShield);
            rate = 9f;
            float ease = k * k;
            if (_deathFrom > 0.2f)
            {
                // Killed on the ground (or halfway up from it): he sinks back the rest of the way where he lies.
                fallAngle = Mathf.Lerp(_deathFrom, _fallMax, ease);
                fallDir = _lastFallDir;
                crouchExtra = Mathf.Lerp(_onFace ? 0.1f : 0.3f, 0.12f, ease);
            }
            else
            {
                fallAngle = _fallMax * ease;
                fallDir = s.DeathDir.Rotated(_deathSpin);
                // Twist as he goes down so he lands square on his back (struck from the front) or his face (from behind):
                // his arms and shield then lie flat whichever way the blow came.
                bool onFace = fallDir.Dot(MathUtil.Dir(s.Facing)) > 0.25f;
                float landYaw = MathUtil.Angle(onFace ? fallDir : -fallDir) + _deathTwist;
                crouchExtra = 0.3f * SmoothStep01(k / 0.45f) * (1f - 0.6f * SmoothStep01((k - 0.45f) / 0.55f));
                yawExtra = MathUtil.AngleDiff(yaw, landYaw) * SmoothStep01(k * 1.4f);
            }
            if (k >= 1f && !_landed)
            {
                _landed = true;
                JustLanded = now - s.DeathTime < _fallDur + 0.3f;
                LandDir = fallDir;
            }
            if (!_hasPose) { _cur = p; _hasPose = true; }
        }
        else if (s.Fleeing)
        {
            p = PoseSet.Flee;
            p.SetLeft(_hasShield ? PoseSet.ShieldFlee : PoseSet.NoShield);
            p.HandR.Z += 0.16f * sw * moving;
            p.HandR.Y += 0.04f * Mathf.Abs(sw) * moving;
            p.HandL.Z -= 0.1f * sw * moving;
            if (_spear) { p.Elev = 0.9f; p.Azim = 0f; }
            p.Lean += 0.1f * Clamp01((_speed - 2f) / 2f);
            p.Head -= p.Lean * 0.6f;
        }
        else
        {
            // ---- reading the blow coming at him: his guard goes to meet it before it lands
            float react = 0f;
            if (t != null && t.Phase == CombatPhase.Windup && t.StrikeTarget == s && Mathf.Abs(relT) < 1.5f)
            {
                float k = t.PhaseLength > 0f ? Clamp01((t.PhaseTime + alpha * fdt) / t.PhaseLength) : 1f;
                float lag = 0.5f - 0.3f * s.Skill;   // a good man reads it sooner
                react = SmoothStep01((k - lag) / (0.9f - lag)) * (t.Feint ? 0.85f : t.Probe ? 0.6f : 1f);
                _anticKind = t.Blow;
            }
            // For a moment his guard stays where the last blow he stopped took it.
            float ga = now - s.GuardLineTime;
            if (ga >= 0f && ga < 0.6f && 0.4f * (1f - ga / 0.6f) > react)
            {
                react = 0.4f * (1f - ga / 0.6f);
                _anticKind = s.GuardLine;
            }
            // Bitten by a feint: his guard goes after it, further than it should, and comes back late.
            float fb = now - s.LastFeintBitTime;
            float bit = fb >= 0f && fb < 0.55f ? (fb < 0.07f ? fb / 0.07f : 1f - SmoothStep01((fb - 0.22f) / 0.33f)) : 0f;
            if (bit > 0f)
            {
                react = Mathf.Max(react, 1.25f * bit);
                _anticKind = s.GuardLine;
            }

            switch (s.Phase)
            {
                case CombatPhase.Ready when s.Feint && phaseT < 0.35f:
                {
                    // A feint pulled: the arm snaps back to his guard and his weight rocks back off the front foot.
                    float k = Mathf.Sin(Mathf.Pi * phaseT / 0.35f);
                    p.Lean -= 0.05f * k;
                    p.HipZ -= 0.035f * k;
                    p.Twist -= 0.1f * k;
                    rate = 22f;
                    break;
                }
                case CombatPhase.Windup:
                {
                    int ki = (int)s.Blow;
                    float len = Mathf.Max(0.05f, s.PhaseLength);
                    bool bash = s.Blow == BlowKind.Bash && _hasShield;
                    // A probe is only a jab from the guard, drawn back a little and put in half way.
                    float reach = s.Probe ? 0.45f : 1f;
                    var wind = SoldierPose.LerpRight(p, set.Winds[ki], s.Probe ? 0.35f : 1f);
                    if (s.Feint)
                    {
                        // A feint is sold hard: the arm goes all the way back, faster than for a real blow, and the front
                        // foot stamps in; then he pulls it (Ready, above) before it would have come.
                        float e = EaseOut(phaseT / (Combat.FeintPullback * len), 2f);
                        p = SoldierPose.LerpRight(p, wind, e);
                        p.StepL += 0.09f * e;
                        p.HipZ += 0.03f * e;
                        p.Lean += 0.05f * e;
                        if (_hasShield) p.SetLeft(LeftArm.Lerp(p.Left, PoseSet.ShieldAttack, 0.7f * e));
                        rate = 30f;
                        break;
                    }
                    // Most of the windup is drawing back (quick to lift, slower as it loads, never quite still); the last
                    // instant is the swing itself, so that the blade arrives just as the blow lands.
                    float swing = s.Blow switch
                    {
                        BlowKind.Thrust or BlowKind.Bash => Mathf.Clamp(0.25f * len, 0.07f, 0.12f),
                        _ => Mathf.Clamp(0.3f * len, 0.08f, 0.16f),
                    };
                    float chamber = len - swing;
                    if (phaseT < chamber)
                    {
                        float c = phaseT / chamber;
                        float e = 0.92f * EaseOut(c, 2.2f) + 0.1f * c;
                        p = SoldierPose.LerpRight(p, wind, e);
                        if (_hasShield)
                            p.SetLeft(bash ? LeftArm.Lerp(p.Left, PoseSet.ShieldBashWind, Mathf.Min(1f, e))
                                : LeftArm.Lerp(p.Left, PoseSet.ShieldAttack, Mathf.Min(1f, e) * alert * (s.Probe ? 0.3f : 0.8f)));
                        rate = 26f;
                    }
                    else
                    {
                        float k = Clamp01((phaseT - chamber) / swing);
                        float w = k * k;   // gathering speed all the way in
                        var from = SoldierPose.LerpRight(p, wind, 1.02f);
                        var hit = SoldierPose.LerpRight(p, set.Contacts[ki], reach);
                        p = SoldierPose.LerpRight(from, hit, w);
                        if (_hasShield)
                            p.SetLeft(bash ? LeftArm.Lerp(PoseSet.ShieldBashWind, PoseSet.ShieldBashStrike, w)
                                : LeftArm.Lerp(p.Left, PoseSet.ShieldAttack, alert * (s.Probe ? 0.3f : 0.8f)));
                        rate = 70f;
                    }
                    break;
                }
                case CombatPhase.Recover:
                {
                    int ki = (int)s.Blow;
                    float len = Mathf.Max(0.05f, s.PhaseLength);
                    float reach = s.Probe ? 0.45f : 1f;
                    bool bash = s.Blow == BlowKind.Bash && _hasShield;
                    var hit = SoldierPose.LerpRight(p, set.Contacts[ki], reach);
                    // Stopped on a shield or a blade, the blow jars back up the way it came; otherwise it carries on through.
                    bool stopped = now - StoppedTime <= phaseT + 0.05f;
                    bool missed = !stopped && now - MissedTime <= phaseT + 0.05f && !s.Probe;
                    var end = stopped ? SoldierPose.LerpRight(hit, set.Winds[ki], 0.2f * reach)
                        : SoldierPose.LerpRight(p, set.Strikes[ki], s.Probe ? 0.5f : missed ? 1.12f : 1f);
                    if (missed)
                    {
                        // Swung at air: it carries him over his front foot and he has to catch himself.
                        float lurch = Mathf.Sin(Mathf.Pi * Clamp01(phaseT / Mathf.Min(len, 0.45f)));
                        end.Lean += 0.1f * lurch;
                        end.HipZ += 0.04f * lurch;
                        end.StepL += 0.08f * lurch;
                        end.Head += 0.08f * lurch;
                    }
                    float follow = stopped ? 0.1f : Mathf.Min(0.16f, 0.4f * len);
                    if (phaseT < follow)
                    {
                        float k = EaseOut(phaseT / follow, 2f);
                        p = SoldierPose.LerpRight(hit, end, k);
                        if (_hasShield) p.SetLeft(bash ? PoseSet.ShieldBashStrike : LeftArm.Lerp(p.Left, PoseSet.ShieldAttack, alert * 0.8f));
                        rate = 60f;
                    }
                    else
                    {
                        // Back to his guard, the shield first: a tired man is slow to start it.
                        float r = Clamp01((phaseT - follow) / Mathf.Max(0.05f, len - follow));
                        float k = SmoothStep01(Mathf.Pow(r, 1f + 0.8f * tired));
                        p = SoldierPose.LerpRight(end, p, k);
                        if (_hasShield)
                            p.SetLeft(LeftArm.Lerp(bash ? PoseSet.ShieldBashStrike : LeftArm.Lerp(p.Left, PoseSet.ShieldAttack, alert * 0.8f), p.Left, SmoothStep01(r * 1.6f)));
                        rate = 26f * (1f - 0.3f * tired);
                    }
                    break;
                }
                case CombatPhase.Stagger:
                {
                    if (_wasDown)
                    {
                        // Getting up after being knocked flat: he sits up first, then gets his feet under him.
                        // Timed from when he started, so a blow that staggers him on the way doesn't put him back down.
                        if (_upStart < 0f)
                        {
                            _upStart = now - phaseT;
                            _upLen = Mathf.Max(0.2f, s.PhaseLength);
                        }
                        float k = Clamp01((now - _upStart) / _upLen);
                        fallAngle = 1.45f * (1f - SmoothStep01(k));
                        fallDir = _fallDir;
                        crouchExtra = (_onFace ? 0.1f : 0.3f) * (1f - SmoothStep01((k - 0.3f) / 0.7f));
                        var lying = Lying();
                        p = SoldierPose.Lerp(lying, p, k);
                        p.SetLeft(LeftArm.Lerp(lying.Left, left, k));
                        p.Lean += (_onFace ? -0.6f : 0.6f) * Mathf.Sin(Mathf.Pi * Mathf.Min(1f, k / 0.8f));
                        rate = 20f;
                        if (k >= 1f) _wasDown = false;
                    }
                    else
                    {
                        float w = Clamp01(phaseT / 0.1f) * Clamp01((s.PhaseLength - phaseT) / 0.18f + 0.15f);
                        var loose = PoseSet.Loose;
                        loose.Side = 0.12f * Mathf.Sin(_seed * 3f);
                        p = SoldierPose.Lerp(p, loose, w);
                        if (_hasShield) p.SetLeft(LeftArm.Lerp(left, PoseSet.ShieldLoose, w));
                    }
                    break;
                }
                case CombatPhase.Down:
                {
                    if (_lastPhase != CombatPhase.Down || !_hasPose)
                    {
                        // Flat on his back, or on his face if the charge hit him from behind. Knocked over again
                        // while still getting up, he goes back down from where he is.
                        if (!_wasDown)
                        {
                            var fwd = MathUtil.Dir(s.Facing);
                            _onFace = ImpactPush.Dot(fwd) > 0.3f;
                            _fallDir = _onFace ? fwd : -fwd;
                        }
                        _wasDown = true;
                        _downFrom = _hasPose ? _lastFall : 1.45f;
                        _upStart = -1f;
                        _landedDown = false;
                    }
                    float k = Clamp01(phaseT / 0.3f);
                    fallAngle = Mathf.Lerp(_downFrom, 1.45f, k * k);
                    fallDir = _fallDir;
                    // His knees buckle as he goes over and stay drawn up while he lies there, shield over him.
                    crouchExtra = (_onFace ? 0.1f : 0.3f) * SmoothStep01(k / 0.6f);
                    p = Lying();
                    rate = 18f;
                    if (k >= 1f && !_landedDown)
                    {
                        _landedDown = true;
                        JustLanded = phaseT < 0.6f;
                        LandDir = _fallDir;
                    }
                    break;
                }
            }
            if (s.Phase != CombatPhase.Down && s.Phase != CombatPhase.Stagger) _wasDown = false;
            bool onFeet = s.Phase is not (CombatPhase.Down or CombatPhase.Stagger);

            // His shield (or, without one, his blade) goes to the line the blow is coming in on; he sets his feet for it.
            if (react > 0f && onFeet)
            {
                float mulL = s.Phase switch { CombatPhase.Ready => 1f, CombatPhase.Windup => 0.5f, _ => 0.6f };
                float mulR = s.Phase switch { CombatPhase.Ready => 1f, CombatPhase.Windup => 0.12f, _ => 0.3f };
                if (_hasShield) p.SetLeft(LeftArm.Lerp(p.Left, PoseSet.ShieldMeets[(int)_anticKind], react * mulL));
                float wr = Mathf.Min(1f, react) * mulR * (_hasShield ? 0.3f : 0.85f);
                if (wr > 0f) p = SoldierPose.LerpRight(p, set.ParryFor(_anticKind), wr);
                float wb = Mathf.Min(1f, react) * mulL;
                p.Crouch += (_anticKind == BlowKind.Low ? 0.08f : 0.04f) * wb;
                if (_anticKind == BlowKind.Bash) { p.Lean += 0.12f * wb; p.Twist -= 0.12f * wb; }
                if (_anticKind == BlowKind.Overhead) p.Head += 0.12f * wb;
                rate = Mathf.Max(rate, 16f + 12f * s.Skill);
            }
            if (bit > 0f && onFeet)
            {
                // The feint drew his whole body after it for a moment.
                p.Twist += 0.14f * bit * (_anticKind == BlowKind.Backhand ? -1f : 1f);
                p.Lean -= 0.05f * bit;
                p = SoldierPose.LerpRight(p, set.ParryFor(_anticKind), 0.5f * bit);
                rate = Mathf.Max(rate, 26f);
            }

            // Charging: a battle cry, blade up (spears stay levelled).
            if (s.Charging && _speed > 2.2f && s.Phase == CombatPhase.Ready)
            {
                p = SoldierPose.LerpRight(p, set.Raised, 0.75f);
            }

            // Shield punched out at a blow he turned (knocked back a hand's breadth as it lands), or the blade put across it.
            float ba = now - s.LastBlockTime;
            if (ba >= 0f && ba < 0.34f)
            {
                float w = ba < 0.05f ? ba / 0.05f : 1f - SmoothStep01((ba - 0.13f) / 0.21f);
                if (s.LastBlockWasShield && _hasShield)
                {
                    float rel = Mathf.Clamp(BlockRel, -1.2f, 1.2f);
                    ref readonly var meet = ref PoseSet.ShieldMeets[(int)BlockKind];
                    float jolt = ba < 0.12f ? Mathf.Sin(ba / 0.12f * Mathf.Pi) : 0f;
                    var punch = new LeftArm(meet.Hand + new Vector3(0.1f * Mathf.Sin(rel), 0.02f, 0.08f - 0.05f * Mathf.Abs(rel) - 0.09f * jolt),
                        meet.Pole, meet.Yaw + Mathf.Clamp(rel * 0.6f, -0.7f, 0.7f), meet.Pitch + 0.06f);
                    p.SetLeft(LeftArm.Lerp(p.Left, punch, w));
                    p.Twist = Mathf.Lerp(p.Twist, -0.25f + 0.3f * rel, w * 0.5f);
                    p.Lean -= 0.07f * jolt;
                }
                else
                {
                    p = SoldierPose.LerpRight(p, set.ParryFor(BlockKind), w);
                }
                rate = Mathf.Max(rate, 30f);
            }

            // Rocked back by a blow, away from the man who struck it: the harder the blow, the further.
            float ha = now - s.LastHitTime;
            float heavy = Mathf.Clamp(HitAmount / 30f, 0.5f, 1.5f);
            float hitLen = 0.3f + 0.15f * heavy;
            if (ha >= 0f && ha < hitLen)
            {
                float w = ha < 0.05f ? ha / 0.05f : 1f - SmoothStep01((ha - 0.05f) / (hitLen - 0.05f));
                var push = HitPush.LengthSquared() > 0.01f ? HitPush : -MathUtil.Dir(s.Facing);
                var lp = MathUtil.ToLocal(push, s.Facing);   // x = his right, y = forward
                p.Lean += (0.32f * Mathf.Clamp(lp.Y, -1f, 1f) - 0.05f) * w * heavy;
                p.Side += 0.25f * w * heavy * Mathf.Clamp(lp.X, -1f, 1f);
                p.Head -= 0.3f * w;
                p.HipZ += 0.05f * w * heavy * Mathf.Clamp(lp.Y, -1f, 1f);
                p.HandL.X += 0.06f * w * heavy;
                p.HandR.Y -= 0.05f * w * heavy;
                p.Crouch += 0.04f * w * heavy;
                rate = Mathf.Max(rate, 26f);
            }

            // A blow just past him: he flinches back from it.
            float ma = now - MissTime;
            if (onFeet && ma >= 0f && ma < 0.35f)
            {
                float w = ma < 0.05f ? ma / 0.05f : 1f - SmoothStep01((ma - 0.05f) / 0.3f);
                var lp = MathUtil.ToLocal(MissFrom, s.Facing);
                p.Lean -= 0.15f * w;
                p.Head -= 0.2f * w;
                p.Side -= 0.1f * w * Mathf.Clamp(lp.X, -1f, 1f);
                p.HipZ -= 0.04f * w;
                p.HandL.Y += 0.05f * w;
                rate = Mathf.Max(rate, 26f);
            }

            // Shoved back by a shield punch: rocked back onto his heels behind his shield, the shield driven into him
            // and the sword arm out for balance, and a step back with the rear foot to catch himself (not a stagger:
            // his guard is still there, just knocked off line for a moment).
            float sb = now - s.LastShovedTime;
            if (onFeet && sb >= 0f && sb < 0.6f)
            {
                float w = sb < 0.05f ? sb / 0.05f : 1f - SmoothStep01((sb - 0.1f) / 0.5f);
                p.Lean -= 0.22f * w;
                p.Head -= 0.15f * w;
                p.HandL.Z -= 0.1f * w;
                p.ShPitch -= 0.15f * w;
                p.ShYaw += 0.2f * w;
                p.HandR += new Vector3(-0.08f, 0.1f, -0.05f) * w;
                p.HipZ -= 0.06f * w;
                p.Crouch += 0.03f * w;
                float step = sb < 0.35f ? 1f : 1f - SmoothStep01((sb - 0.35f) / 0.25f);
                p.StepR -= 0.16f * step;
                p.StepL -= 0.05f * step;
                rate = Mathf.Max(rate, 26f);
            }

            // A charge crashing home: the man it hits is rocked back behind his shield, the man who hits him drives in.
            float sa = now - ShoveTime;
            if (onFeet && sa >= 0f && sa < 0.45f)
            {
                float w = (sa < 0.05f ? sa / 0.05f : 1f - SmoothStep01((sa - 0.05f) / 0.4f)) * Mathf.Clamp(Shove, 0.4f, 1f);
                var lp = MathUtil.ToLocal(ImpactPush, s.Facing);
                p.Lean += 0.3f * w * Mathf.Clamp(lp.Y, -1f, 1f);
                p.Side += 0.2f * w * Mathf.Clamp(lp.X, -1f, 1f);
                p.Crouch += 0.06f * w;
                p.HandL.Z -= 0.06f * w;
                p.Head -= 0.2f * w;
                rate = Mathf.Max(rate, 26f);
            }
            float ra = now - RamTime;
            if (onFeet && ra >= 0f && ra < 0.35f)
            {
                float w = ra < 0.04f ? ra / 0.04f : 1f - SmoothStep01((ra - 0.04f) / 0.31f);
                p.Lean += 0.2f * w;
                p.Twist = Mathf.Lerp(p.Twist, -0.3f, 0.6f * w);   // shield shoulder first
                p.Crouch += 0.05f * w;
                rate = Mathf.Max(rate, 26f);
            }

            // A footwork step: he pushes off, knees dipping and the hips going first the way he steps.
            float fs = now - s.LastStepTime;
            if (onFeet && fs >= 0f && fs < 0.32f && s.Phase is CombatPhase.Ready or CombatPhase.Recover)
            {
                float w = Mathf.Sin(Mathf.Pi * fs / 0.32f) * Clamp01(_speed / 0.4f);
                p.Crouch += 0.03f * w;
                p.HipX += 0.035f * w * _moveLocal.X;
                p.HipZ += 0.035f * w * _moveLocal.Z;
                p.Lean += 0.04f * w * _moveLocal.Z;
            }

            // Backing out of the fight (giving ground, or going back through his file to be relieved), or squeezing up
            // past the man he relieves: shield shoulder to the enemy and the shield kept up, never his back.
            bool backing = _speed > 0.25f && _moveLocal.Z < -0.35f && alert > 0.5f;
            bool swapping = now - s.ReliefTime < 2.5f && _speed > 0.2f;
            _retire = Mathf.MoveToward(_retire, onFeet && (backing || swapping) ? 1f : 0f, simDt * 3f);
            if (_retire > 0f)
            {
                float w = SmoothStep01(_retire) * (s.Phase == CombatPhase.Ready ? 1f : 0.3f);
                p.Twist = Mathf.Lerp(p.Twist, -0.45f, 0.7f * w);
                p.Lean -= 0.04f * w;
                if (swapping) p.Side -= 0.08f * w * s.SwapSide;
                if (_hasShield) p.SetLeft(LeftArm.Lerp(p.Left, PoseSet.ShieldGuard, 0.5f * w));
            }

            // Blown: stepped back for a breather, or just spent between exchanges. The point goes down and the shield
            // sags; a man who is done in all but rests its rim on the ground.
            bool threatened = react > 0.15f || (t != null && t.Phase == CombatPhase.Windup);
            bool resting = s.Phase == CombatPhase.Ready && !threatened
                           && (now < s.BreatherUntil || (s.StaminaFrac < 0.15f && now - s.LastActionTime > 0.7f));
            _rest = Mathf.MoveToward(_rest, resting ? 1f : 0f, simDt * (resting ? 1.5f : 5f));
            if (_rest > 0f)
            {
                float w = SmoothStep01(_rest);
                float spent = 1f - SmoothStep01((s.StaminaFrac - 0.03f) / 0.2f);
                // Shifted rather than set, so that he still sways and heaves while he stands there.
                SoldierPose.ShiftRight(ref p, set.Guard, set.Spent, w * (0.4f + 0.6f * spent));
                if (_hasShield) SoldierPose.ShiftLeft(ref p, PoseSet.ShieldGuard, LeftArm.Lerp(PoseSet.ShieldDown, PoseSet.ShieldGrounded, spent), w);
                p.Lean += 0.08f * w * (0.5f + spent);
                p.Head += 0.15f * w * spent;
                p.Crouch += 0.04f * w;
            }

            // Tired: the guard drops and the shield sags open (he still lifts them to meet a blow), the shoulders slump.
            float drop = tired * (s.Phase == CombatPhase.Ready ? 1f : 0.4f) * (1f - 0.6f * Mathf.Min(1f, react));
            if (drop > 0f)
            {
                p.HandR.Y -= 0.09f * drop;
                p.HandR.Z -= 0.04f * drop;
                p.Elev -= (_spear ? 0.12f : 0.35f) * drop;
                p.HandL.Y -= 0.1f * drop;
                p.HandL.Z -= 0.05f * drop;
                p.ShPitch += 0.14f * drop;
                p.ShYaw += 0.12f * drop;
            }
            // Blown: bent over his shield, head hanging, knees sagging and the feet set wider under the weight (see PlaceFeet).
            p.Lean += 0.16f * tired;
            p.Head += 0.17f * tired;
            p.Crouch += 0.04f * tired;
            // Wounded: hunched over the hurt side, favouring the leg on the other.
            if (_hurt > 0f)
            {
                p.Lean += 0.1f * _hurt;
                p.Side += 0.09f * _hurt * _hurtSide;
                p.Crouch += 0.035f * _hurt;
                p.HandL.Y -= 0.04f * _hurt;
                p.HipX += 0.025f * _hurt * _hurtSide;
            }

            p.Lean += 0.14f * Clamp01((_speed - 2f) / 2f);

            // Pressed up against an enemy, he draws his shield in so that the two boards meet rather than pass through
            // each other: each man gets half the room between them (all of it if the other has no shield toward him).
            if (_hasShield && FrontRoom < 0.9f && !_wasDown)
            {
                var torso = MeshBuilder.RotX(p.Lean) * MeshBuilder.RotZ(p.Side) * MeshBuilder.RotY(p.Twist);
                float reach = (torso * p.HandL).Z + p.HipZ + 0.1f + 0.3f * Mathf.Abs(Mathf.Sin(p.ShYaw));
                float over = reach - (FrontRoom - 0.02f);
                if (over > 0f) p.HandL.Z = Mathf.Max(0.16f, p.HandL.Z - over);
            }
        }

        if (!_hasPose)
        {
            _cur = p;
            _hasPose = true;
        }
        else
        {
            _cur = SoldierPose.Lerp(_cur, p, 1f - Mathf.Exp(-rate * simDt));
        }

        _lastPhase = s.Phase;
        _lastFall = fallAngle;
        if (fallAngle > 0f) _lastFallDir = fallDir;

        float flash = 0f;
        float wa = now - WoundTime;
        if (wa >= 0f && wa < 0.14f) flash = 1f - wa / 0.14f;

        HeadHeight = (1.78f - (_cur.Crouch + crouchExtra)) * _scale * Mathf.Cos(fallAngle);
        Visible = emit;
        if (!emit || s.Escaped)
        {
            _feetValid = false;   // re-planted where he stands when he comes back into view
            return;
        }
        // A corpse that has come to rest never moves again: pose him one last time, then replay the result.
        bool settled = s.Dead && now - s.DeathTime > _fallDur + 1.2f && simDt > 0f;
        if (_frozen != null)
        {
            foreach (var f in _frozen) batches[(int)f.Part].Add(f.Xf, f.Color);
            return;
        }
        if (settled) _frozen = new List<Frozen>(28);
        bool upright = !s.Dead && fallAngle < 1e-3f && s.Phase != CombatPhase.Down && !_wasDown;
        PlaceFeet(pos2, yaw, simDt, en, upright, now - s.LastShovedTime < 0.6f || now - s.LastHitTime < 0.3f);
        Emit(batches, pos2, yaw + yawExtra, fallAngle, fallDir, crouchExtra, lift, flash);
    }

    /// <summary>
    /// The small moves a man makes between real blows, on his own rhythm (a second or so apart, in step with nobody):
    /// each a short pulse on top of his guard, quick out and slower back.
    /// </summary>
    void Beat(ref SoldierPose p, float now, float amount)
    {
        float cyc = (now + _ph3 * 3f) / _beatEvery;
        float k = Mathf.Floor(cyc);
        float f = (cyc - k) * _beatEvery;   // seconds into this cycle
        int ik = (int)k;
        float dur = 0.34f + 0.16f * Hash(S.Id, ik * 2 + 1);
        float h = Hash(S.Id, ik * 2);
        if (f >= dur || h > 0.88f) return;   // now and then he lets one go by
        float x = f / dur;
        float w = amount * (x < 0.3f ? SmoothStep01(x / 0.3f) : 1f - SmoothStep01((x - 0.3f) / 0.7f));
        switch ((int)(h / 0.88f * 6f))
        {
            case 0:   // the point flicked at him, the weight onto the front foot
                p.HandR.Z += (_spear ? 0.14f : 0.1f) * w;
                p.HandR.Y += 0.02f * w;
                p.Elev -= (_spear ? 0.03f : 0.12f) * w;
                p.StepL += 0.05f * w;
                p.Lean += 0.04f * w;
                break;
            case 1:   // the shield nudged out at him
                p.HandL.Z += 0.08f * w;
                p.ShPitch -= 0.08f * w;
                p.Twist -= 0.1f * w;
                p.StepL += 0.04f * w;
                p.HipZ += 0.02f * w;
                break;
            case 2:   // a dip of the weapon shoulder, as if to cut
                p.Twist -= 0.2f * w;
                p.Lean += 0.07f * w;
                p.Crouch += 0.04f * w;
                p.HandR.Y += 0.05f * w;
                p.Elev += (_spear ? 0.05f : 0.25f) * w;
                break;
            case 3:   // the weight back off the front foot, to draw him on
                p.HipZ -= 0.04f * w;
                p.Lean -= 0.06f * w;
                p.StepL -= 0.06f * w;
                p.HandL.Z += 0.03f * w;
                break;
            case 4:   // a look over the rim of his shield
                p.HandL.Y -= 0.06f * w;
                p.Head -= 0.12f * w;
                p.Crouch -= 0.03f * w;
                p.HandR.Y += 0.03f * w;
                break;
            default:  // the point circled round
            {
                float a = Mathf.Tau * x;
                p.Azim += (_spear ? 0.1f : 0.3f) * w * Mathf.Sin(a);
                p.Elev += (_spear ? 0.06f : 0.2f) * w * Mathf.Cos(a);
                p.Roll += 0.4f * w;
                break;
            }
        }
    }

    // ---------------------------------------------------------------- feet

    /// <summary>
    /// Where his feet are this frame. Marching or running they follow the walk cycle. Slower than that (shuffling in
    /// a fight, giving ground, turning on the spot, shifting his weight) each foot stays planted where it is until
    /// the body has moved or turned too far off it, then takes a quick step to where it belongs, one foot at a time.
    /// </summary>
    void PlaceFeet(Vector2 pos2, float yaw, float simDt, float en, bool upright, bool stumbling)
    {
        _feetPlanted = upright;
        if (!upright)
        {
            _feetValid = false;
            return;
        }
        float al = SmoothStep01(_alert);
        float cy = Mathf.Cos(yaw), sy = Mathf.Sin(yaw), sc = _scale;
        // Where each foot belongs under him (root frame): side by side on the march; fighting, the shield foot
        // forward and the back foot turned out, and moved on by any step his blow or guard puts in.
        float wide = _stanceW + 0.035f * _tired;
        var homeL = new Vector3(0.1f, 0f, 0f).Lerp(new Vector3(0.13f + wide, 0f, 0.17f + _stanceL), al);
        var homeR = new Vector3(-0.1f, 0f, 0f).Lerp(new Vector3(-0.13f - wide, 0f, -0.13f - 0.6f * _stanceL), al);
        homeL.Z += _cur.StepL;
        homeR.Z += _cur.StepR;
        float yawL = 0.12f, yawR = -0.12f - 0.35f * al * en;

        // The walk cycle.
        float moving = Clamp01(_speed / 0.6f);
        float a = Mathf.Min(0.1f + _speed * 0.12f, 0.42f);
        float liftAmp = Mathf.Min(0.04f + 0.03f * _speed, 0.15f);
        var walkL = homeL.Lerp(new Vector3(0.1f, 0f, 0f) + _moveLocal * (a * Mathf.Sin(_walkPhase)), moving);
        var walkR = homeR.Lerp(new Vector3(-0.1f, 0f, 0f) + _moveLocal * (a * Mathf.Sin(_walkPhase + Mathf.Pi)), moving);
        walkL.Y = liftAmp * Mathf.Max(0f, Mathf.Cos(_walkPhase)) * moving;
        walkR.Y = liftAmp * Mathf.Max(0f, Mathf.Cos(_walkPhase + Mathf.Pi)) * moving;
        float wWalk = SmoothStep01((_speed - 0.5f) / 0.45f);

        Vector2 ToWorld(Vector3 l) => new(pos2.X + (l.X * cy + l.Z * sy) * sc, pos2.Y + (-l.X * sy + l.Z * cy) * sc);
        if (!_feetValid || wWalk > 0.5f)
        {
            // Carried by the walk cycle (or just come into view): the feet stand where it puts them.
            _footL = new Foot { Plant = ToWorld(wWalk > 0.5f ? walkL : homeL), Yaw = yaw + yawL, T = -1f };
            _footR = new Foot { Plant = ToWorld(wWalk > 0.5f ? walkR : homeR), Yaw = yaw + yawR, T = -1f };
            _feetValid = true;
        }
        else if (simDt > 0f)
        {
            var hL = ToWorld(homeL);
            var hR = ToWorld(homeR);
            float dur = stumbling ? 0.16f : Mathf.Lerp(0.3f, 0.2f, Clamp01(_speed / 0.8f));
            // Where the body will be when the foot comes down, and a little past it, so it doesn't have to step again at once.
            var lead = (S.Vel * (dur + 0.1f)).LimitLength(0.3f);
            AdvanceStep(ref _footL, hL + lead, yaw + yawL, simDt / dur);
            AdvanceStep(ref _footR, hR + lead, yaw + yawR, simDt / dur);
            float thresh = Mathf.Lerp(0.16f, 0.09f, en) * (stumbling ? 0.6f : 1f);
            float eL = _footL.T < 0f ? _footL.Plant.DistanceTo(hL) + 0.25f * Mathf.Abs(MathUtil.AngleDiff(_footL.Yaw, yaw + yawL)) : -1f;
            float eR = _footR.T < 0f ? _footR.Plant.DistanceTo(hR) + 0.25f * Mathf.Abs(MathUtil.AngleDiff(_footR.Yaw, yaw + yawR)) : -1f;
            bool lFree = _footR.T < 0f || _footR.T > 0.6f, rFree = _footL.T < 0f || _footL.T > 0.6f;
            if (lFree && eL > thresh && (eL >= eR || !rFree)) StartStep(ref _footL);
            else if (rFree && eR > thresh) StartStep(ref _footR);
        }

        float stepLift = 0.05f + 0.05f * Clamp01(_speed) + (stumbling ? 0.03f : 0f);
        _footPosL = FootLocal(_footL, pos2, cy, sy, sc, stepLift).Lerp(walkL, wWalk);
        _footPosR = FootLocal(_footR, pos2, cy, sy, sc, stepLift).Lerp(walkR, wWalk);
        _footPosL.Y += AnkleHeight;
        _footPosR.Y += AnkleHeight;
        _footYawL = Mathf.Lerp(MathUtil.AngleDiff(yaw, FootYaw(_footL)), yawL, wWalk);
        _footYawR = Mathf.Lerp(MathUtil.AngleDiff(yaw, FootYaw(_footR)), yawR, wWalk);
    }

    static void StartStep(ref Foot f)
    {
        f.From = f.Plant;
        f.FromYaw = f.Yaw;
        f.T = 0f;
    }

    /// <summary>Carry a step on toward where the foot should come down; plant it when it gets there.</summary>
    static void AdvanceStep(ref Foot f, Vector2 to, float toYaw, float dT)
    {
        if (f.T < 0f) return;
        f.T += dT;
        f.Plant = to;   // while stepping, Plant is where it is going
        f.Yaw = toYaw;
        if (f.T >= 1f) f.T = -1f;
    }

    static float FootYaw(in Foot f) => f.T < 0f ? f.Yaw : f.FromYaw + MathUtil.AngleDiff(f.FromYaw, f.Yaw) * SmoothStep01(f.T);

    static Vector3 FootLocal(in Foot f, Vector2 pos2, float cy, float sy, float sc, float lift)
    {
        var w = f.T < 0f ? f.Plant : f.From.Lerp(f.Plant, SmoothStep01(f.T));
        float dx = w.X - pos2.X, dz = w.Y - pos2.Y;
        float h = f.T < 0f ? 0f : lift * Mathf.Sin(Mathf.Pi * f.T);
        return new Vector3((dx * cy - dz * sy) / sc, h / sc, (dx * sy + dz * cy) / sc);
    }

    /// <summary>Knocked flat: sword arm flung out, head up, shield held over him (or beside him, face down).</summary>
    SoldierPose Lying()
    {
        var p = PoseSet.Loose;
        p.HandR = new Vector3(-0.42f, 0.5f, 0.05f);
        p.Lean = _onFace ? -0.15f : 0.15f;
        p.Head = _onFace ? -0.35f : 0.4f;
        p.SetLeft(!_hasShield ? PoseSet.NoShield : _onFace ? PoseSet.ShieldDead : PoseSet.ShieldDown);
        return p;
    }

    // ---------------------------------------------------------------- skeleton

    struct Frozen
    {
        public Part Part;
        public Transform3D Xf;
        public Color Color;
    }

    List<Frozen> _frozen;

    void Put(PartBatch[] batches, Part part, in Transform3D xf, Color color, float flash = 0f)
    {
        batches[(int)part].Add(xf, color, flash);
        _frozen?.Add(new Frozen { Part = part, Xf = xf, Color = color });
    }

    void Emit(PartBatch[] batches, Vector2 pos2, float yaw, float fallAngle, Vector2 fallDir, float crouchExtra, float lift, float flash)
    {
        ref var p = ref _cur;

        // Root: at his feet, turned to his facing, tipped over about the feet if he is falling.
        var rootBasis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(_scale, _scale, _scale));
        var rootPos = new Vector3(pos2.X, 0f, pos2.Y);
        if (fallAngle > 1e-4f && fallDir.LengthSquared() > 1e-6f)
        {
            var d3 = new Vector3(fallDir.X, 0f, fallDir.Y).Normalized();
            var axis = Vector3.Up.Cross(d3).Normalized();
            rootBasis = new Basis(axis, fallAngle) * rootBasis;
            rootPos.Y += 0.14f * _scale * Mathf.Sin(fallAngle) + lift;
        }
        var root = new Transform3D(rootBasis, rootPos);

        // Walking bob and stance.
        float moving = Clamp01(_speed / 0.6f);
        float crouch = p.Crouch + crouchExtra;
        float bobW = _feetPlanted ? SmoothStep01((_speed - 0.5f) / 0.45f) : moving;
        float bob = 0.035f * bobW * Mathf.Abs(Mathf.Sin(_walkPhase)) * Clamp01(_speed / 2f + 0.3f);
        float pelvisY = PelvisHeight - crouch - bob;

        // ---- legs (root frame)
        float a = Mathf.Min(0.1f + _speed * 0.12f, 0.42f);
        float liftAmp = Mathf.Min(0.04f + 0.03f * _speed, 0.15f);
        float al = SmoothStep01(_alert);
        for (int side = 0; side < 2; side++)
        {
            float sx = side == 0 ? 1f : -1f;   // 0 = left (+X), 1 = right (-X)
            var hip = new Vector3(sx * HipHalf + p.HipX, pelvisY - 0.04f, p.HipZ);
            Vector3 foot;
            float footYaw;
            if (_feetPlanted)
            {
                foot = side == 0 ? _footPosL : _footPosR;
                footYaw = side == 0 ? _footYawL : _footYawR;
            }
            else
            {
                var stance = side == 0 ? new Vector3(0.1f, 0f, 0.0f).Lerp(new Vector3(0.13f, 0f, 0.17f), al)
                                       : new Vector3(-0.1f, 0f, 0.0f).Lerp(new Vector3(-0.13f, 0f, -0.13f), al);
                if (S.Dead) stance = new Vector3(sx * 0.13f, 0f, 0.06f * sx);
                float ph = _walkPhase + (side == 0 ? 0f : Mathf.Pi);
                var walk = new Vector3(sx * 0.1f, 0f, 0f) + _moveLocal * (a * Mathf.Sin(ph));
                foot = stance.Lerp(walk, moving);
                foot.Y = AnkleHeight + liftAmp * Mathf.Max(0f, Mathf.Cos(ph)) * moving;
                footYaw = sx * 0.12f;
            }
            var knee = SolveIK(hip, ref foot, Thigh, Shin, new Vector3(sx * 0.15f, 0f, 1f));
            Put(batches, Part.Thigh, root * Segment(hip, knee, Vector3.Back), _legs, flash);
            Put(batches, Part.Shin, root * Segment(knee, foot, Vector3.Back), _romanLegs ? _skin : _legs, flash);
            Put(batches, Part.Foot, root * new Transform3D(new Basis(Vector3.Up, footYaw), foot), _boots);
        }

        // ---- pelvis and torso
        var pelvis = root * new Transform3D(Basis.Identity, new Vector3(p.HipX, pelvisY, p.HipZ));
        var skirtB = MeshBuilder.RotX(p.Lean * 0.45f) * MeshBuilder.RotZ(p.Side * 0.5f) * MeshBuilder.RotY(p.Twist * 0.3f);
        Put(batches, Part.Skirt, pelvis * new Transform3D(skirtB, Vector3.Zero), _tunic, flash);
        var torso = pelvis * new Transform3D(MeshBuilder.RotX(p.Lean) * MeshBuilder.RotZ(p.Side) * MeshBuilder.RotY(p.Twist), Vector3.Zero);
        Put(batches, _torsoPart, torso, _torso, flash);
        Put(batches, Part.Belt, torso, _belt);

        // ---- head
        var head = torso * new Transform3D(MeshBuilder.RotY(-p.Twist * 0.6f) * MeshBuilder.RotX(p.Head), new Vector3(0f, NeckY, 0f));
        Put(batches, Part.Head, head, _skin, flash);
        bool helmet = _headTop is Part.HelmetRoman or Part.HelmetGaul;
        Put(batches, _headTop, head, helmet ? _helmet : _hair);
        if (_crestOn) Put(batches, Part.Crest, head, _crest);
        if (_goldOn) Put(batches, Part.VetGold, head, _gold);

        // ---- weapon arm
        var handR = p.HandR;
        var elbowR = SolveIK(ShoulderR, ref handR, UpperArm, Forearm, p.PoleR);
        var upperR = torso * Segment(ShoulderR, elbowR, Vector3.Back);
        Put(batches, Part.UpperArm, upperR, _skin, flash);
        Put(batches, _sleevePart, upperR, _sleeve, flash);
        Put(batches, Part.Forearm, torso * Segment(elbowR, handR, Vector3.Back), _skin, flash);
        var weapon = torso * new Transform3D(p.WeaponBasis, handR);
        Put(batches, _weapon1, weapon, _weapon2 != Part.Count ? _wood : _blade);
        if (_weapon2 != Part.Count) Put(batches, _weapon2, weapon, _blade);

        // ---- shield arm
        var handL = p.HandL;
        var elbowL = SolveIK(ShoulderL, ref handL, UpperArm, Forearm, p.PoleL);
        var upperL = torso * Segment(ShoulderL, elbowL, Vector3.Back);
        Put(batches, Part.UpperArm, upperL, _skin, flash);
        Put(batches, _sleevePart, upperL, _sleeve, flash);
        Put(batches, Part.Forearm, torso * Segment(elbowL, handL, Vector3.Back), _skin, flash);
        if (_hasShield)
        {
            var shield = torso * new Transform3D(MeshBuilder.RotY(p.ShYaw) * MeshBuilder.RotX(p.ShPitch), handL);
            Put(batches, _board, shield, _paint);
            Put(batches, _boss, shield, _helmet);
            if (_trim != Part.Count)
                Put(batches, _trim, shield, _trim == Part.OvalSpine ? _paint * 0.8f : _trim == Part.RoundRim ? Lin(0.35f, 0.3f, 0.26f) : Lin(0.78f, 0.6f, 0.3f));
            if (_designPart != Part.Count) Put(batches, _designPart, shield, _design);
        }
    }

    /// <summary>
    /// Two-bone IK: from <paramref name="root"/> reach <paramref name="end"/> (pulled in if out of reach) with segments
    /// of the given lengths, bending toward <paramref name="pole"/>. Returns the middle joint.
    /// </summary>
    public static Vector3 SolveIK(Vector3 root, ref Vector3 end, float l1, float l2, Vector3 pole)
    {
        var d = end - root;
        float len = d.Length();
        float max = l1 + l2 - 0.002f, min = Mathf.Abs(l1 - l2) + 0.05f;
        if (len < 1e-5f) { d = Vector3.Down * min; len = min; end = root + d; }
        else if (len > max) { d *= max / len; len = max; end = root + d; }
        else if (len < min) { d *= min / len; len = min; end = root + d; }
        var dir = d / len;
        float cosA = Mathf.Clamp((l1 * l1 + len * len - l2 * l2) / (2f * l1 * len), -1f, 1f);
        float sinA = Mathf.Sqrt(1f - cosA * cosA);
        var perp = pole - dir * pole.Dot(dir);
        if (perp.LengthSquared() < 1e-6f) perp = dir.Cross(Vector3.Right);
        if (perp.LengthSquared() < 1e-6f) perp = dir.Cross(Vector3.Back);
        perp = perp.Normalized();
        return root + dir * (l1 * cosA) + perp * (l1 * sinA);
    }

    /// <summary>A frame at <paramref name="a"/> whose -Y runs to <paramref name="b"/> (limb meshes hang down -Y).</summary>
    public static Transform3D Segment(Vector3 a, Vector3 b, Vector3 fwdHint)
    {
        var y = a - b;
        float l = y.Length();
        y = l > 1e-6f ? y / l : Vector3.Up;
        var z = fwdHint - y * fwdHint.Dot(y);
        if (z.LengthSquared() < 1e-4f) z = Vector3.Up - y * y.Y;
        if (z.LengthSquared() < 1e-4f) z = Vector3.Right;
        z = z.Normalized();
        var x = y.Cross(z);
        return new Transform3D(new Basis(x, y, z), a);
    }
}
