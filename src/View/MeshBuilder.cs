using System;
using System.Collections.Generic;
using Godot;

namespace UnitFighting.View;

/// <summary>
/// Merges transformed primitive pieces and hand-built polygons into one static ArrayMesh with vertex colours.
/// Colours are given in sRGB and stored linear.
/// Everything the battle draws is built once from these at startup and shared by every soldier.
/// </summary>
public sealed class MeshBuilder
{
    readonly List<Vector3> _v = new();
    readonly List<Vector3> _n = new();
    readonly List<Color> _c = new();
    readonly List<int> _i = new();

    public static Transform3D At(float x, float y, float z) => new(Basis.Identity, new Vector3(x, y, z));

    public static Transform3D At(Vector3 p, Basis b) => new(b, p);

    public static Basis Scale(float x, float y, float z) => Basis.FromScale(new Vector3(x, y, z));

    public static Basis RotX(float a) => new(Vector3.Right, a);
    public static Basis RotY(float a) => new(Vector3.Up, a);
    public static Basis RotZ(float a) => new(Vector3.Back, a);

    /// <summary>Adds a primitive mesh's triangles, transformed (non-uniform scale is fine: normals are corrected).</summary>
    public MeshBuilder Add(PrimitiveMesh mesh, Transform3D xf, Color? color = null)
    {
        var arr = mesh.GetMeshArrays();
        var verts = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var norms = arr[(int)Mesh.ArrayType.Normal].AsVector3Array();
        var idxVar = arr[(int)Mesh.ArrayType.Index];
        int[] idx = idxVar.VariantType == Variant.Type.Nil ? null : idxVar.AsInt32Array();
        var col = (color ?? Colors.White).SrgbToLinear();
        var nb = xf.Basis.Inverse().Transposed();
        int b = _v.Count;
        for (int k = 0; k < verts.Length; k++)
        {
            _v.Add(xf * verts[k]);
            _n.Add((nb * norms[k]).Normalized());
            _c.Add(col);
        }
        if (idx == null || idx.Length == 0)
            for (int k = 0; k < verts.Length; k++) _i.Add(b + k);
        else
            foreach (int k in idx) _i.Add(b + k);
        return this;
    }

    /// <summary>A flat triangle facing <paramref name="normal"/> (winding is fixed up automatically).</summary>
    public MeshBuilder Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Color col) => Tri(a, normal, b, normal, c, normal, col);

    /// <summary>A smooth triangle with per-vertex normals; faces the average normal.</summary>
    public MeshBuilder Tri(Vector3 a, Vector3 na, Vector3 b, Vector3 nb, Vector3 c, Vector3 nc, Color col)
    {
        var face = (b - a).Cross(c - a);
        // Godot treats clockwise triangles as front faces, i.e. (b-a)x(c-a) points away from the viewer.
        if (face.Dot(na + nb + nc) > 0f)
        {
            (b, c) = (c, b);
            (nb, nc) = (nc, nb);
        }
        int i = _v.Count;
        col = col.SrgbToLinear();
        _v.Add(a); _n.Add(na.Normalized()); _c.Add(col);
        _v.Add(b); _n.Add(nb.Normalized()); _c.Add(col);
        _v.Add(c); _n.Add(nc.Normalized()); _c.Add(col);
        _i.Add(i); _i.Add(i + 1); _i.Add(i + 2);
        return this;
    }

    public MeshBuilder Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color col)
    {
        Tri(a, b, c, normal, col);
        Tri(a, c, d, normal, col);
        return this;
    }

    /// <summary>
    /// A flat polygon drawn in the (Z, Y) plane — points are (z, y) — extruded along X from x0 to x1.
    /// Used for crests, axe heads and pennants.
    /// </summary>
    public MeshBuilder ExtrudeX(IList<Vector2> polyZY, float x0, float x1, Color col, Transform3D? xf = null)
    {
        var t = xf ?? Transform3D.Identity;
        var nb = t.Basis.Inverse().Transposed();
        var pts = new Vector2[polyZY.Count];
        for (int k = 0; k < pts.Length; k++) pts[k] = polyZY[k];
        var tris = Geometry2D.TriangulatePolygon(pts);
        Vector3 P(Vector2 p, float x) => t * new Vector3(x, p.Y, p.X);
        var nPos = (nb * Vector3.Right).Normalized();
        var nNeg = -nPos;
        for (int k = 0; k + 2 < tris.Length; k += 3)
        {
            Tri(P(pts[tris[k]], x1), P(pts[tris[k + 1]], x1), P(pts[tris[k + 2]], x1), nPos, col);
            if (x1 - x0 > 1e-5f) Tri(P(pts[tris[k]], x0), P(pts[tris[k + 1]], x0), P(pts[tris[k + 2]], x0), nNeg, col);
        }
        if (x1 - x0 <= 1e-5f) return this;
        // Signed area tells us which side of each edge is outside.
        float area = 0f;
        for (int k = 0; k < pts.Length; k++)
        {
            var p = pts[k];
            var q = pts[(k + 1) % pts.Length];
            area += p.X * q.Y - q.X * p.Y;
        }
        float sgn = area >= 0f ? 1f : -1f;
        for (int k = 0; k < pts.Length; k++)
        {
            var p = pts[k];
            var q = pts[(k + 1) % pts.Length];
            var e = q - p;
            // (u, v) = (z, y); outward normal for a CCW polygon is (ev, -eu).
            var local = new Vector3(0f, -e.X * sgn, e.Y * sgn);
            var n = (nb * local).Normalized();
            Quad(P(p, x0), P(q, x0), P(q, x1), P(p, x1), n, col);
        }
        return this;
    }

    public int VertexCount => _v.Count;

    public ArrayMesh Build()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _v.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _n.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _c.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = _i.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // ---------------------------------------------------------------- common shapes

    public static CylinderMesh Cyl(float top, float bottom, float height, int sides = 8, int rings = 1) =>
        new() { TopRadius = top, BottomRadius = bottom, Height = height, RadialSegments = sides, Rings = rings };

    public static SphereMesh Sphere(float r, int sides = 8, int rings = 5, bool hemi = false) =>
        new() { Radius = r, Height = hemi ? r : r * 2f, RadialSegments = sides, Rings = rings, IsHemisphere = hemi };

    public static BoxMesh Box(float x, float y, float z) => new() { Size = new Vector3(x, y, z) };

    /// <summary>A basis whose +Y runs along <paramref name="dir"/> (for laying cylinders along an axis).</summary>
    public static Basis AlongY(Vector3 dir)
    {
        var y = dir.Normalized();
        var hint = Mathf.Abs(y.Dot(Vector3.Forward)) > 0.9f ? Vector3.Right : Vector3.Forward;
        var x = hint.Cross(y).Normalized();
        var z = x.Cross(y).Normalized();
        return new Basis(x, y, z);
    }

    public static float Hash(int a, int b)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    public static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;

    public static float SmoothStep01(float x)
    {
        x = Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    public static float EaseOut(float x, float p = 2f) => 1f - MathF.Pow(1f - Clamp01(x), p);
}
