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
/// Shortens the ~1 s "can walk but can't click" window after the player is
/// unfrozen (cards, documents, shop menus, Great Hall doors, drafts).
///
/// Where the lock lives:
/// * Player FSM ("__SYSTEM/FPS Home/FPSController - Prince", FSM "FSM"):
///   every freeze makes "UI OVERLAY CAM" the main camera (SetMainCamera), and
///   MousePickEvent/ClickableRaycaster pick from Camera.main, so nothing in the
///   world can be hovered or clicked. "UnFreeze" -> State 11 -> "UNFROZEN 2"
///   turns movement back on and Waits 0.99 s; "FadeUnFreeze" (doors/drafts)
///   -> State 12 -> State 9 (0.2) -> "UNFROZEN 3" waits 0.4 s. Only then does
///   "State 7" give Camera.main back to FirstPersonCharacter and set the
///   crosshair (Global Manager NewCursor = 2).
/// * Global Manager (FSM "FSM"): cards send it deactivate_noUnfreeze
///   (State 43, invisible locked cursor, Wait 1.1), dig cards "disable"
///   (State 42, 0.8), DRAFT UI "deactivate" (State 40, 2). Each then goes to
///   State 41 (NewCursor = 2) -> Cursor Check 2, which draws the crosshair.
///
/// What the feature does: nothing at state entry. From OnUpdate, once the
/// wait has run for ClickUnlock_Delay seconds (scaled game time, like the
/// Wait itself) and at least MinFrames frames, it fast-forwards that Wait's
/// own timer to its end. The Wait then fires its own FINISHED on its next
/// update and the game's State 7 / State 41 run unchanged. No action is
/// disabled, no event is sent, no FSM data is changed (so there's nothing to
/// restore). If the Wait ever ignores the timer (unexpected), a fallback
/// shortens its time for that one entry and restores it afterwards.
///
/// Guards (any failing = the vanilla wait just runs out):
/// * >= MinFrames frames after the unfreeze state was entered, so the click
///   that closed the card/menu (GetMouseButtonDown / Rewired ButtonDown are
///   true for one frame only) can never also reach the world.
/// * DRAFT UI ("__SYSTEM/THE DRAFT/anchor/DRAFT UI") must be in "Listener".
///   A door click sends "Go" to DRAFT UI once and freezes the player; DRAFT UI
///   only handles "Go" in Listener, which it reaches ~1.7 s after a plan pick.
///   Vanilla's 0.6 s unlock after a draft lines up with that; clicking a
///   second door earlier would leave the player frozen with no draft.
/// * Global Manager cursor waits are only cut once the player FSM is back in
///   State 7 / 14 / 17 (camera restored, not frozen), so the crosshair never
///   shows while the world can't be clicked.
/// </summary>
public class ClickUnlockFeature : Feature
{
    public override string Name => "ClickUnlock";
    protected override string Description =>
        "Shorter click lock-out after closing cards, menus and documents (camera/crosshair come back sooner)";

    private const string PlayerObject = "FPSController - Prince";
    private const string PlayerPath = "__SYSTEM/FPS Home/FPSController - Prince"; // an inactive older copy sits at "FPS Home/..."
    private const string GmObject = "Global Manager";
    private const string DraftUiPath = "__SYSTEM/THE DRAFT/anchor/DRAFT UI";
    private const string DraftIdleState = "Listener";
    private const string PlayerDoneState = "State 7";   // camera + crosshair back
    private const string GmDoneState = "State 41";      // NewCursor = 2

    private const string UnfreezeState = "UNFROZEN 2";  // "UnFreeze": cards, menus, documents, Great Hall doors
    private const string FadeUnfreezeState = "UNFROZEN 3"; // "FadeUnFreeze": doors after a draft
    private const string QuickUnfreezeState = "UNFROZEN";  // "QuickUnfreeze": camera zooms (safes, terminals ...), dig cards

    private static readonly HashSet<string> GmWaitStates = new() { "State 40", "State 42", "State 43" };
    // Player states in which the world is clickable again (Camera.main = player camera).
    private static readonly HashSet<string> PlayerUnlockedStates = new() { "State 7", "State 14", "State 17" };

    private const int MinFrames = 2;          // never release in the click frame or the one after it
    private const int FallbackFrames = 5;     // timer fast-forward should finish within a frame or two
    private const float FallbackTime = 0.0001f;

    private MelonPreferences_Entry<float> _delay;
    private MelonPreferences_Entry<bool> _afterDrafts, _quick, _crosshair, _log;

    private enum Kind { None, Player, Gm }

    private sealed class Info
    {
        public Kind Kind;
        public Wait Wait;
    }

    private sealed class Pending
    {
        public Kind Kind;
        public PmFsm Fsm;
        public FsmState State;
        public Wait Wait;
        public int Frame;
        public float EntryTime;     // scaled, like the Wait
        public bool Released;
        public int ReleasedFrame;
        public float Vanilla;
        public FsmFloat Shortened;   // fallback only
        public float ShortenedOrig;
        public bool Blocked;         // logged "held back" once for this entry
    }

    private readonly Dictionary<IntPtr, Info> _states = new();
    private Pending _player, _gm;
    private PmFsm _playerFsm;
    private PmFsm _draftFsm;
    private float _nextDraftSearch;
    private bool _warnedDraft, _warnedShape, _warnedFallback;
    private int _releases;   // the first few are always logged

    protected override void CreatePrefs()
    {
        _delay = Core.Prefs.CreateEntry("ClickUnlock_Delay", 0.1f, "ClickUnlock: delay",
            "Seconds after an unfreeze before you can click the world again (vanilla: 0.99 after cards/menus, 0.6 after drafts). " +
            "Never less than two frames, whatever the value.");
        _afterDrafts = Core.Prefs.CreateEntry("ClickUnlock_AfterDrafts", true, "ClickUnlock: after drafts",
            "Also shorten the 0.4 s lock after walking through a freshly drafted door (still waits for the draft screen to reset)");
        _quick = Core.Prefs.CreateEntry("ClickUnlock_AfterZooms", true, "ClickUnlock: after zooms",
            "Also shorten the 0.1 s after a quick unfreeze (leaving a safe, terminal or other zoomed-in object) to two frames, " +
            "so the crosshair comes back as soon as the camera is back");
        _crosshair = Core.Prefs.CreateEntry("ClickUnlock_Crosshair", true, "ClickUnlock: crosshair",
            "Also bring the crosshair back as soon as clicking works again (Global Manager cursor waits: 1.1 s cards, 0.8 s dig cards, 2 s drafts)");
        _log = Core.Prefs.CreateEntry("ClickUnlock_Log", false, "ClickUnlock: log",
            "Log every shortened lock and why one is held back (for testing; the first 3 are always logged)");
        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        RestoreFallback(_player);
        RestoreFallback(_gm);
        _player = _gm = null;
        _states.Clear();
        _playerFsm = null;
        _draftFsm = null;
        _nextDraftSearch = 0f;
    }

    private float Delay()
    {
        var d = _delay.Value;
        return float.IsNaN(d) || d < 0f ? 0f : d;
    }

    // ------------------------------------------------------------------ hooks

    private void OnBeforeEnter(FsmState state)
    {
        // Undo a fallback change before the Wait's OnEnter reads its time again.
        // (Always, also when disabled.)
        if (_player?.Shortened != null && state.Pointer == _player.State.Pointer) { RestoreFallback(_player); _player = null; }
        if (_gm?.Shortened != null && state.Pointer == _gm.State.Pointer) { RestoreFallback(_gm); _gm = null; }
    }

    private void OnAfterEnter(FsmState state)
    {
        var name = state.Name;
        // Cheap filter first: every state entry in the game comes through here.
        var maybePlayer = name == UnfreezeState || name == FadeUnfreezeState || name == QuickUnfreezeState ||
                          PlayerUnlockedStates.Contains(name);
        if (!maybePlayer && !GmWaitStates.Contains(name)) return;

        var info = InfoOf(state);
        if (info.Kind == Kind.Player) _playerFsm = state.Fsm;
        if (info.Wait == null) return;   // a player "unlocked" state, or not ours
        if (!Enabled) return;
        if (info.Kind == Kind.Player && name == FadeUnfreezeState && !_afterDrafts.Value) return;
        if (info.Kind == Kind.Player && name == QuickUnfreezeState && !_quick.Value) return;
        if (info.Kind == Kind.Gm && !_crosshair.Value) return;

        var p = new Pending
        {
            Kind = info.Kind,
            Fsm = state.Fsm,
            State = state,
            Wait = info.Wait,
            Frame = Time.frameCount,
            EntryTime = Time.time,
            Vanilla = info.Wait.time?.Value ?? 0f,
        };
        if (info.Kind == Kind.Player) { RestoreFallback(_player); _player = p; }
        else { RestoreFallback(_gm); _gm = p; }
    }

    public override void OnUpdate()
    {
        _player = Step(_player);
        _gm = Step(_gm);
    }

    // OnUpdate only runs while the feature is enabled; when it's switched off
    // mid-wait the vanilla Wait just runs out. A fallback change (if any) is
    // undone at the next entry of that state or on scene load.

    // ------------------------------------------------------------- release

    private Pending Step(Pending p)
    {
        if (p == null) return null;
        var active = p.Fsm?.ActiveState;
        if (active == null || active.Pointer != p.State.Pointer)
        {
            RestoreFallback(p);   // the state is over (finished, or a Freeze etc. took over)
            return null;
        }

        var now = Time.frameCount;
        if (p.Released)
        {
            // The Wait should have finished by now. If it hasn't, its timer isn't
            // what we think it is: shorten its time for this entry instead.
            if (p.Shortened == null && now > p.ReleasedFrame + FallbackFrames) ApplyFallback(p);
            return p;
        }

        if (now - p.Frame < MinFrames) return p;
        if (p.State.Name != QuickUnfreezeState && Time.time - p.EntryTime < Delay()) return p;

        var wait = p.Wait;
        var t = wait.time;
        if (t == null || wait.timer >= t.Value) return p;  // finishing on its own anyway

        var why = Gate(p);
        if (why != null)
        {
            if (_log.Value && !p.Blocked) Core.Log.Msg($"[{Name}] holding '{p.State.Name}' ({why})");
            p.Blocked = true;
            return p;
        }

        var elapsed = Time.time - p.EntryTime;
        wait.timer = t.Value;   // Wait.OnUpdate: timer += dt; timer >= time -> FINISHED
        p.Released = true;
        p.ReleasedFrame = now;
        if (_log.Value || _releases < 3)
            Core.Log.Msg($"[{Name}] {Label(p)}: {p.Vanilla:0.00}s -> {elapsed:0.00}s");
        _releases++;
        return p;
    }

    /// <summary>Null when it is safe to end the wait now, else the reason it isn't.</summary>
    private string Gate(Pending p)
    {
        var draft = DraftFsm();
        if (draft == null) return "DRAFT UI not found";
        var ds = draft.ActiveStateName;
        if (ds != DraftIdleState) return $"DRAFT UI in '{ds}'";

        if (p.Kind == Kind.Gm)
        {
            var pl = _playerFsm ??= FsmUtil.Find(PlayerPath)?.Fsm;
            if (pl == null) return "player FSM not found";
            var ps = pl.ActiveStateName;
            if (ps == null || !PlayerUnlockedStates.Contains(ps)) return $"player in '{ps}'";
            var frozen = pl.Variables?.GetFsmBool("currently frozen");
            if (frozen != null && frozen.Value) return "player frozen";
        }
        return null;
    }

    private void ApplyFallback(Pending p)
    {
        var t = p.Wait.time;
        if (t == null || t.UseVariable && !string.IsNullOrEmpty(t.Name)) return; // only literal values
        if (!_warnedFallback)
            Core.Log.Warning($"[{Name}] '{p.State.Name}' didn't finish after its timer was fast-forwarded; shortening its time instead");
        _warnedFallback = true;
        p.Shortened = t;
        p.ShortenedOrig = t.Value;
        t.Value = FallbackTime;
    }

    private static void RestoreFallback(Pending p)
    {
        if (p?.Shortened == null) return;
        // Only if nobody else has changed it since.
        if (Math.Abs(p.Shortened.Value - FallbackTime) < 1e-7f) p.Shortened.Value = p.ShortenedOrig;
        p.Shortened = null;
    }

    private PmFsm DraftFsm()
    {
        if (_draftFsm != null) return _draftFsm;
        if (Time.unscaledTime < _nextDraftSearch) return null;
        _nextDraftSearch = Time.unscaledTime + 1f;
        _draftFsm = FsmUtil.Find(DraftUiPath)?.Fsm;
        if (_draftFsm == null && !_warnedDraft)
        {
            _warnedDraft = true;
            Core.Log.Warning($"[{Name}] '{DraftUiPath}' not found; click locks stay vanilla until it is");
        }
        return _draftFsm;
    }

    private static string Label(Pending p) =>
        p.Kind == Kind.Player
            ? (p.State.Name == FadeUnfreezeState ? "unlock after draft"
                : p.State.Name == QuickUnfreezeState ? "unlock after zoom" : "unlock after unfreeze")
            : $"crosshair ({p.State.Name})";

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
        if (go == null || fsm.Name != "FSM") return null;
        var name = state.Name;

        if (go.name == PlayerObject)
        {
            if (FsmHooks.PathOf(go) != PlayerPath) return null;
            if (name != UnfreezeState && name != FadeUnfreezeState && name != QuickUnfreezeState) return new Info { Kind = Kind.Player };
            var w = SoleWait(state, PlayerDoneState);
            if (w == null) WarnShape($"player '{name}'");
            return new Info { Kind = Kind.Player, Wait = w };
        }

        if (go.name == GmObject && GmWaitStates.Contains(name))
        {
            var w = SoleWait(state, GmDoneState);
            if (w != null && !OnlySetFsmInt(fsm.GetState(GmDoneState))) w = null;
            if (w == null) { WarnShape($"Global Manager '{name}'"); return null; }
            return new Info { Kind = Kind.Gm, Wait = w };
        }
        return null;
    }

    // The state's only Wait, if it is a plain scaled-time Wait whose FINISHED
    // leads to <paramref name="next"/>, and nothing else in the state could
    // send FINISHED first.
    private static Wait SoleWait(FsmState state, string next)
    {
        if (state.Actions == null || state.Transitions == null) return null;
        string to = null;
        foreach (var t in state.Transitions)
            if (t != null && t.EventName == "FINISHED") { to = t.ToState; break; }
        if (to != next) return null;

        Wait wait = null;
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var w = a.TryCast<Wait>();
            if (w == null) continue;
            if (wait != null || w.realTime || w.time == null) return null;
            if (w.finishEvent == null || w.finishEvent.Name != "FINISHED") return null;
            wait = w;
        }
        return wait;
    }

    private static bool OnlySetFsmInt(FsmState s)
    {
        if (s?.Actions == null) return false;
        foreach (var a in s.Actions)
        {
            if (a == null || !a.Enabled) continue;
            if (a.TryCast<SetFsmInt>() == null) return false;
        }
        return true;
    }

    private void WarnShape(string what)
    {
        if (_warnedShape) return;
        _warnedShape = true;
        Core.Log.Warning($"[{Name}] {what} doesn't look as expected; leaving that wait vanilla");
    }
}
