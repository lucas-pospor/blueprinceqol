using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;

namespace BluePrinceQoL.Features;

/// <summary>
/// Shorter Dare mode "Game Over" scene (after breaking a dare).
///
/// Vanilla flow (scene Mount Holly Estate, all FSMs named "FSM"):
///   DAY/DARE ENGINE "State 5" activates Wait for Free.
///   Wait for Free "State 4"   music fades out over 3 s (Wait 3), then waits until the player is unfrozen
///                             and activates DAY/DARE ENGINE/GAME OVER.
///   GAME OVER "pre fade 7"    music "Goodbye to the Sea", Wait 2.5 -> narration (DAY/Music/gameoverVO, 21 s).
///   GAME OVER/text timing     the poem lines, timed to the narration (~23.5 s), then LETTER CAM.
///   LETTER CAM                letter close-up, Wait 8 + fade out 3, then the Game Over screen (Camera) fades in over 4 s.
///   Selector/Main Menu        "State 1" Wait 10, "State 2" fades up "return to MAIN MENU" (Fade up, 5 s) + Wait 1,
///                             "State 3" clickable. Click: "State 6" fade out 3 s (+ Master volume), "State 5" loads Main Menu.
/// About 40 s from the start of the poem until the button can be clicked.
///
/// What this feature does:
/// - Always: Main Menu button after MenuDelay instead of 10 s, its fade-up in 1 s instead of 5 s, the exit fade in
///   1 s instead of 3 s, and the music fade before the scene in 1 s instead of 3 s. Durations only, per state entry,
///   restored afterwards (same scheme as EndOfDay).
/// - Skip key (or AutoSkip): runs the rest of the scene at 10x game speed (Time.timeScale) until the Main Menu button
///   is clickable, and fades the narration out. Every game action still runs in order, just faster. Time scale is put
///   back when the button is reached, when GAME OVER goes away, on scene load, or after 15 s at the latest.
/// Nothing is saved during this scene (it ends in a scene load), so nothing is skipped that matters to the save.
/// </summary>
public class DareGameOverFeature : Feature
{
    public override string Name => "DareGameOver";
    protected override string Description =>
        "Shorter Dare mode Game Over scene: Main Menu button sooner, and a key to fast-forward the poem";

    private const string GameOverObject = "GAME OVER";
    private const string DareEngineObject = "DARE ENGINE";
    private const string MenuObject = "Main Menu";
    private const string SelectorObject = "Selector";
    private const string FadeUpObject = "Fade up";
    private const string WaitForFreeObject = "Wait for Free";
    private const string VoPath = "DAY/Music/gameoverVO";

    private const float FastFade = 1f;
    private const float MinDur = 0.05f;
    private const float SkipScale = 10f;
    private const float SkipGrace = 0.75f;    // real seconds after GAME OVER starts before a key counts
    private const float SkipTimeout = 15f;    // real seconds; the scene needs ~4 at 10x
    private const float VoFadeTime = 0.5f;    // real seconds

    private MelonPreferences_Entry<float> _menuDelay;
    private MelonPreferences_Entry<string> _skipKeysPref;
    private MelonPreferences_Entry<bool> _autoSkip;
    private readonly List<KeyCode> _skipKeys = new();

    private enum Kind
    {
        None,
        GameOverStart,  // GAME OVER "pre fade 7"
        MusicFade,      // Wait for Free "State 4"
        MenuWait,       // Main Menu "State 1"
        MenuFadeUp,     // Main Menu/Fade up "State 2"
        MenuReady,      // Main Menu "State 3"
        MenuExit,       // Main Menu "State 6"
    }

    private readonly Dictionary<IntPtr, Kind> _kinds = new();

    private sealed class Pending
    {
        public FsmState State;
        public readonly List<(FsmFloat f, float v)> Floats = new();
    }
    private readonly Dictionary<IntPtr, Pending> _pending = new();
    private readonly List<IntPtr> _tmp = new();

    private GameObject _gameOver;     // set while the scene runs
    private float _gameOverAt;        // realtime it started
    private bool _menuReached;
    private bool _skipping;
    private float _skipAt, _savedScale;
    private bool _muteVo;
    private AudioSource _vo;

    protected override void CreatePrefs()
    {
        _menuDelay = Core.Prefs.CreateEntry("DareGameOver_MenuDelay", 3f, "DareGameOver: menu delay",
            "Seconds after the Game Over screen appears before the Main Menu button shows (vanilla: 10)");
        _skipKeysPref = Core.Prefs.CreateEntry("DareGameOver_SkipKeys", "Space,Return,Escape,Mouse0",
            "DareGameOver: skip keys",
            "Keys that fast-forward the poem to the Game Over screen (UnityEngine.KeyCode names, comma separated; " +
            "Mouse0 = left click). Empty = no skip key");
        _autoSkip = Core.Prefs.CreateEntry("DareGameOver_AutoSkip", false, "DareGameOver: auto skip",
            "Always fast-forward the poem, without pressing a key");
        ParseKeys();
        _skipKeysPref.OnEntryValueChanged.Subscribe((_, _) => ParseKeys());
        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    private void ParseKeys()
    {
        _skipKeys.Clear();
        foreach (var raw in (_skipKeysPref.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse(raw.Trim(), true, out KeyCode k) && k != KeyCode.None) _skipKeys.Add(k);
            else Core.Log.Warning($"[{Name}] unknown key '{raw}' in DareGameOver_SkipKeys");
        }
    }

    public override void OnSceneLoaded(string sceneName)
    {
        StopSkip("scene load");
        RestoreAll();
        _kinds.Clear();
        _gameOver = null;
        _menuReached = false;
        _muteVo = false;
        _vo = null;
    }

    // ------------------------------------------------------------------ hooks

    private void OnBeforeEnter(FsmState state)
    {
        var ptr = state.Pointer;
        if (_pending.Count > 0)
        {
            if (_pending.TryGetValue(ptr, out var old)) Restore(ptr, old);
            if (!Enabled) RestoreAll();
        }

        if (!_kinds.TryGetValue(ptr, out var kind))
        {
            kind = Classify(state);
            _kinds[ptr] = kind;
        }
        if (kind == Kind.None || !Enabled) return;

        switch (kind)
        {
            case Kind.GameOverStart:
                _gameOver = state.Fsm?.GameObject;
                _gameOverAt = Time.realtimeSinceStartup;
                _menuReached = false;
                Core.Log.Msg($"[{Name}] Game Over scene started");
                if (_autoSkip.Value) StartSkip();
                return;
            case Kind.MenuReady:
                return;
        }

        var pend = new Pending { State = state };
        var log = new List<string>();
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            switch (kind)
            {
                case Kind.MusicFade:
                    if (a.TryCast<Wait>() is { } w) Shorten(pend, w.time, FastFade, "Wait", log);
                    else if (a.TryCast<TrackFadeToVolumeAction>() is { } tf) Shorten(pend, tf.fadeTime, FastFade, "music fade", log);
                    break;
                case Kind.MenuWait:
                    if (a.TryCast<Wait>() is { } mw) Shorten(pend, mw.time, Math.Max(_menuDelay.Value, MinDur), "Wait", log);
                    break;
                case Kind.MenuFadeUp:
                    if (a.TryCast<DotweenEaseColor>() is { } ec) Shorten(pend, ec.duration, FastFade, "fade up", log);
                    break;
                case Kind.MenuExit:
                    if (a.TryCast<CameraFadeOut>() is { } fo) Shorten(pend, fo.time, FastFade, "FadeOut", log);
                    else if (a.TryCast<DotweenAudioMixer>() is { } am) Shorten(pend, am.duration, FastFade, "Audio", log);
                    break;
            }
        }
        if (pend.Floats.Count == 0) return;
        _pending[ptr] = pend;
        Core.Log.Msg($"[{Name}] {state.Fsm?.GameObject?.name} / {state.Name}: {string.Join(", ", log)}");
    }

    private void OnAfterEnter(FsmState state)
    {
        if (!_kinds.TryGetValue(state.Pointer, out var kind) || kind != Kind.MenuReady) return;
        _menuReached = true;
        StopSkip("Main Menu button ready");
    }

    public override void OnUpdate()
    {
        if (_pending.Count > 0)
        {
            _tmp.Clear();
            foreach (var kv in _pending)
            {
                bool active;
                try { active = kv.Value.State.Active; } catch { active = false; }
                if (!active) _tmp.Add(kv.Key);
            }
            foreach (var p in _tmp) Restore(p, _pending[p]);
        }

        if (_muteVo) FadeVo();

        var running = _gameOver != null && _gameOver.activeInHierarchy;
        if (_skipping)
        {
            if (!running) StopSkip("Game Over scene ended");
            else if (Time.realtimeSinceStartup - _skipAt > SkipTimeout) StopSkip("timeout");
            return;
        }
        if (!running || _menuReached || _skipKeys.Count == 0) return;
        if (Time.realtimeSinceStartup - _gameOverAt < SkipGrace) return;
        foreach (var k in _skipKeys)
            if (Input.GetKeyDown(k)) { StartSkip(); return; }
    }

    // ------------------------------------------------------------------ skip

    private void StartSkip()
    {
        if (_skipping || _menuReached) return;
        _savedScale = Time.timeScale;
        Time.timeScale = SkipScale;
        _skipping = true;
        _skipAt = Time.realtimeSinceStartup;
        _muteVo = true;
        Core.Log.Msg($"[{Name}] fast-forwarding to the Game Over screen");
    }

    private void StopSkip(string why)
    {
        if (!_skipping) return;
        _skipping = false;
        // Only put it back if nothing else changed it meanwhile.
        if (Mathf.Approximately(Time.timeScale, SkipScale)) Time.timeScale = _savedScale;
        Core.Log.Msg($"[{Name}] fast-forward stopped ({why}) after {Time.realtimeSinceStartup - _skipAt:0.0}s");
    }

    // The narration plays on its own AudioSource; skipping the poem fades it out
    // (it would otherwise talk over the Game Over screen). The scene ends in a
    // scene load, which resets the source.
    private void FadeVo()
    {
        if (_vo == null)
        {
            var go = GameObject.Find(VoPath);
            if (go == null) return;  // not started yet: it starts 2.5 s into the scene
            _vo = go.GetComponent<AudioSource>();
            if (_vo == null) { _muteVo = false; return; }
        }
        if (!_vo.isPlaying) return;
        _vo.volume = Mathf.MoveTowards(_vo.volume, 0f, Time.unscaledDeltaTime * 0.24f / VoFadeTime);
        if (_vo.volume <= 0f) _vo.Stop();
    }

    // ------------------------------------------------------------- durations

    private static void Shorten(Pending pend, FsmFloat v, float target, string what, List<string> log)
    {
        if (!IsConst(v)) return;
        var orig = v.Value;
        if (orig <= target) return;
        pend.Floats.Add((v, orig));
        v.Value = target;
        log.Add($"{what} {orig:0.##}s -> {target:0.##}s");
    }

    private void Restore(IntPtr ptr, Pending p)
    {
        _pending.Remove(ptr);
        foreach (var (f, v) in p.Floats)
            try { f.Value = v; } catch { /* object gone */ }
    }

    private void RestoreAll()
    {
        if (_pending.Count == 0) return;
        _tmp.Clear();
        _tmp.AddRange(_pending.Keys);
        foreach (var p in _tmp) Restore(p, _pending[p]);
    }

    // ------------------------------------------------------------- classify

    private static Kind Classify(FsmState state)
    {
        var fsm = state.Fsm;
        if (fsm == null || fsm.Name != "FSM") return Kind.None;
        var go = fsm.GameObject;
        if (go == null) return Kind.None;
        var parent = go.transform.parent;
        var parentName = parent != null ? parent.name : null;
        var name = state.Name;

        switch (go.name)
        {
            case GameOverObject when parentName == DareEngineObject:
                return name == "pre fade 7" ? Kind.GameOverStart : Kind.None;
            case WaitForFreeObject when parentName == DareEngineObject:
                return name == "State 4" ? Kind.MusicFade : Kind.None;
            case MenuObject when parentName == SelectorObject:
                return name switch
                {
                    "State 1" => Kind.MenuWait,
                    "State 3" => Kind.MenuReady,
                    "State 6" => Kind.MenuExit,
                    _ => Kind.None,
                };
            case FadeUpObject when parentName == MenuObject:
                return name == "State 2" ? Kind.MenuFadeUp : Kind.None;
        }
        return Kind.None;
    }

    // A literal parameter (not a reference to a shared FSM variable).
    private static bool IsConst(FsmFloat f) =>
        f != null && !(f.UseVariable && !string.IsNullOrEmpty(f.Name));
}
