using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;

namespace BluePrinceQoL.Features;

/// <summary>
/// Shorter black screen when assembling an item at the Workshop table.
///
/// Vanilla (room prefab "Workshop", _GAMEPLAY/WorkShop Table/ICONS TEXT/Wrench, FSM "FSM"):
///   click -> "Click": PlayRandomSound (one of four 7.08 s assembly sounds, a one-shot), iTweenPunchScale 0.2,
///            CameraFadeOut 0.5, Wait 6 (FINISHED)
///   -> "State 1": SendEvent "NEXT FRAME" to the table (WorkShop Activate: the assembled item / "You assembled" card),
///            CameraFadeIn 0.7 (FINISHED) -> "Prestate".
/// The 6 s of black only cover the sound. The feature divides the fade-out, the wait and the fade-in by Speed for that
/// state entry (restored afterwards) and fades the assembly sound out over the last part of the shorter black screen,
/// so it doesn't play on over the result. The NEXT FRAME event and everything the table does with it are unchanged.
/// </summary>
public class WorkshopFeature : Feature
{
    public override string Name => "Workshop";
    protected override string Description => "Shorter black screen while an item is assembled at the Workshop table";

    private const string WrenchObject = "Wrench";
    private const string WrenchParent = "ICONS TEXT";
    private const float MinDur = 0.05f, MaxSpeed = 10f, SoundFade = 0.5f;

    private MelonPreferences_Entry<float> _speed;
    private readonly StateDurations _durations = new();

    private enum Kind { None, Assemble, Reveal }
    private readonly Dictionary<IntPtr, Kind> _kinds = new();

    // The assembly sound of the assembly in progress.
    private readonly List<(AudioSource src, float vol)> _sounds = new();
    private float _blackEnd = -1f;   // Time.time the (shortened) black screen ends

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry("Workshop_Speed", 4f, "Workshop: speed",
            "How many times faster the black screen while assembling runs (1 = vanilla 6 s, at most 10)");
        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        _durations.RestoreAll();
        _kinds.Clear();
        _sounds.Clear();
        _blackEnd = -1f;
    }

    public override void OnUpdate()
    {
        _durations.Tick();
        FadeSounds();
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

        _durations.Begin(state);
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            if (a.TryCast<Wait>() is { } w) _durations.Scale(w.time, f, MinDur, "black");
            else if (a.TryCast<CameraFadeOut>() is { } fo) _durations.Scale(fo.time, f, MinDur, "FadeOut");
            else if (a.TryCast<CameraFadeIn>() is { } fi) _durations.Scale(fi.time, f, MinDur, "FadeIn");
        }
        var log = _durations.Commit();
        if (log != null) Core.Log.Msg($"[{Name}] {state.Name}: {log}");
    }

    // After "Click" started: find the assembly one-shot it just played (PlayRandomSound -> PlayClipAtPoint).
    private void OnAfterEnter(FsmState state)
    {
        if (!Enabled || Speed() <= 1f || KindOf(state) != Kind.Assemble) return;
        var clips = new List<IntPtr>();
        var black = 0f;
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            if (a.TryCast<PlayRandomSound>() is { } prs && prs.audioClips != null)
                foreach (var c in prs.audioClips) if (c?.Value != null) clips.Add(c.Value.Pointer);
            if (a.TryCast<Wait>() is { } w && w.time != null) black = w.time.Value;  // already shortened
        }
        foreach (var (old, _) in _sounds)
            try { if (old != null) old.Stop(); } catch { /* destroyed */ }
        _sounds.Clear();
        if (clips.Count == 0 || black <= 0f) return;
        foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>())
        {
            if (src == null || !src.isPlaying || src.clip == null || src.time > 0.5f) continue;
            if (clips.Contains(src.clip.Pointer)) _sounds.Add((src, src.volume));
        }
        _blackEnd = _sounds.Count > 0 ? Time.time + black : -1f;
    }

    // Fade the sound out over the last SoundFade seconds of the black screen (game time, like the Wait).
    private void FadeSounds()
    {
        if (_blackEnd < 0f) return;
        var k = Mathf.Clamp01((_blackEnd - Time.time) / SoundFade);
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
        _blackEnd = -1f;
    }

    private Kind KindOf(FsmState state)
    {
        var ptr = state.Pointer;
        if (_kinds.TryGetValue(ptr, out var k)) return k;
        k = Kind.None;
        var fsm = state.Fsm;
        var go = fsm?.GameObject;
        if (go != null && fsm.Name == "FSM" && go.name == WrenchObject && go.transform.parent != null &&
            go.transform.parent.name == WrenchParent && FsmHooks.PathOf(go).Contains("WorkShop Table"))
        {
            bool fadeOut = false, fadeIn = false, wait = false;
            foreach (var a in state.Actions)
            {
                if (a == null || !a.Enabled) continue;
                fadeOut |= a.TryCast<CameraFadeOut>() != null;
                fadeIn |= a.TryCast<CameraFadeIn>() != null;
                wait |= a.TryCast<Wait>() != null;
            }
            if (fadeOut && wait) k = Kind.Assemble;      // "Click"
            else if (fadeIn) k = Kind.Reveal;            // "State 1"
        }
        _kinds[ptr] = k;
        return k;
    }
}
