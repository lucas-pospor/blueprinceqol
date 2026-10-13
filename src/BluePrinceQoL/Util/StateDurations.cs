using System;
using System.Collections.Generic;
using Il2CppHutongGames.PlayMaker;

namespace BluePrinceQoL.Util;

/// <summary>
/// Shortens duration parameters (Wait time, fade time, tween duration) for one
/// state entry and puts them back when the state is left, re-entered, or the
/// owner calls RestoreAll. Same scheme as EndOfDay / DareGameOver:
///   BeforeEnter: Begin(state) ... Scale(...) ... Commit()
///   OnUpdate:    Tick()
///   scene load / feature off: RestoreAll()
/// Only literal parameters are touched, never a reference to a shared FSM variable.
/// </summary>
internal sealed class StateDurations
{
    private sealed class Pending
    {
        public FsmState State;
        public readonly List<(FsmFloat f, float v)> Floats = new();
        public readonly List<string> Log = new();
    }

    private readonly Dictionary<IntPtr, Pending> _pending = new();
    private readonly List<IntPtr> _tmp = new();
    private Pending _cur;

    public int Count => _pending.Count;

    /// <summary>Call first in BeforeEnter for every state: restores a re-entered state.</summary>
    public void OnEnter(FsmState state)
    {
        if (_pending.Count > 0 && _pending.TryGetValue(state.Pointer, out var old)) Restore(state.Pointer, old);
    }

    public void Begin(FsmState state) => _cur = new Pending { State = state };

    /// <summary>value = max(value * factor, min), if that is shorter.</summary>
    public void Scale(FsmFloat v, float factor, float min, string what)
    {
        if (_cur == null || !IsConst(v)) return;
        var orig = v.Value;
        if (orig <= 0f) return;
        var target = Math.Max(orig * factor, min);
        if (target >= orig) return;
        _cur.Floats.Add((v, orig));
        v.Value = target;
        if (what != null) _cur.Log.Add($"{what} {orig:0.##}s -> {target:0.##}s");
    }

    /// <summary>Keeps the changes until the state is left. Returns a log line, or null if nothing changed.</summary>
    public string Commit()
    {
        var p = _cur;
        _cur = null;
        if (p == null || p.Floats.Count == 0) return null;
        _pending[p.State.Pointer] = p;
        return string.Join(", ", p.Log);
    }

    public void Tick()
    {
        if (_pending.Count == 0) return;
        _tmp.Clear();
        foreach (var kv in _pending)
        {
            bool active;
            try { active = kv.Value.State.Active; } catch { active = false; }
            if (!active) _tmp.Add(kv.Key);
        }
        foreach (var p in _tmp) Restore(p, _pending[p]);
    }

    public void RestoreAll()
    {
        if (_pending.Count == 0) return;
        _tmp.Clear();
        _tmp.AddRange(_pending.Keys);
        foreach (var p in _tmp) Restore(p, _pending[p]);
    }

    private void Restore(IntPtr ptr, Pending p)
    {
        _pending.Remove(ptr);
        foreach (var (f, v) in p.Floats)
            try { f.Value = v; } catch { /* object gone */ }
    }

    // A literal parameter (not a reference to a shared FSM variable).
    private static bool IsConst(FsmFloat f) =>
        f != null && !(f.UseVariable && !string.IsNullOrEmpty(f.Name));
}
