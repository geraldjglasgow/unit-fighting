using System;
using Godot;
using static UnitFighting.View.MeshBuilder;

namespace UnitFighting.View;

/// <summary>
/// Sparks, blood, dust and splinters as simple CPU particles drawn by one MultiMesh, plus ground decals
/// (blood splashes and pools) in a ring buffer drawn by another. Everything runs on sim time, so it
/// freezes with the pause and crawls in slow motion.
/// </summary>
public sealed partial class EffectsView : Node3D
{
    public const int MaxParticles = 1200;
    public const int MaxDecals = 3000;

    enum Kind : byte { Spark, Blood, Dust, Chip, Flash }

    // Particles, struct-of-arrays, compacted (live ones are 0..count-1).
    readonly Vector3[] _p = new Vector3[MaxParticles], _v = new Vector3[MaxParticles];
    readonly float[] _age = new float[MaxParticles], _life = new float[MaxParticles], _size = new float[MaxParticles];
    readonly Color[] _col = new Color[MaxParticles], _col2 = new Color[MaxParticles];
    readonly Kind[] _kind = new Kind[MaxParticles];
    int _count;

    // Decals: a ring buffer; ones still spreading are rewritten each frame.
    readonly Vector2[] _dp = new Vector2[MaxDecals];
    readonly float[] _dRot = new float[MaxDecals], _dSize = new float[MaxDecals], _dAge = new float[MaxDecals],
        _dGrow = new float[MaxDecals], _dStretch = new float[MaxDecals];
    readonly Color[] _dCol = new Color[MaxDecals];
    int _dHead, _dCount;
    bool _decalsDirty;
    int _growing;

    PartBatch _particles, _decals;
    readonly Random _rng = new(1234);

    public override void _Ready()
    {
        var sphere = new MeshBuilder().Add(Sphere(0.5f, 6, 4), Transform3D.Identity).Build();
        var pm = SoldierAssets.Unshaded(true);
        _particles = new PartBatch(this, sphere, pm, MaxParticles, colors: true, custom: false, shadows: false, "Particles");

        var decalMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            Roughness = 0.5f,
            MetallicSpecular = 0.25f,
        };
        _decals = new PartBatch(this, BuildSplat(), decalMat, 256, colors: true, custom: false, shadows: false, "Decals");
    }

    static ArrayMesh BuildSplat()
    {
        // An irregular flat blob with a few satellite drops, radius ~0.5.
        var b = new MeshBuilder();
        var rng = new Random(7);
        const int n = 14;
        var center = Vector3.Zero;
        var pts = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.Tau * i / n;
            float r = 0.36f + 0.16f * (float)rng.NextDouble();
            pts[i] = new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r);
        }
        for (int i = 0; i < n; i++) b.Tri(center, pts[i], pts[(i + 1) % n], Vector3.Up, Colors.White);
        for (int k = 0; k < 5; k++)
        {
            float a = (float)rng.NextDouble() * Mathf.Tau;
            float r = 0.5f + 0.2f * (float)rng.NextDouble();
            var c = new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r);
            float s = 0.04f + 0.05f * (float)rng.NextDouble();
            for (int i = 0; i < 6; i++)
            {
                float a0 = Mathf.Tau * i / 6f, a1 = Mathf.Tau * (i + 1) / 6f;
                b.Tri(c, c + new Vector3(Mathf.Sin(a0), 0, Mathf.Cos(a0)) * s, c + new Vector3(Mathf.Sin(a1), 0, Mathf.Cos(a1)) * s, Vector3.Up, Colors.White);
            }
        }
        return b.Build();
    }

    float R() => (float)_rng.NextDouble();
    float R(float a, float b) => a + (b - a) * R();

    Vector3 RandomDir()
    {
        float z = R(-1f, 1f), a = R(0f, Mathf.Tau), r = Mathf.Sqrt(1f - z * z);
        return new Vector3(r * Mathf.Cos(a), z, r * Mathf.Sin(a));
    }

    void Spawn(Kind kind, Vector3 p, Vector3 v, float life, float size, Color c, Color c2)
    {
        int i;
        if (_count < MaxParticles) i = _count++;
        else i = _rng.Next(MaxParticles);   // full: recycle a random one
        _kind[i] = kind;
        _p[i] = p;
        _v[i] = v;
        _age[i] = 0f;
        _life[i] = life;
        _size[i] = size;
        _col[i] = c;
        _col2[i] = c2;
    }

    // ---------------------------------------------------------------- emitters

    /// <summary>Metal on metal or wood: a spray of hot sparks thrown off along <paramref name="away"/>.</summary>
    public void Sparks(Vector3 at, Vector3 away, int n, bool grey = false, float speed = 1f)
    {
        var hot = grey ? new Color(1.6f, 1.6f, 1.55f) : new Color(1.9f, 1.55f, 0.9f);
        var cool = grey ? new Color(0.55f, 0.55f, 0.55f, 0f) : new Color(1.2f, 0.45f, 0.1f, 0f);
        for (int i = 0; i < n; i++)
        {
            var d = (RandomDir() + away * 0.9f + Vector3.Up * 0.35f).Normalized();
            Spawn(Kind.Spark, at + RandomDir() * 0.03f, d * R(2.2f, 5.5f) * speed, R(0.16f, 0.42f), R(0.012f, 0.022f), hot, cool);
        }
        Spawn(Kind.Flash, at, Vector3.Zero, 0.07f, grey ? 0.14f : 0.2f, grey ? new Color(1.5f, 1.5f, 1.5f, 0.9f) : new Color(1.9f, 1.7f, 1.2f, 0.95f),
            new Color(1.5f, 1f, 0.5f, 0f));
    }

    public void Blood(Vector3 at, Vector3 away, int n, float speed = 1f)
    {
        for (int i = 0; i < n; i++)
        {
            var d = (away * 1.1f + RandomDir() * 0.75f + Vector3.Up * 0.45f).Normalized();
            var c = new Color(R(0.42f, 0.6f), 0.02f, 0.02f);
            Spawn(Kind.Blood, at + RandomDir() * 0.05f, d * R(1.2f, 3.6f) * speed, R(0.5f, 1.1f), R(0.025f, 0.055f), c, c * 0.7f);
        }
    }

    public void Dust(Vector3 at, int n, float scale = 1f)
    {
        for (int i = 0; i < n; i++)
        {
            var d = RandomDir();
            d.Y = Mathf.Abs(d.Y) * 0.4f;
            var c = new Color(0.6f, 0.55f, 0.44f, 0.26f);
            Spawn(Kind.Dust, at + new Vector3(d.X, 0.05f, d.Z) * 0.2f * scale, d * R(0.5f, 1.4f) * scale,
                R(0.5f, 0.9f), R(0.08f, 0.15f) * scale, c, c with { A = 0f });
        }
    }

    public void Chips(Vector3 at, Vector3 away, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var d = (RandomDir() + away + Vector3.Up * 0.5f).Normalized();
            var c = new Color(R(0.4f, 0.55f), R(0.26f, 0.34f), 0.14f);
            Spawn(Kind.Chip, at, d * R(1.5f, 3.5f), R(0.5f, 0.9f), R(0.025f, 0.045f), c, c);
        }
    }

    public void Flash(Vector3 at, float size, Color c) => Spawn(Kind.Flash, at, Vector3.Zero, 0.1f, size, c, c with { A = 0f });

    /// <summary>A splash on the ground. grow &gt; 0 makes it spread out over that many seconds (a pool under a body).</summary>
    public void Decal(Vector2 at, float size, float grow = 0f, float stretch = 1f, Color? color = null)
    {
        int i = _dHead;
        _dHead = (_dHead + 1) % MaxDecals;
        _dCount = Math.Min(_dCount + 1, MaxDecals);
        _dp[i] = at;
        _dRot[i] = R(0f, Mathf.Tau);
        _dSize[i] = size;
        _dAge[i] = grow > 0f ? 0f : 999f;
        _dGrow[i] = grow;
        _dStretch[i] = stretch;
        _dCol[i] = color ?? new Color(R(0.32f, 0.44f), 0.02f, 0.015f);
        if (grow > 0f) _growing++;
        _decalsDirty = true;
    }

    // ---------------------------------------------------------------- per frame

    public void Tick(float dt)
    {
        // Simulate.
        for (int i = 0; i < _count; i++)
        {
            _age[i] += dt;
            bool dead = _age[i] >= _life[i];
            if (!dead && dt > 0f)
            {
                var k = _kind[i];
                var v = _v[i];
                switch (k)
                {
                    case Kind.Spark: v.Y -= 7f * dt; v *= 1f - 1.2f * dt; break;
                    case Kind.Blood: v.Y -= 9.8f * dt; v *= 1f - 0.4f * dt; break;
                    case Kind.Chip: v.Y -= 9.8f * dt; v *= 1f - 0.8f * dt; break;
                    case Kind.Dust: v *= 1f - 2.2f * dt; v.Y += 0.25f * dt; break;
                }
                _v[i] = v;
                var p = _p[i] + v * dt;
                if (p.Y < 0.01f && k != Kind.Flash && k != Kind.Dust)
                {
                    p.Y = 0.01f;
                    if (k == Kind.Blood)
                    {
                        if (R() < 0.3f) Decal(new Vector2(p.X, p.Z), R(0.05f, 0.12f), 0f, R(1f, 1.8f));
                        dead = true;
                    }
                    else
                    {
                        _v[i] = new Vector3(v.X * 0.4f, -v.Y * 0.25f, v.Z * 0.4f);
                    }
                }
                _p[i] = p;
            }
            if (dead)
            {
                _count--;
                if (i != _count)
                {
                    _p[i] = _p[_count]; _v[i] = _v[_count]; _age[i] = _age[_count]; _life[i] = _life[_count];
                    _size[i] = _size[_count]; _col[i] = _col[_count]; _col2[i] = _col2[_count]; _kind[i] = _kind[_count];
                }
                i--;
            }
        }

        // Draw.
        _particles.Begin();
        for (int i = 0; i < _count; i++)
        {
            float f = _age[i] / _life[i];
            var c = _col[i].Lerp(_col2[i], f);
            float size = _size[i];
            Basis b;
            switch (_kind[i])
            {
                case Kind.Spark:
                {
                    var v = _v[i];
                    float sp = v.Length();
                    var dir = sp > 1e-3f ? v / sp : Vector3.Up;
                    b = AlongZ(dir) * Basis.FromScale(new Vector3(size, size, size + sp * 0.035f));
                    break;
                }
                case Kind.Blood:
                {
                    var v = _v[i];
                    float sp = v.Length();
                    var dir = sp > 1e-3f ? v / sp : Vector3.Up;
                    b = AlongZ(dir) * Basis.FromScale(new Vector3(size, size, size * (1f + sp * 0.4f)));
                    break;
                }
                case Kind.Dust:
                    b = Basis.FromScale(new Vector3(1f, 0.7f, 1f) * size * (1f + 2.2f * f));
                    break;
                case Kind.Flash:
                    b = Basis.FromScale(Vector3.One * size * (1f - 0.6f * f));
                    break;
                default:
                    b = new Basis(new Vector3(0.3f, 1f, 0.2f).Normalized(), _age[i] * 14f) * Basis.FromScale(new Vector3(size, size * 0.5f, size * 2.2f));
                    break;
            }
            _particles.Add(new Transform3D(b, _p[i]), c);
        }
        _particles.Commit();

        // Decals: spread the fresh pools.
        if (_growing > 0 && dt > 0f)
        {
            int still = 0;
            for (int i = 0; i < _dCount; i++)
            {
                if (_dAge[i] >= _dGrow[i] || _dGrow[i] <= 0f) continue;
                _dAge[i] += dt;
                if (_dAge[i] < _dGrow[i]) still++;
            }
            _growing = still;
            _decalsDirty = true;
        }
        if (_decalsDirty)
        {
            _decalsDirty = false;
            _decals.Begin();
            for (int i = 0; i < _dCount; i++)
            {
                float g = _dGrow[i] > 0f ? 0.25f + 0.75f * EaseOut(_dAge[i] / _dGrow[i], 2f) : 1f;
                float s = _dSize[i] * g;
                var b = new Basis(Vector3.Up, _dRot[i]) * Basis.FromScale(new Vector3(s, 1f, s * _dStretch[i]));
                // Each decal a hair above the last so overlapping ones don't fight.
                float y = 0.006f + (i % 24) * 0.0006f;
                _decals.Add(new Transform3D(b, new Vector3(_dp[i].X, y, _dp[i].Y)), _dCol[i]);
            }
            _decals.Commit();
        }
    }

    static Basis AlongZ(Vector3 dir)
    {
        var up = Mathf.Abs(dir.Y) > 0.95f ? Vector3.Right : Vector3.Up;
        var x = up.Cross(dir).Normalized();
        var y = dir.Cross(x);
        return new Basis(x, y, dir);
    }
}
