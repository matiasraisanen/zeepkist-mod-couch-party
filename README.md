# Couch Party: a Zeepkist mod for local multiplayer

Zeepkist's local split-screen calls everyone "Player 1, Player 2…", forgets everything when you quit, and keeps no records. Couch Party fixes that, and adds a few things for bragging rights.

## Features

**🪪 Player profiles**
- Add friends by typing a name or picking them from your Steam friends list. Profiles use the name they chose, not your nickname for them.
- Assign who is Player 1–4. Their names show on the player-select screen and in the race.
- Outfits (soapbox, hat, colour) are remembered and picked for each player automatically next time.

**🏆 Local leaderboards**
- Every local multiplayer race is saved per map. A time only counts if you pass every checkpoint, so shortcuts don't count.
- A results panel styled like the game's own screen shows **NEW PB -0.512**, **+1.234 to record**, and a banner when the record falls ("Carol beat Alice's 0:13.000 by 0.754 s").
- Two views: best time per player, or all runs, so you can see who owns the whole top 10.
- The map's top 3 pops up when a race starts, and **NEW MAP RECORD!** appears the moment someone crosses the line with a record time.

**📊 Stats and Hall of Fame**
- Lifetime stats per player:
  - **Racing:** races, wins, DNFs, shortcut tries
  - **Records:** records held and broken, personal bests
  - **Carnage:** crashes, wheels lost, finishes on 3, 2, 1 or zero wheels, and on foot finishes (ejected, then crossed the line without the car)
  - **Bumper cars:** bumps, rams, got rammed, knockouts (pushed someone off or into a crash) and wheels knocked off
  - **Driving:** distance driven, top speed, air time and longest jump
- A Hall of Fame with gold, silver and bronze in every category: Crash king, Speed demon, Demolition derby, Wheel snatcher, Moon landing, No-wheel hero, Stuntman and more.

**🛠️ Under the hood**
- A C# BepInEx plugin built on ZeepSDK, with Harmony patches into the game.
- Everything is stored locally, with nothing online.
- Press **F8** for the menu, which is styled to match the game.

## Install

Requires **ZeepSDK** (Modkist installs it together with BepInEx).

1. Copy `CouchParty.dll` into `<Zeepkist folder>\BepInEx\plugins\`.
   The Zeepkist folder is usually `C:\Program Files (x86)\Steam\steamapps\common\Zeepkist`.
   (In Steam: right-click Zeepkist > Manage > Browse local files.)
2. Start the game.

## How to use

| Key | What it does |
|---|---|
| **F8** | Open/close the Couch Party window |
| **F9** | Show the current map's top times for a few seconds (during a race) |

1. **Profiles tab:** add your friends by typing a name (Enter adds it) or choosing from Steam friends. Steam profiles use the name your friend chose, not the nickname you gave them. To rename, press **Rename**, type, then press **Enter** (Esc cancels).
2. Go to the player-select screen and let everyone join as usual.
3. **Players tab:** choose a profile for Player 1, 2, 3 and 4. The game now shows those names instead of "Player N". Your picks are remembered for next time.
4. **Outfits are automatic.** Whatever a player picks on the player-select screen is saved to their profile when the race starts. Next time that player joins, their saved outfit is selected for them; they can still change it, and the new choice is saved instead.
5. Race. When the results screen opens, the mod's panel covers the game's "Race Results" column with this race's times and the map's top 10. Each line is tagged: **MAP RECORD!** (plus a banner saying whose record was beaten and by how much), **NEW PB -0.512** (how much you improved), or **+1.234 to record** (the gap to the record). The map's top 10 shows **best per player** by default. Press **F9** (or click the header) to switch to **all runs**, where one player can appear several times.

6. **Leaderboard tab:** every map, with the same "Best per player" / "All runs" switch. **x** removes one time (or all of a player's times in the per-player view).
7. **Stats tab:** pick a player to see their stats in groups. The headline is how many maps they hold the #1 time on ("Holds 5 of 8 map records").
8. **Hall of Fame tab:** the top 3 (gold, silver, bronze) in each category: records held (Record hoarder), wins, records broken, PBs, top speed, distance, air time (Frequent flyer), longest jump (Moon landing), hard crashes (Crash king), ejects (Ejector seat), timeouts (Snail), wheels lost, finishes missing wheels, no-wheel finishes, on foot finishes (Stuntman), shortcut tries, rams (Bumper car champ), knockouts (Demolition derby), wheels knocked off (Wheel snatcher), got rammed (Punching bag), cats hugged (Cat lover), crashes caused by cats (Cat's revenge).

Stats tracked: lifetime stats per profile (races, wins, finishes, DNFs, shortcut tries, personal bests, map records broken and held, crashes (including ejects and timeouts), wheels lost, finishes on 3 / 2 / 1 / 0 wheels, on foot finishes (ejected, then crossed the line without the car), bumps with other racers, rams, got rammed, knockouts (someone crashed or didn't finish soon after bumping into you), wheels knocked off (another racer lost a wheel right after bumping into you), cats hugged (walking cats hit by your car), crashes caused by cats, distance driven, top speed, air time (total, longest jump, big jumps of 1 s+), total time on the clock).

Leaderboard entries are linked to the profile, not the name, so renaming a profile renames all their old records too.

## How the stats are counted

All stats come from local multiplayer races and only for players with a profile.

**Race results**
- **Finish:** crossing the line with every checkpoint. Only these times go on the leaderboard.
- **Shortcut try:** crossing the line with a checkpoint missed. Shown on the results screen, not saved.
- **DNF:** not crossing the line at all.
- **Win:** the fastest valid finish in a race with 2 or more players.
- **On foot finish:** a valid finish where you had already ejected (pressed Y) and your character crossed the line without the car.
- **3, 2, 1 and no-wheel finishes:** a valid finish with that many wheels still on the car.

**Crashes and wheels**
- **Crash:** your race ends before the finish line. That's a hard crash (you hit something or landed badly), an eject (you pressed Y), or a timeout (you were too slow for 10 seconds and the game ejected you). All three count as a crash, for knockouts and for "crashes caused by cats" too. The Stats tab also shows how many were hard crashes, ejects and timeouts. Ejects are recognised from how the game ends your race; if one is ever labelled wrong, it still counts as a crash.
- **Wheels lost:** each wheel that breaks off before you finish.

**Bumps**
- **Bump:** while both of you are still racing, your car (body or wheels) hits another racer's car or character at an impact of at least about 11 km/h. The same two players can only score one bump per second, so a long scrape counts once.
- **Ram / got rammed:** whoever was moving towards the other car faster just before the hit gets the ram; the other gets "got rammed". In a rear-end hit, the car behind is the rammer.

**Knockouts**

After a bump, both players are "marked" for a short while, each with the other as the possible culprit (a new bump replaces the culprit). If a marked player **crashes** (including ejects and timeouts), or **doesn't finish the race** at all, the other player gets a **knockout**.

How long the mark lasts:
- **Staying on the wheels** (a pit manoeuvre, a shove): **4 seconds**. A crash within 4 seconds of the hit counts.
- **Knocked into the air:** until they've been back on the ground for a full second. A crash at the end of a long jump still counts.
- **Never longer than 15 seconds.** Someone pushed off the map who never comes back is out of the race anyway, so they don't finish and that counts too.
- **Crossing the finish line** clears the mark.

It doesn't matter who rammed whom: if you ram someone and *you* end up crashing, *they* get the knockout.

- **Wheels knocked off:** a wheel that breaks off within 1 second of a bump counts for the other player.

**Cats**
- **Cat hugged:** while you're still racing, your car hits one of the walking cats at about 4 km/h or more. Each cat counts once per 2 seconds.
- **Crash caused by a cat:** you crash (including ejects and timeouts) soon after hitting a cat, using the same timing as knockouts: within 4 seconds if you stay on your wheels, longer if the cat sent you flying (up to 15 seconds).

**Driving**
- **Distance:** how far your car moves while racing (not after crashing or finishing).
- **Top speed:** your car's highest speed in km/h.
- **Air time / longest jump:** time with no wheel and no part of the car touching anything. Hops under 0.3 seconds don't count. **Big jumps** are 1 second or more.

## Sync between PCs

Use the same profiles, stats and leaderboards on several PCs, for example at a friend's place, through your own small server. See [server/README.md](server/README.md) for setting it up (Docker plus a reverse proxy with HTTPS).

- **Setup:** in the game, open **F8 → Sync**, enter your server address and this PC's key, and press **Save & sync**. Each PC gets its own key from the server.
- **Offline:** every PC keeps a full copy and works offline.
- **When it syncs:** when the game starts, a few seconds after each race or profile change, and with **Sync now**.
- **Merging, so nothing is lost:**
  - All runs from every PC are combined. Times you removed and maps you cleared stay removed.
  - Stats are counted per PC and added together.
  - Renames and deletions: the newest change wins.
- **What stays per PC:** Player 1–4 picks and outfits, since cosmetic unlocks differ between Steam accounts.
- **Keep every PC on the same mod version.** An older version doesn't know newer stats and would drop them when uploading. So if a PC finds data from a newer version on the server, it only downloads (you still see the latest profiles and times) and shows "update the mod on this PC" until it's updated.
- **Leaving a friend's PC:** press **Forget on this PC** to stop it syncing. You can also remove that PC's key on the server.
- **Where settings live:** the server address and key are stored in `BepInEx\config\com.skofdo.couchparty.cfg` on each PC, never in the mod or this repository.

## Backups

Your profiles, stats and leaderboards are backed up automatically. Open **F8 → Backups** to see them.

- **When:** a backup is made when you start the game, but only if something changed since the last one.
- **How many:** the newest 30 are kept. The limit is a count, not an age, so months without playing never delete anything. Backups made before a mod update are kept forever.
- **Restore:** press **Restore** next to a backup. Your current data is backed up first, so a restore can be undone.
- **Backup now** and **Open backup folder** are on the same page.
- **Unreadable files:** if a data file can't be read, the mod sets it aside as `*.unreadable-<date>.json` instead of overwriting it, and warns you in the game.
- **Extra copy:** set `Extra backup folder` in the config (e.g. a OneDrive folder) to keep a second copy of every backup.

**Start fresh** (F8 → Backups → Start fresh…) wipes stats and leaderboards, or everything including profiles, on every synced PC. A PC that was offline at the time drops its old data the next time it syncs, instead of uploading it again. A backup that's kept forever is made first. You confirm by typing `WIPE`. Old versions in the server's `data/history/` folder stay until you delete them there.

**Deleting a profile** moves it to an archive. If you add the same player again later (the same Steam friend, or the same name for typed profiles), their records and stats come back.

Notes:
- Only **local multiplayer** races (2+ players, not online) are recorded.
- Only players **with a profile** get their times saved.
- A time only counts if the player passed **every checkpoint** (shortcuts show as "missed checkpoint" and aren't saved).
- Races are always recorded. F9 only shows the current map's top times.
- When a race starts, the map's top 3 shows for a few seconds. When someone beats the map record, a "NEW MAP RECORD!" popup appears.
- While the mouse is over the Couch Party window (or you're typing in it), the game ignores clicks and key presses, for everyone, including controllers.
- Remove a single time with the **x** next to it in the Leaderboard tab.
- Outfits can only use cosmetics that the Steam account running the game has unlocked.
- Saved data lives in `%AppData%\Zeepkist\Mods\com.skofdo.couchparty\` (`profiles.json`, `leaderboard.json`).
- Key bindings and options are in `BepInEx\config\com.skofdo.couchparty.cfg`.

## Testing checklist

Send `BepInEx\LogOutput.log` afterwards. The mod writes `[debug]` lines into it.

- [ ] The log says `Couch Party 0.9.5 loaded`.
- [ ] The top-3 popup appears for a few seconds when a race starts, then fades. It doesn't appear on the player-select screen.
- [ ] F9 during a race shows it again for a few seconds.
- [ ] Beating the map record shows "NEW MAP RECORD!" right after crossing the finish (or at the latest on the results screen).
- [ ] With the F8 window open, clicking inside it no longer clicks game buttons behind it.
- [ ] The log has `Scene: '...'` lines. Send them if popups never appear.
- [ ] Crossing the finish logs `Player N crossed the finish: X wheel(s) on` (plus `ON FOOT` after ejecting). Dying without finishing logs nothing.
- [ ] After a race, the Stats tab shows races, crashes, distance and top speed. The log has `Stats: PlayersReady[...] -> Player N` lines; check they match who was who.
- [ ] Big jumps log `Player N: 1.4 s of air time!`; the Stats tab shows air time and longest jump.
- [ ] Knockouts log `Knockout! Player X took out Player Y (...)`, and wheels knocked off log `Wheel knocked off! ...`.
- [ ] F8 → Backups lists a backup after starting the game; Restore works and the "before restore" backup appears.
- [ ] Deleting and re-adding a player shows "Welcome back ... Restored their records and stats".
- [ ] F8 → Sync: after Save & sync it says "Uploaded (first sync)" or "Synced"; a second PC sees the same profiles and times.
- [ ] Top speed looks about right compared to the in-game speedometer.

If times end up next to the wrong players, set `Player number base` in the config file to `0` or `1` (default `-1` = automatic) and tell me which one worked.

## Building from source

Needs the .NET SDK (6 or newer).

```bash
dotnet build -c Release
```

By default the build looks for BepInEx in `C:\Program Files (x86)\Steam\steamapps\common\Zeepkist\BepInEx\core`. If your game is elsewhere, pass the path:

```bash
dotnet build -c Release -p:BepInExCoreDir="D:\SteamLibrary\steamapps\common\Zeepkist\BepInEx\core"
```

The output is `bin\Release\CouchParty.dll`.
