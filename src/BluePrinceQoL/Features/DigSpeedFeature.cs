using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;
using PmFsm = Il2CppHutongGames.PlayMaker.Fsm;

namespace BluePrinceQoL.Features;

/// <summary>
/// Shorter black screen when digging.
///
/// Dirt piles (DIRT PILE prefab, nested in about 40 rooms plus the grounds and
/// the Dining Room feast, FSM "FSM"):
///   "Click" (shovel) / "Click 2" (detector shovel): QuickFreeze, sets the Dig
///     Engine's "Detector Shovel" bool, dig sound, CameraFadeOut 0.5, Wait 1.4
///   "Click 3" (jackhammer): same without the fade, Wait 1.4
///   -> "State 1" (CameraFadeIn 0.5) / "State 6": swap pile for hole, send "Go"
///     to __SYSTEM/Utility/Dig Engine, which rolls and grants the reward.
/// Only the CameraFadeOut / Wait / CameraFadeIn durations are shortened (never
/// disabled), for that one state entry, and put back afterwards. Every logic
/// action, the "Go" and everything the Dig Engine does run as vanilla, in the
/// same order; they only happen sooner.
///
/// Guard: the Dig Engine handles one dig at a time. In vanilla the dig click
/// itself moves a Dig Engine still waiting on the previous reward on (it polls
/// any button), and that reward lands 1 s later, before the 1.4 s hold ends
/// and the new "Go" resets the Dig Engine. So a dig is only shortened when the
/// Dig Engine is idle (its active state has no FINISHED/click transition);
/// otherwise that dig keeps the vanilla hold.
///
/// Optional (DigSpeed_XMarksTheSpot, off): the Treasure Map "X Marks the Spot"
/// dig ("Dig Trigger" FSM: "Fade Down & SFX (shovel)" CameraFadeOut 0.5 + Wait 13
/// -> "The Swap" sends "Coffers Dig Up" to the Global Manager -> "FADE BACK"
/// CameraFadeIn 2). Separate FSM, same kind of change: only the fade and hold
/// durations of a linear FINISHED chain.
/// </summary>
public class DigSpeedFeature : Feature
{
    public override string Name => "DigSpeed";
    protected override string Description => "Shorter black-screen fade when digging (reward, hole and step logic run as vanilla)";

    private const string DigEnginePath = "__SYSTEM/Utility/Dig Engine";
    private const string PileName = "DIRT PILE";
    private const string XTrigger = "Dig Trigger", XParent = "X Marks the Spot";
    private const string XDigState = "Fade Down & SFX (shovel)", XBackState = "FADE BACK";
    private const string DetectorVar = "Detector Shovel";

    private const float MinHold = 0.1f;   // dig click and its card are never in the same frame (lesson 2)
    private const float MinFade = 0.02f;
    private const float RestoreMargin = 0.5f;

    private MelonPreferences_Entry<float> _speed;
    private MelonPreferences_Entry<bool> _xDig;

    private enum Role { None, PileDig, PileLand, XDig, XBack }
    private readonly Dictionary<IntPtr, Role> _roles = new();

    // Floats changed for one state entry. Put back when the state is left or
    // entered again, once vanilla's own duration has passed (the fade-in state
    // of a pile is terminal and stays active), on disable and on scene load.
    private sealed class Pending
    {
        public FsmState State;
        public float Deadline;
        public readonly List<(FsmFloat f, float orig)> Floats = new();
    }
    private readonly Dictionary<IntPtr, Pending> _pending = new();
    private readonly List<IntPtr> _tmp = new();
    private readonly HashSet<IntPtr> _shortDigs = new();   // pile FSMs whose hold was shortened

    private Il2Cpp.PlayMakerFSM _digEngine;
    private bool _loggedDig, _loggedX, _loggedBusy, _warnedEngine;

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry($"{Name}_Speed", 4f, "DigSpeed: speed",
            "How many times faster the dig fade and black hold run (1 = vanilla, 0 = as short as allowed: 0.1 s hold)");
        _xDig = Core.Prefs.CreateEntry($"{Name}_XMarksTheSpot", false, "DigSpeed: X Marks the Spot",
            "Also shorten the long Treasure Map 'X Marks the Spot' dig (13 s black + 2 s fade-in) by the same factor");
        FsmHooks.BeforeEnter += OnBeforeEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        _pending.Clear();   // the scene's FSMs are gone
        _roles.Clear();
        _shortDigs.Clear();
        _digEngine = null;
    }

    /// <summary>Duration factor: 1 = vanilla, smaller = faster.</summary>
    private float Factor()
    {
        var s = _speed.Value;
        if (s <= 0f) return 0f;
        return s <= 1f ? 1f : 1f / s;
    }

    // ------------------------------------------------------------------ hooks

    private void OnBeforeEnter(FsmState state)
    {
        var ptr = state.Pointer;
        if (_pending.Count > 0 && _pending.TryGetValue(ptr, out var old)) Restore(ptr, old);
        if (!Enabled)
        {
            if (_pending.Count > 0) RestoreAll();
            return;
        }

        var role = RoleOf(state);
        if (role == Role.None) return;
        var f = Factor();
        if (f >= 1f) return;

        switch (role)
        {
            case Role.PileDig:
                var fsmPtr = state.Fsm.Pointer;
                _shortDigs.Remove(fsmPtr);
                if (!DigEngineIdle())
                {
                    if (!_loggedBusy) Core.Log.Msg($"[{Name}] Dig Engine still busy with the last reward; this dig keeps the vanilla hold");
                    _loggedBusy = true;
                    return;
                }
                if (Scale(state, f)) _shortDigs.Add(fsmPtr);
                if (!_loggedDig) { _loggedDig = true; Core.Log.Msg($"[{Name}] shortening dig ({state.Fsm.GameObject?.name} / {state.Name})"); }
                break;
            case Role.PileLand:
                if (_shortDigs.Remove(state.Fsm.Pointer)) Scale(state, f);
                break;
            case Role.XDig:
            case Role.XBack:
                if (!_xDig.Value) return;
                Scale(state, f);
                if (!_loggedX) { _loggedX = true; Core.Log.Msg($"[{Name}] shortening X Marks the Spot dig ({state.Name})"); }
                break;
        }
    }

    public override void OnUpdate()
    {
        if (_pending.Count == 0) return;
        var now = Time.time;
        _tmp.Clear();
        foreach (var kv in _pending)
        {
            bool gone;
            try { gone = !kv.Value.State.Active; } catch { gone = true; }
            if (gone || now > kv.Value.Deadline) _tmp.Add(kv.Key);
        }
        foreach (var p in _tmp) Restore(p, _pending[p]);
    }

    // ------------------------------------------------------------------ scaling

    // Shortens every literal Wait / CameraFadeOut / CameraFadeIn of the state.
    private bool Scale(FsmState state, float f)
    {
        Pending pend = null;
        float longest = 0f;
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            FsmFloat t;
            float min;
            var w = a.TryCast<Wait>();
            if (w != null) { t = w.time; min = MinHold; }
            else
            {
                t = a.TryCast<CameraFadeOut>()?.time ?? a.TryCast<CameraFadeIn>()?.time;
                min = MinFade;
            }
            if (!IsConst(t)) continue;
            var orig = t.Value;
            var target = Math.Max(orig * f, min);
            if (target >= orig) continue;
            pend ??= new Pending { State = state };
            pend.Floats.Add((t, orig));
            t.Value = target;
            longest = Math.Max(longest, orig);
        }
        if (pend == null) return false;
        pend.Deadline = Time.time + longest + RestoreMargin;
        _pending[state.Pointer] = pend;
        return true;
    }

    private void Restore(IntPtr ptr, Pending p)
    {
        _pending.Remove(ptr);
        foreach (var (f, v) in p.Floats)
            try { f.Value = v; } catch { /* object gone */ }
    }

    private void RestoreAll()
    {
        _tmp.Clear();
        _tmp.AddRange(_pending.Keys);
        foreach (var p in _tmp) Restore(p, _pending[p]);
        _shortDigs.Clear();
    }

    // Idle = the active state is not waiting to go on by itself (FINISHED) or for
    // the card click ("click"): result states with their 0.5 s Wait, the click
    // states and the 1 s Wait states before a grant are all busy.
    private bool DigEngineIdle()
    {
        if (_digEngine == null) _digEngine = FsmUtil.Get(GameObject.Find(DigEnginePath), "FSM");
        var st = _digEngine?.Fsm?.ActiveState;
        if (st == null)
        {
            if (!_warnedEngine) Core.Log.Warning($"[{Name}] '{DigEnginePath}' FSM not found; digs stay vanilla");
            _warnedEngine = true;
            return false;
        }
        var ts = st.Transitions;
        if (ts != null)
            foreach (var t in ts)
                if (t != null && (t.EventName == "FINISHED" || t.EventName == "click")) return false;
        return true;
    }

    // ------------------------------------------------------------------ classifying

    private Role RoleOf(FsmState state)
    {
        if (_roles.TryGetValue(state.Pointer, out var r)) return r;
        try { r = Classify(state); }
        catch (Exception e) { r = Role.None; Core.Log.Warning($"[{Name}] could not analyse state '{state.Name}': {e.Message}"); }
        _roles[state.Pointer] = r;
        return r;
    }

    private static Role Classify(FsmState state)
    {
        var fsm = state.Fsm;
        var go = fsm?.GameObject;
        if (go == null || state.Actions == null || fsm.Name != "FSM") return Role.None;
        var name = go.name;

        if (name.StartsWith(PileName, StringComparison.Ordinal))
        {
            // Dig: sets the Dig Engine's tool flag and holds on a Wait, leaving on FINISHED only.
            if (SetsBool(state, DetectorVar) && Has<Wait>(state) && OnlyTransition(state, "FINISHED"))
                return Role.PileDig;
            // Landing: the state a dig leads to, sending "Go" (to the Dig Engine) under a fade-in.
            if (SendsEvent(state, "Go") && Has<CameraFadeIn>(state) && ReachedFromDig(fsm, state))
                return Role.PileLand;
            return Role.None;
        }

        if (name == XTrigger && go.transform.parent != null && go.transform.parent.name == XParent)
        {
            if (state.Name == XDigState && Has<Wait>(state) && OnlyTransition(state, "FINISHED")) return Role.XDig;
            if (state.Name == XBackState && Has<CameraFadeIn>(state) && OnlyTransition(state, "FINISHED")) return Role.XBack;
        }
        return Role.None;
    }

    private static bool ReachedFromDig(PmFsm fsm, FsmState target)
    {
        foreach (var s in fsm.States)
        {
            if (s?.Transitions == null || s.Actions == null) continue;
            foreach (var t in s.Transitions)
                if (t != null && t.EventName == "FINISHED" && t.ToState == target.Name && SetsBool(s, DetectorVar))
                    return true;
        }
        return false;
    }

    private static bool OnlyTransition(FsmState s, string ev)
    {
        var ts = s.Transitions;
        if (ts == null || ts.Length == 0) return false;
        foreach (var t in ts)
            if (t == null || t.EventName != ev) return false;
        return true;
    }

    private static bool Has<T>(FsmState s) where T : FsmStateAction
    {
        foreach (var a in s.Actions)
            if (a != null && a.Enabled && a.TryCast<T>() != null) return true;
        return false;
    }

    private static bool SetsBool(FsmState s, string variable)
    {
        foreach (var a in s.Actions)
        {
            var sb = a?.TryCast<SetFsmBool>();
            if (sb != null && sb.Enabled && sb.variableName != null && sb.variableName.Value == variable) return true;
        }
        return false;
    }

    private static bool SendsEvent(FsmState s, string ev)
    {
        foreach (var a in s.Actions)
        {
            var se = a?.TryCast<SendEvent>();
            if (se != null && se.Enabled && se.sendEvent?.Name == ev) return true;
        }
        return false;
    }

    // A literal parameter (not a reference to a shared FSM variable).
    private static bool IsConst(FsmFloat f) =>
        f != null && !(f.UseVariable && !string.IsNullOrEmpty(f.Name));
}
