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

        public PlayerStats Stats { get; set; } = new PlayerStats();

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
        public int Knockouts { get; set; }   // someone crashed within 2 s of bumping into you

        [JsonIgnore]
        public int DamagedFinishes => ThreeWheelFinishes + TwoWheelFinishes + OneWheelFinishes + ZeroWheelFinishes;
    }

    public class ProfileData
    {
        public List<Profile> Profiles { get; set; } = new List<Profile>();

        /// <summary>Profile id assigned to Player 1..4 (null/empty = no profile).</summary>
        public string[] SlotProfileIds { get; set; } = new string[4];
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
        public int RacesPlayed { get; set; }
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
            Runs.RemoveAll(r => r.ProfileId == profileId);
        }
    }

    /// <summary>One valid finish on a map.</summary>
    public class RunRecord
    {
        public string ProfileId { get; set; }
        public string Name { get; set; }
        public float Time { get; set; }
        public DateTime Date { get; set; }
    }

    public class LeaderboardData
    {
        public Dictionary<string, LevelRecords> Levels { get; set; } = new Dictionary<string, LevelRecords>();
    }

    /// <summary>Loads and saves everything through ZeepSDK storage (%AppData%\Zeepkist\Mods\&lt;mod id&gt;).</summary>
    public static class Store
    {
        private const string ProfilesFile = "profiles";
        private const string LeaderboardFile = "leaderboard";

        private static IModStorage _storage;

        public static ProfileData Profiles { get; private set; } = new ProfileData();
        public static LeaderboardData Leaderboard { get; private set; } = new LeaderboardData();

        public static void Load(IModStorage storage)
        {
            _storage = storage;
            Profiles = TryLoad<ProfileData>(ProfilesFile) ?? new ProfileData();
            Leaderboard = TryLoad<LeaderboardData>(LeaderboardFile) ?? new LeaderboardData();

            if (Profiles.Profiles == null) Profiles.Profiles = new List<Profile>();
            foreach (var p in Profiles.Profiles) if (p.Stats == null) p.Stats = new PlayerStats();
            if (Profiles.SlotProfileIds == null || Profiles.SlotProfileIds.Length != 4)
            {
                var fixedSlots = new string[4];
                if (Profiles.SlotProfileIds != null)
                    Array.Copy(Profiles.SlotProfileIds, fixedSlots, Math.Min(4, Profiles.SlotProfileIds.Length));
                Profiles.SlotProfileIds = fixedSlots;
            }
            if (Leaderboard.Levels == null) Leaderboard.Levels = new Dictionary<string, LevelRecords>();

            // Older saves only kept each player's best: start their run list from those.
            foreach (var level in Leaderboard.Levels.Values)
            {
                if (level.Runs == null) level.Runs = new List<RunRecord>();
                if (level.Runs.Count == 0)
                    foreach (var b in level.Best.Values)
                        level.Runs.Add(new RunRecord { ProfileId = b.ProfileId, Name = b.Name, Time = b.Time, Date = b.Date });
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
            try
            {
                if (!_storage.JsonFileExists(name)) return null;
                var result = _storage.TryLoadFromJson<T>(name);
                if (result.IsSuccess) return result.Value;
                Plugin.Log.LogError($"Could not read {name}.json: {result.Error}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not read {name}.json: {e}");
            }
            return null;
        }

        public static void SaveProfiles() => Save(ProfilesFile, Profiles);
        public static void SaveLeaderboard() => Save(LeaderboardFile, Leaderboard);

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

        public static void DeleteProfile(Profile profile)
        {
            Profiles.Profiles.Remove(profile);
            for (int i = 0; i < 4; i++)
                if (Profiles.SlotProfileIds[i] == profile.Id) Profiles.SlotProfileIds[i] = null;
            SaveProfiles();
        }

        /// <summary>Name to show for a leaderboard entry: the profile's current name if it still exists.</summary>
        public static string DisplayName(BestTime entry) => GetProfile(entry.ProfileId)?.Name ?? entry.Name;
        public static string DisplayName(RunRecord run) => GetProfile(run.ProfileId)?.Name ?? run.Name;
    }
}
