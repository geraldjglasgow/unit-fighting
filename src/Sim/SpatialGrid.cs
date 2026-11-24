using System;
using System.Collections.Generic;
using Godot;

namespace UnitFighting.Sim;

/// <summary>Uniform bucket grid over the field, rebuilt every step. Anyone off the edge lands in the edge cells.</summary>
public sealed class SpatialGrid
{
    readonly float _cell, _origin;
    readonly int _dim;
    readonly int[] _head;
    int[] _next = Array.Empty<int>();
    List<Soldier> _items = new();

    public SpatialGrid(float cell, float halfExtent)
    {
        _cell = cell;
        _origin = -halfExtent;
        _dim = (int)Math.Ceiling(halfExtent * 2f / cell);
        _head = new int[_dim * _dim];
    }

    int CellOf(float v) => Math.Clamp((int)((v - _origin) / _cell), 0, _dim - 1);

    public void Rebuild(List<Soldier> soldiers)
    {
        Array.Fill(_head, -1);
        if (_next.Length < soldiers.Count) _next = new int[soldiers.Count * 2];
        _items = soldiers;
        for (int i = 0; i < soldiers.Count; i++)
        {
            var s = soldiers[i];
            if (!s.Active) continue;
            int c = CellOf(s.Pos.X) + CellOf(s.Pos.Y) * _dim;
            _next[i] = _head[c];
            _head[c] = i;
        }
    }

    public void Query(Vector2 p, float r, List<Soldier> result)
    {
        result.Clear();
        int x0 = CellOf(p.X - r), x1 = CellOf(p.X + r), y0 = CellOf(p.Y - r), y1 = CellOf(p.Y + r);
        float r2 = r * r;
        for (int cy = y0; cy <= y1; cy++)
        for (int cx = x0; cx <= x1; cx++)
        for (int i = _head[cx + cy * _dim]; i >= 0; i = _next[i])
        {
            var s = _items[i];
            if (s.Pos.DistanceSquaredTo(p) <= r2) result.Add(s);
        }
    }
}
