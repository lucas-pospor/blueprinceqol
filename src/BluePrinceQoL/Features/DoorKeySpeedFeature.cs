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
/// Faster key animation when you USE a key on a locked door, plus an optional
/// keyboard shortcut for the USE / EXIT buttons.
///
/// Flow (scene object "UI OVERLAY CAM/Locked Door", FSM "FSM" = the popup root;
/// its child "Fly In Folders/Key Fly In/Standard Key", FSM "FSM" = the key):
///   Door Code "Lock Animation" (InvisFreeze, Wait 0.1) -> "Locked" opens the popup.
///   USE BUTTON (or 1ST BUTTON) "Use Key" sends Begin to Standard Key:
///   "State 5" (instant) -> "State 2": key flies in (DOTween move 1 s), Wait 0.3
///   -> "State 3": key-turn sound, DOTween rotate (delay 0.9, 0.5 s), Wait 1.4
///   -> "Open Sesame" (one instant sequence): Key Manager Adjustment -1 + Update
///      (THE key spend), then "Open Sesame" to the popup root ->
///   root "State 3": "Go" to the door (draft starts / Great Hall door opens),
///      Map Retract (delay 0.1), Wait 0.9 -> "State 4" hides the popup.
///
/// What the feature does: the two DOTween durations/delays are scaled for that
/// one state entry (DOTween reads them in OnEnter, they are put back right after
/// the state's actions started). The three Waits are not changed: once a Wait
/// has run for its scaled time (and the guards pass), OnUpdate fast-forwards its
/// own timer so it fires FINISHED itself on its next update (as ClickUnlock).
/// No action is disabled, no event is sent, no state is skipped: the key is
/// spent by the game's own "Open Sesame" state, exactly once, before the door
/// gets "Go", in vanilla order. Only the time between them is shorter.
///
/// Guards on the key-turn wait (the one that leads to the spend + "Go"):
/// * >= 2 frames after it started (>= 3 after the USE click): the click never
///   reaches DRAFT UI.
/// * DRAFT UI is in "Listener" (it only handles the door's "Go" there).
/// * Key Manager is idle (its active state takes "Update"); otherwise the -1
///   could be dropped and the door would open for free.
/// Any guard failing = that wait just runs out as vanilla. Popup close (root
/// "State 3") keeps >= 0.25 s so the delayed Map Retract (0.1 s) fires first.
///
/// Only the regular "Locked Door" popup is covered. Trunks, lockers, vault,
/// basement/sanctum/_T doors and special-key fly-ins use other FSMs (some spend
/// the key elsewhere) and stay vanilla.
/// </summary>
public class DoorKeySpeedFeature : Feature
{
    public override string Name => "DoorKeySpeed";
    protected override string Description =>
        "Faster key animation on locked doors (key spend and door logic unchanged), optional USE/EXIT hotkeys";

    private const string RootSuffix = "UI OVERLAY CAM/Locked Door";
    private const string KeySuffix = "UI OVERLAY CAM/Locked Door/Fly In Folders/Key Fly In/Standard Key";
    private const string DraftUiPath = "__SYSTEM/THE DRAFT/anchor/DRAFT UI";
    private const string DraftIdleState = "Listener";
    private const string KeyManagerPath = "__SYSTEM/HUD/Keys";
    private const string KeyManagerFsm = "Key Manager";

    private const string KeyMoveState = "State 2", KeyTurnState = "State 3", SpendState = "Open Sesame";
    private const string RootStartState = "State 5", RootCloseState = "State 3", RootHideState = "State 4";

    // Minimum durations (game seconds), whatever the speed.
    private const float MinMoveWait = 0.02f;   // + >= 1 frame
    private const float MinTurnWait = 0.1f;    // + >= 2 frames
    private const float MinClose = 0.25f;      // > Map Retract delay 0.1 s
    private const float MinTween = 0.05f;
    private const int MinFramesMove = 1, MinFramesTurn = 2, MinFramesClose = 2;
    private const int FallbackFrames = 5;
    private const float FallbackTime = 0.0001f;

    private static readonly string[] UseButtons = { "2 Button Spread/USE BUTTON", "3 Button Spread/1ST BUTTON" };
    private static readonly string[] ExitButtons = { "2 Button Spread/EXIT BUTTON", "3 Button Spread/3RD BUTTON" };
    private const float HotkeyMinOpen = 0.25f;  // popup must have been open this long (real time)
    private const float HotkeyGiveUp = 0.5f;

    private MelonPreferences_Entry<float> _speed;
    private MelonPreferences_Entry<bool> _hotkeys, _log;
    private MelonPreferences_Entry<string> _useKeyPref, _exitKeyPref;

    private enum Kind { None, KeyMove, KeyTurn, Spend, RootStart, Close }

    private sealed class Info
    {
        public Kind Kind;
        public Wait Wait;
        public DotweenMoveTransformTo Move;
        public DotweenRotateTransformTo Rotate;
    }

    private sealed class Pending
    {
        public Kind Kind;
        public PmFsm Fsm;
        public FsmState State;
        public Wait Wait;
        public float Target;        // game seconds after entry
        public int MinFrames;
        public int Frame;
        public float EntryTime;     // Time.time (scaled, like the Wait)
        public bool Released;
        public int ReleasedFrame;
        public FsmFloat Shortened;  // fallback only
        public float ShortenedOrig;
        public bool Blocked;
    }

    private sealed class SpendCheck
    {
        public int Before;
        public int Frame;
        public FsmInt Keys;
        public FsmBool KeyLock;
    }

    private sealed class Press
    {
        public Il2Cpp.PlayMakerFSM Fsm;
        public GameObject Go;
        public string Label;
        public int Frame;
        public float GiveUpAt;
    }

    private readonly Dictionary<IntPtr, Info> _states = new();
    private readonly List<Pending> _pending = new();
    private readonly List<(FsmFloat f, float orig)> _restoreAfter = new();
    private IntPtr _restoreAfterState;
    private IntPtr _runFsm;            // Standard Key FSM of the run being shortened
    private SpendCheck _spend;
    private Press _press;

    private PmFsm _draftFsm, _keyMgr;
    private float _nextDraftSearch, _nextKeySearch;
    private GameObject _rootGo;
    private float _rootOpenedAt = -100f;
    private KeyCode _useKey = KeyCode.Return, _exitKey = KeyCode.Backspace;
    private bool _warnedShape, _warnedDraft, _warnedKeyMgr, _warnedFallback;
    private int _runs;

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry($"{Name}_Speed", 4f, "DoorKeySpeed: speed",
            "How many times faster the key animation on locked doors runs (1 = vanilla, 0 = as fast as safely possible: " +
            "~0.15 s from USE to the draft). The key is still spent by the game, once, before the door opens.");
        _hotkeys = Core.Prefs.CreateEntry($"{Name}_Hotkeys", false, "DoorKeySpeed: hotkeys",
            "Keyboard shortcuts for the locked-door popup: UseKey presses USE (or the 1st button), ExitKey presses EXIT");
        _useKeyPref = Core.Prefs.CreateEntry($"{Name}_UseKey", "Return", "DoorKeySpeed: USE key", "UnityEngine.KeyCode name");
        _exitKeyPref = Core.Prefs.CreateEntry($"{Name}_ExitKey", "Backspace", "DoorKeySpeed: EXIT key", "UnityEngine.KeyCode name");
        _log = Core.Prefs.CreateEntry($"{Name}_Log", false, "DoorKeySpeed: log",
            "Log every shortened key animation, key spend check and hold-back (the first 3 are always logged)");
        _useKey = ParseKey(_useKeyPref.Value, KeyCode.Return);
        _exitKey = ParseKey(_exitKeyPref.Value, KeyCode.Backspace);
        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    private KeyCode ParseKey(string s, KeyCode def)
    {
        if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse<KeyCode>(s.Trim(), true, out var k) && k != KeyCode.None) return k;
        Core.Log.Warning($"[{Name}] unknown key '{s}', using {def}");
        return def;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        RestoreAfter();
        foreach (var p in _pending) RestoreFallback(p);
        _pending.Clear();
        _states.Clear();
        _runFsm = IntPtr.Zero;
        _spend = null;
        _press = null;
        _draftFsm = _keyMgr = null;
        _nextDraftSearch = _nextKeySearch = 0f;
        _rootGo = null;
        _rootOpenedAt = -100f;
    }

    /// <summary>Duration factor: 1 = vanilla, 0 = minimums only.</summary>
    private float Factor()
    {
        var s = _speed.Value;
        if (float.IsNaN(s)) return 1f;
        if (s <= 0f) return 0f;
        return s <= 1f ? 1f : 1f / s;
    }

    // ------------------------------------------------------------------ hooks

    private void OnBeforeEnter(FsmState state)
    {
        // A fallback Wait change for this state is undone before the Wait reads its time again.
        if (_pending.Count > 0)
            for (var i = _pending.Count - 1; i >= 0; i--)
                if (_pending[i].State.Pointer == state.Pointer) { RestoreFallback(_pending[i]); _pending.RemoveAt(i); }

        if (!Enabled)
        {
            if (_pending.Count > 0) { foreach (var p in _pending) RestoreFallback(p); _pending.Clear(); }
            _runFsm = IntPtr.Zero;
            return;
        }

        var name = state.Name;
        if (name != KeyMoveState && name != KeyTurnState && name != SpendState && name != RootStartState) return;
        var info = InfoOf(state);
        if (info.Kind == Kind.None) return;

        var f = Factor();
        switch (info.Kind)
        {
            case Kind.KeyMove:
                // Start of a key animation (Begin -> State 5 -> State 2).
                _runFsm = f < 1f ? state.Fsm.Pointer : IntPtr.Zero;
                if (_runFsm == IntPtr.Zero) return;
                ScaleTween(state, info.Move.duration, f, MinTween);
                break;
            case Kind.KeyTurn:
                if (_runFsm != state.Fsm.Pointer) return;
                ScaleTween(state, info.Rotate.duration, f, MinTween);
                ScaleTween(state, info.Rotate.setDelay, f, 0f);
                break;
            case Kind.Spend:
                if (_runFsm == state.Fsm.Pointer) StartSpendCheck(state);
                break;
        }
    }

    private void OnAfterEnter(FsmState state)
    {
        // DOTween read duration/delay in OnEnter: put the stored values back now.
        if (_restoreAfterState == state.Pointer) RestoreAfter();

        var name = state.Name;
        if (name != KeyMoveState && name != KeyTurnState && name != RootStartState) return;
        var info = InfoOf(state);
        switch (info.Kind)
        {
            case Kind.RootStart:
                // Popup (re)opened: no key animation of ours is running for it yet.
                _rootGo = state.Fsm.GameObject;
                _rootOpenedAt = Time.unscaledTime;
                _runFsm = IntPtr.Zero;
                return;
            case Kind.KeyMove:
            case Kind.KeyTurn:
                if (!Enabled || _runFsm != state.Fsm.Pointer) return;
                break;
            case Kind.Close:
                // Root "State 3": only when the key animation before it was ours.
                if (!Enabled || _runFsm == IntPtr.Zero) return;
                _runFsm = IntPtr.Zero;
                break;
            default:
                return;
        }

        var f = Factor();
        if (f >= 1f) return;
        var (min, frames) = info.Kind switch
        {
            Kind.KeyMove => (MinMoveWait, MinFramesMove),
            Kind.KeyTurn => (MinTurnWait, MinFramesTurn),
            _ => (MinClose, MinFramesClose),
        };
        var vanilla = info.Wait.time?.Value ?? 0f;
        var target = Math.Max(vanilla * f, min);
        if (target >= vanilla) return;

        _pending.Add(new Pending
        {
            Kind = info.Kind,
            Fsm = state.Fsm,
            State = state,
            Wait = info.Wait,
            Target = target,
            MinFrames = frames,
            Frame = Time.frameCount,
            EntryTime = Time.time,
        });
    }

    public override void OnUpdate()
    {
        if (_pending.Count > 0)
            for (var i = _pending.Count - 1; i >= 0; i--)
                if (!Step(_pending[i])) _pending.RemoveAt(i);
        if (_spend != null && Time.frameCount > _spend.Frame + 1) FinishSpendCheck();
        if (_hotkeys.Value) Hotkeys();
    }

    // ------------------------------------------------------------- tweens

    private void ScaleTween(FsmState state, FsmFloat t, float f, float min)
    {
        if (!IsConst(t)) return;
        var orig = t.Value;
        var v = Math.Max(orig * f, Math.Min(orig, min));
        if (v >= orig) return;
        if (_restoreAfterState != state.Pointer) { RestoreAfter(); _restoreAfterState = state.Pointer; }
        _restoreAfter.Add((t, orig));
        t.Value = v;
    }

    private void RestoreAfter()
    {
        foreach (var (f, v) in _restoreAfter)
            try { f.Value = v; } catch { /* object gone */ }
        _restoreAfter.Clear();
        _restoreAfterState = IntPtr.Zero;
    }

    // ------------------------------------------------------------- waits

    /// <summary>False once the pending wait is over (remove it).</summary>
    private bool Step(Pending p)
    {
        FsmState active;
        try { active = p.Fsm?.ActiveState; } catch { active = null; }
        if (active == null || active.Pointer != p.State.Pointer)
        {
            RestoreFallback(p);
            return false;
        }

        var now = Time.frameCount;
        if (p.Released)
        {
            if (p.Shortened == null && now > p.ReleasedFrame + FallbackFrames) ApplyFallback(p);
            return true;
        }
        if (now - p.Frame < p.MinFrames) return true;
        var elapsed = Time.time - p.EntryTime;
        if (elapsed < p.Target) return true;

        var t = p.Wait.time;
        if (t == null || p.Wait.timer >= t.Value) return true;   // finishing on its own anyway

        if (p.Kind == Kind.KeyTurn)
        {
            var why = Gate();
            if (why != null)
            {
                if ((_log.Value || _runs < 3) && !p.Blocked) Core.Log.Msg($"[{Name}] holding the key turn ({why})");
                p.Blocked = true;
                return true;
            }
        }

        p.Wait.timer = t.Value;   // Wait.OnUpdate: timer += dt; timer >= time -> FINISHED
        p.Released = true;
        p.ReleasedFrame = now;
        if (_log.Value || _runs < 3)
            Core.Log.Msg($"[{Name}] {Label(p.Kind)}: {t.Value:0.00}s -> {elapsed:0.00}s");
        if (p.Kind == Kind.Close) _runs++;
        return true;
    }

    /// <summary>Null when the key may be spent and the door opened now.</summary>
    private string Gate()
    {
        var draft = DraftFsm();
        if (draft == null) return "DRAFT UI not found";
        var ds = draft.ActiveStateName;
        if (ds != DraftIdleState) return $"DRAFT UI in '{ds}'";

        var km = KeyMgr();
        if (km == null) return "Key Manager not found";
        var ks = km.ActiveState;
        if (ks == null || !HasTransition(ks, "Update")) return $"Key Manager in '{ks?.Name}'";
        return null;
    }

    private void ApplyFallback(Pending p)
    {
        var t = p.Wait.time;
        if (!IsConst(t)) return;
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
        try
        {
            if (Math.Abs(p.Shortened.Value - FallbackTime) < 1e-7f) p.Shortened.Value = p.ShortenedOrig;
        }
        catch { /* object gone */ }
        p.Shortened = null;
    }

    private static string Label(Kind k) => k switch
    {
        Kind.KeyMove => "key fly-in",
        Kind.KeyTurn => "key turn (spend + door Go next)",
        _ => "popup close",
    };

    // ---------------------------------------------------------- spend check
    // Diagnostic only: confirms the game's own "Open Sesame" took one key.

    private void StartSpendCheck(FsmState state)
    {
        _spend = null;
        // "STOP time" set: the game's own no-cost path ("skip" -> State 6), nothing to check.
        var stop = state.Fsm.Variables?.GetFsmBool("STOP time");
        if (stop != null && stop.Value) return;
        var km = KeyMgr();
        var keys = km?.Variables?.GetFsmInt("KEYS");
        if (keys == null) return;
        _spend = new SpendCheck
        {
            Before = keys.Value,
            Frame = Time.frameCount,
            Keys = keys,
            KeyLock = km.Variables.GetFsmBool("KEY LOCK"),
        };
    }

    private void FinishSpendCheck()
    {
        var s = _spend;
        _spend = null;
        int after;
        try { after = s.Keys.Value; } catch { return; }
        var locked = s.KeyLock != null && s.KeyLock.Value;
        if (after == s.Before - 1)
        {
            if (_log.Value || _runs < 3) Core.Log.Msg($"[{Name}] key spent: {s.Before} -> {after}");
        }
        else if (!locked && s.Before > 0)
        {
            Core.Log.Warning($"[{Name}] key count {s.Before} -> {after} after the door key animation (expected {s.Before - 1})");
        }
    }

    // ---------------------------------------------------------- hotkeys
    // Same events the game's ControllerSelectable sends: "hov" (select), then
    // "click" (use) once the button sits in its plain hovered state.

    private void Hotkeys()
    {
        if (_press != null) { StepPress(); return; }
        bool use = Input.GetKeyDown(_useKey), exit = !use && Input.GetKeyDown(_exitKey);
        if (!use && !exit) return;

        var root = _rootGo;
        if (root == null || !root.activeInHierarchy) return;
        if (Time.unscaledTime - _rootOpenedAt < HotkeyMinOpen) return;
        var rootState = FsmUtil.Get(root)?.Fsm?.ActiveStateName;
        if (rootState == RootCloseState || rootState == RootHideState) return;

        foreach (var path in use ? UseButtons : ExitButtons)
        {
            var t = root.transform.Find(path);
            if (t == null || !t.gameObject.activeInHierarchy) continue;
            var fsm = FsmUtil.Get(t.gameObject);
            var st = fsm?.Fsm?.ActiveState;
            if (st == null) return;
            if (!Clickable(st))
            {
                if (!HasTransition(st, "hov")) return;   // already used / busy
                fsm.Fsm.Event("hov");
            }
            _press = new Press
            {
                Fsm = fsm,
                Go = t.gameObject,
                Label = path,
                Frame = Time.frameCount,
                GiveUpAt = Time.unscaledTime + HotkeyGiveUp,
            };
            return;
        }
    }

    private void StepPress()
    {
        var p = _press;
        if (Time.frameCount <= p.Frame) return;
        if (Time.unscaledTime > p.GiveUpAt || p.Go == null || !p.Go.activeInHierarchy) { _press = null; return; }
        var st = p.Fsm?.Fsm?.ActiveState;
        if (st == null) { _press = null; return; }
        if (!Clickable(st)) return;   // still in the 0.05 s hover lead-in
        _press = null;
        p.Fsm.Fsm.Event("click");
        if (_log.Value) Core.Log.Msg($"[{Name}] hotkey -> {p.Label}");
    }

    // The button's plain hovered state: takes "click" and doesn't move on by itself.
    private static bool Clickable(FsmState s) => HasTransition(s, "click") && !HasTransition(s, "FINISHED");

    // ---------------------------------------------------------- lookups

    private PmFsm DraftFsm()
    {
        if (_draftFsm != null) return _draftFsm;
        if (Time.unscaledTime < _nextDraftSearch) return null;
        _nextDraftSearch = Time.unscaledTime + 1f;
        _draftFsm = FsmUtil.Find(DraftUiPath)?.Fsm;
        if (_draftFsm == null && !_warnedDraft)
        {
            _warnedDraft = true;
            Core.Log.Warning($"[{Name}] '{DraftUiPath}' not found; key animations keep the vanilla key turn");
        }
        return _draftFsm;
    }

    private PmFsm KeyMgr()
    {
        if (_keyMgr != null) return _keyMgr;
        if (Time.unscaledTime < _nextKeySearch) return null;
        _nextKeySearch = Time.unscaledTime + 1f;
        _keyMgr = FsmUtil.Get(GameObject.Find(KeyManagerPath), KeyManagerFsm)?.Fsm;
        if (_keyMgr == null && !_warnedKeyMgr)
        {
            _warnedKeyMgr = true;
            Core.Log.Warning($"[{Name}] '{KeyManagerPath}' Key Manager not found; key animations keep the vanilla key turn");
        }
        return _keyMgr;
    }

    // ---------------------------------------------------------- classifying

    private Info InfoOf(FsmState state)
    {
        if (_states.TryGetValue(state.Pointer, out var info)) return info;
        try { info = Classify(state); }
        catch (Exception e)
        {
            info = null;
            Core.Log.Warning($"[{Name}] could not analyse state '{state.Name}': {e.Message}");
        }
        info ??= new Info();
        _states[state.Pointer] = info;
        return info;
    }

    private Info Classify(FsmState state)
    {
        var fsm = state.Fsm;
        var go = fsm?.GameObject;
        if (go == null || fsm.Name != "FSM" || state.Actions == null) return null;
        var goName = go.name;
        var name = state.Name;

        if (goName == "Standard Key")
        {
            if (!FsmHooks.PathOf(go).EndsWith(KeySuffix, StringComparison.Ordinal)) return null;
            switch (name)
            {
                case KeyMoveState:
                {
                    var w = SoleWait(state, KeyTurnState);
                    var mv = Sole<DotweenMoveTransformTo>(state);
                    if (w == null || mv == null || IsEvent(mv.finishEvent) || !Spends(fsm.GetState(SpendState)))
                        return Bad(state);
                    return new Info { Kind = Kind.KeyMove, Wait = w, Move = mv };
                }
                case KeyTurnState:
                {
                    var w = SoleWait(state, SpendState);
                    var rt = Sole<DotweenRotateTransformTo>(state);
                    if (w == null || rt == null || IsEvent(rt.finishEvent)) return Bad(state);
                    return new Info { Kind = Kind.KeyTurn, Wait = w, Rotate = rt };
                }
                case SpendState:
                    return Spends(state) ? new Info { Kind = Kind.Spend } : Bad(state);
            }
            return null;
        }

        if (goName == "Locked Door" && FsmHooks.PathOf(go).EndsWith(RootSuffix, StringComparison.Ordinal))
        {
            if (name == RootStartState) return new Info { Kind = Kind.RootStart };
            if (name == RootCloseState)
            {
                var w = SoleWait(state, RootHideState);
                if (w == null || !SendsEvent(state, "Go")) return Bad(state);
                return new Info { Kind = Kind.Close, Wait = w };
            }
        }
        return null;
    }

    private Info Bad(FsmState state)
    {
        if (!_warnedShape)
            Core.Log.Warning($"[{Name}] '{FsmHooks.PathOf(state.Fsm.GameObject)}' state '{state.Name}' doesn't look as expected; left vanilla");
        _warnedShape = true;
        return null;
    }

    // The state's only Wait: plain scaled time, literal value, FINISHED -> next,
    // and FINISHED is the state's only way out.
    private static Wait SoleWait(FsmState state, string next)
    {
        var ts = state.Transitions;
        if (ts == null || ts.Length != 1 || ts[0] == null || ts[0].EventName != "FINISHED" || ts[0].ToState != next) return null;
        Wait wait = null;
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var w = a.TryCast<Wait>();
            if (w == null) continue;
            if (wait != null || w.realTime || !IsConst(w.time) || w.finishEvent?.Name != "FINISHED") return null;
            wait = w;
        }
        return wait;
    }

    private static T Sole<T>(FsmState state) where T : FsmStateAction
    {
        T found = null;
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var t = a.TryCast<T>();
            if (t == null) continue;
            if (found != null) return null;
            found = t;
        }
        return found;
    }

    // "Open Sesame": Key Manager Adjustment Amount = -1, Update, then Open Sesame to the popup.
    private static bool Spends(FsmState s)
    {
        if (s?.Actions == null) return false;
        var adj = false;
        foreach (var a in s.Actions)
        {
            var si = a?.TryCast<SetFsmInt>();
            if (si != null && si.Enabled && si.variableName?.Value == "Adjustment Amount" && si.setValue != null && si.setValue.Value == -1)
                adj = true;
        }
        return adj && SendsEvent(s, "Update") && SendsEvent(s, "Open Sesame");
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

    private static bool HasTransition(FsmState s, string ev)
    {
        var ts = s.Transitions;
        if (ts == null) return false;
        foreach (var t in ts)
            if (t != null && t.EventName == ev) return true;
        return false;
    }

    private static bool IsEvent(FsmEvent e) => e != null && !string.IsNullOrEmpty(e.Name);

    // A literal parameter (not a reference to a shared FSM variable).
    private static bool IsConst(FsmFloat f) =>
        f != null && !(f.UseVariable && !string.IsNullOrEmpty(f.Name));
}
