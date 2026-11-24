// Couch Party tests: sync merge rules, old save-file conversion, Start fresh (seasons) and the version guard.
// These are the parts where a bug would quietly lose or mix up shared data on every synced PC, and they're
// hard to test by playing (two PCs, one offline, a wipe in between...). Gameplay (crashes, bumps, cats, UI)
// needs the game and isn't covered here.
//
// Build the mod first (repo root):   dotnet build -c Release
// Then run:                          dotnet run --project tests/CouchParty.Tests -c Release
// Exit code = number of failed checks (0 = all passed).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CouchParty;
using Newtonsoft.Json;

int failures = 0;
void Check(string what, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

// Sync.Merge is internal: call it through reflection.
var syncType = typeof(SyncDoc).Assembly.GetType("CouchParty.Sync");
var merge = syncType.GetMethod("Merge", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
SyncDoc Merge(SyncDoc a, SyncDoc b) => (SyncDoc)merge.Invoke(null, new object[] { a, b });

DateTime t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

// ---- PC A: Alice with stats from machine A, two runs on map M
var a = new SyncDoc();
var sA = new Profile { Id = "p1", Name = "Alice", Modified = t0 };
sA.StatsByMachine["A"] = new PlayerStats { Races = 10, Wins = 4, Crashes = 7, TopSpeedKmh = 150, CatsPetted = 2, MostRollsInJump = 3, Updated = t0.AddHours(1) };
var mA = new Profile { Id = "p2", Name = "Carol", Modified = t0 };
a.Profiles.AddRange(new[] { sA, mA });
var levelA = new LevelRecords { LevelName = "test-map", LastPlayed = t0.AddHours(1).ToLocalTime() };
levelA.RacesByMachine["A"] = 5;
levelA.Runs.Add(new RunRecord { Id = "r1", ProfileId = "p1", Name = "Alice", Time = 30f, Machine = "A" });
levelA.Runs.Add(new RunRecord { Id = "r2", ProfileId = "p2", Name = "Carol", Time = 28f, Machine = "A" });
levelA.RebuildBest();
a.Leaderboard.Levels["M"] = levelA;

// ---- PC B (friend's): Alice renamed later, stats from machine B, one run of its own + r2 deleted there
var b = new SyncDoc();
var sB = new Profile { Id = "p1", Name = "Alice-renamed", Modified = t0.AddHours(2) };
sB.StatsByMachine["B"] = new PlayerStats { Races = 3, Wins = 1, Crashes = 2, TopSpeedKmh = 190, CatsPetted = 1, MostRollsInJump = 2, Updated = t0.AddHours(2) };
sB.StatsByMachine["A"] = new PlayerStats { Races = 8, Wins = 3, Updated = t0 }; // older copy of A's stats
var newcomer = new Profile { Id = "p3", Name = "Bob", Modified = t0 };
b.Profiles.AddRange(new[] { sB, newcomer });
var levelB = new LevelRecords { LevelName = "test-map", LastPlayed = t0.AddHours(3).ToLocalTime() };
levelB.RacesByMachine["B"] = 2;
levelB.RacesByMachine["A"] = 4; // older count from A
levelB.Runs.Add(new RunRecord { Id = "r1", ProfileId = "p1", Name = "Alice", Time = 30f, Machine = "A" });
levelB.Runs.Add(new RunRecord { Id = "r3", ProfileId = "p1", Name = "Alice", Time = 27f, Machine = "B" });
b.Leaderboard.Levels["M"] = levelB;
b.Leaderboard.DeletedRunIds.Add("r2");
// A map cleared on B after it was last played on A
var levelX = new LevelRecords { LevelName = "old", LastPlayed = t0.ToLocalTime() };
levelX.Runs.Add(new RunRecord { Id = "rx", ProfileId = "p1", Time = 50f });
a.Leaderboard.Levels["X"] = levelX;
b.Leaderboard.ClearedLevels["X"] = t0.AddHours(5);

var m = Merge(a, b);
var p1 = m.Profiles.Single(p => p.Id == "p1");
Check("profiles from both sides kept (3)", m.Profiles.Count == 3);
Check("newest rename wins", p1.Name == "Alice-renamed");
Check("per-machine stats: newer A entry kept", p1.StatsByMachine["A"].Races == 10);
Check("per-machine stats: B entry added", p1.StatsByMachine["B"].Races == 3);
var total = PlayerStats.Sum(p1.StatsByMachine.Values);
Check("totals summed (races 13, wins 5, crashes 9, cats 3)", total.Races == 13 && total.Wins == 5 && total.Crashes == 9 && total.CatsPetted == 3);
Check("top speed takes max (190)", Math.Abs(total.TopSpeedKmh - 190) < 0.01);
Check("most rolls in one jump takes max (3)", total.MostRollsInJump == 3);
var lm = m.Leaderboard.Levels["M"];
Check("runs: union minus deleted (r1, r3)", lm.Runs.Select(r => r.Id).OrderBy(x => x).SequenceEqual(new[] { "r1", "r3" }));
Check("deleted run stays deleted", m.Leaderboard.DeletedRunIds.Contains("r2") && !lm.Best.ContainsKey("p2"));
Check("best rebuilt from runs (Alice 27.0)", Math.Abs(lm.Best["p1"].Time - 27f) < 0.001);
Check("races per machine take max (A 5, B 2 => 7)", lm.RacesPlayed == 7);
Check("cleared map stays gone", !m.Leaderboard.Levels.ContainsKey("X"));

var m2 = Merge(b, a);
Check("merge is symmetric (same JSON both ways)", JsonConvert.SerializeObject(m2.Profiles.OrderBy(p => p.Id).Select(p => new { p.Id, p.Name, R = PlayerStats.Sum(p.StatsByMachine.Values).Races })) ==
                                                   JsonConvert.SerializeObject(m.Profiles.OrderBy(p => p.Id).Select(p => new { p.Id, p.Name, R = PlayerStats.Sum(p.StatsByMachine.Values).Races })));
var m3 = Merge(m, m);
Check("merging with itself changes nothing", m3.ToJson() == m.ToJson());

// Stats reset travels: reset on B after A's stats were updated
var c = SyncDoc.Clone(m);
c.Profiles.Single(p => p.Id == "p1").StatsByMachine.Clear();
c.Profiles.Single(p => p.Id == "p1").StatsResetAt = t0.AddHours(10);
var m4 = Merge(m, c);
Check("stats reset wins over older stats", m4.Profiles.Single(p => p.Id == "p1").StatsByMachine.Count == 0);

// Deleted profile (archived) newer than other side's copy
var d = SyncDoc.Clone(m);
var dp = d.Profiles.Single(p => p.Id == "p3"); dp.IsArchived = true; dp.Modified = t0.AddHours(20);
Check("archiving travels", Merge(m, d).Profiles.Single(p => p.Id == "p3").IsArchived);

// ---- Old save format: legacy Stats / RacesPlayed fields are picked up when reading
var oldProfile = JsonConvert.DeserializeObject<Profile>("{\"Id\":\"x\",\"Name\":\"old\",\"Stats\":{\"Races\":12,\"Wins\":5}}");
Check("old profile file: stats read into LoadedLegacyStats", oldProfile.LoadedLegacyStats != null && oldProfile.LoadedLegacyStats.Races == 12);
var reser = JsonConvert.SerializeObject(oldProfile);
Check("legacy 'Stats' field not written back out", !reser.Contains("\"Stats\""));
var oldLevel = JsonConvert.DeserializeObject<LevelRecords>("{\"LevelName\":\"m\",\"RacesPlayed\":9}");
Check("old level file: RacesPlayed read into LoadedLegacyRaces", oldLevel.LoadedLegacyRaces == 9);
Check("legacy 'RacesPlayed' not written back out", !JsonConvert.SerializeObject(oldLevel).Contains("RacesPlayed"));

// ---- Start fresh (seasons)
var wipeAt = t0.AddDays(1);
var offline = SyncDoc.Clone(m);                 // a PC that was offline during the wipe, still has old data
var p1off = offline.Profiles.Single(p => p.Id == "p1");
p1off.StatsByMachine["C"] = new PlayerStats { Races = 50, Updated = wipeAt.AddHours(1) }; // old-season numbers, updated after the wipe
var lateRun = new RunRecord { Id = "late", ProfileId = "p1", Name = "Alice", Time = 40f, Date = wipeAt.AddHours(2).ToLocalTime() };
offline.Leaderboard.Levels["M"].Runs.Add(lateRun);  // raced after the wipe, while offline

var wiped = SyncDoc.Clone(m);                    // the PC that pressed Start fresh (keep profiles)
wiped.SeasonStart = wipeAt;
foreach (var p in wiped.Profiles) { p.StatsByMachine.Clear(); p.StatsResetAt = wipeAt; }
wiped.Leaderboard = new LeaderboardData();

var w = Merge(offline, wiped);
Check("start fresh: season travels", w.SeasonStart == wipeAt);
Check("start fresh: profiles kept", w.Profiles.Count == m.Profiles.Count);
Check("start fresh: old-season stats dropped, even if updated after the wipe", w.Profiles.All(p => p.StatsByMachine.Count == 0));
Check("start fresh: old runs dropped, run after the wipe kept",
      w.Leaderboard.Levels.TryGetValue("M", out var wm) && wm.Runs.Select(r => r.Id).SequenceEqual(new[] { "late" }));
Check("start fresh: old race counts dropped", wm != null && wm.RacesPlayed == 0);
Check("start fresh: same result both ways", Merge(wiped, offline).ToJson() == w.ToJson());

// New-season stats survive later merges
var after = SyncDoc.Clone(w);
after.Profiles.Single(p => p.Id == "p1").StatsByMachine["A"] = new PlayerStats { Races = 2, Season = wipeAt, Updated = wipeAt.AddHours(3) };
Check("start fresh: new-season stats kept", Merge(after, offline).Profiles.Single(p => p.Id == "p1").StatsByMachine["A"].Races == 2);

// Wipe everything: profiles not touched since the wipe are dropped; one added later survives
var all = SyncDoc.Clone(wiped);
all.ProfilesWipedAt = wipeAt; all.Profiles.Clear();
var offline2 = SyncDoc.Clone(offline);
offline2.Profiles.Add(new Profile { Id = "new", Name = "added later", Modified = wipeAt.AddHours(5) });
var wa = Merge(offline2, all);
Check("wipe everything: old profiles dropped, later one kept", wa.Profiles.Select(p => p.Id).SequenceEqual(new[] { "new" }));

// ---- Version guard
var isNewer = syncType.GetMethod("IsNewer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
bool Newer(string x, string y) => (bool)isNewer.Invoke(null, new object[] { x, y });
Check("version guard: 0.9.4 newer than 0.9.3", Newer("0.9.4", "0.9.3"));
Check("version guard: 1.0.0 newer than 0.10.0", Newer("1.0.0", "0.10.0") && !Newer("0.9.10", "0.10.0"));
Check("version guard: 0.10.0 newer than 0.9.9 (numeric, not text)", Newer("0.10.0", "0.9.9"));
Check("version guard: missing version counts as oldest", Newer("0.9.4", null) && !Newer(null, "0.9.4"));
Check("version guard: same version is not newer", !Newer("0.9.4", "0.9.4"));
var va = SyncDoc.Clone(m); va.WrittenBy = "0.9.4"; var vb = SyncDoc.Clone(m); vb.WrittenBy = "1.0.0";
Check("version guard: merge keeps the newest WrittenBy", Merge(va, vb).WrittenBy == "1.0.0" && Merge(vb, va).WrittenBy == "1.0.0");

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
return failures;
