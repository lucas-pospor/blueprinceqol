using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BluePrinceQoL.Util;
using Il2Cpp;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;
using PmFsm = Il2CppHutongGames.PlayMaker.Fsm;

namespace BluePrinceQoL.Features;

/// <summary>
/// Faster computer terminals (Security, Office, Laboratory, Bomb Shelter,
/// Blackbridge, upgrade disks, remote access ...).
///
/// All terminals are one shared UI in the main scene:
/// "UI OVERLAY CAM/Computer System" with one child object per screen
/// ("SECURITY 1", "SECURITY - MAIN MENU", "Network Menu", ...). The object is
/// active exactly while a terminal is open.
///
/// * Text: each line is a StringTypewriter action typing into a string
///   variable that an everyFrame SetProperty copies into a TextMeshPro. Before
///   such a state starts we switch the typewriter off for that entry, put the
///   whole text into the result variable, and send the typewriter's finish
///   event one frame later, so the flow after it is unchanged.
/// * Waits: "Clear Em All" pauses, the 1.2s input lock-out of every
///   "selection arrow" menu and the pauses between lines become one frame;
///   pure pauses (e.g. "PASSWORD ACCEPTED", 11s "connecting") are capped at
///   MaxPause seconds; looping animations (blinking arrow, "...") are left alone.
/// * Keys: menus already read Up/Down/W/S/Enter themselves (PlayMaker GetKeyDown
///   actions). We add 1-9 (pick the Nth option), Escape/Backspace (pick the
///   menu's Back/Return/Exit option), keypad Enter, and "1. " prefixes on the
///   option labels. Everything is done by sending the same FSM events the
///   game's own GetKeyDown actions would send.
/// </summary>
public class TerminalFeature : Feature
{
    public override string Name => "Terminal";
    protected override string Description =>
        "Instant terminal text, no artificial waits, number keys / Escape / Backspace in terminal menus";

    private const string OverlayName = "UI OVERLAY CAM";
    private const string RootName = "Computer System";
    private const float InstantWait = 0.0001f; // > 0 so Wait finishes on its next update, after the state's other actions

    private MelonPreferences_Entry<bool> _instantText, _fastWaits, _numberKeys, _showNumbers, _backKeys, _escCloses;
    private MelonPreferences_Entry<float> _maxPause;

    private GameObject _root;
    private Transform _rootT;
    private float _nextFindAt;
    private int _findAttempts;
    private bool _warnedRoot, _loggedOpen;

    // Per-scene caches (the scene and every FSM in it are rebuilt each day).
    private readonly Dictionary<IntPtr, float> _waitOrig = new();
    private readonly Dictionary<IntPtr, WaitKind> _waitKind = new();
    private readonly Dictionary<IntPtr, Menu> _menus = new(); // PmFsm ptr -> menu (null = not a menu)
    private readonly Dictionary<IntPtr, string[]> _labels = new(); // PmFsm ptr * chain length -> labels

    // Typewriters switched off in BeforeEnter, switched back on in AfterEnter.
    private readonly Dictionary<IntPtr, List<(StringTypewriter tw, string text, GameObject owner)>> _disabled = new();
    // Typewriter finish events to send next frame.
    private readonly List<Pending> _pending = new();

    private enum WaitKind { Leave, Instant, Pause }

    private struct Pending
    {
        public PmFsm Fsm;
        public IntPtr State;
        public FsmEvent Event;
        public int Frame;
    }

    private sealed class Menu
    {
        public PmFsm Fsm;
        public PlayMakerFSM Component;
        public readonly Dictionary<string, FsmState> Options = new();
        public readonly List<List<string>> Chains = new();
        public bool Grid; // left/right go somewhere else than up/down: no number keys
    }

    private static readonly Regex BackName = new(@"\b(back|return|cancel)\b|main menu", RegexOptions.IgnoreCase);
    private static readonly Regex ExitName = new(@"\b(exit|log ?out)\b", RegexOptions.IgnoreCase);
    private static readonly Regex MenuName = new(@"\bmenu\b", RegexOptions.IgnoreCase);
    private static readonly Regex ExitEvent = new(@"^(exit|back|return|evac)$", RegexOptions.IgnoreCase);
    private static readonly Regex Numbered = new(@"^\s*\d+[.)]");
    private static readonly Regex CustomTags = new(@"<[ps]=[^>]*>");

    private static readonly KeyCode[] DigitKeys =
    {
        KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5,
        KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
    };
    private static readonly KeyCode[] PadKeys =
    {
        KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5,
        KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9,
    };

    protected override void CreatePrefs()
    {
        var c = Core.Prefs;
        _instantText = c.CreateEntry($"{Name}_InstantText", true, $"{Name}: instant text",
            "Terminal text appears at once instead of being typed out");
        _fastWaits = c.CreateEntry($"{Name}_FastWaits", true, $"{Name}: skip waits",
            "Remove the pauses between lines and the menu input lock-out; cap other pauses at MaxPause");
        _maxPause = c.CreateEntry($"{Name}_MaxPause", 0.5f, $"{Name}: max pause (s)",
            "Longest 'reading' pause kept (e.g. PASSWORD ACCEPTED, connecting...). 0 = none");
        _numberKeys = c.CreateEntry($"{Name}_NumberKeys", true, $"{Name}: number keys",
            "1-9 select the Nth option of a terminal menu");
        _showNumbers = c.CreateEntry($"{Name}_ShowNumbers", true, $"{Name}: show numbers",
            "Prefix typed menu option labels with '1. ', '2. ' ... (needs InstantText)");
        _backKeys = c.CreateEntry($"{Name}_BackKeys", true, $"{Name}: Escape/Backspace go back",
            "Escape or Backspace picks the menu's Back/Return/Exit option");
        _escCloses = c.CreateEntry($"{Name}_EscapeClosesTerminal", true, $"{Name}: Escape on start screen closes",
            "Escape on a terminal's 'press [enter]' start screen closes the terminal");

        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    private string _sceneName = "";

    public override void OnSceneLoaded(string sceneName)
    {
        _sceneName = sceneName ?? "";
        _root = null;
        _rootT = null;
        _nextFindAt = 0f;
        _findAttempts = 0;
        _waitOrig.Clear();
        _waitKind.Clear();
        _menus.Clear();
        _labels.Clear();
        _disabled.Clear();
        _pending.Clear();
    }

    // ------------------------------------------------------------------ root

    private bool IsOpen => _root != null && _root.activeInHierarchy;

    private void TryFindRoot()
    {
        if (_root != null || Time.unscaledTime < _nextFindAt) return;
        _nextFindAt = Time.unscaledTime + 2f;
        var overlay = GameObject.Find(OverlayName);
        var t = overlay != null ? overlay.transform.Find(RootName) : null;
        if (t != null)
        {
            SetRoot(t.gameObject);
            return;
        }
        // The overlay only exists in the estate scene; complain once if it never shows up there.
        if (overlay != null && _sceneName != "Main Menu" && ++_findAttempts >= 5 && !_warnedRoot)
        {
            _warnedRoot = true;
            Core.Log.Warning($"[{Name}] '{OverlayName}/{RootName}' not found; terminal tweaks inactive");
        }
    }

    private void SetRoot(GameObject go)
    {
        _root = go;
        _rootT = go.transform;
    }

    private bool InTerminal(GameObject go) =>
        go != null && _rootT != null && go.transform.IsChildOf(_rootT);

    // ------------------------------------------------------------ FSM hooks

    private void OnBeforeEnter(FsmState state)
    {
        if (_root == null)
        {
            // Fallback discovery: the root FSM enters a state whenever a terminal opens.
            var g = state.Fsm?.GameObject;
            if (g == null || g.name != RootName) return;
            var p = g.transform.parent;
            if (p == null || p.name != OverlayName) return;
            SetRoot(g);
        }
        if (!IsOpen) return;
        var fsm = state.Fsm;
        var owner = fsm?.GameObject;
        if (!InTerminal(owner)) return;
        var actions = state.Actions;
        if (actions == null) return;

        foreach (var a in actions)
        {
            if (a == null || !a.Enabled) continue;
            var tw = a.TryCast<StringTypewriter>();
            if (tw != null)
            {
                if (Enabled && _instantText.Value) InstantType(state, fsm, owner, tw);
                continue;
            }
            var w = a.TryCast<Wait>();
            if (w != null) AdjustWait(state, w);
        }
    }

    private void OnAfterEnter(FsmState state)
    {
        if (_disabled.Count == 0) return;
        if (!_disabled.TryGetValue(state.Pointer, out var list)) return;
        _disabled.Remove(state.Pointer);
        foreach (var (tw, text, owner) in list)
        {
            if (tw == null) continue;
            // The state's action list is built by now; turning the typewriter back on
            // only affects the next entry of this state.
            tw.Enabled = true;

            // Text from a variable that an earlier action of this state just set
            // (e.g. GetFsmString -> typewriter $Month String): fill in the fresh
            // value and give the everyFrame SetProperty one frame to copy it.
            var fresh = FullText(tw, owner);
            if (fresh == text || tw.resultString == null) continue;
            tw.resultString.Value = fresh;
            for (var i = 0; i < _pending.Count; i++)
                if (_pending[i].State == state.Pointer)
                {
                    var p = _pending[i];
                    p.Frame = Time.frameCount + 1;
                    _pending[i] = p;
                }
        }
    }

    private string FullText(StringTypewriter tw, GameObject owner)
    {
        var text = tw.baseString?.Value ?? "";
        if (tw.richText) text = CustomTags.Replace(text, "");
        if (_showNumbers.Value) text = NumberLabels(owner, text);
        return text;
    }

    private void InstantType(FsmState state, PmFsm fsm, GameObject owner, StringTypewriter tw)
    {
        var result = tw.resultString;
        if (tw.baseString == null || result == null) return;

        var text = FullText(tw, owner);
        result.Value = text;

        tw.Enabled = false;
        if (!_disabled.TryGetValue(state.Pointer, out var list))
            _disabled[state.Pointer] = list = new List<(StringTypewriter, string, GameObject)>();
        list.Add((tw, text, owner));

        var ev = tw.finishEvent;
        if (ev != null && !string.IsNullOrEmpty(ev.Name))
            _pending.Add(new Pending { Fsm = fsm, State = state.Pointer, Event = ev, Frame = Time.frameCount });
    }

    // --------------------------------------------------------------- waits

    private void AdjustWait(FsmState state, Wait w)
    {
        var t = w.time;
        if (t == null || t.UseVariable) return; // e.g. backspace auto-repeat, camera timings
        var key = w.Pointer;
        if (!_waitOrig.TryGetValue(key, out var orig))
            _waitOrig[key] = orig = t.Value;

        var target = orig;
        if (Enabled && _fastWaits.Value)
        {
            if (!_waitKind.TryGetValue(key, out var kind))
                _waitKind[key] = kind = ClassifyWait(state, w);
            if (kind == WaitKind.Instant) target = Math.Min(orig, InstantWait);
            else if (kind == WaitKind.Pause) target = Math.Min(orig, Math.Max(InstantWait, _maxPause.Value));
        }
        if (t.Value != target) t.Value = target;
    }

    private static WaitKind ClassifyWait(FsmState state, Wait w)
    {
        if (state.Name == "Clear Em All") return WaitKind.Instant;
        if (NextState(state) == null) return WaitKind.Leave; // nothing waits on it
        if (IsLoop(state)) return WaitKind.Leave; // blinking cursor, "..." animations

        var pure = true;
        var textOnly = true;
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled || a.Pointer == w.Pointer) continue;
            if (a.TryCast<PlaySound>() != null) continue;
            pure = false;
            if (a.TryCast<StringTypewriter>() != null || a.TryCast<setTextmeshProText>() != null ||
                a.TryCast<SetPosition>() != null || a.TryCast<SetStringValue>() != null)
                continue;
            var sp = a.TryCast<SetProperty>();
            if (sp != null && sp.targetProperty?.PropertyName == "text") continue;
            textOnly = false;
        }
        if (pure) return WaitKind.Pause;        // a screen held up for reading
        return textOnly ? WaitKind.Instant      // pause between lines / menu input lock-out
                        : WaitKind.Pause;       // something else happens: keep a short beat
    }

    // Where a state goes when its Wait (or the state) finishes.
    private static FsmState NextState(FsmState s)
    {
        string ev = "FINISHED";
        foreach (var a in s.Actions)
        {
            var w = a?.TryCast<Wait>();
            if (w?.finishEvent != null && !string.IsNullOrEmpty(w.finishEvent.Name)) { ev = w.finishEvent.Name; break; }
        }
        var to = Target(s, ev);
        return to == null ? null : s.Fsm?.GetState(to);
    }

    private static bool IsLoop(FsmState start)
    {
        var s = start;
        for (var i = 0; i < 8; i++)
        {
            s = NextState(s);
            if (s == null) return false;
            if (s.Pointer == start.Pointer) return true;
        }
        return false;
    }

    // --------------------------------------------------------------- update

    public override void OnUpdate()
    {
        TryFindRoot();
        if (!IsOpen)
        {
            _pending.Clear();
            _loggedOpen = false;
            return;
        }
        if (!_loggedOpen)
        {
            _loggedOpen = true;
            Core.Log.Msg($"[{Name}] terminal open");
        }

        FlushPending();

        if (Time.timeScale == 0f) return; // pause menu over the terminal

        var digit = PressedDigit();
        var esc = _backKeys.Value && Input.GetKeyDown(KeyCode.Escape);
        var bksp = _backKeys.Value && Input.GetKeyDown(KeyCode.Backspace);
        var padEnter = Input.GetKeyDown(KeyCode.KeypadEnter);
        if (digit < 0 && !esc && !bksp && !padEnter) return;

        var scan = Scan();
        if (scan.TextEntry) return; // password / order fields read keys themselves

        if (padEnter) FireKeyEverywhere(scan, KeyCode.Return);
        if (digit >= 0 && _numberKeys.Value) SelectNumber(scan, digit);
        if (esc && !scan.NativeEscape) GoBack(scan, true);
        else if (bksp && !scan.NativeBackspace) GoBack(scan, false);
    }

    private void FlushPending()
    {
        if (_pending.Count == 0) return;
        var now = Time.frameCount;
        for (var i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            if (p.Frame >= now) continue;
            _pending.RemoveAt(i--);
            var active = p.Fsm?.ActiveState;
            if (active == null || active.Pointer != p.State) continue; // moved on already
            p.Fsm.Event(p.Event);
        }
    }

    private int PressedDigit()
    {
        if (!_numberKeys.Value) return -1;
        for (var i = 0; i < DigitKeys.Length; i++)
            if (Input.GetKeyDown(DigitKeys[i]) || Input.GetKeyDown(PadKeys[i])) return i;
        return -1;
    }

    // ----------------------------------------------------------------- scan

    private sealed class ScanResult
    {
        public readonly List<PlayMakerFSM> Listeners = new(); // active state reads keys
        public readonly List<Menu> Menus = new();             // ... and is a menu option
        public bool TextEntry, NativeEscape, NativeBackspace;
    }

    private ScanResult Scan()
    {
        var r = new ScanResult();
        foreach (var pm in _root.GetComponentsInChildren<PlayMakerFSM>(false))
        {
            if (pm == null || !pm.enabled) continue;
            var fsm = pm.Fsm;
            var st = fsm?.ActiveState;
            if (st == null) continue;
            var listens = false;
            foreach (var a in st.Actions)
            {
                if (a == null || !a.Enabled) continue;
                if (a.TryCast<AnyKeyStoreString>() != null || a.TryCast<AnyKey>() != null) { r.TextEntry = true; continue; }
                KeyCode k;
                var kd = a.TryCast<GetKeyDown>();
                if (kd != null) { k = kd.key; listens = true; }
                else
                {
                    var ku = a.TryCast<GetKeyUp>();
                    if (ku != null) k = ku.key;
                    else
                    {
                        var kh = a.TryCast<GetKey>();
                        if (kh == null) continue;
                        k = kh.key;
                    }
                }
                if (k == KeyCode.Escape) r.NativeEscape = true;
                if (k == KeyCode.Backspace) r.NativeBackspace = true;
            }
            if (!listens) continue;
            r.Listeners.Add(pm);
            var menu = GetMenu(pm);
            if (menu != null && menu.Options.ContainsKey(st.Name)) r.Menus.Add(menu);
        }
        return r;
    }

    // ---------------------------------------------------------------- menus

    private Menu GetMenu(PlayMakerFSM pm)
    {
        var fsm = pm.Fsm;
        if (fsm == null) return null;
        if (_menus.TryGetValue(fsm.Pointer, out var m)) return m;
        m = null;
        try { m = Analyze(pm, fsm); }
        catch (Exception e) { Core.Log.Warning($"[{Name}] could not read menu FSM on '{pm.gameObject.name}': {e.Message}"); }
        _menus[fsm.Pointer] = m;
        return m;
    }

    // Menu = FSM whose states each read Return (and usually Up/Down/W/S),
    // e.g. ".../SECURITY - MAIN MENU/selection arrow": Button 1..N.
    private static Menu Analyze(PlayMakerFSM pm, PmFsm fsm)
    {
        var m = new Menu { Fsm = fsm, Component = pm };
        foreach (var s in fsm.States)
            if (s != null && KeyEvent(s, KeyCode.Return) != null) m.Options[s.Name] = s;
        if (m.Options.Count < 2) return null;

        var seen = new HashSet<string>();
        foreach (var name in m.Options.Keys)
        {
            if (seen.Contains(name)) continue;
            var top = name;
            var visited = new HashSet<string> { top };
            while (true)
            {
                var up = Step(m, top, KeyCode.UpArrow, KeyCode.W);
                if (up == null || !visited.Add(up)) break;
                top = up;
            }
            var chain = new List<string> { top };
            while (true)
            {
                var down = Step(m, chain[^1], KeyCode.DownArrow, KeyCode.S);
                if (down == null || chain.Contains(down)) break;
                chain.Add(down);
            }
            foreach (var c in chain) seen.Add(c);
            m.Chains.Add(chain);
        }

        foreach (var (name, s) in m.Options)
        {
            var up = Step(m, name, KeyCode.UpArrow, KeyCode.W);
            var down = Step(m, name, KeyCode.DownArrow, KeyCode.S);
            foreach (var k in new[] { KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.A, KeyCode.D })
            {
                var ev = KeyEvent(s, k);
                if (ev == null) continue;
                var to = Target(s, ev.Name);
                if (to != null && to != up && to != down) m.Grid = true;
            }
        }

        // Several Return-reading states with no up/down/left/right between them
        // is not a menu (e.g. the "press [enter]" start screens).
        var linked = m.Grid;
        foreach (var c in m.Chains) linked |= c.Count >= 2;
        return linked ? m : null;
    }

    private static string Step(Menu m, string from, KeyCode k1, KeyCode k2)
    {
        var s = m.Options[from];
        foreach (var k in new[] { k1, k2 })
        {
            var ev = KeyEvent(s, k);
            if (ev == null) continue;
            var to = Target(s, ev.Name);
            if (to != null && m.Options.ContainsKey(to)) return to;
        }
        return null;
    }

    private static FsmEvent KeyEvent(FsmState s, KeyCode key)
    {
        var actions = s.Actions;
        if (actions == null) return null;
        foreach (var a in actions)
        {
            if (a == null || !a.Enabled) continue;
            var g = a.TryCast<GetKeyDown>();
            if (g != null && g.key == key && g.sendEvent != null && !string.IsNullOrEmpty(g.sendEvent.Name))
                return g.sendEvent;
        }
        return null;
    }

    private static string Target(FsmState s, string eventName)
    {
        var trs = s.Transitions;
        if (trs == null) return null;
        foreach (var t in trs)
            if (t != null && t.EventName == eventName) return t.ToState;
        return null;
    }

    private static List<string> ChainOf(Menu m, string state)
    {
        foreach (var c in m.Chains)
            if (c.Contains(state)) return c;
        return null;
    }

    // Enter an option state, then send what its own Return key action sends.
    private void Select(Menu m, string stateName)
    {
        var fsm = m.Fsm;
        if (fsm.ActiveStateName != stateName) fsm.SetState(stateName);
        var st = fsm.ActiveState;
        if (st == null || st.Name != stateName) return;
        var ev = KeyEvent(st, KeyCode.Return);
        if (ev != null) fsm.Event(ev);
    }

    private void SelectNumber(ScanResult scan, int index)
    {
        foreach (var m in scan.Menus)
        {
            if (m.Grid) continue;
            var chain = ChainOf(m, m.Fsm.ActiveStateName);
            if (chain == null || chain.Count < 2) continue;
            if (index < chain.Count) Select(m, chain[index]);
            return;
        }
    }

    private void GoBack(ScanResult scan, bool escape)
    {
        foreach (var m in scan.Menus)
        {
            var target = BackOption(m);
            if (target == null) continue;
            Select(m, target);
            return;
        }

        if (scan.Menus.Count > 0) return;

        // "press [enter] to return" style screens: Return already means back.
        PlayMakerFSM startScreen = null;
        foreach (var pm in scan.Listeners)
        {
            var st = pm.Fsm.ActiveState;
            var ret = KeyEvent(st, KeyCode.Return);
            if (ret == null) continue;
            if (ExitEvent.IsMatch(ret.Name) || pm.gameObject.name.IndexOf("exit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                pm.Fsm.Event(ret);
                return;
            }
            if (pm.transform.parent == _rootT && OnlyReturn(st)) startScreen = pm;
        }

        // Terminal start screen ("Welcome to Mt. HOLLY SECURITY / press [enter]"):
        // close the terminal the same way the menus' Exit option does.
        if (escape && startScreen != null && _escCloses.Value)
        {
            var rootFsm = FsmUtil.Get(_root);
            if (rootFsm == null)
            {
                Core.Log.Warning($"[{Name}] no FSM on '{RootName}'; cannot close terminal");
                return;
            }
            rootFsm.SendEvent("deactivate");
        }
    }

    private static bool OnlyReturn(FsmState s)
    {
        foreach (var a in s.Actions)
        {
            if (a == null || !a.Enabled) continue;
            var g = a.TryCast<GetKeyDown>();
            if (g != null && g.key != KeyCode.Return) return false;
        }
        return true;
    }

    // The option Escape/Backspace picks. Checked in order: option state names
    // ("RETURN", "Return To Help Desk", "Cancel"), the state Enter leads to
    // ("Return", "Back 2", "go back", "Return to Main Menu"), the typed labels
    // ("Back", "Return to Network Menu"); then the same for Exit; then an
    // Enter target containing "Menu" ("Network Menu"); then, for two-option
    // yes/no screens only, the bottom option.
    private string BackOption(Menu m)
    {
        var current = m.Fsm.ActiveStateName;
        var chain = m.Grid ? null : ChainOf(m, current);
        var pool = new List<string>(chain ?? (IEnumerable<string>)m.Options.Keys);
        var targets = new List<string>(pool.Count);
        foreach (var n in pool) targets.Add(EnterTarget(m, n) ?? "");
        var labels = chain != null && chain.Count >= 2 ? Labels(m, chain.Count) : null;

        foreach (var re in new[] { BackName, ExitName })
        {
            for (var i = 0; i < pool.Count; i++)
                if (re.IsMatch(pool[i])) return pool[i];
            for (var i = 0; i < pool.Count; i++)
                if (re.IsMatch(targets[i])) return pool[i];
            if (labels != null)
                for (var i = labels.Length - 1; i >= 0; i--)
                    if (re.IsMatch(labels[i])) return chain[i];
        }
        for (var i = pool.Count - 1; i >= 0; i--)
            if (MenuName.IsMatch(targets[i])) return pool[i];

        if (chain != null && chain.Count == 2) return chain[1];
        return null;
    }

    private static string EnterTarget(Menu m, string option)
    {
        var s = m.Options[option];
        var ev = KeyEvent(s, KeyCode.Return);
        return ev == null ? null : Target(s, ev.Name);
    }

    // -------------------------------------------------------------- labels

    private Transform ScreenOf(GameObject go)
    {
        if (go == null || _rootT == null) return null;
        var t = go.transform;
        while (t != null && t.parent != _rootT) t = t.parent;
        return t;
    }

    private string[] Labels(Menu m, int count)
    {
        var key = m.Fsm.Pointer + count;
        if (_labels.TryGetValue(key, out var cached)) return cached;
        string[] found = null;
        var screen = ScreenOf(m.Component.gameObject);
        if (screen != null)
        {
            foreach (var pm in screen.GetComponentsInChildren<PlayMakerFSM>(false))
            {
                var states = pm?.Fsm?.States;
                if (states == null) continue;
                foreach (var s in states)
                {
                    foreach (var a in s.Actions)
                    {
                        var tw = a?.TryCast<StringTypewriter>();
                        var lines = MenuLines(tw?.baseString?.Value, false, out _);
                        if (lines != null && lines.Count == count) { found = lines.ToArray(); break; }
                    }
                    if (found != null) break;
                }
                if (found != null) break;
            }
        }
        _labels[key] = found;
        return found;
    }

    // Menu option text: 2-9 short lines separated by blank lines, e.g.
    // "View Estate Inventory\n\nKeycard System\n\nLogin to Network\n\nExit".
    private static List<string> MenuLines(string text, bool needBlankLines, out List<int> lineIndex)
    {
        lineIndex = null;
        if (string.IsNullOrEmpty(text)) return null;
        var lines = text.Replace("\r", "").Split('\n');
        var labels = new List<string>();
        var idx = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0) continue;
            if (l.Length > 40 || l.StartsWith("<") || Numbered.IsMatch(l)) return null;
            if (needBlankLines && idx.Count > 0 && i - idx[^1] < 2) return null; // option lists have a blank line between options
            labels.Add(l);
            idx.Add(i);
        }
        if (labels.Count < 2 || labels.Count > 9) return null;
        lineIndex = idx;
        return labels;
    }

    private string NumberLabels(GameObject owner, string text)
    {
        var labels = MenuLines(text, true, out var idx);
        if (labels == null) return text;
        var screen = ScreenOf(owner);
        if (screen == null) return text;

        var match = false;
        foreach (var pm in screen.GetComponentsInChildren<PlayMakerFSM>(false))
        {
            if (pm == null) continue;
            var m = GetMenu(pm);
            if (m == null || m.Grid) continue;
            foreach (var c in m.Chains)
                if (c.Count == labels.Count) { match = true; break; }
            if (match) break;
        }
        if (!match) return text;

        var lines = text.Replace("\r", "").Split('\n');
        for (var n = 0; n < idx.Count; n++)
        {
            var l = lines[idx[n]];
            var lead = l.Length - l.TrimStart().Length;
            lines[idx[n]] = l.Substring(0, lead) + (n + 1) + ". " + l.Substring(lead);
        }
        return string.Join("\n", lines);
    }

    // Keypad Enter = Return for every FSM currently reading Return.
    private static void FireKeyEverywhere(ScanResult scan, KeyCode key)
    {
        foreach (var pm in scan.Listeners)
        {
            var st = pm.Fsm.ActiveState;
            if (st == null) continue;
            var ev = KeyEvent(st, key);
            if (ev != null) pm.Fsm.Event(ev);
        }
    }
}
