using System;
using System.Collections.Generic;
using Godot;

namespace UnitFighting.Sim;

public enum OrderType { Hold, Move, Attack }

/// <summary>
/// A reserve relieving its front: moving up close behind it, stepping into its line between its men, then holding
/// the line while the front steps back through it.
/// </summary>
public enum ReliefPhase { None, Closing, Joining, Passing }

/// <summary>
/// A battalion is an anchor point and a facing that its formation's slots hang off, plus orders and morale.
/// It never fights; it only tells its men where to stand and when they are free to go after the enemy.
/// </summary>
public sealed class Battalion
{
    public readonly int Id, Team;
    public readonly UnitType Type;
    public string Name;
    public readonly List<Soldier> Soldiers = new();
    public readonly List<SlotDef> Slots = new();

    public FormationType Formation;
    public int Files;
    public Vector2 Anchor;   // centre of the formation
    public float Facing;

    public OrderType Order = OrderType.Hold;
    public Vector2 Dest;
    public float DestFacing;
    public bool Running;
    public Battalion AttackTarget;
    public bool Engaged;     // attack order has closed: the charge has gone in and the line fights where it met the enemy
    public float ContactTime = -1f;   // when the charge hit (-1: not yet)
    float _noContactTime;
    bool _runningIn;

    public float Morale, MoraleTarget;
    public bool Routing, Shattered;
    public int Routs;
    float _calmTime, _moraleTimer;
    readonly List<float> _recentLosses = new(), _recentKills = new();

    public int InitialCount, Alive, Formed, FightingCount, FleeingCount;
    public Vector2 Centroid;
    public float AvgStamina = 1f;
    public float FightingStamina = 1f;   // mean stamina of the men who are fighting (all of them if none is)
    /// <summary>
    /// Share of the front rank due to be relieved with nobody fit to relieve it: the men behind are blown or hurt
    /// themselves, or gone (see RelieveFrontRank; in a formation with no files to change places in, the share of the
    /// men fighting who are spent). Near 0 while the ranks can keep changing places; when it climbs, the battalion
    /// as a whole is worn out and wants relieving by another (Battle.FrontSpent).
    /// </summary>
    public float Worn;
    public int FrontAttackers, FlankAttackers, RearAttackers;
    public bool SlotsDirty;

    // Reserves (see Battle.UpdateReserves): the Roman line relief, battalion by battalion.
    public Battalion ReserveFor;      // this battalion is the designated reserve behind that one (null = not a reserve)
    public Battalion ReservedBy;      // the reserve standing behind this one, if any
    public ReliefPhase Relief;        // as a reserve: relieving its front
    public bool FallingBack;          // relieved: stepping back out of the fight through its reserve's files
    internal Battalion PassingWith;   // the lines are passing through each other, so their men give way to each other
    internal float PassTime;          // ...for this much longer once the relief is over, while they untangle
    internal float ReliefStartTime, NextReliefTime;
    internal Battalion ReliefTarget;  // what the relieved battalion was attacking, for the reserve to take over
    internal Vector2 ReliefAt;        // where the reserve takes over the line

    public Battalion(int id, int team, UnitType type, string name)
    {
        Id = id;
        Team = team;
        Type = type;
        Name = name;
        Formation = type.Formation;
    }

    public bool Destroyed => Alive == 0;
    public bool Released => Order == OrderType.Attack && Engaged;
    /// <summary>Attacking at a run: the last stretch before contact, the charge and the fight. The men run to keep their places.</summary>
    public bool RunningIn => Order == OrderType.Attack && (Running || Engaged || _runningIn);
    public bool InMelee => FightingCount > 0;
    public bool Wavering => !Routing && Morale < 35f;
    public Vector2 Extent => Formation_Extent();
    public float Width => Extent.X;
    public float Depth => Extent.Y;

    public string Status
    {
        get
        {
            if (Destroyed) return Soldiers.Exists(s => s.Escaped) && !Soldiers.TrueForAll(s => s.Dead) ? "Fled" : "Destroyed";
            if (Routing) return Shattered ? "Shattered" : "Routing";
            if (FallingBack) return "Falling back";
            if (Relief != ReliefPhase.None) return "Relieving";
            if (InMelee) return Wavering ? "Wavering" : "Fighting";
            if (Wavering) return "Wavering";
            if (ReserveFor != null) return "In reserve";
            return Order switch
            {
                OrderType.Move => Running ? "Running" : "Marching",
                OrderType.Attack => Engaged ? "Charging" : "Advancing",
                _ => "Holding",
            };
        }
    }

    (FormationType, int, int) _extentKey = ((FormationType)(-1), 0, 0);
    Vector2 _extent;

    Vector2 Formation_Extent()
    {
        var key = (Formation, Math.Max(1, Formed), Files);
        if (key != _extentKey)
        {
            _extentKey = key;
            _extent = Sim.Formation.Extent(Formation, key.Item2, Files);
        }
        return _extent;
    }

    // ---------------------------------------------------------------- setup

    public void Deploy(Battle b, Vector2 center, float facing)
    {
        Anchor = Dest = center;
        Facing = DestFacing = facing;
        Files = Sim.Formation.DefaultFiles(Formation, Type.Size);
        for (int i = 0; i < Type.Size; i++)
        {
            var s = new Soldier(b.NextSoldierId++, this);
            Roll(b.Rng, s);
            Soldiers.Add(s);
            b.Soldiers.Add(s);
        }
        InitialCount = Alive = Formed = Type.Size;
        RelayoutSlots();

        // The best men take the front rank, as file leaders did.
        var ordered = new List<Soldier>(Soldiers);
        ordered.Sort((x, y) => (y.Skill * 0.7f + y.Nerve * 0.3f).CompareTo(x.Skill * 0.7f + x.Nerve * 0.3f));
        for (int i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            s.Slot = i;
            s.Pos = s.PrevPos = SlotWorld(i) + new Vector2(b.Range(-0.08f, 0.08f), b.Range(-0.08f, 0.08f));
            s.Facing = s.PrevFacing = SlotFacing(i);
            s.ReadyDelay = b.Range(0.2f, 0.8f);
        }
        Recount();
        Morale = MoraleTarget = BaseMorale();
    }

    void Roll(Random rng, Soldier s)
    {
        s.Skill = Mathf.Clamp(MathUtil.Gaussian(rng, Type.SkillMean, Type.SkillSpread), 0.05f, 0.98f);
        s.Strength = Mathf.Clamp(MathUtil.Gaussian(rng, Type.StrengthMean, 0.12f), 0.7f, 1.35f);
        s.Agility = Mathf.Clamp(MathUtil.Gaussian(rng, Type.AgilityMean, 0.1f), 0.7f, 1.3f);
        s.Endurance = Mathf.Clamp(MathUtil.Gaussian(rng, Type.EnduranceMean, 0.1f), 0.7f, 1.35f);
        s.Nerve = Mathf.Clamp(MathUtil.Gaussian(rng, Type.NerveMean, Type.NerveSpread), 0.05f, 0.98f);
        s.MaxHp = s.Hp = 100f * (0.85f + 0.15f * s.Strength);
        s.MaxStamina = s.Stamina = 100f * s.Endurance;
        s.Mass = 0.8f + 0.2f * s.Strength + 0.15f * Type.Armor.Weight + 0.1f * (Type.Shield?.Weight ?? 0f);
        s.BreakPoint = 10f + (1f - s.Nerve) * 32f;
        s.Name = Type.FirstNames[rng.Next(Type.FirstNames.Length)] + " " + Type.LastNames[rng.Next(Type.LastNames.Length)];
    }

    // ---------------------------------------------------------------- slots

    public Vector2 SlotWorld(int slot)
    {
        if (slot < 0 || slot >= Slots.Count) return Anchor;
        return Anchor + MathUtil.FromLocal(Slots[slot].Local, Facing);
    }

    public float SlotFacing(int slot) =>
        slot >= 0 && slot < Slots.Count ? Facing + Slots[slot].FacingOffset : Facing;

    /// <summary>World positions of every slot if this battalion stood at <paramref name="center"/> facing <paramref name="facing"/>.</summary>
    public void PreviewSlots(Vector2 center, float facing, int files, List<Vector2> into)
    {
        var tmp = new List<SlotDef>();
        Sim.Formation.Layout(Formation, Math.Max(1, Formed), files > 0 ? files : Files, tmp);
        foreach (var s in tmp) into.Add(center + MathUtil.FromLocal(s.Local, facing));
    }

    void RelayoutSlots() => Sim.Formation.Layout(Formation, Formed, Math.Clamp(Files, 1, Math.Max(1, Formed)), Slots);

    /// <summary>
    /// Membership changed (a man fell, fled or came back). Men keep their places; each gap is filled,
    /// front first, by the nearest man from a rank behind it. So the rear ranks step up into the line.
    /// </summary>
    public void RefreshSlots()
    {
        SlotsDirty = false;
        var members = new List<Soldier>();
        foreach (var s in Soldiers)
        {
            if (s.Active && !s.Fleeing) members.Add(s);
            else s.Slot = -1;
        }
        Formed = members.Count;
        RelayoutSlots();
        if (Formed == 0) return;

        var owner = new Soldier[Formed];
        foreach (var s in members)
        {
            if (s.Slot >= 0 && s.Slot < Formed && owner[s.Slot] == null) owner[s.Slot] = s;
            else s.Slot = -1;
        }

        for (int v = 0; v < Formed; v++)
        {
            if (owner[v] != null) continue;
            int rank = Slots[v].Rank;
            var at = SlotWorld(v);
            Soldier best = null;
            float bestD = float.MaxValue;
            foreach (var s in members)
            {
                bool behind = s.Slot < 0 || Slots[s.Slot].Rank > rank;
                if (!behind) continue;
                float d = s.Pos.DistanceSquaredTo(at);
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best == null) continue;
            if (best.Slot >= 0) owner[best.Slot] = null;
            best.Slot = v;
            owner[v] = best;
            // Vacating a later slot is fine: the loop reaches it. Vacating an earlier one can't happen (behind only).
        }
    }

    /// <summary>A new shape or an about-face: give every man the nearest slot in the new layout.</summary>
    public void ReshapeSlots()
    {
        var members = new List<Soldier>();
        foreach (var s in Soldiers)
        {
            if (s.Active && !s.Fleeing) members.Add(s);
            s.Slot = -1;
        }
        Formed = members.Count;
        RelayoutSlots();
        if (Formed == 0) return;
        var c = Vector2.Zero;
        foreach (var s in members) c += s.Pos;
        c /= members.Count;
        var local = new Vector2[members.Count];
        for (int i = 0; i < members.Count; i++) local[i] = MathUtil.ToLocal(members[i].Pos - c, Facing);
        var used = new bool[members.Count];
        for (int v = 0; v < Formed; v++)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < members.Count; i++)
            {
                if (used[i]) continue;
                float d = local[i].DistanceSquaredTo(Slots[v].Local);
                if (d < bestD) { bestD = d; best = i; }
            }
            used[best] = true;
            members[best].Slot = v;
        }
    }

    // ---------------------------------------------------------------- orders

    // An order from outside (the player, the AI general), a new formation included, takes the battalion out of any
    // reserve role it had and calls off any relief it was part of. The reserve's own station keeping moves it with
    // MoveTo instead.

    public void OrderMove(Vector2 dest, float facing, bool run, int files = 0)
    {
        if (Routing || Destroyed) return;
        LeaveReserve();
        MoveTo(dest, facing, run, files);
    }

    internal void MoveTo(Vector2 dest, float facing, bool run, int files = 0)
    {
        if (Routing || Destroyed) return;
        bool aboutFace = Mathf.Abs(MathUtil.AngleDiff(Facing, facing)) > 1.75f;
        bool newWidth = files > 0 && Sim.Formation.UsesFiles(Formation) && files != Files;
        if (newWidth) Files = files;
        Order = OrderType.Move;
        Dest = dest;
        DestFacing = facing;
        Running = run;
        AttackTarget = null;
        Engaged = false;
        StopCharging();
        if (aboutFace)
        {
            // Turn about where they stand: the rear rank becomes the front rather than everyone filing through.
            Anchor = Centroid;
            Facing = facing;
        }
        if (aboutFace || newWidth) ReshapeSlots();
    }

    public void OrderAttack(Battalion target, bool run)
    {
        if (Routing || Destroyed || target == null || target.Team == Team || target.Destroyed) return;
        LeaveReserve();
        if (Order == OrderType.Attack && AttackTarget == target) { Running |= run; return; }
        Order = OrderType.Attack;
        AttackTarget = target;
        Running = run;
        Engaged = false;
        _noContactTime = 0f;
    }

    public void OrderHalt()
    {
        if (Routing || Destroyed) return;
        LeaveReserve();
        Halt();
    }

    void Halt()
    {
        if (Routing || Destroyed) return;
        Order = OrderType.Hold;
        Anchor = Dest = Centroid;
        DestFacing = Facing;
        Running = false;
        AttackTarget = null;
        Engaged = false;
        StopCharging();
        if (!Routing) ReshapeSlots();
    }

    public void SetFormation(FormationType type)
    {
        if (Routing || Destroyed) return;
        LeaveReserve();
        Formation = type;
        Files = Sim.Formation.DefaultFiles(type, InitialCount);
        ReshapeSlots();
    }

    public void SetRunning(bool run)
    {
        if (Order != OrderType.Hold) Running = run;
    }

    void StopCharging()
    {
        foreach (var s in Soldiers) s.Charging = false;
    }

    // ---------------------------------------------------------------- per step

    public void Update(Battle b, float dt)
    {
        Recount();
        if (Destroyed) return;
        UpdateMorale(b, dt);
        if (SlotsDirty) RefreshSlots();
        if (Routing) return;
        _reliefTimer -= dt;
        if (_reliefTimer <= 0f)
        {
            _reliefTimer += 0.5f;
            RelieveFrontRank(b);
        }
        switch (Order)
        {
            case OrderType.Move: StepMove(dt); break;
            case OrderType.Attack: StepAttack(b, dt); break;
        }
    }

    public void Recount()
    {
        Alive = Formed = FightingCount = FleeingCount = 0;
        var sum = Vector2.Zero;
        var sumAll = Vector2.Zero;
        float stam = 0f, stamFighting = 0f;
        foreach (var s in Soldiers)
        {
            if (!s.Active) continue;
            Alive++;
            sumAll += s.Pos;
            stam += s.StaminaFrac;
            if (s.Fleeing) { FleeingCount++; continue; }
            Formed++;
            sum += s.Pos;
            if (s.Target != null && s.Pos.DistanceTo(s.Target.Pos) < s.Weapon.Reach + 1.5f)
            {
                FightingCount++;
                stamFighting += s.StaminaFrac;
            }
        }
        if (Formed > 0) Centroid = sum / Formed;
        else if (Alive > 0) Centroid = sumAll / Alive;
        AvgStamina = Alive > 0 ? stam / Alive : 0f;
        FightingStamina = FightingCount > 0 ? stamFighting / FightingCount : AvgStamina;
    }

    /// <summary>How well the men are keeping up with their slots; the anchor slows to let stragglers close.</summary>
    float Cohesion() => Mathf.Clamp(1.45f - 0.3f * SlotError(), 0.3f, 1f);

    float MaxTurnRate() => Math.Min(1.2f, 2.0f / Math.Max(1f, Width * 0.5f));

    void StepMove(float dt)
    {
        var to = Dest - Anchor;
        float dist = to.Length();
        float speed = (Running ? Type.RunSpeed : Type.WalkSpeed) * Cohesion();
        if (dist > 0.001f) Anchor += to.LimitLength(speed * dt);
        Facing = MathUtil.TurnToward(Facing, DestFacing, MaxTurnRate() * dt);
        if (dist < 0.05f && Mathf.Abs(MathUtil.AngleDiff(Facing, DestFacing)) < 0.01f)
        {
            Order = OrderType.Hold;
            Running = false;
        }
    }

    void StepAttack(Battle b, float dt)
    {
        var t = AttackTarget;
        if (t == null || t.Destroyed) { Halt(); return; }
        var to = t.Centroid - Centroid;
        float d = to.Length();
        float gap = d - Depth * 0.5f - t.Depth * 0.5f;
        var dest = t.Centroid - to.Normalized() * (Depth * 0.5f + t.Depth * 0.5f + 0.8f);   // front rank just short of theirs
        if (!Engaged)
        {
            Facing = MathUtil.TurnToward(Facing, MathUtil.Angle(to), MaxTurnRate() * dt);
            bool run = Running || gap < 24f;
            _runningIn = run;
            float speed = run ? Type.RunSpeed : Type.WalkSpeed * Cohesion();
            Anchor += (dest - Anchor).LimitLength(speed * dt);
            if (gap < 12f || FightingCount > 0) BeginCharge(b);
        }
        else
        {
            // Once the fronts meet the formation settles into a fighting line where they met, rather than following
            // its men wherever they wander; until they meet it keeps coming on. Chasing a broken enemy there is no
            // line to keep, and it follows the men.
            if (t.Routing) Anchor = Anchor.Lerp(Centroid, Mathf.Min(1f, dt * 1.5f));
            else if (ContactAnchor(out var at)) Anchor = Anchor.Lerp(at, Mathf.Min(1f, dt * 1.5f));
            else Anchor += (dest - Anchor).LimitLength(Type.RunSpeed * dt);
            Facing = MathUtil.TurnToward(Facing, t.Routing ? MathUtil.Angle(to) : SquareTo(t, to), 0.8f * dt);
            _noContactTime = FightingCount == 0 ? _noContactTime + dt : 0f;
            if (FightingCount > 0 && ContactTime < 0f) ContactTime = b.Time;
            if (t.Routing && _noContactTime > 3f) Halt();   // they've broken; let them go and re-form
            else if (_noContactTime > 6f) Engaged = false;       // lost them; close in again
        }
    }

    /// <summary>
    /// The facing that puts a fighting line square to the face of <paramref name="t"/> it is up against (its front,
    /// a flank or its rear), rather than turned at its middle: with the two lines at arm's length, a step's drift
    /// sideways between the two middles would otherwise wheel the whole line round onto the enemy's flank. A
    /// formation with no straight faces (a wedge, an orb) is just faced.
    /// </summary>
    static float SquareTo(Battalion t, Vector2 to)
    {
        float at = MathUtil.Angle(to);
        if (!Sim.Formation.UsesFiles(t.Formation) || t.Formation == FormationType.Loose) return at;
        float side = Mathf.Round(MathUtil.AngleDiff(t.Facing, at + Mathf.Pi) / (Mathf.Pi * 0.5f)) * Mathf.Pi * 0.5f;
        return t.Facing + side + Mathf.Pi;
    }

    /// <summary>
    /// The charge goes in as a line: the front rank keeps its places as the formation runs in, and each man picks
    /// the man in front of him and throws himself at him only in the last strides. The ranks behind run in at their
    /// places to back them up, so they hit together and the rear ranks are right behind the front when they do, and
    /// press on into any gap for a moment after (see SoldierAI.RearChargeTime).
    /// </summary>
    void BeginCharge(Battle b)
    {
        Engaged = true;
        ContactTime = -1f;
        _noContactTime = 0f;
        foreach (var s in Soldiers)
        {
            if (!s.Active || s.Fleeing) continue;
            s.Charging = true;
            s.ChargeStartTime = b.Time;
            s.Loose = false;
        }
    }

    /// <summary>
    /// The anchor that would put the front-rank men who are fighting on their own slots: where the line actually
    /// stands against the enemy. Men fighting at their places count; loose ones only if nobody is at his place.
    /// False if no front-rank man is fighting.
    /// </summary>
    bool ContactAnchor(out Vector2 at)
    {
        Vector2 placed = Vector2.Zero, loose = Vector2.Zero;
        int np = 0, nl = 0;
        foreach (var s in Soldiers)
        {
            if (!s.Active || s.Fleeing || s.Slot < 0 || s.Slot >= Slots.Count || Slots[s.Slot].Rank != 0) continue;
            if (s.Target == null || s.Pos.DistanceTo(s.Target.Pos) > s.Weapon.Reach + 0.5f) continue;
            var a = s.Pos - MathUtil.FromLocal(Slots[s.Slot].Local, Facing);
            if (s.Loose) { loose += a; nl++; }
            else { placed += a; np++; }
        }
        at = np > 0 ? placed / np : nl > 0 ? loose / nl : Anchor;
        return np + nl > 0;
    }

    float _reliefTimer;

    /// <summary>A front-rank man below this share of his wind (about two minutes' hard fighting), or of his health, is due to be relieved.</summary>
    public const float ReliefStamina = 0.3f, ReliefHp = 0.5f;

    /// <summary>
    /// Line relief: a front-rank man who is blown or badly hurt changes places with the fresher man behind him
    /// in his file, as the Romans did, so the front is kept fresh and a wounded man isn't left to die where he stands.
    /// They wait for a moment when he isn't in the middle of a blow, just hit, or pressed by two; until it comes he
    /// keeps his guard up and starts nothing new. Then the fresh man steps up past his shoulder as he backs down the
    /// file (SoldierAI), so for a moment both stand in the gap.
    /// </summary>
    void RelieveFrontRank(Battle b)
    {
        Worn = 0f;
        if (Formation is not (FormationType.Line or FormationType.Deep or FormationType.Thin or FormationType.Column))
        {
            // No files to change places in: every spent man in the fight stays in it.
            int fighting = 0, spentMen = 0;
            foreach (var s in Soldiers)
            {
                if (!s.Active || s.Fleeing || s.Target == null) continue;
                fighting++;
                if (s.StaminaFrac < ReliefStamina || s.HpFrac < ReliefHp) spentMen++;
            }
            Worn = fighting > 0 ? spentMen / (float)fighting : 0f;
            return;
        }
        int n = Math.Min(Slots.Count, Formed);
        if (n == 0) return;
        var bySlot = new Soldier[n];
        foreach (var s in Soldiers)
            if (s.Active && !s.Fleeing && s.Slot >= 0 && s.Slot < n) bySlot[s.Slot] = s;
        int front = 0, worn = 0;
        for (int v = 0; v < n; v++)
        {
            var f = bySlot[v];
            if (f == null || Slots[v].Rank != 0) continue;
            front++;
            bool spent = f.StaminaFrac < ReliefStamina || f.HpFrac < ReliefHp;
            if (!spent) continue;
            // The man behind him in his file: the nearest in the second rank. A short rank stands centred, so its men
            // stand half a file off those in front, and the end files of a line whose second rank is two or more men
            // short have nobody straight behind them; the man half a pace across comes up instead.
            int behind = -1;
            float bestDx = 1.1f * Sim.Formation.FileSpacing;
            for (int w = 0; w < n; w++)
            {
                float dx = Mathf.Abs(Slots[w].Local.X - Slots[v].Local.X);
                if (Slots[w].Rank == 1 && dx < bestDx) { bestDx = dx; behind = w; }
            }
            var r = behind >= 0 ? bySlot[behind] : null;
            // Nobody fit to take his place: the men behind are blown or hurt themselves, or gone.
            if (r == null || r.HpFrac < ReliefHp || r.StaminaFrac < 0.5f || (r.StaminaFrac < f.StaminaFrac + 0.3f && r.HpFrac < f.HpFrac + 0.25f))
            {
                worn++;
                continue;
            }
            if (b.Time - f.ReliefTime < 3f || r.Phase != CombatPhase.Ready || b.Time - r.ReliefTime < 3f) continue;
            // A file that has come apart can't pass men through it.
            if (f.Pos.DistanceTo(SlotWorld(v)) > 2.2f || r.Pos.DistanceTo(SlotWorld(behind)) > 2.2f) continue;
            if (f.Phase != CombatPhase.Ready || f.ThreatCount > 1 || b.Time - f.LastHitTime < 0.6f)
            {
                f.BreatherUntil = Math.Max(f.BreatherUntil, b.Time + 0.6f);
                continue;
            }
            f.Slot = behind;
            r.Slot = v;
            bySlot[v] = r;
            bySlot[behind] = f;
            f.ReliefTime = r.ReliefTime = b.Time;
            f.SwapSide = 1f;    // he backs down the file on its right, the fresh man comes up on its left
            r.SwapSide = -1f;
            f.Target = null;
            f.Riposte = f.FollowUp = false;
            f.BreatherUntil = -99f;
        }
        Worn = front > 0 ? worn / (float)front : 0f;
    }

    // ---------------------------------------------------------------- morale

    public float BaseMorale()
    {
        float nerve = 0f, skill = 0f;
        int n = 0;
        foreach (var s in Soldiers)
        {
            if (!s.Active) continue;
            nerve += s.Nerve;
            skill += s.Skill;
            n++;
        }
        if (n == 0) return 0f;
        return 45f + 30f * nerve / n + 20f * skill / n;
    }

    public void OnSoldierLost(Battle b, Soldier s)
    {
        _recentLosses.Add(b.Time);
        s.Slot = -1;
        SlotsDirty = true;
    }

    public void OnKill(Battle b) => _recentKills.Add(b.Time);

    public void Shock(float amount) => Morale = Math.Max(0f, Morale - amount);

    void UpdateMorale(Battle b, float dt)
    {
        _moraleTimer -= dt;
        if (_moraleTimer > 0f) return;
        const float step = 0.25f;
        _moraleTimer += step;
        _recentLosses.RemoveAll(t => b.Time - t > 10f);
        _recentKills.RemoveAll(t => b.Time - t > 10f);

        if (Routing) { RoutingUpdate(b, step); return; }

        CountAttackers(b);
        float target = BaseMorale();
        target -= 75f * (1f - Alive / (float)InitialCount);
        target -= Math.Min(30f, 5f * _recentLosses.Count);
        target += Math.Min(12f, 2f * _recentKills.Count);
        target -= Math.Min(30f, 5f * FlankAttackers);
        target -= Math.Min(40f, 8f * RearAttackers);
        int attackers = FrontAttackers + FlankAttackers + RearAttackers;
        if (FightingCount > 0 && attackers > FightingCount * 1.25f)
            target -= Math.Min(18f, 10f * (attackers / (float)FightingCount - 1.25f));
        target -= 30f * (1f - AvgStamina) * (1f - AvgStamina);

        float friendsRouting = 0f, friendsNear = 0f, enemiesRouting = 0f;
        foreach (var o in b.Battalions)
        {
            if (o == this || o.Destroyed || o.Centroid.DistanceTo(Centroid) > 40f) continue;
            if (o.Team == Team)
            {
                if (o.Routing) friendsRouting += 10f;
                else if (o.Centroid.DistanceTo(Centroid) < 30f) friendsNear += 3f;
            }
            else if (o.Routing) enemiesRouting += 5f;
        }
        target += -Math.Min(25f, friendsRouting) + Math.Min(6f, friendsNear) + Math.Min(10f, enemiesRouting);

        MoraleTarget = Mathf.Clamp(target, 0f, 100f);
        Morale += (MoraleTarget - Morale) * (MoraleTarget < Morale ? 0.35f : 0.12f);

        // Men break one by one, the faint-hearted first, and a man who is badly hurt or has two blades on him
        // sooner than one who isn't; when half have gone the rest follow.
        foreach (var s in Soldiers)
        {
            if (!s.Active) continue;
            float breakAt = s.BreakPoint + 15f * (1f - s.HpFrac) + (s.ThreatCount > 1 ? 6f : 0f);
            if (!s.Fleeing && Morale < breakAt && b.Rand() < 0.35f)
            {
                StartFleeing(s);
            }
            else if (s.Fleeing && Morale > s.BreakPoint + 12f && !b.EnemyWithin(s.Pos, Team, 8f))
            {
                s.Fleeing = false;
                SlotsDirty = true;
            }
        }
        Recount();
        if (FleeingCount > Alive * 0.5f) BeginRout(b);
    }

    void StartFleeing(Soldier s)
    {
        s.Fleeing = true;
        s.Target = null;
        s.StrikeTarget = null;
        s.Charging = false;
        s.Slot = -1;
        if (s.Phase == CombatPhase.Windup) s.Phase = CombatPhase.Ready;
        SlotsDirty = true;
    }

    void BeginRout(Battle b)
    {
        Routing = true;
        if (Routs >= 1) Shattered = true;
        Order = OrderType.Hold;
        AttackTarget = null;
        Engaged = false;
        _calmTime = 0f;
        foreach (var s in Soldiers)
            if (s.Active && !s.Fleeing) StartFleeing(s);
        b.EmitUnit(EventKind.Rout, this);
    }

    void RoutingUpdate(Battle b, float step)
    {
        bool threatened = b.EnemyWithin(Centroid, Team, 25f);
        if (threatened)
        {
            _calmTime = 0f;
            Morale = Math.Max(0f, Morale - 2f * step);
            return;
        }
        _calmTime += step;
        if (Shattered || _calmTime < 5f) return;
        Morale = Math.Min(45f, Morale + 5f * step);
        if (Morale >= 40f) Rally(b);
    }

    void Rally(Battle b)
    {
        Routing = false;
        Routs++;
        foreach (var s in Soldiers)
            if (s.Active) s.Fleeing = false;
        Recount();
        Anchor = Dest = Centroid;
        var enemy = b.NearestEnemyUnit(this);
        if (enemy != null) Facing = MathUtil.Angle(enemy.Centroid - Centroid);
        DestFacing = Facing;
        Order = OrderType.Hold;
        MoraleTarget = Morale;
        ReshapeSlots();
        b.EmitUnit(EventKind.Rally, this);
    }

    /// <summary>
    /// Enemies striking at this battalion, sorted by which face of the formation they are on. Beyond its end files
    /// or its rear rank means more than a pace beyond, the same for a battalion holding its ground as for one
    /// fighting where its charge met the enemy: a man fighting the end man half a pace out to the side is still
    /// in front of the line.
    /// </summary>
    void CountAttackers(Battle b)
    {
        const float pace = 1f;
        FrontAttackers = FlankAttackers = RearAttackers = 0;
        var ext = Extent;
        float halfW = ext.X * 0.5f + pace;
        float halfD = ext.Y * 0.5f + pace;
        foreach (var e in b.Soldiers)
        {
            if (!e.Active || e.Fleeing || e.Team == Team || e.Target == null || e.Target.Unit != this) continue;
            if (e.Pos.DistanceTo(e.Target.Pos) > e.Weapon.Reach + 0.8f) continue;
            if (Formation == FormationType.Square) { FrontAttackers++; continue; }
            var l = MathUtil.ToLocal(e.Pos - Anchor, Facing);
            if (l.Y < -halfD) RearAttackers++;
            else if (Mathf.Abs(l.X) > halfW && l.Y < halfD) FlankAttackers++;
            else FrontAttackers++;
        }
    }

    // ---------------------------------------------------------------- reserves

    /// <summary>An order from outside: no longer anyone's reserve, and any relief this battalion is part of is called off.</summary>
    void LeaveReserve()
    {
        if (ReserveFor != null) UnlinkReserve();
        else if (FallingBack && ReservedBy != null) ReservedBy.CancelRelief();
        FallingBack = false;
    }

    internal void LinkReserve(Battalion front)
    {
        ReserveFor = front;
        front.ReservedBy = this;
    }

    internal void UnlinkReserve()
    {
        CancelRelief();
        if (ReserveFor != null && ReserveFor.ReservedBy == this) ReserveFor.ReservedBy = null;
        ReserveFor = null;
    }

    /// <summary>
    /// As a reserve: stop relieving. The reserve carries on with the move it was making; a front that was already
    /// stepping back stands where it is and fights on.
    /// </summary>
    internal void CancelRelief()
    {
        if (Relief == ReliefPhase.None) return;
        Relief = ReliefPhase.None;
        ReliefTarget = null;
        var f = ReserveFor;
        if (f is { FallingBack: true })
        {
            f.FallingBack = false;
            f.Halt();
        }
        if (PassingWith != null) PassTime = PassingWith.PassTime = Battle.PassLinger;
    }

    /// <summary>Files for this battalion to stand as wide as <paramref name="other"/> (0: its formation has no files).</summary>
    internal int FilesToMatch(Battalion other) =>
        Sim.Formation.UsesFiles(Formation) ? Sim.Formation.FilesForWidth(Formation, other.Width, Math.Max(1, Formed)) : 0;

    /// <summary>Depth this battalion would have with the given number of files (0: its own).</summary>
    internal float DepthWith(int files) => Sim.Formation.Extent(Formation, Math.Max(1, Formed), files > 0 ? files : Files).Y;

    /// <summary>Mean distance of the men from their places, for men not fighting.</summary>
    internal float SlotError()
    {
        float err = 0f;
        int n = 0;
        foreach (var s in Soldiers)
        {
            if (!s.Active || s.Fleeing || s.Slot < 0 || s.Target != null) continue;
            err += s.Pos.DistanceTo(SlotWorld(s.Slot));
            n++;
        }
        return n > 0 ? err / n : 0f;
    }

    /// <summary>The reserve has come up into the fight: it takes over the attack, already closed if it is in contact.</summary>
    internal void TakeOverAttack(Battalion target)
    {
        OrderAttack(target, false);
        if (Order == OrderType.Attack && FightingCount > 0)
        {
            Engaged = true;
            _noContactTime = 0f;
        }
    }
}
