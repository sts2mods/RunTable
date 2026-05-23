// RunTableData — scans all run-history files via SaveManager and builds the
// data structures the Run Table UI needs. The game already stores per-run
// badges in RunHistoryPlayer.Badges, so we don't need to re-derive logic;
// we just read them out and index them.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace RunTable;

public sealed record RunRecord(
    string FileName,
    long StartTime,
    bool Win,
    bool WasAbandoned,
    int Ascension,
    bool IsMultiplayer,
    GameMode GameMode,
    string CharacterId,                     // the LOCAL player's character
    ulong LocalPlayerId,                    // the LOCAL player's net id
    List<SerializableBadge> LocalBadges,    // badges this run awarded to local player
    // ─── Stats for the rich run-preview card (final values for the local player) ──
    int FinalHp,
    int MaxHp,
    int FinalGold,
    int Floor,                              // total rooms entered across all acts
    float RunTime,                          // seconds
    int MaxPotionSlots,
    List<SerializablePotion> Potions
);

public sealed record BadgeKey(string Id, BadgeRarity Rarity);

// Identifier for items that can be searched on the Run Table.
// Cards, relics, and potions all share one keyspace so the search bar
// can offer a single mixed-typeahead and we can intersect a single
// item → runs index regardless of category.
public enum SearchItemType { Card, Relic, Potion }
public readonly record struct SearchItemKey(SearchItemType Type, ModelId Id);

public static class RunTableData
{
    private static readonly object _lock = new();
    private static List<RunRecord>? _runs;
    private static Dictionary<BadgeKey, List<RunRecord>>? _badgeToRuns;
    // Per-item → set of run-file names that touched (started with,
    // gained, bought, transformed into) that item. Built during the
    // startup preload while we already have each RunHistory loaded —
    // no extra I/O at search time.
    private static Dictionary<SearchItemKey, HashSet<string>>? _itemToRuns;

    /// <summary>All loaded runs (oldest first), or empty list on failure.</summary>
    public static IReadOnlyList<RunRecord> AllRuns
    {
        get
        {
            EnsureLoaded();
            return _runs ?? (IReadOnlyList<RunRecord>)Array.Empty<RunRecord>();
        }
    }

    /// <summary>Lookup: (badge id, rarity) → all runs that earned it (by the local player).</summary>
    public static IReadOnlyDictionary<BadgeKey, List<RunRecord>> BadgeToRuns
    {
        get
        {
            EnsureLoaded();
            return _badgeToRuns ?? new Dictionary<BadgeKey, List<RunRecord>>();
        }
    }

    /// <summary>Lookup: (item type, id) → set of run-file names containing it.</summary>
    public static IReadOnlyDictionary<SearchItemKey, HashSet<string>> ItemToRuns
    {
        get
        {
            EnsureLoaded();
            return _itemToRuns ?? new Dictionary<SearchItemKey, HashSet<string>>();
        }
    }

    /// <summary>Force a reload on next access (e.g. after the user finishes a run).</summary>
    public static void Invalidate()
    {
        lock (_lock)
        {
            _runs = null;
            _badgeToRuns = null;
            _itemToRuns = null;
        }
    }

    private static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_runs != null && _badgeToRuns != null) return;
            try
            {
                LoadAllRuns();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RunTableMod.LogPrefix}RunTableData load failed: {ex}");
                _runs = new List<RunRecord>();
                _badgeToRuns = new Dictionary<BadgeKey, List<RunRecord>>();
            }
        }
    }

    private static void LoadAllRuns()
    {
        var sm = SaveManager.Instance;
        var names = sm?.GetAllRunHistoryNames() ?? new List<string>();
        var runs = new List<RunRecord>(names.Count);
        var byBadge = new Dictionary<BadgeKey, List<RunRecord>>();
        var byItem = new Dictionary<SearchItemKey, HashSet<string>>();

        void AddItem(SearchItemType type, ModelId id, string runFile)
        {
            if (id == null || id.Equals(ModelId.none)) return;
            var key = new SearchItemKey(type, id);
            if (!byItem.TryGetValue(key, out var set))
            {
                set = new HashSet<string>();
                byItem[key] = set;
            }
            set.Add(runFile);
        }

        ulong localId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);

        int skipped = 0;
        foreach (var name in names)
        {
            try
            {
                var result = sm!.LoadRunHistory(name);
                if (!result.Success || result.SaveData == null) { skipped++; continue; }
                var h = result.SaveData;

                // Find the local player in the run. SP runs have a single player;
                // MP runs have several. We match by net id, falling back to first player.
                var localPlayer =
                    h.Players.FirstOrDefault(p => p.Id == localId)
                    ?? h.Players.FirstOrDefault();
                if (localPlayer == null) { skipped++; continue; }

                // Final HP / gold / floor from the last MapPointHistory entry,
                // if any. Otherwise fall back to character defaults (rare).
                int finalHp = 0, maxHp = 0, finalGold = 0, floor = 0;
                if (h.MapPointHistory != null && h.MapPointHistory.Count > 0)
                {
                    foreach (var rooms in h.MapPointHistory) floor += rooms?.Count ?? 0;
                    var lastAct = h.MapPointHistory[h.MapPointHistory.Count - 1];
                    if (lastAct != null && lastAct.Count > 0)
                    {
                        var lastEntry = lastAct[lastAct.Count - 1];
                        var stat = lastEntry?.PlayerStats?.FirstOrDefault(s => s.PlayerId == localPlayer.Id);
                        if (stat != null)
                        {
                            finalHp   = stat.CurrentHp;
                            maxHp     = stat.MaxHp;
                            finalGold = stat.CurrentGold;
                        }
                    }
                }

                var rec = new RunRecord(
                    FileName: name,
                    StartTime: h.StartTime,
                    Win: h.Win,
                    WasAbandoned: h.WasAbandoned,
                    Ascension: h.Ascension,
                    IsMultiplayer: h.Players.Count > 1,
                    GameMode: h.GameMode,
                    CharacterId: localPlayer.Character?.ToString() ?? "",
                    LocalPlayerId: localPlayer.Id,
                    LocalBadges: (localPlayer.Badges ?? Enumerable.Empty<SerializableBadge>()).ToList(),
                    FinalHp: finalHp,
                    MaxHp: maxHp,
                    FinalGold: finalGold,
                    Floor: floor,
                    RunTime: h.RunTime,
                    MaxPotionSlots: localPlayer.MaxPotionSlotCount,
                    Potions: (localPlayer.Potions ?? Enumerable.Empty<SerializablePotion>()).ToList()
                );
                runs.Add(rec);

                foreach (var b in rec.LocalBadges)
                {
                    var key = new BadgeKey(b.Id, b.Rarity);
                    if (!byBadge.TryGetValue(key, out var list))
                    {
                        list = new List<RunRecord>();
                        byBadge[key] = list;
                    }
                    list.Add(rec);
                }

                // ── Per-item index ─────────────────────────────────────
                // Character's starting kit (CharacterModel exposes
                // StartingDeck/Relics/Potions). Wrapped in try/catch
                // because mod-deleted characters or missing models
                // shouldn't kill the whole preload.
                try
                {
                    var ch = ModelDb.GetByIdOrNull<CharacterModel>(localPlayer.Character ?? ModelId.none);
                    if (ch != null)
                    {
                        foreach (var c in ch.StartingDeck)    AddItem(SearchItemType.Card,   c.Id, name);
                        foreach (var r in ch.StartingRelics)  AddItem(SearchItemType.Relic,  r.Id, name);
                        foreach (var p in ch.StartingPotions) AddItem(SearchItemType.Potion, p.Id, name);
                    }
                }
                catch { /* tolerate */ }

                // Items acquired across the run. We index on first
                // sighting per run (HashSet dedups), so the same item
                // appearing in multiple acts doesn't matter.
                if (h.MapPointHistory != null)
                {
                    foreach (var act in h.MapPointHistory)
                    {
                        if (act == null) continue;
                        foreach (var room in act)
                        {
                            if (room == null) continue;
                            PlayerMapPointHistoryEntry? entry;
                            try { entry = room.GetEntry(localPlayer.Id); }
                            catch { continue; }
                            if (entry == null) continue;
                            foreach (var c in entry.CardsGained)        AddItem(SearchItemType.Card,   c.Id, name);
                            foreach (var id in entry.BoughtColorless)   AddItem(SearchItemType.Card,   id,   name);
                            foreach (var t in entry.CardsTransformed)   AddItem(SearchItemType.Card,   t.FinalCard.Id, name);
                            foreach (var c in entry.CardsRemoved)       AddItem(SearchItemType.Card,   c.Id, name);
                            foreach (var pick in entry.RelicChoices)
                            {
                                if (pick.wasPicked) AddItem(SearchItemType.Relic, pick.choice, name);
                            }
                            foreach (var id in entry.BoughtRelics)      AddItem(SearchItemType.Relic,  id, name);
                            foreach (var pick in entry.PotionChoices)
                            {
                                if (pick.wasPicked) AddItem(SearchItemType.Potion, pick.choice, name);
                            }
                            foreach (var id in entry.BoughtPotions)     AddItem(SearchItemType.Potion, id, name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                skipped++;
                GD.PrintErr($"{RunTableMod.LogPrefix}Run {name}: {ex.Message}");
            }
        }

        // Newest first for display purposes.
        runs.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));
        foreach (var kv in byBadge) kv.Value.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));

        _runs = runs;
        _badgeToRuns = byBadge;
        _itemToRuns = byItem;
        GD.Print($"{RunTableMod.LogPrefix}Loaded {runs.Count} runs ({skipped} skipped), {byBadge.Count} unique badges, {byItem.Count} unique items.");
    }

    // ----- filtering helpers used by the UI -----

    public sealed class Filter
    {
        public HashSet<string>? Characters;        // null = any
        public HashSet<BadgeRarity>? Rarities;     // null = any; applied per-badge, not per-run
        public int AscMin = 0;
        public int AscMax = 10;
        public bool? IsMultiplayer;
        public bool? Win;
        public bool? Abandoned;
        public GameMode? GameMode;

        public bool Matches(RunRecord r)
        {
            if (Characters != null && !Characters.Contains(r.CharacterId)) return false;
            if (r.Ascension < AscMin || r.Ascension > AscMax) return false;
            if (IsMultiplayer.HasValue && r.IsMultiplayer != IsMultiplayer.Value) return false;
            if (Win.HasValue && r.Win != Win.Value) return false;
            if (Abandoned.HasValue && r.WasAbandoned != Abandoned.Value) return false;
            if (GameMode.HasValue && r.GameMode != GameMode.Value) return false;
            return true;
        }
    }

    /// <summary>
    /// Count how many filtered runs earned each (badge id, rarity) — used to
    /// drive the live counters as the user adjusts filters.
    /// </summary>
    public static Dictionary<BadgeKey, int> CountByBadge(Filter f)
    {
        var counts = new Dictionary<BadgeKey, int>();
        foreach (var r in AllRuns)
        {
            if (!f.Matches(r)) continue;
            foreach (var b in r.LocalBadges)
            {
                var k = new BadgeKey(b.Id, b.Rarity);
                counts[k] = counts.GetValueOrDefault(k) + 1;
            }
        }
        return counts;
    }
}
