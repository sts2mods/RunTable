// BadgesScreen — clone of card_library.tscn with cards swapped for badges.
//
// All visible widgets reuse the actual game widgets / scenes / fonts where
// possible:
//   - Sidebar = real card_library Sidebar subtree (Panel + Shadow + Margin
//     + TopVBox with real NCardPoolFilter character icons, real
//     NCardRarityTickbox row, real NSearchBar, real BackButton).
//   - Section headers I add (Ascension / Game Mode / Outcome / Companions
//     / Closure) instantiate the genuine `library_sort_button.tscn`
//     (NCardViewSortButton) and call SetLabel(text) — same gold bar, same
//     kreon font.
//   - Toggle buttons in those rows use Godot Buttons themed with the game's
//     kreon_bold_glyph_space_one font + cream/gold palette.
//   - The Tier tickboxes are relabeled Common / Uncommon / Rare / Tierless
//     via NCardRarityTickbox.SetLabel (the proper API), not by replacing
//     children.
//   - Badge "cards" use MegaLabel (kreon_bold) for titles, MegaRichTextLabel
//     (kreon regular/bold, BBCode enabled — so [blue]N[/blue] tags render
//     as blue text) for descriptions.
//   - Filters are single-select with click-again-to-clear, except ascension
//     which is a 0–10 slider plus a "≥ / =" toggle.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
// Alias so existing `TierFilter.Any` etc. read naturally while the canonical
// enum lives in RunFilterState (the shared cross-menu state).
using TierFilter = RunTable.RunFilterState.TierMode;

namespace RunTable;

// ════════════════════════════════════════════════════════════════════════════
// GameTheme — font + label/button factories that use the game's own
// kreon fonts and STS2's color palette, so nothing we add visually clashes
// with the genuine card library widgets in the same screen.
// ════════════════════════════════════════════════════════════════════════════
public static class GameTheme
{
    public static readonly Color Cream  = new(1f, 0.964706f, 0.886275f);
    public static readonly Color Gold   = new(0.937255f, 0.784314f, 0.317647f);
    public static readonly Color Bronze = new(0.83f, 0.55f, 0.34f);
    public static readonly Color Silver = new(0.82f, 0.78f, 0.85f);
    public static readonly Color Dim    = new(0.50f, 0.45f, 0.33f);
    public static readonly Color Shadow = new(0, 0, 0, 0.501961f);
    // Dark ink for body text — high contrast against the bright parchment
    // backgrounds, paired with a cream outline so it stays readable when the
    // tile dims to grayscale for unearned badges.
    public static readonly Color Ink     = new(0.08f, 0.05f, 0.02f);
    public static readonly Color InkRim  = new(1.0f, 0.96f, 0.86f, 0.95f);

    public const string FontRegular     = "res://themes/kreon_regular_shared.tres";
    public const string FontBold        = "res://themes/kreon_bold_shared.tres";
    public const string FontBoldDense   = "res://themes/kreon_bold_glyph_space_one.tres";
    // glyph_space_one variants are the EXACT fonts the game's hover_tip.tscn
    // uses for badge tooltips. They have tighter glyph spacing tuned for
    // narrow tooltip panels — perfect for badge tiles.
    public const string FontRegTooltip  = "res://themes/kreon_regular_glyph_space_one.tres";
    public const string FontBoldTooltip = "res://themes/kreon_bold_glyph_space_one.tres";

    private static FontVariation? _regular, _bold, _boldDense, _regTooltip, _boldTooltip;
    public static FontVariation KreonRegular     => _regular     ??= ResourceLoader.Load<FontVariation>(FontRegular);
    public static FontVariation KreonBold        => _bold        ??= ResourceLoader.Load<FontVariation>(FontBold);
    public static FontVariation KreonBoldDense   => _boldDense   ??= ResourceLoader.Load<FontVariation>(FontBoldDense);
    public static FontVariation KreonRegTooltip  => _regTooltip  ??= ResourceLoader.Load<FontVariation>(FontRegTooltip);
    public static FontVariation KreonBoldTooltip => _boldTooltip ??= ResourceLoader.Load<FontVariation>(FontBoldTooltip);

    public static MegaLabel MakeMegaLabel(string text, int size, Color color, FontVariation? font = null)
    {
        var l = new MegaLabel { Text = text };
        l.AutoSizeEnabled = false;
        l.MinFontSize = size;
        l.MaxFontSize = size;
        l.AddThemeFontOverride("font", font ?? KreonBoldDense);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_outline_color", Shadow);
        l.AddThemeConstantOverride("outline_size", 8);
        return l;
    }

    // Exact replica of the Title node in hover_tip.tscn:
    //   font: kreon_bold_glyph_space_one, size 22, color GOLD, dropshadow 3,2.
    public static MegaLabel MakeTooltipTitle(string text, int size = 22)
    {
        var l = new MegaLabel { Text = text };
        l.AutoSizeEnabled = false;
        l.MinFontSize = size;
        l.MaxFontSize = size;
        l.AddThemeFontOverride("font", KreonBoldTooltip);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", Gold);
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.25098f));
        l.AddThemeConstantOverride("shadow_offset_x", 3);
        l.AddThemeConstantOverride("shadow_offset_y", 2);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        l.VerticalAlignment = VerticalAlignment.Center;
        return l;
    }

    // Hover-tip-style rich text: exact replica of the Description node in
    // res://scenes/ui/hover_tip.tscn (kreon_regular_glyph_space_one + cream
    // default_color + dark shadow at 3,2 + line_separation -2). This is the
    // game's own badge-tooltip styling, proven readable.
    public static MegaRichTextLabel MakeMegaRich(string text, int size, Color color, bool bbcode = true)
    {
        var l = new MegaRichTextLabel
        {
            BbcodeEnabled = bbcode,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = text,
        };
        // CRITICAL: MegaRichTextLabel defaults to AutoSizeEnabled = true and
        // grows the font to fill the box, so short text renders larger than
        // long text. Disable it so font_size below is the actual rendered size.
        l.AutoSizeEnabled = false;
        l.AddThemeFontOverride("normal_font", KreonRegTooltip);
        l.AddThemeFontOverride("bold_font",   KreonBoldTooltip);
        l.AddThemeFontSizeOverrideAll(size);
        l.AddThemeColorOverride("default_color", color);
        // Exact dropshadow from hover_tip.tscn — proven readable on every bg.
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.25098f));
        l.AddThemeConstantOverride("shadow_offset_x", 3);
        l.AddThemeConstantOverride("shadow_offset_y", 2);
        l.AddThemeConstantOverride("line_separation", -2);
        return l;
    }

    // reward_panel.png is the in-game reward-screen panel — same parchment
    // texture the previous viewer used. Its base hue is green, but the game's
    // own hsv.gdshader can rotate it to any color, so each rarity ends up
    // with a proper tan / blue / gold parchment instead of needing separate
    // assets.
    private const string RewardPanelPath = "res://images/ui/reward_screen/reward_panel.png";
    private const string HsvShaderPath   = "res://shaders/hsv.gdshader";
    private static Texture2D? _rewardPanelTex;
    private static Shader? _hsvShader;
    public static Texture2D RewardPanelTexture =>
        _rewardPanelTex ??= ResourceLoader.Load<Texture2D>(RewardPanelPath);
    public static Shader HsvShader =>
        _hsvShader ??= ResourceLoader.Load<Shader>(HsvShaderPath);

    public static ShaderMaterial MakeHsvMaterial(float h, float s, float v)
    {
        var mat = new ShaderMaterial { Shader = HsvShader };
        mat.SetShaderParameter("h", h);
        mat.SetShaderParameter("s", s);
        mat.SetShaderParameter("v", v);
        return mat;
    }

    // HDR-modulated reward_panel.png. The source is greenish parchment, so
    // the modulate channels go above 1.0 where needed to fully suppress green
    // and boost the target hue. (Plain hue rotation via the HSV shader on
    // this YIQ-space texture produced purples/olives, not the intended tints.)
    public static NinePatchRect MakeRarityBackground(Color modulate)
    {
        var n = new NinePatchRect
        {
            Texture = RewardPanelTexture,
            PatchMarginLeft = 56, PatchMarginRight = 56,
            PatchMarginTop = 56,  PatchMarginBottom = 56,
            Modulate = modulate,
        };
        n.MouseFilter = Control.MouseFilterEnum.Ignore;
        return n;
    }

    public static Button MakeToggleButton(string text, int fontSize = 14, int minHeight = 32)
    {
        var b = new Button { Text = text };
        b.CustomMinimumSize = new Vector2(0, minHeight);
        b.AddThemeFontOverride("font", KreonBoldDense);
        b.AddThemeFontSizeOverride("font_size", fontSize);
        b.AddThemeColorOverride("font_outline_color", Shadow);
        b.AddThemeConstantOverride("outline_size", 6);
        StyleToggle(b, active: false);
        return b;
    }

    public static void StyleToggle(Button b, bool active)
    {
        var normal = new StyleBoxFlat
        {
            BgColor = active ? new Color(0.937255f, 0.784314f, 0.317647f, 0.92f)
                              : new Color(0.10f, 0.13f, 0.17f, 0.7f),
            BorderColor = active ? new Color(1f, 0.92f, 0.45f, 1f)
                                  : new Color(0.34f, 0.40f, 0.45f, 0.8f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 4, ContentMarginRight = 4,
            ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = active ? new Color(1f, 0.86f, 0.40f, 0.97f)
                                : new Color(0.20f, 0.24f, 0.30f, 0.85f);
        b.AddThemeStyleboxOverride("normal",  normal);
        b.AddThemeStyleboxOverride("hover",   hover);
        b.AddThemeStyleboxOverride("pressed", hover);
        b.AddThemeStyleboxOverride("focus",   normal);
        b.AddThemeColorOverride("font_color",
            active ? new Color(0.08f, 0.06f, 0.03f) : Cream);
        b.AddThemeColorOverride("font_hover_color",
            active ? new Color(0.08f, 0.06f, 0.03f) : new Color(0.98f, 0.92f, 0.74f));
        b.SetMeta("active", active);
    }

    public static bool IsToggleActive(Button b) =>
        b.HasMeta("active") && b.GetMeta("active").AsBool();

    // Section header for the SIDEBAR (256px-wide column): genuine
    // library_sort_button.tscn matches the existing Tier section header.
    // Force a min size — when stacked under other widgets in a VBox the
    // scene was collapsing to ~0 height and only the label text rendered.
    public static Control MakeSidebarHeader(string text)
    {
        var scene = ResourceLoader.Load<PackedScene>(
            "res://scenes/screens/card_library/library_sort_button.tscn");
        var node = scene.Instantiate<NCardViewSortButton>();
        node.FocusMode = Control.FocusModeEnum.None;
        node.MouseFilter = Control.MouseFilterEnum.Ignore;
        node.CustomMinimumSize = new Vector2(0, 36);
        node.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        node.Connect(Node.SignalName.Ready, Callable.From(() =>
        {
            try
            {
                node.SetLabel(text);
                var arrow = node.GetNodeOrNull<Control>("HBoxContainer/Image");
                if (arrow != null) arrow.Visible = false;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RunTableMod.LogPrefix}MakeSidebarHeader({text}): {ex.Message}");
            }
        }));
        return node;
    }

    // Section header for the MAIN AREA (wide content area): plain MegaLabel
    // with kreon_bold + gold + a thin separator. library_sort_button would
    // stretch its background texture across ~1600px and look terrible.
    public static Control MakeContentHeader(string text)
    {
        var v = new VBoxContainer { Name = "ContentHeader" };
        v.AddThemeConstantOverride("separation", 4);
        v.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var lbl = MakeMegaLabel(text, 24, Gold, KreonBold);
        v.AddChild(lbl);

        var sep = new ColorRect { Color = new Color(0.937255f, 0.784314f, 0.317647f, 0.55f) };
        sep.CustomMinimumSize = new Vector2(0, 2);
        sep.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        v.AddChild(sep);
        return v;
    }
}

// ════════════════════════════════════════════════════════════════════════════
public static class BadgesScreen
{
    public const string MetaKey = "run_table";

    // The run history screen, when pushed by us, reads this field on
    // OnSubmenuOpened and jumps directly to that run instead of defaulting
    // to the most-recent. Set when a run row is clicked in the Run Table,
    // cleared by NRunHistory_OnSubmenuOpened postfix once consumed.
    public static string? PendingRunFileName;

    public sealed record BadgeEntry(
        string Id,
        bool Tiered,
        BadgeRarity FixedRarity,
        bool MultiplayerOnly,
        bool RequiresWin);

    public static readonly List<BadgeEntry> Catalog = new()
    {
        new("BIG_DECK",        true,  BadgeRarity.None,   false, true),
        new("CCCCOMBO",        false, BadgeRarity.Bronze, false, false),
        new("CURSES",          false, BadgeRarity.Bronze, false, true),
        new("DAMAGE_LEADER",   false, BadgeRarity.Bronze, true,  false),
        new("DEBUFFER",        false, BadgeRarity.Bronze, true,  false),
        new("DOUBLE_SNECKO",   false, BadgeRarity.Bronze, false, false),
        new("ELITE",           true,  BadgeRarity.None,   false, false),
        new("FAMISHED",        false, BadgeRarity.Bronze, false, true),
        new("GLUTTON",         true,  BadgeRarity.None,   false, true),
        new("HEALER",          true,  BadgeRarity.None,   true,  false),
        new("HIGHLANDER",      false, BadgeRarity.Bronze, false, true),
        new("HONED",           false, BadgeRarity.Bronze, false, true),
        new("ILIKESHINY",      false, BadgeRarity.Bronze, false, false),
        new("KACHING",         false, BadgeRarity.Bronze, false, false),
        new("MONEY_MONEY",     true,  BadgeRarity.None,   false, true),
        new("MYSTERY_MACHINE", false, BadgeRarity.Bronze, false, false),
        new("PERFECT",         true,  BadgeRarity.None,   false, false),
        new("RESTFUL",         false, BadgeRarity.Bronze, false, true),
        new("RESTLESS",        false, BadgeRarity.Bronze, false, true),
        new("SPEEDY",          true,  BadgeRarity.None,   false, true),
        new("TABLET",          false, BadgeRarity.Gold,   false, true),
        new("TEAM_PLAYER",     false, BadgeRarity.Silver, true,  false),
        new("TINY_DECK",       true,  BadgeRarity.None,   false, true),
    };

    public static readonly BadgeRarity[] AllTiers =
        { BadgeRarity.Bronze, BadgeRarity.Silver, BadgeRarity.Gold };

    public static NCardLibrary? CreateAndPush(NSubmenuStack stack)
    {
        var inst = NCardLibrary.Create();
        if (inst == null) { GD.PrintErr($"{RunTableMod.LogPrefix}V2 NCardLibrary.Create returned null."); return null; }
        inst.Name = "BadgesScreen";
        inst.SetMeta(MetaKey, true);
        inst.Visible = false;
        stack.AddChild(inst);
        stack.Push(inst);
        GD.Print($"{RunTableMod.LogPrefix}V2 pushed BadgesScreen.");
        return inst;
    }

    public static bool IsRunTable(Node n) =>
        n.HasMeta(MetaKey) && n.GetMeta(MetaKey).AsBool();

    [HarmonyPatch(typeof(NCardLibrary), "_Ready")]
    public static class NCardLibrary_Ready_RunTable_Postfix
    {
        static void Postfix(NCardLibrary __instance)
        {
            if (!IsRunTable(__instance)) return;
            try { Transform(__instance); }
            catch (Exception ex) { GD.PrintErr($"{RunTableMod.LogPrefix}V2 transform failed: {ex}"); }
        }
    }

    private static void Transform(NCardLibrary inst)
    {
        SafeHide(inst, "CardGrid");
        SafeHide(inst, "CardCountLabel");
        RemoveFromTree(inst, "NoResultsLabel");

        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/CardTypeModule");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/CostModule");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/AlphabetSorter");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/PoolFilters/ColorlessPool");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/PoolFilters/AncientsPool");
        SafeHide(inst, "Sidebar/MarginContainer/TopVBox/PoolFilters/MiscPool");
        // Hide only the original BottomVBox children — we'll reuse the
        // container for our own "below the back button" filter sections.
        SafeHide(inst, "Sidebar/MarginContainer/BottomVBox/MultiplayerCards");
        SafeHide(inst, "Sidebar/MarginContainer/BottomVBox/Stats");
        SafeHide(inst, "Sidebar/MarginContainer/BottomVBox/Upgrades");
        // KEEP the original Columns=4 on the pool-filter grid. Setting it to
        // 5 was what was forcing the whole sidebar wider than 288 (5 × 64px
        // icon = 320px min, which propagated up to MarginContainer → Sidebar,
        // shifting the content off the left edge). 5 visible icons now flow
        // as 4 + 1 rows, which fits cleanly in the 256-wide content area.

        // Relabel via the proper NCardRarityTickbox API (the Label is a
        // MegaLabel — direct child access would skip MegaLabel's auto-size).
        TrySetTickboxLabel(inst, "Sidebar/MarginContainer/TopVBox/RarityModule/RarityToggler/CommonRarity",   "Common");
        TrySetTickboxLabel(inst, "Sidebar/MarginContainer/TopVBox/RarityModule/RarityToggler/UncommonRarity", "Uncommon");
        TrySetTickboxLabel(inst, "Sidebar/MarginContainer/TopVBox/RarityModule/RarityToggler/RareRarity",     "Rare");
        TrySetTickboxLabel(inst, "Sidebar/MarginContainer/TopVBox/RarityModule/RarityToggler/OtherRarity",    "Tierless");
        // Relabel the Rarity sort button header to "Tier".
        TrySetSortLabel(inst, "Sidebar/MarginContainer/TopVBox/RarityModule/RaritySorter", "Tier");

        // Tickboxes start unticked → "no tier filter, show all". Force-enable
        // them too — by default NCardLibrary disables them until a character
        // pool is selected, but badges don't depend on character at all.
        foreach (var n in new[] { "CommonRarity", "UncommonRarity", "RareRarity", "OtherRarity" })
        {
            var tb = inst.GetNodeOrNull<NTickbox>(
                $"Sidebar/MarginContainer/TopVBox/RarityModule/RarityToggler/{n}");
            if (tb != null) { tb.IsTicked = false; tb.Enable(); }
        }

        // Pool filter icons start DESELECTED. Ironclad's scene-default has
        // its HSV shader at full saturation (s=1, v=1) so it visually reads
        // as "selected" until we explicitly desaturate it — even though
        // IsSelected starts false in C#. Push the shader to desat (s=0.3,
        // v=0.55) and shrink scale so all icons look uniformly unselected.
        foreach (var n in new[] { "IroncladPool", "SilentPool", "DefectPool", "RegentPool", "NecrobinderPool" })
        {
            var f = inst.GetNodeOrNull<NCardPoolFilter>(
                $"Sidebar/MarginContainer/TopVBox/PoolFilters/{n}");
            if (f == null) continue;
            f.IsSelected = false;
            var image = f.GetNodeOrNull<Control>("Image");
            if (image != null)
            {
                if (image.Material is ShaderMaterial mat)
                {
                    mat.SetShaderParameter("s", 0.3f);
                    mat.SetShaderParameter("v", 0.55f);
                }
                image.Scale = Vector2.One * 0.95f;
            }
        }

        var view = new RunTableV2View { Name = "BadgeView" };
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

    private static void SafeHide(Node root, string path)
    {
        try { if (root.GetNodeOrNull(path) is CanvasItem c) c.Visible = false; }
        catch (Exception ex) { GD.PrintErr($"{RunTableMod.LogPrefix}SafeHide({path}): {ex.Message}"); }
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
        catch (Exception ex) { GD.PrintErr($"{RunTableMod.LogPrefix}RemoveFromTree({path}): {ex.Message}"); }
    }

    private static void TrySetTickboxLabel(Node root, string path, string text)
    {
        try
        {
            var node = root.GetNodeOrNull(path);
            if (node == null) return;
            var setLabel = node.GetType().GetMethod("SetLabel",
                BindingFlags.Instance | BindingFlags.Public);
            if (setLabel != null) { setLabel.Invoke(node, new object[] { text }); return; }
            var lblNode = node.GetNodeOrNull("Label");
            if (lblNode is MegaLabel ml) { ml.SetTextAutoSize(text); return; }
            if (lblNode is Label lbl)    { lbl.Text = text; return; }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}TrySetTickboxLabel({path}): {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void TrySetSortLabel(Node root, string path, string text)
    {
        try
        {
            var node = root.GetNodeOrNull(path);
            if (node == null) return;
            var setLabel = node.GetType().GetMethod("SetLabel",
                BindingFlags.Instance | BindingFlags.Public);
            if (setLabel != null) setLabel.Invoke(node, new object[] { text });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}TrySetSortLabel({path}): {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// RunTableV2View — content overlay + sidebar additions.
//
// Scroll uses the game's NScrollableContainer because Godot's C# lifecycle
// dispatch (_Ready, _Input, _Process) requires the Godot.NET.Sdk source
// generator to run at compile time, which doesn't happen for mod assemblies.
// Custom Control subclasses defined here ARE constructed, but their override
// methods never get called by Godot's event loop — only methods on classes
// from the game assembly (built with Godot.NET.Sdk) are wired up.
// ════════════════════════════════════════════════════════════════════════════
public partial class RunTableV2View : Control
{
    private static readonly Dictionary<string, string> PoolFilterToCharId = new()
    {
        ["IroncladPool"]    = "CHARACTER.IRONCLAD",
        ["SilentPool"]      = "CHARACTER.SILENT",
        ["DefectPool"]      = "CHARACTER.DEFECT",
        ["RegentPool"]      = "CHARACTER.REGENT",
        ["NecrobinderPool"] = "CHARACTER.NECROBINDER",
    };

    // ─── widget refs ───────────────────────────────────────────────────────
    private readonly Dictionary<string, NCardPoolFilter> _poolFilterWidgets = new();
    private readonly Dictionary<TierFilter, NTickbox>    _tierTickboxes     = new();
    private NSearchBar? _searchBar;
    private HSlider?    _ascSlider;
    private MegaLabel?  _ascValueLabel;
    private Button?     _ascModeBtn;
    private readonly Dictionary<string, List<Button>> _toggleGroups = new();
    // Per-group sync closure: re-styles the toggle row to match the value
    // currently held in RunFilterState. Called when the menu re-opens so
    // filters changed in the Run Table appear ticked here too.
    private readonly List<Action> _stateSyncers = new();

    // ─── content widgets ───────────────────────────────────────────────────
    private MegaLabel?           _statusLabel;
    private NScrollableContainer? _scroll;
    private VBoxContainer?       _sectionsHost;
    private NScrollbar?          _bar;
    // Two sticky headers — one for each section. Each one pins to the top
    // of the scroll viewport while its section is visible, and the next
    // section's header physically pushes the previous one upward as you
    // scroll past the boundary (CSS-position-sticky style, not text-swap).
    private Control? _stickyHeaderTiered;
    private Control? _stickyHeaderTierless;
    private const int StickyTopPad   = 24;   // distance from window top to sticky zone
    private const int StickyHeaderH  = 44;   // visible height of a sticky header
    private const int StickyHeaderW  = 220;
    private VBoxContainer?     _tieredSection;
    private VBoxContainer?     _tierlessSection;
    private Container?         _tieredFlow;
    private Container?         _tierlessFlow;
    private readonly List<BadgeTile> _tiles = new();
    // Set true once the first animate-in has finished, so Refresh() updates
    // counts/visibility without re-tweening tiles every keystroke.
    private bool _initialAnimDone;

    private NCardLibrary? _owner;

    public void Build()
    {
        // Build the NScrollableContainer + Content + Scrollbar TREE FIRST, off
        // the scene graph, then AddChild it to ourselves. NScrollableContainer's
        // _Ready does GetNode<NScrollbar>("Scrollbar") which throws if the
        // child doesn't exist yet, so the bar/content must be wired before
        // the scroll enters the tree. (Source generators only run for game
        // assemblies, so we can't override _Ready ourselves to defer the wiring.)
        _scroll = new NScrollableContainer { Name = "Scroll" };
        _scroll.AnchorLeft = 0; _scroll.AnchorRight = 1;
        _scroll.AnchorTop  = 0; _scroll.AnchorBottom = 1;
        // Scroll spans the entire main area — touches the sidebar on the left,
        // the right edge on the right, and runs top-to-bottom with the fade
        // gradient handling visual cutoff.
        _scroll.OffsetLeft = 0; _scroll.OffsetRight = 0;
        _scroll.OffsetTop  = 0; _scroll.OffsetBottom = 0;
        _scroll.ClipContents = true;
        _scroll.MouseFilter = MouseFilterEnum.Stop;

        // Content node — MUST be named "Content". OffsetRight = -100 carves
        // out the right 100px for the scrollbar (which lives at the same right
        // edge of _scroll). Grid centering inside this content area then
        // properly centers between sidebar and scrollbar — not between sidebar
        // and full-screen-right (which would push tiles under the scrollbar).
        _sectionsHost = new VBoxContainer { Name = "Content" };
        _sectionsHost.AddThemeConstantOverride("separation", 32);
        _sectionsHost.AnchorLeft = 0;  _sectionsHost.AnchorRight = 1;
        _sectionsHost.OffsetLeft = 0;  _sectionsHost.OffsetRight = -100;
        _sectionsHost.GrowHorizontal = GrowDirection.End;
        _sectionsHost.MouseFilter = MouseFilterEnum.Ignore;
        _scroll.AddChild(_sectionsHost);

        // Scrollbar — MUST be named "Scrollbar". Geometry mirrors the card
        // library's bar (centered vertically with ~130px padding from top
        // and bottom of the full window), so the visible bar is the same
        // height as the card library's. Anchored to the right of the scroll
        // viewport, narrow (50px wide).
        var sbarScene = ResourceLoader.Load<PackedScene>("res://scenes/ui/scrollbar.tscn");
        if (sbarScene != null)
        {
            _bar = sbarScene.Instantiate<NScrollbar>();
            _bar.Name = "Scrollbar";
            _bar.AnchorLeft = 1; _bar.AnchorRight = 1;
            _bar.AnchorTop = 0;  _bar.AnchorBottom = 1;
            // Card library uses offset_top=129.6 / offset_bottom=-130.4
            // relative to a full-window parent. Our _scroll viewport now also
            // spans full window height, so we use the same values verbatim.
            _bar.OffsetLeft = -100; _bar.OffsetRight = -50;
            _bar.OffsetTop  = 130;  _bar.OffsetBottom = -130;
            _bar.GrowHorizontal = GrowDirection.Begin;
            _bar.GrowVertical = GrowDirection.Both;
            _scroll.AddChild(_bar);
        }

        // Top + bottom fade overlay — same gradient as the relic_collection
        // screen (0.9α at edges, 0α in the middle), so tile textures don't
        // have a hard cutoff line where they scroll past the viewport edge.
        var fade = new TextureRect { Name = "FadeOverlay" };
        fade.AnchorLeft = 0; fade.AnchorRight = 1;
        fade.AnchorTop  = 0; fade.AnchorBottom = 1;
        fade.OffsetLeft = 0; fade.OffsetRight = 0;
        fade.OffsetTop  = 0; fade.OffsetBottom = 0;
        fade.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        fade.StretchMode = TextureRect.StretchModeEnum.Scale;
        fade.MouseFilter = MouseFilterEnum.Ignore;
        var grad = new Gradient
        {
            InterpolationMode = Gradient.InterpolationModeEnum.Cubic,
            Offsets = new[] { 0f, 0.05f, 0.95f, 1f },
            Colors  = new[]
            {
                new Color(0, 0, 0, 0.9f),
                new Color(0, 0, 0, 0f),
                new Color(0, 0, 0, 0f),
                new Color(0, 0, 0, 0.9f),
            },
        };
        var gradTex = new GradientTexture2D
        {
            Gradient = grad,
            Width = 2,
            Height = 256,
            FillFrom = new Vector2(0.5f, 0f),
            FillTo = new Vector2(0.5f, 1f),
        };
        fade.Texture = gradTex;
        _scroll.AddChild(fade);

        // Now safe to add scroll to the tree — _Ready will find both children.
        AddChild(_scroll);

        // Top padding so the first row of tiles starts below the fade band
        // / sticky header (the scroll viewport now extends to y=0 in window
        // coords, so without padding the first row would be cut off).
        _sectionsHost.AddChild(new Control { CustomMinimumSize = new Vector2(0, 80) });

        // Tiered = 3-column GridContainer so each ROW is one badge with its
        // Bronze / Silver / Gold tiles in the same column every time.
        BuildSection("Tiered Badges", out _tieredSection, out _tieredFlow, columns: 3);
        BuildSection("Tierless Badges", out _tierlessSection, out _tierlessFlow, columns: 3);

        // Bottom padding so the last row of tiles isn't hidden behind the
        // status label / bottom fade band.
        _sectionsHost.AddChild(new Control { CustomMinimumSize = new Vector2(0, 100) });

        // Sticky section header — hangs on the LEFT of the scroll viewport,
        // styled like the sidebar's library_sort_button headers, and updates
        // text as the user scrolls between sections.
        BuildStickyHeader();

        // Status readout right-aligned in the bottom-right corner, under the
        // scrollbar. Width comfortably fits "999 awards · 99 tiers · 999 runs".
        _statusLabel = GameTheme.MakeMegaLabel("", 14, GameTheme.Dim, GameTheme.KreonRegular);
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _statusLabel.VerticalAlignment = VerticalAlignment.Center;
        _statusLabel.AnchorLeft = 1; _statusLabel.AnchorRight = 1;
        _statusLabel.AnchorTop = 1;  _statusLabel.AnchorBottom = 1;
        _statusLabel.OffsetLeft = -340; _statusLabel.OffsetRight = -16;
        _statusLabel.OffsetTop  = -36;  _statusLabel.OffsetBottom = -8;
        _statusLabel.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_statusLabel);

        // Top-right "Browse All Runs" tile — entry into the Run Table with
        // no badge filter (other filters carry over via RunFilterState).
        AddChild(BuildBrowseRunsTile());

        foreach (var entry in BadgesScreen.Catalog)
        {
            var tiers = entry.Tiered ? BadgesScreen.AllTiers : new[] { entry.FixedRarity };
            foreach (var rarity in tiers)
            {
                var tile = new BadgeTile(this, entry, rarity);
                _tiles.Add(tile);
                var parent = entry.Tiered ? _tieredFlow : _tierlessFlow;
                parent!.AddChild(tile);
            }
        }
    }

    // Tile-styled entry button anchored top-right of the content area.
    // Click → clear badge filter and push Run Table.
    private Control BuildBrowseRunsTile()
    {
        var btn = new Button { Flat = true, Text = "Browse All Runs  →" };
        btn.CustomMinimumSize = new Vector2(280, 44);
        btn.AnchorLeft = 1; btn.AnchorRight = 1;
        btn.AnchorTop  = 0; btn.AnchorBottom = 0;
        btn.OffsetLeft = -300; btn.OffsetRight = -20;
        btn.OffsetTop  = 20;   btn.OffsetBottom = 64;
        btn.AddThemeFontOverride("font", GameTheme.KreonBoldTooltip);
        btn.AddThemeFontSizeOverride("font_size", 16);
        btn.AddThemeColorOverride("font_color",         GameTheme.Cream);
        btn.AddThemeColorOverride("font_hover_color",   GameTheme.Gold);
        btn.AddThemeColorOverride("font_pressed_color", GameTheme.Gold);
        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.10f, 0.12f, 0.80f),
            BorderColor = new Color(0.55f, 0.45f, 0.20f, 0.85f),
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 14, ContentMarginRight = 14,
            ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.18f, 0.16f, 0.10f, 0.95f);
        hover.BorderColor = new Color(0.95f, 0.78f, 0.32f, 1f);
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover",  hover);
        btn.AddThemeStyleboxOverride("pressed", hover);
        btn.MouseDefaultCursorShape = CursorShape.PointingHand;
        btn.Pressed += OpenRunTable;
        return btn;
    }

    // Shared "open run table" entry — used by the top-right tile.
    // Clears badge filter (intent: "show me everything") but leaves the
    // sidebar filters (char/asc/etc.) intact via RunFilterState.
    private void OpenRunTable()
    {
        try
        {
            RunFilterState.BadgeFilters.Clear();
            if (_owner == null) return;
            var stackField = typeof(NSubmenu).GetField("_stack",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stack = stackField?.GetValue(_owner) as NSubmenuStack;
            if (stack == null) { GD.PrintErr($"{RunTableMod.LogPrefix}OpenRunTable: stack null."); return; }
            RunTableScreen.CreateAndPush(stack);
        }
        catch (Exception ex)
        { GD.PrintErr($"{RunTableMod.LogPrefix}OpenRunTable: {ex}"); }
    }

    private void BuildStickyHeader()
    {
        if (_scroll == null) return;
        // Build two sticky headers, one per section. Each is a Control wrapping
        // a library_sort_button (sidebar header style), pinned to the left of
        // the scroll viewport and repositioned vertically every frame by
        // UpdateStickyHeader based on its section's scroll position.
        _stickyHeaderTiered   = MakeStickyHeader("Tiered Badges");
        _stickyHeaderTierless = MakeStickyHeader("Tierless Badges");
        AddChild(_stickyHeaderTiered);
        AddChild(_stickyHeaderTierless);

        var tracker = new Timer { WaitTime = 0.016, Autostart = true, OneShot = false };
        tracker.Timeout += UpdateStickyHeader;
        AddChild(tracker);
    }

    private Control MakeStickyHeader(string title)
    {
        var c = new Control { Name = "Sticky_" + title.Replace(" ", "") };
        c.MouseFilter = MouseFilterEnum.Ignore;
        c.AnchorLeft = 0; c.AnchorRight = 0;
        c.AnchorTop = 0;  c.AnchorBottom = 0;
        // Placeholder; X position is re-pinned to the leftmost tile's actual
        // screen position in UpdateStickyHeader each frame (the GridContainer's
        // centering layout isn't fully measured until after the first layout
        // pass, so static offsets here would be wrong).
        c.OffsetLeft   = 200;
        c.OffsetRight  = c.OffsetLeft + StickyHeaderW;
        c.OffsetTop    = StickyTopPad;
        c.OffsetBottom = StickyTopPad + StickyHeaderH;
        var inner = GameTheme.MakeSidebarHeader(title);
        inner.AnchorLeft = 0; inner.AnchorRight = 1;
        inner.AnchorTop = 0;  inner.AnchorBottom = 1;
        inner.OffsetLeft = 0; inner.OffsetRight = 0;
        inner.OffsetTop = 0;  inner.OffsetBottom = 0;
        c.AddChild(inner);
        return c;
    }

    // Walk a section's grid and find the leftmost tile, return its X position
    // in OUR coord space (RunTableV2View's coordinate system).
    private float LeftmostTileX(Container? flow)
    {
        if (flow == null || _scroll == null) return float.NaN;
        BadgeTile? leftmost = null;
        foreach (var child in flow.GetChildren())
        {
            if (child is BadgeTile t && t.Visible)
            {
                if (leftmost == null || t.Position.X < leftmost.Position.X)
                    leftmost = t;
            }
        }
        if (leftmost == null) return float.NaN;
        // Tile position → grid → section → sectionsHost → scroll → view.
        // Climb the parent chain summing Position.X.
        Node? n = leftmost;
        float x = 0;
        while (n != null && n != this)
        {
            if (n is Control cc) x += cc.Position.X;
            n = n.GetParent();
        }
        return x;
    }

    // Map a section's content-relative position to its Y in OUR coord space
    // (parent of the scroll viewport), accounting for the content's scroll
    // offset and the scroll viewport's own y-anchor.
    private float SectionScreenTop(VBoxContainer? section)
    {
        if (section == null || _sectionsHost == null || _scroll == null) return float.MaxValue;
        // section.Position.Y is local to sectionsHost; sectionsHost.Position.Y
        // is local to _scroll (this is what scrolls). _scroll's top in our
        // coord space is _scroll.OffsetTop (assuming AnchorTop=0).
        return _scroll.OffsetTop + _sectionsHost.Position.Y + section.Position.Y;
    }

    private void UpdateStickyHeader()
    {
        if (_stickyHeaderTiered == null || _stickyHeaderTierless == null) return;

        float tieredTop   = SectionScreenTop(_tieredSection);
        float tierlessTop = SectionScreenTop(_tierlessSection);

        // Push gap chosen so that by the time Tierless settles at its sticky
        // position (StickyTopPad), Tiered has been pushed entirely off-screen
        // (y + StickyHeaderH < 0). PushGap = StickyHeaderH + StickyTopPad is
        // the minimum to fully hide; we use a bit more for a clean exit.
        const int PushGap = 80;
        float tierlessPinned = Mathf.Max(StickyTopPad, tierlessTop);
        float tieredPinned   = Mathf.Max(StickyTopPad, tieredTop);
        tieredPinned         = Mathf.Min(tieredPinned, tierlessPinned - StickyHeaderH - PushGap);

        SetStickyY(_stickyHeaderTiered,   tieredPinned);
        SetStickyY(_stickyHeaderTierless, tierlessPinned);

        // Align the sticky header X to the leftmost actual tile's X position
        // — that's the "true" left edge of the badge column once the centered
        // GridContainer has done its layout. Headers feel anchored to the
        // badges instead of floating in undefined space.
        float leftX = LeftmostTileX(_tieredFlow);
        if (!float.IsNaN(leftX))
        {
            SetStickyX(_stickyHeaderTiered,   leftX);
        }
        float leftXTierless = LeftmostTileX(_tierlessFlow);
        if (!float.IsNaN(leftXTierless))
        {
            SetStickyX(_stickyHeaderTierless, leftXTierless);
        }

        _stickyHeaderTiered.Visible   = _tieredSection?.Visible   == true && _tieredSection.GetChildCount() > 1;
        _stickyHeaderTierless.Visible = _tierlessSection?.Visible == true && _tierlessSection.GetChildCount() > 1;

        // Force-enable tier tickboxes — NCardLibrary's own UpdatePoolFilter
        // logic disables them when no pool is selected (because cards depend
        // on a chosen character pool). Badges are tier-only, so they should
        // ALWAYS be selectable, regardless of which character is picked.
        foreach (var tb in _tierTickboxes.Values)
        {
            if (tb != null && !tb.IsEnabled) tb.Enable();
        }
    }

    private static void SetStickyX(Control c, float x)
    {
        float w = c.OffsetRight - c.OffsetLeft;
        c.OffsetLeft  = x;
        c.OffsetRight = x + w;
    }

    private static void SetStickyY(Control c, float y)
    {
        float h = c.OffsetBottom - c.OffsetTop;
        c.OffsetTop    = y;
        c.OffsetBottom = y + h;
    }

    private void BuildSection(string title, out VBoxContainer section, out Container flow, int columns = 3)
    {
        section = new VBoxContainer { Name = title.Replace(" ", "") + "Section" };
        section.AddThemeConstantOverride("separation", 12);
        section.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        section.MouseFilter = MouseFilterEnum.Ignore;
        // Tag with the section title so the sticky-header tracker can find
        // each section's vertical range as the user scrolls.
        section.SetMeta("section_title", title);
        _sectionsHost!.AddChild(section);

        // Top spacer keeps the first row of tiles below the fade gradient.
        var topPad = new Control { CustomMinimumSize = new Vector2(0, 8) };
        section.AddChild(topPad);

        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 20);
        grid.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        grid.MouseFilter = MouseFilterEnum.Ignore;
        section.AddChild(grid);
        flow = grid;
    }

    // ─── extra sidebar sections ────────────────────────────────────────────
    public void AppendExtraSidebarSections(NCardLibrary inst)
    {
        var topVbox = inst.GetNodeOrNull<VBoxContainer>("Sidebar/MarginContainer/TopVBox");
        if (topVbox == null) return;

        // Sections that fit ABOVE the back button (anchored top-down).
        topVbox.AddChild(Spacer(8));
        topVbox.AddChild(BuildAscensionSection());

        topVbox.AddChild(Spacer(8));
        topVbox.AddChild(BuildToggleSection("gamemode", "Game Mode",
            new (string, object?)[]
            {
                ("Standard", GameMode.Standard),
                ("Daily",    GameMode.Daily),
                ("Custom",   GameMode.Custom),
            },
            sel => { RunFilterState.GameMode = (GameMode?)sel; Refresh(); },
            () => RunFilterState.GameMode));

        topVbox.AddChild(Spacer(8));
        topVbox.AddChild(BuildToggleSection("outcome", "Outcome",
            new (string, object?)[] { ("Victory", true), ("Defeat", false) },
            sel => { RunFilterState.Win = (bool?)sel; Refresh(); },
            () => RunFilterState.Win));

        // Sections that live BELOW the back button (anchored bottom-up).
        // BottomVBox has size_flags_vertical = 8 (Shrink End) so its content
        // stacks from the bottom of the sidebar, clear of the back button.
        var bottomVbox = inst.GetNodeOrNull<VBoxContainer>("Sidebar/MarginContainer/BottomVBox");
        if (bottomVbox != null)
        {
            bottomVbox.Visible = true;
            bottomVbox.AddThemeConstantOverride("separation", 8);

            bottomVbox.AddChild(BuildToggleSection("companions", "Companions",
                new (string, object?)[] { ("Solo", false), ("Co-op", true) },
                sel => { RunFilterState.Multiplayer = (bool?)sel; Refresh(); },
                () => RunFilterState.Multiplayer));

            bottomVbox.AddChild(BuildToggleSection("closure", "Closure",
                new (string, object?)[] { ("Concluded", false), ("Abandoned", true) },
                sel => { RunFilterState.Abandoned = (bool?)sel; Refresh(); },
                () => RunFilterState.Abandoned));

            var clear = GameTheme.MakeToggleButton("Clear Filters", fontSize: 14, minHeight: 32);
            clear.Pressed += ClearAllFilters;
            bottomVbox.AddChild(clear);
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

    // ─── single-select wiring for card-library widgets ─────────────────────
    public void WireSidebar(NCardLibrary inst)
    {
        _owner = inst;
        // When the menu becomes visible again (e.g. user popped back from
        // the Run Table), pick up any filter changes they made there and
        // refresh tile counts to match.
        inst.VisibilityChanged += () =>
        {
            if (!inst.Visible) return;
            SyncFromState();
            Refresh();
        };

        var pf = inst.GetNodeOrNull<Node>("Sidebar/MarginContainer/TopVBox/PoolFilters");
        if (pf != null)
        {
            foreach (var (nodeName, charId) in PoolFilterToCharId)
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

        WireTier(inst, "Sidebar/MarginContainer/TopVBox/RarityModule/RarityToggler/CommonRarity",   TierFilter.Common);
        WireTier(inst, "Sidebar/MarginContainer/TopVBox/RarityModule/RarityToggler/UncommonRarity", TierFilter.Uncommon);
        WireTier(inst, "Sidebar/MarginContainer/TopVBox/RarityModule/RarityToggler/RareRarity",     TierFilter.Rare);
        WireTier(inst, "Sidebar/MarginContainer/TopVBox/RarityModule/RarityToggler/OtherRarity",    TierFilter.Tierless);

        _searchBar = inst.GetNodeOrNull<NSearchBar>("Sidebar/MarginContainer/TopVBox/SearchBar");
        if (_searchBar != null)
        {
            _searchBar.Connect(NSearchBar.SignalName.QueryChanged, Callable.From<string>(q =>
            {
                RunFilterState.SearchText = q?.Trim().ToLowerInvariant() ?? "";
                Refresh();
            }));
        }
    }

    private void WireTier(NCardLibrary inst, string path, TierFilter tier)
    {
        var t = inst.GetNodeOrNull<NTickbox>(path);
        if (t == null) return;
        _tierTickboxes[tier] = t;
        var captured = t; var cap = tier;
        captured.Connect(NTickbox.SignalName.Toggled, Callable.From<NTickbox>(_ =>
        {
            if (captured.IsTicked)
            {
                RunFilterState.Tier = cap;
                foreach (var (other, tb) in _tierTickboxes)
                    if (other != cap && tb.IsTicked) tb.IsTicked = false;
            }
            else
            {
                if (RunFilterState.Tier == cap) RunFilterState.Tier = TierFilter.Any;
            }
            Refresh();
        }));
    }

    private void ClearAllFilters()
    {
        foreach (var f in _poolFilterWidgets.Values)
            if (f.IsSelected) f.IsSelected = false;

        foreach (var (_, tb) in _tierTickboxes)
            if (tb.IsTicked) tb.IsTicked = false;

        // Wipe shared state in one shot, then resync UI widgets to match.
        RunFilterState.ClearAll();

        if (_searchBar != null) _searchBar.TextArea.Text = "";
        if (_ascSlider != null) _ascSlider.Value = 0;
        if (_ascModeBtn != null) _ascModeBtn.Text = "≥";
        UpdateAscLabel();

        foreach (var btns in _toggleGroups.Values)
            foreach (var b in btns) GameTheme.StyleToggle(b, active: false);

        Refresh();
    }

    // NScrollableContainer handles its own bidirectional scrollbar sync, so
    // no manual wiring needed here.

    public void InitialRefresh()
    {
        SyncFromState();
        Refresh();
        // Defer one short tick so the HFlowContainer has settled tile.Position
        // values before we capture restY for the slide-up tween.
        GetTree().CreateTimer(0.05).Timeout += AnimateInTiles;
    }

    // Re-apply the current RunFilterState onto every widget. Called when the
    // menu (re-)opens so any filter changes made elsewhere (Run Table side)
    // are reflected here.
    private void SyncFromState()
    {
        // Character: select the matching NCardPoolFilter, deselect the rest.
        foreach (var (id, w) in _poolFilterWidgets)
            w.IsSelected = (id == RunFilterState.SelectedChar);

        // Tier tickboxes.
        foreach (var (key, tb) in _tierTickboxes)
            tb.IsTicked = (key == RunFilterState.Tier);

        // Search bar.
        if (_searchBar != null) _searchBar.TextArea.Text = RunFilterState.SearchText;

        // Ascension slider / mode.
        if (_ascSlider  != null) _ascSlider.Value = RunFilterState.AscensionMin;
        if (_ascModeBtn != null) _ascModeBtn.Text = RunFilterState.AscensionExact ? "=" : "≥";
        UpdateAscLabel();

        // Game mode / outcome / companions / closure toggle rows.
        foreach (var s in _stateSyncers) s();
    }

    public void AnimateInTiles()
    {
        if (_initialAnimDone) return;
        _initialAnimDone = true;
        var visible = _tiles.Where(t => t.Visible).ToList();
        if (visible.Count == 0) return;
        var tween = CreateTween().SetParallel();
        for (int i = 0; i < visible.Count; i++)
        {
            var t = visible[i];
            float delay = (float)i / visible.Count * 0.2f;
            float restY = t.Position.Y;
            // Start offset 40px down + transparent, then ease back into place.
            t.Position = new Vector2(t.Position.X, restY + 40f);
            var mod = t.Modulate; mod.A = 0f; t.Modulate = mod;
            tween.TweenProperty(t, "position:y", restY, 0.4)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back)
                .SetDelay(delay);
            tween.TweenProperty(t, "modulate:a", 1f, 0.4)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo)
                .SetDelay(delay);
        }
    }

    private void Refresh()
    {
        if (_tieredSection == null || _tierlessSection == null) return;

        int totalAwards = 0, totalTiers = 0;
        foreach (var tile in _tiles)
        {
            bool visible = TileVisible(tile);
            tile.Visible = visible;
            if (!visible) continue;
            int count = TileCount(tile);
            tile.UpdateAppearance(count);
            if (count > 0) { totalAwards += count; totalTiers++; }
        }

        _tieredSection.Visible   = _tieredFlow!  .GetChildren().OfType<BadgeTile>().Any(t => t.Visible);
        _tierlessSection.Visible = _tierlessFlow!.GetChildren().OfType<BadgeTile>().Any(t => t.Visible);

        int totalRuns = MatchingRunCount();
        if (_statusLabel != null)
            _statusLabel.Text = $"{totalAwards} awards   ·   {totalTiers} tiers   ·   {totalRuns} runs";
    }

    private bool TileVisible(BadgeTile tile)
    {
        switch (RunFilterState.Tier)
        {
            case TierFilter.Any: break;
            case TierFilter.Tierless: if (tile.Entry.Tiered) return false; break;
            case TierFilter.Common:   if (tile.Rarity != BadgeRarity.Bronze) return false; break;
            case TierFilter.Uncommon: if (tile.Rarity != BadgeRarity.Silver) return false; break;
            case TierFilter.Rare:     if (tile.Rarity != BadgeRarity.Gold)   return false; break;
        }
        if (RunFilterState.SearchText.Length > 0 && !tile.MatchesSearch(RunFilterState.SearchText)) return false;
        return true;
    }

    // Run filter delegates to the shared RunFilterState so both menus agree.
    private static bool RunMatches(RunRecord r) => RunFilterState.MatchesRunBasics(r);

    private int TileCount(BadgeTile tile)
    {
        var key = new BadgeKey(tile.Entry.Id, tile.Rarity);
        if (!RunTableData.BadgeToRuns.TryGetValue(key, out var runs)) return 0;
        return runs.Count(RunMatches);
    }

    private int MatchingRunCount() => RunTableData.AllRuns.Count(RunMatches);

    // Click a badge → add it to the shared filter set and push the Run
    // Table. The chip × in the Run Table sidebar lets the user remove
    // individual badges, so this is purely additive — click multiple
    // tiles to build a multi-badge filter.
    internal void OpenRunListModal(BadgeTile tile)
    {
        try
        {
            var key = new BadgeKey(tile.Entry.Id, tile.Rarity);
            RunFilterState.BadgeFilters.Add(key);

            if (_owner == null) return;
            var stackField = typeof(NSubmenu).GetField("_stack",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stack = stackField?.GetValue(_owner) as NSubmenuStack;
            if (stack == null) { GD.PrintErr($"{RunTableMod.LogPrefix}OpenRunListModal: stack null."); return; }
            RunTableScreen.CreateAndPush(stack);
        }
        catch (Exception ex) { GD.PrintErr($"{RunTableMod.LogPrefix}OpenRunListModal: {ex}"); }
    }

    // Shared LocString cache (used by tiles for search + label lookup).
    private static readonly Dictionary<(string Id, BadgeRarity Rarity), (string Title, string Desc)> _textCache = new();

    internal static (string Title, string Desc) LookupText(string id, BadgeRarity rarity)
    {
        var key = (id, rarity);
        if (_textCache.TryGetValue(key, out var v)) return v;
        string title = "", desc = "";
        try
        {
            string prefix = rarity switch
            {
                BadgeRarity.Bronze => "bronze",
                BadgeRarity.Silver => "silver",
                BadgeRarity.Gold   => "gold",
                _ => "",
            };
            if (LocString.Exists("badges", $"{id}.{prefix}Title"))
            {
                title = new LocString("badges", $"{id}.{prefix}Title").GetFormattedText() ?? "";
                desc  = new LocString("badges", $"{id}.{prefix}Description").GetFormattedText() ?? "";
            }
            else if (LocString.Exists("badges", $"{id}.title"))
            {
                title = new LocString("badges", $"{id}.title").GetFormattedText() ?? "";
                desc  = new LocString("badges", $"{id}.description").GetFormattedText() ?? "";
            }
        }
        catch (Exception ex) { GD.PrintErr($"{RunTableMod.LogPrefix}LookupText({id},{rarity}): {ex.Message}"); }
        v = (title, desc);
        _textCache[key] = v;
        return v;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// BadgeTile — game-styled "card" for a (badge, rarity) pair. Uses MegaLabel
// (kreon_bold) for the title, MegaRichTextLabel (kreon, BBCode enabled) for
// the description, so [blue]N[/blue] etc. render as colored text just like
// they do elsewhere in the game.
// ════════════════════════════════════════════════════════════════════════════
public partial class BadgeTile : Control
{
    public BadgesScreen.BadgeEntry Entry { get; }
    public BadgeRarity Rarity { get; }
    public bool EarnedEver { get; private set; }

    private readonly RunTableV2View _view;
    private NBadge?            _badge;
    private MegaRichTextLabel? _descLabel;
    private MegaLabel?         _titleLabel;
    private MegaLabel?         _countLabel;

    // Tile = fixed-size Control with ClipContents so nothing leaks past the
    // parchment edges. Children are absolute-positioned (not packed) so each
    // element lands exactly where it should and the description label can be
    // forced narrower than the tile to wrap inside the parchment's PAINTED
    // area, well clear of the torn 9-slice edges.
    private const int TileWidth      = 340;
    private const int TileHeight     = 380;
    // Text-wrap area is WAY narrower than the tile — the parchment's painted
    // surface is only ~220px wide; the rest is torn 9-slice edges.
    private const int TextAreaWidth  = 210;
    // Distance from the tile bottom to the centerline of the big "×N" stamp.
    // The parchment's painted area ends ~40px above the tile bottom; lifting
    // the count up keeps it inside the readable surface (not on the torn rim).
    private const int CountAreaHeight = 90;
    private const int CountBottomPad  = 45;

    private NinePatchRect? _bg;

    public BadgeTile(RunTableV2View view, BadgesScreen.BadgeEntry entry, BadgeRarity rarity)
    {
        _view = view;
        Entry = entry;
        Rarity = rarity;
        Name = $"Tile_{entry.Id}_{rarity}";
        CustomMinimumSize = new Vector2(TileWidth, TileHeight);
        ClipContents = true;
        // Pass lets wheel events bubble up to the MomentumScroll parent for
        // scrolling, while we still receive left-click events for the modal.
        MouseFilter = MouseFilterEnum.Pass;
        // Start invisible — AnimateInTiles() will fade us in. Prevents a
        // one-frame flash at full opacity before the staggered tween starts.
        var startMod = Modulate; startMod.A = 0f; Modulate = startMod;

        // Background — full tile rect.
        _bg = GameTheme.MakeRarityBackground(RarityModulate(rarity, dimmed: false));
        _bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_bg);

        // Vertical-centered content area, leaving the bottom CountAreaHeight
        // px free for the big "×N" stamp. CenterContainer centers its single
        // VBox child within that rect, so icon+title+desc sit visually in the
        // middle of the parchment.
        var contentRegion = new CenterContainer();
        contentRegion.AnchorLeft = 0; contentRegion.AnchorRight = 1;
        contentRegion.AnchorTop = 0; contentRegion.AnchorBottom = 1;
        contentRegion.OffsetLeft = 0; contentRegion.OffsetRight = 0;
        contentRegion.OffsetTop = 0; contentRegion.OffsetBottom = -CountAreaHeight;
        contentRegion.MouseFilter = MouseFilterEnum.Pass;
        AddChild(contentRegion);

        var (title, desc) = RunTableV2View.LookupText(entry.Id, rarity);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        // CRITICAL: forcing CustomMinimumSize.X here is what makes the rich
        // text wrap to TextAreaWidth. CenterContainer respects the child's
        // min size, and the inner RichTextLabel uses its parent's width when
        // ExpandFill is set.
        v.CustomMinimumSize = new Vector2(TextAreaWidth, 0);
        contentRegion.AddChild(v);

        // Centered badge artwork.
        var badgeRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        v.AddChild(badgeRow);
        try
        {
            _badge = NBadge.Create(entry.Id, rarity);
            if (_badge != null)
            {
                _badge.CustomMinimumSize = new Vector2(76, 76);
                _badge.Modulate = Colors.White;
                _badge.Connect(NClickableControl.SignalName.Released,
                    Callable.From((Action<NButton>)(_ =>
                    {
                        if (EarnedEver) _view.OpenRunListModal(this);
                    })));
                badgeRow.AddChild(_badge);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}BadgeTile NBadge.Create({entry.Id}, {rarity}): {ex.Message}");
        }

        _titleLabel = GameTheme.MakeTooltipTitle(
            string.IsNullOrEmpty(title) ? entry.Id : title, size: 20);
        _titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(_titleLabel);

        _descLabel = GameTheme.MakeMegaRich(desc, 16, GameTheme.Cream, bbcode: true);
        _descLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _descLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _descLabel.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(_descLabel);

        // Big "×N" stamp anchored to the bottom of the tile. No "earned"
        // suffix — the × glyph alone makes the meaning obvious. Sized like a
        // small heading (28pt) so it reads as the at-a-glance status.
        _countLabel = new MegaLabel { Text = "—" };
        _countLabel.AutoSizeEnabled = false;
        _countLabel.MinFontSize = 28;
        _countLabel.MaxFontSize = 28;
        _countLabel.AddThemeFontOverride("font", GameTheme.KreonBoldTooltip);
        _countLabel.AddThemeFontSizeOverride("font_size", 28);
        _countLabel.AddThemeColorOverride("font_color", GameTheme.Dim);
        _countLabel.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.25098f));
        _countLabel.AddThemeConstantOverride("shadow_offset_x", 3);
        _countLabel.AddThemeConstantOverride("shadow_offset_y", 2);
        _countLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _countLabel.VerticalAlignment   = VerticalAlignment.Center;
        _countLabel.AnchorLeft = 0; _countLabel.AnchorRight = 1;
        _countLabel.AnchorTop  = 1; _countLabel.AnchorBottom = 1;
        _countLabel.OffsetLeft = 0; _countLabel.OffsetRight = 0;
        // Lift the stamp UP from the bottom (CountBottomPad px above the tile
        // edge) so it sits inside the parchment painted area, not on the rim.
        _countLabel.OffsetTop  = -CountAreaHeight;
        _countLabel.OffsetBottom = -CountBottomPad;
        AddChild(_countLabel);

        EarnedEver = RunTableData.BadgeToRuns.ContainsKey(new BadgeKey(entry.Id, rarity));
        MouseDefaultCursorShape = EarnedEver ? CursorShape.PointingHand : CursorShape.Arrow;
        // Whole-tile click → same modal as clicking the badge icon. Wired via
        // signal Connect (not override) because Godot's lifecycle dispatch
        // doesn't fire override methods on mod-assembly Control subclasses —
        // the source generator only runs for the main game project.
        this.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnTileGuiInput));
    }

    private void OnTileGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed
            && mb.ButtonIndex == MouseButton.Left)
        {
            if (EarnedEver) _view.OpenRunListModal(this);
            AcceptEvent();
        }
        // Wheel + other events are NOT consumed → they bubble up to
        // NScrollableContainer for scrolling.
    }

    public void UpdateAppearance(int count)
    {
        if (_bg != null)
            _bg.Modulate = RarityModulate(Rarity, dimmed: count == 0);
        if (_badge != null)
        {
            _badge.Modulate = count > 0 ? Colors.White : new Color(0.35f, 0.35f, 0.35f, 0.55f);
            _badge.MouseDefaultCursorShape = EarnedEver ? CursorShape.PointingHand : CursorShape.Arrow;
        }
        var fade = count > 0 ? Colors.White : new Color(1, 1, 1, 0.7f);
        if (_titleLabel != null) _titleLabel.Modulate = fade;
        if (_descLabel  != null) _descLabel.Modulate  = fade;
        if (_countLabel != null)
        {
            // Just "×N" — no "earned" suffix; the × glyph + big font says it.
            _countLabel.SetTextAutoSize(count > 0 ? $"×{count}" : "—");
            _countLabel.AddThemeColorOverride("font_color",
                count > 0 ? TitleColorForRarity(Rarity) : GameTheme.Dim);
        }
    }

    public bool MatchesSearch(string lowerQuery)
    {
        var (title, desc) = RunTableV2View.LookupText(Entry.Id, Rarity);
        return (title?.ToLowerInvariant().Contains(lowerQuery) ?? false)
            || (desc ?.ToLowerInvariant().Contains(lowerQuery) ?? false);
    }

    public string GetTitle()
    {
        var (title, _) = RunTableV2View.LookupText(Entry.Id, Rarity);
        return string.IsNullOrEmpty(title) ? Entry.Id : title;
    }

    // HDR Modulate per rarity. Computed from sampled pixel data
    //   (target / source per channel) then scaled by `intensity` to taste:
    //   intensity 1.0 lands exactly on the badge border RGB; 0.6 is a muted
    //   parchment-ish version of the same hue (easier to read text on top).
    // Parchment dim enough that the hover-tip's cream/gold dropshadow text
    // reads on it the same way it reads on the game's own tooltip panel.
    // Lower intensity = darker parchment, more contrast against text.
    private static Color RarityModulate(BadgeRarity r, bool dimmed)
    {
        const float intensity = 0.30f;
        Color c = r switch
        {
            BadgeRarity.Gold   => new Color(5.59f, 3.42f, 1.61f) * intensity,
            BadgeRarity.Silver => new Color(3.00f, 3.31f, 3.00f) * intensity,
            BadgeRarity.Bronze => new Color(5.16f, 2.52f, 1.43f) * intensity,
            _                  => new Color(2.80f, 2.70f, 2.40f) * intensity,
        };
        c.A = 1.0f;
        if (dimmed) return new Color(c.R * 0.45f, c.G * 0.45f, c.B * 0.45f, 0.60f);
        return c;
    }

    private static Color TitleColorForRarity(BadgeRarity r) => r switch
    {
        BadgeRarity.Gold   => GameTheme.Gold,
        BadgeRarity.Silver => GameTheme.Silver,
        BadgeRarity.Bronze => GameTheme.Bronze,
        _                  => GameTheme.Dim,
    };
}

// ════════════════════════════════════════════════════════════════════════════
// Harmony postfix on NRunHistory.OnSubmenuOpened — when we pushed the run
// history screen from a Run Table modal click, BadgesScreen.PendingRunFileName
// is set. Find that run in the screen's internal list and jump straight to it
// instead of the default (most-recent run).
// ════════════════════════════════════════════════════════════════════════════
[HarmonyPatch(typeof(NRunHistory), nameof(NRunHistory.OnSubmenuOpened))]
public static class NRunHistory_OnSubmenuOpened_Postfix
{
    static void Postfix(NRunHistory __instance)
    {
        var pending = BadgesScreen.PendingRunFileName;
        if (pending == null) return;
        BadgesScreen.PendingRunFileName = null;
        try
        {
            var namesField = typeof(NRunHistory).GetField("_runNames",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var names = namesField?.GetValue(__instance) as List<string>;
            int idx = names?.IndexOf(pending) ?? -1;
            if (idx < 0)
            {
                GD.PrintErr($"{RunTableMod.LogPrefix}Run '{pending}' not in NRunHistory list (size={names?.Count}).");
                return;
            }
            var refresh = typeof(NRunHistory).GetMethod("RefreshAndSelectRun",
                BindingFlags.Instance | BindingFlags.NonPublic);
            refresh?.Invoke(__instance, new object[] { idx });
            GD.Print($"{RunTableMod.LogPrefix}Jumped NRunHistory to index {idx} ({pending}).");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}NRunHistory postfix failed: {ex}");
        }
    }
}
