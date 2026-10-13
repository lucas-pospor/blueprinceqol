using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using MelonLoader;
using UnityEngine;

namespace BluePrinceQoL.Features;

/// <summary>
/// Debug aid: logs PlayMaker state entries to the MelonLoader log so FSM flows
/// can be traced in-game. Off by default; Filter limits output to entries whose
/// "path :: fsm :: state" line contains the given text (case-insensitive).
/// </summary>
public class StateLoggerFeature : Feature
{
    public override string Name => "StateLogger";
    protected override string Description => "Debug: log PlayMaker FSM state entries";
    protected override bool DefaultEnabled => false;

    private MelonPreferences_Entry<string> _filter;
    private int _total;
    private float _nextSummary;

    protected override void CreatePrefs()
    {
        _filter = Core.Prefs.CreateEntry("StateLogger_Filter", "", "StateLogger: filter",
            "Only log entries containing this text (empty = log everything)");
        FsmHooks.BeforeEnter += OnEnter;
    }

    private void OnEnter(FsmState state)
    {
        if (!Enabled) return;
        _total++;
        var fsm = state.Fsm;
        var line = $"{FsmHooks.PathOf(fsm?.GameObject)} :: {fsm?.Name} :: {state.Name}";
        var f = _filter.Value;
        if (string.IsNullOrEmpty(f) || line.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0)
            Core.Log.Msg($"[FSM] {line}");
    }

    public override void OnUpdate()
    {
        if (Time.unscaledTime < _nextSummary) return;
        _nextSummary = Time.unscaledTime + 10f;
        Core.Log.Msg($"[FSM] {_total} state entries so far");
    }
}
