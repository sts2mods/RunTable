# Run Table

The vanilla Run History is a "next / previous" navigator — there's no
index page. I wanted to be able to find one specific run from a month
ago, so this adds a searchable table of every run I've played.

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
