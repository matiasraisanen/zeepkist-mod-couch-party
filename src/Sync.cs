using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace CouchParty
{
    /// <summary>Everything that syncs between PCs. Player 1-4 assignments and outfits stay local.</summary>
    public class SyncDoc
    {
        public int Format { get; set; } = 1;

        /// <summary>
        /// Mod version that last uploaded this data. A PC with an older mod doesn't know newer fields and would
        /// drop them when uploading, so it only downloads until it's updated (see the version guard in Sync).
        /// </summary>
        public string WrittenBy { get; set; }
        public List<Profile> Profiles { get; set; } = new List<Profile>();
        public LeaderboardData Leaderboard { get; set; } = new LeaderboardData();

        /// <summary>Last "Start fresh" (UTC). Older stats, runs and race counts are dropped when merging.</summary>
        public DateTime SeasonStart { get; set; }

        /// <summary>Last "Start fresh" that wiped profiles (UTC). Profiles not changed since are dropped.</summary>
        public DateTime ProfilesWipedAt { get; set; }

        /// <summary>PC names by machine id (the names of their sync keys).</summary>
        public Dictionary<string, MachineName> MachineNames { get; set; } = new Dictionary<string, MachineName>();

        /// <summary>Profiles joined into another one: old id -> kept id.</summary>
        public Dictionary<string, string> JoinedProfiles { get; set; } = new Dictionary<string, string>();

        /// <summary>Profiles deleted for good: id -> when (UTC).</summary>
        public Dictionary<string, DateTime> DeletedProfiles { get; set; } = new Dictionary<string, DateTime>();

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        public string ToJson() => JsonConvert.SerializeObject(this, Settings);
        public static SyncDoc FromJson(string json) => JsonConvert.DeserializeObject<SyncDoc>(json, Settings);
        public static SyncDoc Clone(SyncDoc doc) => FromJson(doc.ToJson());
    }

    /// <summary>
    /// Syncs profiles, stats and leaderboards through a small server (see the server folder).
    /// Every PC keeps a full local copy and works offline; syncing downloads the server's copy,
    /// merges it with the local one, and uploads the result. Merging never loses a run or a stat:
    ///  - runs: all runs from both sides (by id), minus runs/maps someone removed on purpose
    ///  - stats: kept per machine; each machine's newest entry wins; totals are the sum
    ///  - profile name / Steam link / deleted: the newest change wins
    ///  - the same Steam friend added as two profiles (on two PCs before they synced): joined into the older one
    ///  - a player reset or deleted on one PC: their stats and times go everywhere, and don't come back
    /// </summary>
    internal static class Sync
    {
        public static bool Busy { get; private set; }
        public static DateTime? LastSync { get; private set; }
        public static string LastError { get; private set; }
        public static string LastResult { get; private set; }

        private static bool _dirty;
        private static float _dirtySince;
        private static bool _backedUpThisSession;
        private static bool _warnedOutdated;

        /// <summary>True if version <paramref name="a"/> is newer than <paramref name="b"/> (missing / unreadable = oldest).</summary>
        public static bool IsNewer(string a, string b)
        {
            Version va = null, vb = null;
            Version.TryParse(a ?? "", out va);
            Version.TryParse(b ?? "", out vb);
            return (va ?? new Version(0, 0)) > (vb ?? new Version(0, 0));
        }

        /// <summary>Wait this long after the last change before syncing (several saves in a row become one sync).</summary>
        private const float Debounce = 4f;

        public static bool Configured =>
            Plugin.SyncEnabled.Value && !string.IsNullOrWhiteSpace(Plugin.SyncUrl.Value) && !string.IsNullOrWhiteSpace(Plugin.SyncKey.Value);

        public static void MarkDirty()
        {
            if (!_dirty) _dirtySince = Time.unscaledTime;
            _dirty = true;
        }

        /// <summary>Called every frame: syncs a few seconds after local changes.</summary>
        public static void Tick(MonoBehaviour host)
        {
            if (_dirty && !Busy && Configured && Time.unscaledTime - _dirtySince > Debounce)
                SyncNow(host);
        }

        public static void SyncNow(MonoBehaviour host)
        {
            if (Busy || !Configured || host == null) return;
            _dirty = false;
            host.StartCoroutine(Run());
        }

        // ---------------- Network ----------------

        private static string DataUrl => Plugin.SyncUrl.Value.Trim().TrimEnd('/') + "/api/data";

        private static UnityWebRequest Request(string method, string body = null, string ifMatch = null)
        {
            var req = new UnityWebRequest(DataUrl, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 20 };
            if (body != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.SetRequestHeader("Authorization", "Bearer " + Plugin.SyncKey.Value.Trim());
            if (ifMatch != null) req.SetRequestHeader("If-Match", ifMatch);
            return req;
        }

        private static IEnumerator Run()
        {
            Busy = true;
            LastError = null;
            try
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    // 1) Download the server's copy.
                    SyncDoc remote = null;
                    string etag;
                    using (var get = Request("GET"))
                    {
                        _stepStarted = Time.unscaledTime;
                        yield return get.SendWebRequest();
                        // The server says what this PC's key is called (older servers don't).
                        string pcName = get.GetResponseHeader("X-CouchParty-PC");
                        if (!string.IsNullOrEmpty(pcName))
                        {
                            try { Store.SetMachineName(Uri.UnescapeDataString(pcName)); }
                            catch (Exception e) { Plugin.Log.LogWarning($"Sync: couldn't read this PC's name: {e.Message}"); }
                        }
                        if (get.responseCode == 404) etag = "\"0\""; // nothing stored yet
                        else if (get.result != UnityWebRequest.Result.Success)
                        {
                            Fail(get, "download");
                            yield break;
                        }
                        else
                        {
                            etag = get.GetResponseHeader("ETag") ?? "\"0\"";
                            try { remote = SyncDoc.FromJson(get.downloadHandler.text); }
                            catch (Exception e)
                            {
                                LastError = "The server sent data the mod couldn't read: " + e.Message;
                                yield break;
                            }
                        }
                    }

                    // 2) Merge with what's here right now (on the main thread, so nothing saved meanwhile is lost).
                    if (!_backedUpThisSession)
                    {
                        _backedUpThisSession = true;
                        try { Backups.Create("before sync", keep: false, force: false); }
                        catch (Exception e) { Plugin.Log.LogWarning($"Backup before sync failed: {e.Message}"); }
                    }
                    var local = Store.ToSyncDoc();
                    var merged = remote == null ? local : Merge(local, remote);
                    foreach (var kv in merged.JoinedProfiles.Where(kv => local.JoinedProfiles == null || !local.JoinedProfiles.ContainsKey(kv.Key)))
                        Plugin.Log.LogInfo($"Sync: profile {kv.Key} was the same Steam friend as {kv.Value}; joined them.");
                    local.WrittenBy = merged.WrittenBy; // the version stamp alone isn't a change worth saving
                    string mergedJson = merged.ToJson();
                    if (mergedJson != local.ToJson()) Store.ApplySyncDoc(SyncDoc.FromJson(mergedJson));

                    // Version guard: data from a newer mod may contain fields this version doesn't know,
                    // and uploading would drop them. Keep what we downloaded, but don't upload.
                    if (remote != null && IsNewer(remote.WrittenBy, Plugin.Version))
                    {
                        LastError = $"This PC has Couch Party {Plugin.Version}, but the server's data is from {remote.WrittenBy}. " +
                                    "Downloaded only; update the mod on this PC to keep syncing.";
                        if (!_warnedOutdated)
                        {
                            _warnedOutdated = true;
                            try { ZeepSDK.Messaging.MessengerApi.LogWarning($"Couch Party: update the mod on this PC (server data is from {remote.WrittenBy}).", 10f); }
                            catch { /* messenger not ready */ }
                        }
                        yield break;
                    }
                    merged.WrittenBy = Plugin.Version;
                    mergedJson = merged.ToJson();

                    // 3) Upload, unless the server already has exactly this.
                    if (remote != null && mergedJson == remote.ToJson())
                    {
                        Done("Up to date");
                        yield break;
                    }
                    using (var put = Request("PUT", mergedJson, etag))
                    {
                        _stepStarted = Time.unscaledTime;
                        yield return put.SendWebRequest();
                        if (put.responseCode == 409 || put.responseCode == 412)
                        {
                            Plugin.Debug("Sync: someone else synced at the same time, retrying.");
                            continue; // another PC uploaded in between: merge again
                        }
                        if (put.result != UnityWebRequest.Result.Success)
                        {
                            Fail(put, $"upload ({mergedJson.Length / 1024} KB)");
                            yield break;
                        }
                    }
                    Done(remote == null ? "Uploaded (first sync)" : "Synced");
                    yield break;
                }
                LastError = "The server kept changing during sync. Try again in a moment.";
            }
            finally
            {
                Busy = false;
                if (LastError != null) Plugin.Log.LogWarning("Sync failed: " + LastError);
            }
        }

        private static void Done(string result)
        {
            LastSync = DateTime.Now;
            LastResult = result;
            Plugin.Log.LogInfo($"Sync: {result}.");
        }

        private static float _stepStarted;

        private static void Fail(UnityWebRequest req, string step)
        {
            float waited = Time.unscaledTime - _stepStarted;
            LastError = req.responseCode == 401 || req.responseCode == 403
                ? "The server didn't accept the key."
                : req.responseCode > 0 ? $"{step} failed: server answered {req.responseCode} ({req.error})"
                : $"{step} failed after {waited:0.0} s: {req.error}";
            Plugin.Log.LogWarning($"Sync {step} to {req.url} failed after {waited:0.0} s: code {req.responseCode}, {req.error}");
        }

        // ---------------- Merge ----------------

        /// <summary>Combines two copies; neither input is changed.</summary>
        public static SyncDoc Merge(SyncDoc a, SyncDoc b)
        {
            a = SyncDoc.Clone(a);
            b = SyncDoc.Clone(b);
            // "Start fresh": the newest wipe wins and everything from before it is dropped, on both sides.
            var season = a.SeasonStart > b.SeasonStart ? a.SeasonStart : b.SeasonStart;
            var profilesWiped = a.ProfilesWipedAt > b.ProfilesWipedAt ? a.ProfilesWipedAt : b.ProfilesWipedAt;
            var result = new SyncDoc
            {
                Profiles = MergeProfiles(a.Profiles.Where(p => p.Modified >= profilesWiped).ToList(),
                                         b.Profiles.Where(p => p.Modified >= profilesWiped).ToList(), season),
                Leaderboard = MergeLeaderboards(a.Leaderboard, b.Leaderboard, season),
                SeasonStart = season,
                ProfilesWipedAt = profilesWiped,
                WrittenBy = IsNewer(a.WrittenBy, b.WrittenBy) ? a.WrittenBy : b.WrittenBy,
                MachineNames = MergeMachineNames(a.MachineNames, b.MachineNames),
                JoinedProfiles = MergeJoined(a.JoinedProfiles, b.JoinedProfiles),
                DeletedProfiles = MergeDeleted(a.DeletedProfiles, b.DeletedProfiles),
            };
            result.Profiles.RemoveAll(p => result.DeletedProfiles.ContainsKey(p.Id));
            DropResetAndDeletedTimes(result); // before joining: each profile's own reset applies to its own times
            JoinDuplicates(result);
            DropResetAndDeletedTimes(result); // after: a reset of the kept profile covers the times it took over
            return result;
        }

        private static Dictionary<string, DateTime> MergeDeleted(Dictionary<string, DateTime> a, Dictionary<string, DateTime> b)
        {
            var result = new Dictionary<string, DateTime>();
            foreach (var kv in (a ?? new Dictionary<string, DateTime>()).Concat(b ?? new Dictionary<string, DateTime>()))
                if (!result.TryGetValue(kv.Key, out var have) || kv.Value > have) result[kv.Key] = kv.Value;
            return result;
        }

        /// <summary>Times of deleted players, and of reset players from before their reset, go on every PC.</summary>
        private static void DropResetAndDeletedTimes(SyncDoc doc)
        {
            var byId = doc.Profiles.ToDictionary(p => p.Id);
            foreach (var level in doc.Leaderboard.Levels.Values)
            {
                int removed = level.Runs.RemoveAll(r =>
                {
                    string id = FinalId(doc.JoinedProfiles, r.ProfileId);
                    if (doc.DeletedProfiles.ContainsKey(id)) return true;
                    return byId.TryGetValue(id, out var p) && p.TimesResetAt != default && r.Date.ToUniversalTime() <= p.TimesResetAt;
                });
                if (removed > 0 || level.Best.Keys.Any(id => doc.DeletedProfiles.ContainsKey(id))) level.RebuildBest();
            }
        }

        private static Dictionary<string, MachineName> MergeMachineNames(Dictionary<string, MachineName> a, Dictionary<string, MachineName> b)
        {
            var result = new Dictionary<string, MachineName>();
            foreach (var kv in (a ?? new Dictionary<string, MachineName>()).Concat(b ?? new Dictionary<string, MachineName>()))
            {
                if (kv.Value == null || string.IsNullOrEmpty(kv.Value.Name)) continue;
                if (!result.TryGetValue(kv.Key, out var have) || kv.Value.Updated > have.Updated ||
                    (kv.Value.Updated == have.Updated && string.CompareOrdinal(kv.Value.Name, have.Name) < 0))
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        private static Dictionary<string, string> MergeJoined(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            var result = new Dictionary<string, string>();
            foreach (var kv in (a ?? new Dictionary<string, string>()).Concat(b ?? new Dictionary<string, string>()))
                if (!result.TryGetValue(kv.Key, out var have) || string.CompareOrdinal(kv.Value, have) < 0) result[kv.Key] = kv.Value;
            return result;
        }

        /// <summary>Follows the "joined into" chain to the profile id that's kept.</summary>
        public static string FinalId(Dictionary<string, string> joined, string id)
        {
            if (id == null || joined == null) return id;
            var seen = new HashSet<string>();
            while (joined.TryGetValue(id, out var next) && seen.Add(id)) id = next;
            return id;
        }

        /// <summary>
        /// The same Steam friend as two profiles (added on two PCs before they synced): keep the older profile
        /// and move the other one's stats and leaderboard times into it. Stats are kept apart per PC *and* per
        /// old profile (key "machine/oldId"), so joining again later (a PC that still had the old profile) never
        /// counts anything twice. Typed-in names are never joined: two people can have the same name.
        /// </summary>
        private static void JoinDuplicates(SyncDoc doc)
        {
            var joined = doc.JoinedProfiles;
            foreach (var group in doc.Profiles.Where(p => p.SteamId != 0 && !joined.ContainsKey(p.Id)).GroupBy(p => p.SteamId))
            {
                if (group.Count() < 2) continue;
                var keep = group.OrderBy(p => p.Created).ThenBy(p => p.Id, StringComparer.Ordinal).First();
                foreach (var p in group)
                    if (p != keep) joined[p.Id] = keep.Id;
            }
            if (joined.Count == 0) return;

            var byId = doc.Profiles.ToDictionary(p => p.Id);
            foreach (var old in doc.Profiles.Where(p => joined.ContainsKey(p.Id)).ToList())
            {
                string target = FinalId(joined, old.Id);
                if (target == old.Id || !byId.TryGetValue(target, out var keep)) continue;
                JoinInto(old, keep);
                doc.Profiles.Remove(old);
            }

            // Leaderboard times of joined profiles move over too.
            foreach (var level in doc.Leaderboard.Levels.Values)
            {
                bool moved = false;
                foreach (var run in level.Runs)
                {
                    string target = FinalId(joined, run.ProfileId);
                    if (target == run.ProfileId || !byId.ContainsKey(target)) continue;
                    if (level.Best.TryGetValue(run.ProfileId, out var oldBest))
                    {
                        if (level.Best.TryGetValue(target, out var keptBest)) keptBest.Finishes = Math.Max(keptBest.Finishes, oldBest.Finishes);
                        else { oldBest.ProfileId = target; level.Best[target] = oldBest; }
                        level.Best.Remove(run.ProfileId);
                    }
                    run.ProfileId = target;
                    moved = true;
                }
                if (moved) level.RebuildBest();
            }
        }

        private static void JoinInto(Profile old, Profile keep)
        {
            // Name, Steam link and deleted state: the newest change wins, as for any profile.
            var newer = old.Modified > keep.Modified ? old : keep;
            keep.Name = newer.Name;
            keep.IsArchived = newer.IsArchived;
            if (old.Modified > keep.Modified) keep.Modified = old.Modified;
            if (old.Created != default && (keep.Created == default || old.Created < keep.Created))
            {
                keep.Created = old.Created;
                keep.CreatedOn = old.CreatedOn;
            }
            if (keep.StatsByMachine == null) keep.StatsByMachine = new Dictionary<string, PlayerStats>();
            foreach (var kv in old.StatsByMachine ?? new Dictionary<string, PlayerStats>())
            {
                if (kv.Value == null || kv.Value.Updated <= old.StatsResetAt) continue;
                string key = kv.Key.Contains("/") ? kv.Key : kv.Key + "/" + old.Id;
                if (!keep.StatsByMachine.TryGetValue(key, out var have) || kv.Value.Updated > have.Updated) keep.StatsByMachine[key] = kv.Value;
            }
        }

        private static List<Profile> MergeProfiles(List<Profile> a, List<Profile> b, DateTime season)
        {
            // Stats from an older season don't survive.
            foreach (var p in a.Concat(b))
                if (p.StatsByMachine != null)
                    foreach (var key in p.StatsByMachine.Where(kv => kv.Value == null || kv.Value.Season < season).Select(kv => kv.Key).ToList())
                        p.StatsByMachine.Remove(key);

            var result = new List<Profile>();
            var bById = b.ToDictionary(p => p.Id);
            foreach (var id in a.Select(p => p.Id).Concat(b.Select(p => p.Id)).Distinct())
            {
                var pa = a.FirstOrDefault(p => p.Id == id);
                bById.TryGetValue(id, out var pb);
                if (pa == null || pb == null) { result.Add(pa ?? pb); continue; }

                // Name, Steam link and deleted state: newest change wins.
                var newer = pb.Modified > pa.Modified ? pb : pa;
                var merged = newer;
                merged.StatsResetAt = pa.StatsResetAt > pb.StatsResetAt ? pa.StatsResetAt : pb.StatsResetAt;
                merged.TimesResetAt = pa.TimesResetAt > pb.TimesResetAt ? pa.TimesResetAt : pb.TimesResetAt;
                // Created: the earliest known date (PCs may have guessed it for old profiles), and where.
                var first = pa.Created == default ? pb : pb.Created == default ? pa
                    : pb.Created < pa.Created ? pb : pa.Created < pb.Created ? pa
                    : string.CompareOrdinal(pb.CreatedOn, pa.CreatedOn) < 0 ? pb : pa;
                merged.Created = first.Created;
                merged.CreatedOn = first.CreatedOn ?? (first == pa ? pb : pa).CreatedOn;

                // Stats: per machine, the newest entry wins; entries from before a reset are dropped.
                var stats = new Dictionary<string, PlayerStats>();
                foreach (var kv in (pa.StatsByMachine ?? new Dictionary<string, PlayerStats>())
                         .Concat(pb.StatsByMachine ?? new Dictionary<string, PlayerStats>()))
                {
                    if (kv.Value == null || kv.Value.Updated <= merged.StatsResetAt) continue;
                    if (!stats.TryGetValue(kv.Key, out var have) || kv.Value.Updated > have.Updated) stats[kv.Key] = kv.Value;
                }
                merged.StatsByMachine = stats;
                result.Add(merged);
            }
            return result;
        }

        private static LeaderboardData MergeLeaderboards(LeaderboardData a, LeaderboardData b, DateTime season)
        {
            var result = new LeaderboardData
            {
                DeletedRunIds = new HashSet<string>((a.DeletedRunIds ?? new HashSet<string>()).Concat(b.DeletedRunIds ?? new HashSet<string>())),
                ClearedLevels = new Dictionary<string, DateTime>(),
            };
            foreach (var kv in (a.ClearedLevels ?? new Dictionary<string, DateTime>()).Concat(b.ClearedLevels ?? new Dictionary<string, DateTime>()))
                if (kv.Value >= season && (!result.ClearedLevels.TryGetValue(kv.Key, out var t) || kv.Value > t)) result.ClearedLevels[kv.Key] = kv.Value;

            var keys = a.Levels.Keys.Concat(b.Levels.Keys).Distinct();
            foreach (var key in keys)
            {
                a.Levels.TryGetValue(key, out var la);
                b.Levels.TryGetValue(key, out var lb);
                var newer = la == null ? lb : lb == null ? la : lb.LastPlayed > la.LastPlayed ? lb : la;
                var level = new LevelRecords
                {
                    LevelName = newer.LevelName,
                    Author = newer.Author,
                    LastPlayed = new[] { la?.LastPlayed ?? default, lb?.LastPlayed ?? default }.Max(),
                };

                // Cleared by hand and not played since: stays gone.
                if (result.ClearedLevels.TryGetValue(key, out var cleared) && level.LastPlayed.ToUniversalTime() <= cleared) continue;

                // Races per machine (this season only): each machine only counts up, so the higher number is the newest.
                level.Season = season;
                foreach (var side in new[] { la, lb })
                {
                    if (side?.RacesByMachine == null || side.Season < season) continue;
                    foreach (var kv in side.RacesByMachine)
                        if (!level.RacesByMachine.TryGetValue(kv.Key, out var n) || kv.Value > n) level.RacesByMachine[kv.Key] = kv.Value;
                }

                // Runs: everything from both sides (this season), once, minus removed ones; keep the fastest MaxRuns.
                level.Runs = (la?.Runs ?? new List<RunRecord>()).Concat(lb?.Runs ?? new List<RunRecord>())
                    .Where(r => r != null && !result.DeletedRunIds.Contains(r.Id) && r.Date.ToUniversalTime() >= season)
                    .GroupBy(r => r.Id).Select(g => g.First())
                    .OrderBy(r => r.Time).Take(LevelRecords.MaxRuns).ToList();
                level.Best = (la ?? lb).Best ?? new Dictionary<string, BestTime>();
                level.RebuildBest();
                // Nothing left of a map from before the wipe: drop it.
                if (level.Runs.Count == 0 && level.RacesPlayed == 0 && level.LastPlayed.ToUniversalTime() < season) continue;
                result.Levels[key] = level;
            }
            return result;
        }
    }
}
