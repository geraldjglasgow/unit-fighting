using System.Collections.Generic;
using UnitFighting.Sim;

namespace UnitFighting.Game;

/// <summary>
/// Autopilot (P): a BattleAI commands the player's army, but leaves alone any battalion the player has given an order
/// of his own until it has carried that order out. A move or a line is done when the battalion gets there, an attack
/// when the attack is over (its target broke or was destroyed). A halt is an order to stand, so a halted battalion
/// stays off autopilot until the player orders it again.
/// </summary>
public sealed class PlayerAutopilot
{
    public readonly BattleAI AI;
    readonly Dictionary<Battalion, OrderType> _manual = new();
    readonly List<Battalion> _done = new();
    readonly List<(int index, Battalion unit)> _hidden = new();

    public PlayerAutopilot(BattleAI ai) => AI = ai;

    /// <summary>The player has just given u an order himself (a routing battalion takes none).</summary>
    public void Ordered(Battalion u)
    {
        if (!u.Routing && !u.Destroyed) _manual[u] = u.Order;
    }

    /// <summary>Is u under the player's own orders rather than the autopilot's?</summary>
    public bool IsManual(Battalion u) => _manual.ContainsKey(u);

    /// <summary>Call after every sim step.</summary>
    public void Update(Battle b, float dt)
    {
        // A battalion that has carried out its order is handed back.
        _done.Clear();
        foreach (var (u, order) in _manual)
            if (u.Destroyed || (order != OrderType.Hold && u.Order == OrderType.Hold)) _done.Add(u);
        foreach (var u in _done) _manual.Remove(u);
        if (_manual.Count == 0)
        {
            AI.Update(b, dt);
            return;
        }

        // BattleAI can't be told to keep its hands off a battalion, so the player's are taken out of its sight while
        // it thinks and put back where they were. It only reads the list and gives orders, so nothing else notices.
        var list = b.Battalions;
        _hidden.Clear();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!_manual.ContainsKey(list[i])) continue;
            _hidden.Add((i, list[i]));
            list.RemoveAt(i);
        }
        try
        {
            AI.Update(b, dt);
        }
        finally
        {
            for (int k = _hidden.Count - 1; k >= 0; k--) list.Insert(_hidden[k].index, _hidden[k].unit);
        }
    }
}
