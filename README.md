# Run Table

The vanilla Run History is a "next / previous" navigator — there's no index page. I wanted to be able to search through my runs with different types of filters(you can search by cards/relics/potions in the search bar, filter by badges, victory/loss, custom/daily/normal, and more probably).

I originally made this as a badges viewer then realized I was building a full run history searcher so I made it into that with a submenu to view the in-game badges.

click browse runs on the run history page to get to the runtable
<img width="511" height="541" alt="Screenshot 2026-05-23 at 12 18 05 PM" src="https://github.com/user-attachments/assets/f1de82c6-f3e8-4819-aab8-7d557e034b65" />

Here you can see I have 32 runs that were ascension 5 or greater that I got the killed 3 or more elites badges on.  

<img width="1512" height="858" alt="Screenshot 2026-05-23 at 12 19 51 PM" src="https://github.com/user-attachments/assets/2ed6453b-cec7-4407-949a-1fe2db88d40c" />

clicking those runs takes you to the run history page for that run
<img width="1504" height="854" alt="Screenshot 2026-05-23 at 12 27 13 PM" src="https://github.com/user-attachments/assets/d30334ff-59b6-49de-86bc-09a59fa252a8" />

If you have suggestions or find bugs submit them as issues and I can fix them.

The rest of this readme was made by claude so its probably right but who knows:

## What it does

- Adds a **Run Table** entry under Compendium.
- Table of every saved run with sortable columns: character, date,
  ascension, mode, floor reached, time, win/loss.
- Sidebar filters: character, ascension level, win / loss / abandoned,
  single-player vs co-op, custom run vs standard.
- Text search by card / relic name.
- Click any row to open that run's normal Run History page; Back
  returns to the table.
- Includes a **Badges** sub-view — a grid of every end-of-run badge
  across your runs, with the same filters.
- Pure UI mod — doesn't touch gameplay. Safe to enable mid-run.

## Known limits

- Reads from existing `run_history` save files; doesn't change them.
  Uninstalling leaves no trace.
- STS2 disables achievements while any mod is loaded — uninstall if
  you're chasing those.

## Install

### Steam Workshop

Subscribe via the game's Workshop page. Launch the game and enable the
mod from the in-game Mods screen.

### Manual

1. Download the zip from the [Releases page](../../releases).
2. Extract so the folder structure is
   `<game>/mods/RunTable/{RunTable.dll, mod_manifest.json}`.
   - Mac: `<game>/SlayTheSpire2.app/Contents/MacOS/mods/RunTable/`
   - Windows/Linux: `<game>/mods/RunTable/`
3. Launch the game and enable Run Table on the in-game Mods screen.

## Build from source

Requires .NET 9 SDK and a local copy of Slay the Spire 2.

```
./build.sh
```

The build script compiles `RunTable.dll` and copies it + the manifest
into your game's `mods/` folder.

## Companion mods

- [Retry](https://github.com/sts2mods/Retry) — replay any past run
  from any floor. Pairs naturally: find a run in the table, click in,
  click View Acts, replay any floor.
- [Enemy Cycle](https://github.com/sts2mods/EnemyCycle) — see enemy
  move cycles.
- [Timeline](https://github.com/sts2mods/Timeline) — in-combat
  timeline of every event.

## License

MIT.
