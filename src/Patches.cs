using System;
using HarmonyLib;

namespace CouchParty
{
    // Each patch class is applied separately, so if a game update renames one method only that feature breaks.

    internal static class PlayerSelectPanel
    {
        /// <summary>0-based slot of a player-select panel (its position in the selector's list).</summary>
        public static int SlotOf(object character, object selector = null)
        {
            if (selector == null) selector = Access.Get<object>(character, "playerSelector");
            var list = Access.List(selector, "Players");
            int index = list?.IndexOf(character) ?? -1;
            return index >= 0 ? index : Access.Get(character, "playerNum", -1);
        }
    }

    /// <summary>Player-select screen hands each player's choices to the game: put our names in.</summary>
    [HarmonyPatch(typeof(CreateCharacter), nameof(CreateCharacter.PublishResults))]
    internal static class Patch_CreateCharacter_PublishResults
    {
        private static void Postfix(object __instance)
        {
            Game.ApplyNames();
            Outfits.LogSelection(__instance, PlayerSelectPanel.SlotOf(__instance));
        }
    }

    /// <summary>A player panel (re)appears: allow its outfit to be preselected again.</summary>
    [HarmonyPatch(typeof(CreateCharacter), nameof(CreateCharacter.OnEnable))]
    internal static class Patch_CreateCharacter_OnEnable
    {
        private static void Postfix(object __instance) => Outfits.ResetPanel(__instance);
    }

    /// <summary>Every frame on the player-select screen: show profile names and preselect saved outfits.</summary>
    [HarmonyPatch(typeof(CreateCharacter), nameof(CreateCharacter.Update))]
    internal static class Patch_CreateCharacter_Update
    {
        private static void Postfix(object __instance)
        {
            var selector = Access.Get<object>(__instance, "playerSelector");
            int slot = PlayerSelectPanel.SlotOf(__instance, selector);
            var profile = Store.GetSlotProfile(slot);
            var activeFlags = Access.Get<bool[]>(selector, "PlayersActive");
            bool active = activeFlags != null ? slot >= 0 && slot < activeFlags.Length && activeFlags[slot] : Game.IsSlotActive(slot);

            Outfits.Tick(__instance, slot, active, profile);

            if (!Plugin.ShowNamesOnPlayerSelect.Value || profile == null || !active) return;
            var text = Access.Get<TMPro.TMP_Text>(__instance, "playerText");
            if (text != null && text.text != profile.Name) text.text = profile.Name;
        }
    }

    /// <summary>The game (re)builds its name list: put our names back on top.</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.UpdatePlayerNames))]
    internal static class Patch_PlayerManager_UpdatePlayerNames
    {
        private static void Postfix() => Game.ApplyNames();
    }

    /// <summary>Right before the cars spawn, make sure the names are ours.</summary>
    [HarmonyPatch(typeof(GameMaster), nameof(GameMaster.SpawnPlayers))]
    internal static class Patch_GameMaster_SpawnPlayers
    {
        private static void Prefix() => Game.ApplyNames();
    }

    /// <summary>
    /// Saves a finished race once. Called from several hooks because it isn't certain which of them
    /// local multiplayer goes through; the first one that sees results wins.
    /// </summary>
    internal static class Recorder
    {
        public static void TryRecord(GameMaster gm, string source)
        {
            try
            {
                if (Plugin.RecordedThisRound)
                {
                    Plugin.Debug($"Race end via {source}: already recorded this round.");
                    return;
                }

                if (gm == null) gm = Access.Get<GameMaster>(PlayerManager.Instance, "currentMaster");
                if (gm == null) gm = UnityEngine.Object.FindObjectOfType<GameMaster>();
                if (gm == null)
                {
                    Plugin.Log.LogWarning($"Race end via {source}: no GameMaster found, can't read results.");
                    return;
                }

                int count = Game.ResultCount(gm);
                Plugin.Log.LogInfo($"Race end detected via {source} ({count} results).");
                if (count == 0) return; // maybe too early; a later hook can try again

                Plugin.RecordedThisRound = true;
                Plugin.RoundStartTime = -1f; // round over: debug lines say "no race" again
                var summary = Game.RecordRace(gm);
                StatsTracker.CommitRace(summary);
                if (summary != null)
                {
                    CouchUI.Instance?.ShowResults(summary);
                    LiveRecords.AnnounceMissed(summary);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Recording race results failed: {e}");
            }
        }
    }

    /// <summary>Results screen opens after a race: save the times and show our leaderboard.</summary>
    [HarmonyPatch(typeof(RaceResultsUI), nameof(RaceResultsUI.OnOpen))]
    internal static class Patch_RaceResultsUI_OnOpen
    {
        private static void Postfix(object __instance) =>
            Recorder.TryRecord(Access.Get<GameMaster>(__instance, "GameMaster"), "RaceResultsUI.OnOpen");
    }

    [HarmonyPatch(typeof(GameMaster), nameof(GameMaster.OpenResultScreen))]
    internal static class Patch_GameMaster_OpenResultScreen
    {
        private static void Postfix(GameMaster __instance, string caller) =>
            Recorder.TryRecord(__instance, $"GameMaster.OpenResultScreen({caller})");
    }

    [HarmonyPatch(typeof(GameMaster), nameof(GameMaster.GetResults2))]
    internal static class Patch_GameMaster_GetResults2
    {
        private static void Postfix(GameMaster __instance) => Recorder.TryRecord(__instance, "GameMaster.GetResults2");
    }

    [HarmonyPatch(typeof(RaceResultsUI), nameof(RaceResultsUI.OnClose))]
    internal static class Patch_RaceResultsUI_OnClose
    {
        private static void Postfix() => CouchUI.Instance?.HideResults();
    }
}
