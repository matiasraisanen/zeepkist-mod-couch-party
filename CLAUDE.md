# Couch Party: notes for Claude

Couch Party is a BepInEx mod for **Zeepkist**, for **local split-screen multiplayer** (2 to 4 players
on one PC). It adds player profiles (Steam friends or custom names, outfits remembered), local
leaderboards per map, lifetime stats with a Hall of Fame, a "Tonight" view of the evening's session,
backups, and optional sync between PCs through your own small server (`server/`).

## Conventions

- **Keep the README in step with the code.** Every stat is explained there for players who aren't
  developers (and aren't all native English speakers): plain, short sentences. When stat logic or
  timing changes, update the README in the same change.
- **Nothing personal in the repo:** no sync server addresses, keys, hostnames or anything that
  identifies someone's machine. The server address and key are set in the game (F8 > Sync) and live
  only in each PC's BepInEx config. The repo is public. In examples, comments and tests, use
  made-up player names (Alice, Bob, Carol), never real players' names or gamer tags.
- **Names:** "car-to-car hits" (not bumps or collisions); a hit has a **rammer**. An empty value is an
  en dash (`\u2013`, `NoValue` in `CouchUI`), except on the Tonight tab, where zeros stay.
- **UI:** IMGUI scaled to a 1080p reference (`Screen.height / 1080`). It must work at 16:9 and on
  ultrawide. The F8 window is 1150 x 900 at reference size; the Stats tab should fit without
  scrolling at 16:9.
- **Personal setup:** contributors can keep their own notes (machines, deploy steps, workflow) in
  `CLAUDE.local.md`, which is gitignored.

## Build, test, package

```bash
dotnet build CouchParty.csproj -c Release
```

It needs `BepInEx.dll` and `0Harmony.dll` from `BepInEx\core`. `BepInExCoreDir` defaults to the
Windows Steam install (`...\Zeepkist\BepInEx\core`); override it with `-p:BepInExCoreDir=...` if the
game is elsewhere. It also builds on Linux with the .NET SDK: point `BepInExCoreDir` at a folder with
those two DLLs (copied from the game, or from the BepInEx 5.4.23 release). Everything else comes
from NuGet.

```bash
dotnet run --project tests/CouchParty.Tests -c Release
```

The tests (34 checks) cover sync merging, old save files, Start fresh and the version guard. They must
print `ALL PASSED`.

```bash
python tools/memberrefs.py bin/Release/CouchParty.dll
```

Lists every field and method the DLL references directly in the game's `Zeepkist` assembly (needs
`pip install dnfile`). Only public members may appear: the game libs we compile against are
**stripped and publicized**, so code compiles against private members that then throw
`FieldAccessException` at runtime. Private members must go through `Access.Get/Set` (reflection).
Currently the only line is `method PlayerManager.get_Instance`.

**Packaging a release:** `dist/CouchParty-<version>.zip` contains `CouchParty.dll` and `README.md`.

**Version bump** (three places): `Plugin.Version` in `src/Plugin.cs`, `<Version>` in
`CouchParty.csproj`, and the "Couch Party X loaded" line in the README testing checklist.
Data from a newer mod version makes older versions download-only (see Sync), so every syncing PC
should run the same version.

## Code map

| File | What it does |
|---|---|
| `src/Plugin.cs` | Entry point, config entries, Harmony patching (each patch class on its own, so one failure doesn't stop the rest), race events from ZeepSDK `RacingApi` |
| `src/Patches.cs` | Harmony patches into the game (player select, names, spawns, deaths, results screen) |
| `src/Stats.cs` | `StatsTracker`: live per-player tracking every frame (crashes, wheels, hits, knockouts, cats, air time, stunts, finishes), race moments, committing stats after a race. All timing constants are at the top |
| `src/Data.cs` | Saved data: `Profile`, `PlayerStats` (per PC, summed over PCs), leaderboards, `Store` |
| `src/Game.cs` | Reading the game: players, names, results, `RaceSummary`, time formatting |
| `src/CouchUI.cs` | All IMGUI: F8 window tabs (Players, Profiles, Leaderboard, Stats, Hall of Fame, Tonight, Backups, Sync), pre-race popup, new record popup, results screen panels (layout "B") |
| `src/LiveRecords.cs` | "NEW MAP RECORD!" the moment someone crosses the line |
| `src/Session.cs` | Tonight: maps and runs this evening, "fastest on" counts, and every stat per player for the Stats tab's Tonight view (filled in `StatsTracker` next to the lifetime stats); resets after 6 h without racing; this PC only |
| `src/Sync.cs` | Sync with the server: download, merge, upload with If-Match, version guard |
| `src/Backups.cs` | Backups by count (30), never by age; a permanent one before each mod update |
| `src/Outfits.cs` | Remembering and applying outfits per profile |
| `src/SteamNames.cs` | Steam friends list for adding profiles |
| `src/Access.cs` | Reflection helpers for private game members |
| `src/Toolbar.cs` | ZeepSDK toolbar menu |
| `server/server.py` | Sync server, Python standard library only, meant to run in Docker behind a reverse proxy with HTTPS. Stores one JSON document, checks per-PC keys, optimistic locking with a revision number (ETag / If-Match), keeps 100 old versions. Knows nothing about the stat fields |
| `tests/CouchParty.Tests` | Test program for merging and save files |
| `tools/memberrefs.py` | The member scan above |
| `tools/deploy.sh` | Build on Linux, copy the DLL to the game PC over SSH, fetch `LogOutput.log`. Host and paths come from environment variables, never the repo |

Saved data: `%AppData%\Zeepkist\Mods\com.skofdo.couchparty\`. Config:
`BepInEx\config\com.skofdo.couchparty.cfg`. Log: `BepInEx\LogOutput.log` (debug lines carry a
timestamp and the race time).

## How Zeepkist works (this drives the stat rules)

- **Soapboxes have no motor.** Gravity is the engine. Every car is always rolling downhill, there's
  no way to speed up, and braking means you can't catch up again. So "rear-end" hits aren't really
  different from other rams (that stat was dropped).
- **One crash and you're out.** A player's race ends when they crash or cross the finish line.
  **There are no respawns in local multiplayer.** Players crash on their own all the time (take too
  much risk and you don't finish), so not finishing is never someone else's fault by default.
- **Three ways out, all count as a crash:**
  - **Hard crash:** the character touches something. The soapbox has no roof, so landing upside
    down is a crash.
  - **Eject:** the player presses **Y** and jumps out of the car.
  - **Timeout:** the game auto-ejects a player who stays below a certain speed for about 10 seconds.
- **On foot finish:** ejecting throws the driver through the air as a rag doll that nobody can steer.
  If it happens to tumble over the finish line, that's a legit, valid run.
- **Checkpoints:** a time only counts if every checkpoint was passed; otherwise it's a shortcut finish.
- **Every map has a flat "ground" object** below the track (`GameMaster.groundFloor`). Falling onto it
  usually means you're out of the race in practice (you have to eject), but not always: on some maps
  you can drive back to the track, or eject and tumble over the finish line. So the ground floor never ends a
  race or decides a DNF by itself. Falls can be long, up to about 15 s, and can land cleanly. Speed,
  stunts and "back on track" must ignore the ground floor.
- **Maps are mostly 30 to 60 seconds long.**
- **The physics feel "airy":** jumps last long, so time-based thresholds (big jumps) may need to be
  generous.
- **Close racing:** players often rub against each other while fighting for a turn, and that often
  makes one of them crash. Light contact must count.
- **Walking cats** roam some maps and can be hit.
- **Player select:** players show as "Player N" with no way to rename them; that's why profiles
  exist. Cosmetics depend on the unlocks of the Steam account running the game.
- **Who it's built for:** a group on one couch that grinds one map many times, then moves on (a couple
  of dozen maps a night). What matters is the best time per map during the grind, plus some dirty
  play, not wins or race points. That's why the headline stat is map records held, and Tonight
  counts "fastest on" maps.
- Sideways finishes are almost impossible; front flips, backflips and spins do happen.

## Game internals we rely on

- **Libraries:** BepInEx 5.4.23 (net472), ZeepSDK 2.7.6 (`[BepInDependency("ZeepSDK")]`), Zeepkist.GameLibs
  18.10.2087 (stripped and publicized). Unity 2021.3.
- **PlayerManager:** `playersNames`, `playersActive`, `playerSoapbox/Hat/Color`, `currentMaster`,
  `objectsList` (wardrobe). Outfit item IDs aren't unique; use the complete ID.
- **GameMaster:** `playerResults` (`WinCompare.Result`: `playerNum`, `time`, `racepoints`),
  `PlayersReady` (`ReadyToReset`: `index`, `actuallyFinished`, `finished`, `cc`, `character`),
  `groundFloor`.
  - `finished` means the round is over for that player (also after dying).
  - `actuallyFinished` means they really crossed the line.
- **Telling crash types apart** (`DamageCharacterScript.KillCharacter`):
  - **Eject:** caller `ReadyToReset.cs`, forced, reason `Gameplay_Crashed`.
  - **Hard crash:** caller `DamageCharacterScript.cs`, not forced.
  - **Timeout:** comes through `ReadyToReset.SetLocalTimeOutResults` first.
- **Contact checks:** `DamageCharacterScript.isdead` (character), `DamageWheel.isdead` (wheel broken
  off), `New_CustomWheel.isGrounded` and `.hit` (wheel raycast; its collider tells track vs ground floor).
- **Car body:** collisions are reported by a `CarCollisionReporter` component added to each car body
  (car-to-car, cats via `AnimalController`, track or ground floor contact).
- **Car orientation:** the car model's forward axis isn't assumed. `StatsTracker.DetectAxes` finds
  it at the start of every race (first moment the car rolls straight at 20 km/h+ on the track).

## Stat rules in short (details in the README)

- **Car-to-car hit:** at least about 11 km/h of impact, once per pair per second. The **rammer** is
  whoever was moving towards the other car faster.
- **Knockout:** only the rammed player is marked, with the rammer as the culprit.
  - **How long the mark lasts:** 3 s if they stay on their wheels; in the air, until they've been back on the
    real track for 1 s; at most 15 s.
  - **What counts:** a crash, eject or timeout while marked is the rammer's knockout, unless they
    still cross the finish on foot (held until the race ends). Not finishing is never a knockout. Crashing after ramming someone is just your own crash.
- **Wheels knocked off:** any wheel the rammed player loses while still marked.
- **Crash caused by a cat:** same timing as knockouts.
- **Top speed:** only on the real track, not in the first second after landing.
- **Air time:** no wheel and no car-body contact; hops under 0.3 s don't count. Big jump: 1 s or more, counted like a stunt (landed on the
  real track, no crash within 3 s). Longest jump too. Air time counts every jump.
- **Stunts:** rotation around the car's own axes during a jump.
  - **Sizes:** flips and rolls count from 300°; spins count in half turns with 45° slack.
  - **Counters:** 180s / 360s / 720s / 1080s; 540 goes with 360, 900 with 720.
  - **Biggest spin:** exact degrees (Spin doctor).
  - **What counts:** only a landing on the real track (not the ground floor), with no crash within
    3 s. Crossing the finish mid-air counts.
- **Reverse finish:** valid finish moving at least 120° off the car's nose (not on foot).
- **Finishes group:** valid finishes with 3/2/1/0-wheel, on foot and reverse indented below,
  then shortcut finishes.

## Sync, in short

- **Merging is done in the mod** (`Sync.Merge`):
  - Runs are a union by id, minus deleted runs and cleared maps.
  - Stats are kept per PC (newest wins per PC) and summed.
  - Profiles: last write wins.
  - Seasons come from Start fresh.
- **Version guard:** every upload records `WrittenBy` (mod version). An older mod only downloads, and
  shows "update the mod on this PC", so it can't drop newer stat fields.
- **Server:** keys are per PC (`python /app/server.py addkey|listkeys|removekey <name>` inside the
  container); see `server/README.md`.

## Tuning with the debug log

With **Advanced > Debug logging** on, `LogOutput.log` gets `[debug]` lines with a timestamp and the
race time: car-to-car hits (speeds, rammer), crashes and lost wheels (with how long after the last
ram), knockouts, mark cleared, every jump (time, distance, height, drop), rotation per jump, stunts
landed (air time, degrees per axis, best single jump of the race), car axes found, and the angle at the finish. The timing constants at the top of
`src/Stats.cs` were tuned from these lines; check them against real play before changing a rule.
