// Shared filter spec used by both menus. The values here drive what's
// visible in the Run Table tiles AND in the Run Table rows; setting a
// filter in one menu carries to the other automatically the next time it
// opens, so the user's intent persists across navigation.
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Runs;

namespace RunTable;

public static class RunFilterState
{
    // ─── character & ascension ─────────────────────────────────────────────
    public static string?    SelectedChar;             // null = any character
    public static int        AscensionMin;             // 0 by default
    public static bool       AscensionExact;           // false = "≥", true = "exact"

    // ─── run-level toggles ─────────────────────────────────────────────────
    public static GameMode?  GameMode;                 // null = any mode
    public static bool?      Win;                      // null = any outcome (Vic/Def)
    public static bool?      Multiplayer;              // null = any (solo/co-op)
    public static bool?      Abandoned;                // null = any (concluded/abandoned)

    // ─── text search (used by Run Table tile filter) ─────────────────────
    public static string     SearchText = "";

    // ─── item search (card / relic / potion) ───────────────────────────────
    // Run Table only shows runs that contained EVERY one of these
    // cards / relics / potions. Picked via the typeahead suggestion
    // popup attached to the search bar; each selection adds a chip.
    public static readonly HashSet<SearchItemKey> SelectedSearchItems = new();

    // ─── badge filters ─────────────────────────────────────────────────────
    // Tier-mode for the Run Table: a single selected rarity tickbox or
    // "Any" / "Tierless" group. Mirrors the original TierFilter enum.
    public enum TierMode { Any, Common, Uncommon, Rare, Tierless }
    public static TierMode Tier = TierMode.Any;

    // Set of specific badges currently filtered. Empty = no badge filter
    // (show all runs in Run Table). Toggled by clicking icons in the Run
    // Table's Badges grid, or by clicking a tile in the Run Table.
    public static readonly HashSet<BadgeKey> BadgeFilters = new();

    // How BadgeFilters combine: Any = run must contain at least one of
    // them; All = run must contain every one of them. Toggled by the
    // ANY/ALL button at the top of the Badges section.
    public enum BadgeMatchMode { Any, All }
    public static BadgeMatchMode BadgeMode = BadgeMatchMode.Any;

    public static void ClearAll()
    {
        SelectedChar = null;
        AscensionMin = 0;
        AscensionExact = false;
        GameMode = null;
        Win = null;
        Multiplayer = null;
        Abandoned = null;
        SearchText = "";
        SelectedSearchItems.Clear();
        Tier = TierMode.Any;
        BadgeFilters.Clear();
        BadgeMode = BadgeMatchMode.Any;
    }

    // True if the run passes all the non-badge filters (character, asc,
    // game mode, win/loss, coop, abandoned). Badge-set membership is
    // checked separately because the two menus use it differently.
    public static bool MatchesRunBasics(RunRecord r)
    {
        if (SelectedChar != null && r.CharacterId != SelectedChar) return false;
        if (AscensionExact ? r.Ascension != AscensionMin
                           : r.Ascension < AscensionMin) return false;
        if (GameMode.HasValue && r.GameMode != GameMode.Value) return false;
        if (Win.HasValue && r.Win != Win.Value) return false;
        if (Multiplayer.HasValue && r.IsMultiplayer != Multiplayer.Value) return false;
        if (Abandoned.HasValue && r.WasAbandoned != Abandoned.Value) return false;
        if (SelectedSearchItems.Count > 0)
        {
            // Intersection: run must contain *every* selected item. One
            // hashset lookup per chip; lists are tiny so this stays
            // cheap even with a dozen+ chips.
            foreach (var item in SelectedSearchItems)
            {
                if (!RunTableData.ItemToRuns.TryGetValue(item, out var set)
                    || !set.Contains(r.FileName)) return false;
            }
        }
        return true;
    }

    // Used by the Run Table: matches basics AND (no badge filter OR
    // the badge filter set's mode-dependent membership check passes).
    public static bool MatchesRunFull(RunRecord r)
    {
        if (!MatchesRunBasics(r)) return false;
        if (BadgeFilters.Count == 0) return true;
        var earned = r.LocalBadges?.Select(b => new BadgeKey(b.Id, b.Rarity))
                                  .ToHashSet() ?? new HashSet<BadgeKey>();
        return BadgeMode == BadgeMatchMode.Any
            ? BadgeFilters.Any(earned.Contains)
            : BadgeFilters.All(earned.Contains);
    }
}
