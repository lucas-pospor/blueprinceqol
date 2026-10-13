using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using Il2CppHutongGames.PlayMaker.Actions;
using MelonLoader;
using UnityEngine;

namespace BluePrinceQoL.Features;

/// <summary>
/// Shortens the logo slates shown at every game launch (~20 s).
///
/// Main Menu scene, FSM "FSM" on "Menu System":
///   State 3 (Wait 2.1) -> CheckIfGameIntent -> EnterMainMenu -> Logo Slates (Wait 3.7)
///   -> State 6 (5.9) -> State 7 (4) -> State 8 (3) -> Activate Main Menu (fade 3 + delay 1).
/// Returning to the menu from a game enters at State 8.
///
/// Only Wait times and DotweenEaseColor (fade) durations/delays in those states
/// are shortened; every action still runs and every transition still comes from
/// the game's own Wait/fade, so the menu activation, session start and scene
/// loading checks are unchanged. FsmHooks aren't installed yet at boot, so the
/// actions are edited directly when the scene is initialised (before the FSM
/// leaves State 1). The Menu System is rebuilt with the scene, so nothing needs
/// restoring.
/// </summary>
public class BootSkipFeature : Feature
{
    public override string Name => "BootSkip";
    protected override string Description => "Shorten the logo slates at game launch";

    private const string SceneName = "Main Menu";
    private const string MenuSystem = "Menu System";
    private static readonly HashSet<string> SlateStates = new() { "State 3", "Logo Slates", "State 6", "State 7", "State 8" };
    private const string ActivateState = "Activate Main Menu";

    private const float SlateTime = 0.05f;   // waits and fades between logos
    private MelonPreferences_Entry<float> _menuFade;

    protected override void CreatePrefs()
    {
        _menuFade = Core.Prefs.CreateEntry($"{Name}_MenuFadeIn", 0.5f, "BootSkip: main menu fade-in (s)",
            "Fade-in of the main menu after the logos (vanilla: 1 s delay + 3 s fade).");
    }

    public override void OnSceneLoaded(string sceneName)
    {
        if (!Enabled || sceneName != SceneName) return;

        var fsm = FsmUtil.Find(MenuSystem);
        if (fsm == null)
        {
            Core.Log.Warning($"[{Name}] '{MenuSystem}' FSM not found; logos unchanged");
            return;
        }

        int n = 0;
        var fade = Mathf.Clamp(_menuFade.Value, 0.05f, 4f);
        foreach (var state in fsm.Fsm.States)
        {
            if (state == null) continue;
            bool slate = SlateStates.Contains(state.Name);
            bool activate = state.Name == ActivateState;
            if (!slate && !activate) continue;

            foreach (var a in state.Actions)
            {
                if (a == null) continue;
                var wait = a.TryCast<Wait>();
                if (wait != null && slate && Shorten(wait.time, SlateTime)) n++;

                var tween = a.TryCast<DotweenEaseColor>();
                if (tween == null) continue;
                if (slate)
                {
                    if (Shorten(tween.duration, SlateTime)) n++;
                    if (Shorten(tween.setDelay, 0f)) n++;
                }
                else
                {
                    // This fade's finishEvent drives the transition: keep it, just shorter.
                    if (Shorten(tween.duration, fade)) n++;
                    if (Shorten(tween.setDelay, 0f)) n++;
                }
            }
        }

        if (n == 0) Core.Log.Warning($"[{Name}] no logo waits/fades found on '{MenuSystem}'; logos unchanged");
        else Core.Log.Msg($"[{Name}] shortened {n} logo timings");
    }

    // Only literal values (never a shared FSM variable), and only ever shorter.
    private static bool Shorten(FsmFloat f, float to)
    {
        if (f == null || (f.UseVariable && !string.IsNullOrEmpty(f.Name))) return false;
        if (f.Value <= to) return false;
        f.Value = to;
        return true;
    }
}
