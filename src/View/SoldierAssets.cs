using System;
using System.Collections.Generic;
using Godot;
using static UnitFighting.View.MeshBuilder;

namespace UnitFighting.View;

/// <summary>Every kind of body part / piece of kit. Each gets one shared mesh, one shared material and one MultiMesh.</summary>
public enum Part
{
    Thigh, Shin, Foot, Skirt, Torso, TorsoMail, Belt, Head, HelmetRoman, HelmetGaul, Crest, Hair, HairLong, VetGold,
    UpperArm, Sleeve, SleeveMail, Forearm,
    Gladius, Longsword, AxeHaft, AxeHead, SpearShaft, SpearHead,
    Scutum, ScutumTrim, ScutumDesign, BossScutum,
    Round, RoundRim, RoundDesign, BossRound,
    Oval, OvalSpine, OvalDesign, BossOval,
    Count,
}

public enum Surface { Cloth, Skin, Metal, Mail, Paint }

/// <summary>
/// The soldier kit: low-poly meshes built once from primitives and a handful of materials, all shared by every man
/// in every battle. Colours come per instance (MultiMesh custom data: rgb = colour in linear space, a = hit flash).
/// </summary>
public static class SoldierAssets
{
    // Skeleton, in metres. Torso frame origin is the pelvis joint.
    public const float PelvisHeight = 0.93f;
    public const float HipHalf = 0.085f;
    public const float Thigh = 0.44f, Shin = 0.44f, AnkleHeight = 0.075f;
    public const float UpperArm = 0.28f, Forearm = 0.28f;
    public const float NeckY = 0.47f;
    public static readonly Vector3 ShoulderR = new(-0.19f, 0.44f, 0f);
    public static readonly Vector3 ShoulderL = new(0.19f, 0.44f, 0f);

    static Mesh[] _meshes;
    static ShaderMaterial[] _surfaces;
    static Shader _bodyShader;

    public static Mesh MeshOf(Part p)
    {
        Ensure();
        return _meshes[(int)p];
    }

    public static Material MaterialOf(Part p)
    {
        Ensure();
        return _surfaces[(int)SurfaceOf(p)];
    }

    public static bool CastsShadow(Part p) => p is not (Part.VetGold or Part.ScutumDesign or Part.RoundDesign or Part.OvalDesign
        or Part.BossScutum or Part.BossRound or Part.BossOval or Part.ScutumTrim or Part.RoundRim or Part.Belt);

    public static Surface SurfaceOf(Part p) => p switch
    {
        Part.Head or Part.UpperArm or Part.Forearm => Surface.Skin,
        Part.TorsoMail or Part.SleeveMail => Surface.Mail,
        Part.HelmetRoman or Part.HelmetGaul or Part.VetGold or Part.Gladius or Part.Longsword or Part.AxeHead
            or Part.SpearHead or Part.ScutumTrim or Part.BossScutum or Part.BossRound or Part.BossOval or Part.RoundRim => Surface.Metal,
        Part.Scutum or Part.ScutumDesign or Part.Round or Part.RoundDesign or Part.Oval or Part.OvalDesign => Surface.Paint,
        _ => Surface.Cloth,
    };

    const string BodyShaderCode = @"
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;
uniform float rough = 0.85;
uniform float metal = 0.0;
uniform float spec = 0.35;
uniform float rings = 0.0;
varying vec4 v_custom;
varying vec3 v_local;
void vertex() {
    v_custom = INSTANCE_CUSTOM;
    v_local = VERTEX;
}
void fragment() {
    vec3 c = COLOR.rgb * v_custom.rgb;
    if (rings > 0.0) {
        float fade = 1.0 - smoothstep(4.0, 16.0, length(VERTEX));
        c *= 1.0 - rings * fade * (0.5 + 0.5 * sin(v_local.y * 330.0 + v_local.x * 40.0));
    }
    float f = v_custom.a;
    ALBEDO = mix(c, vec3(0.9, 0.05, 0.03), f * 0.75);
    EMISSION = vec3(0.7, 0.03, 0.01) * f;
    ROUGHNESS = rough;
    METALLIC = metal * (1.0 - f);
    SPECULAR = spec;
}
";

    static void Ensure()
    {
        if (_meshes != null) return;
        _bodyShader = new Shader { Code = BodyShaderCode };
        _surfaces = new ShaderMaterial[5];
        _surfaces[(int)Surface.Cloth] = Mat(0.9f, 0f, 0.25f);
        _surfaces[(int)Surface.Skin] = Mat(0.62f, 0f, 0.35f);
        _surfaces[(int)Surface.Metal] = Mat(0.36f, 0.7f, 0.5f);
        _surfaces[(int)Surface.Mail] = Mat(0.58f, 0.45f, 0.4f, 0.4f);
        _surfaces[(int)Surface.Paint] = Mat(0.72f, 0f, 0.3f);
        _meshes = new Mesh[(int)Part.Count];
        for (int i = 0; i < _meshes.Length; i++) _meshes[i] = BuildMesh((Part)i);
    }

    static ShaderMaterial Mat(float rough, float metal, float spec, float rings = 0f)
    {
        var m = new ShaderMaterial { Shader = _bodyShader };
        m.SetShaderParameter("rough", rough);
        m.SetShaderParameter("metal", metal);
        m.SetShaderParameter("spec", spec);
        m.SetShaderParameter("rings", rings);
        return m;
    }

    // ---------------------------------------------------------------- colours (authored in sRGB, stored linear)

    public static Color Lin(float r, float g, float b) => new Color(r, g, b).SrgbToLinear();

    static readonly Color Steel = new(0.78f, 0.8f, 0.84f);
    static readonly Color Bronze = new(0.62f, 0.44f, 0.2f);
    static readonly Color Wood = new(0.42f, 0.28f, 0.15f);
    static readonly Color DarkLeather = new(0.24f, 0.15f, 0.09f);

    // ---------------------------------------------------------------- meshes

    static Mesh BuildMesh(Part p)
    {
        var b = new MeshBuilder();
        switch (p)
        {
            case Part.Thigh:
                b.Add(Sphere(0.078f, 8, 4), At(0, 0, 0));
                b.Add(Cyl(0.078f, 0.058f, Thigh), At(0, -Thigh * 0.5f, 0));
                b.Add(Sphere(0.06f, 8, 4), At(0, -Thigh, 0));
                break;
            case Part.Shin:
                b.Add(Cyl(0.058f, 0.042f, Shin - 0.02f), At(0, -(Shin - 0.02f) * 0.5f, 0));
                b.Add(Sphere(1f, 8, 4), At(new Vector3(0, -0.13f, -0.012f), Scale(0.064f, 0.12f, 0.066f)));
                break;
            case Part.Foot:
                b.Add(Box(0.085f, 0.075f, 0.21f), At(0, -0.037f, 0.045f));
                break;
            case Part.Skirt:
                b.Add(Cyl(0.165f, 0.19f, 0.36f, 12), At(new Vector3(0, -0.1f, 0), Scale(1f, 1f, 0.78f)));
                break;
            case Part.Torso:
            case Part.TorsoMail:
                b.Add(Cyl(0.2f, 0.16f, 0.46f, 12), At(new Vector3(0, 0.21f, 0), Scale(1f, 1f, 0.62f)));
                b.Add(Sphere(1f, 12, 5), At(new Vector3(0, 0.43f, 0), Scale(0.2f, 0.08f, 0.124f)));
                b.Add(Sphere(1f, 10, 5), At(new Vector3(0, 0.3f, 0.035f), Scale(0.17f, 0.13f, 0.1f)));   // chest
                break;
            case Part.Belt:
                b.Add(Cyl(0.168f, 0.168f, 0.065f, 12), At(new Vector3(0, 0.02f, 0), Scale(1f, 1f, 0.66f)));
                b.Add(Box(0.07f, 0.06f, 0.02f), At(0, 0.02f, 0.11f), Bronze);
                break;
            case Part.Head:
                b.Add(Cyl(0.05f, 0.055f, 0.13f, 8), At(0, 0.05f, 0));
                b.Add(Sphere(1f, 12, 7), At(new Vector3(0, 0.165f, 0.005f), Scale(0.098f, 0.113f, 0.106f)));
                b.Add(Box(0.028f, 0.045f, 0.035f), At(0, 0.155f, 0.1f));
                break;
            case Part.HelmetRoman:
                b.Add(Sphere(0.123f, 12, 5, true), At(new Vector3(0, 0.178f, -0.004f), Scale(1f, 1.05f, 1.02f)));
                b.Add(Cyl(0.125f, 0.127f, 0.035f, 12), At(0, 0.185f, -0.004f));
                b.Add(Box(0.22f, 0.018f, 0.085f), At(new Vector3(0, 0.17f, -0.13f), RotX(-0.35f)));
                b.Add(Box(0.012f, 0.1f, 0.08f), At(0.112f, 0.13f, 0.03f));
                b.Add(Box(0.012f, 0.1f, 0.08f), At(-0.112f, 0.13f, 0.03f));
                b.Add(Sphere(0.024f, 6, 3), At(0, 0.312f, 0));
                break;
            case Part.HelmetGaul:
                b.Add(Sphere(0.12f, 12, 5, true), At(new Vector3(0, 0.178f, -0.004f), Scale(1f, 1.2f, 1.02f)));
                b.Add(Cyl(0.14f, 0.145f, 0.02f, 12), At(0, 0.18f, -0.004f));
                b.Add(Cyl(0f, 0.028f, 0.09f, 6), At(0, 0.36f, -0.004f));
                b.Add(Box(0.012f, 0.09f, 0.07f), At(0.11f, 0.13f, 0.03f));
                b.Add(Box(0.012f, 0.09f, 0.07f), At(-0.11f, 0.13f, 0.03f));
                break;
            case Part.Crest:
            {
                // A horsehair crest running front to back: a half-disc standing on the helmet's crown.
                var pts = new List<Vector2>();
                for (int i = 0; i <= 12; i++)
                {
                    float a = Mathf.Pi * i / 12f;
                    pts.Add(new Vector2(-0.02f + 0.165f * Mathf.Cos(a), 0.29f + 0.17f * Mathf.Sin(a)));
                }
                pts.Add(new Vector2(-0.185f, 0.25f));
                pts.Add(new Vector2(0.145f, 0.25f));
                b.ExtrudeX(pts, -0.028f, 0.028f, Colors.White);
                b.Add(Box(0.03f, 0.05f, 0.03f), At(0, 0.3f, -0.01f), Bronze);
                break;
            }
            case Part.Hair:
                b.Add(Sphere(0.116f, 12, 5, true), At(new Vector3(0, 0.165f, 0f), RotX(-0.75f)));
                break;
            case Part.HairLong:
                b.Add(Sphere(0.118f, 12, 5, true), At(new Vector3(0, 0.162f, 0f), RotX(-0.7f)));
                b.Add(Box(0.17f, 0.15f, 0.06f), At(new Vector3(0, 0.1f, -0.075f), RotX(0.15f)));
                b.Add(Box(0.075f, 0.017f, 0.022f), At(0, 0.115f, 0.098f));
                b.Add(Box(0.015f, 0.055f, 0.018f), At(0.037f, 0.092f, 0.094f));
                b.Add(Box(0.015f, 0.055f, 0.018f), At(-0.037f, 0.092f, 0.094f));
                break;
            case Part.VetGold:
            {
                var torc = new TorusMesh { InnerRadius = 0.052f, OuterRadius = 0.074f, Rings = 10, RingSegments = 5 };
                b.Add(torc, At(new Vector3(0, 0.03f, 0.008f), RotX(0.2f)));
                var band = new TorusMesh { InnerRadius = 0.108f, OuterRadius = 0.126f, Rings = 14, RingSegments = 4 };
                b.Add(band, At(new Vector3(0, 0.2f, -0.004f), RotX(-0.12f)));
                b.Add(Sphere(0.02f, 6, 3), At(0, 0.205f, 0.12f));
                break;
            }
            case Part.UpperArm:
                b.Add(Sphere(0.057f, 8, 4), At(0, 0, 0));
                b.Add(Cyl(0.055f, 0.046f, UpperArm), At(0, -UpperArm * 0.5f, 0));
                break;
            case Part.Sleeve:
            case Part.SleeveMail:
                b.Add(Sphere(1f, 8, 4), At(new Vector3(0, 0.005f, 0), Scale(0.074f, 0.068f, 0.074f)));
                b.Add(Cyl(0.072f, 0.066f, 0.15f), At(0, -0.07f, 0));
                break;
            case Part.Forearm:
                b.Add(Sphere(0.047f, 8, 4), At(0, 0, 0));
                b.Add(Cyl(0.046f, 0.036f, Forearm - 0.04f), At(0, -(Forearm - 0.04f) * 0.5f, 0));
                b.Add(Sphere(1f, 8, 4), At(new Vector3(0, -Forearm + 0.005f, 0.005f), Scale(0.042f, 0.052f, 0.05f)));
                break;
            case Part.Gladius:
                Blade(b, 0.05f, 0.46f, 0.028f, 0.09f);
                b.Add(Box(0.04f, 0.085f, 0.035f), At(0, 0, 0.03f), Bronze);
                b.Add(Cyl(0.017f, 0.017f, 0.1f, 6), At(new Vector3(0, 0, -0.03f), RotX(Mathf.Pi / 2)), Wood);
                b.Add(Sphere(0.027f, 8, 4), At(0, 0, -0.09f), Bronze);
                break;
            case Part.Longsword:
                Blade(b, 0.04f, 0.8f, 0.025f, 0.07f);
                b.Add(Box(0.03f, 0.13f, 0.025f), At(0, 0, 0.025f), Steel * 0.8f);
                b.Add(Cyl(0.016f, 0.016f, 0.13f, 6), At(new Vector3(0, 0, -0.04f), RotX(Mathf.Pi / 2)), DarkLeather);
                b.Add(Sphere(0.025f, 8, 4), At(0, 0, -0.11f), Steel * 0.8f);
                break;
            case Part.AxeHaft:
                b.Add(Cyl(0.019f, 0.021f, 0.84f, 6), At(new Vector3(0, 0, 0.2f), RotX(Mathf.Pi / 2)));
                break;
            case Part.AxeHead:
            {
                var pts = new[]
                {
                    new Vector2(0.5f, 0.028f), new Vector2(0.615f, 0.028f), new Vector2(0.6f, -0.045f),
                    new Vector2(0.675f, -0.165f), new Vector2(0.66f, -0.195f), new Vector2(0.43f, -0.19f),
                    new Vector2(0.41f, -0.17f), new Vector2(0.5f, -0.06f),
                };
                b.ExtrudeX(pts, -0.012f, 0.012f, Steel);
                b.Add(Cyl(0.03f, 0.03f, 0.1f, 6), At(0, 0.0f, 0.555f), Steel * 0.7f);
                break;
            }
            case Part.SpearShaft:
                b.Add(Cyl(0.017f, 0.019f, 2.5f, 6), At(new Vector3(0, 0, 0.4f), RotX(Mathf.Pi / 2)));
                break;
            case Part.SpearHead:
            {
                var leaf = new[]
                {
                    new Vector2(1.62f, 0.012f), new Vector2(1.73f, 0.038f), new Vector2(1.96f, 0f),
                    new Vector2(1.73f, -0.038f), new Vector2(1.62f, -0.012f),
                };
                b.ExtrudeX(leaf, -0.006f, 0.006f, Steel);
                b.ExtrudeX(leaf, -0.006f, 0.006f, Steel, new Transform3D(RotZ(Mathf.Pi / 2), Vector3.Zero));
                b.Add(Cyl(0.021f, 0.02f, 0.1f, 6), At(new Vector3(0, 0, 1.6f), RotX(Mathf.Pi / 2)), Steel * 0.75f);
                b.Add(Cyl(0.019f, 0.0f, 0.09f, 6), At(new Vector3(0, 0, -0.89f), RotX(Mathf.Pi / 2)), Steel * 0.75f);
                break;
            }
            case Part.Scutum:
                CurvedPanel(b, ScutumR, 0.33f, -0.53f, 0.53f, ScutumFront, 0.03f, Colors.White, 10);
                break;
            case Part.ScutumTrim:
                CurvedPanel(b, ScutumR + 0.004f, 0.335f, 0.505f, 0.54f, ScutumFront + 0.006f, 0.04f, Colors.White, 10);
                CurvedPanel(b, ScutumR + 0.004f, 0.335f, -0.54f, -0.505f, ScutumFront + 0.006f, 0.04f, Colors.White, 10);
                CurvedStrip(b, ScutumR + 0.004f, 0.3f, 0.335f, -0.53f, 0.53f, ScutumFront + 0.006f, 0.04f);
                CurvedStrip(b, ScutumR + 0.004f, -0.335f, -0.3f, -0.53f, 0.53f, ScutumFront + 0.006f, 0.04f);
                break;
            case Part.ScutumDesign:
                // The spine and a pair of wings either side of the boss, in yellow on red.
                CurvedStrip(b, ScutumR + 0.003f, -0.035f, 0.035f, -0.47f, 0.47f, ScutumFront + 0.004f, 0.01f);
                CurvedPanel(b, ScutumR + 0.003f, 0.27f, 0.17f, 0.21f, ScutumFront + 0.004f, 0.01f, Colors.White, 10);
                CurvedPanel(b, ScutumR + 0.003f, 0.27f, -0.21f, -0.17f, ScutumFront + 0.004f, 0.01f, Colors.White, 10);
                break;
            case Part.BossScutum:
                b.Add(Sphere(1f, 10, 4, true), At(new Vector3(0, 0, ScutumFront + 0.01f), RotX(Mathf.Pi / 2) * Scale(0.1f, 0.055f, 0.1f)));
                break;
            case Part.Round:
                b.Add(Cyl(0.4f, 0.4f, 0.026f, 18), At(new Vector3(0, 0, 0.065f), RotX(Mathf.Pi / 2)));
                break;
            case Part.RoundRim:
                b.Add(new TorusMesh { InnerRadius = 0.385f, OuterRadius = 0.418f, Rings = 18, RingSegments = 4 },
                    At(new Vector3(0, 0, 0.067f), RotX(Mathf.Pi / 2)));
                break;
            case Part.RoundDesign:
                b.Add(new TorusMesh { InnerRadius = 0.2f, OuterRadius = 0.29f, Rings = 18, RingSegments = 4 },
                    At(new Vector3(0, 0, 0.078f), RotX(Mathf.Pi / 2) * Scale(1f, 0.12f, 1f)));
                break;
            case Part.BossRound:
                b.Add(Sphere(1f, 10, 4, true), At(new Vector3(0, 0, 0.076f), RotX(Mathf.Pi / 2) * Scale(0.09f, 0.07f, 0.09f)));
                break;
            case Part.Oval:
                b.Add(Cyl(1f, 1f, 0.026f, 20), At(new Vector3(0, 0, 0.065f), RotX(Mathf.Pi / 2) * Scale(0.33f, 1f, 0.55f)));
                break;
            case Part.OvalSpine:
                b.Add(Box(0.05f, 0.98f, 0.025f), At(0, 0, 0.085f));
                break;
            case Part.OvalDesign:
                b.Add(new TorusMesh { InnerRadius = 0.85f, OuterRadius = 0.93f, Rings = 20, RingSegments = 4 },
                    At(new Vector3(0, 0, 0.078f), RotX(Mathf.Pi / 2) * Scale(0.33f, 0.05f, 0.55f)));
                break;
            case Part.BossOval:
                b.Add(Sphere(1f, 10, 4, true), At(new Vector3(0, 0, 0.09f), RotX(Mathf.Pi / 2) * Scale(0.075f, 0.06f, 0.12f)));
                break;
        }
        return b.Build();
    }

    public const float ScutumR = 0.58f, ScutumFront = 0.08f;

    /// <summary>A double-edged blade lying along +Z, flat faces to the sides, with a point.</summary>
    static void Blade(MeshBuilder b, float z0, float length, float halfWidth, float tip)
    {
        var pts = new[]
        {
            new Vector2(z0, -halfWidth), new Vector2(z0 + length - tip, -halfWidth * 0.9f), new Vector2(z0 + length, 0f),
            new Vector2(z0 + length - tip, halfWidth * 0.9f), new Vector2(z0, halfWidth),
        };
        b.ExtrudeX(pts, -0.006f, 0.006f, Steel);
        // A fuller ridge so it catches the light.
        var ridge = new[]
        {
            new Vector2(z0, -0.006f), new Vector2(z0 + length - tip, -0.004f), new Vector2(z0 + length - tip * 0.3f, 0f),
            new Vector2(z0 + length - tip, 0.004f), new Vector2(z0, 0.006f),
        };
        b.ExtrudeX(ridge, -0.009f, 0.009f, Steel * 0.92f);
    }

    /// <summary>
    /// A curved board: a slab of a vertical cylinder of radius r whose front face passes through z = front at x = 0,
    /// spanning half-width w (arc length) and y0..y1, with thickness t behind the front face.
    /// </summary>
    static void CurvedPanel(MeshBuilder b, float r, float w, float y0, float y1, float front, float t, Color col, int cols)
    {
        float half = w / r;
        float cz = front - r;
        Vector3 Pt(float a, float y, float rad) => new(rad * Mathf.Sin(a), y, cz + rad * Mathf.Cos(a));
        for (int i = 0; i < cols; i++)
        {
            float a0 = -half + 2f * half * i / cols;
            float a1 = -half + 2f * half * (i + 1) / cols;
            var n0 = new Vector3(Mathf.Sin(a0), 0, Mathf.Cos(a0));
            var n1 = new Vector3(Mathf.Sin(a1), 0, Mathf.Cos(a1));
            // front
            b.Tri(Pt(a0, y0, r), n0, Pt(a1, y0, r), n1, Pt(a1, y1, r), n1, col);
            b.Tri(Pt(a0, y0, r), n0, Pt(a1, y1, r), n1, Pt(a0, y1, r), n0, col);
            // back
            float rb = r - t;
            b.Tri(Pt(a0, y0, rb), -n0, Pt(a1, y1, rb), -n1, Pt(a1, y0, rb), -n1, col * 0.8f);
            b.Tri(Pt(a0, y0, rb), -n0, Pt(a0, y1, rb), -n0, Pt(a1, y1, rb), -n1, col * 0.8f);
            // top and bottom edges
            b.Quad(Pt(a0, y1, r), Pt(a1, y1, r), Pt(a1, y1, rb), Pt(a0, y1, rb), Vector3.Up, col);
            b.Quad(Pt(a0, y0, r), Pt(a1, y0, r), Pt(a1, y0, rb), Pt(a0, y0, rb), Vector3.Down, col);
        }
        // side edges
        var sl = new Vector3(Mathf.Cos(-half), 0, -Mathf.Sin(-half));
        b.Quad(Pt(-half, y0, r), Pt(-half, y1, r), Pt(-half, y1, r - t), Pt(-half, y0, r - t), -sl, col);
        var sr = new Vector3(Mathf.Cos(half), 0, -Mathf.Sin(half));
        b.Quad(Pt(half, y0, r), Pt(half, y1, r), Pt(half, y1, r - t), Pt(half, y0, r - t), sr, col);
    }

    /// <summary>A vertical strip on the same kind of curved surface, spanning arc lengths x0..x1.</summary>
    static void CurvedStrip(MeshBuilder b, float r, float x0, float x1, float y0, float y1, float front, float t)
    {
        float a0 = x0 / r, a1 = x1 / r;
        float cz = front - r;
        Vector3 Pt(float a, float y, float rad) => new(rad * Mathf.Sin(a), y, cz + rad * Mathf.Cos(a));
        var n = new Vector3(Mathf.Sin((a0 + a1) * 0.5f), 0, Mathf.Cos((a0 + a1) * 0.5f));
        b.Quad(Pt(a0, y0, r), Pt(a1, y0, r), Pt(a1, y1, r), Pt(a0, y1, r), n, Colors.White);
        var sl = new Vector3(-Mathf.Cos(a0), 0, Mathf.Sin(a0));
        b.Quad(Pt(a0, y0, r), Pt(a0, y1, r), Pt(a0, y1, r - t), Pt(a0, y0, r - t), sl, Colors.White);
        var sr = new Vector3(Mathf.Cos(a1), 0, -Mathf.Sin(a1));
        b.Quad(Pt(a1, y0, r), Pt(a1, y1, r), Pt(a1, y1, r - t), Pt(a1, y0, r - t), sr, Colors.White);
    }

    // ---------------------------------------------------------------- shared simple materials

    /// <summary>Unshaded, alpha-blended, coloured per instance (sRGB instance colours).</summary>
    public static StandardMaterial3D Unshaded(bool transparent, bool noDepth = false, bool cullOff = false) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Transparency = transparent ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        NoDepthTest = noDepth,
        CullMode = cullOff ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
        DisableReceiveShadows = true,
        DisableFog = false,
    };
}
