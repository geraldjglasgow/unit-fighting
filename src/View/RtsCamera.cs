using System;
using Godot;
using UnitFighting.Sim;

namespace UnitFighting.View;

/// <summary>
/// Total War style camera orbiting a ground point. Handles its own input: WASD / arrows pan, Q/E rotate,
/// mouse wheel zoom, middle-mouse drag orbits. Uses real (unscaled) frame time so it works while paused.
/// Yaw uses the sim angle convention: the camera looks horizontally along MathUtil.Dir(Yaw) = (sin, cos) in (x, z).
/// Pitch is the downward angle from horizontal in radians. Distance is metres from Target.
///
/// Target / Yaw / Pitch / Distance are where the camera is heading; it glides there exponentially.
/// </summary>
public partial class RtsCamera : Camera3D
{
    public Vector3 Target;
    public float Yaw;
    public float Pitch = 0.9f;
    public float Distance = 60f;

    /// <summary>When set, the camera target tracks this soldier until the player pans. Set null to stop.</summary>
    public Soldier Follow;

    public bool KeyboardEnabled = true;

    // ---- additions
    public const float MinPitch = 0.2f, MaxPitch = 1.45f, MinDistance = 4f, MaxDistance = 180f;

    /// <summary>How far the target may wander from the origin.</summary>
    public float Bounds = 220f;

    /// <summary>Render interpolation alpha for <see cref="Follow"/>; BattleView.Sync keeps it current.</summary>
    public float InterpAlpha = 1f;

    /// <summary>Zoom toward the ground point under the cursor (otherwise toward the target).</summary>
    public bool ZoomToCursor = true;

    /// <summary>The smoothed state actually shown this frame.</summary>
    public Vector3 CurrentTarget => _t;
    public float CurrentYaw => _yaw;
    public float CurrentPitch => _pitch;
    public float CurrentDistance => _dist;

    Vector3 _t;
    float _yaw, _pitch, _dist;
    bool _init;
    ulong _lastUsec;

    public override void _Ready()
    {
        Near = 0.1f;
        Far = 1500f;
        Fov = 55f;
        ProcessMode = ProcessModeEnum.Always;
        if (!_init) SnapTo(Target, Yaw, Pitch, Distance);
        _lastUsec = Time.GetTicksUsec();
    }

    /// <summary>Jump there immediately with no smoothing.</summary>
    public void SnapTo(Vector3 target, float yaw, float pitch, float distance)
    {
        Target = ClampTarget(target);
        Yaw = yaw;
        Pitch = Mathf.Clamp(pitch, MinPitch, MaxPitch);
        Distance = Mathf.Clamp(distance, MinDistance, MaxDistance);
        _t = Target;
        _yaw = Yaw;
        _pitch = Pitch;
        _dist = Distance;
        _init = true;
        Apply();
    }

    /// <summary>Glide there.</summary>
    public void FocusOn(Vector3 target, float? distance = null)
    {
        Target = ClampTarget(target);
        if (distance.HasValue) Distance = Mathf.Clamp(distance.Value, MinDistance, MaxDistance);
    }

    Vector3 ClampTarget(Vector3 t) => new(Mathf.Clamp(t.X, -Bounds, Bounds), Mathf.Clamp(t.Y, 0f, 3f), Mathf.Clamp(t.Z, -Bounds, Bounds));

    public override void _Process(double delta)
    {
        ulong nowUsec = Time.GetTicksUsec();
        float dt = Math.Clamp((nowUsec - _lastUsec) / 1_000_000f, 0f, 0.1f);
        _lastUsec = nowUsec;
        if (!_init) SnapTo(Target, Yaw, Pitch, Distance);

        if (KeyboardEnabled) KeyboardInput(dt);

        if (Follow != null)
        {
            if (Follow.Escaped) Follow = null;
            else
            {
                var p = Follow.PrevPos.Lerp(Follow.Pos, Mathf.Clamp(InterpAlpha, 0f, 1f));
                Target = ClampTarget(new Vector3(p.X, Target.Y, p.Y));
            }
        }

        Pitch = Mathf.Clamp(Pitch, MinPitch, MaxPitch);
        Distance = Mathf.Clamp(Distance, MinDistance, MaxDistance);
        Target = ClampTarget(Target);

        float kt = 1f - Mathf.Exp(-(Follow != null ? 12f : 9f) * dt);
        float kr = 1f - Mathf.Exp(-14f * dt);
        float kz = 1f - Mathf.Exp(-10f * dt);
        _t = _t.Lerp(Target, kt);
        _yaw += MathUtil.AngleDiff(_yaw, Yaw) * kr;
        _pitch += (Pitch - _pitch) * kr;
        _dist += (Distance - _dist) * kz;
        Apply();
    }

    void KeyboardInput(float dt)
    {
        bool mod = Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Meta) || Input.IsKeyPressed(Key.Alt);
        float fwd = 0f, right = 0f, turn = 0f;
        if (!mod)
        {
            if (Input.IsPhysicalKeyPressed(Key.W)) fwd += 1f;
            if (Input.IsPhysicalKeyPressed(Key.S)) fwd -= 1f;
            if (Input.IsPhysicalKeyPressed(Key.D)) right += 1f;
            if (Input.IsPhysicalKeyPressed(Key.A)) right -= 1f;
            if (Input.IsPhysicalKeyPressed(Key.Q)) turn += 1f;
            if (Input.IsPhysicalKeyPressed(Key.E)) turn -= 1f;
        }
        if (Input.IsKeyPressed(Key.Up)) fwd += 1f;
        if (Input.IsKeyPressed(Key.Down)) fwd -= 1f;
        if (Input.IsKeyPressed(Key.Right)) right += 1f;
        if (Input.IsKeyPressed(Key.Left)) right -= 1f;
        bool fast = Input.IsKeyPressed(Key.Shift);

        if (fwd != 0f || right != 0f)
        {
            Follow = null;
            float speed = (6f + Distance * 0.9f) * (fast ? 2.6f : 1f);
            var f = new Vector3(Mathf.Sin(Yaw), 0f, Mathf.Cos(Yaw));
            var r = new Vector3(-Mathf.Cos(Yaw), 0f, Mathf.Sin(Yaw));   // MathUtil.Right of the view direction
            var move = (f * fwd + r * right).Normalized() * speed * dt;
            Target += move;
            _t += move * 0.35f;   // start moving now rather than after the glide catches up
        }
        if (turn != 0f) Yaw += turn * (fast ? 2.6f : 1.5f) * dt;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true } mb when mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown:
            {
                float f = mb.Factor > 0f ? mb.Factor : 1f;
                Zoom(Mathf.Pow(0.86f, mb.ButtonIndex == MouseButton.WheelUp ? f : -f), mb.Position);
                GetViewport().SetInputAsHandled();
                break;
            }
            case InputEventMagnifyGesture mg:
                Zoom(1f / Mathf.Max(0.2f, mg.Factor), mg.Position);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventPanGesture pg when Mathf.Abs(pg.Delta.Y) > Mathf.Abs(pg.Delta.X):
                Zoom(Mathf.Pow(1.04f, pg.Delta.Y), pg.Position);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseMotion mm when (mm.ButtonMask & MouseButtonMask.Middle) != 0:
            {
                float dy = mm.Relative.X * 0.006f;
                float dp = mm.Relative.Y * 0.005f;
                Yaw += dy;
                _yaw += dy;
                float np = Mathf.Clamp(Pitch + dp, MinPitch, MaxPitch);
                _pitch += np - Pitch;
                Pitch = np;
                GetViewport().SetInputAsHandled();
                break;
            }
        }
    }

    void Zoom(float factor, Vector2 screen)
    {
        float old = Distance;
        float nd = Mathf.Clamp(old * factor, MinDistance, MaxDistance);
        if (Mathf.IsEqualApprox(nd, old)) return;
        float k = nd / old;
        if (ZoomToCursor && Follow == null && GroundUnder(screen, out var p))
        {
            // Scale the camera about the point under the cursor, so that point stays put on screen.
            var off = Target - p;
            off.Y = 0f;
            var anchor = new Vector3(p.X, Target.Y, p.Z);
            Target = ClampTarget(anchor + off * k);
        }
        Distance = nd;
    }

    /// <summary>Where a screen point's ray meets the ground (y = 0), if it does and not absurdly far away.</summary>
    public bool GroundUnder(Vector2 screen, out Vector3 point)
    {
        point = default;
        var o = ProjectRayOrigin(screen);
        var d = ProjectRayNormal(screen);
        if (d.Y > -0.02f) return false;
        float t = -o.Y / d.Y;
        point = o + d * t;
        var flat = new Vector3(point.X - Target.X, 0f, point.Z - Target.Z);
        float max = Distance * 3f + 20f;
        if (flat.Length() > max) point = new Vector3(Target.X, 0f, Target.Z) + flat.Normalized() * max;
        return true;
    }

    void Apply()
    {
        var fwd = new Vector3(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));
        // Close in, look at a man's chest rather than his feet.
        float lookH = Mathf.Max(_t.Y, Mathf.Clamp(1.25f - _dist * 0.045f, 0f, 1.05f));
        var look = new Vector3(_t.X, lookH, _t.Z);
        var pos = look - fwd * (Mathf.Cos(_pitch) * _dist) + Vector3.Up * (Mathf.Sin(_pitch) * _dist);
        if (pos.Y < 0.35f) pos.Y = 0.35f;
        Transform = new Transform3D(new Basis(Vector3.Up, _yaw + Mathf.Pi) * new Basis(Vector3.Right, -_pitch), pos);
    }
}
