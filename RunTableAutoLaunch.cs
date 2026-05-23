// RunTableAutoLaunch — dev-iteration shortcut.
//
// When the file /tmp/sts2_open_run_table.flag exists at game boot, we
// auto-navigate Main Menu → Compendium → Badges and delete the
// flag. Lets tools/dev_iterate.sh do "build → quit → launch → screenshot"
// without manual clicking.
//
// Two-stage deferred chain: wait for NMainMenu._Ready to fully finish,
// then call its public OpenCompendiumSubmenu(), then wait for that
// submenu's _Ready to finish, then push BadgesScreen onto the same stack
// via the existing CreateAndPush helper.
using System;
using System.IO;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace RunTable;

public static class RunTableAutoLaunch
{
    public const string FlagPath        = "/tmp/sts2_open_run_table.flag";
    public const string CardLibraryFlag = "/tmp/sts2_open_card_library.flag";
    // If present alongside FlagPath, after RunTable opens we also push
    // the Run Table on top — lets dev_iterate.sh screenshot the table.
    public const string RunTableFlag    = "/tmp/sts2_open_run_table.flag";

    [HarmonyPatch(typeof(NMainMenu), "_Ready")]
    public static class NMainMenu_Ready_AutoLaunch_Postfix
    {
        static void Postfix(NMainMenu __instance)
        {
            try
            {
                bool openBadge = File.Exists(FlagPath);
                bool openCards = File.Exists(CardLibraryFlag);
                if (!openBadge && !openCards) return;
                try { if (openBadge) File.Delete(FlagPath); } catch { /* best-effort */ }
                try { if (openCards) File.Delete(CardLibraryFlag); } catch { /* best-effort */ }
                string target = openBadge ? "Badges" : "Card Library";
                GD.Print($"{RunTableMod.LogPrefix}AutoLaunch: flag found, opening {target}…");

                // Wait a beat so the main menu's tween/initial focus has settled.
                __instance.GetTree().CreateTimer(0.8).Timeout += () =>
                {
                    if (openCards) OpenCompendiumThenCardLibrary(__instance);
                    else           OpenCompendiumThenRunTable(__instance);
                };
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RunTableMod.LogPrefix}AutoLaunch postfix failed: {ex}");
            }
        }
    }

    private static void OpenCompendiumThenCardLibrary(NMainMenu mainMenu)
    {
        try
        {
            var compendium = mainMenu.OpenCompendiumSubmenu();
            if (compendium == null) return;
            compendium.GetTree().CreateTimer(0.5).Timeout += () =>
            {
                try
                {
                    // Click the "Cards" button (CardLibraryButton) inside the compendium.
                    var cardsBtn = compendium.GetNodeOrNull<Node>("%CardLibraryButton");
                    if (cardsBtn != null)
                    {
                        cardsBtn.EmitSignal("Released", cardsBtn);
                        GD.Print($"{RunTableMod.LogPrefix}AutoLaunch: opened Card Library.");
                    }
                    else
                    {
                        GD.PrintErr($"{RunTableMod.LogPrefix}AutoLaunch: CardLibraryButton not found.");
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RunTableMod.LogPrefix}AutoLaunch: open card library failed: {ex}");
                }
            };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}AutoLaunch: open compendium (cards) failed: {ex}");
        }
    }

    private static void OpenCompendiumThenRunTable(NMainMenu mainMenu)
    {
        try
        {
            var compendium = mainMenu.OpenCompendiumSubmenu();
            if (compendium == null)
            {
                GD.PrintErr($"{RunTableMod.LogPrefix}AutoLaunch: OpenCompendiumSubmenu returned null.");
                return;
            }
            // Give NCompendiumSubmenu._Ready + our button-injection patch a
            // moment to run before we reach into its stack.
            compendium.GetTree().CreateTimer(0.5).Timeout += () => PushRunTable(compendium);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}AutoLaunch: open compendium failed: {ex}");
        }
    }

    private static void PushRunTable(Node compendium)
    {
        try
        {
            var stackField = typeof(NSubmenu).GetField("_stack",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stack = stackField?.GetValue(compendium) as NSubmenuStack;
            if (stack == null)
            {
                GD.PrintErr($"{RunTableMod.LogPrefix}AutoLaunch: stack unavailable on compendium.");
                return;
            }
            BadgesScreen.CreateAndPush(stack);
            GD.Print($"{RunTableMod.LogPrefix}AutoLaunch: pushed Badges.");

            // If the run-table flag is also set, push Run Table on top
            // after a short delay (lets RunTable finish layout first).
            if (File.Exists(RunTableFlag))
            {
                try { File.Delete(RunTableFlag); } catch { /* best-effort */ }
                compendium.GetTree().CreateTimer(0.6).Timeout += () =>
                {
                    try
                    {
                        RunTableScreen.CreateAndPush(stack);
                        GD.Print($"{RunTableMod.LogPrefix}AutoLaunch: pushed Run Table.");
                    }
                    catch (Exception ex)
                    { GD.PrintErr($"{RunTableMod.LogPrefix}AutoLaunch: push RunTable failed: {ex}"); }
                };
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}AutoLaunch: push RunTable failed: {ex}");
        }
    }
}
