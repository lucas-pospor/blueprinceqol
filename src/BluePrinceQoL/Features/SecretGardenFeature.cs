using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;

namespace BluePrinceQoL.Features;

/// <summary>
/// Faster crank sequence in the Secret Garden.
///
/// Vanilla flow (room prefab "Secret Garden", all FSMs named "FSM"):
///   Crank = _GAMEPLAY/"Fountain Gear 1", "Fountain Gear 2" or SECRET LEVER/"Secret Found Gear 3"
///   (the hidden one: same states with " 2" appended).
///   "State 6"                 Camfreeze to the player (frozen from here to "Reset").
///   "Rotate Gear"             DOTween rotate 1.6 s (no finish event) + Wait 0.6.
///   "Main Camera Fade Out"    CameraFadeOut 0.3.
///   "Arriow Camera Fade In"   cut to the arrow camera, CameraFadeIn 0.3.
///   "Rotate Arrow"            Event 1 to the arrow; waits for Event 0 from the niche.
///     Arrow (L1 Secret Garden Fountain/"SH Long|Medium|Short Arrow") "Rotata by 90° - to X":
///       Global Persistent direction, iTweenRotateAdd 2 s, sets the niche ints and sends it Update.
///     Niche (Rotating Niche/Position 1/"C Rotating Niche"): arrows not aligned -> "2.7 Sec Wait";
///       aligned -> "State 7" Wait 2, then "1 second Wait" (already facing) or "Face North |East|West"
///       DOTween 3 s / "Gem Spawn" -> "Face South" DOTween 3 s (no finish event) + "Wait" 3,
///       then "1 second Wait". "State 1" sends Event 0 to the cranks.
///   "Arrow Camera Fade Out"   CameraFadeOut 0.3.
///   "Main Camera Fade In"     back to the player camera; "Reset" QuickUnfreeze.
/// Frozen for 4.2 s (arrows not aligned) up to 7.5 s (niche turns).
///
/// The feature divides all of those durations by Speed for that state entry,
/// all by the same factor. That keeps the vanilla order, in particular the
/// arrow's turn (2 s) still ends before the niche's wait + fade out (3 s), so
/// the arrow is back in its idle state before the crank can be used again.
/// All game-state writes happen on state entry and are untouched. The " 2"
/// restore states of the arrows and niche (run on load) are left alone.
/// </summary>
public class SecretGardenFeature : Feature
{
    public override string Name => "SecretGarden";
    protected override string Description =>
        "Faster crank sequence in the Secret Garden (gear, arrows and niche turn faster)";

    private const float MinDur = 0.05f;
    private const float MaxSpeed = 10f;

    private MelonPreferences_Entry<float> _speed;
    private readonly StateDurations _durations = new();

    private enum Kind
    {
        None,
        GearRotate,   // crank "Rotate Gear"
        GearFade,     // crank "Main Camera Fade Out", "Arriow Camera Fade In", "Arrow Camera Fade Out"
        ArrowTurn,    // arrow "Rotata by 90° - to X"
        NicheWait,    // niche "2.7 Sec Wait", "State 7", "Wait", "1 second Wait"
        NicheTurn,    // niche "Face North|East|South|West"
    }

    private readonly Dictionary<IntPtr, Kind> _kinds = new();

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry("SecretGarden_Speed", 4f, "SecretGarden: speed",
            "How many times faster the Secret Garden crank sequence runs (1 = vanilla, at most 10)");
        FsmHooks.BeforeEnter += OnBeforeEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        _durations.RestoreAll();
        _kinds.Clear();
    }

    public override void OnUpdate() => _durations.Tick();

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
        var f = 1f / Math.Min(s, MaxSpeed);

        _durations.Begin(state);
        foreach (var a in state.Actions)
        {
            if (a == null || !a.Enabled) continue;
            switch (kind)
            {
                case Kind.GearRotate:
                    if (a.TryCast<DotweenRotateTransformTo>() is { } gr && !IsSpeedBased(gr.isSpeedBased))
                        _durations.Scale(gr.duration, f, MinDur, "gear");
                    else if (a.TryCast<Wait>() is { } gw) _durations.Scale(gw.time, f, MinDur, "Wait");
                    break;
                case Kind.GearFade:
                    if (a.TryCast<CameraFadeOut>() is { } fo) _durations.Scale(fo.time, f, MinDur, "FadeOut");
                    else if (a.TryCast<CameraFadeIn>() is { } fi) _durations.Scale(fi.time, f, MinDur, "FadeIn");
                    break;
                case Kind.ArrowTurn:
                    // Speed-based iTweens derive their own time; leave them alone.
                    if (a.TryCast<iTweenRotateAdd>() is { } it && (it.speed == null || it.speed.IsNone || it.speed.Value <= 0f))
                        _durations.Scale(it.time, f, MinDur, "arrow");
                    break;
                case Kind.NicheWait:
                    if (a.TryCast<Wait>() is { } nw) _durations.Scale(nw.time, f, MinDur, "Wait");
                    break;
                case Kind.NicheTurn:
                    if (a.TryCast<DotweenRotateTransformTo>() is { } nr && !IsSpeedBased(nr.isSpeedBased))
                        _durations.Scale(nr.duration, f, MinDur, "niche");
                    break;
            }
        }
        var log = _durations.Commit();
        if (log != null) Core.Log.Msg($"[{Name}] {state.Fsm?.GameObject?.name} / {state.Name}: {log}");
    }

    private static bool IsSpeedBased(FsmBool b) => b != null && !b.IsNone && b.Value;

    private static Kind Classify(FsmState state)
    {
        var fsm = state.Fsm;
        if (fsm == null || fsm.Name != "FSM") return Kind.None;
        var go = fsm.GameObject;
        if (go == null) return Kind.None;
        var parent = go.transform.parent;
        var parentName = parent != null ? parent.name : null;
        var name = state.Name?.Trim();

        switch (go.name)
        {
            case "Fountain Gear 1" or "Fountain Gear 2" when parentName == "_GAMEPLAY":
            case "Secret Found Gear 3" when parentName == "SECRET LEVER":
                if (name != null && name.EndsWith(" 2")) name = name[..^2];  // the hidden crank's state names
                return name switch
                {
                    "Rotate Gear" => Kind.GearRotate,
                    "Main Camera Fade Out" or "Arriow Camera Fade In" or "Arrow Camera Fade Out" => Kind.GearFade,
                    _ => Kind.None,
                };
            case "SH Long Arrow" or "SH Medium Arrow" or "SH Short Arrow" when parentName == "L1 Secret Garden Fountain":
                // "... 2" are the restore-on-load variants.
                return name != null && name.StartsWith("Rotata by 90") && !name.EndsWith(" 2") ? Kind.ArrowTurn : Kind.None;
            case "C Rotating Niche" when parentName == "Position 1":
                return name switch
                {
                    "2.7 Sec Wait" or "State 7" or "Wait" or "1 second Wait" => Kind.NicheWait,
                    "Face North" or "Face East" or "Face South" or "Face West" => Kind.NicheTurn,
                    _ => Kind.None,
                };
        }
        return Kind.None;
    }
}
