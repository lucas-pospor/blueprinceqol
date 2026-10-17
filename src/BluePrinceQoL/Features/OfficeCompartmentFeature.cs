using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;

namespace BluePrinceQoL.Features;

/// <summary>
/// Faster rise of the Office's secret compartment (the bust that goes up to reveal the wall safe).
///
/// Vanilla (room prefab "Office", FSMs "FSM"):
///   _GAMEPLAY/DESK DRAWERS/.../Button Gameplay (nonstatic)/Button: click -> "State 1" button turn 0.6 s
///     -> "State 4" (sequence): SendEvent "activate" to SECRET COMPARTMENT, Wait 1.5, AudioPlay SECRET COMPARTMENT/Sound
///        ("Safe Rising Up", 5.96 s).
///   _GAMEPLAY/SECRET COMPARTMENT: "activate" -> "State 2": activates Safe B, DotweenMove up (y 3.001) over 8 s, no finish
///     event; "deactivate" -> "State 3": DotweenMove down over 7 s after 0.5 s (FINISHED) -> "State 4" hides Safe B.
/// The player is not frozen. The feature divides the move durations and delay, and the 1.5 s before the sound, by
/// Speed for that state entry, and fades the rumble out over the last half second of the faster rise (then stops it
/// and puts its volume back). The "sticky" state that puts the compartment up directly on later loads is untouched.
/// </summary>
public class OfficeCompartmentFeature : Feature
{
    public override string Name => "OfficeCompartment";
    protected override string Description => "Faster rise of the bust that reveals the Office safe";

    private const string Compartment = "SECRET COMPARTMENT";
    private const string ButtonParent = "Button Gameplay (nonstatic)";
    private const float MinDur = 0.05f, MaxSpeed = 10f, SoundFade = 0.5f;

    private MelonPreferences_Entry<float> _speed;
    private readonly StateDurations _durations = new();
    private enum Kind { None, Rise, Lower, Button }
    private readonly Dictionary<IntPtr, Kind> _kinds = new();

    private AudioSource _sound;
    private float _soundVolume, _riseEnd = -1f;

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry("OfficeCompartment_Speed", 4f, "OfficeCompartment: speed",
            "How many times faster the Office bust rises to reveal the safe (1 = vanilla 8 s, at most 10)");
        FsmHooks.BeforeEnter += OnBeforeEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        _durations.RestoreAll();
        _kinds.Clear();
        _sound = null;
        _riseEnd = -1f;
    }

    public override void OnUpdate()
    {
        _durations.Tick();
        FadeSound();
    }

    private float Speed()
    {
        var s = _speed.Value;
        return float.IsNaN(s) || s <= 1f ? 1f : Math.Min(s, MaxSpeed);
    }

    private void OnBeforeEnter(FsmState state)
    {
        _durations.OnEnter(state);
        if (!Enabled) { _durations.RestoreAll(); return; }
        var kind = KindOf(state);
        if (kind == Kind.None) return;
        var s = Speed();
        if (s <= 1f) return;
        var f = 1f / s;

        float rise = 0f;
        _durations.Begin(state);
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            if (a.TryCast<DotweenMoveTransformTo>() is { } mv && !(mv.isSpeedBased != null && !mv.isSpeedBased.IsNone && mv.isSpeedBased.Value))
            {
                _durations.Scale(mv.duration, f, MinDur, "move");
                _durations.Scale(mv.setDelay, f, 0f, null);
                if (kind == Kind.Rise && mv.duration != null) rise = mv.duration.Value;  // already shortened
            }
            else if (kind == Kind.Button && a.TryCast<Wait>() is { } w) _durations.Scale(w.time, f, MinDur, "sound delay");
        }
        var log = _durations.Commit();
        if (log != null) Core.Log.Msg($"[{Name}] {state.Name}: {log}");

        if (kind == Kind.Rise && rise > 0f)
        {
            _sound = state.Fsm.GameObject.transform.Find("Sound")?.GetComponent<AudioSource>();
            _riseEnd = Time.time + rise;
        }
    }

    // The rumble matches the vanilla 8 s rise: fade it out over the last part of the faster one, then stop it.
    private void FadeSound()
    {
        if (_riseEnd < 0f) return;
        if (_sound == null) { _riseEnd = -1f; return; }
        var k = Mathf.Clamp01((_riseEnd - Time.time) / SoundFade);
        if (k >= 1f) return;
        if (_soundVolume <= 0f) _soundVolume = _sound.volume;
        if (k > 0f) { _sound.volume = _soundVolume * k; return; }
        _sound.Stop();
        _sound.volume = _soundVolume;
        _soundVolume = 0f;
        _riseEnd = -1f;
    }

    private Kind KindOf(FsmState state)
    {
        var ptr = state.Pointer;
        if (_kinds.TryGetValue(ptr, out var k)) return k;
        k = Kind.None;
        var fsm = state.Fsm;
        var go = fsm?.GameObject;
        if (go != null && fsm.Name == "FSM" && FsmHooks.PathOf(go).Contains("Office", StringComparison.Ordinal))  // rooms live in the pools as "Office(Clone)..."
        {
            var parent = go.transform.parent;
            if (go.name == Compartment)
                k = state.Name switch { "State 2" => Kind.Rise, "State 3" => Kind.Lower, _ => Kind.None };
            else if (go.name == "Button" && parent != null && parent.name == ButtonParent && state.Name == "State 4" &&
                     state.IsSequence)
                k = Kind.Button;
        }
        _kinds[ptr] = k;
        return k;
    }
}
