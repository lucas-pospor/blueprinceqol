using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;

namespace BluePrinceQoL.Features;

/// <summary>
/// Faster end of day: shorter fades and waits between "Call it a day" (or
/// running out of steps) and the next morning, and a faster stat reveal in the
/// diary.
///
/// Vanilla flow (all FSMs are named "FSM"):
///   DAY "END OF DAY" ... (stats, records, StatsLogger.EndDay: all instant)
///   DAY "Reset Day"            CameraFadeOut 3.1 + Wait 3.1, SendEvent disable (Ambience) after 1 s
///   DAY "Fade In Diary"        activates END OF DAY | HISTORY; Wait 5 / any key / mouse move -> cursor on
///   HISTORY ... "70 - Array Save" (history written) ... "Fade Up"  Wait 2 (black hold)
///   HISTORY "State 15"         CameraFadeIn 8 (diary fades in)
///   Float On "State 1"         Wait 2;  "State 2": 10 EaseColor (time 2, delays 2..10) = stat rows one by one
///   Exit Sign (click / Enter)  "Reset Day": CameraFadeOut 0.5 -> sends Event 10 to DAY
///   DAY "Save to Data Array" -> "State 1": Master volume up over 2 s, CameraFadeIn 0.4, shows SAVE & CONTINUE
///   SAVE & CONTINUE (click)    "Save & Continue" / "Save & Quit": Master mute over 1 s, CameraFadeOut 0.5 / 1
///   next morning: DAY "Fade Out Loading Icon 2" CameraFadeOut 1 (optional, own pref)
///
/// The feature only shortens durations and delays of those presentation
/// actions (Wait, CameraFadeIn/Out, EaseColor, DotweenAudioMixer, one delayed
/// SendEvent). Nothing is disabled, no event is sent, no state is skipped, and
/// both input points (Exit Sign, SAVE & CONTINUE) still wait for the player.
/// Values are changed in BeforeEnter for that one state entry and put back
/// when the state is left, re-entered, or the feature is turned off.
/// Everything except the morning fade only applies after DAY entered
/// "Reset Day" in the current scene.
/// </summary>
public class EndOfDayFeature : Feature
{
    public override string Name => "EndOfDay";
    protected override string Description =>
        "Faster end-of-day fades, diary fade-in and stat reveal (save, stats and day change untouched)";

    private const string DayObject = "DAY";
    private const string HistoryObject = "END OF DAY | HISTORY";
    private const string OverlayObject = "UI OVERLAY CAM";
    private const string FloatOnObject = "Float On";
    private const string EodOnlyObject = "End of Day Only";
    private const string ExitSignObject = "Exit Sign End of Day";
    private const string SaveObject = "SAVE & CONTINUE";
    private const string PauseObject = "PAUSE";

    // Never shorten a fade or wait below this: a few frames, so a finish event
    // can never come in the frame the state started (or the frame of a click).
    private const float MinDur = 0.05f;
    // DAY "Reset Day" never below this. "Call it a day" and the zero-steps
    // ending send "despawn" to DAY and write a few Global Persistent values
    // 0.2 s later; vanilla shows the diary 3.1 s after despawn. Keeping
    // Reset Day >= 0.5 s keeps those writes ahead of the diary, as in vanilla.
    private const float ResetDayMin = 0.5f;

    private MelonPreferences_Entry<float> _speed;
    private MelonPreferences_Entry<bool> _statsAtOnce, _morningFade;

    private enum Kind
    {
        None,
        DayResetDay,     // DAY "Reset Day"
        DayFadeInDiary,  // DAY "Fade In Diary"
        DayDiaryExit,    // DAY "State 1" (after the Exit Sign)
        DayMorningFade,  // DAY "Fade Out Loading Icon 2" (next morning)
        HistoryFadeUp,   // HISTORY "Fade Up"
        HistoryFadeIn,   // HISTORY "State 15"
        FloatOnWait,     // Float On "State 1"
        FloatOnRows,     // Float On "State 2"
        ExitSignFade,    // Exit Sign "Reset Day"
        SaveFade,        // SAVE & CONTINUE "Save & Continue" / "Save & Quit"
    }

    // Per scene: FsmState pointer -> kind (None for every other state seen).
    private readonly Dictionary<IntPtr, Kind> _kinds = new();

    private sealed class Pending
    {
        public FsmState State;
        public readonly List<(FsmFloat f, float v)> Floats = new();
    }
    private readonly Dictionary<IntPtr, Pending> _pending = new();
    private readonly List<IntPtr> _tmp = new();

    private bool _armed;          // DAY entered "Reset Day" in this scene
    private bool _warnedSpeed;

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry("EndOfDay_Speed", 4f, "EndOfDay: speed",
            "How many times faster the end-of-day fades, waits and stat reveal run (1 = vanilla). " +
            "Durations never go below 0.05 s (0.5 s for the first fade to black)");
        _statsAtOnce = Core.Prefs.CreateEntry("EndOfDay_StatsAllAtOnce", false, "EndOfDay: stats all at once",
            "Diary stat rows fade in together instead of one after another");
        _morningFade = Core.Prefs.CreateEntry("EndOfDay_MorningFade", true, "EndOfDay: morning fade",
            "Also shorten the 1 s fade at the start of the next day (before the morning music)");
        FsmHooks.BeforeEnter += OnBeforeEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        RestoreAll();
        _kinds.Clear();
        _armed = false;
    }

    /// <summary>Duration factor, 1 = vanilla.</summary>
    private float Factor()
    {
        var s = _speed.Value;
        if (float.IsNaN(s) || s <= 1f) return 1f;
        return 1f / s;
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

        if (kind == Kind.DayResetDay) _armed = true;
        if (kind == Kind.DayMorningFade) { if (!_morningFade.Value) return; }
        else if (!_armed) return;

        var f = Factor();
        if (f >= 1f)
        {
            if (_speed.Value < 1f && !_warnedSpeed)
                Core.Log.Warning($"[{Name}] EndOfDay_Speed {_speed.Value} < 1; leaving the end of day vanilla");
            _warnedSpeed = true;
            return;
        }
        Apply(state, kind, f);
    }

    public override void OnUpdate()
    {
        if (_pending.Count == 0) return;
        _tmp.Clear();
        foreach (var kv in _pending)
        {
            bool active;
            try { active = kv.Value.State.Active; } catch { active = false; }
            if (!active) _tmp.Add(kv.Key);
        }
        foreach (var p in _tmp) Restore(p, _pending[p]);
    }

    // ------------------------------------------------------------------ apply

    private void Apply(FsmState state, Kind kind, float f)
    {
        var actions = state.Actions;
        if (actions == null) return;
        var pend = new Pending { State = state };
        var minDur = kind == Kind.DayResetDay ? ResetDayMin : MinDur;
        var log = new List<string>();

        foreach (var a in actions)
        {
            if (a == null || !a.Enabled) continue;

            var wait = a.TryCast<Wait>();
            if (wait != null)
            {
                if (kind is Kind.DayResetDay or Kind.DayFadeInDiary or Kind.HistoryFadeUp or Kind.FloatOnWait)
                    Scale(pend, wait.time, f, minDur, "Wait", log);
                continue;
            }

            var fadeOut = a.TryCast<CameraFadeOut>();
            if (fadeOut != null)
            {
                if (kind is Kind.DayResetDay or Kind.ExitSignFade or Kind.SaveFade or Kind.DayMorningFade)
                    Scale(pend, fadeOut.time, f, minDur, "FadeOut", log);
                continue;
            }

            var fadeIn = a.TryCast<CameraFadeIn>();
            if (fadeIn != null)
            {
                if (kind is Kind.HistoryFadeIn or Kind.DayDiaryExit)
                    Scale(pend, fadeIn.time, f, minDur, "FadeIn", log);
                continue;
            }

            var mixer = a.TryCast<DotweenAudioMixer>();
            if (mixer != null)
            {
                if (kind is Kind.DayDiaryExit or Kind.SaveFade)
                    Scale(pend, mixer.duration, f, MinDur, "Audio", log);
                continue;
            }

            var ease = a.TryCast<EaseColor>();
            if (ease != null)
            {
                if (kind != Kind.FloatOnRows) continue;
                // A speed-based ease derives its own duration; leave it alone.
                if (ease.speed != null && !ease.speed.IsNone && ease.speed.Value > 0f) continue;
                Scale(pend, ease.time, f, MinDur, null, null);
                if (_statsAtOnce.Value) SetDelay(pend, ease.delay, 0f);
                else Scale(pend, ease.delay, f, 0f, null, null);
                continue;
            }

            var se = a.TryCast<SendEvent>();
            if (se != null)
            {
                // DAY "Reset Day": 'disable' to DAY/Ambience after 1 s. Scaled with
                // the fade so it still fires before the state ends.
                if (kind == Kind.DayResetDay && se.delay != null && !se.delay.IsNone && se.delay.Value > 0f)
                    Scale(pend, se.delay, f, MinDur, $"SendEvent '{se.sendEvent?.Name}' delay", log);
            }
        }

        if (kind == Kind.FloatOnRows && pend.Floats.Count > 0)
            log.Add($"{pend.Floats.Count} stat fade values{(_statsAtOnce.Value ? " (all at once)" : "")}");

        if (pend.Floats.Count == 0) return;
        _pending[state.Pointer] = pend;
        Core.Log.Msg($"[{Name}] {state.Fsm?.GameObject?.name} / {state.Name}: {string.Join(", ", log)}");
    }

    private static void Scale(Pending pend, FsmFloat v, float f, float min, string what, List<string> log)
    {
        if (!IsConst(v)) return;
        var orig = v.Value;
        if (orig <= 0f) return;
        var target = Math.Max(orig * f, min);
        if (target >= orig) return;
        pend.Floats.Add((v, orig));
        v.Value = target;
        if (what != null) log?.Add($"{what} {orig:0.##}s -> {target:0.##}s");
    }

    private static void SetDelay(Pending pend, FsmFloat v, float target)
    {
        if (!IsConst(v)) return;
        var orig = v.Value;
        if (orig <= target) return;
        pend.Floats.Add((v, orig));
        v.Value = target;
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
        var goName = go.name;
        var parent = go.transform.parent;
        var parentName = parent != null ? parent.name : null;
        var name = state.Name;

        switch (goName)
        {
            case DayObject when parent == null:
                return name switch
                {
                    "Reset Day" => Kind.DayResetDay,
                    "Fade In Diary" => Kind.DayFadeInDiary,
                    // Generic name: only the one that fades the diary out.
                    "State 1" when Has<DotweenAudioMixer>(state) && Has<CameraFadeIn>(state) => Kind.DayDiaryExit,
                    "Fade Out Loading Icon 2" => Kind.DayMorningFade,
                    _ => Kind.None,
                };
            case HistoryObject when parentName == OverlayObject:
                return name switch
                {
                    "Fade Up" => Kind.HistoryFadeUp,
                    "State 15" => Kind.HistoryFadeIn,
                    _ => Kind.None,
                };
            case FloatOnObject when parentName == EodOnlyObject:
                return name switch
                {
                    "State 1" => Kind.FloatOnWait,
                    "State 2" => Kind.FloatOnRows,
                    _ => Kind.None,
                };
            case ExitSignObject when parentName == EodOnlyObject:
                return name == "Reset Day" ? Kind.ExitSignFade : Kind.None;
            case SaveObject when parentName == PauseObject:
                return name is "Save & Continue" or "Save & Quit" ? Kind.SaveFade : Kind.None;
        }
        return Kind.None;
    }

    private static bool Has<T>(FsmState state) where T : FsmStateAction
    {
        if (state.Actions == null) return false;
        foreach (var a in state.Actions)
            if (a != null && a.Enabled && a.TryCast<T>() != null) return true;
        return false;
    }

    // A literal parameter (not a reference to a shared FSM variable).
    private static bool IsConst(FsmFloat f) =>
        f != null && !(f.UseVariable && !string.IsNullOrEmpty(f.Name));
}
