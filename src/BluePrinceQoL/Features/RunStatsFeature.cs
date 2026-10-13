using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using Il2CppBluePrince.UI;
using Il2CppHutongGames.PlayMaker;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace BluePrinceQoL.Features;

/// <summary>
/// A second, read-only HUD line with stats the game tracks for the current day: rooms in the
/// house, rank reached, drafted room colours, dead ends, digs, items found, unexplored doors and
/// a low-steps warning.
///
/// READ-ONLY: this feature never writes an FSM variable, sends an event, or changes a state.
/// Every lookup uses Find*/Contains (no PlayMaker "Get*" that could create a variable), and the
/// door list is read through the proxy's backing field (no lazy initialisation).
/// </summary>
public class RunStatsFeature : Feature
{
    public override string Name => "RunStats";
    protected override string Description => "Second HUD line with today's run stats (rooms, rank, colours, dead ends, digs, items, doors, steps).";

    // ---- names looked up in the game ----
    private const string GlobalManagerName = "Global Manager";
    private const string GmVarSynka = "SYNKA PROJECT";
    private const string GmVarHud = "HUD";
    private const string GmVarPlanManagement = "PLAN MANAGEMENT";
    private const string GmVarGrid = "THE GRID";
    private const string GmVarSteps = "Steps";
    private const string HudFallbackPath = "__SYSTEM/HUD";
    private const string PlanManagementPath = "__SYSTEM/THE DRAFT/PLAN MANAGEMENT";
    private const string GridPath = "__SYSTEM/THE GRID";
    private const string StepsPath = "__SYSTEM/HUD/Steps";
    private const string RankReachedChild = "Rank Reached";
    private const string DoorListChild = "doorlist";
    private const string DraftScreenName = "DRAFT UI";
    private const string RoomTextPath = "Room Text";
    private const int HouseSize = 45; // 5 columns x 9 ranks, Entrance Hall and Antechamber included

    // Global Manager ints (all reset by the scene reload at the start of each day)
    private const string VRooms = "RoomsInHouse";
    private const string VBlue = "Blue Rooms";        // starts at -1: drafted blueprints = value + 1
    private const string VGreen = "Green Rooms";
    private const string VBed = "Bedrooms";
    private const string VHall = "Hallways";
    private const string VShop = "Shop Rooms";
    private const string VRed = "Redrooms";
    private const string VDead = "Deadend Count";
    private const string VDigs = "Dig count";
    private const string VItems = "Items Found";
    private const string VNew = "New Rooms Drafted";
    private const string PmVarBlack = "Blackprints Today"; // PLAN MANAGEMENT local int
    private const string RankVar = "Rank Reached";
    private const string StepsFsmName = "Steps Manager";
    private const string StepsVar = "STEPS";
    private const string StepsTakenVar = "Total Steps Taken Today";
    private const string DoorLockedVar = "LOCKED";

    // Door Code FSM states in which the door is still closed and leads to an undrafted tile.
    private static readonly HashSet<string> OpenDoorStates = new(StringComparer.Ordinal)
    {
        "Wait", "Hover", "Wait Security", "Lock Check", "Lock Animation", "Locked",
    };

    // Colours from the room engines' TYPE strings.
    private const string ColBlue = "#0093EC", ColGreen = "#52B74F", ColPurple = "#9A6FCF", ColOrange = "#E78F52",
        ColYellow = "#D2BE60", ColRed = "#C20F00", ColBlack = "#6A6E7B", ColWarn = "#FF4A3D";

    // ---- prefs ----
    private MelonPreferences_Entry<string> _toggleKeyPref, _align, _fontPref;
    private MelonPreferences_Entry<bool> _visiblePref, _hideWithGameHud, _hideInMenus, _showWhileDrafting;
    private MelonPreferences_Entry<float> _posX, _posY, _fontScale, _interval;
    private MelonPreferences_Entry<bool> _showRooms, _showRank, _showColors, _tintColors, _showDeadEnds, _showDigs,
        _showItems, _showNewRooms, _showDoors, _doorsAntechamber, _showSteps, _stepsOnlyWhenLow, _showStepsTaken;
    private MelonPreferences_Entry<int> _lowSteps;
    private KeyCode _toggleKey = KeyCode.F10;

    // ---- cached game objects / variables ----
    private PlayMakerFSM _gm;
    private GameObject _synkaGo, _hudGo;
    private Culler _hudCuller;
    private FsmInt _rooms, _blue, _green, _bed, _hall, _shop, _red, _dead, _digs, _items, _newRooms, _black;
    private FsmInt _rank, _steps, _stepsTaken;
    private PlayMakerArrayListProxy _doorList;
    private readonly Dictionary<int, DoorInfo> _doors = new();
    private float _nextResolve, _nextRefresh, _sceneLoadedAt;
    private string _sceneName = "";
    private bool _menuCheckBroken, _warnedGm, _warnedMissing;

    private sealed class DoorInfo
    {
        public PlayMakerFSM Fsm;
        public FsmBool Locked;
        public bool InAntechamber;
    }

    // ---- render state ----
    private bool _show;
    private string _text = "";      // may contain <color> tags when tinting is on
    private string _plain = "";     // same text without tags (IMGUI outline)
    private int _doorsOpen = -1, _doorsLocked;
    private string _sep = "  ·  ";
    private Overlay _overlay;

    protected override void CreatePrefs()
    {
        var p = Core.Prefs;
        _toggleKeyPref = p.CreateEntry($"{Name}_ToggleKey", "F10", "RunStats: toggle key", "UnityEngine.KeyCode name. Shift+key logs diagnostics.");
        _visiblePref = p.CreateEntry($"{Name}_Visible", false, "RunStats: visible", "Current show/hide state (toggled by the key). Hidden by default; press the toggle key to show.");
        _posX = p.CreateEntry($"{Name}_PosX", 0.5f, "RunStats: X position", "Anchor X as a fraction of screen width (0 = left, 1 = right).");
        _posY = p.CreateEntry($"{Name}_PosY", 0.05f, "RunStats: Y position", "Top of text as a fraction of screen height (0 = top). Default sits under the Hud line.");
        _align = p.CreateEntry($"{Name}_Align", "Center", "RunStats: alignment", "Left, Center or Right: which edge of the text sits at PosX.");
        _fontScale = p.CreateEntry($"{Name}_FontScale", 0.022f, "RunStats: font scale", "Font size as a fraction of screen height.");
        _fontPref = p.CreateEntry($"{Name}_Font", "Room Text", "RunStats: font",
            "'Room Text' = same font and outline as the room name on the game HUD; or a game TextMeshPro font asset name; or 'IMGUI' for plain text.");
        _interval = p.CreateEntry($"{Name}_UpdateInterval", 0.25f, "RunStats: update interval", "Seconds between text refreshes.");
        _hideWithGameHud = p.CreateEntry($"{Name}_HideWithGameHud", true, "RunStats: hide when game HUD hidden");
        _hideInMenus = p.CreateEntry($"{Name}_HideInMenus", true, "RunStats: hide in menus");
        _showWhileDrafting = p.CreateEntry($"{Name}_ShowWhileDrafting", true, "RunStats: show while drafting");

        _showRooms = p.CreateEntry($"{Name}_ShowRooms", true, "RunStats: rooms", "'Rooms 23/45' (rooms in the house, Entrance Hall and Antechamber included).");
        _showRank = p.CreateEntry($"{Name}_ShowRank", true, "RunStats: rank reached", "Highest rank entered today (1 = Entrance Hall row, 9 = Antechamber row).");
        _showColors = p.CreateEntry($"{Name}_ShowColors", true, "RunStats: room colours",
            "Drafted rooms by colour: B blue, G green, P purple (bedrooms), O orange (hallways), Y yellow (shops), R red, K black.");
        _tintColors = p.CreateEntry($"{Name}_TintColors", true, "RunStats: tint colour counts", "Draw each colour count in the room colour (TextMeshPro only).");
        _showDeadEnds = p.CreateEntry($"{Name}_ShowDeadEnds", true, "RunStats: dead ends");
        _showDigs = p.CreateEntry($"{Name}_ShowDigs", true, "RunStats: digs");
        _showItems = p.CreateEntry($"{Name}_ShowItems", true, "RunStats: items found", "Special items (tools, keys like the Silver Key, etc.) picked up or bought today.");
        _showNewRooms = p.CreateEntry($"{Name}_ShowNewRooms", false, "RunStats: new rooms", "Rooms drafted today for the first time ever.");
        _showDoors = p.CreateEntry($"{Name}_ShowDoors", true, "RunStats: unexplored doors", "Closed doors that still lead to an empty tile ('(n locked)' counts locked/keycard ones).");
        _doorsAntechamber = p.CreateEntry($"{Name}_DoorsIncludeAntechamber", false, "RunStats: count Antechamber doors",
            "Also count the Antechamber's own doors (only usable from inside the Antechamber).");
        _showSteps = p.CreateEntry($"{Name}_ShowSteps", true, "RunStats: steps");
        _stepsOnlyWhenLow = p.CreateEntry($"{Name}_StepsOnlyWhenLow", true, "RunStats: steps only when low", "Only show steps at or below the threshold (the game HUD shows them anyway).");
        _lowSteps = p.CreateEntry($"{Name}_LowStepsThreshold", 10, "RunStats: low steps threshold", "Steps at or below this are highlighted. 0 = no highlight.");
        _showStepsTaken = p.CreateEntry($"{Name}_ShowStepsTaken", false, "RunStats: steps taken today");

        if (!Enum.TryParse(_toggleKeyPref.Value, true, out _toggleKey))
        {
            Core.Log.Warning($"[RunStats] Unknown toggle key '{_toggleKeyPref.Value}', using F10");
            _toggleKey = KeyCode.F10;
        }
    }

    public override void OnSceneLoaded(string sceneName)
    {
        _sceneName = sceneName ?? "";
        _sceneLoadedAt = Time.unscaledTime;
        _nextResolve = 0f;
        _gm = null;
        _synkaGo = _hudGo = null;
        _hudCuller = null;
        _rooms = _blue = _green = _bed = _hall = _shop = _red = _dead = _digs = _items = _newRooms = _black = null;
        _rank = _steps = _stepsTaken = null;
        _doorList = null;
        _doors.Clear();
        _show = false;
    }

    public override void OnUpdate()
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

        _show = ComputeVisible();
        if (_show && Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + Mathf.Clamp(_interval.Value, 0.05f, 5f);
            Refresh();
        }
        if (_show && _text.Length == 0) _show = false;

        _overlay ??= new Overlay(this);
        _overlay.Sync();
    }

    public override void OnGUI()
    {
        _overlay?.DrawImgui();
    }

    // ------------------------------------------------------------------ visibility

    private bool ComputeVisible()
    {
        if (!_visiblePref.Value) return false;

        if (_gm == null || _synkaGo == null)
        {
            if (Time.unscaledTime < _nextResolve) return false;
            _nextResolve = Time.unscaledTime + 1f;
            Resolve();
            if (_gm == null || _synkaGo == null) return false;
        }

        if (!_synkaGo.activeInHierarchy) return false; // player doesn't have control yet
        if (_hideWithGameHud.Value && GameHudHidden()) return false;
        if (_hideInMenus.Value && MenuOpen()) return false;
        return true;
    }

    private bool GameHudHidden()
    {
        if (_hudGo == null) return false;
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
                if (s != null && s.gameObject.name != DraftScreenName) return true;
            }
            return false;
        }
        catch (Exception e)
        {
            _menuCheckBroken = true;
            Core.Log.Warning($"[RunStats] Menu detection failed, disabling it: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ lookups

    private void Resolve()
    {
        _gm = GetFsm(GameObject.Find(GlobalManagerName), "FSM");
        if (_gm == null)
        {
            if (!_warnedGm && _sceneName != "Main Menu" && Time.unscaledTime - _sceneLoadedAt > 30f)
            {
                _warnedGm = true;
                Core.Log.Warning($"[RunStats] '{GlobalManagerName}' FSM not found in scene '{_sceneName}'.");
            }
            return;
        }

        _synkaGo = GoVar(_gm, GmVarSynka) ?? FindIncludingInactive("SYNKA PROJECT");
        _hudGo = GoVar(_gm, GmVarHud) ?? GameObject.Find(HudFallbackPath);
        _hudCuller = _hudGo != null ? _hudGo.GetComponent<Culler>() : null;

        _rooms = IntVar(_gm, VRooms);
        _blue = IntVar(_gm, VBlue);
        _green = IntVar(_gm, VGreen);
        _bed = IntVar(_gm, VBed);
        _hall = IntVar(_gm, VHall);
        _shop = IntVar(_gm, VShop);
        _red = IntVar(_gm, VRed);
        _dead = IntVar(_gm, VDead);
        _digs = IntVar(_gm, VDigs);
        _items = IntVar(_gm, VItems);
        _newRooms = IntVar(_gm, VNew);

        var pmGo = GoVar(_gm, GmVarPlanManagement) ?? GameObject.Find(PlanManagementPath);
        _black = IntVar(GetFsm(pmGo, "FSM"), PmVarBlack);
        _doorList = null;
        var dl = pmGo != null ? pmGo.transform.Find(DoorListChild) : null;
        if (dl != null)
            foreach (var proxy in dl.GetComponents<PlayMakerArrayListProxy>())
                if (proxy != null && (proxy.referenceName == DoorListChild || _doorList == null)) _doorList = proxy;

        var gridGo = GoVar(_gm, GmVarGrid) ?? GameObject.Find(GridPath);
        var rankT = gridGo != null ? gridGo.transform.Find(RankReachedChild) : null;
        _rank = IntVar(GetFsm(rankT != null ? rankT.gameObject : null, "FSM"), RankVar);

        var stepsGo = GoVar(_gm, GmVarSteps) ?? GameObject.Find(StepsPath);
        var stepsFsm = GetFsm(stepsGo, StepsFsmName);
        _steps = IntVar(stepsFsm, StepsVar);
        _stepsTaken = IntVar(stepsFsm, StepsTakenVar);

        if (!_warnedMissing)
        {
            var missing = new List<string>();
            if (_synkaGo == null) missing.Add(GmVarSynka);
            if (_rooms == null) missing.Add(VRooms);
            if (_blue == null || _green == null || _bed == null || _hall == null || _shop == null || _red == null) missing.Add("colour counts");
            if (_black == null) missing.Add(PmVarBlack);
            if (_dead == null) missing.Add(VDead);
            if (_digs == null) missing.Add(VDigs);
            if (_items == null) missing.Add(VItems);
            if (_rank == null) missing.Add(RankVar);
            if (_steps == null) missing.Add(StepsVar);
            if (_doorList == null) missing.Add("doorlist");
            if (missing.Count > 0)
            {
                _warnedMissing = true;
                Core.Log.Warning("[RunStats] not found (those stats are left out): " + string.Join(", ", missing));
            }
        }
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

    private static FsmInt IntVar(PlayMakerFSM fsm, string name) => Has(fsm, name) ? fsm.FsmVariables.FindFsmInt(name) : null;
    private static FsmBool BoolVar(PlayMakerFSM fsm, string name) => Has(fsm, name) ? fsm.FsmVariables.FindFsmBool(name) : null;
    private static GameObject GoVar(PlayMakerFSM fsm, string name)
    {
        if (!Has(fsm, name)) return null;
        var v = fsm.FsmVariables.FindFsmGameObject(name);
        var go = v != null ? v.Value : null;
        return go != null ? go : null; // normalise destroyed objects to a real null for ??
    }

    private static GameObject FindIncludingInactive(string name)
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t != null && t.name == name && t.gameObject.scene.IsValid())
                return t.gameObject;
        return null;
    }

    // ------------------------------------------------------------------ stats

    private static int? Val(FsmInt v) => v != null ? v.Value : null;

    private void Refresh()
    {
        if (_showDoors.Value) CountDoors(); else _doorsOpen = -1;

        bool tint = _tintColors.Value; // both TMP and the IMGUI fallback render <color> tags
        var sb = new StringBuilder(160);
        var plain = new StringBuilder(160);

        void Part(string rich, string flat)
        {
            if (sb.Length > 0) { sb.Append(_sep); plain.Append(_sep); }
            sb.Append(rich);
            plain.Append(flat);
        }
        void Simple(string s) => Part(s, s);

        if (_showRooms.Value && _rooms != null)
            Simple($"Rooms {_rooms.Value}/{HouseSize}");

        if (_showRank.Value && _rank != null && _rank.Value > 0)
            Simple($"Rank {_rank.Value}");

        if (_showColors.Value)
        {
            var rich = new StringBuilder(96);
            var flat = new StringBuilder(48);
            void Col(string letter, int? n, string hex)
            {
                if (n == null) return;
                if (flat.Length > 0) { rich.Append(' '); flat.Append(' '); }
                int v = Math.Max(0, n.Value);
                if (tint) rich.Append("<color=").Append(hex).Append('>').Append(letter).Append(v).Append("</color>");
                else rich.Append(letter).Append(v);
                flat.Append(letter).Append(v);
            }
            Col("B", _blue != null ? _blue.Value + 1 : null, ColBlue);
            Col("G", Val(_green), ColGreen);
            Col("P", Val(_bed), ColPurple);
            Col("O", Val(_hall), ColOrange);
            Col("Y", Val(_shop), ColYellow);
            Col("R", Val(_red), ColRed);
            Col("K", Val(_black), ColBlack);
            if (flat.Length > 0) Part(rich.ToString(), flat.ToString());
        }

        if (_showDeadEnds.Value && _dead != null) Simple($"Dead ends {_dead.Value}");
        if (_showDigs.Value && _digs != null) Simple($"Digs {_digs.Value}");
        if (_showItems.Value && _items != null) Simple($"Items {_items.Value}");
        if (_showNewRooms.Value && _newRooms != null) Simple($"New {_newRooms.Value}");

        if (_showDoors.Value && _doorsOpen >= 0)
            Simple(_doorsLocked > 0 ? $"Doors {_doorsOpen} ({_doorsLocked} locked)" : $"Doors {_doorsOpen}");

        if (_showStepsTaken.Value && _stepsTaken != null) Simple($"Walked {_stepsTaken.Value}");

        if (_showSteps.Value && _steps != null)
        {
            int steps = _steps.Value, threshold = _lowSteps.Value;
            bool low = threshold > 0 && steps <= threshold;
            if (low || !_stepsOnlyWhenLow.Value)
            {
                string s = $"Steps {steps}";
                if (!low) Simple(s);
                else Part($"<color={ColWarn}>{s}!</color>", s + "!");
            }
        }

        _text = sb.ToString();
        _plain = plain.ToString();
    }

    /// <summary>
    /// Every Door Code FSM adds its own GameObject to PLAN MANAGEMENT/doorlist when it starts.
    /// A door is "unexplored" while its FSM idles in Wait/Hover/Wait Security/Locked: it leaves
    /// those states for good once opened (State 7/9) or once its target tile gets a room
    /// (autodestruct, or "Open z Door"). Destroyed doors stay in the list as dead references.
    /// </summary>
    private void CountDoors()
    {
        _doorsOpen = -1;
        _doorsLocked = 0;
        var list = _doorList != null ? _doorList._arrayList : null;
        if (list == null) return;

        int open = 0, locked = 0;
        bool includeAnte = _doorsAntechamber.Value;
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            var obj = list[i];
            if (obj == null) continue;
            var go = obj.TryCast<GameObject>();
            if (go == null) continue; // also catches destroyed doors (Unity null)

            int id = go.GetInstanceID();
            if (!_doors.TryGetValue(id, out var info))
            {
                var fsm = GetFsm(go, "FSM");
                info = new DoorInfo { Fsm = fsm, Locked = BoolVar(fsm, DoorLockedVar), InAntechamber = HasAncestorNamed(go.transform, "Antechamber") };
                _doors[id] = info;
            }
            if (info.Fsm == null) continue;
            if (info.InAntechamber && !includeAnte) continue;

            var state = info.Fsm.ActiveStateName;
            if (state == null || !OpenDoorStates.Contains(state)) continue;
            open++;
            if (info.Locked != null && info.Locked.Value) locked++;
        }
        _doorsOpen = open;
        _doorsLocked = locked;
    }

    private static bool HasAncestorNamed(Transform t, string part)
    {
        for (var p = t; p != null; p = p.parent)
            if (p.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // ------------------------------------------------------------------ diagnostics

    private void LogDiagnostics()
    {
        static string S(FsmInt v) => v != null ? v.Value.ToString() : "n/a";
        var sb = new StringBuilder("[RunStats] diagnostics: ");
        sb.Append($"scene='{_sceneName}' visiblePref={_visiblePref.Value} shown={_show} text='{_plain}' | ");
        sb.Append($"GM={_gm != null} synkaActive={(_synkaGo != null && _synkaGo.activeInHierarchy)} ");
        sb.Append($"hudActive={(_hudGo != null ? _hudGo.activeInHierarchy.ToString() : "n/a")} culler={(_hudCuller != null ? _hudCuller.CullingType.ToString() : "n/a")} | ");
        sb.Append($"{VRooms}={S(_rooms)} {VBlue}={S(_blue)} {VGreen}={S(_green)} {VBed}={S(_bed)} {VHall}={S(_hall)} ");
        sb.Append($"{VShop}={S(_shop)} {VRed}={S(_red)} {PmVarBlack}={S(_black)} {VDead}={S(_dead)} {VDigs}={S(_digs)} ");
        sb.Append($"{VItems}={S(_items)} {VNew}={S(_newRooms)} {RankVar}={S(_rank)} {StepsVar}={S(_steps)} {StepsTakenVar}={S(_stepsTaken)} | ");

        try
        {
            var list = _doorList != null ? _doorList._arrayList : null;
            if (list == null) sb.Append("doorlist=n/a");
            else
            {
                var states = new Dictionary<string, int>();
                int dead = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var go = list[i]?.TryCast<GameObject>();
                    if (go == null) { dead++; continue; }
                    var fsm = GetFsm(go, "FSM");
                    string key = (fsm != null ? fsm.ActiveStateName ?? "null" : "no fsm") +
                                 (HasAncestorNamed(go.transform, "Antechamber") ? " (ante)" : "");
                    states[key] = states.TryGetValue(key, out var c) ? c + 1 : 1;
                }
                sb.Append($"doorlist={list.Count} destroyed={dead} states=[");
                foreach (var kv in states) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(", ");
                sb.Append($"] counted={_doorsOpen} locked={_doorsLocked}");
            }
        }
        catch (Exception e) { sb.Append("doorlist error: ").Append(e.Message); }
        Core.Log.Msg(sb.ToString());
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>
    /// Own overlay canvas with a TextMeshPro label in the game font (copied from the game HUD's
    /// "Room Text"), with an IMGUI fallback. Same approach as HudFeature, duplicated on purpose.
    /// </summary>
    private sealed class Overlay
    {
        private readonly RunStatsFeature _f;
        private GameObject _root;
        private TextMeshProUGUI _tmp;
        private bool _gaveUp;
        private int _attempts;
        private float _nextTry;
        private string _shownText;
        private GUIStyle _style;
        private int _styleFontSize = -1;

        public Overlay(RunStatsFeature f) { _f = f; }

        public void Sync()
        {
            if (_tmp == null)
            {
                if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; }
                if (!_f._show || _gaveUp || Time.unscaledTime < _nextTry) return;
                _nextTry = Time.unscaledTime + 1f;
                if (!TryCreate()) return;
            }

            if (!_f._show)
            {
                if (_root.activeSelf) _root.SetActive(false);
                return;
            }
            if (!_root.activeSelf) _root.SetActive(true);

            if (_shownText != _f._text)
            {
                _tmp.text = _f._text;
                _shownText = _f._text;
            }

            _tmp.fontSize = Mathf.Max(8f, Screen.height * Mathf.Clamp(_f._fontScale.Value, 0.005f, 0.2f));
            float pivotX;
            TextAlignmentOptions align;
            switch ((_f._align.Value ?? "").Trim().ToLowerInvariant())
            {
                case "left": pivotX = 0f; align = TextAlignmentOptions.TopLeft; break;
                case "right": pivotX = 1f; align = TextAlignmentOptions.TopRight; break;
                default: pivotX = 0.5f; align = TextAlignmentOptions.Top; break;
            }
            _tmp.alignment = align;
            var rt = _tmp.rectTransform;
            var anchor = new Vector2(Mathf.Clamp01(_f._posX.Value), 1f - Mathf.Clamp01(_f._posY.Value));
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(pivotX, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(Screen.width, _tmp.fontSize * 1.6f);
        }

        private bool TryCreate()
        {
            var want = (_f._fontPref.Value ?? "").Trim();
            if (want.Equals("IMGUI", StringComparison.OrdinalIgnoreCase)) { _gaveUp = true; return false; }

            TMP_FontAsset font = null;
            Material mat = null;
            Color color = new(1f, 0.97f, 0.88f, 1f);

            if (want.Length == 0 || want.Equals(RoomTextPath, StringComparison.OrdinalIgnoreCase))
            {
                var t = _f._hudGo != null ? _f._hudGo.transform.Find(RoomTextPath) : null;
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
                foreach (var fa in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    if (fa != null && fa.name.Equals(want, StringComparison.OrdinalIgnoreCase)) { font = fa; break; }
            }

            if (font == null)
            {
                if (++_attempts >= 30)
                {
                    _gaveUp = true;
                    Core.Log.Warning($"[RunStats] font '{want}' not found; using the plain fallback text");
                }
                return false;
            }

            _root = new GameObject("BluePrinceQoL RunStats");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 501;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(_root.transform, false);
            _tmp = textGo.AddComponent<TextMeshProUGUI>();
            _tmp.font = font;
            if (mat != null) _tmp.fontSharedMaterial = mat;
            _tmp.color = color;
            _tmp.textWrappingMode = TextWrappingModes.NoWrap;
            _tmp.overflowMode = TextOverflowModes.Overflow;
            _tmp.raycastTarget = false;
            _tmp.richText = true;
            _shownText = null;
            if (!font.HasCharacter('·', true)) _f._sep = "  -  ";
            Core.Log.Msg($"[RunStats] using game font '{font.name}'" + (mat != null ? $" with material '{mat.name}'" : ""));
            return true;
        }

        public void DrawImgui()
        {
            if (_tmp != null) return;
            if (!_f._show || !_f._visiblePref.Value || _f._plain.Length == 0) return;
            if (Event.current.type != EventType.Repaint) return;

            int fontSize = Mathf.Max(9, Mathf.RoundToInt(Screen.height * Mathf.Clamp(_f._fontScale.Value, 0.005f, 0.2f)));
            if (_style == null || fontSize != _styleFontSize)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = fontSize,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = false,
                    clipping = TextClipping.Overflow,
                    richText = true,
                };
                _styleFontSize = fontSize;
            }

            var size = _style.CalcSize(new GUIContent(_f._plain));
            float w = size.x, h = size.y;
            float ax = Screen.width * Mathf.Clamp01(_f._posX.Value);
            float y = Screen.height * Mathf.Clamp01(_f._posY.Value);
            float x = (_f._align.Value ?? "").Trim().ToLowerInvariant() switch
            {
                "left" => ax,
                "right" => ax - w,
                _ => ax - w * 0.5f,
            };
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - w));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - h));

            float o = Mathf.Max(1f, fontSize / 14f);
            var rect = new Rect(x, y, w + 2 * o, h + 2 * o);
            _style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        GUI.Label(new Rect(rect.x + dx * o, rect.y + dy * o, rect.width, rect.height), _f._plain, _style);
            _style.normal.textColor = new Color(1f, 0.97f, 0.88f, 1f);
            GUI.Label(rect, _f._text, _style);
        }
    }
}
