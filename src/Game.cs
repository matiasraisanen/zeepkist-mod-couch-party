using System;
using System.Collections.Generic;
using System.Linq;
using ZeepSDK.Level;
using ZeepSDK.Multiplayer;

namespace CouchParty
{
    public class RaceLine
    {
        public int Slot;          // 0-based (Player 1 = 0)
        public string Name;
        public float Time;        // <= 0 means did not finish
        public int Checkpoints;   // checkpoints this player passed
        public bool MissedCheckpoints;
        /// <summary>Crossed the finish with every checkpoint: the only times that count.</summary>
        public bool Finished => Time > 0 && !MissedCheckpoints;
        public bool CrossedFinish => Time > 0;
        public bool HasProfile;
        public bool NewPersonalBest;
        public bool NewMapRecord;
        public float PreviousBest = float.MaxValue;   // own best before this race
        public float GapToRecord = float.MaxValue;    // behind the map record (as it stood before this race)
    }

    /// <summary>One player's numbers from a single race, for the results screen highlights.</summary>
    public class RaceHighlight
    {
        public int Slot;
        public string Name;
        public float TopSpeed;      // km/h
        public float LongestJump;   // seconds
        public int Hits;            // car-to-car hits
        public int Knockouts;
        public int Cats;
        public int Crashes;
        public int WheelsLost;
        public int Rams;            // rammed others
    }

    public class RaceSummary
    {
        public string LevelKey;
        public string LevelName;
        public int TotalCheckpoints;
        public string PreviousRecordName;              // record holder before this race (null if none)
        public float PreviousRecordTime = float.MaxValue;
        public List<RaceLine> Lines = new List<RaceLine>();
        public List<BestTime> AllTime = new List<BestTime>();   // best per player
        public List<RunRecord> AllRuns = new List<RunRecord>(); // fastest runs overall
        public List<RaceHighlight> Highlights = new List<RaceHighlight>(); // filled by the stats tracker
        public List<string> Moments = new List<string>();                  // "Alice knocked out Bob", ...
    }

    /// <summary>Reads and writes the game's local-player state.</summary>
    public static class Game
    {
        public const int MaxSlots = 4;

        // What we last wrote into PlayerManager.playersNames, so we can undo it when a profile is removed.
        private static readonly string[] AppliedNames = new string[MaxSlots];

        public static PlayerManager Manager => PlayerManager.Instance;

        public static bool IsOnline
        {
            get
            {
                try { return MultiplayerApi.IsPlayingOnline; }
                catch { return false; }
            }
        }

        private static List<string> Names => Access.Get<List<string>>(Manager, "playersNames");
        private static List<bool> Active => Access.Get<List<bool>>(Manager, "playersActive");

        public static bool IsSlotActive(int slot)
        {
            var active = Active;
            return active != null && slot >= 0 && slot < active.Count && active[slot];
        }

        public static int ActivePlayerCount()
        {
            var pm = Manager;
            if (pm == null) return 0;
            int amount = Access.Get(pm, "amountOfPlayers", 0);
            if (amount > 0) return amount;
            return Active?.Count(a => a) ?? 0;
        }

        public static bool IsLocalMultiplayer() => !IsOnline && ActivePlayerCount() >= 2;

        public static string DefaultName(int slot) => $"Player {slot + 1}";

        // ---------------- Names ----------------

        /// <summary>Writes assigned profile names into the game's player-name list.</summary>
        public static void ApplyNames()
        {
            var names = Names;
            if (names == null || IsOnline) return;

            for (int slot = 0; slot < MaxSlots && slot < names.Count; slot++)
            {
                var profile = Store.GetSlotProfile(slot);
                if (profile != null)
                {
                    if (names[slot] != profile.Name)
                    {
                        Plugin.Debug($"Slot {slot}: name '{names[slot]}' -> '{profile.Name}'");
                        names[slot] = profile.Name;
                    }
                    AppliedNames[slot] = profile.Name;
                }
                else if (AppliedNames[slot] != null)
                {
                    // Profile was unassigned: put the game's default name back.
                    if (names[slot] == AppliedNames[slot])
                        names[slot] = DefaultName(slot);
                    AppliedNames[slot] = null;
                }
            }
        }

        /// <summary>Name for a slot: profile name if assigned, otherwise what the game uses.</summary>
        public static string SlotName(int slot)
        {
            var profile = Store.GetSlotProfile(slot);
            if (profile != null) return profile.Name;
            var names = Names;
            if (names != null && slot >= 0 && slot < names.Count && !string.IsNullOrEmpty(names[slot]))
                return names[slot];
            return DefaultName(slot);
        }

        // ---------------- Outfits ----------------

        /// <summary>The cosmetic object a slot currently has (playersSoapbox / playersHat / playersColor).</summary>
        public static object SlotCosmetic(int slot, string managerList) =>
            Access.Item(Access.List(Manager, managerList), slot);

        /// <summary>Reads what the player in a slot picked on the player-select screen.</summary>
        public static bool TryReadSlotOutfit(int slot, out object soapbox, out object hat, out object color)
        {
            soapbox = SlotCosmetic(slot, "playersSoapbox");
            hat = SlotCosmetic(slot, "playersHat");
            color = SlotCosmetic(slot, "playersColor");
            return soapbox != null || hat != null || color != null;
        }

        /// <summary>Item number within its family; NOT unique across the wardrobe.</summary>
        public static int ItemId(object cosmetic) => cosmetic == null ? -1 : Access.Get(cosmetic, "itemID", -1);

        private static readonly Dictionary<Type, System.Reflection.MethodInfo> CompleteIdMethods = new Dictionary<Type, System.Reflection.MethodInfo>();

        /// <summary>The game's unique cosmetic id (family + item), as used by the wardrobe.</summary>
        public static int CompleteId(object cosmetic)
        {
            if (cosmetic == null) return -1;
            var type = cosmetic.GetType();
            if (!CompleteIdMethods.TryGetValue(type, out var method))
                CompleteIdMethods[type] = method = HarmonyLib.AccessTools.Method(type, "GetCompleteID");
            try { return method != null ? (int)method.Invoke(cosmetic, null) : -1; }
            catch { return -1; }
        }

        public static string CosmeticName(object cosmetic) =>
            cosmetic is UnityEngine.Object o && o != null ? o.name.Replace("(Clone)", "").Trim() : null;

        public static bool IsErrorCosmetic(object cosmetic) => cosmetic == null || Access.Get(cosmetic, "isErrorCosmetic", false);

        // ---------------- Levels ----------------

        /// <summary>The level being played, via ZeepSDK (falls back to the game master's copy).</summary>
        private static object CurrentLevel(GameMaster gm)
        {
            object level = null;
            try { level = LevelApi.CurrentLevel; } catch { /* SDK not ready */ }
            return level ?? Access.Get<object>(gm, "GlobalLevel");
        }

        public static string CurrentLevelKey(GameMaster gm)
        {
            string hash = null;
            try { hash = LevelApi.CurrentHash; } catch { /* SDK not ready */ }
            if (string.IsNullOrEmpty(hash)) hash = Access.Get<string>(CurrentLevel(gm), "UID");
            return hash;
        }

        public static string CurrentLevelName(GameMaster gm)
        {
            var name = Access.Get<string>(CurrentLevel(gm), "Name");
            return string.IsNullOrEmpty(name) ? "Unknown map" : name;
        }

        public static string CurrentLevelAuthor(GameMaster gm)
        {
            var level = CurrentLevel(gm);
            var over = Access.Get<string>(level, "OverrideAuthorName");
            return string.IsNullOrEmpty(over) ? Access.Get(level, "Author", "") : over;
        }

        public static LevelRecords CurrentRecords(GameMaster gm)
        {
            var key = CurrentLevelKey(gm);
            if (key == null) return null;
            Store.Leaderboard.Levels.TryGetValue(key, out var records);
            return records;
        }

        // ---------------- Results ----------------

        private static bool IsValidTime(float t) => t > 0f && t < 36000f && !float.IsNaN(t) && !float.IsInfinity(t);

        /// <summary>One player's result as read from the game's (possibly private) result objects.</summary>
        internal struct GameResult
        {
            public int PlayerNum;
            public float Time;
            public int Position;
            public int Checkpoints;
        }

        public static int ResultCount(GameMaster gm) => Access.List(gm, "playerResults")?.Count ?? 0;

        internal static List<GameResult> ReadResults(GameMaster gm)
        {
            var list = new List<GameResult>();
            var raw = Access.List(gm, "playerResults");
            if (raw == null) return list;
            foreach (var r in raw)
            {
                if (r == null) continue;
                list.Add(new GameResult
                {
                    PlayerNum = Access.Get(r, "playerNum", -1),
                    Time = Access.Get(r, "time", 0f),
                    Position = Access.Get(r, "position", 0),
                    Checkpoints = Access.Get(r, "racepoints", -1),
                });
            }
            return list;
        }

        /// <summary>
        /// Reads the finished round's results from the game, saves new best times and returns
        /// a summary for the results overlay. Returns null when this was not a local multiplayer race.
        /// </summary>
        public static RaceSummary RecordRace(GameMaster gm)
        {
            if (gm == null) return null;
            var results = ReadResults(gm);
            if (results.Count == 0) return null;

            // Checkpoints in this level (the game's own counter; shown as "x/N Checkpoints" on its results screen).
            int totalCheckpoints = Access.Get(gm, "racePoints", 0);

            Plugin.Log.LogInfo($"Round ended. online={IsOnline} players={ActivePlayerCount()} checkpoints={totalCheckpoints} results=" +
                               string.Join(", ", results.Select(r => $"[num={r.PlayerNum} time={r.Time:0.000} pos={r.Position} cp={r.Checkpoints}]")));

            if (IsOnline)
            {
                Plugin.Log.LogInfo("Not saving: online race.");
                return null;
            }
            if (ActivePlayerCount() < 2 && results.Count < 2)
            {
                Plugin.Log.LogInfo($"Not saving: not a local multiplayer race (players={ActivePlayerCount()}, results={results.Count}).");
                return null;
            }

            var key = CurrentLevelKey(gm);
            if (string.IsNullOrEmpty(key))
            {
                Plugin.Log.LogWarning("Not saving: could not work out which map this is.");
                return null;
            }

            int numberBase = NumberBase(results);

            if (!Store.Leaderboard.Levels.TryGetValue(key, out var records))
            {
                records = new LevelRecords();
                Store.Leaderboard.Levels[key] = records;
            }
            records.LevelName = CurrentLevelName(gm);
            records.Author = CurrentLevelAuthor(gm);
            records.LastPlayed = DateTime.Now;
            records.AddRace();

            var previousBest = records.Ranked().FirstOrDefault();
            float previousRecord = previousBest?.Time ?? float.MaxValue;

            var summary = new RaceSummary
            {
                LevelKey = key,
                LevelName = records.LevelName,
                TotalCheckpoints = totalCheckpoints,
                PreviousRecordName = previousBest != null ? Store.DisplayName(previousBest) : null,
                PreviousRecordTime = previousRecord,
            };
            foreach (var r in results)
            {
                int slot = r.PlayerNum - numberBase;
                var profile = Store.GetSlotProfile(slot);
                var line = new RaceLine
                {
                    Slot = slot,
                    Name = SlotName(slot),
                    Time = IsValidTime(r.Time) ? r.Time : 0f,
                    Checkpoints = r.Checkpoints,
                    MissedCheckpoints = totalCheckpoints > 0 && r.Checkpoints >= 0 && r.Checkpoints < totalCheckpoints,
                    HasProfile = profile != null,
                };

                if (line.Finished && profile != null)
                {
                    if (!records.Best.TryGetValue(profile.Id, out var best))
                    {
                        best = new BestTime { ProfileId = profile.Id, Time = float.MaxValue };
                        records.Best[profile.Id] = best;
                    }
                    best.Name = profile.Name;
                    best.Finishes++;
                    records.AddRun(new RunRecord { ProfileId = profile.Id, Name = profile.Name, Time = line.Time, Date = DateTime.Now, Machine = Store.MachineId });
                    if (line.Time < best.Time)
                    {
                        line.PreviousBest = best.Time;
                        // Only call it a PB if they had a time before.
                        line.NewPersonalBest = best.Time < float.MaxValue;
                        best.Time = line.Time;
                        best.Date = DateTime.Now;
                    }
                }
                summary.Lines.Add(line);
                Plugin.Log.LogInfo($"  playerNum {r.PlayerNum} -> Player {slot + 1}: {(profile != null ? profile.Name : "no profile, not saved")}, " +
                                   $"time {FormatTime(line.Time)}, checkpoints {r.Checkpoints}/{totalCheckpoints}" +
                                   $"{(line.CrossedFinish && line.MissedCheckpoints ? " (missed checkpoints, not saved)" : "")}{(line.NewPersonalBest ? " (new PB)" : "")}");
            }

            // New map record: fastest profile time this race beats the old record.
            var fastest = summary.Lines.Where(l => l.Finished && l.HasProfile).OrderBy(l => l.Time).FirstOrDefault();
            if (fastest != null && fastest.Time < previousRecord && previousRecord < float.MaxValue)
                fastest.NewMapRecord = true;

            summary.Lines = summary.Lines
                .OrderBy(l => l.Finished ? 0 : l.CrossedFinish ? 1 : 2)
                .ThenBy(l => l.Time)
                .ThenBy(l => l.Slot)
                .ToList();
            foreach (var l in summary.Lines)
                if (l.Finished && previousRecord < float.MaxValue && !l.NewMapRecord)
                    l.GapToRecord = l.Time - previousRecord;

            summary.AllTime = records.Ranked();
            summary.AllRuns = records.TopRuns();

            Store.SaveLeaderboard();
            Plugin.Log.LogInfo($"Saved race on '{records.LevelName}' (map id {key}, number base {numberBase}).");
            return summary;
        }

        /// <summary>Whether the game's result player numbers count from 0 or 1.</summary>
        internal static int NumberBase(List<GameResult> results)
        {
            int numberBase = Plugin.PlayerNumberBase.Value;
            if (numberBase >= 0) return numberBase;
            // Auto: the game counts from 0 (seen in the logs). Only a player number of 4 or more proves it counts
            // from 1. Not "lowest number is 1": that's also what 0-based results look like when Player 1 isn't racing.
            return results.Any(r => r.PlayerNum >= MaxSlots) ? 1 : 0;
        }

        internal static bool IsValidRaceTime(float t) => IsValidTime(t);

        /// <summary>A total duration (0 shows as 0:00.000, not DNF).</summary>
        public static string FormatDuration(double seconds) => seconds <= 0 ? "0:00.000" : FormatTime((float)seconds);

        public static string FormatTime(float seconds)
        {
            if (seconds <= 0 || seconds >= float.MaxValue) return "DNF";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}"
                : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}";
        }
    }
}
