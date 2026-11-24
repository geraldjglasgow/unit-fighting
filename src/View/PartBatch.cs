using System;
using Godot;

namespace UnitFighting.View;

/// <summary>
/// One MultiMeshInstance3D refilled from scratch every frame: Begin, Add each instance, Commit.
/// The buffer is written directly (one RenderingServer call per batch per frame, no managed allocation).
/// Instances carry either a colour (for StandardMaterial3D users) or custom data (rgb colour + a flash, for the body shader).
/// </summary>
public sealed class PartBatch
{
    public readonly MultiMesh Multi;
    public readonly MultiMeshInstance3D Node;
    readonly bool _colors, _custom;
    readonly int _stride;
    float[] _buf = Array.Empty<float>();
    int _count, _capacity, _committed = -1;

    public int Count => _count;

    public PartBatch(Node3D parent, Mesh mesh, Material material, int capacity, bool colors, bool custom, bool shadows, string name = null)
    {
        _colors = colors;
        _custom = custom;
        _stride = 12 + (colors ? 4 : 0) + (custom ? 4 : 0);
        Multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = colors,
            UseCustomData = custom,
            Mesh = mesh,
            // Instances move every frame; a fixed box saves the engine re-deriving bounds each time.
            CustomAabb = new Aabb(new Vector3(-400f, -10f, -400f), new Vector3(800f, 30f, 800f)),
        };
        Node = new MultiMeshInstance3D
        {
            Multimesh = Multi,
            MaterialOverride = material,
            CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        };
        if (name != null) Node.Name = name;
        parent.AddChild(Node);
        Reserve(Math.Max(1, capacity));
    }

    public void Reserve(int capacity)
    {
        if (capacity <= _capacity) return;
        _capacity = capacity;
        Multi.InstanceCount = capacity;
        Multi.VisibleInstanceCount = 0;
        var old = _buf;
        _buf = new float[capacity * _stride];
        Array.Copy(old, _buf, Math.Min(old.Length, _buf.Length));
        _committed = -1;
    }

    public void Begin() => _count = 0;

    public void Add(in Transform3D t, Color c, float flash = 0f)
    {
        if (_count >= _capacity) Reserve(Math.Max(16, _capacity * 2));
        int o = _count * _stride;
        var b = t.Basis;
        var buf = _buf;
        buf[o] = b.X.X; buf[o + 1] = b.Y.X; buf[o + 2] = b.Z.X; buf[o + 3] = t.Origin.X;
        buf[o + 4] = b.X.Y; buf[o + 5] = b.Y.Y; buf[o + 6] = b.Z.Y; buf[o + 7] = t.Origin.Y;
        buf[o + 8] = b.X.Z; buf[o + 9] = b.Y.Z; buf[o + 10] = b.Z.Z; buf[o + 11] = t.Origin.Z;
        o += 12;
        if (_colors)
        {
            buf[o] = c.R; buf[o + 1] = c.G; buf[o + 2] = c.B; buf[o + 3] = c.A;
            o += 4;
        }
        if (_custom)
        {
            buf[o] = c.R; buf[o + 1] = c.G; buf[o + 2] = c.B; buf[o + 3] = flash;
        }
        _count++;
    }

    /// <summary>Uploads the instances added since Begin. Skips the upload when nothing is drawn twice running.</summary>
    public void Commit()
    {
        if (_count == 0 && _committed == 0) return;
        if (_count > 0) RenderingServer.MultimeshSetBuffer(Multi.GetRid(), new ReadOnlySpan<float>(_buf));
        Multi.VisibleInstanceCount = _count;
        _committed = _count;
    }
}
