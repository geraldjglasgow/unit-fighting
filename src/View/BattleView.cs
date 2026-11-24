using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using UnitFighting.Sim;
using static UnitFighting.View.MeshBuilder;

namespace UnitFighting.View;

/// <summary>
/// Everything that draws one battle: soldiers, banners, selection rings, order previews, sparks and blood.
/// Create one per battle, AddChild it, call Build once, then Sync every frame. QueueFree it to throw the battle away.
///
/// Soldiers are not nodes: each body part kind is one shared MultiMesh refilled every frame from a procedural
/// skeleton posed from the sim state (see <see cref="SoldierView"/>), so draw calls stay flat however many men fight.
/// </summary>
public partial class BattleView : Node3D
{
    /// <summary>Set by the game: is this battalion selected (draws selection rings and a highlighted banner).</summary>
    public Func<Battalion, bool> IsSelected = _ => false;

    /// <summary>Set by the game each frame: the soldier under the mouse, the soldier shown in the inspector, the battalion under the mouse.</summary>
    public Soldier Hovered, Inspected;
    public Battalion HoveredUnit;

    /// <summary>The team the player commands (selection colours).</summary>
    public int PlayerTeam = 0;

    /// <summary>Skip posing men well outside the camera's view (they still animate when they come back).</summary>
    public bool FrustumCulling = true;

    /// <summary>The battle being drawn (null before Build).</summary>
    public Battle Battle => _battle;

    /// <summary>Particles and ground decals, exposed so the game can add its own (e.g. for a debug overlay).</summary>
    public EffectsView Effects => _fx;

    public static readonly Color SelectedColor = new(0.55f, 1f, 0.6f, 0.9f);
    public static readonly Color HoverOwnColor = new(1f, 0.88f, 0.3f, 0.85f);
    public static readonly Color HoverEnemyColor = new(1f, 0.28f, 0.2f, 0.9f);
    public static readonly Color HoveredManColor = new(1f, 1f, 1f, 0.75f);
    public static readonly Color InspectedColor = new(1f, 0.85f, 0.25f, 1f);

    Battle _battle;
    readonly List<SoldierView> _views = new();
    readonly Dictionary<Soldier, SoldierView> _byMan = new();
    readonly Dictionary<Battalion, BannerView> _banners = new();
    PartBatch[] _parts;
    PartBatch _rings, _preview;
    MeshInstance3D _marker;
    EffectsView _fx;
    Node3D _soldierRoot;
    readonly Plane[] _frustum = new Plane[6];
    int _frustumCount;
    readonly Random _rng = new(99);

    public void Build(Battle battle)
    {
        _battle = battle;
        Name = "BattleView";
        _soldierRoot = new Node3D { Name = "Soldiers" };
        AddChild(_soldierRoot);

        int n = Math.Max(1, battle.Soldiers.Count);
        _parts = new PartBatch[(int)Part.Count];
        for (int i = 0; i < _parts.Length; i++)
        {
            var p = (Part)i;
            int cap = p is Part.Thigh or Part.Shin or Part.Foot or Part.UpperArm or Part.Forearm ? n * 2 : n;
            _parts[i] = new PartBatch(_soldierRoot, SoldierAssets.MeshOf(p), SoldierAssets.MaterialOf(p), cap,
                colors: false, custom: true, shadows: SoldierAssets.CastsShadow(p), p.ToString());
        }

        var ringMesh = new MeshBuilder()
            .Add(new TorusMesh { InnerRadius = 0.33f, OuterRadius = 0.4f, Rings = 24, RingSegments = 4 }, At(Vector3.Zero, Scale(1f, 0.12f, 1f)))
            .Build();
        _rings = new PartBatch(this, ringMesh, SoldierAssets.Unshaded(true), 64, colors: true, custom: false, shadows: false, "Rings");
        _rings.Node.SortingOffset = 1f;

        var disc = new MeshBuilder()
            .Add(Cyl(0.26f, 0.26f, 0.02f, 16), At(0, 0.01f, 0))
            .Add(new TorusMesh { InnerRadius = 0.26f, OuterRadius = 0.31f, Rings = 16, RingSegments = 3 }, At(new Vector3(0, 0.012f, 0), Scale(1f, 0.1f, 1f)))
            .Build();
        _preview = new PartBatch(this, disc, SoldierAssets.Unshaded(true), 64, colors: true, custom: false, shadows: false, "Preview");

        _marker = new MeshInstance3D { Name = "InspectMarker", Mesh = BuildMarker(), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _marker.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.FixedY,
            FixedSize = true,
            NoDepthTest = true,
            VertexColorUseAsAlbedo = true,
            RenderPriority = 8,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        AddChild(_marker);

        _fx = new EffectsView { Name = "Effects" };
        AddChild(_fx);
        AddChild(new ReserveLinks { View = this });

        SyncViews();
        foreach (var u in battle.Battalions) EnsureBanner(u);
    }

    static ArrayMesh BuildMarker()
    {
        // A downward chevron with a dark outline, in fixed-size units.
        var b = new MeshBuilder();
        var n = Vector3.Back;
        var gold = new Color(1f, 0.82f, 0.2f);
        var dark = new Color(0.1f, 0.06f, 0f);
        const float s = 0.011f;
        b.Tri(new Vector3(-s * 1.25f, s * 1.9f, 0.0001f), new Vector3(s * 1.25f, s * 1.9f, 0.0001f), new Vector3(0, -s * 0.25f, 0.0001f), n, dark);
        b.Tri(new Vector3(-s, s * 1.7f, 0), new Vector3(s, s * 1.7f, 0), new Vector3(0, 0f, 0), n, gold);
        return b.Build();
    }

    void SyncViews()
    {
        var list = _battle.Soldiers;
        for (int i = _views.Count; i < list.Count; i++)
        {
            var v = new SoldierView(list[i]);
            _views.Add(v);
            _byMan[list[i]] = v;
        }
    }

    BannerView EnsureBanner(Battalion u)
    {
        if (_banners.TryGetValue(u, out var b)) return b;
        b = new BannerView();
        b.Init(u);
        AddChild(b);
        _banners[u] = b;
        return b;
    }

    /// <summary>
    /// Call every frame after stepping the sim. alpha (0..1) is how far between the last two sim steps the frame is,
    /// for interpolating PrevPos->Pos. simDt is sim seconds that passed this frame (0 while paused), for animation clocks.
    /// Drains battle.Events (spawns sparks/blood/dust) and clears the list.
    /// </summary>
    public void Sync(float alpha, float simDt)
    {
        if (_battle == null) return;
        alpha = Mathf.Clamp(alpha, 0f, 1f);
        simDt = Mathf.Max(0f, simDt);
        float now = _battle.Time + alpha * Battle.FixedDt;
        if (_views.Count != _battle.Soldiers.Count) SyncViews();

        var cam = GetViewport()?.GetCamera3D();
        if (cam is RtsCamera rts) rts.InterpAlpha = alpha;
        UpdateFrustum(cam);

        long t0 = Stopwatch.GetTimestamp();
        DrainEvents(now);
        long t1 = Stopwatch.GetTimestamp();

        UpdateStandTo();
        _syncCount++;
        foreach (var b in _parts) b.Begin();
        foreach (var v in _views)
        {
            var s = v.S;
            if (s.Escaped) { v.Visible = false; continue; }
            bool emit = !FrustumCulling || InFrustum(new Vector3(s.Pos.X, 0.9f, s.Pos.Y), 3.5f);
            v.StandTo = s.Unit.Id < _standTo.Length && _standTo[s.Unit.Id];
            if (!emit || !s.Active || s.Shield == null || (s.Target == null && !s.Unit.InMelee)) v.FrontRoom = 9f;
            else if (((s.Id + _syncCount) & 1) == 0) v.FrontRoom = FrontRoom(s);   // half the men each frame: it's smoothed anyway
            v.Animate(_parts, alpha, simDt, now, emit);
            if (v.JustLanded)
            {
                v.JustLanded = false;
                var torso = s.Pos + v.LandDir * 1.0f;
                _fx.Dust(new Vector3(torso.X, 0.05f, torso.Y), 5, 0.8f);
            }
        }
        long t2 = Stopwatch.GetTimestamp();
        foreach (var b in _parts) b.Commit();
        long t3 = Stopwatch.GetTimestamp();

        DrawRings(now);
        DrawBanners(simDt, cam);
        long t4 = Stopwatch.GetTimestamp();
        _fx.Tick(simDt);
        long t5 = Stopwatch.GetTimestamp();
        double f = 1000.0 / Stopwatch.Frequency;
        Timings = ((t1 - t0) * f, (t2 - t1) * f, (t3 - t2) * f, (t4 - t3) * f, (t5 - t4) * f);
    }

    /// <summary>The view of one man (null if he isn't drawn), for tools that inspect the animation.</summary>
    public SoldierView ViewOf(Soldier s) => s != null && _byMan.TryGetValue(s, out var v) ? v : null;

    /// <summary>Milliseconds the last Sync spent on: events, posing soldiers, uploading, rings+banners, effects.</summary>
    public (double events, double pose, double upload, double overlay, double fx) Timings;

    void UpdateFrustum(Camera3D cam)
    {
        _frustumCount = 0;
        if (cam == null || !FrustumCulling) return;
        var planes = cam.GetFrustum();
        for (int i = 0; i < planes.Count && i < 6; i++) _frustum[_frustumCount++] = planes[i];
    }

    bool InFrustum(Vector3 p, float radius)
    {
        for (int i = 0; i < _frustumCount; i++)
            if (_frustum[i].DistanceTo(p) > radius) return false;
        return true;
    }

    // ---------------------------------------------------------------- what each man sees coming

    const float ChargeWarning = 14f;   // metres (front to front) at which a charge coming at a battalion makes it stand to

    bool[] _standTo = Array.Empty<bool>();
    int _syncCount;
    readonly List<Soldier> _near = new();

    /// <summary>Battalions with an enemy charge bearing down on them: their men get their guard up and set their feet.</summary>
    void UpdateStandTo()
    {
        var units = _battle.Battalions;
        if (_standTo.Length < _battle.NextUnitId) _standTo = new bool[_battle.NextUnitId];
        Array.Clear(_standTo);
        foreach (var e in units)
        {
            if (e.Destroyed || e.Routing || !e.Released) continue;
            bool charging = false;
            foreach (var s in e.Soldiers)
                if (s.Charging && s.Active) { charging = true; break; }
            if (!charging) continue;
            foreach (var u in units)
            {
                if (u.Team == e.Team || u.Destroyed || u.Routing || u.Id >= _standTo.Length) continue;
                float gap = u.Centroid.DistanceTo(e.Centroid) - (u.Depth + e.Depth) * 0.5f;
                if (gap < ChargeWarning) _standTo[u.Id] = true;
            }
        }
    }

    /// <summary>
    /// How far his shield can reach forward before it meets the nearest enemy close in front of it: half the gap to a
    /// man facing him behind his own shield, most of it to one who isn't.
    /// </summary>
    float FrontRoom(Soldier s)
    {
        if (_battle.Time <= 0f) return 9f;   // the grid is built on the first step
        _battle.Grid.Query(s.Pos, 1.4f, _near);
        var fwd = MathUtil.Dir(s.Facing);
        var left = MathUtil.Left(fwd);
        float room = 9f;
        foreach (var e in _near)
        {
            if (e.Team == s.Team || !e.Active || e.Phase == CombatPhase.Down) continue;
            var d = e.Pos - s.Pos;
            float along = d.Dot(fwd), across = d.Dot(left);
            // His shield hangs on his left: an enemy well off to his right is clear of it.
            if (along < 0.1f || across < -0.4f || across > 0.95f) continue;
            bool facesHim = e.Shield != null && !e.Fleeing && MathUtil.Dir(e.Facing).Dot(-d) > 0.3f * d.Length();
            room = Mathf.Min(room, facesHim ? along * 0.5f : along - 0.25f);
        }
        return room;
    }

    // ---------------------------------------------------------------- events

    void DrainEvents(float now)
    {
        var events = _battle.Events;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var a = e.Attacker;
            var d = e.Defender;
            if (e.Kind is EventKind.Rout or EventKind.Rally || d == null) continue;
            _byMan.TryGetValue(d, out var dv);
            var toA2 = a != null ? a.Pos - d.Pos : MathUtil.Dir(d.Facing);
            if (toA2.LengthSquared() < 1e-6f) toA2 = MathUtil.Dir(d.Facing);
            toA2 = toA2.Normalized();
            var toA = new Vector3(toA2.X, 0f, toA2.Y);
            var dPos = new Vector3(d.Pos.X, 0f, d.Pos.Y);
            SoldierView av = null;
            if (a != null) _byMan.TryGetValue(a, out av);
            switch (e.Kind)
            {
                case EventKind.Block:
                {
                    if (dv != null) { dv.BlockRel = MathUtil.RelativeAngle(d.Facing, toA2); dv.BlockKind = a?.Blow ?? BlowKind.Cut; }
                    if (av != null) av.StoppedTime = now;
                    var left = MathUtil.Left(MathUtil.Dir(d.Facing));
                    var at = dPos + toA * 0.52f + new Vector3(left.X, 0f, left.Y) * 0.1f + Vector3.Up * (1.15f + 0.2f * (float)_rng.NextDouble());
                    _fx.Sparks(at, toA, 11);
                    if (_rng.NextDouble() < 0.5) _fx.Chips(at, toA, 2);
                    break;
                }
                case EventKind.Parry:
                {
                    if (dv != null) { dv.BlockRel = MathUtil.RelativeAngle(d.Facing, toA2); dv.BlockKind = a?.Blow ?? BlowKind.Cut; }
                    if (av != null) av.StoppedTime = now;
                    var at = dPos + toA * 0.62f + Vector3.Up * (1.3f + 0.15f * (float)_rng.NextDouble());
                    _fx.Sparks(at, Vector3.Up * 0.4f, 16, false, 1.15f);
                    break;
                }
                case EventKind.Glance:
                {
                    if (dv != null) { dv.HitPush = -toA2; dv.HitAmount = e.Amount; }
                    var at = dPos + toA * 0.22f + Vector3.Up * HitHeight(e.Location);
                    _fx.Sparks(at, toA, 7, grey: true, 0.8f);
                    break;
                }
                case EventKind.Hit:
                {
                    if (dv != null) { dv.HitPush = -toA2; dv.WoundTime = now; dv.HitAmount = e.Amount; }
                    var at = dPos + toA * 0.18f + Vector3.Up * HitHeight(e.Location);
                    _fx.Blood(at, -toA, 9 + (int)Mathf.Min(14f, e.Amount / 3f));
                    var off = -toA2 * (0.2f + 0.45f * (float)_rng.NextDouble());
                    _fx.Decal(d.Pos + off, 0.18f + Mathf.Min(0.2f, e.Amount / 150f), 0.3f, 1.4f);
                    break;
                }
                case EventKind.Kill:
                {
                    if (dv != null) { dv.HitPush = -toA2; dv.WoundTime = now; }
                    var at = dPos + toA * 0.18f + Vector3.Up * HitHeight(e.Location);
                    _fx.Blood(at, -toA, 30, 1.2f);
                    var pool = d.Pos + d.DeathDir * 0.95f;
                    _fx.Decal(pool, 0.55f + 0.3f * (float)_rng.NextDouble(), 5f, 1.25f, new Color(0.3f, 0.015f, 0.01f));
                    _fx.Decal(d.Pos - toA2 * 0.4f, 0.28f, 0.3f, 1.6f);
                    break;
                }
                case EventKind.Impact:
                {
                    if (dv != null)
                    {
                        dv.ImpactPush = -toA2;
                        dv.ShoveTime = now;
                        dv.Shove = e.Amount;
                    }
                    if (av != null) av.RamTime = now;
                    bool big = e.Amount > 1f;
                    _fx.Dust(dPos + toA * 0.2f, big ? 14 : 6, big ? 1.3f + Mathf.Min(0.6f, (e.Amount - 1f) * 0.5f) : 0.9f);
                    if (e.Amount > 1.6f) _fx.Dust(dPos - toA * 0.8f, 8, 1.4f);
                    break;
                }
                case EventKind.Miss:
                {
                    if (dv != null && a != null && a.Pos.DistanceTo(d.Pos) < a.Weapon.Reach + 0.6f) { dv.MissTime = now; dv.MissFrom = toA2; }
                    if (av != null) av.MissedTime = now;
                    break;
                }
                case EventKind.GuardBreak:
                {
                    if (dv != null) { dv.BlockRel = MathUtil.RelativeAngle(d.Facing, toA2); dv.BlockKind = a?.Blow ?? BlowKind.Cut; }
                    if (av != null) av.StoppedTime = now;
                    var at = dPos + toA * 0.5f + Vector3.Up * 1.2f;
                    _fx.Sparks(at, toA, 22, false, 1.3f);
                    _fx.Chips(at, toA, 12);
                    _fx.Flash(at, 0.45f, new Color(1.8f, 1.6f, 1.3f, 0.9f));
                    break;
                }
            }
        }
        events.Clear();
    }

    static float HitHeight(HitLocation loc) => loc switch
    {
        HitLocation.Head => 1.56f,
        HitLocation.Arm => 1.15f,
        HitLocation.Leg => 0.55f,
        _ => 1.2f,
    };

    // ---------------------------------------------------------------- rings, marker, banners

    void DrawRings(float now)
    {
        _rings.Begin();
        foreach (var u in _battle.Battalions)
        {
            bool sel = IsSelected != null && IsSelected(u);
            bool hov = HoveredUnit == u;
            if (!sel && !hov) continue;
            Color c;
            if (sel) c = u.Team == PlayerTeam ? SelectedColor : HoverEnemyColor.Lerp(Colors.White, 0.25f);
            else c = u.Team == PlayerTeam ? HoverOwnColor : HoverEnemyColor;
            if (sel && hov) c = c.Lerp(Colors.White, 0.35f);
            foreach (var s in u.Soldiers)
            {
                if (!s.Active || !_byMan.TryGetValue(s, out var v)) continue;
                _rings.Add(new Transform3D(Basis.Identity, v.GroundPos + Vector3.Up * 0.035f), c);
            }
        }
        if (Hovered is { Active: true } && _byMan.TryGetValue(Hovered, out var hv))
            _rings.Add(new Transform3D(Basis.FromScale(new Vector3(1.22f, 1f, 1.22f)), hv.GroundPos + Vector3.Up * 0.04f), HoveredManColor);
        if (Inspected is { Active: true } && _byMan.TryGetValue(Inspected, out var iv))
        {
            float pulse = 1.1f + 0.08f * Mathf.Sin((float)Time.GetTicksMsec() * 0.008f);
            _rings.Add(new Transform3D(Basis.FromScale(new Vector3(pulse, 1f, pulse)), iv.GroundPos + Vector3.Up * 0.045f), InspectedColor);
            _marker.Visible = true;
            float bob = 0.06f * Mathf.Sin((float)Time.GetTicksMsec() * 0.005f);
            _marker.Position = iv.GroundPos + Vector3.Up * (Mathf.Max(0.6f, iv.HeadHeight) + 0.32f + bob);
        }
        else
        {
            _marker.Visible = false;
        }
        _rings.Commit();
    }

    void DrawBanners(float simDt, Camera3D cam)
    {
        var camPos = cam?.GlobalPosition ?? Vector3.Zero;
        _bannerOrder.Clear();
        foreach (var u in _battle.Battalions)
        {
            var b = EnsureBanner(u);
            var c = u.Centroid;
            float dist = cam != null ? camPos.DistanceTo(b.Base) : 60f;
            b.Refresh(new Vector3(c.X, BannerView.Height, c.Y), simDt, IsSelected != null && IsSelected(u), HoveredUnit == u, PlayerTeam, dist);
            if (b.Visible) _bannerOrder.Add(b);
        }
        Declutter(cam);
        float realDt = (float)GetProcessDeltaTime();
        foreach (var b in _banners.Values) b.ApplyLift(realDt);
    }

    readonly List<BannerView> _bannerOrder = new();
    static readonly Comparison<BannerView> ByKey = (a, b) => a.SortKey.CompareTo(b.SortKey);
    readonly List<(float x, float y, float w, float h)> _placed = new();

    /// <summary>
    /// Banners of units standing close together would print over each other: stack them, nearest the camera
    /// lowest, by lifting the others just enough (in world height) to clear.
    /// </summary>
    void Declutter(Camera3D cam)
    {
        foreach (var b in _bannerOrder) b.LiftTarget = 0f;
        if (cam == null || !cam.IsInsideTree()) return;
        float tanHalf = Mathf.Tan(Mathf.DegToRad(cam.Fov) * 0.5f);
        float viewH = GetViewport().GetVisibleRect().Size.Y;
        if (viewH < 16f) return;
        var camPos = cam.GlobalPosition;
        // Nearest the camera (lowest on screen) keeps its place.
        foreach (var b in _bannerOrder) b.SortKey = camPos.DistanceSquaredTo(b.Base);
        _bannerOrder.Sort(ByKey);
        _placed.Clear();
        foreach (var b in _bannerOrder)
        {
            if (cam.IsPositionBehind(b.Base)) continue;
            var sp = cam.UnprojectPosition(b.Base);
            float w = b.ScreenWidth, h = b.ScreenHeight;
            float y = sp.Y;
            for (int pass = 0; pass < 8; pass++)
            {
                bool moved = false;
                foreach (var p in _placed)
                {
                    if (Mathf.Abs(sp.X - p.x) < (w + p.w) * 0.5f && Mathf.Abs(y - p.y) < (h + p.h) * 0.5f)
                    {
                        y = p.y - (h + p.h) * 0.5f - 2f;
                        moved = true;
                    }
                }
                if (!moved) break;
            }
            _placed.Add((sp.X, y, w, h));
            float metresPerPixel = 2f * camPos.DistanceTo(b.Base) * tanHalf / viewH;
            b.LiftTarget = (sp.Y - y) * metresPerPixel;
        }
    }

    /// <summary>World position of a battalion's floating banner, for screen-space picking.</summary>
    public Vector3 BannerPosition(Battalion u)
    {
        if (u == null || !_banners.TryGetValue(u, out var b) || !b.Placed) return Vector3.Zero;
        return b.Position;
    }

    // ---------------------------------------------------------------- order preview

    /// <summary>Ghost markers on the ground where a pending order would put each man. Replaces any previous preview.</summary>
    public void SetPreview(IReadOnlyList<Vector2> positions, Color color)
    {
        if (_preview == null) return;
        var c = color;
        if (c.A > 0.6f) c.A = 0.55f;
        _preview.Begin();
        if (positions != null)
            for (int i = 0; i < positions.Count; i++)
                _preview.Add(new Transform3D(Basis.Identity, new Vector3(positions[i].X, 0.02f, positions[i].Y)), c);
        _preview.Commit();
    }

    public void ClearPreview()
    {
        if (_preview == null) return;
        _preview.Begin();
        _preview.Commit();
    }
}
