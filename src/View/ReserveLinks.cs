using Godot;
using UnitFighting.Sim;

namespace UnitFighting.View;

/// <summary>
/// A faint dashed line on the ground from each of the player's reserves to the battalion it backs, with a small
/// arrowhead at the front's rear rank. Brighter while either of the pair is selected or hovered. Redrawn every frame
/// from the sim's reserve links (<see cref="Battalion.ReserveFor"/>).
/// </summary>
public sealed partial class ReserveLinks : Node3D
{
    public static readonly Color LinkColor = new(0.55f, 0.86f, 0.78f);
    const float Y = 0.03f, HalfWidth = 0.11f, Dash = 1.1f, GapLen = 0.7f, Arrow = 0.9f;

    /// <summary>The view whose battle, player team and selection this draws.</summary>
    public BattleView View;

    /// <summary>Links drawn in the last frame (for the self-test).</summary>
    public int Count { get; private set; }

    ImmediateMesh _mesh;
    MeshInstance3D _node;

    public ReserveLinks() => Name = "ReserveLinks";

    public override void _Ready()
    {
        _mesh = new ImmediateMesh();
        _node = new MeshInstance3D
        {
            Mesh = _mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DisableReceiveShadows = true,
            },
        };
        _node.SortingOffset = 0.5f;
        AddChild(_node);
    }

    public override void _Process(double delta)
    {
        _mesh.ClearSurfaces();
        Count = 0;
        var b = View?.Battle;
        if (b == null) return;
        bool open = false;
        foreach (var r in b.Battalions)
        {
            var f = r.ReserveFor;
            if (f == null || r.Team != View.PlayerTeam || r.Destroyed || f.Destroyed) continue;
            // From the middle of the reserve's front rank to the middle of the front's rear rank.
            var from = r.Centroid + MathUtil.Dir(r.Facing) * (r.Depth * 0.5f + 0.8f);
            var to = f.Centroid - MathUtil.Dir(f.Facing) * (f.Depth * 0.5f + 0.8f);
            var d = to - from;
            float len = d.Length();
            if (len < 2f || r.Relief is ReliefPhase.Joining or ReliefPhase.Passing) continue;   // closed up, or passing through each other
            bool lit = View.IsSelected(r) || View.IsSelected(f) || View.HoveredUnit == r || View.HoveredUnit == f;
            var c = new Color(LinkColor, lit ? 0.7f : 0.32f);
            if (!open)
            {
                _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
                open = true;
            }
            var dir = d / len;
            var side = new Vector2(-dir.Y, dir.X) * HalfWidth;
            float end = len - Arrow;
            for (float s = 0f; s < end; s += Dash + GapLen)
                Quad(from + dir * s, from + dir * Mathf.Min(s + Dash, end), side, c);
            // Arrowhead pointing at the front.
            var tip = from + dir * len;
            var baseC = tip - dir * Arrow;
            Tri(baseC + side * 4f, tip, baseC - side * 4f, c);
            Count++;
        }
        if (open) _mesh.SurfaceEnd();
    }

    void Quad(Vector2 a, Vector2 b, Vector2 side, Color c)
    {
        Tri(a + side, b + side, b - side, c);
        Tri(a + side, b - side, a - side, c);
    }

    void Tri(Vector2 a, Vector2 b, Vector2 c, Color col)
    {
        Vertex(a, col);
        Vertex(b, col);
        Vertex(c, col);
    }

    void Vertex(Vector2 p, Color col)
    {
        _mesh.SurfaceSetColor(col);
        _mesh.SurfaceAddVertex(new Vector3(p.X, Y, p.Y));
    }
}
