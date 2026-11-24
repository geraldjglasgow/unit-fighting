using Godot;
using UnitFighting.Sim;

namespace UnitFighting.View;

/// <summary>
/// A battalion's floating standard: a small team-coloured flag with its name and strength above it,
/// drawn at a fixed screen size over everything, hovering above the (smoothed) centroid of its men.
/// </summary>
public sealed partial class BannerView : Node3D
{
    public const float Height = 4.5f;

    public Battalion Unit { get; private set; }
    public bool Placed;

    /// <summary>Smoothed anchor above the centroid; the drawn position is this raised by <see cref="Lift"/>.</summary>
    public Vector3 Base;
    public float Lift, LiftTarget, SortKey;
    /// <summary>Rough on-screen footprint in pixels (for decluttering).</summary>
    public float ScreenWidth => (Mathf.Max(8, Unit.Name.Length) * 8.2f + 14f) * _zoom;
    public float ScreenHeight => 62f * _zoom;

    Label3D _label;
    MeshInstance3D _flag;
    StandardMaterial3D _flagMat;
    int _lastKey = int.MinValue;
    float _zoom = -1f;
    bool _selected;

    static ArrayMesh _romanFlag, _gaulFlag;

    public static readonly Color RomeText = new(1f, 0.74f, 0.64f);
    public static readonly Color GaulText = new(0.74f, 0.95f, 0.68f);

    public void Init(Battalion u)
    {
        Unit = u;
        Name = "Banner_" + u.Id;
        _flagMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            FixedSize = true,
            NoDepthTest = true,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            RenderPriority = 10,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BillboardKeepScale = true,
        };
        _flag = new MeshInstance3D
        {
            Mesh = u.Team == 0 ? RomanFlag() : GaulFlag(),
            MaterialOverride = _flagMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_flag);
        _label = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FixedSize = true,
            NoDepthTest = true,
            Shaded = false,
            PixelSize = 0.00052f,
            FontSize = 30,
            LineSpacing = -6f,
            OutlineSize = 9,
            OutlineModulate = new Color(0f, 0f, 0f, 0.75f),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderPriority = 12,
            OutlineRenderPriority = 11,
            Position = new Vector3(0f, 0.0f, 0f),
            Offset = new Vector2(0f, 10f),
        };
        AddChild(_label);
    }

    public void ApplyLift(float realDt)
    {
        Lift += (LiftTarget - Lift) * (1f - Mathf.Exp(-8f * realDt));
        Position = Base + Vector3.Up * Lift;
    }

    /// <summary>Flag in the billboard's plane: crossbar at y = 0, cloth hanging below. Units are fixed-size units.</summary>
    static ArrayMesh RomanFlag()
    {
        if (_romanFlag != null) return _romanFlag;
        var b = new MeshBuilder();
        var n = Vector3.Back;
        var gold = new Color(1f, 0.8f, 0.32f);
        var red = new Color(0.78f, 0.1f, 0.07f);
        const float w = 0.019f, h = 0.024f;
        b.Quad(new Vector3(-w * 0.62f, 0.0012f, 0), new Vector3(w * 0.62f, 0.0012f, 0), new Vector3(w * 0.62f, -0.0012f, 0), new Vector3(-w * 0.62f, -0.0012f, 0), n, gold);
        b.Tri(new Vector3(0, 0.0065f, 0), new Vector3(0.0022f, 0.0035f, 0), new Vector3(-0.0022f, 0.0035f, 0), n, gold);
        b.Quad(new Vector3(-0.0005f, 0.0035f, 0), new Vector3(0.0005f, 0.0035f, 0), new Vector3(0.0005f, 0f, 0), new Vector3(-0.0005f, 0f, 0), n, gold);
        b.Quad(new Vector3(-w * 0.5f, -0.001f, 0), new Vector3(w * 0.5f, -0.001f, 0), new Vector3(w * 0.5f, -h, 0), new Vector3(-w * 0.5f, -h, 0), n, red);
        // Gold fringe and a wreath-like band.
        b.Quad(new Vector3(-w * 0.5f, -h, 0), new Vector3(w * 0.5f, -h, 0), new Vector3(w * 0.5f, -h - 0.0022f, 0), new Vector3(-w * 0.5f, -h - 0.0022f, 0), n, gold);
        b.Quad(new Vector3(-w * 0.34f, -h * 0.45f, 0), new Vector3(w * 0.34f, -h * 0.45f, 0), new Vector3(w * 0.34f, -h * 0.55f, 0), new Vector3(-w * 0.34f, -h * 0.55f, 0), n, gold);
        return _romanFlag = b.Build();
    }

    static ArrayMesh GaulFlag()
    {
        if (_gaulFlag != null) return _gaulFlag;
        var b = new MeshBuilder();
        var n = Vector3.Back;
        var bronze = new Color(0.85f, 0.7f, 0.4f);
        var green = new Color(0.16f, 0.5f, 0.22f);
        var pale = new Color(0.9f, 0.88f, 0.75f);
        const float w = 0.019f, h = 0.026f;
        b.Quad(new Vector3(-w * 0.62f, 0.0012f, 0), new Vector3(w * 0.62f, 0.0012f, 0), new Vector3(w * 0.62f, -0.0012f, 0), new Vector3(-w * 0.62f, -0.0012f, 0), n, bronze);
        b.Tri(new Vector3(0, 0.0065f, 0), new Vector3(0.0024f, 0.0032f, 0), new Vector3(-0.0024f, 0.0032f, 0), n, bronze);
        b.Quad(new Vector3(-0.0005f, 0.0035f, 0), new Vector3(0.0005f, 0.0035f, 0), new Vector3(0.0005f, 0f, 0), new Vector3(-0.0005f, 0f, 0), n, bronze);
        // Swallow-tailed cloth.
        var tl = new Vector3(-w * 0.5f, -0.001f, 0);
        var tr = new Vector3(w * 0.5f, -0.001f, 0);
        var bl = new Vector3(-w * 0.5f, -h, 0);
        var br = new Vector3(w * 0.5f, -h, 0);
        var notch = new Vector3(0, -h * 0.7f, 0);
        b.Tri(tl, tr, notch, n, green);
        b.Tri(tl, notch, bl, n, green);
        b.Tri(tr, br, notch, n, green);
        b.Quad(new Vector3(-w * 0.5f, -h * 0.2f, 0), new Vector3(w * 0.5f, -h * 0.2f, 0), new Vector3(w * 0.5f, -h * 0.3f, 0), new Vector3(-w * 0.5f, -h * 0.3f, 0), n, pale);
        return _gaulFlag = b.Build();
    }

    public void Refresh(Vector3 target, float simDt, bool selected, bool hovered, int playerTeam, float camDistance)
    {
        var u = Unit;
        // Shrink a little when the camera is far out, so neighbouring banners don't run into each other.
        float zoom = Mathf.Snapped(Mathf.Clamp(1.12f - camDistance * 0.0042f, 0.6f, 1f), 0.04f);
        if (zoom != _zoom || selected != _selected)
        {
            _zoom = zoom;
            _selected = selected;
            _label.PixelSize = 0.00052f * zoom * (selected ? 1.08f : 1f);
            _flag.Scale = Vector3.One * zoom * (selected ? 1.15f : 1f);
        }
        if (!Placed)
        {
            Base = target;
            Placed = true;
        }
        else
        {
            Base = Base.Lerp(target, 1f - Mathf.Exp(-5f * simDt));
        }
        Position = Base + Vector3.Up * Lift;

        int state = u.Destroyed ? (u.Soldiers.TrueForAll(s => !s.Escaped) ? 5 : 6)
            : u.Routing ? (u.Shattered ? 4 : 3) : u.Wavering ? 2 : 1;
        int key = ((u.Alive * 8 + state) * 2 + (selected ? 1 : 0)) * 2 + (hovered ? 1 : 0);
        if (key == _lastKey) return;
        _lastKey = key;

        var team = u.Team == 0 ? RomeText : GaulText;
        string suffix = state switch
        {
            2 => "  WAVERING",
            3 => "  ROUTING",
            4 => "  SHATTERED",
            5 => "  destroyed",
            6 => "  fled",
            _ => "",
        };
        _label.Text = state >= 5 ? u.Name + "\n" + suffix.Trim() : $"{u.Name}\n{u.Alive} men{suffix}";
        Color text = state switch
        {
            2 => new Color(1f, 0.62f, 0.18f),
            3 or 4 => new Color(1f, 0.26f, 0.2f),
            5 or 6 => new Color(0.62f, 0.62f, 0.62f, 0.7f),
            _ => team,
        };
        if (selected && state < 5) text = state == 1 ? (u.Team == playerTeam ? new Color(0.78f, 1f, 0.72f) : new Color(1f, 0.8f, 0.75f)) : text.Lerp(Colors.White, 0.35f);
        else if (hovered && state < 5) text = text.Lerp(Colors.White, 0.3f);
        _label.Modulate = text;
        _label.OutlineModulate = selected
            ? (u.Team == playerTeam ? new Color(0.05f, 0.3f, 0.05f, 1f) : new Color(0.4f, 0.04f, 0.02f, 1f))
            : new Color(0f, 0f, 0f, 0.75f);
        _label.OutlineSize = selected ? 12 : 9;

        _flagMat.AlbedoColor = state >= 5 ? new Color(0.45f, 0.45f, 0.45f, 0.6f)
            : selected ? new Color(1.25f, 1.25f, 1.25f) : hovered ? new Color(1.1f, 1.1f, 1.1f) : new Color(0.92f, 0.92f, 0.92f);
        Visible = state != 6;
    }
}
