using System;
using BluePrinceQoL.Util;
using Il2Cpp;
using Il2CppBluePrince.Inputs;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BluePrinceQoL.Features;

/// <summary>
/// "QUIT WITHOUT SAVING" in the pause menu: leave the day for the main menu without saving, so Continue starts the
/// same day again from the morning.
///
/// The game only writes its save (Global Persistent "SAVE" -> ES3 SaveAll) after the end-of-day diary, from
/// SAVE & CONTINUE ("Save & Continue" / "Save & Quit" -> DAY/SAVE SYSTEM -> "Quit to main menu": LOADING on,
/// LoadSceneAsynch "Main Menu"). Nothing is saved during a day, and the Main Menu resets Global Persistent ("RESET")
/// when it opens; Continue then loads the last save. So leaving for the main menu without sending SAVE discards the
/// day, as quitting the game would. The Dare game over returns to the main menu the same way.
///
/// - Row: a copy of SYSTEM MENU/SETTINGS/SAVE & QUIT (FSM removed), one text line below it, added to the
///   SYSTEM MENU ControllerSelector after SAVE & QUIT.
/// - Dialog: a copy of PAUSE/CALL IT A DAY TEXT (FSMs removed) with our title, warning and QUIT / NOT YET; the
///   selected option is blue and the arrow sits next to it, as in the original. NOT YET or Back returns to the menu.
/// - QUIT: music and sound fade out (AudioListener.volume, put back once the main menu has loaded), the screen fades
///   to black, the game's LOADING icon shows, then LoadSceneAsync("Main Menu"). No save event is ever sent.
/// Steam achievements earned today stay unlocked (they're sent to Steam at once); everything else from today is lost,
/// including permanent unlocks, which the game also only saves at the end of the day.
/// </summary>
public class QuitWithoutSavingFeature : Feature
{
    public override string Name => "QuitWithoutSaving";
    protected override string Description => "QUIT WITHOUT SAVING in the pause menu: back to the main menu, today is discarded";

    private const string RowName = "QUIT WITHOUT SAVING";
    private const string DialogName = "QUIT WITHOUT SAVING TEXT";
    private const string RowText = "\n\n\n\n\nQUIT WITHOUT SAVING";  // SAVE & QUIT is "\n\n\n\nSAVE & QUIT": one line lower
    private static readonly float[] BuildTries = { 1f, 3f, 6f, 10f };
    private const float FadeTime = 1f;   // as SAVE & CONTINUE's "Save & Quit" (Master -80 over 1 s, CameraFadeOut 1)

    private static readonly Color RowNormal = new(0.849f, 0.849f, 0.849f, 1f);
    private static readonly Color RowHover = new(0.412f, 0.796f, 1f, 1f);
    private static readonly Color OptionOn = new(0.2627451f, 0.6039216f, 0.8f, 1f);
    private static readonly Color OptionOff = new(0.3882353f, 0.3882353f, 0.3882353f, 1f);

    private const string Warning =
        "Everything since this morning will be lost: rooms, items, resources, discoveries and anything unlocked today. " +
        "Steam achievements earned today stay unlocked. Choose CONTINUE on the main menu to start the day again.";

    private GameObject _row, _dialog, _systemMenu, _pause, _loading, _arrow;
    private TMP_Text _rowText, _yesText, _noText;
    private float _sceneAt;
    private int _try = -1;
    private bool _warned;

    // Quitting
    private bool _quitting;
    private float _quitAt;
    private float _savedVolume = -1f;   // AudioListener.volume before the fade; put back on the main menu

    public override void OnSceneLoaded(string sceneName)
    {
        _row = _dialog = _systemMenu = _pause = _loading = _arrow = null;
        _rowText = _yesText = _noText = null;
        _try = sceneName == "Mount Holly Estate" ? 0 : -1;
        _sceneAt = Time.unscaledTime;
        if (_quitting)
        {
            _quitting = false;
            if (_savedVolume >= 0f) AudioListener.volume = _savedVolume;
            _savedVolume = -1f;
        }
    }

    public override void OnUpdate()
    {
        if (_try >= 0 && _try < BuildTries.Length && Time.unscaledTime - _sceneAt >= BuildTries[_try])
        {
            var last = _try == BuildTries.Length - 1;
            _try++;
            TryBuild(last);
        }

        if (_quitting)
        {
            var k = Mathf.Clamp01((Time.unscaledTime - _quitAt) / FadeTime);
            if (_savedVolume >= 0f) AudioListener.volume = _savedVolume * (1f - k);
            return;
        }

        // The pause menu was closed or reset behind our dialog (it doesn't know about it).
        if (_dialog != null && _dialog.activeSelf && (_pause == null || !_pause.activeInHierarchy ||
                                                       (_systemMenu != null && _systemMenu.activeSelf)))
            _dialog.SetActive(false);
    }

    public override void OnGUI()
    {
        if (!_quitting) return;
        var k = Mathf.Clamp01((Time.unscaledTime - _quitAt) / FadeTime);
        var old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, k);
        GUI.depth = -1000;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

    // ------------------------------------------------------------------ actions

    private void OpenDialog()
    {
        if (_dialog == null || _systemMenu == null || _quitting) return;
        if (_rowText != null) _rowText.color = RowNormal;
        _systemMenu.SetActive(false);
        _dialog.SetActive(true);
    }

    private void CloseDialog()
    {
        if (_dialog == null || _quitting) return;
        _dialog.SetActive(false);
        if (_systemMenu != null) _systemMenu.SetActive(true);
    }

    private void Quit()
    {
        if (_quitting || !Enabled) return;
        _quitting = true;
        _quitAt = Time.unscaledTime;
        _savedVolume = AudioListener.volume;
        Core.Log.Msg($"[{Name}] leaving for the main menu without saving");
        MelonCoroutines.Start(QuitRoutine());
    }

    private System.Collections.IEnumerator QuitRoutine()
    {
        while (Time.unscaledTime - _quitAt < FadeTime) yield return null;
        if (_dialog != null) _dialog.SetActive(false);
        if (_loading != null) _loading.SetActive(true);
        if (Time.timeScale <= 0f) Time.timeScale = 1f;  // never carry a paused clock into the menu
        SceneManager.LoadSceneAsync("Main Menu");
    }

    private void Select(bool yes)
    {
        if (_yesText != null) _yesText.color = yes ? OptionOn : OptionOff;
        if (_noText != null) _noText.color = yes ? OptionOff : OptionOn;
        if (_arrow == null) return;
        var opt = yes ? _yesText?.transform : _noText?.transform;
        if (opt == null) return;
        // The original moves the arrow to y -1003.15 (YES) / -1003.708 (NO): the option rows' heights in the dialog.
        var local = _arrow.transform.parent.InverseTransformPoint(opt.position);
        var p = _arrow.transform.localPosition;
        _arrow.transform.localPosition = new Vector3(p.x, local.y + _arrowOffset, p.z);
    }

    private float _arrowOffset;

    // ------------------------------------------------------------------ build

    private void TryBuild(bool last)
    {
        var overlay = GameObject.Find("UI OVERLAY CAM");
        var pause = overlay != null ? overlay.transform.Find("PAUSE") : null;
        var system = pause?.Find("SYSTEM MENU");
        var settings = system?.Find("SETTINGS");
        var saveQuit = settings?.Find("SAVE & QUIT");
        var callIt = pause?.Find("CALL IT A DAY TEXT");
        if (saveQuit == null || callIt == null)
        {
            if (last) Warn("the pause menu's SAVE & QUIT or its Call-it-a-day dialog wasn't found");
            return;
        }
        _try = BuildTries.Length;
        if (settings.Find(RowName) != null) return;

        GameObject row = null, dialog = null;
        try
        {
            dialog = BuildDialog(callIt.gameObject, pause);
            row = BuildRow(saveQuit.gameObject, settings, system.gameObject);
            _row = row;
            _dialog = dialog;
            _systemMenu = system.gameObject;
            _pause = pause.gameObject;
            _loading = overlay.transform.Find("LOADING")?.gameObject;
            Core.Log.Msg($"[{Name}] QUIT WITHOUT SAVING added to the pause menu");
        }
        catch (Exception e)
        {
            if (row != null) Object.Destroy(row);
            if (dialog != null) Object.Destroy(dialog);
            _row = _dialog = null;
            Warn($"couldn't build it: {e}");
        }
    }

    private void Warn(string msg)
    {
        if (_warned) return;
        _warned = true;
        Core.Log.Warning($"[{Name}] {msg}. The pause menu stays as it is.");
    }

    private static GameObject CloneInactive(GameObject src, string name, Action<GameObject> prepare)
    {
        var holder = new GameObject("QoL clone holder");
        holder.SetActive(false);
        try
        {
            var go = Object.Instantiate(src, holder.transform);
            go.name = name;
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var type = mb.GetIl2CppType().Name;
                if (mb.TryCast<PlayMakerFSM>() != null || type.Contains("Localiz")) Object.DestroyImmediate(mb);
            }
            prepare(go);
            return go;
        }
        finally { Object.Destroy(holder); }
    }

    private static UnityEvent Event(Action a)
    {
        var ev = new UnityEvent();
        ev.AddListener((UnityAction)a);
        return ev;
    }

    private static TMP_Text Text(Transform t) =>
        (t != null ? t.GetComponent<TMP_Text>() : null) ?? throw new Exception($"'{t?.name}' has no text");

    private GameObject BuildRow(GameObject saveQuit, Transform parent, GameObject system)
    {
        GameObject row = null;
        CloneInactive(saveQuit, RowName, go =>
        {
            row = go;
            _rowText = Text(go.transform);
            var origText = Text(saveQuit.transform);
            _rowText.text = RowText;
            _rowText.color = RowNormal;

            // Click area: one text line lower, as wide as the longer label (right-aligned text: same right edge).
            var box = go.GetComponent<BoxCollider>();
            var origBox = saveQuit.GetComponent<BoxCollider>();
            if (box != null && origBox != null)
            {
                var wOld = origText.GetPreferredValues("SAVE & QUIT").x;
                var wNew = origText.GetPreferredValues("QUIT WITHOUT SAVING").x;
                var size = origBox.size;
                var center = origBox.center;
                var width = wOld > 0f ? size.x * wNew / wOld : size.x * 1.7f;
                var right = center.x + size.x / 2f;
                var line = origText.GetPreferredValues("A\nA").y - origText.GetPreferredValues("A").y;
                if (line <= 0f) line = 0.706f;  // SETTINGS -> SAVE & QUIT click areas, one line apart
                box.size = new Vector3(width, size.y, size.z);
                box.center = new Vector3(right - width / 2f, center.y - line, center.z);
            }

            var cs = go.GetComponent<ControllerSelectable>() ?? throw new Exception("SAVE & QUIT has no ControllerSelectable");
            cs.TargetFSM = null;
            if (cs.ExplicitNavigation != null) cs.ExplicitNavigation.UseExplicit = false;
            cs.OnSelect = Event(() => { if (_rowText != null) _rowText.color = RowHover; });
            cs.OnDeselect = Event(() => { if (_rowText != null) _rowText.color = RowNormal; });
            cs.OnUse = Event(OpenDialog);

            go.transform.SetParent(parent, false);
            go.transform.SetSiblingIndex(saveQuit.transform.GetSiblingIndex() + 1);
            go.SetActive(true);

            var sel = system.GetComponent<ControllerSelector>();
            var after = saveQuit.GetComponent<ControllerSelectable>();
            if (sel != null && after != null) sel.Selectables = InsertAfter(sel.Selectables, cs, after);
            else Core.Log.Warning($"[{Name}] QUIT WITHOUT SAVING can't be reached with keyboard/gamepad; mouse only");
        });
        return row;
    }

    private GameObject BuildDialog(GameObject callIt, Transform pause)
    {
        GameObject dialog = null;
        CloneInactive(callIt, DialogName, go =>
        {
            dialog = go;
            var t = go.transform;
            Text(t.Find("call it a day")).text = "QUIT WITHOUT SAVING?";

            // The explanation: the game shows one of two texts; ours uses the plain one.
            var expl = t.Find("Explanations");
            if (expl != null)
                for (int i = 0; i < expl.childCount; i++) expl.GetChild(i).gameObject.SetActive(false);
            var text = expl?.Find("'Explanation text:'");
            if (text != null)
            {
                text.gameObject.SetActive(true);
                Text(text).text = Warning;
            }

            var options = t.Find("Options") ?? throw new Exception("dialog has no Options");
            var yes = options.Find("YES") ?? throw new Exception("dialog has no YES");
            var no = options.Find("NO") ?? throw new Exception("dialog has no NO");
            _yesText = Text(yes);
            _noText = Text(no);
            _yesText.text = "QUIT";
            CenterOver(_yesText, _noText);
            _arrow = t.Find("arrow")?.gameObject;
            if (_arrow != null)
            {
                // Offset between the arrow and the YES row as laid out in the scene (arrow starts next to YES).
                var yesLocal = _arrow.transform.parent.InverseTransformPoint(yes.position);
                _arrowOffset = _arrow.transform.localPosition.y - yesLocal.y;
                _arrow.SetActive(true);
            }

            var yesCs = yes.GetComponent<ControllerSelectable>() ?? throw new Exception("YES has no ControllerSelectable");
            var noCs = no.GetComponent<ControllerSelectable>() ?? throw new Exception("NO has no ControllerSelectable");
            foreach (var cs in new[] { yesCs, noCs })
            {
                cs.TargetFSM = null;
                if (cs.ExplicitNavigation != null) cs.ExplicitNavigation.UseExplicit = false;
            }
            yesCs.OnSelect = Event(() => Select(true));
            noCs.OnSelect = Event(() => Select(false));
            yesCs.OnDeselect = new UnityEvent();
            noCs.OnDeselect = new UnityEvent();
            yesCs.OnUse = Event(Quit);
            noCs.OnUse = Event(CloseDialog);

            var sel = options.GetComponent<ControllerSelector>();
            if (sel != null)
            {
                var arr = new Il2CppReferenceArray<ControllerSelectable>(2);
                arr[0] = yesCs;   // top to bottom, as on screen
                arr[1] = noCs;
                sel.Selectables = arr;
                sel.DefaultSelection = noCs;  // NOT YET is selected when the dialog opens: the safe choice
                sel.DefaultSelectionIsNull = false;
                sel.OnClose = Event(CloseDialog);
            }
            Select(false);

            go.transform.SetParent(pause, false);
            go.transform.localPosition = callIt.transform.localPosition;
            go.SetActive(false);
        });
        return dialog;
    }

    // Both options are right-aligned in equal text boxes; the game's "CALL IT " was about as wide as "NOT YET",
    // so "QUIT" would hug the right. Move it so its centre lines up with NOT YET's, and fit its click area to it.
    private static void CenterOver(TMP_Text yes, TMP_Text no)
    {
        var wYes = yes.GetPreferredValues(yes.text).x;
        var wNo = no.GetPreferredValues(no.text).x;
        if (wYes <= 0f || wNo <= 0f) return;
        var yt = yes.transform;
        var nt = no.transform;
        var p = yt.localPosition;
        var newX = nt.localPosition.x - wNo / 2f + wYes / 2f;
        yt.localPosition = new Vector3(newX, p.y, p.z);

        // The click area moved with the text; keep its right edge on the text's and narrow it to the new label.
        var box = yt.GetComponent<BoxCollider>();
        if (box == null) return;
        var c = box.center;
        var sz = box.size;
        var right = c.x + sz.x / 2f;
        var w = Math.Max(wYes + 0.4f, sz.x * 0.5f);
        box.size = new Vector3(w, sz.y, sz.z);
        box.center = new Vector3(right - w / 2f, c.y, c.z);
    }

    private static Il2CppReferenceArray<T> InsertAfter<T>(Il2CppReferenceArray<T> old, T item, T after)
        where T : Il2CppSystem.Object
    {
        var n = old?.Length ?? 0;
        var arr = new Il2CppReferenceArray<T>(n + 1);
        int j = 0;
        bool done = false;
        for (int i = 0; i < n; i++)
        {
            arr[j++] = old[i];
            if (!done && old[i] != null && old[i].Pointer == after.Pointer) { arr[j++] = item; done = true; }
        }
        if (!done) arr[j] = item;
        return arr;
    }
}
