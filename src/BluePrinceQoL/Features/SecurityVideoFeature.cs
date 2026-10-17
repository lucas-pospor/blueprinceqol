using System;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;
using PmFsm = Il2CppHutongGames.PlayMaker.Fsm;

namespace BluePrinceQoL.Features;

/// <summary>
/// Skips the video the first time you enter the Security room (off by default: it's story content).
///
/// Main path: the room root's FSM (Security/"State 3", run when the room is placed) reads Global Persistent
/// "securitydrafted" and, only if it is false, switches on the two entry triggers ("State 7"). The feature sets the flag
/// right before that check, so the room is placed like any visited room: no trigger, no fade, no warp, no video.
/// Vanilla sets the same flag the moment you walk in (trigger "State 4"), so the only differences are that it is set at
/// drafting time, and the scene's extras don't happen (the "Visions of Emerald" music cue, +1 step if you had none).
/// DAY's "Security Cutscene?" (day start) only reads it on later days, when vanilla has it set too.
///
/// Fallback (the room was placed before the option was turned on):
/// Vanilla:
///   Security/_GAMEPLAY/"Enter Trigger - scene" (only active until Global Persistent "securitydrafted" is set):
///     "State 4" sets securitydrafted, freezes -> "State 5" track "Visions of Emerald", CameraFadeOut 0.7
///     -> "Step Check" (+1 step if at 0) -> "State 6" Event 2 to VIDEO CAM, QuickFreeze -> "State 8" CamWarp into the room.
///   VIDEO CAM (FSM "FSM"): global Event 2 -> "VISIONS OF EMERALD" (Skip Code on) -> "Security Vid" (plays the video,
///     Wait clip length; "cont" when done or "skipvideo" from the hold-Back Skip Code) -> "Skip Vid" -> "State 7":
///     CameraFadeIn 3, camera back, unfreeze.
/// The feature sends "skipvideo" to VIDEO CAM, the same event the game's own skip control sends, so "Skip Vid" and
/// "State 7" clean up as usual. It waits until the trigger's "State 8" (CamWarp, one frame after "State 6" starts the
/// video) has run: the warp freezes the player, and only the video's "State 7" QuickUnfreeze lets them go again, so the
/// skip must come after the warp (in vanilla it comes 90 s later). Fallback: 2 s after the video started. Around it, the trigger's fade-out (0.7 s) and
/// State 7's fade-in (3 s) are shortened for that entry. securitydrafted, the step and the warp happen as in vanilla.
/// </summary>
public class SecurityVideoFeature : Feature
{
    public override string Name => "SecurityVideo";
    protected override string Description => "Skip the video the first time you enter the Security room";
    protected override bool DefaultEnabled => false;

    private const string VideoCam = "VIDEO CAM";
    private const string VideoState = "Security Vid";
    private const string ReturnState = "State 7";
    private const string Trigger = "Enter Trigger - scene";
    private const string TriggerFadeState = "State 5";
    private const string GlobalPersistent = "Global Persitent Manager";  // sic
    private const string Flag = "securitydrafted";
    private const string TriggerWarpState = "State 8";
    private const float WarpTimeout = 2f;   // real seconds; skip anyway if the warp state is never seen
    private const float FadeOut = 0.2f, FadeIn = 0.5f;

    private readonly StateDurations _durations = new();
    private PmFsm _skipFsm;
    private FsmState _skipState;
    private int _skipFrame;
    private float _videoAt;
    private int _warpFrame = -1;   // frame the trigger's warp ran in (-1 = not yet)
    private bool _skipped;   // this video was skipped: shorten the fade-in that follows

    public override void OnSceneLoaded(string sceneName)
    {
        _durations.RestoreAll();
        _skipFsm = null;
        _skipState = null;
        _skipped = false;
    }

    protected override void CreatePrefs()
    {
        FsmHooks.BeforeEnter += OnBeforeEnter;
        FsmHooks.AfterEnter += OnAfterEnter;
    }

    private static bool IsVideoCam(FsmState s) =>
        s.Fsm?.GameObject is { } go && go.name == VideoCam && go.transform.parent == null && s.Fsm.Name == "FSM";

    private void OnBeforeEnter(FsmState state)
    {
        _durations.OnEnter(state);
        if (!Enabled) return;
        var name = state.Name;
        if (name == "State 3" && IsRoomCheck(state)) { MarkSeen(); return; }
        if (name == TriggerFadeState && state.Fsm?.GameObject is { } go && go.name == Trigger && state.Fsm.Name == "FSM")
        {
            Shorten(state, FadeOut);
            return;
        }
        if (name == ReturnState && _skipped && IsVideoCam(state))
        {
            _skipped = false;
            Shorten(state, FadeIn);
        }
    }

    // The Security room root ("Security(Clone)..." in the room pools), state "State 3": GetFsmBool securitydrafted.
    private static bool IsRoomCheck(FsmState state)
    {
        var go = state.Fsm?.GameObject;
        if (go == null || state.Fsm.Name != "FSM" || !go.name.StartsWith("Security", StringComparison.Ordinal) || state.Actions == null)
            return false;
        foreach (var a in state.Actions)
            if (a?.TryCast<GetFsmBool>() is { } g && g.variableName?.Value == Flag) return true;
        return false;
    }

    private void MarkSeen()
    {
        var flag = FsmUtil.Bool(FsmUtil.Get(GameObject.Find(GlobalPersistent)), Flag);
        if (flag == null) { Core.Log.Warning($"[{Name}] Global Persistent '{Flag}' not found; the video will be skipped instead"); return; }
        if (flag.Value) return;
        flag.Value = true;
        Core.Log.Msg($"[{Name}] Security room placed as already visited (no first-visit scene)");
    }

    private void Shorten(FsmState state, float to)
    {
        _durations.Begin(state);
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            if (a.TryCast<CameraFadeOut>() is { } fo) _durations.Scale(fo.time, 0f, to, "FadeOut");
            else if (a.TryCast<CameraFadeIn>() is { } fi) _durations.Scale(fi.time, 0f, to, "FadeIn");
        }
        var log = _durations.Commit();
        if (log != null) Core.Log.Msg($"[{Name}] {state.Fsm?.GameObject?.name} / {state.Name}: {log}");
    }

    private void OnAfterEnter(FsmState state)
    {
        if (!Enabled) return;
        if (state.Name == TriggerWarpState && state.Fsm?.GameObject is { } tg && tg.name == Trigger && state.Fsm.Name == "FSM")
        {
            _warpFrame = Time.frameCount;
            return;
        }
        if (state.Name != VideoState || !IsVideoCam(state)) return;
        // Not inside its own entry: the video player and Skip Code are being set up right now.
        _skipFsm = state.Fsm;
        _skipState = state;
        _skipFrame = Time.frameCount;
        _videoAt = Time.unscaledTime;
        if (_warpFrame >= 0 && Time.frameCount - _warpFrame > 5) _warpFrame = -1;  // an old warp, not this one
    }

    public override void OnUpdate()
    {
        _durations.Tick();
        if (_skipFsm == null || Time.frameCount <= _skipFrame) return;
        // After the warp (at least one frame later), or after the fallback timeout.
        var warped = _warpFrame >= 0 && Time.frameCount >= _warpFrame + 2;  // let the player FSM finish the warp first
        if (!warped && Time.unscaledTime - _videoAt < WarpTimeout) return;
        _warpFrame = -1;
        var fsm = _skipFsm;
        var st = _skipState;
        _skipFsm = null;
        _skipState = null;
        var active = fsm.ActiveState;
        if (active == null || st == null || active.Pointer != st.Pointer) return;  // already over
        _skipped = true;
        fsm.Event("skipvideo");
        Core.Log.Msg($"[{Name}] Security room video skipped");
    }
}
