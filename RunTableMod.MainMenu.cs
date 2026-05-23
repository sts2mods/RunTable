// Main-menu and Compendium-submenu integration.
//
// Goal: by the time the user opens the Compendium or Run Table, both
// the JSON run-history load (RunTableData.AllRuns) and the run-card
// pre-build (RunTableScreen.StartCachePreBuild) are already done — opening
// is instant.
//
// Previously: RunTableData was loaded lazily inside StartCachePreBuild,
// which itself fired from NCompendiumSubmenu._Ready. That meant the
// half-second-plus JSON-load happened *while* the Compendium was opening,
// blocking the main thread and producing the visible lag.
//
// New flow: NMainMenu._Ready Postfix kicks off the data load on a
// background thread (RunTableData uses a lock for thread safety) and
// starts the chunked card pre-build on the main thread immediately
// after the load completes. NCompendiumSubmenu._Ready still calls
// StartCachePreBuild as a fallback — both calls are idempotent.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace RunTable;

public static class RunTableMenuPatch
{
    // Tracks whether we've already kicked off the startup preload —
    // NMainMenu._Ready can fire more than once over a session
    // (LoadMainMenu re-instantiates it on return-from-run).
    private static bool _startupPreloadKicked;

    [HarmonyPatch(typeof(NMainMenu), "_Ready")]
    public static class NMainMenu_Ready_Postfix
    {
        static void Postfix(NMainMenu __instance)
        {
            try
            {
                // Background data load — JSON I/O off the main thread so
                // the menu animation stays smooth. RunTableData.AllRuns
                // is lock-guarded, so concurrent access by the compendium
                // path is safe (it'll just wait until the load finishes).
                if (!_startupPreloadKicked)
                {
                    _startupPreloadKicked = true;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            int n = RunTableData.AllRuns.Count;
                            sw.Stop();
                            GD.Print($"{RunTableMod.LogPrefix}startup preload: {n} runs in {sw.ElapsedMilliseconds}ms");
                            // Now that data is in memory, hop back to the
                            // main thread to start chunked card-building.
                            // Godot Controls must be constructed on the
                            // main thread, so we defer via Callable.From.
                            var captured = __instance;
                            Callable.From(() =>
                            {
                                if (GodotObject.IsInstanceValid(captured))
                                    RunTableScreen.StartCachePreBuild(captured);
                            }).CallDeferred();
                        }
                        catch (Exception ex)
                        {
                            GD.PrintErr($"{RunTableMod.LogPrefix}startup preload failed: {ex}");
                        }
                    });
                }
                else
                {
                    // Subsequent menu loads (e.g. returning from a run):
                    // data is already cached, just make sure the card
                    // cache is up to date.
                    RunTableScreen.StartCachePreBuild(__instance);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RunTableMod.LogPrefix}MainMenu _Ready postfix: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(NCompendiumSubmenu), "_Ready")]
    public static class NCompendiumSubmenu_Ready_Postfix
    {
        static void Postfix(NCompendiumSubmenu __instance)
        {
            // Idempotent fallback — if the user opens the compendium
            // before the startup preload completes, this no-ops because
            // StartCachePreBuild already checks _cacheBuilding /
            // cache-complete.
            try
            {
                RunTableScreen.StartCachePreBuild(__instance);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RunTableMod.LogPrefix}Compendium _Ready postfix: {ex}");
            }
        }
    }
}
