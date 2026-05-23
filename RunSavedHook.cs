// When a run ends, invalidate the in-memory run cache so the next
// time the user opens the Run Table the just-finished run shows up.
//
// RunTableData loads every RunHistory file once at startup and
// caches the result; without an invalidation point, a run that
// finishes mid-session is on disk but missing from the cache until
// the game is restarted. NGameOverScreen._Ready fires after the
// game has already saved the new RunHistory file, which is the
// right moment to drop our cache.
//
// RunTableScreen's UI cardCache repopulates lazily — StartCachePreBuild
// skips entries it's already built, so re-running it after the
// RunTableData reload only constructs the one new run-card.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;

namespace RunTable;

[HarmonyPatch(typeof(NGameOverScreen), "_Ready")]
public static class NGameOverScreen_Ready_Patch
{
    static void Postfix()
    {
        try
        {
            RunTableData.Invalidate();
            GD.Print($"{RunTableMod.LogPrefix}invalidated run cache on game over");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RunTableMod.LogPrefix}invalidate on game over: {ex.Message}");
        }
    }
}
