# Tests

`CouchParty.Tests` checks the parts of the mod that handle shared data, where a bug could quietly lose or mix up stats and times on every synced PC:

- **Sync merging:**
  - runs from two PCs are combined without duplicates, and stats from different PCs add up
  - renames and archived profiles: the newest change wins
  - removed times and cleared maps stay removed
- **Old save files:** data from earlier versions is converted correctly when loading.
- **Start fresh:** a wipe reaches PCs that were offline, their old data is dropped, and runs recorded after the wipe are kept.
- **Version guard:** version numbers compare correctly, and the newest version stamp is kept.

The tests load the compiled mod and run outside the game in a few seconds. Gameplay (crashes, bumps, cats, outfits, UI) needs the game, so it's checked with the in-game debug log instead.

## Running

Needs the .NET 8 SDK (or newer).

```bash
dotnet build -c Release
```
```bash
dotnet run --project tests/CouchParty.Tests -c Release
```

Each check prints `PASS` or `FAIL`, ending with `ALL PASSED` or the number of failures (also the exit code).
