using System;
using Godot;
using static UnitFighting.View.MeshBuilder;

namespace UnitFighting.View;

/// <summary>
/// Sky, sun, fog and the grass field. Add one as a child of the main scene; it builds itself in _Ready.
/// </summary>
public partial class WorldView : Node3D
{
    public const float GroundSize = 1600f;

    public DirectionalLight3D Sun { get; private set; }
    public WorldEnvironment Environment { get; private set; }

    const string GroundShader = @"
shader_type spatial;
render_mode diffuse_burley;
uniform sampler2D detail : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D patches : filter_linear_mipmap, repeat_enable;
uniform vec3 grass_a : source_color = vec3(0.3, 0.4, 0.16);
uniform vec3 grass_b : source_color = vec3(0.37, 0.43, 0.19);
uniform vec3 dry : source_color = vec3(0.47, 0.46, 0.27);
uniform vec3 earth : source_color = vec3(0.38, 0.32, 0.2);
varying vec3 wpos;
void vertex() {
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
void fragment() {
    float d1 = texture(detail, wpos.xz * 0.31).r;
    float d2 = texture(detail, wpos.xz * 0.067 + vec2(0.37, 0.11)).r;
    float p1 = texture(patches, wpos.xz * 0.0045).r;
    float p2 = texture(patches, wpos.xz * 0.017 + vec2(0.5, 0.2)).r;
    vec3 c = mix(grass_a, grass_b, smoothstep(0.3, 0.72, p1));
    c = mix(c, dry, smoothstep(0.5, 0.9, p2) * 0.2);
    c = mix(c, earth, smoothstep(0.66, 0.9, d2) * 0.25);
    c *= 0.84 + 0.3 * d1;
    ALBEDO = c;
    ROUGHNESS = 0.96;
    SPECULAR = 0.15;
}
";

    const string GrassShader = @"
shader_type spatial;
render_mode cull_disabled, diffuse_burley;
uniform float sway = 0.035;
varying vec3 tint;
void vertex() {
    vec3 w = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    float h = max(VERTEX.y, 0.0);
    float s = sin(TIME * 1.3 + w.x * 0.21 + w.z * 0.17) + 0.4 * sin(TIME * 2.9 + w.x * 0.7);
    VERTEX.x += s * sway * h * 4.0;
    VERTEX.z += s * sway * h * 2.0;
    tint = INSTANCE_CUSTOM.rgb;
    NORMAL = vec3(0.0, 1.0, 0.0);
}
void fragment() {
    // Light blades like the ground under them, whichever face we see.
    NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
    ALBEDO = COLOR.rgb * tint;
    ROUGHNESS = 0.95;
    SPECULAR = 0.15;
}
";

    /// <summary>Set before adding to the tree to turn the distance fog off (for testing).</summary>
    public static bool Fog = true;

    public override void _Ready()
    {
        Name = string.IsNullOrEmpty(Name) ? "World" : Name;
        BuildEnvironment();
        BuildSun();
        BuildGround();
        BuildScatter();
    }

    void BuildEnvironment()
    {
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.3f, 0.5f, 0.8f),
            SkyHorizonColor = new Color(0.72f, 0.8f, 0.88f),
            SkyCurve = 0.12f,
            GroundBottomColor = new Color(0.25f, 0.3f, 0.2f),
            GroundHorizonColor = new Color(0.6f, 0.66f, 0.62f),
            SunAngleMax = 25f,
            SunCurve = 0.1f,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = sky, ProcessMode = Sky.ProcessModeEnum.Automatic },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 1f,
            AmbientLightEnergy = 1.0f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapExposure = 1.0f,
            TonemapWhite = 6f,
            FogEnabled = Fog,
            FogMode = Godot.Environment.FogModeEnum.Depth,
            FogDepthBegin = 140f,
            FogDepthEnd = 900f,
            FogDepthCurve = 1.6f,
            FogLightColor = new Color(0.7f, 0.77f, 0.86f),
            FogLightEnergy = 1f,
            FogSunScatter = 0.12f,
            FogDensity = 0.75f,
            FogSkyAffect = 0.15f,
            FogAerialPerspective = 0.1f,
            SsaoEnabled = true,
            SsaoRadius = 0.9f,
            SsaoIntensity = 1.6f,
            SsaoPower = 1.4f,
            SsaoDetail = 0.6f,
            GlowEnabled = true,
            GlowIntensity = 0.55f,
            GlowStrength = 0.9f,
            GlowBloom = 0f,
            GlowHdrThreshold = 1.15f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive,
            AdjustmentEnabled = true,
            AdjustmentSaturation = 1.0f,
            AdjustmentContrast = 1.04f,
        };
        Environment = new WorldEnvironment { Name = "Environment", Environment = env };
        AddChild(Environment);
    }

    void BuildSun()
    {
        Sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightColor = new Color(1f, 0.95f, 0.86f),
            LightEnergy = 1.35f,
            ShadowEnabled = true,
            ShadowBias = 0.03f,
            ShadowNormalBias = 1.2f,
            ShadowBlur = 1.2f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowSplit1 = 0.05f,
            DirectionalShadowSplit2 = 0.15f,
            DirectionalShadowSplit3 = 0.4f,
            DirectionalShadowMaxDistance = 170f,
            DirectionalShadowBlendSplits = true,
            DirectionalShadowFadeStart = 0.85f,
            SkyMode = DirectionalLight3D.SkyModeEnum.LightAndSky,
        };
        AddChild(Sun);
        // Late-morning sun from the south-east: it rakes across both battle lines and throws shadows sideways.
        var toSun = new Vector3(0.62f, 0.72f, -0.45f).Normalized();
        Sun.Basis = Basis.LookingAt(-toSun, Vector3.Up);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftMedium);
    }

    void BuildGround()
    {
        var detail = new NoiseTexture2D
        {
            Width = 256, Height = 256, Seamless = true, GenerateMipmaps = true,
            Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex, Frequency = 0.06f, FractalOctaves = 4, Seed = 3 },
        };
        var patches = new NoiseTexture2D
        {
            Width = 256, Height = 256, Seamless = true, GenerateMipmaps = true,
            Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.012f, FractalOctaves = 3, Seed = 11 },
        };
        var mat = new ShaderMaterial { Shader = new Shader { Code = GroundShader } };
        mat.SetShaderParameter("detail", detail);
        mat.SetShaderParameter("patches", patches);
        var ground = new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(GroundSize, GroundSize), SubdivideDepth = 0, SubdivideWidth = 0 },
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(ground);
    }

    void BuildScatter()
    {
        var rng = new Random(20240901);
        float R() => (float)rng.NextDouble();

        // ---- grass tufts: dense round the battle, thinning out beyond
        var tuft = BuildTuft(rng);
        var grassMat = new ShaderMaterial { Shader = new Shader { Code = GrassShader } };
        const int tufts = 60000;
        var grass = new PartBatch(this, tuft, grassMat, tufts, colors: false, custom: true, shadows: false, "Grass");
        grass.Begin();
        for (int i = 0; i < tufts; i++)
        {
            Vector2 p = i < 38000
                ? new Vector2(R() * 220f - 110f, R() * 220f - 110f)
                : RandomAnnulus(rng, 90f, 330f);
            float s = 0.7f + 0.8f * R() * R();
            var b = new Basis(Vector3.Up, R() * Mathf.Tau) * Basis.FromScale(new Vector3(s, s * (0.7f + 0.6f * R()), s));
            float v = 0.85f + 0.3f * R();
            var tint = new Color(v * (0.95f + 0.15f * R()), v, v * (0.85f + 0.2f * R()));
            grass.Add(new Transform3D(b, new Vector3(p.X, 0f, p.Y)), tint);
        }
        grass.Commit();

        // ---- small stones
        var stoneMesh = new MeshBuilder().Add(Sphere(0.5f, 7, 3), At(new Vector3(0, 0.1f, 0), Scale(1f, 0.55f, 0.8f))).Build();
        var stoneMat = SoldierAssets.MaterialOf(Part.Foot);   // plain matte body shader, coloured per instance
        const int stones = 2200;
        var rocks = new PartBatch(this, stoneMesh, stoneMat, stones, colors: false, custom: true, shadows: false, "Stones");
        rocks.Begin();
        for (int i = 0; i < stones; i++)
        {
            var p = i < 1400 ? new Vector2(R() * 240f - 120f, R() * 240f - 120f) : RandomAnnulus(rng, 110f, 330f);
            float s = 0.06f + 0.22f * R() * R() * R();
            var b = new Basis(new Vector3(R() - 0.5f, 1f, R() - 0.5f).Normalized(), R() * Mathf.Tau) * Basis.FromScale(new Vector3(s, s, s));
            float g = 0.45f + 0.25f * R();
            rocks.Add(new Transform3D(b, new Vector3(p.X, -0.02f, p.Y)), new Color(g, g * 0.97f, g * 0.9f).SrgbToLinear());
        }
        rocks.Commit();

        // ---- wild flowers
        var flowerMesh = new MeshBuilder()
            .Add(Cyl(0.004f, 0.005f, 0.2f, 3), At(0, 0.1f, 0), new Color(0.3f, 0.45f, 0.15f))
            .Add(Sphere(0.022f, 5, 3), At(0, 0.205f, 0))
            .Build();
        var flowers = new PartBatch(this, flowerMesh, grassMat, 5000, colors: false, custom: true, shadows: false, "Flowers");
        var palette = new[] { new Color(1f, 0.95f, 0.85f), new Color(1f, 0.85f, 0.25f), new Color(0.7f, 0.55f, 0.95f), new Color(0.95f, 0.45f, 0.35f) };
        flowers.Begin();
        for (int i = 0; i < 5000; i++)
        {
            // Flowers grow in clumps.
            var c = new Vector2(R() * 300f - 150f, R() * 300f - 150f);
            var col = palette[rng.Next(palette.Length)];
            int n = 3 + rng.Next(8);
            for (int k = 0; k < n && i < 5000; k++, i++)
            {
                var p = c + new Vector2(R() - 0.5f, R() - 0.5f) * 2.5f;
                float s = 0.7f + 0.6f * R();
                flowers.Add(new Transform3D(Basis.FromScale(new Vector3(s, s, s)), new Vector3(p.X, 0f, p.Y)), col.SrgbToLinear());
            }
        }
        flowers.Commit();
    }

    static Vector2 RandomAnnulus(Random rng, float r0, float r1)
    {
        float a = (float)rng.NextDouble() * Mathf.Tau;
        float r = Mathf.Sqrt(Mathf.Lerp(r0 * r0, r1 * r1, (float)rng.NextDouble()));
        return new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
    }

    /// <summary>A clump of thin grass blades, dark at the root and pale at the tip (vertex colours, linear).</summary>
    static ArrayMesh BuildTuft(Random rng)
    {
        var b = new MeshBuilder();
        var root = new Color(0.27f, 0.35f, 0.13f);
        var tip = new Color(0.56f, 0.62f, 0.3f);
        int blades = 7;
        for (int i = 0; i < blades; i++)
        {
            float a = (float)rng.NextDouble() * Mathf.Tau;
            float h = 0.14f + 0.18f * (float)rng.NextDouble();
            float lean = 0.05f + 0.1f * (float)rng.NextDouble();
            var basePos = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.03f;
            var side = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)) * 0.016f;
            var top = basePos + new Vector3(Mathf.Cos(a) * lean, h, Mathf.Sin(a) * lean);
            // Two triangles so the blade has a colour gradient: base quad half then tip.
            var mid = basePos.Lerp(top, 0.5f);
            var n = Vector3.Up;
            var cMid = root.Lerp(tip, 0.5f);
            b.Tri(basePos - side, n, basePos + side, n, mid + side * 0.6f, n, root);
            b.Tri(basePos - side, n, mid + side * 0.6f, n, mid - side * 0.6f, n, root);
            b.Tri(mid - side * 0.6f, n, mid + side * 0.6f, n, top, n, cMid);
        }
        // Per-vertex gradient would need per-vertex colours; the two-tone split above reads well enough at this size.
        return b.Build();
    }
}
