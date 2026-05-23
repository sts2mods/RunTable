// Run-history-page integration for Run Table.
//
// Adds a small "Run Table" button to the top-right of the run history
// detail screen. Click → push RunTableScreen on the same submenu stack so the
// user can switch from "this run, in detail" to "all runs in a list" and
// back via the Back button.
using System;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;

namespace RunTable;

public static class RunHistoryEntryPatch
{
    private const string ButtonName = "RunTableEntryButton";

    [HarmonyPatch(typeof(NRunHistory), "_Ready")]
    public static class NRunHistory_Ready_Postfix
    {
        static void Postfix(NRunHistory __instance)
        {
            try { AddRunTableButton(__instance); }
            catch (Exception ex)
            { GD.PrintErr($"{RunTableMod.LogPrefix}NRunHistory _Ready postfix: {ex}"); }
        }
    }

    private const string BannerTexPath  = "res://images/atlases/ui_atlas.sprites/back_button.tres";
    private const string OutlineTexPath = "res://images/atlases/compressed.sprites/back_button_outline.tres";

    private static void AddRunTableButton(NRunHistory rh)
    {
        if (rh.HasNode(ButtonName)) return;

        // The button is a small composite: a hover-clickable Button
        // sized like the visible banner area, with two scaled
        // TextureRects underneath (red banner + cream outline ripped
        // straight from the game's NBackButton). The arrow icon is
        // simply not added. A MegaLabel rides on top for the text.
        var bannerTex  = ResourceLoader.Load<Texture2D>(BannerTexPath);
        var outlineTex = ResourceLoader.Load<Texture2D>(OutlineTexPath);

        var root = new Control { Name = ButtonName };
        root.AnchorLeft = 0; root.AnchorRight = 0;
        root.AnchorTop  = 0; root.AnchorBottom = 0;
        // Banner anchored to the LEFT edge of the screen (sidebar
        // side); width stays 460 (the proportion that looked right
        // before). Both offsets shift together by `slidePast` so the
        // pennant tail hangs off the left edge of the screen without
        // stretching the texture.
        // Vertical OffsetTop/Bottom get re-derived by AlignToIconRow.
        const float bannerW = 460f;
        const float slidePast = 150f;
        root.OffsetLeft  = -slidePast;
        root.OffsetRight = bannerW - slidePast;
        root.OffsetTop  = 280;  root.OffsetBottom = 380;
        root.MouseFilter = Control.MouseFilterEnum.Pass;

        // Helper to size a child texture to fill root.
        void Fill(Control c)
        {
            c.AnchorLeft = 0; c.AnchorRight = 1;
            c.AnchorTop = 0; c.AnchorBottom = 1;
            c.OffsetLeft = 0; c.OffsetRight = 0;
            c.OffsetTop = 0; c.OffsetBottom = 0;
            c.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        // Layered draw order, back to front:
        //   1) Shadow — banner texture, offset + dark + alpha.
        //   2) Outline (glow) — same banner shape but slightly scaled
        //      up and sat *behind* the banner. Transparent at rest;
        //      tweens to a soft gold on hover, only the outer halo
        //      shows (the banner covers everything inside it). This
        //      matches NBackButton's "edges glow" hover behavior
        //      without painting the whole interior gold.
        //   3) Banner — modulated red.
        //   4) Label — cream w/ dark brown outline, on top.
        // Source texture's notches point left (back button lives
        // bottom-left). For a LEFT-side banner we want notches
        // pointing toward the left screen edge — that's the natural
        // orientation, so no FlipH here.
        const bool flip = false;

        if (bannerTex != null)
        {
            var shadow = new TextureRect
            {
                Texture = bannerTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = new Color(0f, 0f, 0f, 0.55f),
                FlipH = flip,
            };
            Fill(shadow);
            shadow.OffsetLeft  += 4;  shadow.OffsetRight  += 4;
            shadow.OffsetTop   += 5;  shadow.OffsetBottom += 5;
            root.AddChild(shadow);
        }
        TextureRect? outlineNode = null;
        if (outlineTex != null)
        {
            // The outline texture is authored so its outline pixels
            // already extend past the banner-shape boundary — that's
            // how NBackButton gets an even-thickness glow. Render it
            // at the same rect as the banner (no expansion); the
            // outline naturally peeks past the banner edges.
            outlineNode = new TextureRect
            {
                Texture = outlineTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = Colors.Transparent,
                FlipH = flip,
            };
            Fill(outlineNode);
            root.AddChild(outlineNode);
        }
        if (bannerTex != null)
        {
            var banner = new TextureRect
            {
                Texture = bannerTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                FlipH = flip,
            };
            Fill(banner);
            // The back_button atlas has warm tones (red/brown) baked
            // into its shadows. A plain Modulate × green leaves the
            // shadows muddy olive. Use a recolor shader that pulls
            // each pixel's luminance and tints it by the target hue
            // — this hands us a clean monochromatic green that
            // preserves the texture's grain.
            banner.Material = BuildRecolorMaterial(new Color(0.18f, 0.55f, 0.20f, 1f), brightness: 2.1f);
            root.AddChild(banner);
        }

        // MegaLabel defaults to AutoSize: it scales the font to fill
        // the rect, which is why bumping the banner's height pulled
        // the text up with it. The font_size theme override is also
        // ignored when AutoSize is on. Trick: pin MinFontSize and
        // MaxFontSize to the same value — the auto-sizer's binary
        // search collapses to that single value as long as the text
        // fits, giving us an effective fixed-size label.
        var label = new MegaLabel
        {
            Text = "Browse Runs",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            MinFontSize = 28,
            MaxFontSize = 28,
        };
        label.AddThemeFontOverride("font", GameTheme.KreonBoldTooltip);
        label.AddThemeColorOverride("font_color", new Color(0.96f, 0.90f, 0.78f, 1f));
        // Dark brown text outline so the cream pops against the red,
        // matching the arrow icon's outline on the real back button.
        label.AddThemeColorOverride("font_outline_color", new Color(0.18f, 0.07f, 0.04f, 1f));
        label.AddThemeConstantOverride("outline_size", 6);
        Fill(label);
        // Lift the label a few pixels — the banner has more visual
        // weight on the bottom (the pennant tail dips down), so true
        // pixel-center reads as slightly low.
        label.OffsetTop    -= 6;
        label.OffsetBottom -= 6;
        // Small rightward nudge so the text reads as visually centered
        // on the visible (right-of-tail) portion of the banner.
        label.OffsetLeft  += 15;
        label.OffsetRight += 15;
        root.AddChild(label);

        // Invisible Button sized to root so we keep proper hover/press
        // behaviour without re-implementing it.
        var hit = new Button { Flat = true, FocusMode = Control.FocusModeEnum.None };
        Fill(hit);
        hit.MouseFilter = Control.MouseFilterEnum.Stop;
        hit.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        // Hover: fade the halo (outline, which sits *behind* the
        // banner, slightly oversized) from transparent to a soft
        // alpha-gold. Because the banner covers the halo's interior,
        // only the edges show — reading as a glow, not a recolor.
        var outlineForHover = outlineNode;
        var glow = new Color(0.95f, 0.74f, 0.10f, 0.75f);
        hit.MouseEntered += () =>
        {
            try
            {
                if (outlineForHover != null && GodotObject.IsInstanceValid(outlineForHover))
                    outlineForHover.CreateTween().TweenProperty(outlineForHover, "modulate", glow, 0.10);
            } catch { }
        };
        hit.MouseExited += () =>
        {
            try
            {
                if (outlineForHover != null && GodotObject.IsInstanceValid(outlineForHover))
                    outlineForHover.CreateTween().TweenProperty(outlineForHover, "modulate", Colors.Transparent, 0.25);
            } catch { }
        };
        hit.Pressed += () => OpenRunTable(rh);
        root.AddChild(hit);

        rh.AddChild(root);
        ScheduleAlign(root, rh, 0);
    }

    private static void ScheduleAlign(Control btn, NRunHistory rh, int attempt)
    {
        if (attempt > 30) return;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(btn) || !GodotObject.IsInstanceValid(rh)) return;
            bool aligned = AlignToIconRow(btn, rh);
            if (!aligned) ScheduleAlign(btn, rh, attempt + 1);
        }).CallDeferred();
    }

    // Returns true once positioned using a real, non-degenerate icon
    // rect — caller uses that as the signal to stop retrying.
    private static bool AlignToIconRow(Control btn, NRunHistory rh)
    {
        try
        {
            var mph = rh.GetNodeOrNull<NMapPointHistory>("%MapPointHistory");
            if (mph == null) return false;
            var firstEntry = FindFirstEntry(mph);
            if (firstEntry == null) return false;
            var rect = firstEntry.GetGlobalRect();
            if (rect.Size.Y <= 1f) return false;
            float centerYGlobal = rect.Position.Y + rect.Size.Y * 0.5f;
            float centerYLocal = centerYGlobal - rh.GlobalPosition.Y;
            if (centerYLocal < 0 || centerYLocal > 4000) return false;
            float h = btn.OffsetBottom - btn.OffsetTop;
            if (h <= 0) h = 64;
            btn.OffsetTop = centerYLocal - h * 0.5f;
            btn.OffsetBottom = centerYLocal + h * 0.5f;
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}AlignToIconRow: {ex.Message}");
            return false;
        }
    }

    // Recolor shader: extracts each pixel's luminance and multiplies
    // by the desired tint, dropping the source texture's underlying
    // hue entirely. Brightness scalar lets us compensate for the
    // luminance step (a 1.0 tint with no brightness would render
    // about half as bright as the source).
    private static ShaderMaterial BuildRecolorMaterial(Color tint, float brightness)
    {
        var shader = new Shader();
        shader.Code = @"
shader_type canvas_item;
uniform vec3 tint : source_color = vec3(1.0);
uniform float brightness : hint_range(0.0, 4.0) = 1.0;
void fragment() {
    vec4 tex = texture(TEXTURE, UV);
    float lum = dot(tex.rgb, vec3(0.299, 0.587, 0.114));
    COLOR = vec4(tint * lum * brightness, tex.a);
}
";
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("tint", new Vector3(tint.R, tint.G, tint.B));
        mat.SetShaderParameter("brightness", brightness);
        return mat;
    }

    private static NMapPointHistoryEntry? FindFirstEntry(Godot.Node root)
    {
        foreach (var ch in root.GetChildren())
        {
            if (ch is NMapPointHistoryEntry e && GodotObject.IsInstanceValid(e)) return e;
            var deep = FindFirstEntry(ch);
            if (deep != null) return deep;
        }
        return null;
    }

    private static bool _backBtnDumped;
    public static void DumpNBackButtonTree()
    {
        if (_backBtnDumped) return;
        _backBtnDumped = true;
        try
        {
            string[] candidates = new[]
            {
                "res://scenes/screens/character_select_screen.tscn",
                "res://scenes/screens/map/map_screen.tscn",
                "res://scenes/screens/custom_run/custom_run_load_screen.tscn",
            };
            foreach (var path in candidates)
            {
                if (!ResourceLoader.Exists(path)) continue;
                var packed = ResourceLoader.Load<PackedScene>(path);
                if (packed == null) continue;
                var orphan = packed.Instantiate<Godot.Node>(PackedScene.GenEditState.Disabled);
                if (orphan == null) continue;
                var bb = FindFirstNBackButton(orphan);
                if (bb != null)
                {
                    GD.Print($"[RunTable] === NBackButton tree from {path} ===");
                    Walk(bb, 0);
                }
                orphan.QueueFree();
                if (bb != null) return;
            }
        }
        catch (Exception ex) { GD.PrintErr($"[RunTable] dump: {ex.Message}"); }
    }
    private static Godot.Node? FindFirstNBackButton(Godot.Node root)
    {
        if (root is MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton) return root;
        foreach (var ch in root.GetChildren())
        {
            var hit = FindFirstNBackButton(ch);
            if (hit != null) return hit;
        }
        return null;
    }
    private static void Walk(Godot.Node n, int depth)
    {
        string indent = new string(' ', depth * 2);
        string info = n.GetType().Name + "/" + n.Name;
        if (n is TextureRect tr && tr.Texture != null)
            info += $" tex={tr.Texture.ResourcePath}";
        if (n is NinePatchRect np && np.Texture != null)
            info += $" 9p={np.Texture.ResourcePath}";
        if (n is Sprite2D sp && sp.Texture != null)
            info += $" sp={sp.Texture.ResourcePath}";
        GD.Print($"[RunTable] BACK {indent}{info}");
        foreach (var c in n.GetChildren()) Walk(c, depth + 1);
    }

    private static void OpenRunTable(NRunHistory rh)
    {
        try
        {
            var stackField = typeof(NSubmenu).GetField("_stack",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stack = stackField?.GetValue(rh) as NSubmenuStack;
            if (stack == null)
            { GD.PrintErr($"{RunTableMod.LogPrefix}NRunHistory→RunTable: stack null."); return; }
            // Coming from a single-run detail page, the user's intent is
            // "show me the list" — drop any badge filter so they see
            // everything. Other filters carry over via RunFilterState.
            RunFilterState.BadgeFilters.Clear();
            RunTableScreen.CreateAndPush(stack);
        }
        catch (Exception ex)
        { GD.PrintErr($"{RunTableMod.LogPrefix}NRunHistory→RunTable: {ex}"); }
    }
}
