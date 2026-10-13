using System;
using System.Globalization;
using System.Text;
using Il2Cpp;
using Il2CppBluePrince.UI;
using Il2CppHutongGames.PlayMaker;
using Il2CppTMPro;
using UnityEngine.UI;
using MelonLoader;
using UnityEngine;

namespace BluePrinceQoL.Features;

/// <summary>
/// Shows the in-game day number, calendar date and time of day on screen.
///
/// Sources:
///  * Time: "Global Manager" FSM var SYNKA PROJECT -> GameObject "DAY/SYNKA PROJECT",
///    FSM "FSM", float TEMP HOUR = hours elapsed since 8:00 AM. This is exactly what every
///    in-world clock reads, so the HUD agrees with the clocks.
///  * Day: "Global Manager" FSM int DAY (written once per day by DAY FSM state "Day Compare";
///    the same number the game shows on its "Day N" title card).
///  * Visibility: the SYNKA PROJECT object only becomes active when the player gets control
///    (DAY FSM state "Mouse On"); the game hides its own HUD via the Culler on "__SYSTEM/HUD"
///    (CullingType = Forced_off); menus/documents/shops register with UIScreen.
/// </summary>
public class HudFeature : Feature
{
    public override string Name => "Hud";
    protected override string Description => "On-screen in-game day, date and time.";

    // ---- names looked up in the game (kept together so updates are easy to patch) ----
    private const string GlobalManagerName = "Global Manager";
    private const string PersistentManagerName = "Global Persitent Manager"; // sic
    private const string GmVarSynka = "SYNKA PROJECT";
    private const string GmVarDay = "DAY";
    private const string GmVarHud = "HUD";
    private const string GmVarDayFsm = "DAY FSM";
    private const string SynkaVarTempHour = "TEMP HOUR";
    private const string SynkaVarClock = "SYNKA CLOCK";
    private const string DayFsmVarDay = "Day";
    private const string PersistentVarDay = "DAY";
    private const string HudFallbackPath = "__SYSTEM/HUD";
    private const string DraftScreenName = "DRAFT UI";
    private const float DayStartHour = 8f; // SYNKA clock 0 == 8:00 AM
    // DAY object's "Dates" ArrayList: index 1 (Day 1) = "SAT NOV 7"; 2020/21 calendar matches all 101 entries.
    private static readonly DateTime DayZeroDate = new(2020, 11, 6);

    // ---- prefs ----
    private MelonPreferences_Entry<string> _toggleKeyPref;
    private MelonPreferences_Entry<bool> _visiblePref;
    private MelonPreferences_Entry<float> _posX, _posY, _fontScale;
    private MelonPreferences_Entry<string> _align;
    private MelonPreferences_Entry<bool> _use24h, _showDate, _hideWithGameHud, _hideInMenus, _showWhileDrafting;
    private MelonPreferences_Entry<string> _fontPref;
    private MelonPreferences_Entry<bool> _showIcons;
    private MelonPreferences_Entry<float> _iconScale;

    private KeyCode _toggleKey = KeyCode.F8;

    // ---- cached game objects / variables (re-resolved after scene loads) ----
    private PlayMakerFSM _gm;
    private GameObject _synkaGo;
    private FsmFloat _tempHour;     // hours since 8 AM
    private FsmFloat _synkaClock;   // fallback: seconds since 8 AM
    private FsmInt _gmDay;
    private FsmInt _dayFsmDay;
    private FsmInt _persistentDay;
    private GameObject _hudGo;
    private Culler _hudCuller;
    private float _nextResolve;
    private float _sceneLoadedAt;
    private string _sceneName = "";
    private bool _menuCheckBroken;
    private bool _triedInactiveFind;

    // warn-once flags
    private bool _warnedGm, _warnedSynka, _warnedTime, _warnedDay, _warnedHud;

    // ---- render state ----
    private bool _show;
    private string _text = "";
    private int _lastDay = int.MinValue, _lastMinute = int.MinValue;
    private bool _lastDateFlag, _last24h;
    private GUIStyle _style;
    private int _styleFontSize = -1;
    private Vector2 _textSize;
    private bool _sizeDirty = true;

    // ---- TextMeshPro rendering (game font); IMGUI above is the fallback ----
    private const string RoomTextPath = "Room Text"; // child of the game HUD: the room name, bottom right
    private GameObject _tmpRoot;
    private TextMeshProUGUI _tmp;
    private bool _tmpGaveUp;
    private int _tmpAttempts;
    private float _tmpNextTry;
    private string _tmpShownText;
    private string _sep = "  ·  "; // swapped for "  -  " if the game font has no middle dot
    private string _dayText = "", _timeText = "", _timeShownText;
    private TextMeshProUGUI _timeTmp;
    private RectTransform _rowRect;
    private HorizontalLayoutGroup _rowLayout;
    private Image _sunImg, _clockImg;
    private LayoutElement _sunLayout, _clockLayout, _gapLayout;
    private GameObject _gap;
    private static Sprite _sunSprite, _clockSprite;

    protected override void CreatePrefs()
    {
        var p = Core.Prefs;
        _toggleKeyPref = p.CreateEntry($"{Name}_ToggleKey", "F8", "Hud: toggle key", "UnityEngine.KeyCode name. Shift+key logs diagnostics.");
        _visiblePref = p.CreateEntry($"{Name}_Visible", true, "Hud: visible", "Current show/hide state (toggled by the key).");
        _posX = p.CreateEntry($"{Name}_PosX", 0.5f, "Hud: X position", "Anchor X as a fraction of screen width (0 = left, 1 = right).");
        _posY = p.CreateEntry($"{Name}_PosY", 0.012f, "Hud: Y position", "Top of text as a fraction of screen height (0 = top).");
        _align = p.CreateEntry($"{Name}_Align", "Center", "Hud: alignment", "Left, Center or Right: which edge of the text sits at PosX.");
        _fontScale = p.CreateEntry($"{Name}_FontScale", 0.03f, "Hud: font scale", "Font size as a fraction of screen height.");
        _use24h = p.CreateEntry($"{Name}_Use24Hour", false, "Hud: 24-hour clock");
        _showDate = p.CreateEntry($"{Name}_ShowDate", true, "Hud: show calendar date", "e.g. 'Sat Nov 7' (Day 1 = Nov 7).");
        _hideWithGameHud = p.CreateEntry($"{Name}_HideWithGameHud", true, "Hud: hide when game HUD hidden", "Hide when the game hides its own steps/gems HUD (cutscenes, documents, end of day).");
        _hideInMenus = p.CreateEntry($"{Name}_HideInMenus", true, "Hud: hide in menus", "Hide while a game menu/screen is open (pause, shops, documents...).");
        _showWhileDrafting = p.CreateEntry($"{Name}_ShowWhileDrafting", true, "Hud: show while drafting", "Keep showing while the room draft screen is the only open screen.");
        _fontPref = p.CreateEntry($"{Name}_Font", "Room Text", "Hud: font",
            "'Room Text' = same font and outline as the room name on the game HUD; or a game TextMeshPro font asset name " +
            "(e.g. 'VersicaSerif SDF', 'Rank Font', 'Versica SDF outline'); or 'IMGUI' for the plain fallback text.");
        _showIcons = p.CreateEntry($"{Name}_ShowIcons", true, "Hud: show icons", "Sun before the day, clock before the time (game-font mode only).");
        _iconScale = p.CreateEntry($"{Name}_IconScale", 1.15f, "Hud: icon size", "Icon size relative to the font size.");

        if (!Enum.TryParse(_toggleKeyPref.Value, true, out _toggleKey))
        {
            Core.Log.Warning($"[Hud] Unknown toggle key '{_toggleKeyPref.Value}', using F8");
            _toggleKey = KeyCode.F8;
        }
    }

    public override void OnSceneLoaded(string sceneName)
    {
        _sceneName = sceneName ?? "";
        ClearCache();
        _sceneLoadedAt = Time.unscaledTime;
        _nextResolve = 0f;
    }

    private void ClearCache()
    {
        _gm = null;
        _synkaGo = null;
        _tempHour = _synkaClock = null;
        _gmDay = _dayFsmDay = _persistentDay = null;
        _hudGo = null;
        _hudCuller = null;
        _show = false;
        _triedInactiveFind = false;
    }

    public override void OnUpdate()
    {
        UpdateState();
        SyncTmp();
    }

    private void UpdateState()
    {
        if (Input.GetKeyDown(_toggleKey))
        {
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                LogDiagnostics();
            else
            {
                _visiblePref.Value = !_visiblePref.Value;
                MelonPreferences.Save();
            }
        }

        _show = false;
        if (!_visiblePref.Value) return;

        // Unity-null check also catches objects destroyed by a scene reload.
        if (_gm == null || _synkaGo == null)
        {
            if (Time.unscaledTime < _nextResolve) return;
            _nextResolve = Time.unscaledTime + 1f;
            Resolve();
            if (_gm == null || _synkaGo == null) return;
        }

        // The clock object is only switched on once the player has control for the day.
        if (!_synkaGo.activeInHierarchy) return;
        if (_hideWithGameHud.Value && GameHudHidden()) return;
        if (_hideInMenus.Value && MenuOpen()) return;

        float hoursSince8 = _tempHour != null ? _tempHour.Value
            : _synkaClock != null ? _synkaClock.Value / 3600f
            : float.NaN;
        int day = CurrentDay();

        UpdateText(day, hoursSince8);
        _show = _text.Length > 0;
    }

    public override void OnGUI()
    {
        if (_tmp != null) return; // drawn by TextMeshPro
        if (!_show || !_visiblePref.Value) return;
        if (Event.current.type != EventType.Repaint) return;

        int fontSize = Mathf.Max(10, Mathf.RoundToInt(Screen.height * Mathf.Clamp(_fontScale.Value, 0.005f, 0.2f)));
        if (_style == null || fontSize != _styleFontSize)
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                clipping = TextClipping.Overflow,
            };
            _styleFontSize = fontSize;
            _sizeDirty = true;
        }
        if (_sizeDirty)
        {
            _textSize = _style.CalcSize(new GUIContent(_text));
            _sizeDirty = false;
        }

        float w = _textSize.x, h = _textSize.y;
        float ax = Screen.width * Mathf.Clamp01(_posX.Value);
        float y = Screen.height * Mathf.Clamp01(_posY.Value);
        float x = (_align.Value ?? "").Trim().ToLowerInvariant() switch
        {
            "left" => ax,
            "right" => ax - w,
            _ => ax - w * 0.5f,
        };
        // keep it on screen
        x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - w));
        y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - h));

        float o = Mathf.Max(1f, fontSize / 14f);
        var rect = new Rect(x, y, w + 2 * o, h + 2 * o);

        _style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    GUI.Label(new Rect(rect.x + dx * o, rect.y + dy * o, rect.width, rect.height), _text, _style);
        _style.normal.textColor = new Color(1f, 0.97f, 0.88f, 1f);
        GUI.Label(rect, _text, _style);
    }

    // ------------------------------------------------------------------ TextMeshPro

    private void SyncTmp()
    {
        if (_tmp == null)
        {
            if (!_show || _tmpGaveUp || Time.unscaledTime < _tmpNextTry) return;
            _tmpNextTry = Time.unscaledTime + 1f;
            if (!TryCreateTmp()) return;
        }

        if (!_show)
        {
            if (_tmpRoot.activeSelf) _tmpRoot.SetActive(false);
            return;
        }
        if (!_tmpRoot.activeSelf) _tmpRoot.SetActive(true);

        bool icons = _showIcons.Value && _sunSprite != null && _clockSprite != null;
        // With icons: [sun] day/date  [clock] time. Without: one label with separators.
        string dayText = icons ? _dayText : _text;
        string timeText = icons ? _timeText : "";
        if (_tmpShownText != dayText) { _tmp.text = dayText; _tmpShownText = dayText; }
        if (_timeShownText != timeText) { _timeTmp.text = timeText; _timeShownText = timeText; }
        SetActive(_sunImg.gameObject, icons && dayText.Length > 0);
        SetActive(_tmp.gameObject, dayText.Length > 0);
        SetActive(_gap, icons && dayText.Length > 0 && timeText.Length > 0);
        SetActive(_clockImg.gameObject, icons && timeText.Length > 0);
        SetActive(_timeTmp.gameObject, timeText.Length > 0);

        float fontSize = Mathf.Max(10f, Screen.height * Mathf.Clamp(_fontScale.Value, 0.005f, 0.2f));
        if (!Mathf.Approximately(fontSize, _tmp.fontSize))
        {
            _tmp.fontSize = fontSize;
            _timeTmp.fontSize = fontSize;
            float icon = fontSize * Mathf.Clamp(_iconScale.Value, 0.3f, 3f);
            foreach (var le in new[] { _sunLayout, _clockLayout })
            {
                le.preferredWidth = icon;
                le.preferredHeight = icon;
            }
            _gapLayout.preferredWidth = fontSize * 0.6f;
            _rowLayout.spacing = fontSize * 0.25f;
        }

        float pivotX = (_align.Value ?? "").Trim().ToLowerInvariant() switch
        {
            "left" => 0f,
            "right" => 1f,
            _ => 0.5f,
        };
        var rt = _rowRect;
        var anchor = new Vector2(Mathf.Clamp01(_posX.Value), 1f - Mathf.Clamp01(_posY.Value));
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = new Vector2(pivotX, 1f);
        rt.anchoredPosition = Vector2.zero;
    }

    private static void SetActive(GameObject go, bool on)
    {
        if (go.activeSelf != on) go.SetActive(on);
    }

    /// <summary>Builds an overlay canvas with the HUD row in a game font. False = retry later.</summary>
    private bool TryCreateTmp()
    {
        var want = (_fontPref.Value ?? "").Trim();
        if (want.Equals("IMGUI", StringComparison.OrdinalIgnoreCase)) { _tmpGaveUp = true; return false; }

        TMP_FontAsset font = null;
        Material mat = null;
        Color color = new(1f, 0.97f, 0.88f, 1f);

        if (want.Length == 0 || want.Equals(RoomTextPath, StringComparison.OrdinalIgnoreCase))
        {
            // Copy the room-name label's look (font, outline material, colour).
            var t = _hudGo != null ? _hudGo.transform.Find(RoomTextPath) : null;
            var src = t != null ? t.GetComponent<TMP_Text>() : null;
            if (src != null && src.font != null)
            {
                font = src.font;
                mat = src.fontSharedMaterial;
                color = src.color;
            }
        }
        else
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (f != null && f.name.Equals(want, StringComparison.OrdinalIgnoreCase)) { font = f; break; }
        }

        if (font == null)
        {
            // The HUD and its fonts appear once a day is loaded; give it a while before falling back.
            if (++_tmpAttempts >= 30)
            {
                _tmpGaveUp = true;
                Core.Log.Warning($"[Hud] font '{want}' not found; using the plain fallback text");
            }
            return false;
        }

        _sunSprite ??= LoadIcon("sun_icon.rgba");
        _clockSprite ??= LoadIcon("clock_icon.rgba");

        _tmpRoot = new GameObject("BluePrinceQoL HUD");
        UnityEngine.Object.DontDestroyOnLoad(_tmpRoot);
        var canvas = _tmpRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;

        // Row sized to its contents; its pivot does the left/centre/right alignment.
        var row = new GameObject("Row");
        row.transform.SetParent(_tmpRoot.transform, false);
        _rowRect = row.AddComponent<RectTransform>();
        _rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        _rowLayout.childAlignment = TextAnchor.MiddleCenter;
        _rowLayout.childControlWidth = true;
        _rowLayout.childControlHeight = true;
        _rowLayout.childForceExpandWidth = false;
        _rowLayout.childForceExpandHeight = false;
        var fit = row.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _sunImg = MakeIcon(row, "Sun", _sunSprite, out _sunLayout);
        _tmp = MakeText(row, "Day", font, mat, color);
        _gap = new GameObject("Gap");
        _gap.transform.SetParent(row.transform, false);
        _gap.AddComponent<RectTransform>();
        _gapLayout = _gap.AddComponent<LayoutElement>();
        _clockImg = MakeIcon(row, "Clock", _clockSprite, out _clockLayout);
        _timeTmp = MakeText(row, "Time", font, mat, color);

        _tmpShownText = _timeShownText = null;
        if (!font.HasCharacter('\u00B7', true)) _sep = "  -  ";
        _lastDay = int.MinValue; // rebuild the text with the new separator
        Core.Log.Msg($"[Hud] using game font '{font.name}'" + (mat != null ? $" with material '{mat.name}'" : "") +
                     (_sunSprite != null && _clockSprite != null ? ", with icons" : ", icons unavailable"));
        return true;
    }

    private static TextMeshProUGUI MakeText(GameObject row, string name, TMP_FontAsset font, Material mat, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(row.transform, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = font;
        if (mat != null) t.fontSharedMaterial = mat;
        t.color = color;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Overflow;
        t.alignment = TextAlignmentOptions.Midline;
        t.raycastTarget = false;
        t.richText = false;
        return t;
    }

    private static Image MakeIcon(GameObject row, string name, Sprite sprite, out LayoutElement layout)
    {
        var go = new GameObject(name);
        go.transform.SetParent(row.transform, false);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.preserveAspect = true;
        img.raycastTarget = false;
        layout = go.AddComponent<LayoutElement>();
        return img;
    }

    /// <summary>
    /// Icon embedded by the build (tools/png2rgba.py): 8-byte header (width, height)
    /// then RGBA32 rows bottom-up. Loaded via SetPixels32 so Unity builds mipmaps
    /// (smooth when scaled down to text height). Null if missing.
    /// </summary>
    private static Sprite LoadIcon(string resource)
    {
        try
        {
            using var s = typeof(HudFeature).Assembly.GetManifestResourceStream(resource);
            if (s == null) return null;
            var buf = new byte[s.Length];
            int read = 0;
            while (read < buf.Length)
            {
                int n = s.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            int w = BitConverter.ToInt32(buf, 0), h = BitConverter.ToInt32(buf, 4);
            if (w <= 0 || h <= 0 || buf.Length < 8 + w * h * 4) return null;

            var px = new Color32[w * h];
            for (int i = 0, o = 8; i < px.Length; i++, o += 4)
                px[i] = new Color32(buf[o], buf[o + 1], buf[o + 2], buf[o + 3]);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.SetPixels32(px);
            tex.Apply(true, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            // The game periodically unloads unused assets; keep ours.
            tex.hideFlags = HideFlags.HideAndDontSave;
            var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
        catch (Exception e)
        {
            Core.Log.Warning($"[Hud] could not load icon '{resource}': {e.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ text

    private void UpdateText(int day, float hoursSince8)
    {
        int minuteOfDay = int.MinValue;
        if (!float.IsNaN(hoursSince8) && !float.IsInfinity(hoursSince8))
        {
            // Same as the clock faces: hour hand = TEMP HOUR * 30 deg, wraps every 12/24 h.
            double total = Math.Floor((DayStartHour + hoursSince8) * 60.0);
            minuteOfDay = (int)(((long)total % 1440 + 1440) % 1440);
        }

        bool showDate = _showDate.Value, use24 = _use24h.Value;
        if (day == _lastDay && minuteOfDay == _lastMinute && showDate == _lastDateFlag && use24 == _last24h)
            return;
        _lastDay = day; _lastMinute = minuteOfDay; _lastDateFlag = showDate; _last24h = use24;

        var sb = new StringBuilder(32);
        if (day > 0)
        {
            sb.Append("Day ").Append(day);
            if (showDate)
                sb.Append(_sep).Append(DayZeroDate.AddDays(day).ToString("ddd MMM d", CultureInfo.InvariantCulture));
        }
        _dayText = sb.ToString();

        sb.Clear();
        if (minuteOfDay != int.MinValue)
        {
            int hh = minuteOfDay / 60, mm = minuteOfDay % 60;
            if (use24)
                sb.Append(hh.ToString("00")).Append(':').Append(mm.ToString("00"));
            else
                sb.Append(hh % 12 == 0 ? 12 : hh % 12).Append(':').Append(mm.ToString("00"))
                  .Append(hh < 12 ? " AM" : " PM");
        }
        _timeText = sb.ToString();

        _text = _dayText.Length > 0 && _timeText.Length > 0 ? _dayText + _sep + _timeText : _dayText + _timeText;
        _sizeDirty = true;
    }

    private int CurrentDay()
    {
        // Global Manager DAY is set once per day in DAY FSM "Day Compare" and is what the
        // "Day N" title card shows. The others are fallbacks (all equal during play, except
        // the persistent one, which the save system may decrement at end of day).
        if (_gmDay != null && _gmDay.Value > 0) return _gmDay.Value;
        if (_dayFsmDay != null && _dayFsmDay.Value > 0) return _dayFsmDay.Value;
        if (_persistentDay != null && _persistentDay.Value > 0) return _persistentDay.Value;
        return 0;
    }

    // ------------------------------------------------------------------ visibility

    private bool GameHudHidden()
    {
        if (_hudGo == null) return false; // unknown -> don't hide
        if (!_hudGo.activeInHierarchy) return true;
        return _hudCuller != null && _hudCuller.CullingType == CullingState.Forced_off;
    }

    private bool MenuOpen()
    {
        if (_menuCheckBroken) return false;
        try
        {
            if (!UIScreen.AnyOpened) return false;
            if (!_showWhileDrafting.Value) return true;

            var mgr = BluePrinceUIManager.Active;
            var open = mgr != null ? mgr.OpenedScreens : null;
            if (open == null) return true;
            for (int i = 0; i < open.Count; i++)
            {
                var s = open[i];
                if (s == null) continue;
                if (s.gameObject.name != DraftScreenName) return true;
            }
            return false;
        }
        catch (Exception e)
        {
            _menuCheckBroken = true;
            Core.Log.Warning($"[Hud] Menu detection (UIScreen/BluePrinceUIManager) failed, disabling it: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ lookups

    private void Resolve()
    {
        var gmGo = GameObject.Find(GlobalManagerName);
        _gm = GetFsm(gmGo, "FSM");
        if (_gm == null)
        {
            if (!_warnedGm && _sceneName != "Main Menu" && Time.unscaledTime - _sceneLoadedAt > 30f)
            {
                _warnedGm = true;
                Core.Log.Warning($"[Hud] '{GlobalManagerName}' FSM not found in scene '{_sceneName}'; HUD disabled until it appears.");
            }
            return;
        }

        // --- clock ---
        _synkaGo = GoVar(_gm, GmVarSynka);
        if (_synkaGo == null && !_triedInactiveFind)
        {
            _triedInactiveFind = true; // expensive scan: once per scene
            _synkaGo = FindIncludingInactive("SYNKA PROJECT");
        }
        var synka = GetFsm(_synkaGo, "FSM");
        if (synka == null)
        {
            WarnOnce(ref _warnedSynka, $"'{GmVarSynka}' clock object/FSM not found (Global Manager var '{GmVarSynka}').");
            _synkaGo = null;
        }
        else
        {
            _tempHour = FloatVar(synka, SynkaVarTempHour);
            _synkaClock = FloatVar(synka, SynkaVarClock);
            if (_tempHour == null && _synkaClock == null)
                WarnOnce(ref _warnedTime, $"Clock FSM has neither '{SynkaVarTempHour}' nor '{SynkaVarClock}'; time hidden.");
        }

        // --- day ---
        _gmDay = IntVar(_gm, GmVarDay);
        _dayFsmDay = IntVar(GetFsm(GoVar(_gm, GmVarDayFsm), "FSM"), DayFsmVarDay);
        _persistentDay = IntVar(GetFsm(GameObject.Find(PersistentManagerName), "FSM"), PersistentVarDay);
        if (_gmDay == null)
            WarnOnce(ref _warnedDay, $"Global Manager int '{GmVarDay}' not found; falling back to " +
                                     (_dayFsmDay != null ? "DAY FSM 'Day'." : _persistentDay != null ? "persistent DAY." : "nothing (day hidden)."));

        // --- game HUD (for visibility) ---
        _hudGo = GoVar(_gm, GmVarHud);
        if (_hudGo == null) _hudGo = GameObject.Find(HudFallbackPath);
        _hudCuller = _hudGo != null ? _hudGo.GetComponent<Culler>() : null;
        if (_hudGo == null || _hudCuller == null)
            WarnOnce(ref _warnedHud, $"Game HUD object/Culler not found (Global Manager var '{GmVarHud}'); 'hide with game HUD' only partially works.");
    }

    private static void WarnOnce(ref bool flag, string msg)
    {
        if (flag) return;
        flag = true;
        Core.Log.Warning("[Hud] " + msg);
    }

    private static PlayMakerFSM GetFsm(GameObject go, string fsmName)
    {
        if (go == null) return null;
        foreach (var f in go.GetComponents<PlayMakerFSM>())
            if (f != null && f.FsmName == fsmName) return f;
        return null;
    }

    private static bool Has(PlayMakerFSM fsm, string name) =>
        fsm != null && fsm.FsmVariables != null && fsm.FsmVariables.Contains(name);

    private static FsmFloat FloatVar(PlayMakerFSM fsm, string name) => Has(fsm, name) ? fsm.FsmVariables.GetFsmFloat(name) : null;
    private static FsmInt IntVar(PlayMakerFSM fsm, string name) => Has(fsm, name) ? fsm.FsmVariables.GetFsmInt(name) : null;
    private static GameObject GoVar(PlayMakerFSM fsm, string name) => Has(fsm, name) ? fsm.FsmVariables.GetFsmGameObject(name)?.Value : null;

    /// <summary>GameObject.Find skips inactive objects; the clock object starts inactive.</summary>
    private static GameObject FindIncludingInactive(string name)
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t != null && t.name == name && t.gameObject.scene.IsValid())
                return t.gameObject;
        return null;
    }

    // ------------------------------------------------------------------ diagnostics

    private void LogDiagnostics()
    {
        var sb = new StringBuilder("[Hud] diagnostics: ");
        sb.Append($"scene='{_sceneName}' visiblePref={_visiblePref.Value} shown={_show} text='{_text}' | ");
        sb.Append($"GM={(_gm != null)} synkaGo={(_synkaGo != null)} synkaActive={(_synkaGo != null && _synkaGo.activeInHierarchy)} | ");
        sb.Append($"TEMP HOUR={(_tempHour != null ? _tempHour.Value.ToString("0.000") : "n/a")} ");
        sb.Append($"SYNKA CLOCK={(_synkaClock != null ? _synkaClock.Value.ToString("0.0") : "n/a")} | ");
        sb.Append($"day GM={(_gmDay?.Value.ToString() ?? "n/a")} DAYFSM={(_dayFsmDay?.Value.ToString() ?? "n/a")} persistent={(_persistentDay?.Value.ToString() ?? "n/a")} | ");
        sb.Append($"hudActive={(_hudGo != null ? _hudGo.activeInHierarchy.ToString() : "n/a")} culler={(_hudCuller != null ? _hudCuller.CullingType.ToString() : "n/a")} | ");
        try
        {
            sb.Append($"UIScreen.AnyOpened={UIScreen.AnyOpened} open=[");
            var open = BluePrinceUIManager.Active != null ? BluePrinceUIManager.Active.OpenedScreens : null;
            if (open != null)
                for (int i = 0; i < open.Count; i++)
                    sb.Append(open[i] != null ? open[i].gameObject.name : "null").Append(i < open.Count - 1 ? ", " : "");
            sb.Append(']');
        }
        catch (Exception e) { sb.Append("menu check error: ").Append(e.Message); }
        Core.Log.Msg(sb.ToString());
    }
}
