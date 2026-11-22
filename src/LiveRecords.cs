using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CouchParty
{
    /// <summary>
    /// Announces a new map record the moment a player crosses the finish, instead of waiting for the
    /// results screen. Anything it misses is announced when the race is saved.
    /// </summary>
    internal static class LiveRecords
    {
        private static float _recordToBeat = float.MaxValue;
        private static string _holder;
        private static readonly HashSet<int> AnnouncedSlots = new HashSet<int>();

        /// <summary>A new round: remember the record everyone is chasing.</summary>
        public static void OnRoundStarted()
        {
            AnnouncedSlots.Clear();
            var best = Game.CurrentRecords(null)?.Ranked().FirstOrDefault();
            _recordToBeat = best?.Time ?? float.MaxValue;
            _holder = best != null ? Store.DisplayName(best) : null;
        }

        public static bool WasAnnounced(int slot) => AnnouncedSlots.Contains(slot);

        /// <summary>ZeepSDK says someone crossed the finish; find out who once the game has stored the result.</summary>
        public static IEnumerator CheckFinish(float time)
        {
            yield return new WaitForSecondsRealtime(0.3f);
            try { Check(time); }
            catch (Exception e) { Plugin.Log.LogWarning($"Live record check failed: {e.Message}"); }
        }

        private static void Check(float time)
        {
            if (!Game.IsLocalMultiplayer() || _recordToBeat >= float.MaxValue || time >= _recordToBeat) return;

            var gm = UnityEngine.Object.FindObjectOfType<GameMaster>();
            var results = Game.ReadResults(gm);
            var match = results.FirstOrDefault(r => Game.IsValidRaceTime(r.Time) && Math.Abs(r.Time - time) < 0.01f);
            if (!Game.IsValidRaceTime(match.Time))
            {
                Plugin.Debug($"Finish at {time:0.000}: no matching result yet; will check when the race is saved.");
                return;
            }

            int total = Access.Get(gm, "racePoints", 0);
            if (total > 0 && match.Checkpoints >= 0 && match.Checkpoints < total) return; // shortcut, doesn't count

            int slot = match.PlayerNum - Game.NumberBase(results);
            var profile = Store.GetSlotProfile(slot);
            if (profile == null || AnnouncedSlots.Contains(slot)) return;

            AnnouncedSlots.Add(slot);
            CouchUI.Instance?.ShowRecord(profile.Name, time, _holder, _recordToBeat);
            Plugin.Log.LogInfo($"New map record by {profile.Name}: {Game.FormatTime(time)} (was {_holder} {Game.FormatTime(_recordToBeat)})");

            // A second player can still beat this one in the same race.
            _holder = profile.Name;
            _recordToBeat = time;
        }

        /// <summary>After the race is saved: announce any record the live check didn't catch.</summary>
        public static void AnnounceMissed(RaceSummary summary)
        {
            var line = summary?.Lines.FirstOrDefault(l => l.NewMapRecord);
            if (line == null || AnnouncedSlots.Contains(line.Slot)) return;
            AnnouncedSlots.Add(line.Slot);
            CouchUI.Instance?.ShowRecord(line.Name, line.Time, summary.PreviousRecordName, summary.PreviousRecordTime);
        }
    }
}
