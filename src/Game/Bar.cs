using Godot;

namespace UnitFighting.Game;

/// <summary>A thin rounded meter for the HUD. Value is 0..1.</summary>
public partial class Bar : Control
{
    float _value;
    Color _fill = Colors.White;
    public Color Back = new(0f, 0f, 0f, 0.45f);

    public Bar() : this(new Vector2(100, 8)) { }

    public Bar(Vector2 minSize)
    {
        CustomMinimumSize = minSize;
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public void Set(float value, Color fill)
    {
        value = Mathf.Clamp(float.IsFinite(value) ? value : 0f, 0f, 1f);
        if (Mathf.IsEqualApprox(value, _value) && fill.IsEqualApprox(_fill)) return;
        _value = value;
        _fill = fill;
        QueueRedraw();
    }

    public override void _Draw()
    {
        float r = Mathf.Min(3f, Size.Y * 0.5f);
        DrawStyle(new Rect2(Vector2.Zero, Size), Back, r);
        if (_value > 0.001f)
            DrawStyle(new Rect2(Vector2.Zero, new Vector2(Mathf.Max(Size.X * _value, r * 2f), Size.Y)), _fill, r);
    }

    readonly StyleBoxFlat _sb = new() { AntiAliasing = true };

    void DrawStyle(Rect2 rect, Color c, float r)
    {
        _sb.BgColor = c;
        _sb.SetCornerRadiusAll((int)r);
        DrawStyleBox(_sb, rect);   // the colour is baked into the draw command, so one box serves both
    }
}
