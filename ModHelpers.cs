// Small set of mod-wide helpers, ported from patterns we liked in
// Alchyr's BaseLib-StS2 (NuGet: Alchyr.Sts2.BaseLib). We deliberately
// don't take BaseLib as a hard dependency — these patterns are tiny
// and self-contained, and copying them keeps the mod single-DLL.
//
// What's here:
//   • TryPatchAll — like Harmony.PatchAll, but runs each annotated
//     class through its own try/catch so a single signature
//     mismatch (game update breaks one patch) doesn't abort every
//     other patch in the assembly. Logs success / failure counts.
//   • AddThemeFontSizeOverrideAll — one call covers every theme
//     font-size variant (font_size, normal_font_size, bold_font_size,
//     italics_font_size, bold_italics_font_size, mono_font_size).
using System;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace RunTable;

internal static class ModHelpers
{
    public static void TryPatchAll(Harmony harmony, Assembly assembly, string logPrefix)
    {
        int ok = 0, fail = 0;
        foreach (var type in assembly.GetTypes())
        {
            var attrs = HarmonyMethodExtensions.GetFromType(type);
            if (attrs == null || attrs.Count == 0) continue;
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                GD.PrintErr($"{logPrefix}patch {type.Name} skipped: {ex.Message}");
            }
        }
        GD.Print($"{logPrefix}Harmony: {ok} ok, {fail} failed");
    }

    private static readonly string[] AllFontSizeKeys =
    {
        "font_size",
        "normal_font_size",
        "bold_font_size",
        "italics_font_size",
        "bold_italics_font_size",
        "mono_font_size",
    };

    public static void AddThemeFontSizeOverrideAll(this Control control, int fontSize)
    {
        foreach (var k in AllFontSizeKeys) control.AddThemeFontSizeOverride(k, fontSize);
    }
}
