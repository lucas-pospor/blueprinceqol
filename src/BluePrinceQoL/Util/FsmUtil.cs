using Il2Cpp;
using Il2CppHutongGames.PlayMaker;
using UnityEngine;

namespace BluePrinceQoL.Util;

/// <summary>Helpers for finding PlayMaker FSMs and their variables.</summary>
internal static class FsmUtil
{
    /// <summary>First FSM named <paramref name="fsmName"/> on the GameObject, or null.</summary>
    public static PlayMakerFSM Get(GameObject go, string fsmName = "FSM")
    {
        if (go == null) return null;
        foreach (var fsm in go.GetComponents<PlayMakerFSM>())
            if (fsm.FsmName == fsmName) return fsm;
        return null;
    }

    /// <summary>Find an active GameObject by name, then its FSM.</summary>
    public static PlayMakerFSM Find(string goName, string fsmName = "FSM") =>
        Get(GameObject.Find(goName), fsmName);

    public static FsmInt Int(PlayMakerFSM fsm, string name) => fsm?.FsmVariables.GetFsmInt(name);
    public static FsmFloat Float(PlayMakerFSM fsm, string name) => fsm?.FsmVariables.GetFsmFloat(name);
    public static FsmBool Bool(PlayMakerFSM fsm, string name) => fsm?.FsmVariables.GetFsmBool(name);
    public static FsmString String(PlayMakerFSM fsm, string name) => fsm?.FsmVariables.GetFsmString(name);
}
