using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using ZeepSDK.Storage;

namespace CouchParty
{
    /// <summary>A saved player: a name, optionally linked to a Steam friend, plus a favourite outfit.</summary>
    public class Profile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New player";
        public ulong SteamId { get; set; }

        // Outfit, stored as the game's complete cosmetic ids (family + item), plus names for display.
        public int SoapboxId { get; set; } = -1;
        public int HatId { get; set; } = -1;
        public int ColorId { get; set; } = -1;
        public string SoapboxName { get; set; }
        public string HatName { get; set; }
        public string ColorName { get; set; }

        /// <summary>2 = ids are complete ids. Older saves used per-family ids, which aren't unique.</summary>
        public int OutfitVersion { get; set; }

        [JsonIgnore]
        public bool HasOutfit => SoapboxId >= 0 || HatId >= 0 || ColorId >= 0;

        /// <summary>When name / Steam link / archived state last changed (UTC). Newest wins when syncing.</summary>
        public DateTime Modified { get; set; }
        public bool IsArchived { get; set; }

        /// <summary>Stats per machine (each PC only adds to its own entry); the totals are their sum.</summary>
        public Dictionary<string, PlayerStats> StatsByMachine { get; set; } = new Dictionary<string, PlayerStats>();

        /// <summary>"Reset stats" time (UTC): machine entries not updated since then are dropped.</summary>
        public DateTime StatsResetAt { get; set; }

        /// <summary>This machine's stats entry: the one to add new numbers to.</summary>
        [JsonIgnore]
        public PlayerStats MyStats
        {
            get
            {
                if (StatsByMachine == null) StatsByMachine = new Dictionary<string, PlayerStats>();
                if (!StatsByMachine.TryGetValue(Store.MachineId, out var st))
                    StatsByMachine[Store.MachineId] = st = new PlayerStats();
                return st;
            }
        }

        private PlayerStats _total;
        private int _totalGeneration = -1;

        /// <summary>Lifetime totals over all machines (cached until stats change).</summary>
        [JsonIgnore]
        public PlayerStats Stats
        {
            get
            {
                if (_total == null || _totalGeneration != Store.StatsGeneration)
                {
                    _total = PlayerStats.Sum(StatsByMachine?.Values ?? Enumerable.Empty<PlayerStats>());
                    _totalGeneration = Store.StatsGeneration;
                }
                return _total;
            }
        }

        /// <summary>Stats saved by versions before sync (one set per profile); moved into StatsByMachine on load.</summary>
        [JsonProperty("Stats", NullValueHandling = NullValueHandling.Ignore)]
        public PlayerStats LegacyStats { get => null; set => LoadedLegacyStats = value; }

        [JsonIgnore] public PlayerStats LoadedLegacyStats;

        public void ClearOutfit()
        {
            SoapboxId = HatId = ColorId = -1;
            SoapboxName = HatName = ColorName = null;
        }
    }

    /// <summary>Lifetime local-multiplayer stats for one profile.</summary>
    public class PlayerStats
    {
        public int Races { get; set; }
        public int Wins { get; set; }
        public int Finishes { get; set; }
        public int Dnfs { get; set; }
        public int Shortcuts { get; set; }        // crossed the finish but missed checkpoints
        public int PersonalBests { get; set; }
        public int RecordsBroken { get; set; }
        public int Crashes { get; set; }
        public int WheelsLost { get; set; }
        public double DistanceMeters { get; set; }
        public float TopSpeedKmh { get; set; }
        public double FinishTimeSeconds { get; set; } // sum of valid finish times

        // Valid finishes with wheels missing (a normal soapbox has 4).
        public int ThreeWheelFinishes { get; set; }
        public int TwoWheelFinishes { get; set; }
        public int OneWheelFinishes { get; set; }
        public int ZeroWheelFinishes { get; set; }

        /// <summary>Valid finishes where the character had already ejected/crashed and still crossed the line.</summary>
        public int OnFootFinishes { get; set; }

        // Bumper cars: collisions with other racers.
        public int Bumps { get; set; }
        public int Rams { get; set; }        // you drove into them
        public int GotRammed { get; set; }   // they drove into you
        public int Knockouts { get; set; }   // someone crashed, fell off or respawned after bumping into you
        public int WheelsKnockedOff { get; set; } // wheels other racers lost right after bumping into you
        public int CatsPetted { get; set; }        // walking cats hit by your car
        public int CatCrashes { get; set; }        // crashed after hitting a cat, before getting back on track

        // Air time: no wheel and no part of the car body touching anything (while racing).
        public double AirSeconds { get; set; }
        public float LongestJumpSeconds { get; set; }
        public int BigJumps { get; set; }          // 1 second or more in the air

        /// <summary>When this entry last changed (UTC).</summary>
        public DateTime Updated { get; set; }

        [JsonIgnore]
        public int DamagedFinishes => ThreeWheelFinishes + TwoWheelFinishes + OneWheelFinishes + ZeroWheelFinishes;

        /// <summary>Adds up several machines' stats (counts are summed, records like top speed take the maximum).</summary>
        public static PlayerStats Sum(IEnumerable<PlayerStats> parts)
        {
            var t = new PlayerStats();
            foreach (var s in parts)
            {
                if (s == null) continue;
                t.Races += s.Races; t.Wins += s.Wins; t.Finishes += s.Finishes; t.Dnfs += s.Dnfs;
                t.Shortcuts += s.Shortcuts; t.PersonalBests += s.PersonalBests; t.RecordsBroken += s.RecordsBroken;
                t.Crashes += s.Crashes; t.WheelsLost += s.WheelsLost; t.DistanceMeters += s.DistanceMeters;
                t.TopSpeedKmh = Math.Max(t.TopSpeedKmh, s.TopSpeedKmh); t.FinishTimeSeconds += s.FinishTimeSeconds;
                t.ThreeWheelFinishes += s.ThreeWheelFinishes; t.TwoWheelFinishes += s.TwoWheelFinishes;
                t.OneWheelFinishes += s.OneWheelFinishes; t.ZeroWheelFinishes += s.ZeroWheelFinishes;
                t.OnFootFinishes += s.OnFootFinishes;
                t.Bumps += s.Bumps; t.Rams += s.Rams; t.GotRammed += s.GotRammed; t.Knockouts += s.Knockouts;
                t.WheelsKnockedOff += s.WheelsKnockedOff;
                t.CatsPetted += s.CatsPetted;
                t.CatCrashes += s.CatCrashes;
                t.AirSeconds += s.AirSeconds; t.LongestJumpSeconds = Math.Max(t.LongestJumpSeconds, s.LongestJumpSeconds);
                t.BigJumps += s.BigJumps;
                if (s.Updated > t.Updated) t.Updated = s.Updated;
            }
            return t;
        }
    }

    public class ProfileData
    {
        public List<Profile> Profiles { get; set; } = new List<Profile>();

        /// <summary>Profile id assigned to Player 1..4 (null/empty = no profile).</summary>
        public string[] SlotProfileIds { get; set; } = new string[4];

        /// <summary>Deleted profiles, kept so re-adding the same player restores their records and stats.</summary>
        public List<Profile> Archived { get; set; } = new List<Profile>();
    }

    /// <summary>One profile's best time on one map.</summary>
    public class BestTime
    {
        public string ProfileId { get; set; }
        public string Name { get; set; }
        public float Time { get; set; }
        public DateTime Date { get; set; }
        public int Finishes { get; set; }
    }

    public class LevelRecords
    {
        public string LevelName { get; set; }
        public string Author { get; set; }
        public DateTime LastPlayed { get; set; }

        /// <summary>Races played on this map, per machine.</summary>
        public Dictionary<string, int> RacesByMachine { get; set; } = new Dictionary<string, int>();

        [JsonIgnore]
        public int RacesPlayed => RacesByMachine?.Values.Sum() ?? 0;

        public void AddRace()
        {
            if (RacesByMachine == null) RacesByMachine = new Dictionary<string, int>();
            RacesByMachine.TryGetValue(Store.MachineId, out int n);
            RacesByMachine[Store.MachineId] = n + 1;
        }

        /// <summary>Saved by versions before sync; moved into RacesByMachine on load.</summary>
        [JsonProperty("RacesPlayed", NullValueHandling = NullValueHandling.Ignore)]
        public int? LegacyRacesPlayed { get => null; set => LoadedLegacyRaces = value; }
        [JsonIgnore] public int? LoadedLegacyRaces;

        public Dictionary<string, BestTime> Best { get; set; } = new Dictionary<string, BestTime>();

        /// <summary>Every valid run (several per player), fastest kept; capped at <see cref="MaxRuns"/>.</summary>
        public List<RunRecord> Runs { get; set; } = new List<RunRecord>();

        public const int MaxRuns = 200;

        /// <summary>One time per player, fastest first.</summary>
        public List<BestTime> Ranked() => Best.Values.OrderBy(b => b.Time).ToList();

        /// <summary>Fastest runs overall; a player can appear several times.</summary>
        public List<RunRecord> TopRuns() => Runs.OrderBy(r => r.Time).ToList();

        public void AddRun(RunRecord run)
        {
            Runs.Add(run);
            if (Runs.Count > MaxRuns)
                Runs = Runs.OrderBy(r => r.Time).Take(MaxRuns).ToList();
        }

        /// <summary>Removes one run and recalculates that player's best from what's left.</summary>
        public void RemoveRun(RunRecord run)
        {
            Runs.Remove(run);
            Store.Leaderboard.ForgetRun(run.Id);
            var left = Runs.Where(r => r.ProfileId == run.ProfileId).OrderBy(r => r.Time).FirstOrDefault();
            if (left == null) Best.Remove(run.ProfileId);
            else if (Best.TryGetValue(run.ProfileId, out var best))
            {
                best.Time = left.Time;
                best.Date = left.Date;
            }
        }

        /// <summary>Removes a player's best and all their runs on this map.</summary>
        public void RemovePlayer(string profileId)
        {
            Best.Remove(profileId);
            foreach (var r in Runs.Where(r => r.ProfileId == profileId)) Store.Leaderboard.ForgetRun(r.Id);
            Runs.RemoveAll(r => r.ProfileId == profileId);
        }

        /// <summary>Recomputes each player's best from the runs (after merging).</summary>
        public void RebuildBest()
        {
            var old = Best;
            Best = new Dictionary<string, BestTime>();
            foreach (var g in Runs.GroupBy(r => r.ProfileId))
            {
                var fastest = g.OrderBy(r => r.Time).First();
                Best[g.Key] = new BestTime
                {
                    ProfileId = g.Key, Name = fastest.Name, Time = fastest.Time, Date = fastest.Date,
                    Finishes = Math.Max(g.Count(), old.TryGetValue(g.Key, out var b) ? b.Finishes : 0),
                };
            }
        }
    }

    /// <summary>One valid finish on a map.</summary>
    public class RunRecord
    {
        /// <summary>Unique id, so the same run is recognised on every machine when syncing.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Machine { get; set; }
        public string ProfileId { get; set; }
        public string Name { get; set; }
        public float Time { get; set; }
        public DateTime Date { get; set; }
    }

    public class LeaderboardData
    {
        public Dictionary<string, LevelRecords> Levels { get; set; } = new Dictionary<string, LevelRecords>();

        /// <summary>Runs removed by hand; remembered so syncing doesn't bring them back.</summary>
        public HashSet<string> DeletedRunIds { get; set; } = new HashSet<string>();

        /// <summary>Maps cleared by hand (UTC time); anything older than this doesn't come back when syncing.</summary>
        public Dictionary<string, DateTime> ClearedLevels { get; set; } = new Dictionary<string, DateTime>();

        public void ForgetRun(string runId)
        {
            if (!string.IsNullOrEmpty(runId)) DeletedRunIds.Add(runId);
        }

        public void ClearLevel(string key)
        {
            if (Levels.TryGetValue(key, out var level))
                foreach (var r in level.Runs) ForgetRun(r.Id);
            Levels.Remove(key);
            ClearedLevels[key] = DateTime.UtcNow;
        }
    }

    /// <summary>Loads and saves everything through ZeepSDK storage (%AppData%\Zeepkist\Mods\&lt;mod id&gt;).</summary>
    public static class Store
    {
        private const string ProfilesFile = "profiles";
        private const string LeaderboardFile = "leaderboard";

        private static IModStorage _storage;

        public static ProfileData Profiles { get; private set; } = new ProfileData();
        public static LeaderboardData Leaderboard { get; private set; } = new LeaderboardData();

        /// <summary>Where ZeepSDK keeps this mod's files: %AppData%\Zeepkist\Mods\&lt;mod id&gt;.</summary>
        public static string DataDir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zeepkist", "Mods", Plugin.Guid);

        public static string FilePath(string name) => System.IO.Path.Combine(DataDir, name + ".json");
        public static readonly string[] DataFiles = { ProfilesFile, LeaderboardFile };

        /// <summary>Set when a data file couldn't be read at start-up (shown in game).</summary>
        public static string LoadWarning { get; private set; }

        private static string _machineId;

        /// <summary>Random id of this PC (stored next to the data, never synced as a file).</summary>
        public static string MachineId
        {
            get
            {
                if (_machineId != null) return _machineId;
                string path = System.IO.Path.Combine(DataDir, "machine-id.txt");
                try
                {
                    if (System.IO.File.Exists(path)) _machineId = System.IO.File.ReadAllText(path).Trim();
                    if (string.IsNullOrEmpty(_machineId))
                    {
                        _machineId = Guid.NewGuid().ToString("N").Substring(0, 12);
                        System.IO.Directory.CreateDirectory(DataDir);
                        System.IO.File.WriteAllText(path, _machineId);
                    }
                }
                catch { _machineId = _machineId ?? "local"; }
                return _machineId;
            }
        }

        /// <summary>Bumped whenever stats change, so cached totals are recalculated.</summary>
        public static int StatsGeneration { get; private set; }
        public static void StatsChanged() => StatsGeneration++;

        /// <summary>While true, saving doesn't schedule a sync (used when applying synced data).</summary>
        internal static bool SuppressSync;

        public static void Load(IModStorage storage)
        {
            _storage = storage;
            // Back up the raw files before anything reads or writes them.
            try { Backups.OnStartup(); }
            catch (Exception e) { Plugin.Log.LogError($"Start-up backup failed: {e}"); }
            ReadAll();
        }

        /// <summary>(Re)reads both files, e.g. after restoring a backup.</summary>
        public static void ReadAll()
        {
            Profiles = TryLoad<ProfileData>(ProfilesFile) ?? new ProfileData();
            Leaderboard = TryLoad<LeaderboardData>(LeaderboardFile) ?? new LeaderboardData();

            if (Profiles.Profiles == null) Profiles.Profiles = new List<Profile>();
            if (Profiles.Archived == null) Profiles.Archived = new List<Profile>();
            bool upgraded = false;
            foreach (var p in Profiles.Profiles) p.IsArchived = false;
            foreach (var p in Profiles.Archived) p.IsArchived = true;
            foreach (var p in Profiles.Profiles.Concat(Profiles.Archived))
            {
                if (p.StatsByMachine == null) p.StatsByMachine = new Dictionary<string, PlayerStats>();
                // Stats from before sync belong to this machine.
                if (p.LoadedLegacyStats != null && !p.StatsByMachine.ContainsKey(MachineId))
                {
                    p.LoadedLegacyStats.Updated = DateTime.UtcNow;
                    p.StatsByMachine[MachineId] = p.LoadedLegacyStats;
                    upgraded = true;
                }
                p.LoadedLegacyStats = null;
                if (p.Modified == default) { p.Modified = DateTime.UtcNow; upgraded = true; }
            }
            StatsChanged();
            if (Profiles.SlotProfileIds == null || Profiles.SlotProfileIds.Length != 4)
            {
                var fixedSlots = new string[4];
                if (Profiles.SlotProfileIds != null)
                    Array.Copy(Profiles.SlotProfileIds, fixedSlots, Math.Min(4, Profiles.SlotProfileIds.Length));
                Profiles.SlotProfileIds = fixedSlots;
            }
            if (Leaderboard.Levels == null) Leaderboard.Levels = new Dictionary<string, LevelRecords>();
            if (Leaderboard.DeletedRunIds == null) Leaderboard.DeletedRunIds = new HashSet<string>();
            if (Leaderboard.ClearedLevels == null) Leaderboard.ClearedLevels = new Dictionary<string, DateTime>();

            // Older saves only kept each player's best: start their run list from those.
            foreach (var level in Leaderboard.Levels.Values)
            {
                if (level.Runs == null) level.Runs = new List<RunRecord>();
                if (level.Best == null) level.Best = new Dictionary<string, BestTime>();
                if (level.RacesByMachine == null) level.RacesByMachine = new Dictionary<string, int>();
                if (level.Runs.Count == 0)
                    foreach (var b in level.Best.Values)
                        level.Runs.Add(new RunRecord { ProfileId = b.ProfileId, Name = b.Name, Time = b.Time, Date = b.Date });
                // Runs and race counts from before sync belong to this machine.
                foreach (var r in level.Runs)
                {
                    if (string.IsNullOrEmpty(r.Id)) { r.Id = Guid.NewGuid().ToString("N"); upgraded = true; }
                    if (string.IsNullOrEmpty(r.Machine)) { r.Machine = MachineId; upgraded = true; }
                }
                if (level.LoadedLegacyRaces.HasValue && !level.RacesByMachine.ContainsKey(MachineId))
                {
                    level.RacesByMachine[MachineId] = level.LoadedLegacyRaces.Value;
                    upgraded = true;
                }
                level.LoadedLegacyRaces = null;
            }
            if (upgraded)
            {
                SuppressSync = true;
                SaveProfiles();
                SaveLeaderboard();
                SuppressSync = false;
            }

            // Outfits saved by 0.2.0 used non-unique ids; forget them (they get re-saved at the next race).
            bool migrated = false;
            foreach (var p in Profiles.Profiles.Where(p => p.OutfitVersion < 2))
            {
                p.ClearOutfit();
                p.OutfitVersion = 2;
                migrated = true;
            }
            if (migrated) SaveProfiles();
        }

        private static T TryLoad<T>(string name) where T : class
        {
            string error;
            try
            {
                if (!_storage.JsonFileExists(name)) return null;
                var result = _storage.TryLoadFromJson<T>(name);
                if (result.IsSuccess && result.Value != null) return result.Value;
                error = result.Error ?? "empty file";
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            // Never let a file we couldn't read be overwritten by an empty one: move it aside.
            string moved = Quarantine(name);
            Plugin.Log.LogError($"Could not read {name}.json ({error}). " +
                                (moved != null ? $"It was moved to {System.IO.Path.GetFileName(moved)}. " : "") +
                                "Restore a backup from the Couch Party window (F8 > Backups).");
            LoadWarning = $"Couldn't read {name}.json; it was set aside. Restore a backup from F8 > Backups.";
            return null;
        }

        private static string Quarantine(string name)
        {
            try
            {
                string path = FilePath(name);
                if (!System.IO.File.Exists(path)) return null;
                string target = System.IO.Path.Combine(DataDir, $"{name}.unreadable-{DateTime.Now:yyyy-MM-dd_HHmmss}.json");
                System.IO.File.Move(path, target);
                return target;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not move {name}.json aside: {e.Message}");
                return null;
            }
        }

        public static void SaveProfiles()
        {
            Save(ProfilesFile, Profiles);
            if (!SuppressSync) Sync.MarkDirty();
        }

        public static void SaveLeaderboard()
        {
            Save(LeaderboardFile, Leaderboard);
            if (!SuppressSync) Sync.MarkDirty();
        }

        public static void RenameProfile(Profile p, string name)
        {
            p.Name = name;
            p.Modified = DateTime.UtcNow;
            SaveProfiles();
        }

        /// <summary>Clears every profile's stats on all machines (the reset time travels with sync).</summary>
        public static void ResetAllStats()
        {
            foreach (var p in Profiles.Profiles.Concat(Profiles.Archived))
            {
                p.StatsByMachine.Clear();
                p.StatsResetAt = DateTime.UtcNow;
            }
            StatsChanged();
            SaveProfiles();
        }

        // ---------------- Sync document ----------------

        /// <summary>A copy of everything that syncs (profiles incl. archived, stats, leaderboards).</summary>
        public static SyncDoc ToSyncDoc() => SyncDoc.Clone(new SyncDoc
        {
            Profiles = Profiles.Profiles.Concat(Profiles.Archived).ToList(),
            Leaderboard = Leaderboard,
        });

        /// <summary>Takes merged data. Player 1-4 assignments and outfits stay as they are on this PC.</summary>
        public static void ApplySyncDoc(SyncDoc doc)
        {
            var local = Profiles.Profiles.Concat(Profiles.Archived).ToDictionary(p => p.Id);
            foreach (var p in doc.Profiles)
            {
                if (local.TryGetValue(p.Id, out var mine))
                {
                    p.SoapboxId = mine.SoapboxId; p.HatId = mine.HatId; p.ColorId = mine.ColorId;
                    p.SoapboxName = mine.SoapboxName; p.HatName = mine.HatName; p.ColorName = mine.ColorName;
                    p.OutfitVersion = mine.OutfitVersion;
                }
                else
                {
                    p.ClearOutfit();
                    p.OutfitVersion = 2;
                }
            }
            Profiles.Profiles = doc.Profiles.Where(p => !p.IsArchived).ToList();
            Profiles.Archived = doc.Profiles.Where(p => p.IsArchived).ToList();
            for (int i = 0; i < 4; i++)
                if (GetProfile(Profiles.SlotProfileIds[i]) == null) Profiles.SlotProfileIds[i] = null;
            Leaderboard = doc.Leaderboard;
            StatsChanged();

            SuppressSync = true;
            SaveProfiles();
            SaveLeaderboard();
            SuppressSync = false;
            Game.ApplyNames();
        }

        private static void Save(string name, object data)
        {
            try { _storage?.SaveToJson(name, data); }
            catch (Exception e) { Plugin.Log.LogError($"Could not save {name}.json: {e}"); }
        }

        // ---- Profile helpers ----

        public static Profile GetProfile(string id) =>
            string.IsNullOrEmpty(id) ? null : Profiles.Profiles.FirstOrDefault(p => p.Id == id);

        /// <summary>Profile assigned to a 0-based slot (Player 1 = slot 0).</summary>
        public static Profile GetSlotProfile(int slot) =>
            slot < 0 || slot >= 4 ? null : GetProfile(Profiles.SlotProfileIds[slot]);

        public static void AssignSlot(int slot, Profile profile)
        {
            if (slot < 0 || slot >= 4) return;
            // A profile can only sit in one slot at a time.
            if (profile != null)
                for (int i = 0; i < 4; i++)
                    if (Profiles.SlotProfileIds[i] == profile.Id) Profiles.SlotProfileIds[i] = null;
            Profiles.SlotProfileIds[slot] = profile?.Id;
            SaveProfiles();
        }

        /// <summary>Removes a profile from the list but keeps it (with its stats) in the archive.</summary>
        public static void DeleteProfile(Profile profile)
        {
            profile.IsArchived = true;
            profile.Modified = DateTime.UtcNow;
            Profiles.Profiles.Remove(profile);
            Profiles.Archived.RemoveAll(p => p.Id == profile.Id);
            Profiles.Archived.Add(profile);
            for (int i = 0; i < 4; i++)
                if (Profiles.SlotProfileIds[i] == profile.Id) Profiles.SlotProfileIds[i] = null;
            SaveProfiles();
        }

        /// <summary>
        /// Adds a player. If they were deleted before (same Steam friend, or same name for non-Steam
        /// profiles) their old profile comes back with its records and stats. Leaderboard times from
        /// profiles deleted before archiving existed are re-linked by name.
        /// Returns the profile and a short note about what was restored (or null).
        /// </summary>
        public static (Profile profile, string restored) AddOrRestoreProfile(string name, ulong steamId)
        {
            var archived = Profiles.Archived.FirstOrDefault(p => steamId != 0
                ? p.SteamId == steamId
                : p.SteamId == 0 && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            Profile profile;
            string note = null;
            if (archived != null)
            {
                Profiles.Archived.Remove(archived);
                archived.IsArchived = false;
                archived.Modified = DateTime.UtcNow;
                archived.Name = name;
                if (steamId != 0) archived.SteamId = steamId;
                profile = archived;
                note = "records and stats";
            }
            else profile = new Profile { Name = name, SteamId = steamId, OutfitVersion = 2, Modified = DateTime.UtcNow };
            Profiles.Profiles.Add(profile);

            int relinked = RelinkOrphanedTimes(profile);
            if (relinked > 0) note = note == null ? $"{relinked} old times" : note + $" (+{relinked} old times)";

            SaveProfiles();
            if (relinked > 0) SaveLeaderboard();
            return (profile, note);
        }

        /// <summary>Leaderboard entries whose profile no longer exists anywhere, with the same name: give them to this profile.</summary>
        private static int RelinkOrphanedTimes(Profile profile)
        {
            var known = new HashSet<string>(Profiles.Profiles.Concat(Profiles.Archived).Select(p => p.Id));
            bool Orphan(string id, string entryName) =>
                id != profile.Id && !known.Contains(id) && string.Equals(entryName, profile.Name, StringComparison.OrdinalIgnoreCase);

            int count = 0;
            foreach (var level in Leaderboard.Levels.Values)
            {
                foreach (var old in level.Best.Values.Where(b => Orphan(b.ProfileId, b.Name)).ToList())
                {
                    level.Best.Remove(old.ProfileId);
                    if (!level.Best.TryGetValue(profile.Id, out var mine) || old.Time < mine.Time)
                    {
                        old.ProfileId = profile.Id;
                        if (mine != null) old.Finishes += mine.Finishes;
                        level.Best[profile.Id] = old;
                    }
                    else mine.Finishes += old.Finishes;
                    count++;
                }
                foreach (var run in level.Runs.Where(r => Orphan(r.ProfileId, r.Name)))
                    run.ProfileId = profile.Id;
            }
            return count;
        }

        /// <summary>Name to show for a leaderboard entry: the profile's current name if it still exists.</summary>
        public static string DisplayName(BestTime entry) => GetProfile(entry.ProfileId)?.Name ?? entry.Name;
        public static string DisplayName(RunRecord run) => GetProfile(run.ProfileId)?.Name ?? run.Name;
    }
}
