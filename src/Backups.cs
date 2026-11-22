using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CouchParty
{
    /// <summary>
    /// Lightweight backups of profiles.json and leaderboard.json.
    ///  - At start-up, before anything is read or saved, the files are copied into backups\&lt;date&gt;\,
    ///    but only if they changed since the newest backup (so idle months or many restarts don't push old ones out).
    ///  - The newest N backups are kept (by count, never by age). Backups made before a mod update are kept forever.
    ///  - Optionally every backup is also copied to an extra folder (e.g. OneDrive).
    /// </summary>
    internal static class Backups
    {
        public class Entry
        {
            public string Folder;      // full path
            public DateTime Created;
            public string Reason;
            public string ModVersion;
            public bool Keep;          // never pruned
            public int Maps, Runs, Profiles;
        }

        private const string InfoFile = "backup-info.json";
        private const string VersionFile = "last-version.txt";

        public static string BackupDir => Path.Combine(Store.DataDir, "backups");

        // ---------------- Start-up ----------------

        public static void OnStartup()
        {
            if (!Store.DataFiles.Any(f => File.Exists(Store.FilePath(f)))) return; // nothing saved yet

            string versionPath = Path.Combine(Store.DataDir, VersionFile);
            string lastVersion = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : null;

            if (lastVersion != Plugin.Version)
            {
                // First run of a new mod version (or of the backup feature): keep this copy forever.
                Create(lastVersion == null ? "first backup" : $"before update to {Plugin.Version}", keep: true, force: true);
                File.WriteAllText(versionPath, Plugin.Version);
            }
            else Create("start-up", keep: false, force: false);

            Prune(BackupDir);
        }

        // ---------------- Create / list / prune ----------------

        /// <summary>Copies the data files into a new backup folder. Skipped when nothing changed (unless forced).</summary>
        public static Entry Create(string reason, bool keep, bool force)
        {
            var newest = List().FirstOrDefault();
            if (!force && newest != null && SameAsBackup(newest)) return null;

            Directory.CreateDirectory(BackupDir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string folder = Path.Combine(BackupDir, $"{stamp}_{Safe(reason)}");
            for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(BackupDir, $"{stamp}_{Safe(reason)}_{n}");
            Directory.CreateDirectory(folder);

            foreach (var f in Store.DataFiles)
            {
                string src = Store.FilePath(f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(folder, f + ".json"));
            }

            var entry = new Entry { Folder = folder, Created = DateTime.Now, Reason = reason, ModVersion = Plugin.Version, Keep = keep };
            Count(folder, entry);
            File.WriteAllText(Path.Combine(folder, InfoFile), JsonConvert.SerializeObject(new
            {
                entry.Created, entry.Reason, entry.ModVersion, entry.Keep, entry.Maps, entry.Runs, entry.Profiles,
            }, Formatting.Indented));
            Plugin.Log.LogInfo($"Backup created: {Path.GetFileName(folder)} ({entry.Maps} maps, {entry.Runs} runs, {entry.Profiles} profiles)");

            CopyToExtraFolder(folder);
            return entry;
        }

        /// <summary>All backups, newest first.</summary>
        public static List<Entry> List() => List(BackupDir);

        private static List<Entry> List(string dir)
        {
            var list = new List<Entry>();
            if (!Directory.Exists(dir)) return list;
            foreach (var folder in Directory.GetDirectories(dir))
            {
                var e = new Entry { Folder = folder, Created = Directory.GetCreationTime(folder), Reason = "?" };
                try
                {
                    var info = JObject.Parse(File.ReadAllText(Path.Combine(folder, InfoFile)));
                    e.Created = info.Value<DateTime?>("Created") ?? e.Created;
                    e.Reason = info.Value<string>("Reason") ?? "?";
                    e.ModVersion = info.Value<string>("ModVersion");
                    e.Keep = info.Value<bool?>("Keep") ?? false;
                    e.Maps = info.Value<int?>("Maps") ?? 0;
                    e.Runs = info.Value<int?>("Runs") ?? 0;
                    e.Profiles = info.Value<int?>("Profiles") ?? 0;
                }
                catch { Count(folder, e); }
                list.Add(e);
            }
            return list.OrderByDescending(e => e.Created).ToList();
        }

        /// <summary>Keeps the newest N regular backups; "keep" backups are never deleted.</summary>
        private static void Prune(string dir)
        {
            int max = Mathf.Max(1, Plugin.MaxBackups.Value);
            foreach (var old in List(dir).Where(e => !e.Keep).Skip(max))
            {
                try
                {
                    Directory.Delete(old.Folder, true);
                    Plugin.Debug($"Removed old backup {Path.GetFileName(old.Folder)}");
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"Could not remove old backup {old.Folder}: {ex.Message}"); }
            }
        }

        // ---------------- Restore ----------------

        /// <summary>Puts a backup's files back. The current files are backed up first, so a restore can be undone.</summary>
        public static void Restore(Entry entry)
        {
            Create("before restore", keep: false, force: true);
            foreach (var f in Store.DataFiles)
            {
                string src = Path.Combine(entry.Folder, f + ".json");
                if (File.Exists(src)) File.Copy(src, Store.FilePath(f), true);
            }
            Store.ReadAll();
            Game.ApplyNames();
            Prune(BackupDir);
            Plugin.Log.LogInfo($"Restored backup {Path.GetFileName(entry.Folder)}");
        }

        public static void Delete(Entry entry)
        {
            try { Directory.Delete(entry.Folder, true); }
            catch (Exception ex) { Plugin.Log.LogWarning($"Could not delete backup: {ex.Message}"); }
        }

        public static void BackupNow()
        {
            Create("manual", keep: false, force: true);
            Prune(BackupDir);
        }

        public static void OpenFolder()
        {
            Directory.CreateDirectory(BackupDir);
            Application.OpenURL("file:///" + BackupDir.Replace('\\', '/'));
        }

        // ---------------- Helpers ----------------

        private static void CopyToExtraFolder(string folder)
        {
            string extra = Plugin.ExtraBackupFolder.Value?.Trim();
            if (string.IsNullOrEmpty(extra)) return;
            try
            {
                string target = Path.Combine(extra, Path.GetFileName(folder));
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(folder))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
                Prune(extra);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"Could not copy backup to extra folder '{extra}': {ex.Message}"); }
        }

        private static bool SameAsBackup(Entry backup)
        {
            foreach (var f in Store.DataFiles)
            {
                string current = Store.FilePath(f), saved = Path.Combine(backup.Folder, f + ".json");
                bool a = File.Exists(current), b = File.Exists(saved);
                if (a != b) return false;
                if (a && !File.ReadAllBytes(current).SequenceEqual(File.ReadAllBytes(saved))) return false;
            }
            return true;
        }

        /// <summary>Fills in map / run / profile counts by peeking into the backup's files.</summary>
        private static void Count(string folder, Entry e)
        {
            try
            {
                string lb = Path.Combine(folder, "leaderboard.json");
                if (File.Exists(lb) && JObject.Parse(File.ReadAllText(lb))["Levels"] is JObject levels)
                {
                    e.Maps = levels.Count;
                    e.Runs = levels.Properties().Sum(p => (p.Value["Runs"] as JArray)?.Count ?? (p.Value["Best"] as JObject)?.Count ?? 0);
                }
                string pr = Path.Combine(folder, "profiles.json");
                if (File.Exists(pr) && JObject.Parse(File.ReadAllText(pr))["Profiles"] is JArray profiles)
                    e.Profiles = profiles.Count;
            }
            catch { /* counts are only informational */ }
        }

        private static string Safe(string text) =>
            new string(text.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '-').ToArray());
    }
}
