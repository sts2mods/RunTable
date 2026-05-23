// RunTableScreen — secondary menu showing every run as a row in a sortable
// scroll table. Reuses NCardLibrary as a host (same sidebar geometry,
// same Back-button flow) and reuses RunCardBuilder to draw rows, so the
// visual language matches the Run Table's modal exactly.
//
// Filter state is shared with the Run Table through RunFilterState —
// flipping "Win = true" here also restricts which badges show up there.
// Sort state lives on the view (it's table-local; the Run Table has
// no concept of "sort by gold").
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using TierFilter = RunTable.RunFilterState.TierMode;

namespace RunTable;

public static class RunTableScreen
{
    private const string MetaKey = "RunTableScreen";

    // Pool name → character id (same mapping the card library's PoolFilters use).
    public static readonly Dictionary<string, string> PoolFilterToCharId = new()
    {
        ["IroncladPool"]    = "CHARACTER.IRONCLAD",
        ["SilentPool"]      = "CHARACTER.SILENT",
        ["DefectPool"]      = "CHARACTER.DEFECT",
        ["RegentPool"]      = "CHARACTER.REGENT",
        ["NecrobinderPool"] = "CHARACTER.NECROBINDER",
    };

    public static NCardLibrary? CreateAndPush(NSubmenuStack stack)
    {
        var inst = NCardLibrary.Create();
        if (inst == null) { GD.PrintErr($"{RunTableMod.LogPrefix}RunTable NCardLibrary.Create returned null."); return null; }
        inst.Name = "RunTableScreen";
        inst.SetMeta(MetaKey, true);
        inst.Visible = false;
        stack.AddChild(inst);
        stack.Push(inst);
        GD.Print($"{RunTableMod.LogPrefix}Pushed Run Table.");
        return inst;
    }

    public static bool IsRunTable(Node n) =>
        n.HasMeta(MetaKey) && n.GetMeta(MetaKey).AsBool();

    // ─── persistent run-card cache ─────────────────────────────────────────
    // Built once when the Compendium submenu is opened (in chunks across
    // frames, so the menu itself opens instantly), then reused across all
    // RunTable views. Holds Godot Controls; RemoveChild detaches without
    // freeing, so cards survive view destruction. Click-callback is
    // late-bound via OnRunCardClick (set per active view).
    private static readonly Dictionary<string, Control> _cardCache = new();
    private static bool _cacheBuilding;
    private static int _cacheBuildIndex;
    private static List<RunRecord>? _cacheBuildOrder;

    public static Action<string>? OnRunCardClick;
    public static int  CacheCount    => _cardCache.Count;
    public static bool CacheBuilding => _cacheBuilding;

    public static Control? GetCachedCard(string fileName)
    {
        if (!_cardCache.TryGetValue(fileName, out var c)) return null;
        if (!GodotObject.IsInstanceValid(c))
        {
            GD.Print($"{RunTableMod.LogPrefix}DBG dead cache entry: {fileName}");
            _cardCache.Remove(fileName);
            return null;
        }
        return c;
    }

    // True if `c` is currently stored anywhere in the cache (so callers
    // know not to QueueFree it on detach).
    public static Control? GetCachedCard(Control c) =>
        GodotObject.IsInstanceValid(c) && _cardCache.Values.Contains(c) ? c : null;

    // Kicks off background card-building. Safe to call repeatedly — no-ops
    // if already building or already complete. `host` is just a node we
    // can reach a SceneTree from; the SceneTreeTimer outlives `host` so
    // closing/reopening the compendium mid-build is fine.
    public static void StartCachePreBuild(Node host)
    {
        if (_cacheBuilding) return;
        var runs = RunTableData.AllRuns;
        if (_cardCache.Count >= runs.Count) return;

        _cacheBuilding = true;
        _cacheBuildIndex = 0;
        _cacheBuildOrder = runs.OrderByDescending(r => r.StartTime).ToList();
        host.GetTree().CreateTimer(0.0).Timeout += () => BuildChunk(host.GetTree());
    }

    private static void BuildChunk(SceneTree tree)
    {
        if (_cacheBuildOrder == null) { _cacheBuilding = false; return; }
        const int CardsPerChunk = 12;
        int end = System.Math.Min(_cacheBuildIndex + CardsPerChunk, _cacheBuildOrder.Count);
        for (int i = _cacheBuildIndex; i < end; i++)
        {
            var r = _cacheBuildOrder[i];
            if (!_cardCache.ContainsKey(r.FileName))
                _cardCache[r.FileName] = RunCardBuilder.BuildRunCard(
                    r, fn => OnRunCardClick?.Invoke(fn));
        }
        _cacheBuildIndex = end;
        if (_cacheBuildIndex >= _cacheBuildOrder.Count)
        {
            _cacheBuilding = false;
            _cacheBuildOrder = null;
            GD.Print($"{RunTableMod.LogPrefix}Run-card cache built ({_cardCache.Count} cards).");
        }
        else
        {
            tree.CreateTimer(0.0).Timeout += () => BuildChunk(tree);
        }
    }

    [HarmonyPatch(typeof(NCardLibrary), "_Ready")]
    public static class NCardLibrary_Ready_RunTable_Postfix
    {
        static void Postfix(NCardLibrary __instance)
        {
            if (!IsRunTable(__instance)) return;
            try { Transform(__instance); }
            catch (Exception ex)
            { GD.PrintErr($"{RunTableMod.LogPrefix}RunTable transform failed: {ex}"); }
        }
    }

    // Strips card-library specifics we don't want and stitches in the
    // RunTableView. Mirrors BadgesScreen.Transform but hides the rarity
    // module + search bar (replaced by the badges chip section + no text
    // search in this view) and the badge grid.
    private static void Transform(NCardLibrary inst)
    {
        SafeHide(inst, "CardGrid");
        SafeHide(inst, "CardCountLabel");
        // NoResultsLabel: we have to keep it in the tree (QueueFree'd
        // it crashes NCardLibrary.DisplayCards), and a one-shot
        // SafeHide isn't enough because DisplayCards re-shows it every
        // time the SearchBar query changes (since CardGrid is empty,
        // the game thinks "0 results, show the label"). Hook the
        // VisibilityChanged signal to slam it back to invisible
        // whenever DisplayCards flips it on. The follow-up signal
        // (Visible=false → fires again) is a no-op so this doesn't
        // loop.
        var noResults = inst.GetNodeOrNull<Control>("NoResultsLabel");
        if (noResults != null)
        {
            noResults.Visible = false;
            noResults.VisibilityChanged += () =>
            {
                if (!GodotObject.IsInstanceValid(noResults)) return;
                if (noResults.Visible) noResults.Visible = false;
            };
        }

        // Sidebar trim — drop card-only widgets.
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/CardTypeModule");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/CostModule");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/AlphabetSorter");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/RarityModule");   // replaced below by BadgesSection
        // Keep SearchBar visible — we wire it in WireSidebar to filter
        // runs by text (currently CharacterId; will expand later).
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/PoolFilters/ColorlessPool");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/PoolFilters/AncientsPool");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/PoolFilters/MiscPool");
        SafeHide(inst, "Sidebar/MarginContainer/BottomVBox/MultiplayerCards");
        SafeHide(inst, "Sidebar/MarginContainer/BottomVBox/Stats");
        SafeHide(inst, "Sidebar/MarginContainer/BottomVBox/Upgrades");

        // Pool icons reset to the same desaturated, false-IsSelected baseline
        // the Run Table uses, so the user gets a clean "nothing picked" UI
        // even though the underlying scene defaults Ironclad to selected.
        foreach (var n in new[] { "IroncladPool", "SilentPool", "DefectPool", "RegentPool", "NecrobinderPool" })
        {
            var f = inst.GetNodeOrNull<NCardPoolFilter>(
                $"Sidebar/MarginContainer/TopVBox/PoolFilters/{n}");
            if (f == null) continue;
            f.IsSelected = false;
            var image = f.GetNodeOrNull<Control>("Image");
            if (image?.Material is ShaderMaterial mat)
            {
                mat.SetShaderParameter("s", 0.3f);
                mat.SetShaderParameter("v", 0.55f);
            }
            if (image != null) image.Scale = Vector2.One * 0.95f;
        }

        var view = new RunTableView { Name = "RunTableView" };
        view.AnchorLeft = 0; view.AnchorRight = 1;
        view.AnchorTop  = 0; view.AnchorBottom = 1;
        view.OffsetLeft = 288; view.OffsetRight = 0;
        view.OffsetTop  = 0;   view.OffsetBottom = 0;
        view.MouseFilter = Control.MouseFilterEnum.Pass;
        inst.AddChild(view);

        view.Build();
        view.AppendExtraSidebarSections(inst);
        view.WireSidebar(inst);
        view.InitialRefresh();

    }

    // NBadge hover tips spawn via NHoverTipSet.CreateAndShow and get
    // parented somewhere whose default z=0 lands BEHIND our table's
    // character icons (NRunHistoryPlayerIcon has its own positive Z
    // internally). Bump every hover tip's z so it always wins.
    [HarmonyPatch]
    public static class NHoverTipSet_CreateAndShow_BumpZ
    {
        static System.Reflection.MethodBase TargetMethod() =>
            typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .First(m => m.Name == "CreateAndShow");
        static void Postfix(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet __result)
        {
            if (__result != null) __result.ZIndex = 4000;
        }
    }

    private static void SafeHide(Node root, string path)
    {
        try { if (root.GetNodeOrNull(path) is CanvasItem c) c.Visible = false; }
        catch (Exception ex) { GD.PrintErr($"{RunTableMod.LogPrefix}RunTable SafeHide({path}): {ex.Message}"); }
    }

    private static void RemoveFromTree(Node root, string path)
    {
        try
        {
            var n = root.GetNodeOrNull(path);
            if (n == null) return;
            n.GetParent()?.RemoveChild(n);
            n.QueueFree();
        }
        catch (Exception ex) { GD.PrintErr($"{RunTableMod.LogPrefix}RunTable RemoveFromTree({path}): {ex.Message}"); }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// RunTableView — the right-hand content area: fixed header row + scrolling
// list of run rows. Reads RunFilterState every Refresh so changes from the
// Run Table side appear immediately when the menu (re-)opens.
// ════════════════════════════════════════════════════════════════════════════
public partial class RunTableView : Control
{
    // ─── sort state ────────────────────────────────────────────────────────
    public enum SortBy { Date, Hp, Gold, Potions, Floors, Time }
    private SortBy _sortBy = SortBy.Date;
    private bool   _sortDesc = true;

    // Layout constants — sized so the page reads as a real "report" with
    // air around it rather than a list jammed against the screen edges.
    private const int TitleTop      = 32;   // top of the "Run History" title
    private const int TitleH        = 44;   // title band height
    private const int HeaderTop     = TitleTop + TitleH + 24;   // sort row top
    private const int HeaderH       = 44;   // sort row band height
    private const int RuleY         = HeaderTop + HeaderH + 6; // gold divider Y
    private const int TableLeftPad  = 60;   // extra left air before Players col
    private const int TableColGap   = 32;   // breathing room between columns

    // ─── widget refs (sidebar) ─────────────────────────────────────────────
    private readonly Dictionary<string, NCardPoolFilter> _poolFilterWidgets = new();
    private NSearchBar? _searchBar;
    // Both popovers float over the rest of the UI so neither pushes
    // the sidebar / table content down when they appear.
    private PanelContainer? _searchSuggestionsPanel;
    private VBoxContainer?  _searchSuggestionsBox;
    private PanelContainer? _selectedItemChipPanel;
    private VBoxContainer?  _selectedItemChipBox;
    // Compact icon-only strip that floats over the LEFT side of the
    // search bar when it's NOT focused. Shows the user the active
    // filters at a glance without forcing them to focus the bar.
    private Control?       _inlineIconStripPanel;
    private HBoxContainer? _inlineIconStripBox;
    // Original placeholder text — hidden while there are chips so it
    // doesn't compete with the inline icon strip; restored on clear.
    private string? _searchPlaceholderOriginal;
    // State used by the ProcessFrame-driven outside-click detector:
    // we poll Input.IsMouseButtonPressed each frame instead of
    // overriding _Input, because runtime-loaded C# assemblies don't
    // get input dispatch on custom Node subclasses.
    private bool _lastMouseDown;
    private bool _processFrameHooked;
    private HSlider?    _ascSlider;
    private MegaLabel?  _ascValueLabel;
    private Button?     _ascModeBtn;
    private readonly Dictionary<string, List<Button>> _toggleGroups = new();
    private readonly List<Action> _stateSyncers = new();

    // Badges section — two grids (tiered / tierless) of clickable icons,
    // plus an ANY/ALL toggle. _badgeCells lets RefreshBadgesGrid restyle
    // each icon on selection change without rebuilding the grids.
    private Button?        _badgeModeBtn;
    private readonly Dictionary<BadgeKey, (Control cell, Node? badge)> _badgeCells = new();
    private NCardLibrary?  _owner;

    // ─── widget refs (content) ─────────────────────────────────────────────
    private NScrollableContainer? _scroll;
    private VBoxContainer?        _runList;
    private NScrollbar?           _bar;
    private MegaLabel?            _statusLabel;
    private readonly Dictionary<SortBy, Button> _sortHeaders = new();

    public void Build()
    {
        // Wire up exit-time cleanup via Godot's underlying signal —
        // the C# event accessor (TreeExiting += ...) silently no-ops
        // in our mod DLL (no Godot source generator), and the _ExitTree
        // override is dead for the same reason.
        Connect(Node.SignalName.TreeExiting, Callable.From(OnTreeExiting));

        // "Run History" title at the very top so the page has a clear name
        // and visual breathing room above the filter row.
        var title = GameTheme.MakeMegaLabel("Run History", 28, GameTheme.Gold, GameTheme.KreonBoldTooltip);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.AnchorLeft = 0; title.AnchorRight = 1;
        title.AnchorTop  = 0; title.AnchorBottom = 0;
        title.OffsetLeft = TableLeftPad; title.OffsetRight = -RunCardBuilder.RowRightGutter;
        title.OffsetTop  = TitleTop;     title.OffsetBottom = TitleTop + TitleH;
        title.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(title);

        // Header band — anchored to top, fixed height, sits ABOVE the scroll
        // viewport so column titles don't scroll with the rows.
        var header = BuildHeaderRow();
        header.AnchorLeft = 0; header.AnchorRight = 1;
        header.AnchorTop  = 0; header.AnchorBottom = 0;
        header.OffsetLeft = 0; header.OffsetRight = -RunCardBuilder.RowRightGutter;
        header.OffsetTop  = HeaderTop; header.OffsetBottom = HeaderTop + HeaderH;
        header.MouseFilter = MouseFilterEnum.Pass;
        AddChild(header);

        // Thin gold divider under the header.
        var rule = new ColorRect
        {
            Color = new Color(GameTheme.Gold.R, GameTheme.Gold.G, GameTheme.Gold.B, 0.45f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        rule.AnchorLeft = 0; rule.AnchorRight = 1;
        rule.AnchorTop  = 0; rule.AnchorBottom = 0;
        rule.OffsetLeft = TableLeftPad; rule.OffsetRight = -RunCardBuilder.RowRightGutter;
        rule.OffsetTop  = RuleY; rule.OffsetBottom = RuleY + 1;
        AddChild(rule);

        // Scroll viewport for the run rows — same wiring pattern as
        // RunTableV2View (NScrollableContainer requires Content + Scrollbar
        // wired before _Ready since we can't override _Ready in mod code).
        _scroll = new NScrollableContainer { Name = "Scroll" };
        _scroll.AnchorLeft = 0; _scroll.AnchorRight = 1;
        _scroll.AnchorTop  = 0; _scroll.AnchorBottom = 1;
        _scroll.OffsetLeft = 0; _scroll.OffsetRight = 0;
        _scroll.OffsetTop  = RuleY + 10; _scroll.OffsetBottom = 0;
        _scroll.ClipContents = true;
        _scroll.MouseFilter = MouseFilterEnum.Stop;

        _runList = new VBoxContainer { Name = "Content" };
        _runList.AddThemeConstantOverride("separation", 6);
        _runList.AnchorLeft = 0; _runList.AnchorRight = 1;
        // OffsetLeft 42 + card ContentMarginLeft 18 = 60 = TableLeftPad,
        // so the header sort buttons sit directly above the cell content
        // and the leftmost player icons have visible breathing room from
        // the sidebar edge.
        _runList.OffsetLeft = 42; _runList.OffsetRight = -100;
        _runList.GrowHorizontal = GrowDirection.End;
        _runList.MouseFilter = MouseFilterEnum.Pass;
        _scroll.AddChild(_runList);

        var sbarScene = ResourceLoader.Load<PackedScene>("res://scenes/ui/scrollbar.tscn");
        if (sbarScene != null)
        {
            _bar = sbarScene.Instantiate<NScrollbar>();
            _bar.Name = "Scrollbar";
            _bar.AnchorLeft = 1; _bar.AnchorRight = 1;
            _bar.AnchorTop  = 0; _bar.AnchorBottom = 1;
            _bar.OffsetLeft = -100; _bar.OffsetRight = -50;
            _bar.OffsetTop  = 130;  _bar.OffsetBottom = -130;
            _bar.GrowHorizontal = GrowDirection.Begin;
            _bar.GrowVertical = GrowDirection.Both;
            _scroll.AddChild(_bar);
        }

        // Note: previously had a canvas_group + fragment-shader fade on
        // _scroll, but that put the scroll on a render layer that
        // covered the sidebar's NBadge hover popups. Plain clip is
        // boring but doesn't break hover tooltips.

        AddChild(_scroll);

        // Tiny top padding so the first run row doesn't kiss the divider.
        _runList.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        // Status label, bottom-right (same slot the Run Table uses).
        _statusLabel = GameTheme.MakeMegaLabel("", 14, GameTheme.Dim, GameTheme.KreonRegular);
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _statusLabel.VerticalAlignment = VerticalAlignment.Center;
        _statusLabel.AnchorLeft = 1; _statusLabel.AnchorRight = 1;
        _statusLabel.AnchorTop  = 1; _statusLabel.AnchorBottom = 1;
        _statusLabel.OffsetLeft = -340; _statusLabel.OffsetRight = -16;
        _statusLabel.OffsetTop  = -36;  _statusLabel.OffsetBottom = -8;
        _statusLabel.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_statusLabel);
    }

    // Fragment shader that fades COLOR.a → 0 near the top edge of the
    // canvas group's UV space. Used on _scroll (with canvas_group
    // transparent) so scrolled rows dissolve into the blurred background
    // instead of being covered by an opaque overlay.
    private static ShaderMaterial MakeScrollTopFadeMaterial()
    {
        var shader = new Shader
        {
            Code = @"
shader_type canvas_item;
uniform float fade_top : hint_range(0.0, 0.3) = 0.06;
void fragment() {
    COLOR.a *= smoothstep(0.0, fade_top, UV.y);
}",
        };
        return new ShaderMaterial { Shader = shader };
    }

    // ─── header row ────────────────────────────────────────────────────────
    private Control BuildHeaderRow()
    {
        var c = new Control { Name = "HeaderRow" };
        // Inner row uses the same column widths as RunCardBuilder and the
        // same separation, so header labels sit directly above the cell
        // content below. Both are left-aligned — when the user scans
        // numbers in a column, the header label sits flush over the digits.
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", TableColGap);
        row.AnchorLeft = 0; row.AnchorRight = 1;
        row.AnchorTop  = 0; row.AnchorBottom = 1;
        row.OffsetLeft = TableLeftPad; row.OffsetRight = -TableLeftPad;
        row.OffsetTop  = 0;  row.OffsetBottom = 0;
        c.AddChild(row);

        row.AddChild(MakeColumnLabel("Players", RunCardBuilder.ColIconsW));
        row.AddChild(MakeSortColumn  ("HP",      SortBy.Hp,      RunCardBuilder.ColHpW));
        row.AddChild(MakeSortColumn  ("Gold",    SortBy.Gold,    RunCardBuilder.ColGoldW));
        row.AddChild(MakeSortColumn  ("Potions", SortBy.Potions, RunCardBuilder.ColPotionW));
        row.AddChild(MakeSortColumn  ("Floors",  SortBy.Floors,  RunCardBuilder.ColFloorW));
        row.AddChild(MakeSortColumn  ("Time",    SortBy.Time,    RunCardBuilder.ColTimeW));
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        row.AddChild(MakeSortColumn  ("Date",    SortBy.Date,    RunCardBuilder.ColOutcomeW));
        return c;
    }

    private Control MakeColumnLabel(string text, int width)
    {
        var cell = new Control
        {
            CustomMinimumSize = new Vector2(width, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var lbl = GameTheme.MakeMegaLabel(text, 14, GameTheme.Dim, GameTheme.KreonRegular);
        lbl.HorizontalAlignment = HorizontalAlignment.Left;
        lbl.VerticalAlignment = VerticalAlignment.Center;
        lbl.AnchorLeft = 0; lbl.AnchorRight = 1;
        lbl.AnchorTop  = 0; lbl.AnchorBottom = 1;
        cell.AddChild(lbl);
        return cell;
    }

    // Sort header: left-anchored button (NOT full-cell stretch) so the text
    // sits directly above the cell content's left edge. Button width hugs
    // the text + arrow indicator + a small click margin.
    private Control MakeSortColumn(string text, SortBy key, int width)
    {
        var cell = new Control
        {
            CustomMinimumSize = new Vector2(width, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var btn = GameTheme.MakeToggleButton(text, fontSize: 14, minHeight: 32);
        // Hug the left edge so it visually aligns with the value below.
        btn.AnchorLeft = 0; btn.AnchorRight = 0;
        btn.AnchorTop  = 0.5f; btn.AnchorBottom = 0.5f;
        btn.OffsetTop  = -18; btn.OffsetBottom = 18;
        btn.OffsetLeft = 0;   btn.OffsetRight  = 0;
        // Let the button size to its text; CustomMinimumSize gives the click
        // surface enough horizontal padding to feel like a button.
        btn.CustomMinimumSize = new Vector2(72, 32);
        btn.Pressed += () => OnHeaderClick(key);
        _sortHeaders[key] = btn;
        cell.AddChild(btn);
        return cell;
    }

    // Default Recency-desc state is rendered as "Date ▼" — every other
    // column shows its plain name; the active column adds ▼ or ▲ to indicate
    // direction. Inactive columns are styled as "off" toggles.
    private void RefreshHeaderStyles()
    {
        foreach (var (key, btn) in _sortHeaders)
        {
            bool active = key == _sortBy;
            GameTheme.StyleToggle(btn, active);
            btn.Text = active ? $"{NameOf(key)} {(_sortDesc ? "▼" : "▲")}" : NameOf(key);
        }
    }

    private static string NameOf(SortBy s) => s switch
    {
        SortBy.Date    => "Date",
        SortBy.Hp      => "HP",
        SortBy.Gold    => "Gold",
        SortBy.Potions => "Potions",
        SortBy.Floors  => "Floors",
        SortBy.Time    => "Time",
        _              => s.ToString(),
    };

    private void OnHeaderClick(SortBy key)
    {
        if (_sortBy == key)
        {
            // Same column: flip direction.
            _sortDesc = !_sortDesc;
        }
        else
        {
            // New column: start in descending order (typical "show me the
            // most" first for numbers and most recent for dates).
            _sortBy = key;
            _sortDesc = true;
        }
        Refresh();
    }

    // ─── sidebar wiring ────────────────────────────────────────────────────
    public void AppendExtraSidebarSections(NCardLibrary inst)
    {
        var topVbox = inst.GetNodeOrNull<VBoxContainer>("Sidebar/MarginContainer/TopVBox");
        if (topVbox == null) return;

        // TopVBox flows top-down. Keep it to the "browse/inspect"
        // filters: Badges (dense), Ascension, Game Mode. The "outcome"
        // group (Outcome + Companions) lives below the back button so
        // the fixed-Y back button doesn't cover the labels.
        topVbox.AddChild(BuildBadgesSection());
        topVbox.AddChild(BuildAscensionSection());
        topVbox.AddChild(BuildToggleSection("gamemode", "Game Mode",
            new (string, object?)[]
            {
                ("Standard", GameMode.Standard),
                ("Daily",    GameMode.Daily),
                ("Custom",   GameMode.Custom),
            },
            sel => { RunFilterState.GameMode = (GameMode?)sel; Refresh(); },
            () => RunFilterState.GameMode));

        var bottomVbox = inst.GetNodeOrNull<VBoxContainer>("Sidebar/MarginContainer/BottomVBox");
        if (bottomVbox != null)
        {
            bottomVbox.Visible = true;
            bottomVbox.AddThemeConstantOverride("separation", 8);

            bottomVbox.AddChild(BuildToggleSection("outcome", "Outcome",
                new (string, object?)[] { ("Victory", true), ("Defeat", false) },
                sel => { RunFilterState.Win = (bool?)sel; Refresh(); },
                () => RunFilterState.Win));

            bottomVbox.AddChild(BuildToggleSection("companions", "Companions",
                new (string, object?)[] { ("Solo", false), ("Co-op", true) },
                sel => { RunFilterState.Multiplayer = (bool?)sel; Refresh(); },
                () => RunFilterState.Multiplayer));

            var clear = GameTheme.MakeToggleButton("Clear Filters", fontSize: 14, minHeight: 32);
            clear.Pressed += ClearAllFilters;
            bottomVbox.AddChild(clear);
        }
    }

    public void WireSidebar(NCardLibrary inst)
    {
        _owner = inst;
        var pf = inst.GetNodeOrNull<Node>("Sidebar/MarginContainer/TopVBox/PoolFilters");
        if (pf != null)
        {
            foreach (var (nodeName, charId) in RunTableScreen.PoolFilterToCharId)
            {
                var f = pf.GetNodeOrNull<NCardPoolFilter>(nodeName);
                if (f == null) continue;
                _poolFilterWidgets[charId] = f;
                var captured = f; var cid = charId;
                captured.Connect(NCardPoolFilter.SignalName.Toggled,
                    Callable.From<NCardPoolFilter>(_ =>
                    {
                        if (captured.IsSelected)
                        {
                            RunFilterState.SelectedChar = cid;
                            foreach (var (id, w) in _poolFilterWidgets)
                                if (id != cid && w.IsSelected) w.IsSelected = false;
                        }
                        else
                        {
                            if (RunFilterState.SelectedChar == cid) RunFilterState.SelectedChar = null;
                        }
                        Refresh();
                    }));
            }
        }

        // Search box (the card library's NSearchBar). Two paths feed
        // off it:
        //   • Plain text → RunFilterState.SearchText (legacy filter
        //     against character / mode / badge titles).
        //   • Typeahead → as the user types, a popup of matching
        //     cards / relics / potions appears below the search bar.
        //     Clicking one sets RunFilterState.SelectedSearchItem,
        //     which the run table reads to intersect with its item
        //     index (constant-time lookup against the preload).
        _searchBar = inst.GetNodeOrNull<NSearchBar>("Sidebar/MarginContainer/TopVBox/SearchBar");
        if (_searchBar != null)
        {
            BuildSearchTypeaheadUi();
            _searchBar.Connect(NSearchBar.SignalName.QueryChanged, Callable.From<string>(q =>
            {
                // The typeahead is the only filter the search bar
                // drives now — leaving the legacy free-text run
                // filter active on top of it caused double-filtering
                // and made every keystroke re-scan all runs.
                var trimmed = q?.Trim().ToLowerInvariant() ?? "";
                RefreshSearchTypeahead(trimmed);
                // Don't call Refresh() here — the typeahead doesn't
                // change the filter set by itself (only clicking a
                // suggestion or removing a chip does).
            }));
            RefreshSearchTypeahead("");
        }
    }

    // Sidebar section: a Badges header with an ANY/ALL toggle, then two
    // sub-grids (Tiered / Tierless) of larger clickable icons. Tiered
    // lays out 6 per row so each row holds B/S/G for two badges side by
    // side. Tierless is also 6 per row, one cell per badge. Clicking an
    // icon toggles it in RunFilterState.BadgeFilters; hover shows the
    // game's localized title for that badge.
    private const int BadgeCellPx       = 40;   // visual size per cell
    private const int BadgesPerRow      = 6;    // grid columns

    // NBadge inherits from a Button-y NClickableControl tree, so its
    // internal children grab the mouse — that swallows the parent's
    // tooltip + PointingHand cursor. Recursively disable mouse on the
    // whole subtree so events fall through to the outer cell button.
    private static void IgnoreMouseTree(Node n)
    {
        if (n is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var ch in n.GetChildren()) IgnoreMouseTree(ch);
    }

    private Control BuildBadgesSection()
    {
        var v = new VBoxContainer { Name = "BadgesSection" };
        v.AddThemeConstantOverride("separation", 4);

        v.AddChild(GameTheme.MakeSidebarHeader("Badges"));

        // ANY/ALL + View All in a single row so neither steals vertical
        // space from the badges grid below.
        var modeRow = new HBoxContainer();
        modeRow.AddThemeConstantOverride("separation", 6);

        _badgeModeBtn = GameTheme.MakeToggleButton("ANY", fontSize: 12, minHeight: 24);
        _badgeModeBtn.CustomMinimumSize = new Vector2(54, 24);
        _badgeModeBtn.TooltipText =
            "ANY: run earned at least one selected badge.\n" +
            "ALL: run earned every selected badge.";
        _badgeModeBtn.Pressed += () =>
        {
            RunFilterState.BadgeMode = RunFilterState.BadgeMode == RunFilterState.BadgeMatchMode.Any
                ? RunFilterState.BadgeMatchMode.All
                : RunFilterState.BadgeMatchMode.Any;
            UpdateBadgeModeBtn();
            Refresh();
        };
        UpdateBadgeModeBtn();
        modeRow.AddChild(_badgeModeBtn);

        modeRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var openVault = MakeViewAllBadgesButton();
        modeRow.AddChild(openVault);

        v.AddChild(modeRow);

        // Separate grids so tiered (B/S/G popover handles) and tierless
        // groups read as distinct rows visually. Tiered = 8 cells →
        // 2 rows of 6. Tierless = 15 cells → 3 rows (last partial).
        var tieredGrid = new GridContainer { Columns = BadgesPerRow, Name = "TieredGrid" };
        tieredGrid.AddThemeConstantOverride("h_separation", 6);
        tieredGrid.AddThemeConstantOverride("v_separation", 6);
        v.AddChild(tieredGrid);
        foreach (var entry in BadgesScreen.Catalog.Where(e => e.Tiered))
            tieredGrid.AddChild(MakeTieredBadgeCell(entry));

        // Visual gap between the two groups so it's clear which icons
        // are tiered (B/S/G popover) vs tierless (single-tap toggle).
        v.AddChild(Spacer(12));

        var tierlessGrid = new GridContainer { Columns = BadgesPerRow, Name = "TierlessGrid" };
        tierlessGrid.AddThemeConstantOverride("h_separation", 6);
        tierlessGrid.AddThemeConstantOverride("v_separation", 6);
        v.AddChild(tierlessGrid);
        foreach (var entry in BadgesScreen.Catalog.Where(e => !e.Tiered))
            tierlessGrid.AddChild(MakeBadgeCell(entry.Id, entry.FixedRarity));

        return v;
    }

    // Small gold-bordered button that sits inline with the ANY/ALL
    // toggle in the Badges section header row. Opens the full Badge
    // Vault screen (tile view with names + descriptions + counts).
    private Button MakeViewAllBadgesButton()
    {
        var b = new Button { Flat = true, Text = "View All  →" };
        b.CustomMinimumSize = new Vector2(112, 24);
        b.MouseDefaultCursorShape = CursorShape.PointingHand;
        b.TooltipText = "Open the full Run Table screen";
        b.AddThemeFontOverride("font", GameTheme.KreonBoldTooltip);
        b.AddThemeFontSizeOverride("font_size", 12);
        b.AddThemeColorOverride("font_color",         GameTheme.Cream);
        b.AddThemeColorOverride("font_hover_color",   GameTheme.Gold);
        b.AddThemeColorOverride("font_pressed_color", GameTheme.Gold);
        var nm = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.10f, 0.12f, 0.85f),
            BorderColor = new Color(GameTheme.Gold.R, GameTheme.Gold.G, GameTheme.Gold.B, 0.85f),
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 2, ContentMarginBottom = 2,
        };
        var hv = (StyleBoxFlat)nm.Duplicate();
        hv.BgColor = new Color(0.18f, 0.16f, 0.10f, 0.95f);
        hv.BorderColor = new Color(0.95f, 0.78f, 0.32f, 1f);
        b.AddThemeStyleboxOverride("normal", nm);
        b.AddThemeStyleboxOverride("hover",  hv);
        b.AddThemeStyleboxOverride("pressed", hv);
        b.Pressed += GoToRunTable;
        return b;
    }

    // Navigate to the Run Table screen. If the user came from Badge
    // Vault → Run Table, popping returns there for free. Otherwise we
    // push a fresh Run Table on top.
    private void GoToRunTable()
    {
        try
        {
            if (_owner == null) return;
            var stackField = typeof(NSubmenu).GetField("_stack",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stack = stackField?.GetValue(_owner) as NSubmenuStack;
            if (stack == null) return;
            stack.Pop();
            var top = stack.Peek();
            if (top == null || !BadgesScreen.IsRunTable(top))
                BadgesScreen.CreateAndPush(stack);
        }
        catch (Exception ex)
        { GD.PrintErr($"{RunTableMod.LogPrefix}GoToRunTable: {ex}"); }
    }

    private void UpdateBadgeModeBtn()
    {
        if (_badgeModeBtn == null) return;
        _badgeModeBtn.Text = RunFilterState.BadgeMode == RunFilterState.BadgeMatchMode.Any
            ? "ANY" : "ALL";
    }

    // Tiered group cell — shows the Bronze icon as the visual handle for
    // a tiered badge (BIG_DECK, ELITE, etc.). Click opens a popover with
    // all three tier variants laid out side by side; user picks which
    // tier(s) to filter by from there. The cell tracks selection state
    // for ANY of its three tiers (so the user can see at a glance that
    // a tiered group has a filter applied without opening the popover).
    //
    // NBadge is added directly so its own hover/tooltip mechanism fires
    // — that's the same pattern BadgeTile uses on the Run Table page,
    // and the Card Library uses the same: let the badge widget handle
    // its own mouse and connect to its Released signal for click work.
    private Control MakeTieredBadgeCell(BadgesScreen.BadgeEntry entry)
    {
        var cell = new Control { Name = $"Tiered_{entry.Id}" };
        cell.CustomMinimumSize = new Vector2(BadgeCellPx, BadgeCellPx);
        // Mouse passes through to children — NBadge underneath handles
        // its own hover (game-style tooltip with name + description).
        cell.MouseFilter = MouseFilterEnum.Ignore;

        var sel = MakeSelectionPanel(
            isSelected: BadgesScreen.AllTiers.Any(r =>
                RunFilterState.BadgeFilters.Contains(new BadgeKey(entry.Id, r))));
        cell.AddChild(sel);

        Node? badgeNode = null;
        try
        {
            var badge = NBadge.Create(entry.Id, BadgeRarity.Bronze);
            if (badge != null)
            {
                badgeNode = badge;
                badge.CustomMinimumSize = new Vector2(BadgeCellPx - 4, BadgeCellPx - 4);
                badge.Modulate = Colors.White;
                badge.MouseDefaultCursorShape = CursorShape.PointingHand;
                var wrap = new CenterContainer();
                wrap.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                wrap.MouseFilter = MouseFilterEnum.Ignore;
                wrap.AddChild(badge);
                cell.AddChild(wrap);
                badge.Connect(NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ => OpenTierPopover(cell, entry)));
            }
        }
        catch (Exception ex)
        { GD.PrintErr($"{RunTableMod.LogPrefix}MakeTieredBadgeCell NBadge.Create({entry.Id}): {ex.Message}"); }

        // Tiny "▾" hint glyph so the user knows this cell opens a popover.
        var hint = GameTheme.MakeMegaLabel("▾", 10, GameTheme.Gold);
        hint.AnchorLeft = 1; hint.AnchorRight = 1;
        hint.AnchorTop  = 1; hint.AnchorBottom = 1;
        hint.OffsetLeft = -12; hint.OffsetRight = -2;
        hint.OffsetTop  = -14; hint.OffsetBottom = -2;
        hint.MouseFilter = MouseFilterEnum.Ignore;
        cell.AddChild(hint);

        // Sentinel key — RefreshBadgesGrid uses Rarity.None to mean
        // "OR across all three tier variants for this id".
        var groupKey = new BadgeKey(entry.Id, BadgeRarity.None);
        _badgeCells[groupKey] = (cell, badgeNode);
        return cell;
    }

    // Selection-state panel that sits behind a badge. Border lights up
    // gold when the cell's filter key is active. Returns the Panel so
    // RefreshBadgesGrid can update its style without recreating the cell.
    private Panel MakeSelectionPanel(bool isSelected)
    {
        var sel = new Panel { Name = "SelectionBg" };
        sel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        sel.MouseFilter = MouseFilterEnum.Ignore;
        sel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = isSelected
                ? new Color(GameTheme.Gold.R, GameTheme.Gold.G, GameTheme.Gold.B, 0.95f)
                : new Color(0, 0, 0, 0),
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        });
        return sel;
    }

    // Modal popover anchored near the clicked tiered badge cell. Shows
    // Bronze / Silver / Gold as 3 full-size NBadge buttons; clicking
    // one toggles that exact key in BadgeFilters and refreshes (the
    // popover stays open so the user can pick multiple tiers).
    // Parented to NCardLibrary (not RunTableView) so the click-outside
    // dismiss layer covers the sidebar too.
    private Control? _activeTierPopover;
    private void OpenTierPopover(Control anchor, BadgesScreen.BadgeEntry entry)
    {
        CloseTierPopover();
        if (_owner == null) return;

        var popover = new Control { Name = "TierPopover" };
        popover.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        popover.MouseFilter = MouseFilterEnum.Stop;
        // Force on top of all siblings including the scroll's canvas
        // group (which otherwise renders character icons over the
        // popover's panel for cards behind it).
        popover.ZIndex = 100;
        // Explicit size — anchors-preset alone leaves Size=(0,0) until
        // the next layout pass, which would clip our positioned panel.
        popover.Size = _owner.Size;

        // Full-screen dismiss layer (any click anywhere outside the
        // panel closes the popover, including clicks in the sidebar).
        var dismiss = new Button { Flat = true };
        dismiss.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        dismiss.AddThemeStyleboxOverride("normal",  new StyleBoxEmpty());
        dismiss.AddThemeStyleboxOverride("hover",   new StyleBoxEmpty());
        dismiss.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
        dismiss.AddThemeStyleboxOverride("focus",   new StyleBoxEmpty());
        dismiss.Pressed += CloseTierPopover;
        popover.AddChild(dismiss);

        // Panel sized for 3 large badge buttons + name label. Generous
        // horizontal slack so Alignment=Center can lay all 3 out.
        const int badgeBig = 80;
        const int padding  = 16;
        const int gap      = 18;
        int panelW = padding * 2 + badgeBig * 3 + gap * 2 + 20; // extra slack
        int panelH = padding * 2 + badgeBig + 36 + 24; // badge + label + title row

        // Position the panel just to the RIGHT of the anchor cell so it
        // floats over the table area; popover is anchored to NCardLibrary
        // so coords are in that node's space. Use explicit anchor offsets
        // for the rect — Size + Position alone gets stomped by the
        // parent's layout pass once the panel enters the tree.
        var anchorRect = anchor.GetGlobalRect();
        var ownerRect  = _owner.GetGlobalRect();
        float x = anchorRect.End.X + 8;
        float y = anchorRect.Position.Y - 4;
        if (x + panelW > ownerRect.End.X) x = ownerRect.End.X - panelW - 8;
        if (y + panelH > ownerRect.End.Y) y = ownerRect.End.Y - panelH - 8;
        if (y < ownerRect.Position.Y)     y = ownerRect.Position.Y + 8;
        float lx = x - ownerRect.Position.X;
        float ly = y - ownerRect.Position.Y;

        var panel = new Panel { Name = "PopoverPanel" };
        var pbg = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.06f, 0.05f, 0.97f),
            BorderColor = new Color(GameTheme.Gold.R, GameTheme.Gold.G, GameTheme.Gold.B, 0.85f),
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ShadowSize = 12,
            ShadowColor = new Color(0, 0, 0, 0.65f),
        };
        panel.AddThemeStyleboxOverride("panel", pbg);
        popover.AddChild(panel);
        // Set position+size AFTER adding to tree — the parent's layout
        // pass on AddChild was resetting whatever we set beforehand.
        panel.Position = new Vector2(lx, ly);
        panel.Size     = new Vector2(panelW, panelH);
        panel.CustomMinimumSize = new Vector2(panelW, panelH);

        // Title row at the top of the panel.
        var (title, _) = RunTableV2View.LookupText(entry.Id, BadgeRarity.Bronze);
        var titleLbl = GameTheme.MakeMegaLabel(
            string.IsNullOrEmpty(title) ? entry.Id : title,
            14, GameTheme.Cream, GameTheme.KreonBoldTooltip);
        titleLbl.HorizontalAlignment = HorizontalAlignment.Center;
        titleLbl.VerticalAlignment   = VerticalAlignment.Center;
        titleLbl.AnchorLeft = 0; titleLbl.AnchorRight = 1;
        titleLbl.AnchorTop  = 0; titleLbl.AnchorBottom = 0;
        titleLbl.OffsetLeft = padding; titleLbl.OffsetRight = -padding;
        titleLbl.OffsetTop  = padding; titleLbl.OffsetBottom = padding + 22;
        titleLbl.MouseFilter = MouseFilterEnum.Ignore;
        panel.AddChild(titleLbl);

        // 3 tier choices in an HBoxContainer — matches BadgeTile's
        // pattern (HBox with center alignment + NBadge.CustomMinimumSize)
        // which is what actually gets NBadge to render. The HBox itself
        // is anchored explicitly so the panel doesn't need to layout it.
        var row = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        row.AddThemeConstantOverride("separation", gap);
        row.AnchorLeft = 0; row.AnchorRight = 1;
        row.AnchorTop  = 0; row.AnchorBottom = 1;
        row.OffsetLeft = padding; row.OffsetRight = -padding;
        row.OffsetTop  = padding + 28; row.OffsetBottom = -padding;
        panel.AddChild(row);

        foreach (var rarity in BadgesScreen.AllTiers)
            row.AddChild(MakeTierPopoverChoice(entry.Id, rarity, badgeBig));

        _owner.AddChild(popover);
        _activeTierPopover = popover;
    }

    private void CloseTierPopover()
    {
        if (_activeTierPopover == null) return;
        var p = _activeTierPopover;
        _activeTierPopover = null;
        if (GodotObject.IsInstanceValid(p)) p.QueueFree();
    }

    // One tier choice in the popover. Built as a VBoxContainer (badge
    // on top, tier label below) the same way BadgeTile builds its
    // badge+title — Container-managed layout is the only thing that
    // gets NBadge to render. Cell itself handles clicks via GuiInput so
    // the NBadge doesn't compete for the mouse.
    private Control MakeTierPopoverChoice(string id, BadgeRarity rarity, int sizePx)
    {
        var key = new BadgeKey(id, rarity);
        var v = new VBoxContainer { Name = $"Choice_{rarity}" };
        v.AddThemeConstantOverride("separation", 2);
        v.MouseFilter = MouseFilterEnum.Stop;
        v.MouseDefaultCursorShape = CursorShape.PointingHand;
        v.TooltipText = $"{rarity} tier";

        // Badge row — HBox + Center alignment, the proven pattern.
        var badgeRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        badgeRow.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(badgeRow);
        try
        {
            var badge = NBadge.Create(id, rarity);
            if (badge != null)
            {
                badge.CustomMinimumSize = new Vector2(sizePx, sizePx);
                badge.Modulate = Colors.White;
                badgeRow.AddChild(badge);
                badge.Connect(Node.SignalName.Ready, Callable.From(() => IgnoreMouseTree(badge)));
            }
        }
        catch (Exception ex)
        { GD.PrintErr($"{RunTableMod.LogPrefix}TierPopover NBadge.Create({id},{rarity}): {ex.Message}"); }

        // Tier label below the badge, color-coded per tier.
        var tierColor = rarity switch
        {
            BadgeRarity.Gold   => GameTheme.Gold,
            BadgeRarity.Silver => GameTheme.Silver,
            BadgeRarity.Bronze => GameTheme.Bronze,
            _                  => GameTheme.Dim,
        };
        var lbl = GameTheme.MakeMegaLabel(rarity.ToString(), 12, tierColor, GameTheme.KreonBoldTooltip);
        lbl.HorizontalAlignment = HorizontalAlignment.Center;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(lbl);

        // Selected indicator — small gold border around the whole stack,
        // applied via an absolute-positioned overlay rect that sits
        // behind everything (drawn first, click-through).
        var sel = new Panel();
        sel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        sel.MouseFilter = MouseFilterEnum.Ignore;
        var selStyle = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = RunFilterState.BadgeFilters.Contains(key)
                ? new Color(GameTheme.Gold.R, GameTheme.Gold.G, GameTheme.Gold.B, 0.95f)
                : new Color(0, 0, 0, 0),
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        };
        sel.AddThemeStyleboxOverride("panel", selStyle);
        // Add LAST so it sits on top visually (just the border, fully
        // transparent fill, click-through).
        v.AddChild(sel);

        v.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                if (RunFilterState.BadgeFilters.Contains(key))
                    RunFilterState.BadgeFilters.Remove(key);
                else
                    RunFilterState.BadgeFilters.Add(key);
                selStyle.BorderColor = RunFilterState.BadgeFilters.Contains(key)
                    ? new Color(GameTheme.Gold.R, GameTheme.Gold.G, GameTheme.Gold.B, 0.95f)
                    : new Color(0, 0, 0, 0);
                v.AcceptEvent();
                Refresh();
            }
        }));

        return v;
    }

    // Single badge cell — NBadge added directly so its built-in hover
    // popup fires. Connect to NBadge.Released for click toggling.
    // Selection state via a Panel behind the NBadge (not a Button
    // wrapper, since wrapping eats the badge's mouse events).
    private Control MakeBadgeCell(string id, BadgeRarity rarity)
    {
        var key = new BadgeKey(id, rarity);
        var cell = new Control { Name = $"Cell_{id}_{rarity}" };
        cell.CustomMinimumSize = new Vector2(BadgeCellPx, BadgeCellPx);
        cell.MouseFilter = MouseFilterEnum.Ignore;

        var sel = MakeSelectionPanel(RunFilterState.BadgeFilters.Contains(key));
        cell.AddChild(sel);

        Node? badgeNode = null;
        try
        {
            var badge = NBadge.Create(id, rarity);
            if (badge != null)
            {
                badgeNode = badge;
                badge.CustomMinimumSize = new Vector2(BadgeCellPx - 4, BadgeCellPx - 4);
                badge.Modulate = Colors.White;
                badge.MouseDefaultCursorShape = CursorShape.PointingHand;
                var wrap = new CenterContainer();
                wrap.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                wrap.MouseFilter = MouseFilterEnum.Ignore;
                wrap.AddChild(badge);
                cell.AddChild(wrap);
                badge.Connect(NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ =>
                    {
                        if (RunFilterState.BadgeFilters.Contains(key))
                            RunFilterState.BadgeFilters.Remove(key);
                        else
                            RunFilterState.BadgeFilters.Add(key);
                        Refresh();
                    }));
            }
        }
        catch (Exception ex)
        { GD.PrintErr($"{RunTableMod.LogPrefix}MakeBadgeCell NBadge.Create({id},{rarity}): {ex.Message}"); }

        _badgeCells[key] = (cell, badgeNode);
        return cell;
    }

    // Apply selected/unselected styling to each cell. Tiered group cells
    // are keyed with BadgeRarity.None and light up if ANY of their three
    // tier variants is currently in BadgeFilters.
    private void RefreshBadgesGrid()
    {
        foreach (var (key, (cell, badge)) in _badgeCells)
        {
            bool sel = key.Rarity == BadgeRarity.None
                ? BadgesScreen.AllTiers.Any(r =>
                    RunFilterState.BadgeFilters.Contains(new BadgeKey(key.Id, r)))
                : RunFilterState.BadgeFilters.Contains(key);

            if (badge is CanvasItem ci)
                ci.Modulate = new Color(1f, 1f, 1f, sel ? 1f : 0.4f);

            var selPanel = cell.GetNodeOrNull<Panel>("SelectionBg");
            if (selPanel?.GetThemeStylebox("panel") is StyleBoxFlat sb)
            {
                sb.BorderColor = sel
                    ? new Color(GameTheme.Gold.R, GameTheme.Gold.G, GameTheme.Gold.B, 0.95f)
                    : new Color(0, 0, 0, 0);
            }
        }
    }

    private Control BuildAscensionSection()
    {
        var v = new VBoxContainer { Name = "AscensionSection" };
        v.AddThemeConstantOverride("separation", 4);
        v.AddChild(GameTheme.MakeSidebarHeader("Ascension"));

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 8);
        v.AddChild(topRow);

        _ascModeBtn = GameTheme.MakeToggleButton("≥", fontSize: 16, minHeight: 30);
        _ascModeBtn.CustomMinimumSize = new Vector2(40, 30);
        _ascModeBtn.TooltipText = "Toggle ≥ / exact";
        _ascModeBtn.Pressed += () =>
        {
            RunFilterState.AscensionExact = !RunFilterState.AscensionExact;
            _ascModeBtn.Text = RunFilterState.AscensionExact ? "=" : "≥";
            UpdateAscLabel();
            Refresh();
        };
        topRow.AddChild(_ascModeBtn);

        _ascValueLabel = GameTheme.MakeMegaLabel("A0+", 18, GameTheme.Gold);
        _ascValueLabel.CustomMinimumSize = new Vector2(60, 0);
        _ascValueLabel.VerticalAlignment = VerticalAlignment.Center;
        topRow.AddChild(_ascValueLabel);

        topRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _ascSlider = new HSlider();
        _ascSlider.MinValue = 0;
        _ascSlider.MaxValue = 10;
        _ascSlider.Step = 1;
        _ascSlider.Value = 0;
        _ascSlider.CustomMinimumSize = new Vector2(0, 22);
        _ascSlider.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _ascSlider.ValueChanged += val =>
        {
            RunFilterState.AscensionMin = (int)val;
            UpdateAscLabel();
            Refresh();
        };
        v.AddChild(_ascSlider);

        UpdateAscLabel();
        return v;
    }

    private void UpdateAscLabel()
    {
        if (_ascValueLabel == null) return;
        _ascValueLabel.SetTextAutoSize(RunFilterState.AscensionExact
            ? $"A{RunFilterState.AscensionMin}"
            : $"A{RunFilterState.AscensionMin}+");
    }

    // Same shape as RunTableV2View.BuildToggleSection — kept local because
    // it captures this view's toggle-group + syncer collections.
    private Control BuildToggleSection(string groupKey, string header,
        (string label, object? value)[] options, Action<object?> onPick,
        Func<object?>? readCurrent = null)
    {
        var v = new VBoxContainer { Name = $"{groupKey}Section" };
        v.AddThemeConstantOverride("separation", 4);
        v.AddChild(GameTheme.MakeSidebarHeader(header));

        var hb = new HBoxContainer();
        hb.AddThemeConstantOverride("separation", 6);
        v.AddChild(hb);

        var buttons = new List<Button>();
        var values  = new List<object?>();
        for (int i = 0; i < options.Length; i++)
        {
            var (label, value) = options[i];
            var btn = GameTheme.MakeToggleButton(label, fontSize: 14, minHeight: 30);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            buttons.Add(btn);
            values.Add(value);
            var capturedBtn = btn; var capturedValue = value;
            btn.Pressed += () =>
            {
                bool wasActive = GameTheme.IsToggleActive(capturedBtn);
                foreach (var b in buttons) GameTheme.StyleToggle(b, active: false);
                if (!wasActive)
                {
                    GameTheme.StyleToggle(capturedBtn, active: true);
                    onPick(capturedValue);
                }
                else
                {
                    onPick(null);
                }
            };
            hb.AddChild(btn);
        }
        _toggleGroups[groupKey] = buttons;
        if (readCurrent != null)
        {
            _stateSyncers.Add(() =>
            {
                var current = readCurrent();
                for (int i = 0; i < buttons.Count; i++)
                    GameTheme.StyleToggle(buttons[i], Equals(values[i], current));
            });
        }
        return v;
    }

    private static Control Spacer(int height) =>
        new Control { CustomMinimumSize = new Vector2(0, height) };

    private void ClearAllFilters()
    {
        foreach (var f in _poolFilterWidgets.Values)
            if (f.IsSelected) f.IsSelected = false;

        RunFilterState.ClearAll();

        if (_ascSlider != null) _ascSlider.Value = 0;
        if (_ascModeBtn != null) _ascModeBtn.Text = "≥";
        UpdateAscLabel();

        foreach (var btns in _toggleGroups.Values)
            foreach (var b in btns) GameTheme.StyleToggle(b, active: false);

        Refresh();
    }

    // ─── refresh / sync ────────────────────────────────────────────────────
    public void InitialRefresh()
    {
        // Active view receives row-click callbacks. The cards themselves
        // live in the static cache (built once when Compendium opened);
        // this just points them at the current view's OpenRunInHistory.
        RunTableScreen.OnRunCardClick = OpenRunInHistory;
        SyncFromState();
        Refresh();

        // Dev flag — auto-open the BIG_DECK tier popover so dev_iterate
        // can screenshot it without a manual click.
        if (System.IO.File.Exists("/tmp/sts2_open_tier_popover.flag"))
        {
            try { System.IO.File.Delete("/tmp/sts2_open_tier_popover.flag"); } catch { }
            GetTree().CreateTimer(0.8).Timeout += () =>
            {
                if (!_badgeCells.TryGetValue(new BadgeKey("BIG_DECK", BadgeRarity.None), out var pair))
                    return;
                var bigDeckEntry = BadgesScreen.Catalog.FirstOrDefault(e => e.Id == "BIG_DECK");
                if (bigDeckEntry != null) OpenTierPopover(pair.cell, bigDeckEntry);
            };
        }
    }

    // Note: we CAN'T override _ExitTree here — the Godot C# source
    // generator that wires lifecycle overrides only runs for projects
    // using Godot.NET.Sdk, and our mod DLL isn't one of them. Use the
    // TreeExiting signal instead (wired in Build()).
    private void OnTreeExiting()
    {
        GD.Print($"{RunTableMod.LogPrefix}DBG TreeExiting fired. cache={RunTableScreen.CacheCount}");
        if (RunTableScreen.OnRunCardClick == (Action<string>)OpenRunInHistory)
            RunTableScreen.OnRunCardClick = null;
        CloseTierPopover();

        int detached = 0;
        if (_runList != null && GodotObject.IsInstanceValid(_runList))
        {
            foreach (var ch in _runList.GetChildren().ToList())
            {
                if (ch is Control c && RunTableScreen.GetCachedCard(c) != null)
                {
                    _runList.RemoveChild(c);
                    detached++;
                }
            }
        }
        GD.Print($"{RunTableMod.LogPrefix}DBG TreeExiting detached {detached} cards");
    }

    private void SyncFromState()
    {
        foreach (var (id, w) in _poolFilterWidgets)
            w.IsSelected = (id == RunFilterState.SelectedChar);

        if (_searchBar != null) _searchBar.TextArea.Text = RunFilterState.SearchText;
        if (_ascSlider  != null) _ascSlider.Value = RunFilterState.AscensionMin;
        if (_ascModeBtn != null) _ascModeBtn.Text = RunFilterState.AscensionExact ? "=" : "≥";
        UpdateAscLabel();

        foreach (var s in _stateSyncers) s();
    }

    // Re-order the run list to match the current filter + sort. Cards
    // live in RunTableScreen's static cache — RemoveChild detaches without
    // freeing, so the next view (or next Refresh) can re-attach them
    // in a new order with no rebuild + no disk I/O.
    private void Refresh()
    {
        RefreshHeaderStyles();
        RefreshBadgesGrid();
        UpdateBadgeModeBtn();

        if (_runList == null) return;

        foreach (var ch in _runList.GetChildren().ToList())
        {
            _runList.RemoveChild(ch);
            if (ch is Control c && RunTableScreen.GetCachedCard(c) == null)
                ch.QueueFree();
        }

        _runList.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        var runs = FilteredSortedRuns();
        int added = 0, missing = 0, reparented = 0;
        foreach (var r in runs)
        {
            var card = RunTableScreen.GetCachedCard(r.FileName);
            if (card == null) { missing++; continue; }
            // If the card is still parented to a previous RunTable's
            // _runList (Pop doesn't always free it), AddChild silently
            // fails. Explicitly detach first.
            var p = card.GetParent();
            if (p != null && p != _runList) { p.RemoveChild(card); reparented++; }
            if (card.GetParent() == null) _runList.AddChild(card);
            added++;
        }
        GD.Print($"{RunTableMod.LogPrefix}DBG Refresh: runs={runs.Count}, added={added}, missing={missing}, reparented={reparented}, cache={RunTableScreen.CacheCount}");

        _runList.AddChild(new Control { CustomMinimumSize = new Vector2(0, 100) });

        if (_statusLabel != null)
        {
            int cached = RunTableScreen.CacheCount;
            int total  = RunTableData.AllRuns.Count;
            string suffix = RunTableScreen.CacheBuilding ? $"  ·  loading {cached}/{total}" : "";
            _statusLabel.Text = $"{runs.Count} runs{suffix}";
        }
    }

    // Filter by RunFilterState (shared with Run Table), then by the
    // search box, then apply the table-local sort key.
    private List<RunRecord> FilteredSortedRuns()
    {
        // The free-text per-keystroke filter is gone — only the
        // item-chip filter (handled inside MatchesRunBasics) drives
        // the run list now.
        var src = RunTableData.AllRuns.Where(RunFilterState.MatchesRunFull);

        Func<RunRecord, IComparable> key = _sortBy switch
        {
            SortBy.Date    => r => r.StartTime,
            SortBy.Hp      => r => r.FinalHp,
            SortBy.Gold    => r => r.FinalGold,
            SortBy.Potions => r => r.MaxPotionSlots,
            SortBy.Floors  => r => r.Floor,
            SortBy.Time    => r => r.RunTime,
            _              => r => r.StartTime,
        };
        return (_sortDesc
            ? src.OrderByDescending(key)
            : src.OrderBy(key)).ToList();
    }

    // Loose match across fields available cheaply on RunRecord. Card
    // names / deck contents would require per-run RunHistory loads; we
    // can extend this later if performance allows.
    private static bool MatchesSearch(RunRecord r, string q)
    {
        if (r.CharacterId != null &&
            r.CharacterId.ToLowerInvariant().Contains(q)) return true;
        if (r.GameMode.ToString().ToLowerInvariant().Contains(q)) return true;
        if (r.LocalBadges != null)
        {
            foreach (var b in r.LocalBadges)
            {
                var (title, _) = RunTableV2View.LookupText(b.Id, b.Rarity);
                if (!string.IsNullOrEmpty(title) &&
                    title.ToLowerInvariant().Contains(q)) return true;
                if (b.Id.ToLowerInvariant().Contains(q)) return true;
            }
        }
        return false;
    }

    // Stash the file name and push NRunHistory — its OnSubmenuOpened
    // postfix (in BadgesScreen.cs) consumes the field and jumps to the run.
    private void OpenRunInHistory(string fileName)
    {
        try
        {
            if (_owner == null) return;
            var stackField = typeof(NSubmenu).GetField("_stack",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stack = stackField?.GetValue(_owner) as NSubmenuStack;
            if (stack == null) { GD.PrintErr($"{RunTableMod.LogPrefix}RunTable OpenRunInHistory: stack null."); return; }
            BadgesScreen.PendingRunFileName = fileName;
            var rh = stack.PushSubmenuType<MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen.NRunHistory>();
            if (rh == null)
            {
                BadgesScreen.PendingRunFileName = null;
                GD.PrintErr($"{RunTableMod.LogPrefix}RunTable PushSubmenuType<NRunHistory> null.");
            }
        }
        catch (Exception ex)
        {
            BadgesScreen.PendingRunFileName = null;
            GD.PrintErr($"{RunTableMod.LogPrefix}RunTable OpenRunInHistory: {ex}");
        }
    }

    // ─────── Search typeahead (cards / relics / potions) ──────────────────
    //
    // The whole index is keyed off RunTableData.ItemToRuns (built
    // during the startup preload). Picking a suggestion writes
    // RunFilterState.SelectedSearchItem; the run table re-filters
    // through a single hashset lookup, no per-run iteration.

    private const int SearchSuggestionMax = 8;
    private const int SearchIconPx        = 28;

    private sealed record SearchableItem(
        SearchItemKey Key,
        string DisplayName,
        string LowerName,
        SearchItemType Type,
        Texture2D? Icon,
        // Only one of these is non-null; carried so the icon builder
        // can do per-type compositing (e.g. frame+portrait for cards)
        // beyond a bare texture lookup.
        MegaCrit.Sts2.Core.Models.CardModel? Card = null,
        MegaCrit.Sts2.Core.Models.RelicModel? Relic = null,
        MegaCrit.Sts2.Core.Models.PotionModel? Potion = null);

    private static List<SearchableItem>? _searchableItems;

    private static IReadOnlyList<SearchableItem> GetSearchableItems()
    {
        if (_searchableItems != null) return _searchableItems;
        var list = new List<SearchableItem>();
        try
        {
            // We only surface items that appear in *some* run — those
            // are the only ones the user can actually filter to. Pulls
            // from the same index the filter uses.
            foreach (var key in RunTableData.ItemToRuns.Keys)
            {
                string name; Texture2D? icon = null;
                CardModel? card = null;
                RelicModel? relic = null;
                PotionModel? potion = null;
                switch (key.Type)
                {
                    case SearchItemType.Card:
                        card = ModelDb.GetByIdOrNull<CardModel>(key.Id);
                        if (card == null) continue;
                        // CardModel.Title is already a formatted string
                        // (handles upgrade suffix etc.), not a LocString.
                        name = card.Title;
                        icon = TryLoadTexture(() => card.Portrait);
                        break;
                    case SearchItemType.Relic:
                        relic = ModelDb.GetByIdOrNull<RelicModel>(key.Id);
                        if (relic == null) continue;
                        name = relic.Title.GetFormattedText();
                        icon = TryLoadTexture(() => relic.Icon);
                        break;
                    case SearchItemType.Potion:
                        potion = ModelDb.GetByIdOrNull<PotionModel>(key.Id);
                        if (potion == null) continue;
                        name = potion.Title.GetFormattedText();
                        icon = TryLoadTexture(() => potion.Image);
                        break;
                    default:
                        continue;
                }
                if (string.IsNullOrWhiteSpace(name)) name = key.Id.ToString();
                list.Add(new SearchableItem(key, name, name.ToLowerInvariant(),
                    key.Type, icon, card, relic, potion));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}GetSearchableItems: {ex.Message}");
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        _searchableItems = list;
        return list;
    }

    private static Texture2D? TryLoadTexture(Func<Texture2D?> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    // Single entry point for rendering an item's icon — uses
    // CardIconBuilder (frame + tinted portrait) for cards and a
    // plain TextureRect for relics / potions.
    private static Control BuildItemIcon(SearchableItem item, float size)
    {
        if (item.Type == SearchItemType.Card && item.Card != null)
        {
            return CardIconBuilder.Build(item.Card, size);
        }
        return new TextureRect
        {
            CustomMinimumSize = new Vector2(size, size),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = item.Icon,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
    }

    private void BuildSearchTypeaheadUi()
    {
        if (_searchBar == null) return;

        // Popovers go on the viewport root so they can float on top
        // of layout containers (no parent forces them into a vbox).
        // But that means when the user backs out of the run table,
        // the screen unloads while the popovers stay parented to the
        // global root and linger over whatever's next. Hook the
        // search bar's TreeExiting signal to tear them down then.
        var sceneRoot = _searchBar.GetTree()?.Root;
        var attach = (Godot.Node?)sceneRoot ?? _searchBar.GetParent();

        // Suggestions popover: shows top-N matches while typing. Drawn
        // over everything via a high ZIndex; positioned each show
        // under the search bar's current rect.
        _searchSuggestionsPanel = MakePopoverPanel(zIndex: 101);
        _searchSuggestionsBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _searchSuggestionsBox.AddThemeConstantOverride("separation", 2);
        _searchSuggestionsPanel.AddChild(_searchSuggestionsBox);
        attach?.AddChild(_searchSuggestionsPanel);

        // Chip popover: full chip list (icon + name + ✕). Only shown
        // while the search bar is focused so the user can manage the
        // active filter set without it eating space the rest of the
        // time.
        _selectedItemChipPanel = MakePopoverPanel(zIndex: 100);
        _selectedItemChipBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _selectedItemChipBox.AddThemeConstantOverride("separation", 3);
        _selectedItemChipPanel.AddChild(_selectedItemChipBox);
        attach?.AddChild(_selectedItemChipPanel);

        // Inline icon strip: tiny floating row of just the icons,
        // pinned to the right edge of the search bar. Visible when
        // the bar is NOT focused so the user can still see what's
        // filtered at a glance. No background panel — just icons.
        var stripWrap = new Control
        {
            Visible = false,
            ZIndex = 98,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _inlineIconStripPanel = stripWrap;
        _inlineIconStripBox = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _inlineIconStripBox.AddThemeConstantOverride("separation", 3);
        stripWrap.AddChild(_inlineIconStripBox);
        attach?.AddChild(stripWrap);

        try
        {
            var ta = _searchBar.TextArea;
            // Cache the original placeholder so we can restore it when
            // chips are cleared.
            _searchPlaceholderOriginal = ta.PlaceholderText;
            ta.FocusEntered += () => RefreshSearchTypeahead(_searchBar.TextArea.Text?.ToLowerInvariant() ?? "");
            ta.FocusExited += () =>
            {
                Callable.From(() =>
                {
                    // If the cursor's on either popover the user is
                    // about to click — keep the focused-state UI up
                    // until that click resolves.
                    if (_searchSuggestionsPanel != null && _searchSuggestionsPanel.Visible)
                    {
                        var m = _searchSuggestionsPanel.GetGlobalMousePosition();
                        if (_searchSuggestionsPanel.GetGlobalRect().HasPoint(m)) return;
                    }
                    if (_selectedItemChipPanel != null && _selectedItemChipPanel.Visible)
                    {
                        var m = _selectedItemChipPanel.GetGlobalMousePosition();
                        if (_selectedItemChipPanel.GetGlobalRect().HasPoint(m)) return;
                    }
                    RefreshSearchTypeahead("");
                }).CallDeferred();
            };
        }
        catch { }

        // The search bar's built-in ✕ should also wipe the chip set
        // (the user reads it as "clear the whole filter", not just
        // the text). _clearButton is private so we go via reflection.
        try
        {
            var btnField = typeof(NSearchBar).GetField("_clearButton",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (btnField?.GetValue(_searchBar) is Godot.GodotObject clearBtn)
            {
                clearBtn.Connect("Released", Callable.From<MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton>(_ =>
                {
                    RunFilterState.SelectedSearchItems.Clear();
                    RefreshSearchTypeahead("");
                    Refresh();
                }));
            }
        }
        catch { }

        // Click anywhere outside the search bar + popovers = defocus.
        // Polled in ProcessFrame because GuiInput on a low-z catcher
        // got eaten by the screen's main Control, and Node._Input
        // doesn't dispatch on runtime-loaded C# subclasses.
        try
        {
            if (!_processFrameHooked)
            {
                _processFrameHooked = true;
                _searchBar.GetTree().ProcessFrame += PollOutsideClick;
            }
        }
        catch { }

        // When the run-table screen unmounts (back button → next
        // submenu), tear down everything we attached to the viewport
        // root so the chip popover / icon strip / suggestions don't
        // linger over the next screen.
        // Submenus aren't removed from the tree when popped — they're
        // just made invisible. So TreeExiting never fires; we hook
        // VisibilityChanged on the search bar instead, which fires
        // when any ancestor toggles visibility.
        try
        {
            _searchBar.VisibilityChanged += () =>
            {
                try
                {
                    if (_searchBar != null && !_searchBar.IsVisibleInTree())
                        HideTypeaheadUi();
                }
                catch { }
            };
            _searchBar.TreeExiting += CleanupTypeaheadUi;
        }
        catch { }
    }

    // Hides the popovers (but keeps the nodes alive). Used when the
    // run-table screen is dismissed without being freed.
    private void HideTypeaheadUi()
    {
        try { if (_searchSuggestionsPanel != null) _searchSuggestionsPanel.Visible = false; } catch { }
        try { if (_selectedItemChipPanel  != null) _selectedItemChipPanel.Visible  = false; } catch { }
        try { if (_inlineIconStripPanel   != null) _inlineIconStripPanel.Visible   = false; } catch { }
    }

    private void CleanupTypeaheadUi()
    {
        try { _searchSuggestionsPanel?.QueueFree(); } catch { }
        try { _selectedItemChipPanel?.QueueFree(); } catch { }
        try { _inlineIconStripPanel?.QueueFree(); } catch { }
        _searchSuggestionsPanel = null;
        _selectedItemChipPanel = null;
        _inlineIconStripPanel = null;
        // Disconnect ProcessFrame so the polled handler stops running
        // after the screen is gone.
        try
        {
            if (_processFrameHooked && _searchBar != null)
                _searchBar.GetTree().ProcessFrame -= PollOutsideClick;
        }
        catch { }
        _processFrameHooked = false;
    }

    // ProcessFrame-driven polling: every frame, check if the left
    // mouse went from up → down. If so and the cursor isn't over the
    // search bar or its popovers, move focus off the LineEdit (a
    // ReleaseFocus call alone wasn't sticking — grabbing a different
    // control's focus does).
    private void PollOutsideClick()
    {
        try
        {
            if (_searchBar == null || !_searchBar.IsVisibleInTree()) return;
            bool down = Godot.Input.IsMouseButtonPressed(Godot.MouseButton.Left);
            bool just = down && !_lastMouseDown;
            _lastMouseDown = down;
            if (!just) return;

            var ta = _searchBar.TextArea;
            if (!ta.HasFocus()) return;

            var mouse = _searchBar.GetGlobalMousePosition();
            if (_searchBar.GetGlobalRect().HasPoint(mouse)) return;
            if (_searchSuggestionsPanel != null && _searchSuggestionsPanel.Visible
                && _searchSuggestionsPanel.GetGlobalRect().HasPoint(mouse)) return;
            if (_selectedItemChipPanel != null && _selectedItemChipPanel.Visible
                && _selectedItemChipPanel.GetGlobalRect().HasPoint(mouse)) return;

            // ReleaseFocus by itself doesn't always update the focus
            // state on a LineEdit. Toggling FocusMode forces the
            // viewport to drop it.
            var prev = ta.FocusMode;
            ta.FocusMode = Control.FocusModeEnum.None;
            ta.FocusMode = prev;
            // And re-run the refresh so chip popover collapses to the
            // icon strip immediately.
            RefreshSearchTypeahead("");
        }
        catch { }
    }

    private static bool HasPoint(Control c, Vector2 globalPos)
    {
        try { return c.GetGlobalRect().HasPoint(globalPos); }
        catch { return false; }
    }

    private PanelContainer MakePopoverPanel(int zIndex)
    {
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.05f, 0.03f, 0.97f),
            BorderColor = new Color(GameTheme.Gold.R, GameTheme.Gold.G, GameTheme.Gold.B, 0.7f),
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 6, ContentMarginRight = 6,
            ContentMarginTop = 6, ContentMarginBottom = 6,
            ShadowSize = 6,
            ShadowColor = new Color(0, 0, 0, 0.55f),
        };
        var p = new PanelContainer
        {
            Visible = false,
            ZIndex = zIndex,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        p.AddThemeStyleboxOverride("panel", bg);
        return p;
    }

    private void PositionPopovers()
    {
        if (_searchBar == null) return;
        try
        {
            var sb = _searchBar.GetGlobalRect();
            float cursorY = sb.Position.Y + sb.Size.Y + 4f;
            float width = sb.Size.X;

            if (_selectedItemChipPanel != null && _selectedItemChipPanel.Visible)
            {
                _selectedItemChipPanel.CustomMinimumSize = new Vector2(width, 0);
                _selectedItemChipPanel.GlobalPosition = new Vector2(sb.Position.X, cursorY);
                cursorY += _selectedItemChipPanel.Size.Y + 4f;
            }
            if (_searchSuggestionsPanel != null && _searchSuggestionsPanel.Visible)
            {
                _searchSuggestionsPanel.CustomMinimumSize = new Vector2(width, 0);
                _searchSuggestionsPanel.GlobalPosition = new Vector2(sb.Position.X, cursorY);
            }
            // Inline icons: LEFT-aligned over the search bar, vertically
            // centered (sits where the placeholder text would be — and
            // we blank the placeholder while chips exist so the two
            // never compete).
            if (_inlineIconStripPanel != null && _inlineIconStripPanel.Visible
                && _inlineIconStripBox != null)
            {
                var stripSize = _inlineIconStripBox.Size;
                if (stripSize.X <= 1 || stripSize.Y <= 1)
                    stripSize = _inlineIconStripBox.GetCombinedMinimumSize();
                _inlineIconStripPanel.Size = stripSize;
                _inlineIconStripPanel.GlobalPosition = new Vector2(
                    sb.Position.X + 12f,
                    sb.Position.Y + (sb.Size.Y - stripSize.Y) * 0.5f);
            }
        }
        catch { }
    }

    private void RefreshSearchTypeahead(string query)
    {
        if (_searchSuggestionsPanel == null || _searchSuggestionsBox == null) return;
        if (_selectedItemChipPanel == null || _selectedItemChipBox == null) return;
        if (_inlineIconStripPanel == null || _inlineIconStripBox == null) return;

        // Drop stale (mod-removed) selections from the set, then
        // rebuild both representations of the chips.
        PruneStaleSelections();
        RebuildChipPopover();
        RebuildInlineIconStrip();

        bool hasChips = RunFilterState.SelectedSearchItems.Count > 0;
        bool focused = false;
        try { focused = _searchBar?.TextArea.HasFocus() == true; } catch { }

        // Chip popover only appears with focus; icon strip is its
        // unfocused counterpart. Suggestion popover is independent
        // (focus + non-empty query).
        _selectedItemChipPanel.Visible = hasChips && focused;
        _inlineIconStripPanel!.Visible = hasChips && !focused;

        // Blank the placeholder while chips are shown inline so the
        // icons aren't competing with "Search…" text overlapping
        // underneath. Restore the original when there's nothing
        // active.
        try
        {
            if (_searchBar != null)
            {
                _searchBar.TextArea.PlaceholderText =
                    hasChips ? "" : (_searchPlaceholderOriginal ?? "");
            }
        }
        catch { }

        ClearChildren(_searchSuggestionsBox);
        int shown = 0;
        if (focused && !string.IsNullOrEmpty(query))
        {
            foreach (var item in GetSearchableItems())
            {
                if (!item.LowerName.Contains(query)) continue;
                if (RunFilterState.SelectedSearchItems.Contains(item.Key)) continue;
                _searchSuggestionsBox.AddChild(BuildSuggestionRow(item));
                shown++;
                if (shown >= SearchSuggestionMax) break;
            }
        }
        _searchSuggestionsPanel.Visible = shown > 0;

        // Defer so children measure themselves before we read sizes.
        Callable.From(PositionPopovers).CallDeferred();
    }

    private void PruneStaleSelections()
    {
        var items = GetSearchableItems();
        foreach (var key in RunFilterState.SelectedSearchItems.ToArray())
        {
            if (!items.Any(i => i.Key.Equals(key)))
                RunFilterState.SelectedSearchItems.Remove(key);
        }
    }

    private void RebuildInlineIconStrip()
    {
        if (_inlineIconStripBox == null) return;
        ClearChildren(_inlineIconStripBox);
        var items = GetSearchableItems();
        foreach (var key in RunFilterState.SelectedSearchItems)
        {
            var item = items.FirstOrDefault(i => i.Key.Equals(key));
            if (item == null) continue;
            _inlineIconStripBox.AddChild(BuildItemIcon(item, 22));
        }
    }

    private void RebuildChipPopover()
    {
        // Visibility is decided in RefreshSearchTypeahead — this just
        // rebuilds the row contents.
        if (_selectedItemChipBox == null) return;
        ClearChildren(_selectedItemChipBox);
        var items = GetSearchableItems();
        foreach (var key in RunFilterState.SelectedSearchItems)
        {
            var item = items.FirstOrDefault(i => i.Key.Equals(key));
            if (item == null) continue;
            _selectedItemChipBox.AddChild(BuildChipRow(item));
        }
    }

    private static void ClearChildren(Node n)
    {
        foreach (var c in n.GetChildren()) { try { n.RemoveChild(c); c.QueueFree(); } catch { } }
    }

    private Control BuildSuggestionRow(SearchableItem item)
    {
        // Whole row is a flat Button — click anywhere on the row to
        // select that item. The Button hosts an HBox with the icon,
        // name, and a small type tag (Card/Relic/Potion).
        var btn = new Button { Flat = true, FocusMode = Control.FocusModeEnum.None };
        btn.CustomMinimumSize = new Vector2(0, SearchIconPx + 8);
        btn.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            ContentMarginLeft = 6, ContentMarginRight = 6,
            ContentMarginTop = 3, ContentMarginBottom = 3,
        };
        var bgHover = (StyleBoxFlat)bg.Duplicate();
        bgHover.BgColor = new Color(GameTheme.Gold.R, GameTheme.Gold.G, GameTheme.Gold.B, 0.15f);
        btn.AddThemeStyleboxOverride("normal", bg);
        btn.AddThemeStyleboxOverride("hover", bgHover);
        btn.AddThemeStyleboxOverride("pressed", bgHover);
        btn.AddThemeStyleboxOverride("focus", bg);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        row.AnchorLeft = 0; row.AnchorRight = 1; row.AnchorTop = 0; row.AnchorBottom = 1;
        row.OffsetLeft = 6; row.OffsetRight = -6;
        btn.AddChild(row);

        var iconRect = BuildItemIcon(item, SearchIconPx);
        row.AddChild(iconRect);

        var nameLbl = GameTheme.MakeMegaLabel(item.DisplayName, 16, GameTheme.Cream, GameTheme.KreonBoldTooltip);
        nameLbl.MouseFilter = Control.MouseFilterEnum.Ignore;
        nameLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLbl.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(nameLbl);

        var tagLbl = GameTheme.MakeMegaLabel(TypeTag(item.Type), 12, GameTheme.Dim, GameTheme.KreonRegular);
        tagLbl.MouseFilter = Control.MouseFilterEnum.Ignore;
        tagLbl.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(tagLbl);

        var keyCopy = item.Key;
        btn.Pressed += () => OnSuggestionSelected(keyCopy);
        return btn;
    }

    private static string TypeTag(SearchItemType t) => t switch
    {
        SearchItemType.Card   => "Card",
        SearchItemType.Relic  => "Relic",
        SearchItemType.Potion => "Potion",
        _ => "",
    };

    private void OnSuggestionSelected(SearchItemKey key)
    {
        RunFilterState.SelectedSearchItems.Add(key);
        // Clear the free-text filter so the next search starts blank
        // and the suggestion box hides until the user types again.
        RunFilterState.SearchText = "";
        try { if (_searchBar != null) _searchBar.TextArea.Text = ""; } catch { }
        RefreshSearchTypeahead("");
        Refresh();
    }

    // One chip per row inside the chip popover: [icon] [name] [✕].
    private Control BuildChipRow(SearchableItem item)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 6);

        var iconRect = BuildItemIcon(item, SearchIconPx);
        row.AddChild(iconRect);

        var nameLbl = GameTheme.MakeMegaLabel(item.DisplayName, 14, GameTheme.Cream, GameTheme.KreonBoldTooltip);
        nameLbl.MouseFilter = Control.MouseFilterEnum.Ignore;
        nameLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLbl.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(nameLbl);

        var clearBtn = new Button { Text = "✕", Flat = true, FocusMode = Control.FocusModeEnum.None };
        clearBtn.CustomMinimumSize = new Vector2(22, 22);
        clearBtn.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        clearBtn.AddThemeColorOverride("font_color", GameTheme.Dim);
        clearBtn.AddThemeColorOverride("font_hover_color", GameTheme.Gold);
        var keyCopy = item.Key;
        clearBtn.Pressed += () =>
        {
            RunFilterState.SelectedSearchItems.Remove(keyCopy);
            RefreshSearchTypeahead(_searchBar?.TextArea.Text?.ToLowerInvariant() ?? "");
            Refresh();
        };
        row.AddChild(clearBtn);
        return row;
    }
}

