using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppDG.Tweening;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;
using PmFsm = Il2CppHutongGames.PlayMaker.Fsm;

namespace BluePrinceQoL.Features;

/// <summary>
/// Shorter black screen when using the game's own "stairs skip" (Settings ->
/// Motion Assist -> Stairs Skip) in the Clock Tower, Library, Tomb and Well
/// Joint.
///
/// Every one of these is an FSM on a GameObject named "STAIRS SKIP code" or
/// "STAIRS SKIP 2". On click it goes:
///   "Fade Down[ 2]": QuickFreeze the player, CameraFadeOut 0.7 s, and either
///       DOTween the player to $warpPosition (1 s delay, 3 s) and the camera to
///       the target angles (Clock Tower, Tomb, Well Joint), then Wait 6.2-11.6 s;
///       or just Wait 5.2 s (Library)
///   [Library only: "Warp": SetPosition / SetRotation, finishes at once]
///   "Fade Up[ 2]": QuickUnfreeze the player, CameraFadeIn 2 s.
///
/// The feature only scales durations, for that one state entry, and puts them
/// back afterwards. No action is disabled, no event is sent, no state is
/// skipped: the Wait still fires FINISHED, the tweens still move the player to
/// the exact vanilla position, QuickUnfreeze still comes from "Fade Up".
/// Ordering guarantees kept:
/// * the move starts only once the screen is fully black (delay >= fade-out);
/// * the move tween takes at least MinMove, so trigger volumes along the way
///   (music / culling boxes) are still crossed by the physics steps;
/// * the Wait (and so QuickUnfreeze) comes at least Tail after every tween has
///   ended; just before that the move is checked and, if needed, completed,
///   and physics is synced so the CharacterController can't snap back.
/// FSMs that don't match this exact structure are left vanilla.
/// </summary>
public class StairsWarpFeature : Feature
{
    public override string Name => "StairsWarp";
    protected override string Description =>
        "Shorter black screen for the game's stairs skip (Clock Tower, Library, Tomb, Well Joint)";

    private const string ObjectPrefix = "STAIRS SKIP";

    private const float MinFadeOut = 0.05f; // never ask for a 0 s fade
    private const float MinFadeIn = 0.1f;
    private const float MinMove = 0.5f;     // player move tween: >= 25 physics steps, triggers still hit
    private const float MinTween = 0.05f;   // camera rotation tween (cosmetic, in black)
    private const float Tail = 0.4f;        // Wait ends >= this long after the last tween (> max deltaTime 0.333)
    private const float CheckSlack = 0.05f; // check/complete the move this long after its scheduled end
    private const float GiveUp = 60f;       // forget a warp after this long (real time)

    private MelonPreferences_Entry<float> _speed;
    private MelonPreferences_Entry<bool> _fadeIn;

    // ---- per-FSM plan (null = not a stairs warp / doesn't match; left vanilla) ----
    private sealed class Plan
    {
        public string Label;
        public FsmState Down, Warp, Up;
        public Wait Wait;
        public CameraFadeOut FadeOut;
        public CameraFadeIn FadeIn;
        public readonly List<DotweenMoveTransformTo> Moves = new();
        public readonly List<DotweenRotateTransformTo> Rotates = new();
    }
    private readonly Dictionary<IntPtr, Plan> _plans = new();

    // ---- values to put back ----
    private sealed class Pending
    {
        public FsmState State;
        public readonly List<(FsmFloat f, float v)> Floats = new();
    }
    // Read every frame while the state runs (Wait, CameraFade*): put back once the state is left.
    private readonly Dictionary<IntPtr, Pending> _restoreOnExit = new();
    // Read once in OnEnter (DOTween duration/delay): put back right after the state's actions started.
    private Pending _restoreAfterEnter;
    private readonly List<IntPtr> _tmp = new();

    // ---- the warp in progress ----
    private sealed class Current
    {
        public Plan Plan;
        public PmFsm Fsm;
        public float Start;      // Time.time at "Fade Down" entry (scaled time, like Wait and DOTween)
        public float MoveEnd;    // scheduled end of the player move tween, relative to Start
        public bool Checked;     // move verified/completed and physics synced
        public bool TargetsRead;
        public float GiveUpAt;   // unscaled
        public readonly List<(Transform t, Vector3 to)> Targets = new();
    }
    private Current _cur;
    private int _syncFrames;

    private bool _warnedPlan, _warnedComplete;

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry($"{Name}_Speed", 4f, "StairsWarp: speed",
            "How many times shorter the stairs-skip fades and black screen are (1 = vanilla). " +
            "The player move always takes at least 0.5 s and the black screen always lasts until 0.4 s after it.");
        _fadeIn = Core.Prefs.CreateEntry($"{Name}_FadeIn", true, "StairsWarp: shorter fade-in",
            "Also shorten the 2 s fade back in at the end (you already have control during it)");
        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        // The scene (and every room prefab with its FSMs) is rebuilt each day.
        RestoreAll();
        _restoreAfterEnter = null;
        _plans.Clear();
        _cur = null;
        _syncFrames = 0;
    }

    /// <summary>Duration factor in (0, 1]; 1 = vanilla.</summary>
    private float Factor()
    {
        var s = _speed.Value;
        return s > 1f ? 1f / s : 1f;
    }

    // ------------------------------------------------------------------ hooks

    private void OnBeforeEnter(FsmState state)
    {
        if (_restoreOnExit.Count > 0 && _restoreOnExit.Remove(state.Pointer, out var old)) Restore(old);

        if (!Enabled)
        {
            // OnUpdate doesn't run while disabled: put everything back here.
            if (_restoreOnExit.Count > 0) RestoreAll();
            _cur = null;
            return;
        }

        var name = state.Name;
        if (name == null || !(name.StartsWith("Fade ", StringComparison.Ordinal) || name == "Warp")) return;
        var plan = PlanOf(state);
        if (plan == null) return;

        var ptr = state.Pointer;
        if (ptr == plan.Down.Pointer) StartWarp(state, plan);
        else if (ptr == plan.Up.Pointer) EnterUp(state, plan);
    }

    private void OnAfterEnter(FsmState state)
    {
        // The tweens exist now (created in OnEnter): put the stored values back.
        var p = _restoreAfterEnter;
        if (p != null && p.State.Pointer == state.Pointer)
        {
            _restoreAfterEnter = null;
            Restore(p);
        }
        // Remember where the player is being moved to ($warpPosition is set by now).
        var cur = _cur;
        if (cur == null || cur.TargetsRead || cur.Plan.Down.Pointer != state.Pointer) return;
        cur.TargetsRead = true;
        foreach (var mv in cur.Plan.Moves)
        {
            var go = state.Fsm.GetOwnerDefaultTarget(mv.gameObject);
            if (go != null && mv.to != null) cur.Targets.Add((go.transform, mv.to.Value));
        }
    }

    public override void OnUpdate()
    {
        if (_syncFrames > 0)
        {
            _syncFrames--;
            Physics.SyncTransforms();
        }

        if (_restoreOnExit.Count > 0)
        {
            _tmp.Clear();
            foreach (var kv in _restoreOnExit)
                if (!kv.Value.State.Active) _tmp.Add(kv.Key);
            foreach (var k in _tmp)
            {
                Restore(_restoreOnExit[k]);
                _restoreOnExit.Remove(k);
            }
        }

        var cur = _cur;
        if (cur == null) return;
        if (Time.unscaledTime > cur.GiveUpAt) { _cur = null; return; }
        // The move tween should be done by now; make sure (well before the Wait ends).
        if (!cur.Checked && Time.time - cur.Start >= cur.MoveEnd + CheckSlack) CheckMove(cur);
    }

    // ------------------------------------------------------------- the warp

    // "Fade Down": scale fade-out, tweens and Wait for this entry.
    private void StartWarp(FsmState state, Plan plan)
    {
        _cur = null;
        var f = Factor();
        if (f >= 1f) return;

        var fo = plan.FadeOut.time.Value;
        var fo2 = Shorter(fo, f, MinFadeOut);

        var after = new Pending { State = state };
        var moveEnd = 0f;
        var tweenEnd = 0f;
        foreach (var mv in plan.Moves)
        {
            var end = ScaleTween(after, mv.duration, mv.setDelay, f, MinMove, fo2);
            moveEnd = Math.Max(moveEnd, end);
            tweenEnd = Math.Max(tweenEnd, end);
        }
        foreach (var rt in plan.Rotates)
            tweenEnd = Math.Max(tweenEnd, ScaleTween(after, rt.duration, rt.setDelay, f, MinTween, fo2));

        var w = plan.Wait.time.Value;
        var w2 = Math.Max(w * f, Math.Max(tweenEnd + Tail, fo2 + 0.1f));
        if (w2 >= w)
        {
            // Nothing to gain: undo and leave this warp vanilla.
            Restore(after);
            return;
        }

        var exit = PendingOnExit(state);
        Set(exit, plan.FadeOut.time, fo2);
        Set(exit, plan.Wait.time, w2);
        _restoreAfterEnter = after.Floats.Count > 0 ? after : null;

        _cur = new Current
        {
            Plan = plan,
            Fsm = state.Fsm,
            Start = Time.time,
            MoveEnd = moveEnd,
            Checked = plan.Moves.Count == 0,
            GiveUpAt = Time.unscaledTime + GiveUp,
        };
        Core.Log.Msg($"[{Name}] {plan.Label}: fade-out {fo:0.00}s -> {fo2:0.00}s, black {w:0.0}s -> {w2:0.00}s" +
                     (plan.Moves.Count > 0 ? $", move ends at {moveEnd:0.00}s" : ""));
    }

    // Returns the scaled end time (delay + duration) of the tween.
    private static float ScaleTween(Pending p, FsmFloat dur, FsmFloat delay, float f, float minDur, float minDelay)
    {
        var d = dur.Value;
        var dl = delay.Value;
        var d2 = Shorter(d, f, minDur);
        // Start moving only once the screen is fully black (as vanilla: 1 s > 0.7 s).
        var dl2 = Math.Min(dl, Math.Max(dl * f, minDelay));
        Set(p, dur, d2);
        Set(p, delay, dl2);
        return dl2 + d2;
    }

    // Once the move should be over: if the player isn't at the warp position
    // yet, finish the tween now (DOTween puts it exactly on the end value),
    // then sync physics so the CharacterController starts from there.
    private void CheckMove(Current cur)
    {
        cur.Checked = true;
        foreach (var (t, to) in cur.Targets)
        {
            if (t == null) continue;
            if ((t.position - to).sqrMagnitude < 1e-4f) continue;
            var n = DOTween.Complete(t, true);
            if (n > 0 && !_warnedComplete)
            {
                _warnedComplete = true;
                Core.Log.Warning($"[{Name}] {cur.Plan.Label}: move tween still running at its end time; completed it");
            }
        }
        Physics.SyncTransforms();
        _syncFrames = 3;
    }

    // "Fade Up": runs QuickUnfreeze. Make sure the warp is applied first.
    private void EnterUp(FsmState state, Plan plan)
    {
        var cur = _cur;
        if (cur == null || cur.Fsm.Pointer != state.Fsm.Pointer) return; // this warp wasn't shortened
        _cur = null;
        if (!cur.Checked) CheckMove(cur);
        // Library: "Warp" just SetPosition'ed the player in this same frame.
        Physics.SyncTransforms();
        _syncFrames = 3;

        var f = Factor();
        if (!_fadeIn.Value || f >= 1f) return;
        var t = plan.FadeIn.time;
        var t2 = Shorter(t.Value, f, MinFadeIn);
        if (t2 < t.Value) Set(PendingOnExit(state), t, t2);
    }

    private static float Shorter(float orig, float f, float min) =>
        Math.Min(orig, Math.Max(orig * f, Math.Min(orig, min)));

    // ------------------------------------------------------------- restore

    private Pending PendingOnExit(FsmState state)
    {
        if (!_restoreOnExit.TryGetValue(state.Pointer, out var p))
            _restoreOnExit[state.Pointer] = p = new Pending { State = state };
        return p;
    }

    private static void Set(Pending p, FsmFloat f, float v)
    {
        var known = false;
        foreach (var (g, _) in p.Floats)
            if (g.Pointer == f.Pointer) { known = true; break; } // keep the first (true) original
        if (!known) p.Floats.Add((f, f.Value));
        f.Value = v;
    }

    private static void Restore(Pending p)
    {
        foreach (var (f, v) in p.Floats)
            if (f != null) f.Value = v;
        p.Floats.Clear();
    }

    private void RestoreAll()
    {
        foreach (var p in _restoreOnExit.Values) Restore(p);
        _restoreOnExit.Clear();
        if (_restoreAfterEnter != null) { Restore(_restoreAfterEnter); _restoreAfterEnter = null; }
    }

    // ------------------------------------------------------------ analysis

    private Plan PlanOf(FsmState state)
    {
        var fsm = state.Fsm;
        if (fsm == null) return null;
        if (_plans.TryGetValue(fsm.Pointer, out var plan)) return plan;
        plan = null;
        var go = fsm.GameObject;
        if (go != null && go.name.StartsWith(ObjectPrefix, StringComparison.Ordinal))
        {
            try { plan = BuildPlan(fsm, go); }
            catch (Exception e)
            {
                if (!_warnedPlan) Core.Log.Warning($"[{Name}] could not analyse '{FsmHooks.PathOf(go)}': {e.Message}");
                _warnedPlan = true;
            }
            if (plan == null)
                Core.Log.Warning($"[{Name}] '{FsmHooks.PathOf(go)}' doesn't match the analysed stairs skip; left vanilla");
            else
                Core.Log.Msg($"[{Name}] covering {plan.Label} ('{plan.Down.Name}' -> {(plan.Warp != null ? "'Warp' -> " : "")}'{plan.Up.Name}')");
        }
        _plans[fsm.Pointer] = plan;
        return plan;
    }

    private static Plan BuildPlan(PmFsm fsm, GameObject go)
    {
        Plan plan = null;
        foreach (var s in fsm.States)
        {
            if (s?.Actions == null) continue;
            var p = new Plan();
            if (!ReadDown(s, p)) continue;
            if (plan != null) return null; // more than one: not the analysed FSM
            plan = p;
        }
        if (plan == null) return null;

        var next = Follow(fsm, plan.Down);
        if (next == null) return null;
        if (!ReadUp(next, plan))
        {
            if (!IsWarp(next)) return null;
            plan.Warp = next;
            next = Follow(fsm, next);
            if (next == null || !ReadUp(next, plan)) return null;
        }
        if (plan.Moves.Count == 0 && plan.Warp == null) return null; // no warp found
        plan.Label = Label(go);
        return plan;
    }

    // QuickFreeze + CameraFadeOut + one Wait(FINISHED) + optional DOTween move/rotate,
    // plus only harmless getters/setters.
    private static bool ReadDown(FsmState s, Plan p)
    {
        var freeze = 0;
        foreach (var a in s.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var se = a.TryCast<SendEvent>();
            if (se != null)
            {
                if (se.sendEvent?.Name != "QuickFreeze" || !IsZero(se.delay)) return false;
                freeze++;
                continue;
            }
            var fo = a.TryCast<CameraFadeOut>();
            if (fo != null)
            {
                if (p.FadeOut != null || fo.realTime || !IsConst(fo.time) || HasEvent(fo.finishEvent)) return false;
                p.FadeOut = fo;
                continue;
            }
            var w = a.TryCast<Wait>();
            if (w != null)
            {
                if (p.Wait != null || w.realTime || !IsConst(w.time) || w.finishEvent?.Name != "FINISHED") return false;
                p.Wait = w;
                continue;
            }
            var mv = a.TryCast<DotweenMoveTransformTo>();
            if (mv != null)
            {
                if (!PlainTween(mv.duration, mv.setDelay, mv.isSpeedBased, mv.isTimeIndependent, mv.loopsQuantity,
                        (int)mv.updateTypeSelect, mv.finishEvent) || mv.to == null ||
                    (mv.toObject != null && mv.toObject.Value != null)) return false;
                p.Moves.Add(mv);
                continue;
            }
            var rt = a.TryCast<DotweenRotateTransformTo>();
            if (rt != null)
            {
                if (!PlainTween(rt.duration, rt.setDelay, rt.isSpeedBased, rt.isTimeIndependent, rt.loopsQuantity,
                        (int)rt.updateTypeSelect, rt.finishEvent)) return false;
                p.Rotates.Add(rt);
                continue;
            }
            if (a.TryCast<GetFsmGameObject>() != null || a.TryCast<GetPosition>() != null ||
                a.TryCast<GetRotation>() != null || a.TryCast<SetFsmInt>() != null ||
                a.TryCast<PlaySound>() != null) continue;
            return false;
        }
        if (freeze != 1 || p.FadeOut == null || p.Wait == null) return false;
        p.Down = s;
        return OnlyFinished(s);
    }

    // QuickUnfreeze + CameraFadeIn(FINISHED), plus the cursor SetFsmInt.
    private static bool ReadUp(FsmState s, Plan p)
    {
        if (s?.Actions == null) return false;
        var unfreeze = 0;
        CameraFadeIn fi = null;
        foreach (var a in s.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var se = a.TryCast<SendEvent>();
            if (se != null)
            {
                if (se.sendEvent?.Name != "QuickUnfreeze" || !IsZero(se.delay)) return false;
                unfreeze++;
                continue;
            }
            var c = a.TryCast<CameraFadeIn>();
            if (c != null)
            {
                if (fi != null || c.realTime || !IsConst(c.time) || c.finishEvent?.Name != "FINISHED") return false;
                fi = c;
                continue;
            }
            if (a.TryCast<SetFsmInt>() != null) continue;
            return false;
        }
        if (unfreeze != 1 || fi == null) return false;
        p.Up = s;
        p.FadeIn = fi;
        return true;
    }

    // Library "Warp": getters + one-shot SetPosition/SetRotation, finishes at once.
    private static bool IsWarp(FsmState s)
    {
        if (s?.Actions == null) return false;
        var setPos = 0;
        foreach (var a in s.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var sp = a.TryCast<SetPosition>();
            if (sp != null) { if (sp.everyFrame) return false; setPos++; continue; }
            var sr = a.TryCast<SetRotation>();
            if (sr != null) { if (sr.everyFrame) return false; continue; }
            if (a.TryCast<GetFsmGameObject>() != null || a.TryCast<GetPosition>() != null ||
                a.TryCast<GetRotation>() != null || a.TryCast<SetFsmInt>() != null) continue;
            return false;
        }
        return setPos > 0 && OnlyFinished(s);
    }

    private static bool PlainTween(FsmFloat dur, FsmFloat delay, FsmBool speedBased, FsmBool timeIndependent,
        FsmInt loops, int updateType, FsmEvent finish) =>
        IsConst(dur) && IsConst(delay) && dur.Value > 0f && delay.Value >= 0f &&
        !(speedBased?.Value ?? false) && !(timeIndependent?.Value ?? false) &&
        (loops == null || loops.Value <= 1) && updateType == 0 && !HasEvent(finish);

    private static FsmState Follow(PmFsm fsm, FsmState s)
    {
        foreach (var t in s.Transitions)
            if (t != null && t.EventName == "FINISHED") return fsm.GetState(t.ToState);
        return null;
    }

    private static bool OnlyFinished(FsmState s)
    {
        if (s.Transitions == null || s.Transitions.Length == 0) return false;
        foreach (var t in s.Transitions)
            if (t == null || t.EventName != "FINISHED") return false;
        return true;
    }

    // Full path: room prefab instances sit under grid objects, so the room name isn't always first.
    private static string Label(GameObject go) => $"'{FsmHooks.PathOf(go)}'";

    private static bool HasEvent(FsmEvent e) => e != null && !string.IsNullOrEmpty(e.Name);

    private static bool IsZero(FsmFloat f) => f == null || (IsConst(f) && f.Value == 0f);

    private static bool IsConst(FsmFloat f) =>
        f != null && !(f.UseVariable && !string.IsNullOrEmpty(f.Name));
}
