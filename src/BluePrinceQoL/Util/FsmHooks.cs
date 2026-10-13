using System;
using HarmonyLib;
using Il2CppHutongGames.PlayMaker;
using UnityEngine;

namespace BluePrinceQoL.Util;

/// <summary>
/// Global PlayMaker state-entry hooks. Every FSM in the game (including FSMs
/// on room prefabs instantiated mid-run) goes through Fsm.EnterState, so
/// features subscribe here instead of hunting for objects.
///
/// BeforeEnter runs before the state's actions start: disabling an action
/// (action.Enabled = false) here stops it running this time.
/// AfterEnter runs once the state's actions have started; it is the place to
/// send an event or SetState to jump past the state.
///
/// Handlers are called for every state entry in the game: filter on the
/// cheapest thing first (usually state.Name) and return early.
///
/// The patch is installed a few seconds after the main menu loads, not at
/// mod load: with it active while UnityExplorer loads its UI asset bundle
/// at startup, the game crashes. Nothing before the main menu needs hooking.
/// </summary>
internal static class FsmHooks
{
    public static event Action<FsmState> BeforeEnter;
    public static event Action<FsmState> AfterEnter;

    /// <summary>
    /// Features needing their own Harmony patches subscribe here (in CreatePrefs)
    /// and patch inside the handler, so every patch goes in after startup.
    /// Never use [HarmonyPatch] attributes in this mod: MelonLoader applies those
    /// at load time, which crashes the game alongside UnityExplorer.
    /// </summary>
    public static event Action<HarmonyLib.Harmony> InstallPatches;

    public static bool Installed { get; private set; }
    private const float InstallDelay = 5f;
    private static float _installAt = -1f;

    internal static void RaiseBefore(FsmState s) => Raise(BeforeEnter, s);
    internal static void RaiseAfter(FsmState s) => Raise(AfterEnter, s);

    private static void Raise(Action<FsmState> ev, FsmState s)
    {
        if (ev == null || s == null) return;
        foreach (Action<FsmState> h in ev.GetInvocationList())
        {
            try { h(s); }
            catch (Exception e) { Core.Log.Error($"FSM hook {h.Method.DeclaringType?.Name}: {e}"); }
        }
    }

    internal static void OnSceneLoaded()
    {
        if (!Installed && _installAt < 0f)
            _installAt = Time.realtimeSinceStartup + InstallDelay;
    }

    internal static void Tick(HarmonyLib.Harmony harmony)
    {
        if (Installed || _installAt < 0f || Time.realtimeSinceStartup < _installAt) return;
        Installed = true;
        // FsmState.OnEnter is inlined by IL2CPP ([CallerCount(0)] in the interop
        // assembly), so patching it never fires. Fsm.EnterState(state) wraps it
        // and is a real call site. Check CallerCount before patching any game method.
        var target = AccessTools.Method(typeof(Il2CppHutongGames.PlayMaker.Fsm), "EnterState");
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(FsmHooks), nameof(EnterStatePrefix)),
            postfix: new HarmonyMethod(typeof(FsmHooks), nameof(EnterStatePostfix)));
        Core.Log.Msg("FSM hooks installed");
        if (InstallPatches == null) return;
        foreach (Action<HarmonyLib.Harmony> h in InstallPatches.GetInvocationList())
        {
            try { h(harmony); }
            catch (Exception e) { Core.Log.Error($"Patch install {h.Method.DeclaringType?.Name}: {e}"); }
        }
    }

    private static void EnterStatePrefix(FsmState state) => RaiseBefore(state);
    private static void EnterStatePostfix(FsmState state) => RaiseAfter(state);

    /// <summary>Full hierarchy path of a GameObject, e.g. "Room/_GAMEPLAY/Item".</summary>
    public static string PathOf(GameObject go)
    {
        if (go == null) return "";
        var path = go.name;
        for (var t = go.transform.parent; t != null; t = t.parent)
            path = t.name + "/" + path;
        return path;
    }
}
