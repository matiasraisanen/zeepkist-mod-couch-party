using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace CouchParty
{
    /// <summary>
    /// Tonight's play session. You usually grind one map for a while and then move on, so the main score is
    /// "best times": on how many of tonight's maps a player has the fastest time of the evening.
    /// Kept on this PC only (not synced, not backed up). Survives a game restart; a new evening starts
    /// after <see cref="Plugin.SessionResetHours"/> hours without a race.
    /// </summary>
    internal static class Session
    {
        public class MapTonight
        {
            public string LevelName { get; set; }
            public int Runs { get; set; }                                   // races on this map tonight
            public DateTime LastPlayed { get; set; }
            public Dictionary<string, float> Best { get; set; } = new Dictionary<string, float>(); // profile -> best time tonight
            public Dictionary<string, int> PlayerRuns { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int> PlayerCrashes { get; set; } = new Dictionary<string, int>();
        }

        /// <summary>One player's grind on one map tonight.</summary>
        public class MapPlayer
        {
            public string Name;
            public float Best = float.MaxValue;   // MaxValue = no valid time yet
            public float Gap = float.MaxValue;    // behind tonight's fastest on this map
            public int Runs;
            public int Crashes;
        }

        public class PlayerTonight
        {
            public string Name { get; set; }
            public int Races { get; set; }
            public int PersonalBests { get; set; }
            public int Records { get; set; }
            public int Crashes { get; set; }
            public int Hits { get; set; }
        }

        public class Data
        {
            public DateTime Started { get; set; } = DateTime.Now;
            public DateTime LastRace { get; set; } = DateTime.Now;
            public Dictionary<string, MapTonight> Maps { get; set; } = new Dictionary<string, MapTonight>();
            public Dictionary<string, PlayerTonight> Players { get; set; } = new Dictionary<string, PlayerTonight>();
        }

        public class Standing
        {
            public string ProfileId;
            public string Name;
            public int BestTimes;
            public PlayerTonight Stats;
        }

        private static Data _data;
        private static string FilePath => Path.Combine(Store.DataDir, "session.json");

        public static Data Current
        {
            get
            {
                if (_data == null) Load();
                if ((DateTime.Now - _data.LastRace).TotalHours > Math.Max(1, Plugin.SessionResetHours.Value))
                    _data = new Data(); // a new evening
                return _data;
            }
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(FilePath)) _data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(FilePath));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Could not read session.json, starting a new evening: {e.Message}"); }
            if (_data == null) _data = new Data();
            if (_data.Maps == null) _data.Maps = new Dictionary<string, MapTonight>();
            if (_data.Players == null) _data.Players = new Dictionary<string, PlayerTonight>();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Store.DataDir);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(_data, Formatting.Indented));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Could not save session.json: {e.Message}"); }
        }

        /// <summary>Adds a saved race to tonight's session.</summary>
        public static void AddRace(RaceSummary summary)
        {
            if (summary == null || string.IsNullOrEmpty(summary.LevelKey)) return;
            var data = Current;
            data.LastRace = DateTime.Now;

            if (!data.Maps.TryGetValue(summary.LevelKey, out var map))
                data.Maps[summary.LevelKey] = map = new MapTonight();
            map.LevelName = summary.LevelName;
            map.Runs++;
            map.LastPlayed = DateTime.Now;
            if (map.PlayerRuns == null) map.PlayerRuns = new Dictionary<string, int>();
            if (map.PlayerCrashes == null) map.PlayerCrashes = new Dictionary<string, int>();

            foreach (var line in summary.Lines)
            {
                var profile = Store.GetSlotProfile(line.Slot);
                if (profile == null) continue;
                if (!data.Players.TryGetValue(profile.Id, out var p)) data.Players[profile.Id] = p = new PlayerTonight();
                p.Name = profile.Name;
                p.Races++;
                if (line.NewPersonalBest) p.PersonalBests++;
                if (line.NewMapRecord) p.Records++;
                map.PlayerRuns.TryGetValue(profile.Id, out int runs);
                map.PlayerRuns[profile.Id] = runs + 1;
                var h = summary.Highlights.FirstOrDefault(x => x.Slot == line.Slot);
                if (h != null)
                {
                    p.Crashes += h.Crashes;
                    p.Hits += h.Hits;
                    map.PlayerCrashes.TryGetValue(profile.Id, out int crashes);
                    map.PlayerCrashes[profile.Id] = crashes + h.Crashes;
                }
                if (line.Finished && (!map.Best.TryGetValue(profile.Id, out var best) || line.Time < best))
                    map.Best[profile.Id] = line.Time;
            }
            Save();
        }

        /// <summary>Tonight's standings: most "best times" first, then PBs, then records.</summary>
        public static List<Standing> Standings()
        {
            var data = Current;
            var holders = data.Maps.Values
                .Where(m => m.Best.Count > 0)
                .Select(m => m.Best.OrderBy(kv => kv.Value).First().Key)
                .ToList();
            return data.Players
                .Select(kv => new Standing
                {
                    ProfileId = kv.Key,
                    Name = Store.GetProfile(kv.Key)?.Name ?? kv.Value.Name,
                    BestTimes = holders.Count(id => id == kv.Key),
                    Stats = kv.Value,
                })
                .OrderByDescending(s => s.BestTimes)
                .ThenByDescending(s => s.Stats.PersonalBests)
                .ThenByDescending(s => s.Stats.Records)
                .ThenBy(s => s.Name)
                .ToList();
        }

        /// <summary>"Alice 0:25.429 (14 runs)" for a map tonight, or null.</summary>
        public static string MapLine(string levelKey)
        {
            if (string.IsNullOrEmpty(levelKey) || !Current.Maps.TryGetValue(levelKey, out var map)) return null;
            if (map.Best.Count == 0) return $"no valid times yet ({map.Runs} run{(map.Runs == 1 ? "" : "s")})";
            var best = map.Best.OrderBy(kv => kv.Value).First();
            string name = Store.GetProfile(best.Key)?.Name ?? Current.Players.GetOrNull(best.Key)?.Name ?? "?";
            return $"{name} {Game.FormatTime(best.Value)} ({map.Runs} run{(map.Runs == 1 ? "" : "s")})";
        }

        public static int MapsTonight => Current.Maps.Count;
        public static int RunsTonight => Current.Maps.Values.Sum(m => m.Runs);

        /// <summary>Tonight's maps, most recently played first.</summary>
        public static List<(string key, MapTonight map)> MapsNewestFirst() =>
            Current.Maps.OrderByDescending(kv => kv.Value.LastPlayed).Select(kv => (kv.Key, kv.Value)).ToList();

        public static string NameOf(string profileId) =>
            Store.GetProfile(profileId)?.Name ?? Current.Players.GetOrNull(profileId)?.Name ?? "?";

        /// <summary>The grind on one map tonight: everyone who raced it, fastest first.</summary>
        public static List<MapPlayer> MapStandings(string levelKey, out int totalRuns)
        {
            totalRuns = 0;
            var list = new List<MapPlayer>();
            if (string.IsNullOrEmpty(levelKey) || !Current.Maps.TryGetValue(levelKey, out var map)) return list;
            totalRuns = map.Runs;
            var ids = (map.PlayerRuns ?? new Dictionary<string, int>()).Keys.Union(map.Best.Keys);
            foreach (var id in ids)
            {
                var mp = new MapPlayer { Name = NameOf(id) };
                if (map.Best.TryGetValue(id, out var best)) mp.Best = best;
                if (map.PlayerRuns != null && map.PlayerRuns.TryGetValue(id, out var runs)) mp.Runs = runs;
                if (map.PlayerCrashes != null && map.PlayerCrashes.TryGetValue(id, out var crashes)) mp.Crashes = crashes;
                list.Add(mp);
            }
            float fastest = list.Count > 0 ? list.Min(p => p.Best) : float.MaxValue;
            foreach (var p in list)
                if (p.Best < float.MaxValue && fastest < float.MaxValue) p.Gap = p.Best - fastest;
            return list.OrderBy(p => p.Best).ThenByDescending(p => p.Runs).ToList();
        }

        /// <summary>"Start a new evening" by hand.</summary>
        public static void Reset()
        {
            _data = new Data();
            Save();
        }
    }

    internal static class DictionaryExtensions
    {
        public static TValue GetOrNull<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key) where TValue : class =>
            dict.TryGetValue(key, out var value) ? value : null;
    }
}
