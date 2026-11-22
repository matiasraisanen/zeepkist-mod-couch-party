using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using ZeepSDK.Racing;
using ZeepSDK.Storage;
using ZeepSDK.UI;

namespace CouchParty
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("ZeepSDK")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.skofdo.couchparty";
        public const string Name = "Couch Party";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyCode> ToggleWindowKey;
        internal static ConfigEntry<KeyCode> ShowRecordsKey;
        internal static ConfigEntry<bool> ShowNamesOnPlayerSelect;
        internal static ConfigEntry<bool> ShowPreRacePopup;
        internal static ConfigEntry<float> PreRacePopupSeconds;
        internal static ConfigEntry<int> PreRaceTopCount;
        internal static ConfigEntry<int> ResultsTopCount;
        internal static ConfigEntry<float> UiScale;
        internal static ConfigEntry<int> PlayerNumberBase;
        internal static ConfigEntry<bool> DebugLogging;
        internal static ConfigEntry<int> MaxBackups;
        internal static ConfigEntry<string> ExtraBackupFolder;
        internal static ConfigEntry<bool> SyncEnabled;
        internal static ConfigEntry<string> SyncUrl;
        internal static ConfigEntry<string> SyncKey;

        /// <summary>Set once results for the current round are saved; cleared when a new round starts.</summary>
        internal static bool RecordedThisRound;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{Name} {Version} starting...");

            ToggleWindowKey = Config.Bind("Keys", "Toggle window", KeyCode.F8, "Opens the Couch Party window (profiles and leaderboard).");
            ShowRecordsKey = Config.Bind("Keys", "Show map records", KeyCode.F9, "Shows the top times for the current map.");
            ShowNamesOnPlayerSelect = Config.Bind("General", "Names on player select", true, "Show profile names instead of 'Player N' on the player-select screen.");
            ShowPreRacePopup = Config.Bind("Leaderboard", "Race start popup", true, "Show the map's top times for a few seconds when a local multiplayer race starts.");
            PreRacePopupSeconds = Config.Bind("Leaderboard", "Popup seconds", 5f, "How long the top-times and new-record popups stay on screen.");
            PreRaceTopCount = Config.Bind("Leaderboard", "Pre-race popup entries", 3,
                new ConfigDescription("How many top times the pre-race popup shows.", new AcceptableValueRange<int>(1, 20)));
            ResultsTopCount = Config.Bind("Leaderboard", "Results screen entries", 10,
                new ConfigDescription("How many top times the results screen panel shows.", new AcceptableValueRange<int>(1, 20)));
            UiScale = Config.Bind("General", "UI scale", 1f, "Extra size multiplier for Couch Party windows.");
            PlayerNumberBase = Config.Bind("Advanced", "Player number base", -1,
                new ConfigDescription("Whether the game's result player numbers start at 0 or 1. -1 = detect automatically.", new AcceptableValueRange<int>(-1, 1)));
            DebugLogging = Config.Bind("Advanced", "Debug logging", true, "Write extra details to the BepInEx log (useful while testing).");
            MaxBackups = Config.Bind("Backups", "Backups to keep", 30,
                new ConfigDescription("How many backups to keep (by count, not age). Backups made before a mod update are always kept.", new AcceptableValueRange<int>(5, 500)));
            SyncEnabled = Config.Bind("Sync", "Enabled", true, "Sync profiles, stats and leaderboards with your Couch Party server (needs an address and a key).");
            SyncUrl = Config.Bind("Sync", "Server address", "", "Address of your Couch Party server, e.g. https://your-domain.example/couchparty. Set it in the game: F8 > Sync.");
            SyncKey = Config.Bind("Sync", "Key", "", "This PC's key for the server. Set it in the game: F8 > Sync.");
            ExtraBackupFolder = Config.Bind("Backups", "Extra backup folder", "",
                "Optional second folder that gets a copy of every backup, e.g. C:\\Users\\you\\OneDrive\\CouchParty. Empty = off.");

            // Each step runs on its own so one failure can't stop the rest (especially the UI) from starting.
            Step("load saved data", () => Store.Load(StorageApi.CreateModStorage(this)));

            Step("create UI", () =>
            {
                // Own object that survives scene changes, independent of BepInEx's manager object.
                var host = new GameObject("CouchParty");
                DontDestroyOnLoad(host);
                host.hideFlags = HideFlags.HideAndDontSave;
                host.AddComponent<CouchUI>();
            });

            Step("patch game", () =>
            {
                _harmony = new Harmony(Guid);
                PatchEach(
                    typeof(Patch_CreateCharacter_PublishResults),
                    typeof(Patch_CreateCharacter_Update),
                    typeof(Patch_CreateCharacter_OnEnable),
                    typeof(Patch_PlayerManager_UpdatePlayerNames),
                    typeof(Patch_GameMaster_SpawnPlayers),
                    typeof(Patch_ReadyToReset_TimeOut),
                    typeof(Patch_ReadyToReset_ExternalKill),
                    typeof(Patch_ReadyToReset_DeadResults),
                    typeof(Patch_DamageCharacter_Kill),
                    typeof(Patch_RaceResultsUI_OnOpen),
                    typeof(Patch_GameMaster_OpenResultScreen),
                    typeof(Patch_GameMaster_GetResults2),
                    typeof(Patch_RaceResultsUI_OnClose));
            });

            Step("subscribe to race events", () =>
            {
                RacingApi.LevelLoaded += OnLevelLoaded;
                RacingApi.RoundStarted += OnRoundStarted;
                RacingApi.PlayerSpawned += OnPlayerSpawned;
                RacingApi.CrossedFinishLine += OnCrossedFinishLine;
                RacingApi.Quit += OnQuit;
            });

            Step("add toolbar menu", () => UIApi.AddToolbarDrawer(new Toolbar()));

            Log.LogInfo($"{Name} {Version} loaded. {Store.Profiles.Profiles.Count} profiles, {Store.Leaderboard.Levels.Count} maps with records.");
        }

        private static void Step(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Log.LogError($"Startup step '{what}' failed: {e}"); }
        }

        private void PatchEach(params Type[] patchClasses)
        {
            foreach (var type in patchClasses)
            {
                try { _harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception e) { Log.LogError($"Patch {type.Name} failed, that feature won't work: {e.Message}"); }
            }
        }

        private void OnLevelLoaded()
        {
            RecordedThisRound = false;
            Debug($"Level loaded: '{Game.CurrentLevelName(null)}' players={Game.ActivePlayerCount()} online={Game.IsOnline}");
            if (!Game.IsLocalMultiplayer()) return;
            try { Outfits.AutosaveFromSlots(); }
            catch (Exception e) { Log.LogError($"Saving outfits failed: {e}"); }

        }

        private void OnRoundStarted()
        {
            RecordedThisRound = false;
            RoundStartTime = UnityEngine.Time.unscaledTime;
            Debug("Round started.");
            StatsTracker.OnRoundStarted();
            if (!Game.IsLocalMultiplayer()) return;
            LiveRecords.OnRoundStarted();
            if (ShowPreRacePopup.Value) CouchUI.Instance?.ShowPreRace();
        }

        private void OnQuit()
        {
            RoundStartTime = -1f;
            StatsTracker.OnQuit();
        }

        private void OnCrossedFinishLine(float time)
        {
            if (Game.IsLocalMultiplayer() && CouchUI.Instance != null)
                CouchUI.Instance.StartCoroutine(LiveRecords.CheckFinish(time));
        }

        private void OnPlayerSpawned() => Game.ApplyNames();

        private void OnDestroy()
        {
            RacingApi.LevelLoaded -= OnLevelLoaded;
            RacingApi.RoundStarted -= OnRoundStarted;
            RacingApi.PlayerSpawned -= OnPlayerSpawned;
            RacingApi.CrossedFinishLine -= OnCrossedFinishLine;
            RacingApi.Quit -= OnQuit;
            _harmony?.UnpatchSelf();
        }

        /// <summary>When the current round started (unscaled time), for race times in the log. Negative = no round.</summary>
        internal static float RoundStartTime = -1f;

        internal static void Debug(string message)
        {
            if (DebugLogging == null || !DebugLogging.Value) return;
            string race = RoundStartTime >= 0 ? $"race {UnityEngine.Time.unscaledTime - RoundStartTime:0.00} s" : "no race";
            Log.LogInfo($"[debug {DateTime.Now:HH:mm:ss.fff} | {race}] {message}");
        }
    }
}
