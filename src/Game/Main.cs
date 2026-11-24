using System;
using Godot;
using UnitFighting.Sim;
using UnitFighting.View;

namespace UnitFighting.Game;

/// <summary>
/// Bootstrap: builds the world, camera, input and HUD, then runs battles. The sim steps at a fixed rate from
/// an accumulator fed by scaled frame time, and the view interpolates between the last two steps.
/// A normal launch shows <see cref="SetupScreen"/> first; CLI flags used by tooling (--seed, --auto, --time,
/// --screenshot, --selftest, --quit-after, or a custom --rome/--gauls) skip straight to a battle, as before.
/// </summary>
public partial class Main : Node3D
{
    public static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f };
    const int MaxStepsPerFrame = 24;

    public static readonly Vector3 StartTarget = new(0f, 0f, -14f);
    public const float StartYaw = 0f, StartPitch = 0.62f, StartDistance = 70f;

    public enum GameScreen { Setup, Battle }

    public GameArgs Args { get; private set; }
    public Battle Battle { get; private set; }
    public BattleView View { get; private set; }
    public RtsCamera Camera { get; private set; }
    public PlayerInput Controls { get; private set; }
    public Hud Hud { get; private set; }
    public SetupScreen Setup { get; private set; }
    public GameScreen Screen { get; private set; } = GameScreen.Setup;

    public int Seed { get; private set; }
    public int SpeedIndex { get; private set; } = 3;
    public float TimeScale => Speeds[SpeedIndex];
    public bool Paused { get; private set; }
    public bool Autopilot { get; private set; }

    /// <summary>The AI commanding the player's army while autopilot is on (null when it's off).</summary>
    public PlayerAutopilot Pilot { get; private set; }

    float _acc;
    int _frame;
    bool _shotTaken;
    SelfTest _selfTest;

    public override void _Ready()
    {
        Args = GameArgs.Parse(OS.GetCmdlineUserArgs());
        Seed = Args.Seed ?? (int)(GD.Randi() % 100000);
        Autopilot = Args.Auto;
        if (Args.Speed is { } sp) SpeedIndex = NearestSpeed(sp);

        AddChild(new WorldView { Name = "World" });
        Camera = new RtsCamera { Name = "Camera" };
        AddChild(Camera);
        Camera.MakeCurrent();
        Controls = new PlayerInput { Name = "Controls" };
        AddChild(Controls);
        Controls.Init(this);
        Hud = new Hud { Name = "Hud" };
        AddChild(Hud);
        Hud.Init(this);
        Setup = new SetupScreen { Name = "Setup" };
        AddChild(Setup);
        Setup.Init(this);

        bool hasCustom = Args.Rome != null || Args.Gauls != null;
        // Tooling keeps working unchanged: any of these flags skips straight to a battle. --setup forces the
        // screen back on even so (e.g. to screenshot the screen itself with --screenshot --setup).
        bool skipScreen = !Args.Setup && (Args.SelfTest || Args.Screenshot != null || Args.QuitAfter >= 0 ||
                                           Args.Seed.HasValue || Args.Auto || Args.Time > 0f || hasCustom);
        if (skipScreen)
        {
            if (hasCustom) BeginCustomBattle(BuildSetupFromArgs(), Args.Time);
            else BeginDefaultBattle(Seed, Args.Time);
        }
        else
        {
            Setup.ApplyArgs(Args, Seed);
            ShowSetup();
        }

        if (Battle != null)
        {
            switch (Args.ShotView)
            {
                case "overview": OverviewCamera(); break;
                case "melee": MeleeCamera(); break;
            }
            if (Args.Select >= 0)
            {
                var own = Battle.Battalions.FindAll(u => u.Team == 0);
                if (Args.Select < own.Count) Controls.SelectUnit(own[Args.Select], false);
                else GD.PushWarning($"--select={Args.Select}: there are only {own.Count} player battalions");
            }
            if (Args.Inspect && Controls.Inspected == null) Controls.Inspected = PickFighter();
            if (Args.SelfTest) _selfTest = new SelfTest(this);
        }
    }

    // ---------------------------------------------------------------- battles

    BattleSetup BuildSetupFromArgs()
    {
        var setup = new BattleSetup { Seed = Seed, Autopilot = Autopilot };
        setup.Rome.AddRange(GameArgs.ParseGroups(Args.Rome));
        setup.Gauls.AddRange(GameArgs.ParseGroups(Args.Gauls));
        int men = Math.Max(1, Args.Men ?? 20);
        setup.RomeMen = Math.Max(1, Args.RomeMen ?? men);
        setup.GaulsMen = Math.Max(1, Args.GaulsMen ?? men);
        return setup;
    }

    public void BeginDefaultBattle(int seed, float fastForward = 0f)
    {
        Seed = seed;
        Begin(Scenarios.Default(seed, Autopilot), fastForward, isDefaultCam: true);
        Setup.SyncFromDefault(seed, Autopilot);
    }

    public void BeginCustomBattle(BattleSetup setup, float fastForward = 0f)
    {
        Seed = setup.Seed;
        Autopilot = setup.Autopilot;
        Begin(Scenarios.Custom(setup), fastForward, isDefaultCam: false);
        Setup.SyncFromSetup(setup);
    }

    /// <summary>Shared by <see cref="BeginDefaultBattle"/> and <see cref="BeginCustomBattle"/> once the Battle
    /// itself has been built: fast-forward, hand it to the view and HUD, place the camera, switch screens.</summary>
    void Begin(Battle battle, float fastForward, bool isDefaultCam)
    {
        if (View != null)
        {
            View.QueueFree();
            View = null;
        }
        Battle = battle;
        if (Args.ReserveOf >= 0)
        {
            var own = Battle.Battalions.FindAll(u => u.Team == 0);
            if (Args.ReserveOf < own.Count && Args.ReserveFront < own.Count) Battle.AssignReserve(own[Args.ReserveOf], own[Args.ReserveFront]);
        }
        // The player's AI runs from here rather than inside the battle, so that it can leave his own orders alone.
        var ai = Battle.AIs.Find(a => a.Team == 0);
        if (ai != null) Battle.AIs.Remove(ai);
        Pilot = ai != null ? new PlayerAutopilot(ai) : null;
        if (fastForward > 0f)
        {
            int steps = Mathf.RoundToInt(fastForward / Battle.FixedDt);
            for (int i = 0; i < steps; i++)
            {
                StepBattle();
                if (Battle.Events.Count > 1000) Battle.Events.Clear();
            }
            Battle.Events.Clear();
        }

        View = new BattleView { PlayerTeam = 0 };
        AddChild(View);
        View.Build(Battle);
        View.IsSelected = Controls.IsSelected;
        _acc = 0f;

        Controls.Bind(Battle, View);
        bool showHelp = Args.HelpOverlay || (Args.Screenshot == null && !Args.SelfTest);
        Hud.Bind(Battle, showHelp, Args.HelpOverlay);

        Screen = GameScreen.Battle;
        Setup.Visible = false;
        Hud.Visible = true;
        Camera.KeyboardEnabled = true;
        Camera.Follow = null;
        PlaceStartCamera(Battle, isDefaultCam);
        GD.Print($"[game] battle seed {Seed}{(Autopilot ? " (autopilot)" : "")}{(fastForward > 0f ? $", fast-forwarded {fastForward:0.#} s" : "")}");
    }

    void PlaceStartCamera(Battle battle, bool isDefault)
    {
        if (isDefault)
        {
            Camera.SnapTo(StartTarget, StartYaw, StartPitch, StartDistance);
            return;
        }
        // A custom battle's deployment can be almost any shape (1 battalion of 10 up to 8 of 60, second lines,
        // ...), so frame it from the actual soldiers' bounding box rather than the Default scenario's constants.
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        float romeSumY = 0f, gaulSumY = 0f, gaulFrontZ = float.MaxValue;
        int romeN = 0, gaulN = 0;
        foreach (var s in battle.Soldiers)
        {
            minX = Mathf.Min(minX, s.Pos.X); maxX = Mathf.Max(maxX, s.Pos.X);
            minZ = Mathf.Min(minZ, s.Pos.Y); maxZ = Mathf.Max(maxZ, s.Pos.Y);
            if (s.Team == 0) { romeSumY += s.Pos.Y; romeN++; }
            else { gaulSumY += s.Pos.Y; gaulN++; gaulFrontZ = Mathf.Min(gaulFrontZ, s.Pos.Y); }
        }
        if (romeN == 0 || gaulN == 0)
        {
            Camera.SnapTo(StartTarget, StartYaw, StartPitch, StartDistance);
            return;
        }
        float romeY = romeSumY / romeN, gaulY = gaulSumY / gaulN;
        // Biased toward the player's own line, the same fraction of the gap Default's fixed -14 is of its ±32.
        float targetY = Mathf.Lerp(romeY, gaulY, 0.28125f);
        float targetX = (minX + maxX) * 0.5f;
        float halfExtent = Mathf.Max((maxX - minX) * 0.5f, (maxZ - minZ) * 0.5f);
        float distance = Mathf.Clamp(halfExtent * 1.9f, 45f, 165f);
        Camera.SnapTo(new Vector3(targetX, 0f, targetY), StartYaw, StartPitch, distance);

        // Many battalions wrap the HUD's cards into two rows, which would hide the player's rear line: slide the view back
        // over it, drawing out a little if need be, until his whole army and the enemy's front rank are clear of the
        // cards and the top bar. If they can't both be, his own army comes first: the men stay big enough to read.
        var band = Hud.OpenBand();
        float top = band.X + 45f, bottom = band.Y - 12f;   // room above the far men for their banners and labels
        float maxDistance = Mathf.Min(distance * 1.3f, 165f);
        for (int i = 0; i < 40; i++)
        {
            float near = Camera.UnprojectPosition(new Vector3(targetX, 0f, minZ - 2f)).Y;
            float far = Camera.UnprojectPosition(new Vector3(targetX, 0f, gaulFrontZ)).Y;
            if (near <= bottom && far >= top) break;
            if (near - far > bottom - top && distance < maxDistance) distance = Mathf.Min(distance * 1.04f, maxDistance);
            else
            {
                float up = near > bottom ? near - bottom : -Mathf.Min(top - far, bottom - near);   // pixels to lift the view
                if (Mathf.Abs(up) < 1f) break;
                // Slide by about the metres that is worth at the near edge.
                float perMetre = near - Camera.UnprojectPosition(new Vector3(targetX, 0f, minZ - 1f)).Y;
                targetY -= up / Mathf.Max(perMetre, 1f);
            }
            Camera.SnapTo(new Vector3(targetX, 0f, targetY), StartYaw, StartPitch, distance);
        }
    }

    /// <summary>F5, and the end screen's button: back to the setup screen with the previous choices kept. Kept
    /// this name (rather than "ReturnToSetup") so PlayerInput.cs and Hud.cs's end-screen button, both owned by
    /// other agents this wave, don't need to change their call sites.</summary>
    public void NewBattle()
    {
        if (View != null)
        {
            View.QueueFree();
            View = null;
        }
        Battle = null;
        Pilot = null;
        Controls.Bind(null, null);
        ShowSetup();
    }

    void ShowSetup()
    {
        Screen = GameScreen.Setup;
        Camera.KeyboardEnabled = false;
        Hud.Visible = false;
        Setup.Visible = true;
        Setup.RefreshTotals();
    }

    public void TogglePause() => Paused = !Paused;

    public void ChangeSpeed(int step) => SpeedIndex = Math.Clamp(SpeedIndex + step, 0, Speeds.Length - 1);

    static int NearestSpeed(float s)
    {
        int best = 3;
        for (int i = 0; i < Speeds.Length; i++)
            if (Mathf.Abs(Speeds[i] - s) < Mathf.Abs(Speeds[best] - s)) best = i;
        return best;
    }

    /// <summary>Autopilot: a BattleAI commands the player's army too (see <see cref="PlayerAutopilot"/>).</summary>
    public void SetAutopilot(bool on)
    {
        Autopilot = on;
        if (!on) Pilot = null;
        else Pilot ??= new PlayerAutopilot(new BattleAI(0) { AdvanceAfter = 20f });
    }

    /// <summary>The player has just ordered u himself: the autopilot leaves it to carry the order out.</summary>
    public void PlayerOrdered(Battalion u) => Pilot?.Ordered(u);

    /// <summary>One fixed step of the battle, with the player's autopilot if it is on.</summary>
    public void StepBattle()
    {
        Battle.Step(Battle.FixedDt);
        Pilot?.Update(Battle, Battle.FixedDt);
    }

    // ---------------------------------------------------------------- frame

    public override void _Process(double delta)
    {
        if (Screen == GameScreen.Battle)
        {
            float dt = (float)delta;
            if (!Paused)
            {
                _acc += dt * TimeScale;
                int steps = 0;
                while (_acc >= Battle.FixedDt && steps < MaxStepsPerFrame)
                {
                    StepBattle();
                    _acc -= Battle.FixedDt;
                    steps++;
                }
                if (_acc >= Battle.FixedDt) _acc = 0f;   // too far behind: drop the backlog rather than spiral
            }
            float alpha = Mathf.Clamp(_acc / Battle.FixedDt, 0f, 1f);

            Hud.CollectEvents(Battle);
            Controls.Tick();
            View.Sync(alpha, Paused ? 0f : dt * TimeScale);
            Hud.Refresh();
        }

        _frame++;
        _selfTest?.Step();
        if (Args.Screenshot != null && !_shotTaken && _frame >= Args.ShotFrames)
        {
            _shotTaken = true;
            CaptureAndQuit(Args.Screenshot);
        }
        if (Battle != null && Args.QuitAfter >= 0 && _frame >= Args.QuitAfter && Args.Screenshot == null && _selfTest == null)
        {
            GD.Print($"[game] quit after {_frame} frames, battle time {Battle.Time:0.0} s, " +
                     $"Rome {Battle.Strength(0)} standing / {Battle.Dead(0)} dead, Gauls {Battle.Strength(1)} / {Battle.Dead(1)}, winner {Battle.Winner}");
            GetTree().Quit();
        }
    }

    async void CaptureAndQuit(string path)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = GetViewport().GetTexture()?.GetImage();
        if (img == null || img.IsEmpty())
        {
            GD.PrintErr("[shot] no image to save (headless?)");
            GetTree().Quit(1);
            return;
        }
        var dir = path.GetBaseDir();
        if (dir != "" && !DirAccess.DirExistsAbsolute(dir)) DirAccess.MakeDirRecursiveAbsolute(dir);
        var err = img.SavePng(path);
        if (err == Error.Ok) GD.Print($"[shot] saved {path} ({img.GetWidth()}x{img.GetHeight()})");
        else GD.PrintErr($"[shot] failed to save {path}: {err}");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    // ---------------------------------------------------------------- camera presets

    void OverviewCamera()
    {
        var c = Vector2.Zero;
        int n = 0;
        foreach (var s in Battle.Soldiers)
        {
            if (!s.Active) continue;
            c += s.Pos;
            n++;
        }
        if (n > 0) c /= n;
        Camera.SnapTo(new Vector3(c.X, 0f, c.Y), 0f, 1.15f, 115f);
    }

    /// <summary>Close on the thickest fighting: a man winding up a blow nearest the middle of everyone engaged.</summary>
    void MeleeCamera()
    {
        var c = Vector2.Zero;
        int n = 0;
        foreach (var s in Battle.Soldiers)
        {
            if (!s.Active || s.Target == null) continue;
            c += s.Pos;
            n++;
        }
        if (n == 0)
        {
            GD.Print("[game] melee view: nobody is fighting yet; showing the overview");
            OverviewCamera();
            return;
        }
        c /= n;
        Soldier best = null;
        float bestD = float.MaxValue;
        foreach (var s in Battle.Soldiers)
        {
            if (!s.Active || s.Target == null) continue;
            float d = s.Pos.DistanceSquaredTo(c) * (s.Phase == CombatPhase.Windup ? 1f : 4f) + 1f;
            if (d < bestD) { bestD = d; best = s; }
        }
        var mid = (best.Pos + best.Target.Pos) * 0.5f;
        // Side-on to the duel, looking roughly north like the player.
        float yaw = best.Facing + Mathf.Pi * 0.5f;
        if (MathUtil.Dir(yaw).Y < 0f) yaw += Mathf.Pi;
        Camera.SnapTo(new Vector3(mid.X, 0.9f, mid.Y), yaw, 0.45f, 12f);
        if (Args.Inspect) Controls.Inspected = best;
    }

    /// <summary>Someone worth watching: a man who is fighting, from the selection if possible.</summary>
    Soldier PickFighter()
    {
        Soldier Find(Func<Soldier, bool> ok)
        {
            Soldier best = null;
            foreach (var s in Battle.Soldiers)
                if (s.Active && ok(s) && (best == null || s.Rating > best.Rating)) best = s;
            return best;
        }
        return Find(s => s.Target != null && Controls.IsSelected(s.Unit))
            ?? Find(s => s.Target != null && s.Team == 0)
            ?? Find(s => s.Target != null)
            ?? Find(s => Controls.IsSelected(s.Unit))
            ?? Find(s => s.Team == 0);
    }
}
