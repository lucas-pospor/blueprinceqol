using System;
using System.Collections.Generic;
using BluePrinceQoL.Util;
using Il2Cpp;
using Il2CppBluePrince.Inputs;
using Il2CppBluePrince.UI;
using Il2CppCosimo.Music;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace BluePrinceQoL.Features;

/// <summary>
/// Separate music volume (and mute), independent of sound effects, ambience and voices.
/// The game's settings menu only has a master volume.
///
/// 1. Mixer: "Master Mixer" has a "Music" group with an exposed volume parameter, also named
///    "Music". Every TrackManager track (score, room themes like the Ballroom piano, Conservatory
///    cello and Chapel) plays through that group. We set "Music" to (game value + 20*log10(volume)) dB
///    on every loaded copy of the mixer (bundles can load more than one). The game writes "Music" only once
///    (DAY FSM "Day Color Set", 0 dB). A change we didn't make is treated as the game's new
///    baseline and our offset goes back on top, so it doesn't compound. The game's own fades work
///    on AudioSource volumes and the Master parameter, so they still run.
/// 2. Unrouted music (no mixer group, so the mixer can't reach it): main menu music and any
///    TrackGroup without a TargetMixer ("Under the Blackbridge Open"). Their AudioSource.volume is
///    scaled in LateUpdate, after the game's own writes that frame; a value we didn't write (e.g. an
///    AudioFade step) becomes the new baseline, so fades still work and nothing compounds.
///    (The game's BoostListenerVolume filter was tried first and had no audible effect.)
/// Ctrl+F11 logs every playing AudioSource with its group and mixer copy.
/// 3. Cutscene videos (VIDEO CAM's VideoPlayer) play their whole soundtrack (narration, effects and
///    music) through the AudioSource "Audio for Cam", which is on the Music group. Unless
///    MusicVolume_IncludeCutscenes is set, while music is below 100% that one source is moved to the mixer's Master group, the
///    Music group's parent. Music sits at 0 dB in vanilla, so it plays exactly as loud as before and
///    muting music doesn't mute cutscenes.
/// 4. Settings menu: the game's Volume screen ("VOLUME (new)", main menu and pause menu) has one
///    UISlider driven by UIAudioSettings. We clone that slider row once per screen, strip anything
///    that could talk to the game's settings, add the clone to the screen's ControllerSelector
///    (keyboard/gamepad navigation) and poll its value into MusicVolume_Volume.
/// </summary>
public class MusicVolumeFeature : Feature
{
    public override string Name => "MusicVolume";
    protected override string Description => "Separate music volume and mute (a Music slider in the game's Volume settings, F11 mutes).";

    private const string MixerName = "Master Mixer";
    private const string MusicParam = "Music"; // exposed volume parameter of the "Music" group
    private const float MinDb = -80f;
    private const float RescanEvery = 10f;
    private const string CutsceneSourcePath = "Audio for Cam"; // VIDEO CAM VideoPlayer audio output
    private static readonly float[] EarlyScans = { 0.5f, 2f, 5f, 10f };

    // ---- settings menu row (layout numbers are in the Volume screen's local units, see notes) ----
    private const string RowName = "Volume Slider (QoL Music)";
    private const string MusicLabelName = "QoL Music Label";
    private const string MasterLabelName = "QoL Master Label";
    private const float RowSpacing = 1.7f;   // a slider row (arrows, value text, line) is ~1.65 tall
    private const float BelowShift = 0.6f;   // texts under the game's slider move down by this much
    private const float LabelWidth = 1.6f;
    private const float LabelGap = 0.35f;

    // ---- prefs ----
    private MelonPreferences_Entry<float> _volume;
    private MelonPreferences_Entry<bool> _muted, _unrouted, _cutscenes, _toastPref, _menuRow;
    private MelonPreferences_Entry<string> _muteKeyPref, _extraPref;
    private Hotkey _muteKey;
    private HashSet<string> _extraPaths = new(StringComparer.Ordinal);

    // ---- mixer state ----
    private AudioMixer _mixer;   // first instance: used for group lookups / cutscene routing
    private readonly List<AudioMixer> _mixers = new(); // every loaded instance with the Music parameter
    private float _nextMixerSearch;
    private sealed class MixerState { public bool Written; public float Last, Baseline; public float Factor = -1f; }
    private readonly Dictionary<IntPtr, MixerState> _mixerState = new();
    private bool _warnedParam;
    private AudioMixerGroup _musicGroup, _masterGroup;
    private bool _warnedGroups;
    private AudioSource _cutsceneSrc;

    // ---- unrouted sources ----
    // Music AudioSources outside the mixer: their volume is scaled in LateUpdate (after the game's
    // own writes that frame). Any value we didn't write is the game's and becomes the new baseline.
    private sealed class Scaled { public AudioSource Src; public bool Written; public float Last, Baseline; }
    private readonly List<Scaled> _scaled = new();
    private readonly HashSet<IntPtr> _filteredSources = new();
    private KeyCode _diagKey = KeyCode.F11; // with Ctrl: log every playing AudioSource
    // The game's music system (TrackGroups under e.g. "DAY/Music/Track Manager") creates its
    // AudioSources on the fly when a track starts, with no mixer group (seen in-game: the morning
    // music "Eight in the Morning" plays group-less). These roots are re-checked often so a new
    // track is scaled before it is audible.
    private const string MusicRootPath = "DAY/Music";
    private const float FastScanEvery = 0.25f;
    private readonly List<Transform> _musicRoots = new();
    private float _nextFastScan;
    private int _logged;
    private float _sceneLoadedAt;
    private int _earlyScanIdx;
    private float _nextScan;

    // ---- settings menu rows ----
    private sealed class MenuRow
    {
        public UIAudioSettings Host;
        public UISlider Slider;
        public GameObject Go;
        public bool WasActive;
        public float Shown;   // the value we last saw on (or wrote to) the slider
        public float Applied; // the music level (Factor) the slider currently stands for
    }
    private readonly List<MenuRow> _rows = new();
    private readonly HashSet<IntPtr> _failedHosts = new();
    private bool _expectMenu, _warnedMenu, _warnedNav;

    // ---- prefs save debounce ----
    private bool _saveDirty;
    private float _saveAt;

    // ---- toast ----
    private string _toast;
    private float _toastUntil;
    private GUIStyle _toastStyle;
    private int _toastFont = -1;

    protected override void CreatePrefs()
    {
        var p = Core.Prefs;
        _volume = p.CreateEntry($"{Name}_Volume", 1f, "MusicVolume: volume",
            "Music volume, 0 to 1 (1 = vanilla), as shown on the Music slider in Settings > Volume. Linear amplitude: 0.5 is about -6 dB.");
        _muted = p.CreateEntry($"{Name}_Muted", false, "MusicVolume: muted",
            "Current mute state (toggled by the mute key; moving the Music slider unmutes).");
        _muteKeyPref = p.CreateEntry($"{Name}_MuteKey", "F11", "MusicVolume: mute key",
            "Toggles mute. UnityEngine.KeyCode name, optionally with Shift+/Ctrl+/Alt+ in front. " +
            "Listed modifiers must be held; other modifiers are ignored (Shift is the game's stroll key).");
        _menuRow = p.CreateEntry($"{Name}_MenuSlider", true, "MusicVolume: Music slider in settings",
            "Add a Music slider under the game's master volume slider (main menu and pause menu Settings > Volume).");
        _unrouted = p.CreateEntry($"{Name}_UnroutedMusic", true, "MusicVolume: unrouted music",
            "Also scale music that bypasses the mixer: main menu music and the Blackbridge grotto track.");
        _extraPref = p.CreateEntry($"{Name}_ExtraSources",
            "Main Camera/Music;Main Camera/Music/Curse Music;Main Camera/Music/Dare Muisic",
            "MusicVolume: extra music sources",
            "Full hierarchy paths (separated by ';') of AudioSources without a mixer group that count as music. " +
            "Defaults are the main menu tracks. Add 'Gear Room Sound' paths etc. here if wanted.");
        _cutscenes = p.CreateEntry($"{Name}_IncludeCutscenes", false, "MusicVolume: include cutscene videos",
            "Cutscene videos (intro, Alzara, endings...) play narration, effects and music as one track on the Music " +
            "mixer group. false = move that track to the Master group so it stays at vanilla volume when music is muted.");
        _toastPref = p.CreateEntry($"{Name}_ShowToast", true, "MusicVolume: show toast", "Show 'Music 40%' / 'Music muted' briefly when the mute key is pressed.");

        _muteKey = Hotkey.Parse(_muteKeyPref.Value, new Hotkey(KeyCode.F11, false, false, false), Name);
        foreach (var s in (_extraPref.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _extraPaths.Add(s);
    }

    private float Factor => _muted.Value ? 0f : Mathf.Clamp01(_volume.Value);

    public override void OnSceneLoaded(string sceneName)
    {
        _mixer = null;           // re-find: the asset may have been reloaded with the scene
        _mixers.Clear();
        _mixerState.Clear();
        _musicGroup = _masterGroup = null;
        _cutsceneSrc = null;
        _nextMixerSearch = 0f;
        _scaled.Clear();
        _filteredSources.Clear();
        _musicRoots.Clear();
        _rows.Clear();
        _failedHosts.Clear();
        _expectMenu = sceneName == "Main Menu" || sceneName == "Mount Holly Estate";
        _sceneLoadedAt = Time.unscaledTime;
        _earlyScanIdx = 0;
        _nextScan = _sceneLoadedAt + EarlyScans[0];
    }

    public override void OnUpdate()
    {
        // Independent steps: a failure in one (e.g. the mixer) must not stop the others
        // (e.g. building the menu slider), which happened when they shared one try.
        Step(HandleKeys, 0);
        Step(ApplyMixer, 1);
        Step(ApplyUnrouted, 2);
        Step(SyncMenuRows, 3);
        if (_saveDirty && Time.unscaledTime >= _saveAt) Step(SavePrefs, 4);
    }

    private readonly int[] _stepErrors = new int[5];

    private void Step(Action a, int i)
    {
        try { a(); }
        catch (Exception e)
        {
            if (++_stepErrors[i] <= 2) Core.Log.Error($"[{Name}] {a.Method.Name}: {e}");
            if (_stepErrors[i] == 2) Core.Log.Error($"[{Name}] {a.Method.Name}: further errors suppressed");
        }
    }

    // ------------------------------------------------------------------ keys

    private void HandleKeys()
    {
        if (Input.GetKeyDown(_diagKey) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
        {
            LogDiagnostics();
            return;
        }
        if (!_muteKey.Down()) return;
        _muted.Value = !_muted.Value;
        MarkDirty(0f);
        ShowToast();
    }

    private void ShowToast()
    {
        if (!_toastPref.Value) return;
        _toast = _muted.Value ? "Music muted" : $"Music {Mathf.RoundToInt(Mathf.Clamp01(_volume.Value) * 100f)}%";
        _toastUntil = Time.unscaledTime + 1.6f;
    }

    private void MarkDirty(float delay)
    {
        RouteCutscenes();
        _saveDirty = true;
        _saveAt = Time.unscaledTime + delay;
    }

    private void SavePrefs()
    {
        _saveDirty = false;
        MelonPreferences.Save();
    }

    // ------------------------------------------------------------------ mixer

    private void ApplyMixer()
    {
        if (_mixers.Count == 0 || Time.unscaledTime >= _nextMixerSearch)
        {
            if (Time.unscaledTime < _nextMixerSearch) return;
            // Re-check now and then: scenes/bundles can load further copies of the mixer asset,
            // and the music may play through any of them.
            _nextMixerSearch = Time.unscaledTime + (_mixers.Count == 0 ? 2f : RescanEvery);
            FindMixers();
            if (_mixers.Count == 0) return; // e.g. main menu: the mixer isn't loaded there
        }

        float factor = Factor;
        for (int i = _mixers.Count - 1; i >= 0; i--)
        {
            var m = _mixers[i];
            if (m == null) { _mixers.RemoveAt(i); continue; }
            if (!m.GetFloat(MusicParam, out var cur)) continue;
            if (!_mixerState.TryGetValue(m.Pointer, out var st)) _mixerState[m.Pointer] = st = new MixerState();

            bool gameWrote = !st.Written || Mathf.Abs(cur - st.Last) > 0.01f;
            if (gameWrote) st.Baseline = cur; // the game's own value (0 dB in vanilla), never one of ours
            if (!gameWrote && factor == st.Factor) continue;

            float target = factor <= 0.0001f ? MinDb : Mathf.Max(MinDb, st.Baseline + 20f * Mathf.Log10(factor));
            if (Mathf.Abs(target - cur) > 0.001f && !m.SetFloat(MusicParam, target) && !_warnedParam)
            {
                _warnedParam = true;
                Core.Log.Warning($"[{Name}] SetFloat('{MusicParam}') refused by mixer '{m.name}'");
            }
            st.Last = target;
            st.Written = true;
            st.Factor = factor;
        }
    }

    private void FindMixers()
    {
        int before = _mixers.Count;
        foreach (var m in Resources.FindObjectsOfTypeAll<AudioMixer>())
        {
            if (m == null || !m.GetFloat(MusicParam, out var v)) continue;
            bool known = false;
            foreach (var k in _mixers)
                if (k != null && k.Pointer == m.Pointer) { known = true; break; }
            if (known) continue;
            _mixers.Add(m);
            Core.Log.Msg($"[{Name}] mixer '{m.name}' #{_mixers.Count} found, '{MusicParam}' = {v:0.##} dB");
        }
        if (_mixer == null && _mixers.Count > 0)
        {
            _mixer = _mixers[0];
            foreach (var m in _mixers)
                if (m.name == MixerName) { _mixer = m; break; }
        }
        if (_mixers.Count != before) RouteCutscenes();
    }

    // ------------------------------------------------------------------ unrouted music

    private void ApplyUnrouted()
    {
        if (Time.unscaledTime >= _nextScan)
        {
            bool lastEarly = _earlyScanIdx == EarlyScans.Length - 1; // the 10 s scan
            if (_earlyScanIdx < EarlyScans.Length) _earlyScanIdx++;
            _nextScan = _earlyScanIdx < EarlyScans.Length
                ? _sceneLoadedAt + EarlyScans[_earlyScanIdx]
                : Time.unscaledTime + RescanEvery;
            RouteCutscenes();
            if (_unrouted.Value) Scan();
            if (_menuRow.Value) ScanMenus(lastEarly);
        }

        if (_unrouted.Value && _musicRoots.Count > 0 && Time.unscaledTime >= _nextFastScan)
        {
            _nextFastScan = Time.unscaledTime + FastScanEvery;
            for (int i = _musicRoots.Count - 1; i >= 0; i--)
            {
                if (_musicRoots[i] == null) { _musicRoots.RemoveAt(i); continue; }
                AttachAll(_musicRoots[i]);
            }
        }

    }

    public override void OnLateUpdate()
    {
        float f = _unrouted.Value ? Factor : 1f;
        for (int i = _scaled.Count - 1; i >= 0; i--)
        {
            var e = _scaled[i];
            if (e.Src == null) { _scaled.RemoveAt(i); continue; }
            float cur = e.Src.volume;
            if (!e.Written || Mathf.Abs(cur - e.Last) > 0.0005f) e.Baseline = cur; // game set it (fades too)
            float want = e.Baseline * f;
            if (Mathf.Abs(want - cur) > 0.0005f) e.Src.volume = want;
            e.Last = want;
            e.Written = true;
        }
    }

    private void Scan()
    {
        // Every TrackGroup: whatever their TargetMixer says, their sources were seen playing with no
        // mixer group. AttachAll only takes group-less sources; ones on the Music group stay with the mixer.
        foreach (var tg in Resources.FindObjectsOfTypeAll<TrackGroup>())
        {
            if (tg == null || !tg.gameObject.scene.IsValid()) continue;
            AddMusicRoot(tg.transform);
            var parent = tg._sourcesParent;
            if (parent != null && !parent.IsChildOf(tg.transform)) AddMusicRoot(parent);
        }
        var dayMusic = GameObject.Find(MusicRootPath);
        if (dayMusic != null) AddMusicRoot(dayMusic.transform);

        if (_extraPaths.Count == 0) return;
        foreach (var src in Resources.FindObjectsOfTypeAll<AudioSource>())
        {
            if (src == null || src.outputAudioMixerGroup != null) continue;
            var go = src.gameObject;
            if (!go.scene.IsValid()) continue;
            if (!_extraPaths.Contains(FsmHooks.PathOf(go))) continue;
            Attach(src);
        }
    }

    private void RouteCutscenes()
    {
        if (_mixer == null) return;
        if (_musicGroup == null || _masterGroup == null)
        {
            // Not AudioMixer.FindMatchingGroups: its interop wrapper goes through an Il2Cpp
            // ReadOnlySpan and throws MissingMethodException here. The groups are loaded objects.
            foreach (var g in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
            {
                if (g == null || g.audioMixer == null || g.audioMixer.Pointer != _mixer.Pointer) continue;
                if (g.name == "Music") _musicGroup = g;
                else if (g.name == "Master") _masterGroup = g;
            }
            if (_musicGroup == null || _masterGroup == null)
            {
                if (!_warnedGroups)
                {
                    _warnedGroups = true;
                    Core.Log.Warning($"[{Name}] mixer groups Master/Music not found; cutscene audio stays on the Music group");
                }
                return;
            }
        }

        // Cached: this runs on every Music slider change, and the fallback search is expensive.
        var src = _cutsceneSrc;
        if (src == null)
        {
            var go = GameObject.Find(CutsceneSourcePath);
            src = go != null ? go.GetComponent<AudioSource>() : null;
            if (src == null) // GameObject.Find skips inactive objects
                foreach (var s in Resources.FindObjectsOfTypeAll<AudioSource>())
                    if (s != null && s.gameObject.scene.IsValid() && s.name == CutsceneSourcePath && s.transform.parent == null) { src = s; break; }
            if (src == null) return;
            _cutsceneSrc = src;
        }
        // At full volume the game's own routing is left untouched; the cutscene source is only
        // moved off the Music group while music is turned down or muted.
        var want = _cutscenes.Value || Factor >= 0.999f ? _musicGroup : _masterGroup;
        var have = src.outputAudioMixerGroup;
        if (have == want) return;
        if (have != _musicGroup && have != _masterGroup) return; // routed elsewhere by the game: leave it
        src.outputAudioMixerGroup = want;
        Core.Log.Msg($"[{Name}] cutscene audio '{CutsceneSourcePath}' -> mixer group '{want.name}'");
    }

    private void AddMusicRoot(Transform t)
    {
        foreach (var r in _musicRoots)
            if (r != null && (r.Pointer == t.Pointer || t.IsChildOf(r))) return;
        _musicRoots.Add(t);
        AttachAll(t);
    }

    private void AttachAll(Transform root)
    {
        foreach (var src in root.GetComponentsInChildren<AudioSource>(true))
            if (src != null && src.outputAudioMixerGroup == null) Attach(src);
    }

    private void Attach(AudioSource src)
    {
        if (!_filteredSources.Add(src.Pointer)) return;
        _scaled.Add(new Scaled { Src = src });
        if (_logged++ < 12) Core.Log.Msg($"[{Name}] scaling volume of '{FsmHooks.PathOf(src.gameObject)}'");
        else if (_logged == 13) Core.Log.Msg($"[{Name}] (further scaled sources not logged)");
    }

    /// <summary>Ctrl+F11: every playing AudioSource with its clip, mixer group/instance and volume.</summary>
    private void LogDiagnostics()
    {
        var sb = new System.Text.StringBuilder($"[{Name}] diagnostics: factor={Factor:0.##} mixers=");
        for (int i = 0; i < _mixers.Count; i++)
        {
            var m = _mixers[i];
            if (m == null) continue;
            m.GetFloat(MusicParam, out var v);
            sb.Append($"#{i + 1} '{m.name}' {MusicParam}={v:0.#}dB; ");
        }
        Core.Log.Msg(sb.ToString());
        int n = 0;
        foreach (var src in Resources.FindObjectsOfTypeAll<AudioSource>())
        {
            if (src == null || !src.isPlaying) continue;
            var g = src.outputAudioMixerGroup;
            string grp = g == null ? "(no group)" : g.name;
            int mi = -1;
            if (g != null && g.audioMixer != null)
                for (int i = 0; i < _mixers.Count; i++)
                    if (_mixers[i] != null && _mixers[i].Pointer == g.audioMixer.Pointer) { mi = i + 1; break; }
            string inst = g == null ? "" : mi > 0 ? $" mixer#{mi}" : " mixer UNKNOWN";
            bool scaled = _filteredSources.Contains(src.Pointer);
            Core.Log.Msg($"[{Name}]   '{FsmHooks.PathOf(src.gameObject)}' clip='{(src.clip != null ? src.clip.name : "-")}' " +
                         $"group={grp}{inst} vol={src.volume:0.###}{(scaled ? " (scaled)" : "")}");
            n++;
        }
        Core.Log.Msg($"[{Name}] {n} playing sources");
    }

    // ------------------------------------------------------------------ settings menu row

    private void WarnMenu(string msg)
    {
        if (_warnedMenu) return;
        _warnedMenu = true;
        Core.Log.Warning($"[{Name}] {msg}. The Music slider is unavailable; F11 and MusicVolume_Volume still work.");
    }

    /// <summary>Find each Volume screen (one UIAudioSettings per scene, inactive until opened) and give it a Music row.</summary>
    private void ScanMenus(bool lastEarlyScan)
    {
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (_rows[i].Host == null || _rows[i].Go == null || _rows[i].Slider == null) _rows.RemoveAt(i);

        UIAudioSettings[] hosts;
        try { hosts = Resources.FindObjectsOfTypeAll<UIAudioSettings>(); }
        catch (Exception e) { WarnMenu($"can't look up the game's volume settings ({e.Message})"); return; }

        foreach (var host in hosts)
        {
            if (host == null || !host.gameObject.scene.IsValid()) continue;
            if (_failedHosts.Contains(host.Pointer)) continue;
            bool known = false;
            foreach (var r in _rows)
                if (r.Host.Pointer == host.Pointer) { known = true; break; }
            if (known) continue;

            try
            {
                var row = BuildRow(host);
                if (row != null) _rows.Add(row);
                else _failedHosts.Add(host.Pointer);
            }
            catch (Exception e)
            {
                _failedHosts.Add(host.Pointer);
                WarnMenu($"couldn't add the Music slider to '{FsmHooks.PathOf(host.gameObject)}': {e}");
            }
        }

        if (lastEarlyScan && _expectMenu && _rows.Count == 0)
            WarnMenu("the game's Volume settings screen (UIAudioSettings) wasn't found");
    }

    private MenuRow BuildRow(UIAudioSettings host)
    {
        var orig = host.VolumeSlider;
        if (orig == null) { WarnMenu($"'{FsmHooks.PathOf(host.gameObject)}' has no VolumeSlider"); return null; }
        var origGo = orig.gameObject;
        var parent = origGo.transform.parent;
        if (parent == null) { WarnMenu("the game's volume slider has no parent"); return null; }

        var existing = parent.Find(RowName);
        GameObject go = existing != null ? existing.gameObject : CreateRow(host, origGo, parent);
        if (go == null) return null;
        var slider = go.GetComponent<UISlider>();
        if (slider == null) { WarnMenu("the cloned row has no UISlider"); return null; }

        try { LayoutOnce(parent, orig); }
        catch (Exception e) { Core.Log.Warning($"[{Name}] Music slider layout/labels: {e.Message}"); }

        Core.Log.Msg($"[{Name}] Music slider added to '{FsmHooks.PathOf(parent.gameObject)}'");
        return new MenuRow { Host = host, Slider = slider, Go = go };
    }

    /// <summary>Clone into an inactive holder first, so nothing on the copy runs before it is stripped.</summary>
    private static GameObject CloneInactive(GameObject src, Transform parent, string name)
    {
        var holder = new GameObject("QoL clone holder");
        holder.SetActive(false);
        try
        {
            var go = Object.Instantiate(src, holder.transform); // keeps src's local position/rotation/scale
            go.name = name;
            Strip(go);
            go.transform.SetParent(parent, false);
            return go;
        }
        finally { Object.Destroy(holder); }
    }

    /// <summary>Remove anything on a copy that could write the game's settings or drive game FSMs.</summary>
    private static void Strip(GameObject go)
    {
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            string type = mb.GetIl2CppType().Name;
            if (mb.TryCast<PlayMakerFSM>() != null || type.Contains("Localiz") || type.Contains("Settings"))
                Object.DestroyImmediate(mb);
        }
    }

    private GameObject CreateRow(UIAudioSettings host, GameObject origGo, Transform parent)
    {
        var go = CloneInactive(origGo, parent, RowName);
        try
        {
            var t = go.transform;
            t.localPosition = origGo.transform.localPosition + Vector3.down * RowSpacing;
            t.SetSiblingIndex(origGo.transform.GetSiblingIndex() + 1);

            var slider = go.GetComponent<UISlider>();
            if (slider != null)
            {
                slider.currentValue = Factor; // serialized value the slider shows when it first wakes up
                try { slider.OnValueChanged = (Action<float>)(_ => { }); } // never null, in case the game doesn't check
                catch (Exception e) { Core.Log.Msg($"[{Name}] Music slider OnValueChanged: {e.Message}"); }
            }

            var cs = go.GetComponent<ControllerSelectable>();
            if (cs != null)
            {
                cs.TargetFSM = null;           // hover/click events must not reach any game FSM
                cs.OnUse = new UnityEvent();
                cs.OnSelect = new UnityEvent();
                cs.OnDeselect = new UnityEvent();
            }
        }
        catch
        {
            Object.Destroy(go); // half-built: don't leave a slider on screen that does nothing
            throw;
        }

        // Navigation is a bonus: if it fails, the row still works with the mouse.
        try
        {
            var cs = go.GetComponent<ControllerSelectable>();
            if (cs == null) throw new Exception("the row has no ControllerSelectable");
            AddToSelector(host, origGo.GetComponent<ControllerSelectable>(), cs);
        }
        catch (Exception e)
        {
            if (!_warnedNav)
            {
                _warnedNav = true;
                Core.Log.Warning($"[{Name}] keyboard/gamepad can't reach the Music slider ({e.Message}); mouse only");
            }
        }
        return go;
    }

    /// <summary>
    /// The screen's ControllerSelector ("Audio settings selector") has a hand-wired Selectables list
    /// (vertical, by index). Insert the clone right after the game's slider so Down/Up moves between them.
    /// </summary>
    private void AddToSelector(UIAudioSettings host, ControllerSelectable origSel, ControllerSelectable clone)
    {
        if (origSel == null) return;
        ControllerSelector found = null;
        int idx = -1;
        foreach (var sel in host.GetComponentsInChildren<ControllerSelector>(true))
            if ((idx = IndexIn(sel, origSel)) >= 0) { found = sel; break; }
        if (found == null)
            foreach (var sel in Resources.FindObjectsOfTypeAll<ControllerSelector>())
                if (sel != null && sel.gameObject.scene.IsValid() && (idx = IndexIn(sel, origSel)) >= 0) { found = sel; break; }

        if (found == null)
        {
            if (!_warnedNav)
            {
                _warnedNav = true;
                Core.Log.Warning($"[{Name}] no ControllerSelector lists the game's volume slider; the Music slider is mouse-only");
            }
            return;
        }
        if (IndexIn(found, clone) >= 0) return;

        var old = found.Selectables;
        var arr = new Il2CppReferenceArray<ControllerSelectable>(old.Length + 1);
        for (int i = 0, j = 0; i < old.Length; i++)
        {
            arr[j++] = old[i];
            if (i == idx) arr[j++] = clone;
        }
        found.Selectables = arr;
    }

    private static int IndexIn(ControllerSelector sel, ControllerSelectable s)
    {
        var arr = sel?.Selectables;
        if (arr == null) return -1;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] != null && arr[i].Pointer == s.Pointer) return i;
        return -1;
    }

    /// <summary>
    /// Once per screen: move the texts below the game's slider ("Text at bottom") down to make room,
    /// and put "Master" / "Music" labels to the left of each slider's minimum label.
    /// </summary>
    private static void LayoutOnce(Transform parent, UISlider orig)
    {
        if (parent.Find(MusicLabelName) != null) return;

        var lineT = orig.SlideArea != null ? orig.SlideArea.transform : orig.transform;
        float lineY = parent.InverseTransformPoint(lineT.position).y;
        for (int i = 0; i < parent.childCount; i++)
        {
            var t = parent.GetChild(i);
            if (t == orig.transform || t.name == RowName || t.name == MasterLabelName) continue;
            if (t.GetComponent<TMP_Text>() == null) continue; // e.g. BLACK FADE, selector, ESC handler
            if (parent.InverseTransformPoint(t.position).y >= lineY) continue; // headlines
            t.localPosition += Vector3.down * BelowShift;
        }

        var min = orig.MinLabel;
        if (min == null) return;
        if (parent.Find(MasterLabelName) == null) MakeLabel(min, parent, MasterLabelName, "Master", 0f);
        MakeLabel(min, parent, MusicLabelName, "Music", -RowSpacing);
    }

    private static void MakeLabel(TextMeshPro min, Transform parent, string name, string text, float dy)
    {
        var go = CloneInactive(min.gameObject, parent, name);
        var tmp = go.GetComponent<TMP_Text>();
        if (tmp != null)
        {
            tmp.text = text;
            tmp.horizontalAlignment = HorizontalAlignmentOptions.Right;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
        }
        var rt = go.GetComponent<RectTransform>();
        float minW = 1f;
        if (rt != null)
        {
            minW = rt.sizeDelta.x;
            rt.sizeDelta = new Vector2(LabelWidth, rt.sizeDelta.y);
        }
        var p = parent.InverseTransformPoint(min.transform.position);
        go.transform.localPosition = p + new Vector3(-(minW * 0.5f + LabelGap + LabelWidth * 0.5f), dy, 0f);
    }

    /// <summary>Per frame: show the current music level on each open Music slider, and take the user's changes from it.</summary>
    private void SyncMenuRows()
    {
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            var r = _rows[i];
            if (r.Go == null || r.Slider == null) { _rows.RemoveAt(i); continue; }
            if (!r.Go.activeInHierarchy)
            {
                if (r.WasActive && _saveDirty) SavePrefs(); // screen closed
                r.WasActive = false;
                continue;
            }

            float want = Factor;
            if (!r.WasActive)
            {
                r.WasActive = true;
                Show(r, want);
                continue;
            }

            float v = r.Slider.Value;
            if (Mathf.Abs(v - r.Shown) > 0.0001f)
            {
                // The user moved the slider (mouse drag/click or left/right while selected).
                r.Shown = v;
                _volume.Value = Quantize(v);
                if (_muted.Value) _muted.Value = false; // moving the slider unmutes
                r.Applied = Factor;
                MarkDirty(0.5f);
            }
            else if (Mathf.Abs(want - r.Applied) > 0.0001f)
            {
                Show(r, want); // changed elsewhere (mute key, prefs file)
            }
        }
    }

    private static float Quantize(float v) => Mathf.Clamp01(Mathf.Round(v * 100f) / 100f);

    private static void Show(MenuRow r, float value)
    {
        r.Slider.Value = value;
        r.Shown = r.Slider.Value; // read back: the slider may clamp
        r.Applied = value;
    }

    // ------------------------------------------------------------------ drawing

    public override void OnGUI()
    {
        if (_toast != null && Time.unscaledTime < _toastUntil && Event.current.type == EventType.Repaint) DrawToast();
    }

    private void DrawToast()
    {
        int font = Mathf.Max(12, Mathf.RoundToInt(Screen.height * 0.03f));
        if (_toastStyle == null || font != _toastFont)
        {
            _toastFont = font;
            _toastStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = font,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
                clipping = TextClipping.Overflow,
            };
        }
        float left = _toastUntil - Time.unscaledTime;
        float a = Mathf.Clamp01(left / 0.4f); // fade out over the last 0.4 s
        var r = new Rect(0, Screen.height * 0.12f, Screen.width, font * 1.6f);
        float o = Mathf.Max(1f, font / 14f);
        _toastStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f * a);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    GUI.Label(new Rect(r.x + dx * o, r.y + dy * o, r.width, r.height), _toast, _toastStyle);
        _toastStyle.normal.textColor = new Color(1f, 0.97f, 0.88f, a);
        GUI.Label(r, _toast, _toastStyle);
    }

    // ------------------------------------------------------------------ hotkeys

    private readonly struct Hotkey
    {
        private readonly KeyCode _key;
        private readonly bool _shift, _ctrl, _alt;

        public Hotkey(KeyCode key, bool shift, bool ctrl, bool alt)
        {
            _key = key; _shift = shift; _ctrl = ctrl; _alt = alt;
        }

        /// <summary>"F11", "Ctrl+M", "Ctrl+Alt+M".</summary>
        public static Hotkey Parse(string text, Hotkey fallback, string feature)
        {
            bool shift = false, ctrl = false, alt = false;
            KeyCode key = KeyCode.None;
            foreach (var raw in (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "shift": shift = true; break;
                    case "ctrl": case "control": ctrl = true; break;
                    case "alt": alt = true; break;
                    default:
                        if (!Enum.TryParse(raw, true, out key)) key = KeyCode.None;
                        break;
                }
            }
            if (key == KeyCode.None)
            {
                Core.Log.Warning($"[{feature}] can't parse hotkey '{text}', using the default");
                return fallback;
            }
            return new Hotkey(key, shift, ctrl, alt);
        }

        /// <summary>
        /// Listed modifiers must be held; unlisted ones are ignored, so plain "F11" also fires while
        /// Shift (the game's stroll button) or Ctrl/Alt is held.
        /// </summary>
        public bool Down()
        {
            if (!Input.GetKeyDown(_key)) return false;
            if (_shift && !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return false;
            if (_ctrl && !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return false;
            if (_alt && !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))) return false;
            return true;
        }
    }
}
