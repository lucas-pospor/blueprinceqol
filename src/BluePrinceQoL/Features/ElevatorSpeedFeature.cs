using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2CppHutongGames.PlayMaker;
using MelonLoader;
using UnityEngine;
using PmFsm = Il2CppHutongGames.PlayMaker.Fsm;

namespace BluePrinceQoL.Features;

/// <summary>
/// Faster elevator rides.
///
/// The four elevators the player rides (all FSMs named "FSM"):
///   Boiler Room L4    STEAM 2/L4 Elevator/Buttons Code: "Up Button Pressed" (Wait 0.4) -> "Elevator Up"
///                     (cab and FPS Home DOTween 3 s, Wait 3) -> "DARE CHECK 2" -> "Up" (QuickUnfreeze); down:
///                     "Button Down Press" -> "Elevator Lowering" -> "DARE CHECK 3" -> "Elevator is Down".
///   Chamber of Mirrors L3   _GAMEPLAY/MirrorMise pillar 5: "Move 5" -> "1 up 5" / "1 down 5" (speed-based DOTween
///                     1 m/s after 0.9 s, ~5.4 s) [-> "State 4"] -> "State 3" (QuickUnfreeze after 0.1 s).
///   Foundation L1     Northside Secret/L1 Elevator/L1 Lever.001/Handle/Handle Collider: "Mouse Cursor Off" ->
///                     "State 8" (player parented to the cab) -> "Elevator Going Down" (cab 15 s) -> "Music Play" ->
///                     "State 6" (room moves behind a black screen) -> "DARE CHECK" -> "State 7" (unfreeze), ~16 s;
///                     up: "Mouse Cursor Off 2" -> "Music Swap Back" -> "State 9" -> "Elevator Going Up" ->
///                     "State 10" -> "DARE CHECK 2" -> "State 11", ~14 s. Timed together with
///                     UNDERGROUND/Below Foundation (fades, black screen) and the L1 Elevator/Duct Mask.
///   Cliff (Precipice) TERRAIN/.../Cliff Elevator/LEVER: "Lever Turn - Gas ON" (doors close, 2 s) -> "State 2" +
///                     "State 3" (cab 17 s, player parented and free to walk) -> "DARE CHECK" -> "Doors Open Back Up"
///                     (2.3 s); up: "... 2" / "State 7".
///
/// The Foundation ride is timed across three FSMs (the room may only move while the screen is black, and the cab
/// must still be moving when the room move ends), so shortening single durations is fragile. Instead the feature
/// raises Time.timeScale while one of those FSMs is in a ride state: every Wait, fade, tween and delayed event in
/// every FSM runs faster by the same factor, so their order and overlaps stay exactly as in vanilla. Time scale is
/// put back as soon as the FSM leaves the ride states (before the state that unfreezes the player runs), when the
/// FSM goes away, on scene load, when the feature is turned off, or after 30 s. If something else changes the time
/// scale meanwhile (a pause menu), the feature stops managing it and leaves the new value alone.
/// Not covered: calling the Foundation elevator from the basement (player not aboard) and the one-time power-on and
/// unlock cutscenes.
/// </summary>
public class ElevatorSpeedFeature : Feature
{
    public override string Name => "ElevatorSpeed";
    protected override string Description => "Faster elevator rides (Boiler Room, Chamber of Mirrors, Foundation, cliff)";

    private const float MaxSpeed = 6f;
    private const float Timeout = 30f;  // real seconds; the longest ride is ~20 game seconds

    private sealed class Ride
    {
        public string Label, PathSuffix;
        public HashSet<string> States;
        public MelonPreferences_Entry<bool> Enabled;
    }

    private readonly Ride[] _rides =
    {
        new() { Label = "Boiler Room", PathSuffix = "STEAM 2/L4 Elevator/Buttons Code", States = new()
        {
            "Up Button Pressed", "Elevator Up", "DARE CHECK 2",
            "Button Down Press", "Elevator Lowering", "DARE CHECK 3",
        } },
        new() { Label = "Chamber of Mirrors", PathSuffix = "_GAMEPLAY/MirrorMise", States = new()
        {
            "Move 5", "1 up 5", "1 down 5", "State 4",
        } },
        new() { Label = "Foundation", PathSuffix = "L1 Elevator/L1 Lever.001/Handle/Handle Collider", States = new()
        {
            "Mouse Cursor Off", "State 8", "Elevator Going Down", "Music Play", "State 6", "DARE CHECK",
            "Mouse Cursor Off 2", "Music Swap Back", "State 9", "Elevator Going Up", "State 10", "DARE CHECK 2",
        } },
        new() { Label = "Cliff", PathSuffix = "Cliff Elevator/LEVER", States = new()
        {
            "Lever Turn - Gas ON", "State 2", "State 3", "DARE CHECK", "Doors Open Back Up",
            "Lever Turn - Gas ON 2", "State 7", "DARE CHECK 2", "Doors Open Back Up 2",
        } },
    };

    private readonly HashSet<string> _allStates = new();
    // Fsm pointer -> (its GameObject, ride index or -1). The GameObject is checked on lookup: room FSMs come and
    // go during a run and a freed pointer can be reused by another FSM.
    private readonly Dictionary<IntPtr, (IntPtr go, int idx)> _fsmRide = new();

    private MelonPreferences_Entry<float> _speed;

    private PmFsm _fsm;       // the FSM of the ride in progress, or null
    private Ride _ride;
    private float _scale, _savedScale, _startedAt;

    protected override void CreatePrefs()
    {
        _speed = Core.Prefs.CreateEntry("ElevatorSpeed_Speed", 3f, "ElevatorSpeed: speed",
            "How many times faster elevator rides run (1 = vanilla, at most 6). Everything in the game runs faster " +
            "for the length of the ride, so the cab, the player, fades and sound cues stay in sync");
        foreach (var r in _rides)
        {
            var key = r.Label.Replace(" ", "");
            r.Enabled = Core.Prefs.CreateEntry($"ElevatorSpeed_{key}", true, $"ElevatorSpeed: {r.Label}",
                $"Speed up the {r.Label} elevator");
            _allStates.UnionWith(r.States);
        }
        FsmHooks.BeforeEnter += OnBeforeEnter;
    }

    public override void OnSceneLoaded(string sceneName)
    {
        Stop("scene load");
        _fsmRide.Clear();
    }

    private void OnBeforeEnter(FsmState state)
    {
        var fsm = state.Fsm;
        if (fsm == null) return;

        // A ride in progress ends as soon as its FSM enters any other state,
        // before that state's actions (the unfreeze) run.
        if (_fsm != null && fsm.Pointer == _fsm.Pointer)
        {
            if (!_ride.States.Contains(state.Name)) Stop($"'{state.Name}'");
            return;
        }

        if (!Enabled || _fsm != null || !_allStates.Contains(state.Name)) return;
        var idx = RideOf(fsm);
        if (idx < 0) return;
        var ride = _rides[idx];
        if (!ride.Enabled.Value || !ride.States.Contains(state.Name)) return;
        Start(fsm, ride, state.Name);
    }

    public override void OnUpdate()
    {
        if (_fsm == null) return;
        string active;
        try { active = _fsm.ActiveStateName; } catch { active = null; }
        if (active == null || !_ride.States.Contains(active)) Stop("ride over");
        else if (Time.realtimeSinceStartup - _startedAt > Timeout) Stop("timeout");
        else if (!Mathf.Approximately(Time.timeScale, _scale))
        {
            Core.Log.Msg($"[{Name}] time scale changed to {Time.timeScale} by the game; leaving it alone");
            _fsm = null;
            _ride = null;
        }
    }

    private void Start(PmFsm fsm, Ride ride, string stateName)
    {
        var s = _speed.Value;
        if (float.IsNaN(s) || s <= 1f) return;
        _scale = Math.Min(s, MaxSpeed);
        _savedScale = Time.timeScale;
        if (_savedScale <= 0f) return;  // paused
        Time.timeScale = _savedScale * _scale;
        _scale = Time.timeScale;
        _fsm = fsm;
        _ride = ride;
        _startedAt = Time.realtimeSinceStartup;
        Core.Log.Msg($"[{Name}] {ride.Label} elevator: '{stateName}', time scale {_scale:0.##}");
    }

    private void Stop(string why)
    {
        if (_fsm == null) return;
        if (Mathf.Approximately(Time.timeScale, _scale)) Time.timeScale = _savedScale;
        Core.Log.Msg($"[{Name}] {_ride.Label} elevator done ({why}) after {Time.realtimeSinceStartup - _startedAt:0.0}s");
        _fsm = null;
        _ride = null;
    }

    private int RideOf(PmFsm fsm)
    {
        var ptr = fsm.Pointer;
        var go = fsm.GameObject;
        if (go == null) return -1;
        if (_fsmRide.TryGetValue(ptr, out var c) && c.go == go.Pointer) return c.idx;
        var idx = -1;
        if (fsm.Name == "FSM")
        {
            var path = FsmHooks.PathOf(go);
            for (var i = 0; i < _rides.Length; i++)
                if (path.EndsWith(_rides[i].PathSuffix, StringComparison.Ordinal)) { idx = i; break; }
        }
        _fsmRide[ptr] = (go.Pointer, idx);
        return idx;
    }
}
