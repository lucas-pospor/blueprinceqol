using BluePrinceQoL.Util;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityStandardAssets.Characters.FirstPerson;

namespace BluePrinceQoL.Features;

/// <summary>
/// Slightly faster walking and running.
///
/// All player movement goes through FirstPersonController (Prince, Underground,
/// Reddington ... all use the same component). Once per frame LateUpdate calls
/// ManageSpeeds(), which (from the native code) does:
///   walkTarget = max(MinWalkSpeed, m_WalkSpeed) * DraftSpeedScale * debugMult
///   runTarget  = max(MinRunSpeed, (HasRunningShoes ? m_ShoesRunSpeed : m_RunSpeed)
///                + m_ExtraRunSpeed * _extraRunPercent) * DraftSpeedScale * debugMult
///   _actualWalkSpeed/_actualRunSpeed = Lerp(current, target, dt * SpeedInterpolationFactor)
/// GetInput() then hands _actualWalkSpeed or _actualRunSpeed to the movement code.
///
/// Several FSMs write these public fields at runtime (RUN ENGINE sets
/// m_WalkSpeed/m_RunSpeed, the basement molasses tweens m_ExtraRunSpeed, the
/// cheat console sets MinWalkSpeed, drafting tweens DraftSpeedScale 0 -> 1 ...),
/// and some read them back. So the fields are never changed for good: a prefix on
/// ManageSpeeds scales the six speed inputs, ManageSpeeds computes its targets
/// from them, and the postfix puts the game's values back. Both targets are
/// degree-1 in those inputs, so the result is exactly multiplier x the game's
/// own target, whatever the game has set. Nothing compounds, since each frame
/// starts again from the game's values. DraftSpeedScale, freezes (m_CanMove)
/// and the pathways multiplier are untouched, so they still work as before.
/// </summary>
public class MoveSpeedFeature : Feature
{
    public override string Name => "MoveSpeed";
    protected override string Description => "Slightly faster walking and running (multiplies the game's own speeds)";

    private const float MinMul = 0.5f, MaxMul = 3f;

    private static MoveSpeedFeature _self;
    private MelonPreferences_Entry<float> _walkPref, _runPref;

    // Game values saved by the prefix and the scaled values it wrote.
    private struct Speeds { public float MinWalk, Walk, MinRun, Run, ShoesRun, ExtraRun; }
    private static FirstPersonController _pending;
    private static Speeds _base, _scaled;

    private static float _lastWalk = float.NaN, _lastRun = float.NaN;
    private static bool _warnedRange, _loggedFirst;
    private static int _errors;

    protected override void CreatePrefs()
    {
        _self = this;
        var p = Core.Prefs;
        _walkPref = p.CreateEntry($"{Name}_WalkMultiplier", 1.2f, "MoveSpeed: walk multiplier",
            $"Multiplies the game's walking speed (holding the stroll key). 1 = vanilla. Clamped to {MinMul}-{MaxMul}.");
        _runPref = p.CreateEntry($"{Name}_RunMultiplier", 1.2f, "MoveSpeed: run multiplier",
            $"Multiplies the game's normal (running) speed, including running shoes and the long-run bonus. 1 = vanilla. Clamped to {MinMul}-{MaxMul}.");
        FsmHooks.InstallPatches += Install;
    }

    private void Install(HarmonyLib.Harmony harmony)
    {
        // ManageSpeeds is [CallerCount(1)]: a real call from LateUpdate, so the detour fires.
        var target = AccessTools.Method(typeof(FirstPersonController), nameof(FirstPersonController.ManageSpeeds));
        if (target == null)
        {
            Core.Log.Warning($"[{Name}] FirstPersonController.ManageSpeeds not found; speeds stay vanilla");
            return;
        }
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(MoveSpeedFeature), nameof(ManageSpeedsPrefix)),
            postfix: new HarmonyMethod(typeof(MoveSpeedFeature), nameof(ManageSpeedsPostfix)));
        Core.Log.Msg($"[{Name}] patched FirstPersonController.ManageSpeeds");
    }

    private float Clamped(MelonPreferences_Entry<float> e)
    {
        var v = e.Value;
        if (float.IsNaN(v) || float.IsInfinity(v)) v = 1f;
        if (v < MinMul || v > MaxMul)
        {
            if (!_warnedRange)
                Core.Log.Warning($"[{Name}] {e.Identifier} = {e.Value} is outside {MinMul}-{MaxMul}; clamping");
            _warnedRange = true;
            v = Mathf.Clamp(v, MinMul, MaxMul);
        }
        return v;
    }

    private static void ManageSpeedsPrefix(FirstPersonController __instance)
    {
        try
        {
            // A postfix that never ran (exception inside ManageSpeeds) must not
            // leave scaled values behind, or the next prefix would scale them again.
            if (_pending != null) Restore();

            var self = _self;
            if (self == null || !self.Enabled || _errors >= 3 || __instance == null) return;

            var walk = self.Clamped(self._walkPref);
            var run = self.Clamped(self._runPref);
            if (walk != _lastWalk || run != _lastRun)
            {
                // Prefs are read every frame, so an edited cfg applies as soon as
                // MelonLoader reloads it (or at the latest on next launch).
                if (!float.IsNaN(_lastWalk))
                    Core.Log.Msg($"[{self.Name}] multipliers now walk x{walk}, run x{run}");
                _lastWalk = walk;
                _lastRun = run;
            }
            if (walk == 1f && run == 1f) return;

            var b = new Speeds
            {
                MinWalk = __instance.MinWalkSpeed,
                Walk = __instance.m_WalkSpeed,
                MinRun = __instance.MinRunSpeed,
                Run = __instance.m_RunSpeed,
                ShoesRun = __instance.m_ShoesRunSpeed,
                ExtraRun = __instance.m_ExtraRunSpeed,
            };
            var s = new Speeds
            {
                MinWalk = b.MinWalk * walk,
                Walk = b.Walk * walk,
                MinRun = b.MinRun * run,
                Run = b.Run * run,
                ShoesRun = b.ShoesRun * run,
                ExtraRun = b.ExtraRun * run,
            };

            _base = b;
            _scaled = s;
            _pending = __instance;
            __instance.MinWalkSpeed = s.MinWalk;
            __instance.m_WalkSpeed = s.Walk;
            __instance.MinRunSpeed = s.MinRun;
            __instance.m_RunSpeed = s.Run;
            __instance.m_ShoesRunSpeed = s.ShoesRun;
            __instance.m_ExtraRunSpeed = s.ExtraRun;

            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Core.Log.Msg($"[{self.Name}] active on '{__instance.gameObject.name}': walk x{walk} " +
                             $"(game {Mathf.Max(b.MinWalk, b.Walk):0.##} -> {Mathf.Max(s.MinWalk, s.Walk):0.##}), run x{run} " +
                             $"(game {Mathf.Max(b.MinRun, b.Run):0.##} -> {Mathf.Max(s.MinRun, s.Run):0.##}), before drafting/debug scale");
            }
        }
        catch (System.Exception e) { Fail(e); }
    }

    private static void ManageSpeedsPostfix()
    {
        try
        {
            if (_pending != null) Restore();
        }
        catch (System.Exception e) { Fail(e); }
    }

    // Put the game's values back. A field is only restored if it still holds the
    // value we wrote; if something else changed it meanwhile, its write wins.
    private static void Restore()
    {
        var fpc = _pending;
        _pending = null;
        if (fpc == null || fpc.WasCollected) return;
        if (fpc.MinWalkSpeed == _scaled.MinWalk) fpc.MinWalkSpeed = _base.MinWalk;
        if (fpc.m_WalkSpeed == _scaled.Walk) fpc.m_WalkSpeed = _base.Walk;
        if (fpc.MinRunSpeed == _scaled.MinRun) fpc.MinRunSpeed = _base.MinRun;
        if (fpc.m_RunSpeed == _scaled.Run) fpc.m_RunSpeed = _base.Run;
        if (fpc.m_ShoesRunSpeed == _scaled.ShoesRun) fpc.m_ShoesRunSpeed = _base.ShoesRun;
        if (fpc.m_ExtraRunSpeed == _scaled.ExtraRun) fpc.m_ExtraRunSpeed = _base.ExtraRun;
    }

    // Patch errors are counted here (Core.Safe does not see them); after 3 the
    // feature stops scaling and the game runs vanilla speeds.
    private static void Fail(System.Exception e)
    {
        _errors++;
        if (_errors <= 3) Core.Log.Error($"[MoveSpeed] {e}");
        if (_errors == 3) Core.Log.Error("[MoveSpeed] disabled after repeated errors; speeds are vanilla");
    }
}
