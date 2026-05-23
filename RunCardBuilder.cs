// Shared run-preview-card builder. Both the Run Table's run modal and the
// Run Table secondary menu render rows that show the same per-run data
// (character icons + ascension, HP, gold, potions, floor, time, outcome,
// date), so we extract it here to keep the layout in one place.
//
// Loads each run's full RunHistory on demand to populate the actual game
// widgets (NRunHistoryPlayerIcon, NPotion/NPotionHolder, top-bar atlas
// textures) — same widgets the run history screen itself uses.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;

namespace RunTable;

public static class RunCardBuilder
{
    // ─── top-bar atlas textures (same paths run_history.tscn uses) ─────────
    public const string TopBarHeart         = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_heart.tres";
    public const string TopBarGold          = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_gold.tres";
    public const string TopBarFloor         = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_floor.tres";
    public const string TopBarTimer         = "res://images/atlases/ui_atlas.sprites/top_bar/timer_icon.tres";
    public const string TopBarCharBackdrop  = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_char_backdrop.tres";

    // ─── per-cell widths (driven by table-header widths in RunTableView) ───
    public const int ColIconsW   = 180;
    public const int ColHpW      = 150;
    public const int ColGoldW    = 120;
    public const int ColPotionW  = 220;   // matches 3 visible slots + fade
    public const int ColFloorW   = 110;
    public const int ColTimeW    = 160;
    public const int ColOutcomeW = 120;
    public const int RowHeight   = 128;
    public const int RowRightGutter = 70;  // matches scrollbar gutter

    // Build a clickable rich card for a single run. onClick receives the
    // run's filename so the caller can navigate to the run history screen.
    public static Control BuildRunCard(RunRecord r, Action<string> onClick)
    {
        var card = new Button { Flat = true };
        card.CustomMinimumSize = new Vector2(0, RowHeight);
        card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var normalBg = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.12f, 0.16f, 0.80f),
            BorderColor = new Color(0.30f, 0.25f, 0.15f, 0.6f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 18, ContentMarginRight = 18,
            ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        var hoverBg = (StyleBoxFlat)normalBg.Duplicate();
        hoverBg.BgColor     = new Color(0.18f, 0.16f, 0.10f, 0.92f);
        hoverBg.BorderColor = new Color(0.55f, 0.45f, 0.20f, 0.95f);
        card.AddThemeStyleboxOverride("normal", normalBg);
        card.AddThemeStyleboxOverride("hover",  hoverBg);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 32);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        card.AddChild(row);

        // Source of truth for every header element — co-op players, real
        // potion models, etc. Loaded lazily here, OK for a few-dozen-row
        // table since LoadRunHistory is JSON-on-disk and quite fast.
        RunHistory? history = null;
        try
        {
            var result = SaveManager.Instance.LoadRunHistory(r.FileName);
            if (result.Success) history = result.SaveData;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}LoadRunHistory({r.FileName}): {ex.Message}");
        }

        row.AddChild(Column(BuildPlayerIcons(r, history), ColIconsW));
        row.AddChild(Column(BuildStatIcon(TopBarHeart, $"{r.FinalHp}/{r.MaxHp}",
                                          new Color(1f, 0.39f, 0.39f), 90), ColHpW));
        row.AddChild(Column(BuildStatIcon(TopBarGold,  $"{r.FinalGold}", GameTheme.Gold, 60), ColGoldW));
        row.AddChild(BuildPotionCell(r, history, visibleSlots: 3));
        row.AddChild(Column(BuildStatIcon(TopBarFloor, $"{r.Floor}", GameTheme.Cream, 56),   ColFloorW));
        row.AddChild(Column(BuildStatIcon(TopBarTimer, FormatTime(r.RunTime), GameTheme.Gold, 100), ColTimeW));

        // Spacer pushes outcome + date to the right (inside the table's
        // right gutter so they don't slip under the scrollbar).
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

        var outcomeText = r.Win ? "Victory" : (r.WasAbandoned ? "Abandoned" : "Defeat");
        var outcomeColor = r.Win
            ? new Color(0.70f, 0.90f, 0.55f)
            : r.WasAbandoned ? new Color(0.85f, 0.65f, 0.25f)
            : new Color(0.88f, 0.45f, 0.45f);
        var outcomeBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        outcomeBox.AddThemeConstantOverride("separation", 2);
        outcomeBox.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        outcomeBox.AddChild(MakeStatLabel(outcomeText, 18, outcomeColor, GameTheme.KreonBoldTooltip));
        outcomeBox.AddChild(MakeStatLabel(DateOf(r.StartTime), 14, GameTheme.Dim, GameTheme.KreonRegTooltip));
        row.AddChild(Column(outcomeBox, ColOutcomeW));

        card.Pressed += () => onClick(r.FileName);
        return card;
    }

    // ─── cells ─────────────────────────────────────────────────────────────

    public static Control Column(Control inner, int width)
    {
        var cell = new Control
        {
            CustomMinimumSize = new Vector2(width, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        cell.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        inner.AnchorLeft = 0; inner.AnchorRight = 0;
        inner.AnchorTop = 0.5f; inner.AnchorBottom = 0.5f;
        inner.OffsetLeft = 0; inner.OffsetRight = 0;
        inner.GrowHorizontal = Control.GrowDirection.End;
        inner.GrowVertical   = Control.GrowDirection.Both;
        cell.AddChild(inner);
        return cell;
    }

    public static Control BuildStatIcon(string texPath, string value, Color valueColor, int labelMinWidth)
    {
        var hb = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        hb.AddThemeConstantOverride("separation", 6);
        var tex = ResourceLoader.Load<Texture2D>(texPath);
        if (tex != null)
        {
            var tr = new TextureRect
            {
                Texture = tex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(42, 56),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            hb.AddChild(tr);
        }
        var lbl = MakeStatLabel(value, 22, valueColor, GameTheme.KreonBoldTooltip);
        lbl.CustomMinimumSize = new Vector2(labelMinWidth, 0);
        lbl.VerticalAlignment = VerticalAlignment.Center;
        hb.AddChild(lbl);
        return hb;
    }

    public static Control BuildPlayerIcons(RunRecord r, RunHistory? history)
    {
        var wrap = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        wrap.CustomMinimumSize = new Vector2(180, 100);

        var hb = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        hb.AddThemeConstantOverride("separation", -8);
        hb.AnchorLeft = 0; hb.AnchorRight = 0;
        hb.AnchorTop = 0.5f; hb.AnchorBottom = 0.5f;
        // 0 offset — the table-wide left padding (set by RunTableView's
        // _runList.OffsetLeft + the card's StyleBox ContentMarginLeft)
        // already provides breathing room from the sidebar.
        hb.OffsetLeft = 0;
        hb.GrowHorizontal = Control.GrowDirection.End;
        hb.GrowVertical = Control.GrowDirection.Both;
        wrap.AddChild(hb);

        if (history?.Players == null || history.Players.Count == 0) return wrap;
        try
        {
            foreach (var player in history.Players)
            {
                var scene = ResourceLoader.Load<PackedScene>(NRunHistoryPlayerIcon.scenePath);
                if (scene == null) continue;
                var icon = scene.Instantiate<NRunHistoryPlayerIcon>();
                if (icon == null) continue;
                icon.MouseFilter = Control.MouseFilterEnum.Ignore;
                bool isLocal = player.Id == r.LocalPlayerId;
                // Local player +1 z so its ascension number sits on top
                // of co-op portraits. Parent _runList rides at a deeply
                // negative z so this stays well below the sidebar's
                // NBadge hover popups (default z=0).
                if (isLocal) icon.ZIndex = 1;
                var capturedIcon = icon;
                var capturedPlayer = player;
                var capturedHistory = history;
                bool capturedIsLocal = isLocal;
                icon.Connect(Node.SignalName.Ready, Callable.From(() =>
                {
                    try
                    {
                        capturedIcon.LoadRun(capturedPlayer, capturedHistory);
                        if (capturedIsLocal) capturedIcon.Select();
                        var inner = capturedIcon.GetNodeOrNull<TextureRect>("Icon");
                        if (inner != null)
                        {
                            inner.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                            inner.PivotOffset = new Vector2(32, 64);
                        }
                    }
                    catch (Exception ex)
                    { GD.PrintErr($"{RunTableMod.LogPrefix}player icon LoadRun: {ex.Message}"); }
                }));
                hb.AddChild(icon);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}BuildPlayerIcons: {ex.Message}");
        }
        return wrap;
    }

    public static Control BuildPotionCell(RunRecord r, RunHistory? history, int visibleSlots)
    {
        int cellWidth = visibleSlots * 60 + 40;
        var cell = new Control
        {
            CustomMinimumSize = new Vector2(cellWidth, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        cell.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var inner = BuildPotionContainer(r, history, visibleSlots);
        inner.AnchorLeft = 0; inner.AnchorRight = 0;
        inner.AnchorTop = 0.5f; inner.AnchorBottom = 0.5f;
        inner.OffsetLeft = 0;
        inner.GrowHorizontal = Control.GrowDirection.End;
        inner.GrowVertical = Control.GrowDirection.Both;
        cell.AddChild(inner);

        // True alpha fade-to-transparent via canvas_group + fragment
        // shader: children render into an offscreen buffer, then the
        // cell composites that buffer ONCE through our shader, which
        // fades COLOR.a toward 0 past UV.x = 0.65. The blurred scene bg
        // shows through smoothly instead of being covered by a black
        // gradient (which made a hard visible edge).
        //
        // Deferred via TreeEntered: the canvas_group buffer is sized
        // from the cell's rect, which only becomes valid after the
        // layout system has placed the cell — applying it at build time
        // (before the cell joins a tree) was a no-op.
        cell.TreeEntered += () =>
        {
            cell.Material = MakeRightFadeMaterial();
            ApplyCanvasGroupTransparent(cell);
        };
        return cell;
    }

    private static ShaderMaterial MakeRightFadeMaterial()
    {
        var shader = new Shader
        {
            Code = @"
shader_type canvas_item;
uniform float fade_start : hint_range(0.0, 1.0) = 0.65;
void fragment() {
    COLOR.a *= 1.0 - smoothstep(fade_start, 1.0, UV.x);
}",
        };
        return new ShaderMaterial { Shader = shader };
    }

    // Turn a Control into a transparent canvas group so its children
    // composite into an offscreen buffer and a Material on the Control
    // affects the final draw (the only way to apply a fragment shader
    // across a Control hierarchy in Godot 4 without a SubViewport).
    public static void ApplyCanvasGroupTransparent(Control c)
    {
        RenderingServer.CanvasItemSetCanvasGroupMode(
            c.GetCanvasItem(),
            RenderingServer.CanvasGroupMode.Transparent
        );
    }

    public static Control BuildPotionContainer(RunRecord r, RunHistory? history, int visibleSlots = 3)
    {
        var localPlayer = history?.Players?.FirstOrDefault(p => p.Id == r.LocalPlayerId);
        int slots = localPlayer?.MaxPotionSlotCount ?? r.MaxPotionSlots;
        if (slots <= 0) slots = 3;

        var outer = new MarginContainer
        {
            CustomMinimumSize = new Vector2(40 * slots + 40, 80),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        outer.AddThemeConstantOverride("margin_top", 4);
        outer.AddThemeConstantOverride("margin_bottom", 8);
        outer.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;

        var bgTex = ResourceLoader.Load<Texture2D>(TopBarCharBackdrop);
        if (bgTex != null)
        {
            var bg = new NinePatchRect
            {
                Texture = bgTex,
                PatchMarginLeft = 32, PatchMarginRight = 32,
                PatchMarginTop = 32,  PatchMarginBottom = 32,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            outer.AddChild(bg);
        }

        var inner = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inner.AddThemeConstantOverride("margin_left", 18);
        inner.AddThemeConstantOverride("margin_top", 5);
        inner.AddThemeConstantOverride("margin_right", 19);
        inner.AddThemeConstantOverride("margin_bottom", 6);
        outer.AddChild(inner);

        var hb = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        hb.AddThemeConstantOverride("separation", 2);
        inner.AddChild(hb);

        try
        {
            Player? owner = null;
            if (localPlayer != null)
            {
                var character = SaveUtil.CharacterOrDeprecated(localPlayer.Character);
                var unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
                owner = Player.CreateForNewRun(character, unlockState, localPlayer.Id);
            }
            var potions = localPlayer?.Potions?.Select(PotionModel.FromSerializable).ToList()
                          ?? new List<PotionModel>();
            for (int i = 0; i < slots; i++)
            {
                var holder = NPotionHolder.Create(isUsable: false);
                if (holder == null) continue;
                holder.MouseFilter = Control.MouseFilterEnum.Ignore;
                PotionModel? potionModel = i < potions.Count ? potions[i] : null;
                var capturedHolder = holder;
                var capturedModel  = potionModel;
                var capturedOwner  = owner;
                holder.Connect(Node.SignalName.Ready, Callable.From(() =>
                {
                    if (capturedModel == null || capturedOwner == null) return;
                    try
                    {
                        var potion = NPotion.Create(capturedModel);
                        if (potion == null) return;
                        potion.Model.Owner = capturedOwner;
                        capturedHolder.AddPotion(potion);
                        potion.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                    }
                    catch (Exception ex)
                    { GD.PrintErr($"{RunTableMod.LogPrefix}AddPotion failed: {ex.Message}"); }
                }));
                hb.AddChild(holder);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}BuildPotionContainer: {ex.Message}");
        }
        return outer;
    }

    // ─── small helpers ─────────────────────────────────────────────────────

    public static MegaLabel MakeStatLabel(string text, int size, Color color, FontVariation font)
    {
        var l = new MegaLabel { Text = text };
        l.AutoSizeEnabled = false;
        l.MinFontSize = size; l.MaxFontSize = size;
        l.AddThemeFontOverride("font", font);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        l.AddThemeConstantOverride("shadow_offset_x", 3);
        l.AddThemeConstantOverride("shadow_offset_y", 2);
        return l;
    }

    public static string FormatTime(float seconds)
    {
        if (seconds <= 0) return "0:00";
        int total = (int)seconds;
        int h = total / 3600;
        int m = (total % 3600) / 60;
        int s = total % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    public static string DateOf(long unixSeconds)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("MMM d"); }
        catch { return "?"; }
    }
}
