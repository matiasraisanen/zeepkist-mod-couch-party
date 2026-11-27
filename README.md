# Couch Party: a Zeepkist mod for local multiplayer

Zeepkist's local split-screen calls everyone "Player 1, Player 2…", forgets everything when you quit, and keeps no records. Couch Party adds player profiles, leaderboards per map, lifetime stats and a few things for bragging rights.

## What it looks like

Mockups at 1080p with four players. Click an image for the full size.

<table>
  <tr>
    <td colspan="2" align="center">
      <a href="docs/images/results.png"><img src="docs/images/results.png" width="640" alt="Results screen: this race, the map's records, this map tonight, this race's stats and moments, tonight's standings"></a><br>
      The results screen
    </td>
  </tr>
  <tr>
    <td align="center">
      <a href="docs/images/stats.png"><img src="docs/images/stats.png" width="400" alt="F8 window, Stats tab: one player's stats in groups, with All time and Tonight buttons"></a><br>
      F8 &gt; Stats
    </td>
    <td align="center">
      <a href="docs/images/hall-of-fame.png"><img src="docs/images/hall-of-fame.png" width="400" alt="F8 window, Hall of Fame tab: gold, silver and bronze in every category"></a><br>
      F8 &gt; Hall of Fame
    </td>
  </tr>
</table>

## Features

- **🪪 Player profiles:** add friends by name or from your Steam friends list, and pick who is Player 1–4. Names show in the game, and outfits are remembered.
- **🏆 Leaderboards per map:** every race is saved. The results screen shows **NEW PB**, the gap to the record, and a banner when the record falls. **NEW MAP RECORD!** pops up the moment someone crosses the line.
- **📊 Stats and Hall of Fame:** lifetime stats per player (records, crashes, car-to-car hits, knockouts, jumps, flips, spins and more), and gold, silver and bronze in every category.
- **🌙 Tonight:** the evening's standings, awards and every map played tonight.
- **🔄 Sync and backups:** automatic backups, and optional sync between PCs through your own small server.

## Install

Requires **ZeepSDK** (Modkist installs it together with BepInEx).

1. Copy `CouchParty.dll` into `<Zeepkist folder>\BepInEx\plugins\`.
   In Steam: right-click Zeepkist > Manage > Browse local files.
2. Start the game.

## How to use

| Key | What it does |
|---|---|
| **F8** | Open/close the Couch Party window |
| **F9** | Stats tab: switch between **all time** and **tonight**. Results screen: switch between **best per player** and **all runs** |

1. **Profiles tab:** add your friends. Type a name and press Enter, or pick a Steam friend.
2. **Player-select screen:** let everyone join as usual.
3. **Players tab:** choose a profile for Player 1, 2, 3 and 4. The game now shows their names. Your picks are remembered.
4. Race. Outfits are saved when the race starts and picked again next time.

<details>
<summary><b>The tabs in the F8 window</b></summary>

- **Players:** who is Player 1–4.
- **Profiles:** add, rename (**Rename**, type, **Enter**; Esc cancels) and manage profiles. Click a profile to see its stats.
  - **Archive** hides a profile from Players, Stats and the Hall of Fame, but keeps its stats and times. **Restore** brings it back, and so does adding the same player again.
  - **Reset** wipes the player's stats and leaderboard times on every synced PC. The profile stays.
  - **Delete** removes the profile, its stats and its times for good, on every synced PC.
- **Leaderboard:** every map, as best per player or all runs. **x** removes a time.
- **Stats:** one player's stats on one screen. Click the name (▼) to pick someone else. The headline is how many map records they hold. **Tonight** (or F9) shows only this evening, with "Fastest on" maps instead of records.
- **Hall of Fame:** the top 3 in each category, like Crash king, Speed demon, Demolition derby, Wheel snatcher, Moon landing, Acrobat, Spin doctor, Moonwalker and Cat lover.
- **Tonight:** the evening's standings, **Tonight's awards** and every map played tonight with who's fastest on it. **Start a new evening** resets it.
- **Backups** and **Sync:** see below.

</details>

<details>
<summary><b>The results screen</b></summary>

- **Left, this map:** the race results with **MAP RECORD!**, **NEW PB -0.512** or **+1.234 to record**; the map's records (F9 or click the header to switch between best per player and all runs); and **This map tonight**: each player's best time tonight, the gap to the fastest, runs and crashes.
- **Right, this race and the evening:** each player's top speed, longest jump, hits, knockouts, wheels lost and cats (best in gold), up to 5 **moments** ("0:12 Alice knocked out Bob"), and **Tonight**: the evening's standings. **Fastest on** counts on how many of tonight's maps you have the fastest time.

A new evening starts after 6 hours without racing (`New evening after hours` in the config). Restarting the game doesn't reset it. Tonight is this PC only.

</details>

## How the stats are counted

Only **local multiplayer** races (2+ players, not online) count, and only for players **with a profile**. All timings use game time, so pausing doesn't count.

<details>
<summary><b>Race results</b></summary>

- **Finish:** crossing the line with every checkpoint. Only these times go on the leaderboard (each player's 10 fastest per map).
- **Shortcut finish:** crossing the line with a checkpoint missed. Counted in your stats, but the time isn't saved.
- **DNF:** not crossing the line at all.
- **Win:** the fastest valid finish in a race with 2 or more players.
- **On foot finish:** you ejected (pressed Y), and your character tumbled over the line without the car. It's a valid finish.
- **3, 2, 1 and no-wheel finishes:** driving over the line with that many wheels on the car. Not counted for on foot finishes.
- **Reverse finish:** the car crosses the line moving at least 120° off the way it faces. Not counted for on foot finishes.

</details>

<details>
<summary><b>Crashes and wheels</b></summary>

- **Crash:** your race ends before the finish line. That's a **hard crash** (you hit something or landed upside down), an **eject** (you pressed Y) or a **timeout** (too slow for about 10 seconds, so the game ejected you). All three count as a crash everywhere.
- **Wheels lost:** each wheel that breaks off while you're still racing. Wheels that fly off when you crash or eject don't count.

</details>

<details>
<summary><b>Car-to-car hits and knockouts</b></summary>

- **Car-to-car hit:** your car hits another racer's car or character at about 11 km/h or more. Both players get one. The same two players score at most one hit per second.
- **Rammed others / got rammed:** in every hit, whoever was moving towards the other car faster is the **rammer**. The other one got rammed.
- **Knockout:** the rammed player is "marked" for a while. If they crash while marked, the rammer gets a knockout. Light contact counts too: rubbing in a turn until one of you crashes is a knockout.
  - The mark lasts **3 seconds** if they stay on their wheels. If they're knocked into the air, it lasts until they've been back on the track for a second. Never longer than **15 seconds**.
  - Landing on the map's flat ground below the track isn't "back on the track".
  - Crossing the finish line clears the mark. Ejecting and then tumbling over the line on foot is not a knockout.
  - Only the rammer gets knockouts. If you ram someone and crash yourself, it's just your own crash.
- **Wheels knocked off:** every wheel the rammed player loses while still marked counts for the rammer.

</details>

<details>
<summary><b>Cats</b></summary>

- **Cat petted:** your car hits a walking cat at about 4 km/h or more. Each cat counts once per 2 seconds.
- **Crash caused by a cat:** you crash soon after hitting a cat, with the same timing as knockouts.

</details>

<details>
<summary><b>Driving</b></summary>

- **Distance:** how far your car moves while racing.
- **Top speed:** your highest speed in km/h with at least two wheels on the track, not in the first second after landing. Falling, or driving on the flat ground below the track, doesn't count.
- **Air time:** time with no wheel and no part of the car touching anything. Hops under 0.3 seconds don't count. Every jump counts, even one that ends in a crash.
- **Longest jump:** your longest jump that you landed like a stunt (see below).

</details>

<details>
<summary><b>Stunts</b></summary>

During each jump, the mod adds up how far the car turns:
- **Front flip / backflip:** nose over tail. Every full turn counts, so turning twice is a double backflip.
- **Barrel roll:** rolling sideways.
- **Spin:** turning flat, counted in half turns: 180, 360, 540, 720, 900, 1080 and up.

A flip or roll counts from 300°, and a spin from 45° short of the half turn (135° is a 180). Only turning in the air counts: riding round a loop or a tube isn't a trick.

**A stunt only counts if you land it:** on the real track (not the flat ground below), with no crash within **3 seconds**. Crossing the finish line mid-air counts, but only for a valid finish. The same goes for big jumps and the longest jump.

- **Big jumps:** 2 seconds or more in the air.
- **Flips and barrel rolls:** every full turn adds one. **Most flips / rolls in one jump** keep your best jump.
- **Spins:** counted as 180s, 360s, 720s and 1080s (a 540 counts as a 360, a 900 as a 720). **Biggest spin** is your best in exact degrees.

The mod finds out which way each car faces at the start of every race, once it rolls straight at 20 km/h or more. Until then, stunts and reverse finishes don't count.

</details>

## Sync between PCs

Share profiles, stats and leaderboards between PCs, for example at a friend's place. Every PC keeps a full copy and works offline. Sync is optional: without it, everything stays on one PC.

**You need your own server for this.** The sync server is a small program that runs all the time on a computer that the PCs can reach over the internet, such as a home server or a rented one. Setting that up safely means knowing how to run a service on the internet: a domain name, HTTPS, a firewall, and keeping it updated. This guide doesn't cover those. If that sounds unfamiliar, ask someone who has done it before, or skip sync. [server/README.md](server/README.md) explains how to start the server itself (with Docker).

1. Create a key for each PC on the server (the name is a label for that PC):

   ```bash
   docker compose exec couchparty python /app/server.py addkey friends-pc
   ```

2. In the game on that PC, open **F8 → Sync**, enter the server address and the key, and press **Save & sync**.

**The key works like a password.** Anyone who has it can read and change all your synced data. Don't share it or post it anywhere. Give each PC its own key, so you can remove one PC's key without changing the others ([Managing keys](server/README.md#managing-keys)).

It syncs when the game starts, a few seconds after each race or profile change, and with **Sync now**. **Forget on this PC** stops syncing. **Keep every PC on the same mod version:** a PC with an older version only downloads, and says "update the mod on this PC".

<details>
<summary><b>How data is merged</b></summary>

- Runs from every PC are combined. Times you removed and maps you cleared stay removed.
- Stats are counted per PC and added together.
- Renames and archiving: the newest change wins.
- Reset and Delete win over older data: a PC that was offline can't bring it back.
- The same Steam friend added on two PCs is joined into one profile. Profiles made by typing a name are never joined.
- Each PC is named after its key, so a profile can say "Added on living-room".
- Player 1–4 picks and outfits stay per PC, since cosmetic unlocks differ between Steam accounts.
- The server address and key are stored only in `BepInEx\config\com.skofdo.couchparty.cfg` on each PC.

</details>

## Backups

A backup is made when you start the game, if something changed. The newest 30 are kept (by count, not age), and backups made before a mod update are kept forever. **F8 → Backups** lists them, with **Restore**, **Backup now** and **Open backup folder**. A restore backs up your current data first.

<details>
<summary><b>More about backups</b></summary>

- **Extra copy:** set `Extra backup folder` in the config (e.g. a OneDrive folder) to keep a second copy.
- **Unreadable files** are set aside as `*.unreadable-<date>.json` instead of being overwritten, and the game warns you.
- **Start fresh** (F8 → Backups → Start fresh…) wipes stats and leaderboards, or everything including profiles, on every synced PC. A permanent backup is made first, and you confirm by typing `WIPE`. Old versions on the server (`data/history/`) stay until you delete them there.

</details>

## Good to know

- While the mouse is over the Couch Party window, the game ignores clicks and key presses, for everyone.
- Outfits can only use cosmetics the Steam account running the game has unlocked.
- Saved data: `%AppData%\Zeepkist\Mods\com.skofdo.couchparty\`. Config and key bindings: `BepInEx\config\com.skofdo.couchparty.cfg`.
- If times end up next to the wrong players, set `Player number base` in the config to `0` or `1` (default `-1` = automatic).

## Building from source

<details>
<summary><b>Build and test</b></summary>

Needs the .NET SDK (6 or newer).

```bash
dotnet build -c Release
```

The build looks for BepInEx in `C:\Program Files (x86)\Steam\steamapps\common\Zeepkist\BepInEx\core`. If your game is elsewhere, pass the path:

```bash
dotnet build -c Release -p:BepInExCoreDir="D:\SteamLibrary\steamapps\common\Zeepkist\BepInEx\core"
```

The output is `bin\Release\CouchParty.dll`. Tests: `dotnet run --project tests/CouchParty.Tests -c Release` (see [tests/README.md](tests/README.md)).

</details>
