using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;

namespace BluePrinceQoL.Features;

/// <summary>
/// Faster Rotunda turn.
///
/// Vanilla flow (room prefab "Rotunda", all FSMs named "FSM"):
///   _GAMEPLAY/Rotunda Button B/Button B: click -> "Power Check" (Global Manager "Rotunda Power")
///   -> "Click": activates _COLLIDERS/COLLIDER ROTATING, button press iTween 0.5 s
///   -> "Rotate": iTweenRotateBy _GAMEPLAY/Internal L1 a quarter turn over 10 s (FINISHED), gear Animators
///      "RTND Gear A".."G" at speed 1, two sounds
///   -> "Update Floorplan": Update to _GAMEPLAY/Tile Rotator (map tile texture) -> "Bounce Back" (gears speed 0).
///   COLLIDER ROTATING "State 1": Wait 8, then switches itself off (blocks the doorways while the room turns).
///   The player is not frozen. Without power ("Click 2") nothing turns.
///
/// The feature divides the 10 s turn and the 8 s collider wait by Speed and runs the gear animations Speed times
/// faster, for that state entry. Order and the floorplan update are unchanged.
///
/// The two turn sounds ("Rotunda Gears" 11.9 s, "rotunda ceiling gears" 10 s) are PlaySound one-shots
/// (AudioSource.PlayClipAtPoint, a temporary "One shot audio" object) and can't be sped up without a pitch shift.
/// Instead they are faded out over the last part of the turn (SoundFadeTime), so they stop when the room stops.
/// </summary>
public class RotundaFeature : Feature
{
    public override string Name => "Rotunda";
    protected override string Description => "Faster Rotunda turn";

    private const float MinDur = 0.05f;
    private const float MaxSpeed = 10f;

    private MelonPreferences_Entry<float> _speed;
    private MelonPreferences_Entry<bool> _fadeSound;
    private MelonPreferences_Entry<float> _fadeTime;

    // Turn sounds of the turn in progress, and the "Rotate" state they belong to.
    private readonly List<(AudioSource src, float vol)> _sounds = new();
    private FsmState _turnState;
    private float _turnEnd;       // Time.time the turn ends (game time: the turn's iTween runs on it)
    private bool _turnLeft;

    private readonly StateDurations _durations = new();

    private enum Kind { None, Rotate, Collider }
    private readonly Dictionary<IntPtr, Kind> _kinds = new();

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry("Rotunda_Speed", 4f, "Rotunda: speed",
            "How many times faster the Rotunda turns (1 = vanilla 10 s, at most 10)");
        _fadeSound = Core.Prefs.CreateEntry("Rotunda_FadeSound", true, "Rotunda: fade sound",
            "Fade the turning sound out as the faster turn ends (the sound is 10 s long)");
        _fadeTime = Core.Prefs.CreateEntry("Rotunda_SoundFadeTime", 0.6f, "Rotunda: sound fade time",
            "Seconds the turning sound takes to fade out; the fade ends when the room stops");
        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        _durations.RestoreAll();
        _kinds.Clear();
        _sounds.Clear();
        _turnState = null;
    }

    public override void OnUpdate()
    {
        _durations.Tick();
        FadeSounds();
    }

    private void OnBeforeEnter(FsmState state)
    {
        _durations.OnEnter(state);
        if (!Enabled) { _durations.RestoreAll(); return; }

        var ptr = state.Pointer;
        if (!_kinds.TryGetValue(ptr, out var kind))
        {
            kind = Classify(state);
            _kinds[ptr] = kind;
        }
        if (kind == Kind.None) return;

        var s = _speed.Value;
        if (float.IsNaN(s) || s <= 1f) return;
        s = Math.Min(s, MaxSpeed);
        var f = 1f / s;

        _durations.Begin(state);
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            if (kind == Kind.Rotate)
            {
                if (a.TryCast<iTweenRotateBy>() is { } rot && (rot.speed == null || rot.speed.IsNone || rot.speed.Value <= 0f))
                    _durations.Scale(rot.time, f, MinDur, "turn");
                else if (a.TryCast<SetAnimatorSpeed>() is { } an && !an.speed.IsNone && an.speed.Value > 0f)
                    _durations.Set(an.speed, an.speed.Value * s, null);
            }
            else if (a.TryCast<Wait>() is { } w)
                _durations.Scale(w.time, f, MinDur, "collider Wait");
        }
        var log = _durations.Commit();
        if (log != null) Core.Log.Msg($"[{Name}] {state.Fsm?.GameObject?.name} / {state.Name}: {log}");
    }

    // ------------------------------------------------------------------ sound

    private void OnAfterEnter(FsmState state)
    {
        if (!Enabled || !_fadeSound.Value || _speed.Value <= 1f) return;
        if (!_kinds.TryGetValue(state.Pointer, out var kind) || kind != Kind.Rotate) return;

        var clips = new List<IntPtr>();
        var turn = 0f;
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            if (a.TryCast<PlaySound>() is { } ps && ps.clip?.Value != null) clips.Add(ps.clip.Value.Pointer);
            else if (a.TryCast<iTweenRotateBy>() is { } rot && rot.time != null) turn = rot.time.Value;  // already shortened
        }
        if (clips.Count == 0 || turn <= 0f) return;

        // A new turn while the last one's sounds are still fading: stop those now.
        foreach (var (old, _) in _sounds)
            try { if (old != null) old.Stop(); } catch { /* destroyed */ }

        // The one-shots were created by this state's OnEnter, just now.
        _sounds.Clear();
        _turnEnd = Time.time + turn;
        _turnLeft = false;
        foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>())
        {
            if (src == null || !src.isPlaying || src.clip == null || src.time > 0.5f) continue;
            if (clips.Contains(src.clip.Pointer)) _sounds.Add((src, src.volume));
        }
        _turnState = _sounds.Count > 0 ? state : null;
    }

    private void FadeSounds()
    {
        if (_turnState == null) return;
        var fade = Mathf.Max(_fadeTime.Value, 0.05f);
        if (!_turnLeft)
        {
            bool active;
            try { active = _turnState.Active; } catch { active = false; }
            // The turn ended before the planned time: finish the fade from here.
            if (!active) { _turnLeft = true; _turnEnd = Mathf.Min(_turnEnd, Time.time + fade); }
        }
        var k = Mathf.Clamp01((_turnEnd - Time.time) / fade);
        if (k >= 1f) return;
        foreach (var (src, vol) in _sounds)
        {
            try
            {
                if (src == null) continue;
                if (k <= 0f) src.Stop();
                else src.volume = vol * k;
            }
            catch { /* one-shot already destroyed */ }
        }
        if (k > 0f) return;
        _sounds.Clear();
        _turnState = null;
    }

    private static Kind Classify(FsmState state)
    {
        var fsm = state.Fsm;
        if (fsm == null || fsm.Name != "FSM") return Kind.None;
        var go = fsm.GameObject;
        if (go == null) return Kind.None;
        var parent = go.transform.parent;
        var parentName = parent != null ? parent.name : null;
        return go.name switch
        {
            "Button B" when parentName == "Rotunda Button B" && state.Name == "Rotate" => Kind.Rotate,
            "COLLIDER ROTATING" when parentName == "_COLLIDERS" && state.Name == "State 1" => Kind.Collider,
            _ => Kind.None,
        };
    }
}
