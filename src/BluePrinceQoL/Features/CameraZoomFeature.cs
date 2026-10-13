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
/// Faster camera zooms onto terminals, the Dining Room dinner plate and the
/// Utility Closet fuse box (and, opt-in, onto safes, dartboards, maps ... that
/// use the same code).
///
/// Every one of these zooms is the same mechanism. The clicked object's FSM
/// (the "sender") sets CamDistance / CamTarget / CamRotate on the player FSM
/// ("__SYSTEM/FPS Home/FPSController - Prince", FSM "FSM") and sends it
/// "CamTarget". The player FSM goes Target Set -> "State 3", which computes
/// d = clamp(CamDistance * 2, 0.6, 4), calls LockPlayerFor(d) and DOTweens the
/// player to CamTarget and the camera to the CamRotate angles over d seconds.
/// Meanwhile the sender waits in its FINISHED follow-up state ("Camera Move 2"
/// in, "Camera Move 3" out), which recomputes the same clamp(2*dist, .6, 4)
/// into its own $CamDistance and Waits on it (the Dining Room zoom-in waits a
/// constant 2 s instead), then carries on (opens the terminal / dinner menu,
/// or sends QuickUnfreeze to the player).
///
/// The feature only shortens durations, it never disables an action or sends
/// an event itself:
/// * player "State 3": the FloatClamp's min and max are set to the scaled
///   duration for that one entry, so $CamDistance, LockPlayerFor and both
///   tweens get it. DOTween still ends on the exact CamTarget position/angles.
/// * sender follow-up Wait: set to max(vanilla * factor, tween) + 50 ms, so the
///   tween has always finished before the sender goes on (the unfreeze can
///   never come before the camera has arrived).
/// * EnableFOVOffsetAction fade times on the way in/out of terminals.
/// * optional: the 2 s fade-to-black + 2 s fade-in when eating in the Dining Room.
/// * Utility Closet fuse box (its follow-ups also open/close the panel door):
///   the door tweens and the door-close delay are scaled by the same factor,
///   and the 0.9 s "Reset" wait after the zoom-out is scaled only when the
///   zoom-out wait before it was set to cover the (scaled) tween.
/// Constants changed for a whole state run are put back on the next entry of
/// that state (or right after the state's actions start, where they are only
/// read on entry).
/// </summary>
public class CameraZoomFeature : Feature
{
    public override string Name => "CameraZoom";
    protected override string Description =>
        "Faster camera zoom onto terminals, the Dining Room plate and the Utility Closet fuse box (optionally safes, darts, maps ...)";

    private const string PlayerObject = "FPSController - Prince";
    private const string PlayerTweenState = "State 3";
    private const string ComputerRoot = "Computer System";
    private const string OverlayName = "UI OVERLAY CAM";
    private const string CamDistanceVar = "CamDistance";
    // Utility Closet/_GAMEPLAY/FUSE BOX/...
    private const string PanelRoot = "FUSE BOX";
    private const string PanelCollider = "Fuse Box - First Collider";
    private const string PanelDoor = "S FuseboxDoor";
    private const float PanelResetTime = 15f; // zoom-out follow -> "Reset" must follow within this

    private const float Eps = 0.0001f;   // > 0: Wait/tween finishes on its next update
    private const float Margin = 0.05f;  // sender wait is always this much longer than the tween
    private const float FovMin = 0.05f;  // never ask the FOV offset animation for a zero-length fade
    private const float ArmTime = 1.5f;  // sender -> player "State 3" must follow within this (real time)

    private MelonPreferences_Entry<float> _speed;
    private MelonPreferences_Entry<bool> _instant, _terminals, _dining, _eatFade, _utility, _other;

    private enum Scope { None, Terminal, Dining, Utility, Other }
    private enum Kind { None, PlayerTween, PlayerOther, ComputerRoot, Sender, DiningFade, PanelDelay, PanelDoor }

    private sealed class Info
    {
        public Kind Kind;
        public Scope Scope;
        public IntPtr Follow;      // sender: FINISHED follow-up state
        public Wait FollowWait;    // sender: the Wait in it
        public IntPtr After;       // panel zoom-out: the follow-up's FINISHED state ("Reset", a pure wait)
        public Wait AfterWait;     // panel zoom-out: the Wait in it
    }

    // Per scene (the scene and every FSM in it are rebuilt each day; room
    // prefabs add new FSMs, which get new pointers).
    private readonly Dictionary<IntPtr, Info> _states = new();
    private readonly Dictionary<IntPtr, Scope> _fsmScope = new();

    // Values to put back right after a state's actions have started (read on entry only).
    private readonly Dictionary<IntPtr, List<(FsmFloat f, float orig)>> _restoreAfter = new();
    // Values to put back on the next entry of a state (read every frame while it runs).
    private readonly Dictionary<IntPtr, List<(FsmFloat f, float orig)>> _restoreOnEntry = new();

    // Current zoom: armed by a sender state, consumed by the player tween.
    private IntPtr _armFsm, _armFollow;
    private Wait _armWait;
    private Scope _armScope;
    private float _armUntil = -1f;
    private bool _tweenScaled;
    private float _tweenDur;
    private FsmState _pendingFollow; // follow-up state entered before the tween was scaled
    private IntPtr _armAfter;
    private Wait _armAfterWait;

    // Panel "Reset": scaled on its next entry, only after a covered zoom-out.
    private IntPtr _resetState;
    private Wait _resetWait;
    private float _resetUntil = -1f;

    private bool _tweenActive;
    private int _syncFrames;
    private bool _warnedPlayer;

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry("CameraZoom_Speed", 4f, "CameraZoom: speed",
            "How many times faster the zoom runs (1 = vanilla, 0 = instant)");
        _instant = Core.Prefs.CreateEntry("CameraZoom_Instant", false, "CameraZoom: instant",
            "Skip the zoom entirely (camera jumps to the vanilla end position); overrides Speed");
        _terminals = Core.Prefs.CreateEntry("CameraZoom_Terminals", true, "CameraZoom: terminals",
            "Computer terminals (Security, Office, Laboratory, Bomb Shelter, Grotto): zoom in/out and FOV change");
        _dining = Core.Prefs.CreateEntry("CameraZoom_DiningRoom", true, "CameraZoom: Dining Room",
            "Dining Room dinner plate: zoom in/out");
        _eatFade = Core.Prefs.CreateEntry("CameraZoom_EatFade", true, "CameraZoom: eating fade",
            "Also speed up the 2 s fade to black + 2 s fade back when eating in the Dining Room");
        _utility = Core.Prefs.CreateEntry("CameraZoom_UtilityPanel", true, "CameraZoom: Utility Closet panel",
            "Utility Closet fuse box / breaker panel: zoom in/out, its door, and the waits that cover the zoom-out");
        _other = Core.Prefs.CreateEntry("CameraZoom_OtherObjects", false, "CameraZoom: other objects",
            "Also speed up identical zooms elsewhere (wall safes, dartboards, Room 46 map, Lab machine, Shrine/Curse menus ...)");
        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        _states.Clear();
        _fsmScope.Clear();
        _restoreAfter.Clear();
        _restoreOnEntry.Clear();
        Disarm();
        ClearReset();
        _tweenActive = false;
        _syncFrames = 0;
    }

    /// <summary>Duration factor: 0 = instant, 1 = vanilla.</summary>
    private float Factor()
    {
        if (_instant.Value) return 0f;
        var s = _speed.Value;
        if (s <= 0f) return 0f;
        return s <= 1f ? 1f : 1f / s;
    }

    private bool Active(Scope scope)
    {
        if (!Enabled || Factor() >= 1f) return false;
        return scope switch
        {
            Scope.Terminal => _terminals.Value,
            Scope.Dining => _dining.Value,
            Scope.Utility => _utility.Value,
            Scope.Other => _other.Value,
            _ => false,
        };
    }

    // ------------------------------------------------------------------ hooks

    private void OnBeforeEnter(FsmState state)
    {
        var ptr = state.Pointer;

        // Always (also when disabled): undo last entry's changes first.
        if (_restoreOnEntry.Count > 0 && _restoreOnEntry.Remove(ptr, out var undo))
            foreach (var (f, orig) in undo) if (f != null) f.Value = orig;

        if (_resetState != IntPtr.Zero && ptr == _resetState) ApplyPanelReset(state);

        var info = InfoOf(state);
        if (info.Kind == Kind.None) return;

        switch (info.Kind)
        {
            case Kind.PlayerTween:
                ScalePlayerTween(state);
                break;
            case Kind.PlayerOther:
                if (_tweenActive) { _tweenActive = false; _syncFrames = 3; }
                break;
            case Kind.ComputerRoot:
                if (Active(Scope.Terminal)) ScaleFov(state);
                break;
            case Kind.Sender:
                if (!Active(info.Scope)) break;
                Arm(state, info);
                ScaleFov(state); // terminal "State 7": FOV offset off over 0.6 s
                break;
            case Kind.DiningFade:
                if (_eatFade.Value && Active(Scope.Dining)) ScaleFades(state);
                break;
            case Kind.PanelDelay:
                if (Active(Scope.Utility)) ScaleDelays(state);
                break;
            case Kind.PanelDoor:
                if (Active(Scope.Utility)) ScaleDoor(state);
                break;
        }
    }

    private void OnAfterEnter(FsmState state)
    {
        var ptr = state.Pointer;
        if (_restoreAfter.Count > 0 && _restoreAfter.Remove(ptr, out var undo))
            foreach (var (f, orig) in undo) if (f != null) f.Value = orig;

        if (_armFollow == IntPtr.Zero || ptr != _armFollow) return;
        if (state.Fsm == null || state.Fsm.Pointer != _armFsm) return;
        if (_tweenScaled) ApplyFollow(state);
        else _pendingFollow = state; // player tween not started yet: do it then
    }

    public override void OnUpdate()
    {
        if (_syncFrames > 0)
        {
            _syncFrames--;
            // The tween moved the CharacterController's transform; make sure
            // physics agrees before the player can move again (as IntroSkip).
            Physics.SyncTransforms();
        }
        if (_armUntil >= 0f && Time.unscaledTime > _armUntil && !_tweenScaled) Disarm();
        if (_tweenScaled && _armFollow != IntPtr.Zero && Time.unscaledTime > _armUntil + 10f) Disarm();
        if (_resetUntil >= 0f && Time.unscaledTime > _resetUntil) ClearReset();
    }

    // ------------------------------------------------------------- the zoom

    private void Arm(FsmState sender, Info info)
    {
        _armFsm = sender.Fsm.Pointer;
        _armFollow = info.Follow;
        _armWait = info.FollowWait;
        _armScope = info.Scope;
        _armAfter = info.After;
        _armAfterWait = info.AfterWait;
        _armUntil = Time.unscaledTime + ArmTime;
        _tweenScaled = false;
        _tweenDur = 0f;
        _pendingFollow = null;
    }

    private void Disarm()
    {
        _armFsm = _armFollow = IntPtr.Zero;
        _armWait = null;
        _armScope = Scope.None;
        _armAfter = IntPtr.Zero;
        _armAfterWait = null;
        _armUntil = -1f;
        _tweenScaled = false;
        _pendingFollow = null;
    }

    // Player "State 3": FloatOperator ($CamDistance *= 2), FloatClamp (0.6..4),
    // LockPlayerFor($CamDistance), DotweenMove/Rotate (duration $CamDistance).
    // Pinning the clamp to the scaled duration changes all of them at once.
    private void ScalePlayerTween(FsmState state)
    {
        if (_armFollow == IntPtr.Zero || _tweenScaled || Time.unscaledTime > _armUntil) return;
        if (!Active(_armScope)) { Disarm(); return; }

        FloatOperator op = null;
        FloatClamp cl = null;
        DotweenMoveTransformTo mv = null;
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            op ??= a.TryCast<FloatOperator>();
            cl ??= a.TryCast<FloatClamp>();
            mv ??= a.TryCast<DotweenMoveTransformTo>();
        }
        if (op == null || cl == null || mv == null || op.operation != FloatOperator.Operation.Multiply ||
            !IsVar(cl.floatVariable, CamDistanceVar) || !IsVar(mv.duration, CamDistanceVar) ||
            !IsConst(cl.minValue) || !IsConst(cl.maxValue) || op.float1 == null || op.float2 == null)
        {
            if (!_warnedPlayer)
                Core.Log.Warning($"[{Name}] player '{PlayerTweenState}' doesn't look as expected; leaving zooms vanilla");
            _warnedPlayer = true;
            Disarm();
            return;
        }

        var min = cl.minValue.Value;
        var max = cl.maxValue.Value;
        var vanilla = Mathf.Clamp(op.float1.Value * op.float2.Value, min, max);
        var scaled = Math.Max(vanilla * Factor(), Eps);

        AddRestore(_restoreAfter, state.Pointer, cl.minValue, min);
        AddRestore(_restoreAfter, state.Pointer, cl.maxValue, max);
        cl.minValue.Value = scaled;
        cl.maxValue.Value = scaled;

        _tweenScaled = true;
        _tweenDur = scaled;
        _tweenActive = true;
        Core.Log.Msg($"[{Name}] {_armScope} zoom {vanilla:0.00}s -> {scaled:0.00}s");

        var p = _pendingFollow;
        _pendingFollow = null;
        if (p != null && p.Fsm?.ActiveState != null && p.Fsm.ActiveState.Pointer == p.Pointer)
            ApplyFollow(p);
    }

    // Sender follow-up state ("Camera Move 2/3"): its Wait mirrors the tween.
    private void ApplyFollow(FsmState state)
    {
        var w = _armWait;
        var dur = _tweenDur;
        var after = _armAfter;
        var afterWait = _armAfterWait;
        var f = Factor();
        Disarm();
        var t = w?.time;
        if (t == null) return;
        var target = Math.Max(t.Value * f, dur) + Margin;
        if (target >= t.Value) return;
        if (!IsVar(t, CamDistanceVar))
            AddRestore(_restoreOnEntry, state.Pointer, t, t.Value); // constant (Dining / panel "Camera Move 2": 2 s)
        // A variable ($CamDistance) is recomputed by the state's own actions on every entry.
        t.Value = target;

        // Panel zoom-out: this wait now ends after the tween (+ Margin), so the
        // "Reset" wait that follows no longer has to cover the camera move.
        if (after != IntPtr.Zero && afterWait != null)
        {
            _resetState = after;
            _resetWait = afterWait;
            _resetUntil = Time.unscaledTime + PanelResetTime;
        }
    }

    private void ClearReset()
    {
        _resetState = IntPtr.Zero;
        _resetWait = null;
        _resetUntil = -1f;
    }

    // Panel "Reset" (cursor, re-enable the panel collider, Wait 0.9 -> "State 3":
    // QuickUnfreeze + Wait 0.1 -> clickable again). Wait reads time every update.
    private void ApplyPanelReset(FsmState state)
    {
        var t = _resetWait?.time;
        ClearReset();
        if (!IsConst(t) || !Active(Scope.Utility)) return;
        var orig = t.Value;
        var target = Math.Max(orig * Factor(), Eps);
        if (target >= orig) return;
        AddRestore(_restoreOnEntry, state.Pointer, t, orig);
        t.Value = target;
    }

    // Panel "Camera Move 2": SendEvent "Event 2" (close the door) with delay 0.5.
    // The delay is read once on entry.
    private void ScaleDelays(FsmState state)
    {
        var f = Factor();
        foreach (var a in state.Actions)
        {
            var se = a?.TryCast<SendEvent>();
            if (se == null || !se.Enabled || !IsConst(se.delay)) continue;
            var orig = se.delay.Value;
            var target = orig * f;
            if (orig <= 0f || target >= orig) continue;
            AddRestore(_restoreAfter, state.Pointer, se.delay, orig);
            se.delay.Value = target;
        }
    }

    // Panel door: "Open Door 3" clamps the collider's raw distance to 0.9..2 s for
    // its DOTween (scale the clamp, read on entry); "Close Door 3" tweens 1.3 s
    // with finishEvent FINISHED (scale the duration, put back on the next entry).
    private void ScaleDoor(FsmState state)
    {
        var f = Factor();
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var cl = a.TryCast<FloatClamp>();
            if (cl != null && !cl.everyFrame && IsConst(cl.minValue) && IsConst(cl.maxValue))
            {
                AddRestore(_restoreAfter, state.Pointer, cl.minValue, cl.minValue.Value);
                AddRestore(_restoreAfter, state.Pointer, cl.maxValue, cl.maxValue.Value);
                cl.minValue.Value = Math.Max(cl.minValue.Value * f, Eps);
                cl.maxValue.Value = Math.Max(cl.maxValue.Value * f, Eps);
                continue;
            }
            var rt = a.TryCast<DotweenRotateTransformTo>();
            if (rt != null && IsConst(rt.duration))
            {
                var orig = rt.duration.Value;
                var target = Math.Max(orig * f, Eps);
                if (target >= orig) continue;
                AddRestore(_restoreOnEntry, state.Pointer, rt.duration, orig);
                rt.duration.Value = target;
            }
        }
    }

    private void ScaleFov(FsmState state)
    {
        var f = Factor();
        foreach (var a in state.Actions)
        {
            var fov = a?.TryCast<EnableFOVOffsetAction>();
            if (fov == null || !fov.Enabled || !IsConst(fov.FadeTime)) continue;
            var orig = fov.FadeTime.Value;
            var target = Math.Max(orig * f, FovMin);
            if (target >= orig) continue;
            AddRestore(_restoreAfter, state.Pointer, fov.FadeTime, orig);
            fov.FadeTime.Value = target;
        }
    }

    // Dining Room eating: CameraFadeOut 2 s -> swap plates -> CameraFadeIn 2 s.
    // Both read "time" every frame and fire FINISHED themselves; we only shorten.
    private void ScaleFades(FsmState state)
    {
        var f = Factor();
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var t = a.TryCast<CameraFadeOut>()?.time ?? a.TryCast<CameraFadeIn>()?.time;
            if (!IsConst(t)) continue;
            var orig = t.Value;
            var target = Math.Max(orig * f, Eps);
            if (target >= orig) continue;
            AddRestore(_restoreOnEntry, state.Pointer, t, orig);
            t.Value = target;
        }
    }

    // ---------------------------------------------------------- classifying

    private Info InfoOf(FsmState state)
    {
        if (_states.TryGetValue(state.Pointer, out var info)) return info;
        info = Classify(state) ?? new Info();
        _states[state.Pointer] = info;
        return info;
    }

    private Info Classify(FsmState state)
    {
        var fsm = state.Fsm;
        var go = fsm?.GameObject;
        if (go == null || state.Actions == null) return null;
        var goName = go.name;

        if (goName == PlayerObject)
        {
            if (fsm.Name != "FSM") return null;
            return new Info { Kind = state.Name == PlayerTweenState ? Kind.PlayerTween : Kind.PlayerOther };
        }

        if (goName == ComputerRoot)
        {
            var parent = go.transform.parent;
            if (parent == null || parent.name != OverlayName) return null;
            return HasAction<EnableFOVOffsetAction>(state) ? new Info { Kind = Kind.ComputerRoot } : null;
        }

        if (SendsCamTarget(state))
        {
            var scope = ScopeOf(fsm, go);
            var follow = FollowOf(state, scope);
            if (follow.state == null) return null;
            var info = new Info { Kind = Kind.Sender, Scope = scope, Follow = follow.state.Pointer, FollowWait = follow.wait };
            if (scope == Scope.Utility && IsConst(follow.wait.time))
            {
                var after = PanelResetOf(follow.state);
                if (after.state != null) { info.After = after.state.Pointer; info.AfterWait = after.wait; }
            }
            return info;
        }

        if ((HasAction<CameraFadeOut>(state) || HasAction<CameraFadeIn>(state)) && ScopeOf(fsm, go) == Scope.Dining)
            return new Info { Kind = Kind.DiningFade, Scope = Scope.Dining };

        if (goName == PanelCollider && ScopeOf(fsm, go) == Scope.Utility && HasDelayedSend(state))
            return new Info { Kind = Kind.PanelDelay, Scope = Scope.Utility };

        if (goName == PanelDoor && IsUnder(go, PanelRoot) &&
            (HasAction<FloatClamp>(state) || HasAction<DotweenRotateTransformTo>(state)))
            return new Info { Kind = Kind.PanelDoor, Scope = Scope.Utility };

        return null;
    }

    private static bool HasDelayedSend(FsmState state)
    {
        foreach (var a in state.Actions)
        {
            var se = a?.TryCast<SendEvent>();
            if (se != null && se.Enabled && IsConst(se.delay) && se.delay.Value > 0f) return true;
        }
        return false;
    }

    private static bool IsUnder(GameObject go, string parentName)
    {
        var p = go.transform.parent;
        return p != null && p.name == parentName;
    }

    // Panel zoom-out: the follow-up ("Camera Move 2", constant Wait 2) goes on
    // FINISHED to "Reset": SetFsmInt (cursor), SetProperty (re-enable the panel
    // collider) and one constant Wait 0.9 -> "State 3" (QuickUnfreeze). In vanilla
    // 2 + 0.9 s cover the 2.8 s tween; accept "Reset" only if it is exactly that.
    private static (FsmState state, Wait wait) PanelResetOf(FsmState follow)
    {
        var st = FinishedTarget(follow);
        if (st?.Actions == null) return default;
        Wait wait = null;
        foreach (var a in st.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var w = a.TryCast<Wait>();
            if (w != null)
            {
                if (wait != null || w.realTime) return default;
                wait = w;
                continue;
            }
            if (a.TryCast<SetFsmInt>() != null || a.TryCast<SetProperty>() != null) continue;
            return default;
        }
        return IsConst(wait?.time) ? (st, wait) : default;
    }

    private static FsmState FinishedTarget(FsmState state)
    {
        if (state.Transitions == null) return null;
        foreach (var t in state.Transitions)
            if (t != null && t.EventName == "FINISHED")
                return string.IsNullOrEmpty(t.ToState) ? null : state.Fsm.GetState(t.ToState);
        return null;
    }

    private static bool SendsCamTarget(FsmState state)
    {
        foreach (var a in state.Actions)
        {
            var se = a?.TryCast<SendEvent>();
            if (se != null && se.Enabled && se.sendEvent?.Name == "CamTarget") return true;
        }
        return false;
    }

    private Scope ScopeOf(PmFsm fsm, GameObject go)
    {
        if (_fsmScope.TryGetValue(fsm.Pointer, out var s)) return s;
        s = Scope.Other;
        var n = go.name;
        if (n == "Click Trigger" && OpensTerminal(fsm)) s = Scope.Terminal;
        else if (n == PanelCollider && IsUnder(go, PanelRoot)) s = Scope.Utility;
        else if ((n == "C - Dining Room Dishes" || n == "Course Trigger" ||
                  (n.StartsWith("C - ", StringComparison.Ordinal) && n.EndsWith("Dish Click", StringComparison.Ordinal)))
                 && FsmHooks.PathOf(go).Contains("Dining Room"))
            s = Scope.Dining;
        _fsmScope[fsm.Pointer] = s;
        return s;
    }

    // Terminal click triggers activate "UI OVERLAY CAM/Computer System" in "Wait for Exit 2".
    private static bool OpensTerminal(PmFsm fsm)
    {
        var st = fsm.GetState("Wait for Exit 2");
        if (st?.Actions == null) return false;
        foreach (var a in st.Actions)
        {
            var gc = a?.TryCast<GetChild>();
            if (gc?.childName != null && gc.childName.Value == ComputerRoot) return true;
        }
        return false;
    }

    // The state a sender goes to on FINISHED, if it is a pure "wait for the
    // camera" state: only FloatOperator / FloatClamp / SetFsmInt (cursor) and
    // exactly one Wait on $CamDistance (the same clamp(2*dist, .6, 4) as the
    // tween). A constant Wait is accepted for the Dining Room (its zoom-in
    // "Camera Move 2" waits 2 s and then just opens the dinner menu) and the
    // Utility Closet panel (zoom-out "Camera Move 2": 2 s); elsewhere constant
    // waits may cover other animations (doors, puzzles), so those zooms stay
    // vanilla. The panel's follow-ups also send the door its open/close event
    // (a SendEvent); the door tweens are scaled separately (Kind.PanelDoor).
    private static (FsmState state, Wait wait) FollowOf(FsmState sender, Scope scope)
    {
        var st = FinishedTarget(sender);
        if (st?.Actions == null) return default;

        Wait wait = null;
        foreach (var a in st.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var w = a.TryCast<Wait>();
            if (w != null)
            {
                if (wait != null || w.realTime) return default;
                wait = w;
                continue;
            }
            var op = a.TryCast<FloatOperator>();
            if (op != null) { if (op.everyFrame) return default; continue; }
            var cl = a.TryCast<FloatClamp>();
            if (cl != null) { if (cl.everyFrame) return default; continue; }
            if (a.TryCast<SetFsmInt>() != null) continue;
            if (scope == Scope.Utility)
            {
                var se = a.TryCast<SendEvent>();
                if (se != null && !se.everyFrame && se.sendEvent?.Name != "CamTarget") continue;
            }
            return default; // anything else: not a pure camera wait
        }
        if (wait?.time == null) return default;
        if (IsVar(wait.time, CamDistanceVar)) return (st, wait);
        if ((scope == Scope.Dining || scope == Scope.Utility) && IsConst(wait.time)) return (st, wait);
        return default;
    }

    // -------------------------------------------------------------- helpers

    private static bool HasAction<T>(FsmState state) where T : FsmStateAction
    {
        foreach (var a in state.Actions)
            if (a != null && a.Enabled && a.TryCast<T>() != null) return true;
        return false;
    }

    private static void AddRestore(Dictionary<IntPtr, List<(FsmFloat, float)>> d, IntPtr state, FsmFloat f, float orig)
    {
        if (!d.TryGetValue(state, out var list)) d[state] = list = new List<(FsmFloat, float)>();
        foreach (var (g, _) in list) if (g.Pointer == f.Pointer) return; // keep the first (true) original
        list.Add((f, orig));
    }

    private static bool IsConst(FsmFloat f) =>
        f != null && !(f.UseVariable && !string.IsNullOrEmpty(f.Name));

    private static bool IsVar(FsmFloat f, string name) =>
        f != null && f.UseVariable && f.Name == name;
}
