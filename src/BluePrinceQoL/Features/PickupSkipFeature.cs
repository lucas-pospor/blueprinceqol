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
/// Skips item-pickup presentation: the item flying to the HUD / shrinking,
/// the waits around it, and the "You Found X" card that needs a click.
/// All logic actions still run, in their original order; only presentation
/// actions are switched off for that one state entry.
///
/// Three kinds of FSM are touched:
///  1. World item FSMs (coins, gems, keys, food, special items). Detected by
///     structure: the states reached from a "click" (or "fly", multi-coin
///     children) transition contain a PmtDeSpawn, a "...Pickup" event or
///     "Add Coins". In those states Wait / iTween* / Dotween* are disabled,
///     iTweenScaleTo is snapped to its end scale, and the "fly" stagger and
///     PmtDeSpawn delays are zeroed.
///  2. "You Found ..." cards (FSMs on children of UI OVERLAY CAM/You Found Text).
///     Wait / iTween* / Dotween* disabled; states waiting for "click" get
///     "click" sent one frame later (never inside the Global Manager's own
///     state entry, which activated the card).
///  3. Any state that activates such a card and sends a delayed "Freeze" to
///     the player (Global Manager "X Pickup" states, Dig Engine): that delay
///     is zeroed, so the card's "UnFreeze" can't arrive before the "Freeze".
///     Delayed freezes we don't zero hold back auto-clicks until they've fired.
///  4. Dig Engine companion states (only with SkipDigShopCards on). The Dig
///     Engine waits for the same click as its "You Dug Up" card in a state of
///     its own and only grants keys/coins/gems 1 s after it; see DigRoles.
///     That state gets the deferred "click" too, and the Waits around it are
///     shortened to one frame, so the reward lands right after the card closes.
/// </summary>
public class PickupSkipFeature : Feature
{
    public override string Name => "PickupSkip";
    protected override string Description => "Skip item pickup animations and the 'You Found ...' cards (item still granted normally)";

    private MelonPreferences_Entry<bool> _skipCards;
    private MelonPreferences_Entry<bool> _skipExtraCards;
    private MelonPreferences_Entry<bool> _skipShopCards;
    private MelonPreferences_Entry<string> _unfreezeKeyPref;
    private KeyCode _unfreezeKey = KeyCode.None;
    private const string PlayerPath = "__SYSTEM/FPS Home/FPSController - Prince";

    private const string CardParent = "You Found Text";
    private static readonly string[] MainCardPrefixes = { "You Found", "You assembled", "You Picked Up" };
    private static readonly string[] ExtraCardPrefixes = { "You Dug Up" };
    // Shop cards: no Freeze/UnFreeze or Global Manager events, just wait/click/slide.
    private static readonly string[] ShopCardPrefixes = { "You Bought" };
    private static readonly HashSet<string> FreezeEvents = new()
        { "Freeze", "QuickFreeze", "InvisFreeze", "Freeze No Retract", "PumpFreeze", "Camfreeze", "disablemove" };

    private const int CatNone = 0, CatMain = 1, CatExtra = 2, CatShop = 3;
    private const float CardWait = 0.001f; // > 0, so a Wait ends on the next frame, never the current one

    // ---- per-state plans, computed once per FsmState (cleared each scene load) ----
    private sealed class Plan
    {
        public FsmState State;                       // keeps the native object (and its pointer) alive
        public List<FsmStateAction> Disable;         // presentation actions (item chain / card)
        public List<(FsmStateAction a, string ev)> Finish; // their finish events, in action order
        public List<FsmFloat> ShortWaits;            // card Waits: shortened to one frame, not disabled
        public List<iTweenScaleTo> Snap;             // item chain: apply end scale instantly
        public List<FsmFloat> ZeroDelays;            // item chain: "fly" stagger, PmtDeSpawn delay
        public List<(FsmFloat delay, int cat)> FreezeZero; // delayed freeze in a state that opens a card
        public List<FsmFloat> FreezeHold;            // other delayed freezes
        public int CardCat;                          // card state: which pref governs it
        public bool Click;                           // card state waits for "click"
        public bool Item;
        public List<FsmFloat> DigWaits;              // Dig Engine: Waits around a dig card, shortened to one frame
        public bool DigClick;                        // Dig Engine: polls for the same click as the dig card
    }
    private static readonly Plan Nothing = new();

    private sealed class FsmInfo
    {
        public PmFsm Fsm;
        public HashSet<string> ItemChain;   // null = not an item FSM
        public int CardCat;                 // > 0 = card FSM
        public Dictionary<string, int> DigRoles; // Dig Engine companion states (see DigRoles), null = not computed
    }
    private const int DigResult = 1, DigClickState = 2, DigGrantWait = 3;

    private readonly Dictionary<IntPtr, Plan> _plans = new();
    private readonly Dictionary<IntPtr, FsmInfo> _fsms = new();

    // ---- things we changed, restored when the state is left / re-entered / feature off ----
    private sealed class Pending
    {
        public FsmState State;
        public readonly List<FsmStateAction> Actions = new();
        public readonly List<(FsmFloat f, float v)> Floats = new();
    }
    private readonly Dictionary<IntPtr, Pending> _pending = new();
    private readonly List<IntPtr> _tmp = new();

    // ---- deferred "click" for cards ----
    private readonly List<(FsmState state, PmFsm fsm, int frame, float giveUp)> _clicks = new();

    // ---- deferred finish events of disabled Wait/tween actions ----
    // A disabled action never sends its finish event. States like a card's
    // "SetFsmInt everyFrame + Wait 0.5 -> FINISHED" then never end, so the
    // event the action would have sent is sent for it, one frame later.
    private readonly List<(FsmState state, PmFsm fsm, string ev, int frame)> _finishes = new();
    private float _holdClicksUntil;

    private bool _warnedStuck, _warnedPlan, _loggedItem, _loggedCard;

    protected override void CreatePrefs()
    {
        _skipCards = Core.Prefs.CreateEntry($"{Name}_SkipFoundCards", true, "PickupSkip: skip 'You Found' cards",
            "Auto-dismiss the 'You Found X' / 'You assembled X' item cards (item info text is not shown).");
        _skipShopCards = Core.Prefs.CreateEntry($"{Name}_SkipShopCards", true, "PickupSkip: skip shop cards",
            "Auto-dismiss the 'You Bought X' cards in shops.");
        _skipExtraCards = Core.Prefs.CreateEntry($"{Name}_SkipDigShopCards", false, "PickupSkip: skip dig cards",
            "Also auto-dismiss 'You Dug Up X' cards; the Dig Engine gets the same click, so dug keys/coins/gems arrive at once (less tested). Pref name kept for old configs.");
        _unfreezeKeyPref = Core.Prefs.CreateEntry($"{Name}_UnfreezeKey", "F9", "PickupSkip: emergency unfreeze key",
            "Sends the player the same 'UnFreeze' a closing card sends, in case a skip leaves you unable to move. 'None' disables.");
        if (!Enum.TryParse(_unfreezeKeyPref.Value, true, out _unfreezeKey)) _unfreezeKey = KeyCode.None;
        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        RestoreAll();
        _clicks.Clear();
        _finishes.Clear();
        _plans.Clear();
        _fsms.Clear();
        _holdClicksUntil = 0f;
    }

    private bool CatEnabled(int cat) =>
        cat == CatMain ? _skipCards.Value
        : cat == CatShop ? _skipCards.Value && _skipShopCards.Value
        : cat == CatExtra && _skipCards.Value && _skipExtraCards.Value;

    // ------------------------------------------------------------------ hooks

    private void OnBeforeEnter(FsmState state)
    {
        var ptr = state.Pointer;
        if (_pending.Count > 0 && _pending.TryGetValue(ptr, out var old)) Restore(ptr, old);

        if (!Enabled)
        {
            if (_pending.Count > 0) RestoreAll();
            _clicks.Clear();
            _finishes.Clear();
            return;
        }

        if (!_plans.TryGetValue(ptr, out var plan))
        {
            try { plan = BuildPlan(state); }
            catch (Exception e)
            {
                plan = Nothing;
                if (!_warnedPlan) Core.Log.Warning($"[{Name}] could not analyse state '{state.Name}': {e.Message}");
                _warnedPlan = true;
            }
            _plans[ptr] = plan;
        }
        if (plan == Nothing) return;
        Apply(ptr, state, plan);
    }

    private void OnAfterEnter(FsmState state)
    {
        if (!Enabled) return;
        if (!_plans.TryGetValue(state.Pointer, out var plan)) return;
        // Card click states, and (dig cards only) the Dig Engine state polling for the same click.
        bool click = plan.Click ? CatEnabled(plan.CardCat) : plan.DigClick && CatEnabled(CatExtra);
        if (!click) return;
        // Not now: we're usually nested inside the Global Manager's "X Pickup"
        // state (it just activated this card); a transition sent back to it
        // from here would cut its remaining logic actions short.
        _clicks.Add((state, state.Fsm, Time.frameCount, Time.unscaledTime + 10f));
    }

    public override void OnUpdate()
    {
        if (_unfreezeKey != KeyCode.None && Input.GetKeyDown(_unfreezeKey)) Unfreeze();

        if (_pending.Count > 0)
        {
            _tmp.Clear();
            foreach (var kv in _pending)
                if (!kv.Value.State.Active) _tmp.Add(kv.Key);
            foreach (var p in _tmp) Restore(p, _pending[p]);
        }

        var now = Time.frameCount;
        for (var i = _finishes.Count - 1; i >= 0; i--)
        {
            var (state, fsm, ev, frame) = _finishes[i];
            if (now <= frame) continue;
            _finishes.RemoveAt(i);
            var active = fsm.ActiveState;
            if (active != null && active.Pointer == state.Pointer) fsm.Event(ev);
        }

        if (_clicks.Count == 0) return;
        for (var i = _clicks.Count - 1; i >= 0; i--)
        {
            var (state, fsm, frame, giveUp) = _clicks[i];
            if (now <= frame) continue;
            if (Time.time < _holdClicksUntil && Time.unscaledTime < giveUp) continue;
            _clicks.RemoveAt(i);
            var active = fsm.ActiveState;
            if (active == null || active.Pointer != state.Pointer) continue;   // already moved on
            fsm.Event("click");
            var after = fsm.ActiveState;
            if (after != null && after.Pointer == state.Pointer && !_warnedStuck)
            {
                _warnedStuck = true;
                Core.Log.Warning($"[{Name}] card '{fsm.GameObject?.name}' state '{state.Name}' ignored \"click\"; leaving it for the player");
            }
        }
    }

    private void Unfreeze()
    {
        var player = GameObject.Find(PlayerPath);
        var fsm = FsmUtil.Get(player, "FSM");
        if (fsm == null) { Core.Log.Warning($"[{Name}] unfreeze: '{PlayerPath}' FSM not found"); return; }
        Core.Log.Msg($"[{Name}] unfreeze requested (player FSM was in '{fsm.ActiveStateName}')");
        fsm.SendEvent("UnFreeze");
    }

    // ------------------------------------------------------------------ apply / restore

    private void Apply(IntPtr ptr, FsmState state, Plan plan)
    {
        Pending pend = null;
        Pending P() => pend ??= new Pending { State = state };

        bool presentation = plan.Item || (plan.CardCat > 0 && CatEnabled(plan.CardCat));
        if (presentation)
        {
            if (plan.Snap != null)
                foreach (var tw in plan.Snap)
                    if (tw.Enabled) SnapScale(state.Fsm, tw);
            if (plan.Disable != null)
                foreach (var a in plan.Disable)
                    if (a.Enabled) { a.Enabled = false; P().Actions.Add(a); }
            if (plan.Finish != null && pend != null)
                foreach (var (a, ev) in plan.Finish)
                    if (pend.Actions.Contains(a))
                    {
                        _finishes.Add((state, state.Fsm, ev, Time.frameCount));
                        break;
                    }
            if (plan.ShortWaits != null)
                foreach (var f in plan.ShortWaits)
                    if (f.Value > CardWait) { P().Floats.Add((f, f.Value)); f.Value = CardWait; }
            if (plan.ZeroDelays != null)
                foreach (var f in plan.ZeroDelays)
                    if (f.Value > 0f) { P().Floats.Add((f, f.Value)); f.Value = 0f; }
            if (plan.Item && !_loggedItem) { _loggedItem = true; Core.Log.Msg($"[{Name}] skipping pickup animation ({state.Fsm?.GameObject?.name} / {state.Name})"); }
            if (plan.CardCat > 0 && !_loggedCard) { _loggedCard = true; Core.Log.Msg($"[{Name}] auto-dismissing card ({state.Fsm?.GameObject?.name})"); }
        }

        if (plan.FreezeZero != null)
            foreach (var (f, cat) in plan.FreezeZero)
            {
                if (f.Value <= 0f) continue;
                if (CatEnabled(cat)) { P().Floats.Add((f, f.Value)); f.Value = 0f; }
                else Hold(f.Value);
            }
        if (plan.FreezeHold != null)
            foreach (var f in plan.FreezeHold) Hold(f.Value);

        // Dig Engine: its 0.5 s Wait next to the card and its 1 s Wait before the grant (dig cards only).
        if (plan.DigWaits != null && CatEnabled(CatExtra))
            foreach (var f in plan.DigWaits)
                if (f.Value > CardWait) { P().Floats.Add((f, f.Value)); f.Value = CardWait; }

        if (pend != null) _pending[ptr] = pend;
    }

    private void Hold(float delay)
    {
        if (delay <= 0f) return;
        _holdClicksUntil = Math.Max(_holdClicksUntil, Time.time + delay + 0.15f);
    }

    private void Restore(IntPtr ptr, Pending p)
    {
        _pending.Remove(ptr);
        foreach (var a in p.Actions)
            try { a.Enabled = true; } catch { /* object gone */ }
        foreach (var (f, v) in p.Floats)
            try { f.Value = v; } catch { /* object gone */ }
    }

    private void RestoreAll()
    {
        _tmp.Clear();
        _tmp.AddRange(_pending.Keys);
        foreach (var p in _tmp) Restore(p, _pending[p]);
    }

    // iTweenScaleTo's end state: localScale = transformScale's scale (+ vectorScale), else vectorScale.
    private static void SnapScale(PmFsm fsm, iTweenScaleTo tw)
    {
        if ((int)tw.loopType != 0) return;   // looping tween: no end state
        var go = Target(fsm, tw.gameObject);
        if (go == null) return;
        var v = tw.vectorScale == null || tw.vectorScale.IsNone ? Vector3.zero : tw.vectorScale.Value;
        var from = tw.transformScale != null && !tw.transformScale.IsNone ? tw.transformScale.Value : null;
        go.transform.localScale = from != null ? from.transform.localScale + v : v;
    }

    private static GameObject Target(PmFsm fsm, FsmOwnerDefault od)
    {
        if (od == null) return null;
        if (od.OwnerOption == OwnerDefaultOption.UseOwner) return fsm?.GameObject;
        return od.GameObject?.Value;
    }

    // ------------------------------------------------------------------ analysis

    private Plan BuildPlan(FsmState state)
    {
        var fsm = state.Fsm;
        if (fsm == null) return Nothing;
        var info = GetInfo(fsm);
        var actions = state.Actions;
        if (actions == null) return Nothing;
        Plan plan = null;
        Plan P() => plan ??= new Plan { State = state };

        bool inChain = info.ItemChain != null && info.ItemChain.Contains(state.Name);
        if (inChain || info.CardCat > 0)
        {
            foreach (var a in actions)
            {
                if (a == null || !a.Enabled) continue;
                var tn = TypeName(a);
                if (info.CardCat > 0 && tn == "Wait")
                {
                    // Cards open on the very frame of the pickup click, and their
                    // first state next listens for a mouse/interact press. A disabled
                    // Wait lets the card reach that state the same frame, where the
                    // pickup click itself closes it and unfreezes the player before
                    // the pickup's own Freeze arrives: the player stays frozen. A
                    // one-frame Wait keeps the card a frame behind, like vanilla.
                    var t = a.Cast<Wait>().time;
                    if (IsConst(t) && t.Value > CardWait) (P().ShortWaits ??= new()).Add(t);
                }
                else if (IsPresentation(tn))
                {
                    (P().Disable ??= new()).Add(a);
                    var ev = FinishEventName(a);
                    if (ev != null) (P().Finish ??= new()).Add((a, ev));
                    if (inChain && tn == "iTweenScaleTo") (P().Snap ??= new()).Add(a.Cast<iTweenScaleTo>());
                }
                else if (inChain && tn == "PmtDeSpawn")
                {
                    var d = a.Cast<PmtDeSpawn>().delay;
                    if (IsConst(d) && d.Value > 0f) (P().ZeroDelays ??= new()).Add(d);
                }
                else if (inChain && tn == "SendEvent")
                {
                    var se = a.Cast<SendEvent>();
                    if (se.sendEvent?.Name == "fly" && IsConst(se.delay) && se.delay.Value > 0f)
                        (P().ZeroDelays ??= new()).Add(se.delay);
                }
            }
            if (inChain) P().Item = true;
            if (info.CardCat > 0)
            {
                P().CardCat = info.CardCat;
                foreach (var t in state.Transitions)
                    if (t?.EventName == "click") { P().Click = true; break; }
            }
        }

        // Delayed freezes (any FSM). Zero them when this state also opens a card we skip.
        List<FsmFloat> delayed = null;
        int openedCat = CatNone;
        foreach (var a in actions)
        {
            if (a == null || !a.Enabled) continue;
            var se = a.TryCast<SendEvent>();
            if (se != null)
            {
                var ev = se.sendEvent?.Name;
                if (ev != null && FreezeEvents.Contains(ev) && se.delay != null && !se.delay.IsNone && se.delay.Value > 0f)
                    (delayed ??= new()).Add(se.delay);
                continue;
            }
            var ag = a.TryCast<ActivateGameObject>();
            if (ag != null && ag.activate != null && ag.activate.Value)
            {
                var c = CardCategory(Target(fsm, ag.gameObject));
                if (c > openedCat) openedCat = c;
            }
        }
        if (delayed != null)
            foreach (var d in delayed)
            {
                if (openedCat > 0 && IsConst(d)) (P().FreezeZero ??= new()).Add((d, openedCat));
                else (P().FreezeHold ??= new()).Add(d);
            }

        // Dig Engine companion states. Roles are worked out for the whole FSM the
        // first time one of its states opens a dig card (the result state is always
        // entered before its click and Wait states).
        if (info.CardCat == CatNone && info.ItemChain == null)
        {
            if (openedCat == CatExtra && info.DigRoles == null) info.DigRoles = DigRoles(fsm);
            if (info.DigRoles != null && info.DigRoles.TryGetValue(state.Name, out var role))
            {
                if (role == DigClickState) P().DigClick = true;
                else
                    foreach (var a in actions)
                    {
                        var w = a?.TryCast<Wait>();
                        if (w != null && w.Enabled && IsConst(w.time) && w.time.Value > CardWait)
                            (P().DigWaits ??= new()).Add(w.time);
                    }
            }
        }

        return plan ?? Nothing;
    }

    // __SYSTEM/Utility/Dig Engine, FSM "FSM". For keys, coins and gems a result
    // state ("1 Key", "1 coin".."4 coins", "Gem", "2 Keys", "Gem 2", "Gem 3",
    // "3 Keys") activates the "You Dug Up" card, Waits 0.5 and goes FINISHED to a
    // state ("State 2/4/6/8/10/12/14") that polls GetMouseButtonDown / Rewired
    // any-button itself, -click-> a 1 s Wait state ("Wait", "Wait 2".."Wait 7")
    // -> the grant (Key/Gold/Gem Manager "Update"). In vanilla one physical click
    // closes the card and moves the Dig Engine on; an auto-dismissed card left the
    // Dig Engine waiting for the player's next button press, and a new dig ("Go",
    // global) in the meantime dropped the reward. Found by structure, not by name.
    private static Dictionary<string, int> DigRoles(PmFsm fsm)
    {
        var roles = new Dictionary<string, int>();
        var states = fsm.States;
        if (states == null) return roles;
        foreach (var s in states)
        {
            if (s?.Actions == null || OpensCardCat(fsm, s) != CatExtra) continue;
            var click = StateOn(fsm, s, "FINISHED");
            if (!PollsClick(click)) continue;
            roles[s.Name] = DigResult;
            roles[click.Name] = DigClickState;
            var wait = StateOn(fsm, click, "click");
            if (OnlyWaits(wait)) roles[wait.Name] = DigGrantWait;
        }
        return roles;
    }

    private static int OpensCardCat(PmFsm fsm, FsmState s)
    {
        int cat = CatNone;
        foreach (var a in s.Actions)
        {
            var ag = a?.TryCast<ActivateGameObject>();
            if (ag == null || !ag.Enabled || ag.activate == null || !ag.activate.Value) continue;
            var c = CardCategory(Target(fsm, ag.gameObject));
            if (c > cat) cat = c;
        }
        return cat;
    }

    private static FsmState StateOn(PmFsm fsm, FsmState s, string ev)
    {
        if (s?.Transitions == null) return null;
        foreach (var t in s.Transitions)
            if (t != null && t.EventName == ev && !string.IsNullOrEmpty(t.ToState)) return fsm.GetState(t.ToState);
        return null;
    }

    // Only "click" transitions, and only mouse/Rewired button polls that send it.
    private static bool PollsClick(FsmState s)
    {
        if (s?.Actions == null || s.Transitions == null || s.Transitions.Length == 0) return false;
        foreach (var t in s.Transitions)
            if (t == null || t.EventName != "click") return false;
        bool polls = false;
        foreach (var a in s.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var tn = TypeName(a);
            if (tn == "GetMouseButtonDown" || tn.StartsWith("Rewired", StringComparison.Ordinal)) polls = true;
            else return false;
        }
        return polls;
    }

    // Nothing but Wait actions, leaving on FINISHED only.
    private static bool OnlyWaits(FsmState s)
    {
        if (s?.Actions == null || s.Transitions == null || s.Transitions.Length == 0) return false;
        foreach (var t in s.Transitions)
            if (t == null || t.EventName != "FINISHED") return false;
        bool any = false;
        foreach (var a in s.Actions)
        {
            if (a == null || !a.Enabled) continue;
            if (a.TryCast<Wait>() == null) return false;
            any = true;
        }
        return any;
    }

    private FsmInfo GetInfo(PmFsm fsm)
    {
        var ptr = fsm.Pointer;
        if (_fsms.TryGetValue(ptr, out var info)) return info;
        info = new FsmInfo { Fsm = fsm };
        _fsms[ptr] = info;
        info.CardCat = CardCategory(fsm.GameObject);
        if (info.CardCat == CatNone) info.ItemChain = ItemChain(fsm);
        return info;
    }

    // Card = direct child of "You Found Text" whose name starts with a known prefix.
    private static int CardCategory(GameObject go)
    {
        if (go == null) return CatNone;
        var parent = go.transform.parent;
        if (parent == null || parent.name != CardParent) return CatNone;
        var n = go.name.TrimStart('\'', ' ');
        foreach (var p in MainCardPrefixes)
            if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return CatMain;
        foreach (var p in ExtraCardPrefixes)
            if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return CatExtra;
        foreach (var p in ShopCardPrefixes)
            if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return CatShop;
        return CatNone;
    }

    // States reached from a "click"/"fly" transition, following all transitions
    // except hover ones and stopping at idle states (those polling MousePickEvent).
    // Null unless the chain actually grants something.
    private static HashSet<string> ItemChain(PmFsm fsm)
    {
        var states = fsm.States;
        if (states == null) return null;
        var byName = new Dictionary<string, FsmState>();
        var todo = new Queue<string>();
        foreach (var s in states)
        {
            if (s == null) continue;
            byName[s.Name] = s;
            foreach (var t in s.Transitions)
                if (t != null && (t.EventName == "click" || t.EventName == "fly") && !string.IsNullOrEmpty(t.ToState))
                    todo.Enqueue(t.ToState);
        }
        if (todo.Count == 0) return null;

        var chain = new HashSet<string>();
        bool grants = false;
        while (todo.Count > 0)
        {
            var name = todo.Dequeue();
            if (chain.Contains(name) || !byName.TryGetValue(name, out var s)) continue;
            bool idle = false;
            foreach (var a in s.Actions)
            {
                if (a == null) continue;
                var tn = TypeName(a);
                if (tn == "MousePickEvent") { idle = true; break; }
                if (tn == "CameraFadeOut" || tn == "CameraFadeIn") return null;  // dig menus etc.: not a plain pickup
                if (!a.Enabled) continue;
                if (tn == "PmtDeSpawn") grants = true;
                else if (tn == "SendEvent")
                {
                    var ev = a.Cast<SendEvent>().sendEvent?.Name;
                    if (ev != null && (ev.IndexOf("pickup", StringComparison.OrdinalIgnoreCase) >= 0 || ev == "Add Coins"))
                        grants = true;
                }
            }
            if (idle) continue;
            chain.Add(name);
            foreach (var t in s.Transitions)
                if (t != null && t.EventName != "hov" && t.EventName != "hovoff" && !string.IsNullOrEmpty(t.ToState))
                    todo.Enqueue(t.ToState);
        }
        return grants && chain.Count > 0 ? chain : null;
    }

    private static bool IsPresentation(string tn) =>
        tn == "Wait" || tn.StartsWith("iTween", StringComparison.Ordinal) || tn.StartsWith("Dotween", StringComparison.Ordinal);

    // A literal parameter (not a reference to a shared FSM variable).
    private static bool IsConst(FsmFloat f) =>
        f != null && !(f.UseVariable && !string.IsNullOrEmpty(f.Name));

    private static string TypeName(FsmStateAction a) => a.GetIl2CppType().Name;

    // Wait / iTween* use "finishEvent", the Dotween* actions "finishedEvent".
    private static readonly string[] FinishFields = { "finishEvent", "finishedEvent" };

    private static string FinishEventName(FsmStateAction a)
    {
        var t = a.GetIl2CppType();
        foreach (var n in FinishFields)
        {
            var f = t.GetField(n, Il2CppSystem.Reflection.BindingFlags.Public | Il2CppSystem.Reflection.BindingFlags.NonPublic | Il2CppSystem.Reflection.BindingFlags.Instance);
            var ev = f?.GetValue(a)?.TryCast<FsmEvent>();
            if (ev != null && !string.IsNullOrEmpty(ev.Name)) return ev.Name;
        }
        return null;
    }
}
