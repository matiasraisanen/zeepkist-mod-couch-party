using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CouchParty
{
    /// <summary>All Couch Party on-screen UI, drawn with Unity's immediate-mode GUI.</summary>
    public class CouchUI : MonoBehaviour
    {
        public enum Tab { Players, Profiles, Leaderboard, Stats, Fame, Tonight, Backups, Sync }

        public static CouchUI Instance { get; private set; }

        private const float BaseHeight = 1080f;
        private const int WindowId = 0x0C0C;

        // Main window
        private bool _open;
        private Tab _tab = Tab.Players;
        private Rect _windowRect = new Rect(60, 60, 1150, 900);

        /// <summary>Wide enough for three columns of stats / Hall of Fame cards.</summary>
        private bool Wide => _windowRect.width >= 1050;
        private Vector2 _scroll;

        // Players tab
        private int _pickingForSlot = -1;

        // Profiles tab
        private string _newName = "";
        private bool _friendPicker;
        private string _friendSearch = "";
        private List<FriendEntry> _friends;
        private string _friendsError;
        private string _confirmDeleteId;
        private string _renamingId;
        private string _renameText;
        private bool _focusRename;

        // Leaderboard tab
        private string _expandedLevel;
        private string _confirmClearLevel;
        private string _confirmRemove;
        private bool _confirmResetStats;
        private List<Backups.Entry> _backupList;
        private string _confirmRestore;
        private string _flash;
        private float _flashUntil;
        private string _statsProfileId;
        private bool _lbAllRuns;        // Leaderboard tab: all runs instead of best per player
        private bool _statsTonight;     // Stats tab: tonight's stats instead of all time
        private bool _resultsAllRuns;   // results screen: same

        // Overlays
        private RaceSummary _results;
        private float _popupUntil;
        private string _popupLevelName;
        private List<BestTime> _popupRanked = new List<BestTime>();

        // "New map record!" popup
        private float _recordUntil;
        private string _recordName, _recordPrevName;
        private float _recordTime, _recordPrevTime;

        // Blocks the game's input while the mouse is over our window or we're typing (via ZeepSDK).
        private IDisposable _inputBlock;
        private bool _blockInput;
        private const string SearchControl = "cp_search";

        // Styles
        private bool _stylesReady;
        private GUIStyle _title, _header, _label, _small, _row, _gold, _green, _dim, _panel, _button, _tabOn, _tabOff, _field;
        private Texture2D _panelTex;

        // "Loaded" hint shown in the corner for a while after start-up.
        private float _hintUntil = -1f;
        private bool _announced;

        private void Awake()
        {
            Instance = this;
            Plugin.Log.LogInfo("Couch Party UI ready.");
        }

        private void Start()
        {
            SceneManager.activeSceneChanged += OnSceneChanged;
            _sceneName = SceneManager.GetActiveScene().name;
            try { _inputBlock = ZeepSDK.Controls.ControlsApi.DisableAllInput(() => _blockInput); }
            catch (Exception e) { Plugin.Log.LogWarning($"Input blocking not available (clicks may reach the game): {e.Message}"); }
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnSceneChanged;
            _inputBlock?.Dispose();
        }

        private static string _sceneName;

        private static void OnSceneChanged(Scene from, Scene to)
        {
            _sceneName = to.name;
            if (_sceneName != "GameScene") Plugin.RoundStartTime = -1f;
            Plugin.Debug($"Scene: '{_sceneName}'");
        }

        /// <summary>Popups only make sense while actually racing, not in menus or the character creator.</summary>
        private static bool InGameScene => _sceneName == null || _sceneName == "GameScene";

        private bool PopupVisible => Time.unscaledTime < _popupUntil;

        // ---------------- Public API ----------------

        public void Open(Tab tab)
        {
            _tab = tab;
            _open = true;
        }

        public void ShowResults(RaceSummary summary)
        {
            _results = summary;
            _resultsAllRuns = false;
            _popupUntil = 0; // results replace the top-times popup
        }

        public void ShowRecord(string name, float time, string previousName, float previousTime)
        {
            _recordName = name;
            _recordTime = time;
            _recordPrevName = previousName;
            _recordPrevTime = previousTime;
            _recordUntil = Time.unscaledTime + Mathf.Max(2f, Plugin.PreRacePopupSeconds.Value);
        }

        public void HideResults() => _results = null;

        public void ShowPreRace()
        {
            RefreshPopupData();
            _popupUntil = Time.unscaledTime + Mathf.Max(1f, Plugin.PreRacePopupSeconds.Value);
        }

        // ---------------- Unity ----------------

        private void Update()
        {
            // First frame the game's player manager exists (main menu): announce ourselves.
            if (!_announced && PlayerManager.Instance != null)
            {
                _announced = true;
                _hintUntil = Time.unscaledTime + 15f;
                try
                {
                    if (Store.LoadWarning != null) ZeepSDK.Messaging.MessengerApi.LogWarning("Couch Party: " + Store.LoadWarning, 10f);
                    else ZeepSDK.Messaging.MessengerApi.LogSuccess($"Couch Party loaded. Press {Plugin.ToggleWindowKey.Value}", 5f);
                }
                catch (Exception e) { Plugin.Debug("Messenger not available: " + e.Message); }
                Sync.SyncNow(this); // pull what other PCs added (does nothing if sync isn't set up)
            }

            if (Input.GetKeyDown(Plugin.ToggleWindowKey.Value))
            {
                _open = !_open;
                _pickingForSlot = -1;
                _friendPicker = false;
            }

            if (InGameScene) StatsTracker.Tick();
            Sync.Tick(this);

            // F9 switches views: on the Stats tab all time / tonight, on the results screen best per player / all runs.
            if (Input.GetKeyDown(Plugin.SwitchViewKey.Value))
            {
                if (_open && _tab == Tab.Stats) _statsTonight = !_statsTonight;
                else if (_results != null && InGameScene) _resultsAllRuns = !_resultsAllRuns;
            }
        }

        private void LateUpdate()
        {
            // The game hides the mouse while racing; we need it while our window is open.
            if (_open)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        private void OnGUI()
        {
            EnsureStyles();
            float scale = Screen.height / BaseHeight * Mathf.Clamp(Plugin.UiScale.Value, 0.5f, 3f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            float vw = Screen.width / scale, vh = Screen.height / scale;

            if (Time.unscaledTime < _hintUntil && !_open)
                GUI.Label(new Rect(16, vh - 44, 600, 36), $"Couch Party {Plugin.Version}: press {Plugin.ToggleWindowKey.Value}", _gold);

            bool inGame = InGameScene;
            if (!inGame) { _popupUntil = 0; _recordUntil = 0; _results = null; }

            if (_results != null)
            {
                DrawResults(vw, vh);
                try { DrawSidePanel(vw, vh); }
                catch (Exception e) { Plugin.Debug("Side panel failed: " + e.Message); }
            }
            if (Time.unscaledTime < _recordUntil) DrawRecord(vw);
            else if (_results == null && PopupVisible) DrawPreRace(vw);

            if (_open)
            {
                _windowRect.width = Mathf.Min(_windowRect.width, vw - 20);
                _windowRect.height = Mathf.Min(_windowRect.height, vh - 20);
                DrawOutline(_windowRect, 4f);
                _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, "", _panel);
                _windowRect.x = Mathf.Clamp(_windowRect.x, 0, vw - _windowRect.width);
                _windowRect.y = Mathf.Clamp(_windowRect.y, 0, vh - _windowRect.height);
            }

            // Keep clicks and key presses from reaching the game while the mouse is over our window or a text box has focus.
            if (Event.current.type == EventType.Repaint)
            {
                var mouse = new Vector2(Input.mousePosition.x / scale, (Screen.height - Input.mousePosition.y) / scale);
                string focused = GUI.GetNameOfFocusedControl();
                bool typing = focused == NewNameControl || focused == RenameControl || focused == SearchControl ||
                              focused == SyncUrlControl || focused == SyncKeyControl || focused == WipeControl;
                _blockInput = _open && (_windowRect.Contains(mouse) || typing);
            }
        }

        // ---------------- Main window ----------------

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Couch Party", _title);
            GUILayout.FlexibleSpace();
            string syncLabel = Sync.Busy ? "Sync..." : Sync.LastError != null ? "Sync (!)" : "Sync";
            if (GUILayout.Button(syncLabel, _tab == Tab.Sync ? _tabOn : _button, GUILayout.Width(120))) OpenSyncTab();
            if (GUILayout.Button("Backups", _tab == Tab.Backups ? _tabOn : _button, GUILayout.Width(140)))
            {
                _tab = Tab.Backups;
                _backupList = null;
                _scroll = Vector2.zero;
            }
            if (GUILayout.Button("Close", _button, GUILayout.Width(110))) _open = false;
            GUILayout.EndHorizontal();
            if (Store.LoadWarning != null) GUILayout.Label(Store.LoadWarning, _gold);

            GUILayout.BeginHorizontal();
            TabButton("Players", Tab.Players);
            TabButton("Profiles", Tab.Profiles);
            TabButton("Leaderboard", Tab.Leaderboard);
            TabButton("Stats", Tab.Stats);
            TabButton("Hall of Fame", Tab.Fame);
            TabButton("Tonight", Tab.Tonight);
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            _scroll = GUILayout.BeginScrollView(_scroll);
            switch (_tab)
            {
                case Tab.Players: DrawPlayersTab(); break;
                case Tab.Profiles: DrawProfilesTab(); break;
                case Tab.Leaderboard: DrawLeaderboardTab(); break;
                case Tab.Stats: DrawStatsTab(); break;
                case Tab.Fame: DrawFameTab(); break;
                case Tab.Tonight: DrawTonightTab(); break;
                case Tab.Backups: DrawBackupsTab(); break;
                case Tab.Sync: DrawSyncTab(); break;
            }
            GUILayout.EndScrollView();

            GUILayout.Label(_tab == Tab.Stats
                ? $"{Plugin.ToggleWindowKey.Value}: open/close this window   {Plugin.SwitchViewKey.Value}: switch all time / tonight"
                : $"{Plugin.ToggleWindowKey.Value}: open/close this window", _small);
            GUI.DragWindow(new Rect(0, 0, 10000, 50));
        }

        private void TabButton(string text, Tab tab)
        {
            if (GUILayout.Button(text, _tab == tab ? _tabOn : _tabOff))
            {
                _tab = tab;
                _scroll = Vector2.zero;
                _pickingForSlot = -1;
                _friendPicker = false;
            }
        }

        // ---------------- Players tab ----------------

        private void DrawPlayersTab()
        {
            GUILayout.Label("Who is playing as which player? Join on the player-select screen as usual, then pick a profile for each player here. " +
                            "Picks are remembered for next time.", _small);
            GUILayout.Space(6);

            for (int slot = 0; slot < Game.MaxSlots; slot++)
            {
                var profile = Store.GetSlotProfile(slot);
                bool active = Game.IsSlotActive(slot);

                GUILayout.BeginVertical(_row);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Player {slot + 1}", _header, GUILayout.Width(150));
                GUILayout.Label(profile?.Name ?? "(no profile)", profile != null ? _label : _dim, GUILayout.MinWidth(200));
                GUILayout.FlexibleSpace();
                if (active) GUILayout.Label("in game", _green, GUILayout.Width(90));
                if (GUILayout.Button(_pickingForSlot == slot ? "Cancel" : "Choose", _button, GUILayout.Width(110)))
                    _pickingForSlot = _pickingForSlot == slot ? -1 : slot;
                GUILayout.EndHorizontal();

                if (_pickingForSlot == slot) DrawProfileChooser(slot);
                GUILayout.EndVertical();
                GUILayout.Space(4);
            }

            GUILayout.Space(6);
            GUILayout.Label("Outfits are remembered automatically: each player gets what they last raced with.", _small);
        }

        private void DrawProfileChooser(int slot)
        {
            var profiles = Store.Profiles.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (profiles.Count == 0)
            {
                GUILayout.Label("No profiles yet. Create some in the Profiles tab.", _dim);
                if (GUILayout.Button("Go to Profiles", _button)) { _tab = Tab.Profiles; _pickingForSlot = -1; }
                return;
            }

            if (GUILayout.Button("(no profile)", _button)) { Store.AssignSlot(slot, null); _pickingForSlot = -1; Game.ApplyNames(); }
            foreach (var p in profiles)
            {
                int inSlot = Array.IndexOf(Store.Profiles.SlotProfileIds, p.Id);
                string label = inSlot >= 0 && inSlot != slot ? $"{p.Name}   (now Player {inSlot + 1})" : p.Name;
                if (GUILayout.Button(label, _button))
                {
                    Store.AssignSlot(slot, p);
                    _pickingForSlot = -1;
                    Game.ApplyNames();
                }
            }
        }

        // ---------------- Profiles tab ----------------

        private const string NewNameControl = "cp_new_name";
        private const string RenameControl = "cp_rename";

        /// <summary>True (and consumes the event) when Enter is pressed while the named text field has focus.</summary>
        private static bool EnterPressedIn(string control) => KeyPressedIn(control, KeyCode.Return) || KeyPressedIn(control, KeyCode.KeypadEnter);

        private static bool KeyPressedIn(string control, KeyCode key)
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || e.keyCode != key || GUI.GetNameOfFocusedControl() != control) return false;
            e.Use();
            return true;
        }

        private void DrawProfilesTab()
        {
            if (_friendPicker) { DrawFriendPicker(); return; }

            // Add a profile by name (Enter or button).
            bool add = EnterPressedIn(NewNameControl);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(NewNameControl);
            _newName = GUILayout.TextField(_newName ?? "", 32, _field, GUILayout.MinWidth(300));
            GUI.enabled = !string.IsNullOrWhiteSpace(_newName);
            if (GUILayout.Button("Add name", _button, GUILayout.Width(150))) add = true;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (add && !string.IsNullOrWhiteSpace(_newName))
            {
                AddProfile(_newName.Trim(), 0);
                _newName = "";
            }
            if (GUILayout.Button("Add from Steam friends...", _button)) OpenFriendPicker();
            DrawFlash();
            GUILayout.Space(10);

            var profiles = Store.Profiles.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (profiles.Count == 0) GUILayout.Label("No profiles yet.", _dim);

            foreach (var p in profiles)
            {
                GUILayout.BeginVertical(_row);
                GUILayout.BeginHorizontal();
                if (_renamingId == p.Id) DrawRename(p);
                else
                {
                    GUILayout.Label(p.Name, _header, GUILayout.MinWidth(220));
                    if (p.SteamId != 0) GUILayout.Label("Steam", _dim, GUILayout.Width(70));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Rename", _button, GUILayout.Width(110)))
                    {
                        _renamingId = p.Id;
                        _renameText = p.Name;
                        _focusRename = true;
                    }
                    if (_confirmDeleteId == p.Id)
                    {
                        GUILayout.Label("Re-adding them later restores their records.", _small, GUILayout.Width(200));
                        if (GUILayout.Button("Delete", _button, GUILayout.Width(100))) { Store.DeleteProfile(p); _confirmDeleteId = null; Game.ApplyNames(); }
                        if (GUILayout.Button("No", _button, GUILayout.Width(60))) _confirmDeleteId = null;
                    }
                    else if (GUILayout.Button("Delete", _button, GUILayout.Width(100))) _confirmDeleteId = p.Id;
                }
                GUILayout.EndHorizontal();

                var st = p.Stats;
                GUILayout.Label($"{RecordsHeld(p)} map records held  |  {st.Races} races", _small);
                GUILayout.EndVertical();
                GUILayout.Space(4);
            }
        }

        private void DrawRename(Profile p)
        {
            bool confirm = EnterPressedIn(RenameControl);
            bool cancel = KeyPressedIn(RenameControl, KeyCode.Escape);

            GUI.SetNextControlName(RenameControl);
            _renameText = GUILayout.TextField(_renameText ?? "", 32, _field, GUILayout.MinWidth(260));
            if (_focusRename)
            {
                GUI.FocusControl(RenameControl);
                _focusRename = false;
            }
            GUI.enabled = !string.IsNullOrWhiteSpace(_renameText);
            if (GUILayout.Button("Save", _button, GUILayout.Width(90))) confirm = true;
            GUI.enabled = true;
            if (GUILayout.Button("Cancel", _button, GUILayout.Width(100))) cancel = true;

            if (confirm && !string.IsNullOrWhiteSpace(_renameText))
            {
                Store.RenameProfile(p, _renameText.Trim());
                Game.ApplyNames();
                _renamingId = null;
            }
            else if (cancel) _renamingId = null;
        }

        private void AddProfile(string name, ulong steamId)
        {
            var (profile, restored) = Store.AddOrRestoreProfile(name, steamId);
            if (restored != null) FlashProfiles($"Welcome back, {profile.Name}! Restored their {restored}.");
        }

        // ---------------- Sync page ----------------

        private const string SyncUrlControl = "cp_sync_url";
        private const string SyncKeyControl = "cp_sync_key";
        private string _syncUrlText, _syncKeyText;
        private bool _confirmForget;

        private void OpenSyncTab()
        {
            _tab = Tab.Sync;
            _scroll = Vector2.zero;
            _syncUrlText = Plugin.SyncUrl.Value;
            _syncKeyText = Plugin.SyncKey.Value;
            _confirmForget = false;
        }

        private void DrawSyncTab()
        {
            GUILayout.Label("Share profiles, stats and leaderboards between PCs through your own Couch Party server. " +
                            "Every PC keeps its own copy and works offline; syncing merges the copies, so nothing is lost. " +
                            "Player 1-4 picks and outfits stay on each PC.", _small);
            GUILayout.Space(8);

            GUILayout.Label("Server address", _label);
            GUI.SetNextControlName(SyncUrlControl);
            _syncUrlText = GUILayout.TextField(_syncUrlText ?? "", 200, _field);
            GUILayout.Label("e.g. https://your-domain.example/couchparty", _small);
            GUILayout.Label("Key for this PC", _label);
            GUI.SetNextControlName(SyncKeyControl);
            _syncKeyText = GUILayout.PasswordField(_syncKeyText ?? "", '*', 200, _field);
            GUILayout.Space(6);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save & sync", _button, GUILayout.Width(180)))
            {
                Plugin.SyncUrl.Value = (_syncUrlText ?? "").Trim();
                Plugin.SyncKey.Value = (_syncKeyText ?? "").Trim();
                Plugin.SyncEnabled.Value = true;
                Sync.SyncNow(this);
            }
            GUI.enabled = Sync.Configured && !Sync.Busy;
            if (GUILayout.Button("Sync now", _button, GUILayout.Width(150))) Sync.SyncNow(this);
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            bool enabled = GUILayout.Toggle(Plugin.SyncEnabled.Value, " Sync on", _label);
            if (enabled != Plugin.SyncEnabled.Value) Plugin.SyncEnabled.Value = enabled;
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            // Status
            if (Sync.Busy) GUILayout.Label("Syncing...", _gold);
            else if (Sync.LastError != null) GUILayout.Label("Last sync failed: " + Sync.LastError, _gold);
            else if (Sync.LastSync.HasValue) GUILayout.Label($"{Sync.LastResult} at {Sync.LastSync:t}", _green);
            else if (!Sync.Configured) GUILayout.Label("Not set up on this PC.", _dim);
            else GUILayout.Label("Not synced yet this session.", _dim);
            GUILayout.Label("Syncs when the game starts and a few seconds after each race or profile change.", _small);

            GUILayout.Space(14);
            GUILayout.Label("Playing on someone else's PC? When you leave, forget the server and key here. " +
                            "The data already synced stays on this PC, but it won't sync any more.", _small);
            if (_confirmForget)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Yes, forget server and key", _button, GUILayout.Width(330)))
                {
                    Plugin.SyncUrl.Value = "";
                    Plugin.SyncKey.Value = "";
                    _syncUrlText = _syncKeyText = "";
                    _confirmForget = false;
                }
                if (GUILayout.Button("No", _button, GUILayout.Width(60))) _confirmForget = false;
                GUILayout.EndHorizontal();
            }
            else if (GUILayout.Button("Forget on this PC", _button, GUILayout.Width(240))) _confirmForget = true;
        }

        // ---------------- Backups page ----------------

        private void DrawBackupsTab()
        {
            GUILayout.Label("Backups of your profiles, stats and leaderboards. One is made when you start the game, but only if " +
                            $"something changed since the last one. The newest {Plugin.MaxBackups.Value} are kept, however old they are; " +
                            "backups made before a mod update are kept forever.", _small);
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Backup now", _button, GUILayout.Width(180)))
            {
                try { Backups.BackupNow(); FlashProfiles("Backup created."); }
                catch (Exception e) { FlashProfiles("Backup failed: " + e.Message); }
                _backupList = null;
            }
            if (GUILayout.Button("Open backup folder", _button, GUILayout.Width(240))) Backups.OpenFolder();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Back", _button, GUILayout.Width(100))) _tab = Tab.Players;
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(Plugin.ExtraBackupFolder.Value))
                GUILayout.Label($"Also copied to: {Plugin.ExtraBackupFolder.Value}", _small);
            DrawFlash();
            DrawStartFresh();
            GUILayout.Space(8);

            if (_backupList == null)
            {
                try { _backupList = Backups.List(); }
                catch (Exception e) { _backupList = new List<Backups.Entry>(); FlashProfiles("Could not list backups: " + e.Message); }
            }
            if (_backupList.Count == 0) GUILayout.Label("No backups yet.", _dim);

            foreach (var b in _backupList)
            {
                GUILayout.BeginVertical(_row);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{b.Created:g}", _header, GUILayout.Width(260));
                GUILayout.Label(b.Reason + (b.Keep ? "  (kept forever)" : ""), b.Keep ? _gold : _label);
                GUILayout.FlexibleSpace();
                if (_confirmRestore == b.Folder)
                {
                    if (GUILayout.Button("Really restore?", _button, GUILayout.Width(190)))
                    {
                        try { Backups.Restore(b); FlashProfiles($"Restored the backup from {b.Created:g}. (Your previous data was backed up first.)"); }
                        catch (Exception e) { FlashProfiles("Restore failed: " + e.Message); }
                        _confirmRestore = null;
                        _backupList = null;
                        GUILayout.EndHorizontal();
                        GUILayout.EndVertical();
                        break;
                    }
                    if (GUILayout.Button("No", _button, GUILayout.Width(60))) _confirmRestore = null;
                }
                else if (GUILayout.Button("Restore", _button, GUILayout.Width(120))) _confirmRestore = b.Folder;
                GUILayout.EndHorizontal();
                GUILayout.Label($"{b.Maps} maps  |  {b.Runs} runs  |  {b.Profiles} profiles  |  mod {b.ModVersion ?? "?"}", _small);
                GUILayout.EndVertical();
                GUILayout.Space(3);
            }
        }

        // ---------------- Start fresh ----------------

        private const string WipeControl = "cp_wipe";
        private bool _wipeOpen;
        private bool _wipeProfiles;
        private string _wipeText = "";

        private void DrawStartFresh()
        {
            GUILayout.Space(6);
            if (!_wipeOpen)
            {
                if (GUILayout.Button("Start fresh...", _button, GUILayout.Width(200))) { _wipeOpen = true; _wipeText = ""; _wipeProfiles = false; }
                return;
            }

            GUILayout.BeginVertical(_row);
            GUILayout.Label("Start fresh", _groupHeader);
            GUILayout.Label("Wipes the data on this PC and, through sync, on every synced PC (including ones that are offline " +
                            "right now: their old data is dropped the next time they sync). A backup that is kept forever is made first.", _small);
            if (GUILayout.Toggle(!_wipeProfiles, " Wipe stats and leaderboards, keep profiles", _label)) _wipeProfiles = false;
            if (GUILayout.Toggle(_wipeProfiles, " Wipe everything, including profiles", _label)) _wipeProfiles = true;
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Type WIPE to confirm:", _label, GUILayout.Width(260));
            GUI.SetNextControlName(WipeControl);
            _wipeText = GUILayout.TextField(_wipeText ?? "", 10, _field, GUILayout.Width(160));
            GUI.enabled = _wipeText == "WIPE";
            if (GUILayout.Button(_wipeProfiles ? "Wipe everything" : "Wipe stats & leaderboards", _button))
            {
                try
                {
                    Store.StartFresh(_wipeProfiles);
                    Sync.SyncNow(this);
                    FlashProfiles(_wipeProfiles ? "Everything wiped. A backup was kept." : "Stats and leaderboards wiped. A backup was kept.");
                }
                catch (Exception e) { FlashProfiles("Start fresh failed: " + e.Message); }
                _wipeOpen = false;
                _backupList = null;
            }
            GUI.enabled = true;
            if (GUILayout.Button("Cancel", _button, GUILayout.Width(110))) _wipeOpen = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("The server keeps its own history of old versions in data/history/; delete those files there if you want them gone too.", _small);
            GUILayout.EndVertical();
        }

        private void FlashProfiles(string message)
        {
            _flash = message;
            _flashUntil = Time.unscaledTime + 6f;
        }

        private void DrawFlash()
        {
            if (_flash != null && Time.unscaledTime < _flashUntil) GUILayout.Label(_flash, _gold);
        }


        private class FriendEntry
        {
            public string Name;      // the name they chose themselves
            public string Nickname;  // the nickname you gave them, if different
            public ulong Id;
            public bool IsYou;

            public bool Matches(string search) =>
                string.IsNullOrEmpty(search) ||
                Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (Nickname != null && Nickname.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void OpenFriendPicker()
        {
            _friendPicker = true;
            _friendSearch = "";
            _friends = null;
            _friendsError = null;
            try
            {
                if (!SteamClient.IsValid) { _friendsError = "Steam isn't available right now."; return; }
                _friends = new List<FriendEntry>
                {
                    new FriendEntry { Name = SteamClient.Name, Id = SteamClient.SteamId.Value, IsYou = true }
                };
                foreach (var f in SteamFriends.GetFriends())
                {
                    string shown = f.Name;
                    string own = SteamNames.OwnName(f);
                    if (string.IsNullOrEmpty(own)) continue;
                    _friends.Add(new FriendEntry
                    {
                        Name = own,
                        Nickname = !string.IsNullOrEmpty(shown) && shown != own ? shown : null,
                        Id = f.Id.Value,
                    });
                }
                _friends = _friends.Take(1)
                    .Concat(_friends.Skip(1).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception e)
            {
                _friendsError = "Couldn't read your Steam friends list.";
                Plugin.Log.LogError($"Reading Steam friends failed: {e}");
            }
        }

        private void DrawFriendPicker()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Steam friends", _header);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Back", _button, GUILayout.Width(100))) { _friendPicker = false; return; }
            GUILayout.EndHorizontal();

            if (_friendsError != null) { GUILayout.Label(_friendsError, _dim); return; }
            if (_friends == null) return;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", _label, GUILayout.Width(100));
            GUI.SetNextControlName(SearchControl);
            _friendSearch = GUILayout.TextField(_friendSearch ?? "", 32, _field);
            GUILayout.EndHorizontal();
            GUILayout.Label("Profiles get the name your friend chose. Your nickname for them is shown in brackets.", _small);
            GUILayout.Space(6);

            var existing = new HashSet<ulong>(Store.Profiles.Profiles.Where(p => p.SteamId != 0).Select(p => p.SteamId));
            int shownCount = 0;
            foreach (var f in _friends)
            {
                if (!f.Matches(_friendSearch)) continue;
                shownCount++;
                bool added = existing.Contains(f.Id);
                string label = f.Name;
                if (f.IsYou) label += "   (you)";
                else if (f.Nickname != null) label += $"   ({f.Nickname})";
                if (added) label += "   - added";
                GUI.enabled = !added;
                if (GUILayout.Button(label, _button))
                {
                    AddProfile(f.Name, f.Id);
                    existing.Add(f.Id);
                }
                GUI.enabled = true;
            }
            if (shownCount == 0) GUILayout.Label("No matches.", _dim);
        }

        // ---------------- Stats tab ----------------

        private static int RecordsHeld(Profile p) =>
            Store.Leaderboard.Levels.Values.Count(l => l.Ranked().FirstOrDefault()?.ProfileId == p.Id);

        private static string Km(double meters) => meters >= 1000 ? $"{meters / 1000:0.0} km" : $"{meters:0} m";

        private static string AirTime(double seconds) =>
            seconds >= 60 ? $"{(int)(seconds / 60)} min {seconds % 60:0} s" : $"{seconds:0.0} s";

        /// <summary>Maps that have a record at all (someone has a valid time there).</summary>
        private static int MapsWithRecords() => Store.Leaderboard.Levels.Values.Count(l => l.Best.Count > 0);

        private List<Profile> ProfilesWithStats() =>
            Store.Profiles.Profiles.Where(p => p.Stats != null && p.Stats.Races > 0)
                .OrderByDescending(p => RecordsHeld(p)).ThenByDescending(p => p.Stats.Races).ToList();

        private void DrawStatsTab()
        {
            var profiles = ProfilesWithStats();
            if (profiles.Count == 0)
            {
                GUILayout.Label("No stats yet. They build up as profiled players race in local multiplayer.", _dim);
                return;
            }

            // Player picker.
            var selected = profiles.FirstOrDefault(p => p.Id == _statsProfileId) ?? profiles[0];
            for (int i = 0; i < profiles.Count; i += 4)
            {
                GUILayout.BeginHorizontal();
                foreach (var p in profiles.Skip(i).Take(4))
                    if (GUILayout.Button(p.Name, p == selected ? _tabOn : _tabOff)) _statsProfileId = p.Id;
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);

            // All time, or tonight (same evening as the Tonight tab: this PC only, new after hours without racing).
            bool tonight = _statsTonight;
            var st = tonight ? Session.StatsOrNull(selected.Id) ?? new PlayerStats() : selected.Stats;
            GUILayout.BeginVertical(_row);
            GUILayout.BeginHorizontal();
            GUILayout.Label(selected.Name, _title);
            GUILayout.FlexibleSpace();
            GUILayout.Label(tonight ? $"{st.Races} races tonight" : $"{st.Races} races", _gold);
            GUILayout.Space(16);
            if (GUILayout.Button("All time", !tonight ? _tabOn : _tabOff, GUILayout.Width(130))) _statsTonight = false;
            if (GUILayout.Button("Tonight", tonight ? _tabOn : _tabOff, GUILayout.Width(130))) _statsTonight = true;
            GUILayout.EndHorizontal();
            // The headline: all time, how many maps this player has the #1 time on;
            // tonight, on how many of tonight's maps they have the fastest time of the evening.
            int held, maps;
            string headline;
            if (tonight)
            {
                held = Session.FastestOn(selected.Id);
                maps = Session.MapsTonight;
                headline = maps > 0 ? $"Fastest on {held} of {maps} map{(maps == 1 ? "" : "s")} tonight" : "No maps raced yet tonight";
            }
            else
            {
                held = RecordsHeld(selected);
                maps = MapsWithRecords();
                headline = maps > 0 ? $"Holds {held} of {maps} map records" : "No map records yet";
            }
            ProgressBar(headline, maps > 0 ? (float)held / maps : 0f);
            GUILayout.Space(8);

            // Groups of (label, value) rows, laid out in 3 columns on a wide window (fits 16:9 without scrolling), else 2.
            var racing = new (string, object)[] { ("Races", st.Races), ("Wins", st.Wins), ("DNFs", st.Dnfs) };
            // Every kind of finish in one place: valid finishes with their special cases indented, then shortcuts.
            var finishes = new (string, object)[] { ("Finishes", st.Finishes), ("   3-wheel", st.ThreeWheelFinishes), ("   2-wheel", st.TwoWheelFinishes), ("   1-wheel", st.OneWheelFinishes), ("   no-wheel", st.ZeroWheelFinishes), ("   on foot", st.OnFootFinishes), ("   reverse", st.ReverseFinishes), ("Shortcut finishes", st.ShortcutFinishes) };
            var records = new (string, object)[] { (tonight ? "Fastest on (maps)" : "Map records held", held), ("Records broken", st.RecordsBroken), ("Personal bests", st.PersonalBests), ("Time on the clock", Game.FormatDuration(st.FinishTimeSeconds)) };
            var carnage = new (string, object)[] { ("Crashes", st.Crashes), ("   hard crashes", st.HardCrashes), ("   ejects", st.Ejects), ("   timeouts", st.Timeouts), ("Wheels lost", st.WheelsLost) };
            var driving = new (string, object)[] { ("Distance driven", Km(st.DistanceMeters)), ("Distance per race", Km(st.Races > 0 ? st.DistanceMeters / st.Races : 0)), ("Top speed", $"{st.TopSpeedKmh:0} km/h"), ("Air time", AirTime(st.AirSeconds)), ("Longest jump", $"{st.LongestJumpSeconds:0.0} s"), ("Big jumps (1 s+)", st.BigJumps) };
            var bumper = new (string, object)[] { ("Car-to-car hits", st.CarToCarHits), ("Rammed others", st.RammedOthers), ("Got rammed", st.GotRammed), ("Knockouts", st.Knockouts), ("Wheels knocked off", st.WheelsKnockedOff) };
            var cats = new (string, object)[] { ("Cats hugged", st.CatsHugged), ("Crashes caused by cats", st.CatCrashes) };
            var stunts = new (string, object)[] { ("Front flips", st.FrontFlips), ("Backflips", st.Backflips), ("Barrel rolls", st.BarrelRolls), ("180s", st.Spins180), ("360s", st.Spins360), ("720s", st.Spins720), ("1080s", st.Spins1080), ("Biggest spin", st.BiggestSpin > 0 ? $"{st.BiggestSpin}°" : NoValue) };

            var columns = Wide
                ? new[]
                {
                    new[] { ("Racing", racing), ("Finishes", finishes), ("Records", records) },
                    new[] { ("Carnage", carnage), ("Bumper cars", bumper), ("Cats", cats) },
                    new[] { ("Driving", driving), ("Stunts", stunts) },
                }
                : new[]
                {
                    new[] { ("Racing", racing), ("Finishes", finishes), ("Records", records), ("Carnage", carnage) },
                    new[] { ("Bumper cars", bumper), ("Cats", cats), ("Driving", driving), ("Stunts", stunts) },
                };
            float col = (_windowRect.width - 70 - (columns.Length - 1) * 20) / columns.Length;
            GUILayout.BeginHorizontal();
            for (int c = 0; c < columns.Length; c++)
            {
                if (c > 0) GUILayout.Space(20);
                GUILayout.BeginVertical(GUILayout.Width(col));
                for (int g = 0; g < columns[c].Length; g++)
                {
                    if (g > 0) GUILayout.Space(18); // a clear gap before each group header
                    StatGroup(columns[c][g].Item1);
                    foreach (var (label, value) in columns[c][g].Item2) StatRow(label, value);
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            // Resetting is about lifetime stats; tonight has "Start a new evening" on the Tonight tab.
            if (tonight) return;
            GUILayout.Space(10);
            if (_confirmResetStats)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Reset everyone's stats? (Map records are kept.)", _label);
                if (GUILayout.Button("Yes, reset", _button, GUILayout.Width(150)))
                {
                    Store.ResetAllStats();
                    _confirmResetStats = false;
                }
                if (GUILayout.Button("No", _button, GUILayout.Width(60))) _confirmResetStats = false;
                GUILayout.EndHorizontal();
            }
            else if (GUILayout.Button("Reset stats", _button, GUILayout.Width(170))) _confirmResetStats = true;
        }

        private void StatGroup(string title)
        {
            GUILayout.Label(title, _groupHeader);
            var r = GUILayoutUtility.GetRect(1, 3, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(r, _texAccent);
            GUILayout.Space(4);
        }

        private void StatRow(string label, object value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label);
            GUILayout.FlexibleSpace();
            GUILayout.Label(value.ToString(), _statValue);
            GUILayout.EndHorizontal();
        }

        private void ProgressBar(string label, float fraction)
        {
            var r = GUILayoutUtility.GetRect(1, 34, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(r, _texBarBack);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fraction), r.height), _texAccent);
            ShadowLabel(new Rect(r.x + 12, r.y, r.width - 24, r.height), label, _barLabel);
        }

        // ---------------- Hall of Fame tab ----------------

        private void DrawFameTab()
        {
            var profiles = ProfilesWithStats();
            if (profiles.Count == 0)
            {
                GUILayout.Label("No stats yet. Race a few rounds first!", _dim);
                return;
            }

            var categories = new List<(string title, string about, Func<Profile, double> value, Func<double, string> format)>
            {
                ("Record hoarder", "Holds the #1 time on the most maps right now", p => RecordsHeld(p), v => $"{v:0} maps"),
                ("Most wins", "Most races won", p => p.Stats.Wins, v => $"{v:0} wins"),
                ("Record breaker", "Broke an existing map record most often", p => p.Stats.RecordsBroken, v => $"{v:0} records"),
                ("PB machine", "Beat their own best time most often", p => p.Stats.PersonalBests, v => $"{v:0} PBs"),
                ("Speed demon", "Highest top speed ever", p => p.Stats.TopSpeedKmh, v => $"{v:0} km/h"),
                ("Road tripper", "Longest total distance driven", p => p.Stats.DistanceMeters, v => Km(v)),
                ("Frequent flyer", "Most total time in the air", p => p.Stats.AirSeconds, v => AirTime(v)),
                ("Moon landing", "Longest single jump", p => p.Stats.LongestJumpSeconds, v => $"{v:0.0} s"),
                ("Acrobat", "Most flips landed (front flips and backflips, without crashing)", p => p.Stats.FrontFlips + p.Stats.Backflips, v => $"{v:0} flips"),
                ("Corkscrew", "Most barrel rolls landed (without crashing)", p => p.Stats.BarrelRolls, v => $"{v:0} rolls"),
                ("Spin doctor", "Biggest flat spin landed, in exact degrees", p => p.Stats.BiggestSpin, v => $"{v:0}°"),
                ("Moonwalker", "Most finishes crossing the line backwards", p => p.Stats.ReverseFinishes, v => $"{v:0} reverse finishes"),
                ("Crash king", "Most hard crashes (actually hit something)", p => p.Stats.HardCrashes, v => $"{v:0} crashes"),
                ("Ejector seat", "Ejected the most", p => p.Stats.Ejects, v => $"{v:0} ejects"),
                ("Snail", "Timed out the most (too slow for 10 seconds)", p => p.Stats.Timeouts, v => $"{v:0} timeouts"),
                ("Wheel donor", "Lost the most wheels", p => p.Stats.WheelsLost, v => $"{v:0} wheels"),
                ("Limping home", "Most finishes with wheels missing", p => p.Stats.DamagedFinishes, v => $"{v:0} finishes"),
                ("No-wheel hero", "Most finishes with no wheels left", p => p.Stats.ZeroWheelFinishes, v => $"{v:0} finishes"),
                ("Stuntman", "Most finishes on foot, after ejecting from the car", p => p.Stats.OnFootFinishes, v => $"{v:0} on foot finishes"),
                ("Shortcut hunter", "Crossed the finish but missed checkpoints most often", p => p.Stats.ShortcutFinishes, v => $"{v:0} shortcut finishes"),
                ("Bumper car champ", "Rammed other racers the most", p => p.Stats.RammedOthers, v => $"{v:0} rams"),
                ("Demolition derby", "Most knockouts: rammed another racer who then crashed soon after", p => p.Stats.Knockouts, v => $"{v:0} knockouts"),
                ("Wheel snatcher", "Knocked the most wheels off other racers", p => p.Stats.WheelsKnockedOff, v => $"{v:0} wheels"),
                ("Punching bag", "Got rammed by other racers the most", p => p.Stats.GotRammed, v => $"{v:0} times"),
                ("Cat lover", "Hugged the most walking cats (with their car)", p => p.Stats.CatsHugged, v => $"{v:0} cats"),
                ("Cat's revenge", "Crashed the most after hitting a cat", p => p.Stats.CatCrashes, v => $"{v:0} crashes"),
            };

            int perRow = Wide ? 3 : 2;
            float col = (_windowRect.width - 70 - (perRow - 1) * 10) / perRow;
            for (int i = 0; i < categories.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(i + perRow, categories.Count); j++)
                {
                    var c = categories[j];
                    GUILayout.BeginVertical(_row, GUILayout.Width(col));
                    GUILayout.Label(c.title, _groupHeader);
                    GUILayout.Label(c.about, _small);
                    var top = profiles.Select(p => (p, v: c.value(p))).Where(x => x.v > 0)
                        .OrderByDescending(x => x.v).Take(3).ToList();
                    if (top.Count == 0) GUILayout.Label("nobody yet", _dim);
                    for (int k = 0; k < top.Count; k++)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(Medal(k), _medals[k], GUILayout.Width(44));
                        GUILayout.Label(top[k].p.Name, _medals[k]);
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(c.format(top[k].v), _label);
                        GUILayout.EndHorizontal();
                    }
                    for (int k = top.Count; k < 3; k++) GUILayout.Label(" ", _label);
                    GUILayout.EndVertical();
                    if (j < Mathf.Min(i + perRow, categories.Count) - 1) GUILayout.Space(10);
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(8);
            }
        }

        private static string Medal(int place) => place == 0 ? "1st" : place == 1 ? "2nd" : "3rd";

        // ---------------- Tonight tab ----------------

        private bool _confirmNewEvening;

        private void DrawTonightTab()
        {
            var data = Session.Current;
            int maps = Session.MapsTonight;
            GUILayout.Label($"Started {data.Started:t}  |  {maps} map{(maps == 1 ? "" : "s")}  |  {Session.RunsTonight} runs. " +
                            $"A new evening starts after {Plugin.SessionResetHours.Value} hours without racing.", _small);
            GUILayout.Space(6);

            // Standings
            GUILayout.Label("Standings", _groupHeader);
            var standings = Session.Standings();
            if (standings.Count == 0) GUILayout.Label("Nobody with a profile has raced yet tonight.", _dim);
            else
            {
                TonightRow(true, "", "Fastest on", "PBs", "Records", "Crashes", "Hits", "Races");
                for (int i = 0; i < standings.Count; i++)
                {
                    var s = standings[i];
                    TonightRow(false, s.Name, s.BestTimes.ToString(), s.Stats.PersonalBests.ToString(), s.Stats.Records.ToString(),
                               s.Stats.Crashes.ToString(), s.Stats.Hits.ToString(), s.Stats.Races.ToString(), i == 0 && s.BestTimes > 0);
                }
            }
            GUILayout.Space(12);

            // Every map tonight, newest first
            GUILayout.Label("Tonight's maps", _groupHeader);
            var list = Session.MapsNewestFirst();
            if (list.Count == 0) GUILayout.Label("No maps raced yet tonight.", _dim);
            foreach (var (key, map) in list)
            {
                GUILayout.BeginHorizontal(_row);
                GUILayout.Label(map.LevelName ?? "Unknown map", _label, GUILayout.Width(_windowRect.width * 0.38f));
                if (map.Best.Count > 0)
                {
                    var best = map.Best.OrderBy(kv => kv.Value).First();
                    GUILayout.Label(Session.NameOf(best.Key), _gold, GUILayout.Width(_windowRect.width * 0.22f));
                    GUILayout.Label(Game.FormatTime(best.Value), _gold, GUILayout.Width(140));
                }
                else GUILayout.Label("no valid times", _dim, GUILayout.Width(_windowRect.width * 0.22f + 140));
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{map.Runs} run{(map.Runs == 1 ? "" : "s")}", _small, GUILayout.Width(90));
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(12);
            if (_confirmNewEvening)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Start a new evening? Tonight's standings and maps are cleared (records and stats are kept).", _label);
                if (GUILayout.Button("Yes", _button, GUILayout.Width(90))) { Session.Reset(); _confirmNewEvening = false; }
                if (GUILayout.Button("No", _button, GUILayout.Width(60))) _confirmNewEvening = false;
                GUILayout.EndHorizontal();
            }
            else if (GUILayout.Button("Start a new evening", _button, GUILayout.Width(260))) _confirmNewEvening = true;
        }

        private void TonightRow(bool header, string name, string a, string b, string c, string d, string e, string f, bool leader = false)
        {
            var style = header ? _small : leader ? _gold : _label;
            GUILayout.BeginHorizontal(header ? GUIStyle.none : _row);
            GUILayout.Label(name, style, GUILayout.Width(_windowRect.width * 0.28f));
            foreach (var v in new[] { a, b, c, d, e, f })
                GUILayout.Label(v, header ? _small : (leader && v == a ? _gold : _label), GUILayout.Width(_windowRect.width * 0.105f));
            GUILayout.EndHorizontal();
        }

        // ---------------- Leaderboard tab ----------------

        private void DrawLeaderboardTab()
        {
            var levels = Store.Leaderboard.Levels.OrderByDescending(kv => kv.Value.LastPlayed).ToList();
            if (levels.Count == 0)
            {
                GUILayout.Label("No local multiplayer races recorded yet. Times are saved for players who have a profile " +
                                "and pass every checkpoint.", _dim);
                return;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Best per player", !_lbAllRuns ? _tabOn : _tabOff)) _lbAllRuns = false;
            if (GUILayout.Button("All runs", _lbAllRuns ? _tabOn : _tabOff)) _lbAllRuns = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            foreach (var kv in levels)
            {
                var rec = kv.Value;
                var ranked = rec.Ranked();
                bool expanded = _expandedLevel == kv.Key;

                GUILayout.BeginVertical(_row);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(expanded ? "-" : "+", _button, GUILayout.Width(44)))
                    _expandedLevel = expanded ? null : kv.Key;
                GUILayout.Label(rec.LevelName ?? "Unknown map", _header);
                GUILayout.FlexibleSpace();
                if (ranked.Count > 0)
                    GUILayout.Label($"{Store.DisplayName(ranked[0])}  {Game.FormatTime(ranked[0].Time)}", _gold);
                GUILayout.EndHorizontal();

                if (expanded)
                {
                    GUILayout.Label($"by {rec.Author}  |  {rec.RacesPlayed} races  |  last played {rec.LastPlayed:d}", _small);
                    if (_lbAllRuns) DrawRunList(kv.Key, rec);
                    else DrawBestList(kv.Key, rec, ranked);

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (_confirmClearLevel == kv.Key)
                    {
                        if (GUILayout.Button("Really clear this map?", _button, GUILayout.Width(260)))
                        {
                            Store.Leaderboard.ClearLevel(kv.Key);
                            Store.SaveLeaderboard();
                            _confirmClearLevel = null;
                        }
                        if (GUILayout.Button("No", _button, GUILayout.Width(60))) _confirmClearLevel = null;
                    }
                    else if (GUILayout.Button("Clear map", _button, GUILayout.Width(150))) _confirmClearLevel = kv.Key;
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
                GUILayout.Space(4);
            }
        }

        private void DrawBestList(string levelKey, LevelRecords rec, List<BestTime> ranked)
        {
            for (int i = 0; i < ranked.Count; i++)
            {
                var b = ranked[i];
                string key = levelKey + "|p|" + b.ProfileId;
                if (LeaderboardRow(i, Store.DisplayName(b), b.Time, b.Date, key, "Remove all their times?"))
                {
                    rec.RemovePlayer(b.ProfileId);
                    Store.SaveLeaderboard();
                    break;
                }
            }
        }

        private void DrawRunList(string levelKey, LevelRecords rec)
        {
            var runs = rec.TopRuns();
            for (int i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                string key = levelKey + "|r|" + r.ProfileId + "|" + r.Time.ToString("R") + "|" + r.Date.Ticks;
                if (LeaderboardRow(i, Store.DisplayName(r), r.Time, r.Date, key, "Remove?"))
                {
                    rec.RemoveRun(r);
                    Store.SaveLeaderboard();
                    break;
                }
            }
        }

        /// <summary>Draws one leaderboard row; returns true when its removal was confirmed.</summary>
        private bool LeaderboardRow(int i, string name, float time, DateTime date, string key, string confirmText)
        {
            bool removed = false;
            var style = i == 0 ? _gold : _label;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{i + 1}.", style, GUILayout.Width(50));
            GUILayout.Label(name, style, GUILayout.MinWidth(200));
            GUILayout.FlexibleSpace();
            GUILayout.Label(Game.FormatTime(time), style, GUILayout.Width(130));
            GUILayout.Label($"{date:d}", _small, GUILayout.Width(110));
            if (_confirmRemove == key)
            {
                if (GUILayout.Button(confirmText, _button)) { removed = true; _confirmRemove = null; }
                if (GUILayout.Button("No", _button, GUILayout.Width(56))) _confirmRemove = null;
            }
            else if (GUILayout.Button("x", _button, GUILayout.Width(40))) _confirmRemove = key;
            GUILayout.EndHorizontal();
            return removed;
        }

        // ---------------- Overlays ----------------

        // The game's results panel is laid out as fractions of the screen (measured on 16:9 and 21:9,
        // identical on both), so the "Race Results" column is found the same way on any screen shape.
        private const float GameColumnLeft = 0.100f;    // left edge of the "Race Results" column (of screen width)
        private const float GameColumnRight = 0.5785f;  // divider between "Race Results" and "Championship"
        private const float GameColumnTop = 0.2204f;    // below the header (of screen height)
        private const float GameColumnBottom = 0.7804f; // above the Retry / Continue bar

        private void DrawResults(float vw, float vh)
        {
            TryLoadGameFont();
            var area = Rect.MinMaxRect(vw * GameColumnLeft, vh * GameColumnTop, vw * GameColumnRight, vh * GameColumnBottom);
            GUI.DrawTexture(area, _texBody);

            // Left side = everything about this map: this race's results, the map's records, tonight's grind on it.
            var recordLine = _results.Lines.FirstOrDefault(l => l.NewMapRecord);
            bool showRecordBand = recordLine != null && _results.PreviousRecordTime < float.MaxValue;
            var grind = Session.MapStandings(_results.LevelKey, out int mapRuns);
            int lines = _results.Lines.Count;
            int available = _resultsAllRuns ? _results.AllRuns.Count : _results.AllTime.Count;

            const float titleH = 60, bannerH = 38, gapH = 10, recHeadH = 42, grindBandH = 36, grindHeadH = 20, minRow = 24, maxRow = 36;
            float fixedH = titleH + (showRecordBand ? bannerH : 0) + gapH + recHeadH + (grind.Count > 0 ? gapH + grindBandH + grindHeadH : 0);
            float space = area.height - fixedH;
            // As many records as fit (up to the setting), keeping rows readable.
            int top = Mathf.Min(Plugin.ResultsTopCount.Value, available);
            while (top > 1 && (lines + top + grind.Count) * minRow > space) top--;
            float rowH = Mathf.Clamp(space / Mathf.Max(1, lines + Mathf.Max(1, top) + grind.Count), minRow, maxRow);
            bool compact = rowH < 34;

            float y = area.y;
            var titleRect = new Rect(area.x, y, area.width, titleH);
            GUI.DrawTexture(titleRect, _texTitleBand);
            ShadowLabel(titleRect, "Race Results", _ovTitle);
            y += titleH;

            // ---- This race
            int pos = 0;
            for (int i = 0; i < lines; i++)
            {
                var line = _results.Lines[i];
                var row = new Rect(area.x, y, area.width, rowH);
                GUI.DrawTexture(row, i % 2 == 0 ? _texRowA : _texRowB);
                string rank = line.Finished ? $"#{++pos}" : "";
                string tag;
                GUIStyle tagStyle = _ovTagDim;
                if (line.NewMapRecord) { tag = "MAP RECORD!"; tagStyle = _ovTagGold; }
                else if (line.NewPersonalBest) { tag = $"NEW PB  -{line.PreviousBest - line.Time:0.000}"; tagStyle = _ovTagGreen; }
                else if (line.CrossedFinish && line.MissedCheckpoints) tag = $"missed checkpoint ({Mathf.Max(0, line.Checkpoints)}/{_results.TotalCheckpoints})";
                else if (line.Finished && !line.HasProfile) tag = "no profile";
                else if (line.Finished && line.GapToRecord < float.MaxValue) tag = $"+{line.GapToRecord:0.000} to record";
                else tag = "";
                DrawRow(row, rank, line.Name, line.CrossedFinish ? Game.FormatTime(line.Time) : "DNF", tag, tagStyle,
                    line.Finished ? (compact ? _ovTimeS : _ovTime) : _ovTimeDim, compact);
                y += rowH;
            }

            // "X beat Y's record by Z s"
            if (showRecordBand)
            {
                var band = new Rect(area.x, y, area.width, bannerH);
                GUI.DrawTexture(band, _texHeader);
                float gain = _results.PreviousRecordTime - recordLine.Time;
                string text = _results.PreviousRecordName == recordLine.Name
                    ? $"{recordLine.Name} beat their own record by {gain:0.000} s"
                    : $"{recordLine.Name} beat {_results.PreviousRecordName}'s {Game.FormatTime(_results.PreviousRecordTime)} by {gain:0.000} s";
                ShadowLabel(band, text, _ovBand);
                y += bannerH;
            }
            y += gapH;

            // ---- Map records: best per player (default) or all runs. Click the header or press F9 to switch.
            var sectionRect = new Rect(area.x, y, area.width, recHeadH);
            GUI.DrawTexture(sectionRect, _texTitleBand);
            string view = _resultsAllRuns ? "all runs" : "best per player";
            ShadowLabel(sectionRect, $"Map Records: {view}", _ovTitleMid);
            ShadowLabel(new Rect(sectionRect.x, sectionRect.y, sectionRect.width - 12, recHeadH), $"{Plugin.SwitchViewKey.Value}: switch", _ovTagDim);
            if (GUI.Button(sectionRect, GUIContent.none, GUIStyle.none)) _resultsAllRuns = !_resultsAllRuns;
            y += recHeadH;

            if (available == 0)
            {
                ShadowLabel(new Rect(area.x, y, area.width, rowH), "No valid times yet", _ovCenter);
                y += rowH;
            }
            for (int i = 0; i < Mathf.Min(top, available); i++)
            {
                var row = new Rect(area.x, y, area.width, rowH);
                GUI.DrawTexture(row, i % 2 == 0 ? _texRowB : _texRowA);
                string name; float time; bool fresh;
                if (_resultsAllRuns)
                {
                    var r = _results.AllRuns[i];
                    name = Store.DisplayName(r); time = r.Time;
                    fresh = (DateTime.Now - r.Date).TotalMinutes < 2;
                }
                else
                {
                    var b = _results.AllTime[i];
                    name = Store.DisplayName(b); time = b.Time;
                    fresh = (DateTime.Now - b.Date).TotalMinutes < 2;
                }
                DrawRow(row, $"{i + 1}.", name, Game.FormatTime(time), fresh ? "new!" : "", _ovTagGold,
                    compact ? _ovTimeS : (i == 0 ? _ovTime : _ovTimeSmall), compact);
                y += rowH;
            }

            // ---- This map tonight: the grind
            if (grind.Count == 0) return;
            y += gapH;
            y = Band(area, y, grindBandH, "This map tonight", $"{mapRuns} run{(mapRuns == 1 ? "" : "s")}", _ovSectionS);
            y = TableHeader(area, y, grindHeadH, new[] { "Best tonight", "Gap", "Runs", "Crashes" });
            for (int i = 0; i < grind.Count; i++)
            {
                var g = grind[i];
                var row = new Rect(area.x, y, area.width, rowH);
                if (row.yMax > area.yMax + 1) break;
                GUI.DrawTexture(row, i % 2 == 0 ? _texRowB : _texRowA);
                bool leader = i == 0 && g.Best < float.MaxValue;
                TableRow(row, g.Name, leader ? _ovSmallGold : _ovSmall,
                    Cell(g.Best < float.MaxValue ? Game.FormatTime(g.Best) : NoValue, leader),
                    Cell(g.Gap < float.MaxValue && g.Gap > 0 ? $"+{g.Gap:0.000}" : NoValue, false),
                    Cell(g.Runs.ToString(), false),
                    Cell(g.Crashes.ToString(), false));
                y += rowH;
            }
        }

        // ---------------- Results screen: right panel (replaces the game's Championship points) ----------------

        // The game's "Championship" column, as fractions of the screen (same on 16:9 and 21:9).
        private const float ChampLeft = 0.5815f;   // just right of the divider
        private const float ChampRight = 0.900f;   // right edge of the results panel

        private void DrawSidePanel(float vw, float vh)
        {
            var area = Rect.MinMaxRect(vw * ChampLeft, vh * GameColumnTop, vw * ChampRight, vh * GameColumnBottom);
            GUI.DrawTexture(area, _texBody);

            // Right side = this race and the evening.
            var highlights = _results.Highlights;
            var moments = _results.Moments;
            var standings = Session.Standings();

            const float bandH = 40, headH = 24, gapH = 10;
            float fixedH = bandH * 2 + headH * 2 + gapH;
            float units = highlights.Count + standings.Count + moments.Count * 0.8f;
            float rowH = Mathf.Clamp((area.height - fixedH) / Mathf.Max(1f, units), 22, 38);
            float y = area.y;

            // ---- This race
            y = Band(area, y, bandH, "This race", null, _ovSection);
            y = TableHeader(area, y, headH, new[] { "km/h", "Jump", "Hits", "KOs", "Wheels", "Cats" });
            float bestSpeed = highlights.Count > 0 ? highlights.Max(h => h.TopSpeed) : 0;
            float bestJump = highlights.Count > 0 ? highlights.Max(h => h.LongestJump) : 0;
            int bestHits = highlights.Count > 0 ? highlights.Max(h => h.Hits) : 0;
            int bestKos = highlights.Count > 0 ? highlights.Max(h => h.Knockouts) : 0;
            int mostWheels = highlights.Count > 0 ? highlights.Max(h => h.WheelsLost) : 0;
            int bestCats = highlights.Count > 0 ? highlights.Max(h => h.Cats) : 0;
            for (int i = 0; i < highlights.Count; i++)
            {
                var h = highlights[i];
                var row = new Rect(area.x, y, area.width, rowH);
                GUI.DrawTexture(row, i % 2 == 0 ? _texRowA : _texRowB);
                TableRow(row, h.Name, _ovSmall,
                    Cell(h.TopSpeed > 0 ? $"{h.TopSpeed:0}" : NoValue, h.TopSpeed > 0 && h.TopSpeed >= bestSpeed),
                    Cell(h.LongestJump >= 0.3f ? $"{h.LongestJump:0.0} s" : NoValue, h.LongestJump >= 0.3f && h.LongestJump >= bestJump),
                    Cell(h.Hits > 0 ? h.Hits.ToString() : NoValue, h.Hits > 0 && h.Hits >= bestHits),
                    Cell(h.Knockouts > 0 ? h.Knockouts.ToString() : NoValue, h.Knockouts > 0 && h.Knockouts >= bestKos),
                    Cell(h.WheelsLost > 0 ? $"-{h.WheelsLost}" : NoValue, h.WheelsLost > 0 && h.WheelsLost >= mostWheels), // "-2" = two wheels lost
                    Cell(h.Cats > 0 ? h.Cats.ToString() : NoValue, h.Cats > 0 && h.Cats >= bestCats));
                y += rowH;
            }
            foreach (var m in moments)
            {
                float mh = rowH * 0.8f;
                ShadowLabel(new Rect(area.x + 16, y, area.width - 32, mh), m, _ovMoment);
                y += mh;
            }
            y += gapH;

            // ---- Tonight: the evening's standings
            int maps = Session.MapsTonight;
            y = Band(area, y, bandH, "Tonight", $"{maps} map{(maps == 1 ? "" : "s")}, {Session.RunsTonight} runs", _ovSection);
            y = TableHeader(area, y, headH, new[] { "Fastest on", "PBs", "Records", "Crashes", "Hits" });
            for (int i = 0; i < standings.Count; i++)
            {
                var s = standings[i];
                var row = new Rect(area.x, y, area.width, rowH);
                if (row.yMax > area.yMax + 1) break;
                GUI.DrawTexture(row, i % 2 == 0 ? _texRowA : _texRowB);
                bool leader = i == 0 && s.BestTimes > 0;
                TableRow(row, s.Name, leader ? _ovSmallGold : _ovSmall,
                    Cell(s.BestTimes.ToString(), leader),
                    Cell(s.Stats.PersonalBests.ToString(), false),
                    Cell(s.Stats.Records.ToString(), false),
                    Cell(s.Stats.Crashes.ToString(), false),
                    Cell(s.Stats.Hits.ToString(), false));
                y += rowH;
            }
            if (standings.Count == 0) ShadowLabel(new Rect(area.x, y, area.width, rowH), "Nobody with a profile has raced yet tonight", _ovCenter);
        }

        private float Band(Rect area, float y, float h, string title, string side, GUIStyle titleStyle)
        {
            var band = new Rect(area.x, y, area.width, h);
            GUI.DrawTexture(band, _texTitleBand);
            ShadowLabel(band, title, titleStyle);
            if (side != null) ShadowLabel(new Rect(band.x, band.y, band.width - 12, h), side, _ovTagDim);
            return y + h;
        }

        /// <summary>En dash for "nothing here", so it can't be mistaken for a minus sign (e.g. "-2" wheels lost).</summary>
        private const string NoValue = "\u2013";

        private struct CellValue { public string Text; public bool Best; }
        private static CellValue Cell(string text, bool best) => new CellValue { Text = text, Best = best };

        // Name column takes ~32% of the width; the five value columns share the rest.
        private const float NameColumn = 0.32f;

        private float TableHeader(Rect area, float y, float h, string[] columns)
        {
            float colW = area.width * (1 - NameColumn) / columns.Length;
            for (int c = 0; c < columns.Length; c++)
                ShadowLabel(new Rect(area.x + area.width * NameColumn + c * colW, y, colW, h), columns[c], _ovColHead);
            return y + h;
        }

        private void TableRow(Rect row, string name, GUIStyle nameStyle, params CellValue[] cells)
        {
            ShadowLabel(new Rect(row.x + 16, row.y, row.width * NameColumn - 16, row.height), name, nameStyle);
            float colW = row.width * (1 - NameColumn) / cells.Length;
            for (int c = 0; c < cells.Length; c++)
                ShadowLabel(new Rect(row.x + row.width * NameColumn + c * colW, row.y, colW, row.height), cells[c].Text,
                    cells[c].Best ? _ovCellGold : _ovCell);
        }

        private void DrawRow(Rect row, string rank, string name, string time, string tag, GUIStyle tagStyle, GUIStyle timeStyle, bool compact = false)
        {
            float h = row.height;
            ShadowLabel(new Rect(row.x + 16, row.y, 70, h), rank, compact ? _ovRankS : _ovRank);
            ShadowLabel(new Rect(row.x + 90, row.y, row.width * 0.42f, h), name, compact ? _ovNameS : _ovName);
            ShadowLabel(new Rect(row.x + row.width * 0.47f, row.y, row.width * 0.22f, h), time, timeStyle);
            if (!string.IsNullOrEmpty(tag))
                ShadowLabel(new Rect(row.x + row.width * 0.70f, row.y, row.width * 0.29f, h), tag, tagStyle);
        }

        /// <summary>A solid black frame just outside a rectangle (makes the F8 window stand out from the game).</summary>
        private void DrawOutline(Rect r, float t)
        {
            // Soft shadow down and to the right, so the window floats above the game.
            GUI.DrawTexture(new Rect(r.x + 10, r.y + 10, r.width + t, r.height + t), _texShadow);
            GUI.DrawTexture(new Rect(r.x - t, r.y - t, r.width + 2 * t, t), _texBlack);  // top
            GUI.DrawTexture(new Rect(r.x - t, r.yMax, r.width + 2 * t, t), _texBlack);   // bottom
            GUI.DrawTexture(new Rect(r.x - t, r.y, t, r.height), _texBlack);             // left
            GUI.DrawTexture(new Rect(r.xMax, r.y, t, r.height), _texBlack);              // right
        }

        private static void ShadowLabel(Rect rect, string text, GUIStyle style)
        {
            if (string.IsNullOrEmpty(text)) return;
            var color = style.normal.textColor;
            style.normal.textColor = new Color(0.25f, 0.02f, 0.02f, 0.75f * GUI.color.a);
            GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), text, style);
            style.normal.textColor = color;
            GUI.Label(rect, text, style);
        }

        private void RefreshPopupData()
        {
            var gm = FindObjectOfType<GameMaster>();
            var records = Game.CurrentRecords(gm);
            _popupRanked = records?.Ranked() ?? new List<BestTime>();
            _popupLevelName = records?.LevelName ?? Game.CurrentLevelName(gm);
        }

        private void DrawPreRace(float vw)
        {
            TryLoadGameFont();
            var ranked = _popupRanked;
            int count = Mathf.Min(Plugin.PreRaceTopCount.Value, ranked.Count);
            const float width = 640, headerH = 60, rowH = 42;
            float height = headerH + Mathf.Max(1, count) * rowH;
            var rect = new Rect((vw - width) / 2, 36, width, height);

            // Fade out during the last second.
            float alpha = Mathf.Clamp01(_popupUntil - Time.unscaledTime);
            var old = GUI.color;
            GUI.color = new Color(1, 1, 1, alpha);

            var header = new Rect(rect.x, rect.y, rect.width, headerH);
            GUI.DrawTexture(header, _texHeader);
            ShadowLabel(new Rect(header.x + 20, header.y, header.width - 40, headerH), _popupLevelName, _ovHeaderLeft);
            ShadowLabel(new Rect(header.x + 20, header.y, header.width - 40, headerH), $"Top {Plugin.PreRaceTopCount.Value}", _ovHeaderRight);

            float y = header.yMax;
            if (count == 0)
            {
                var row = new Rect(rect.x, y, rect.width, rowH);
                GUI.DrawTexture(row, _texRowA);
                ShadowLabel(row, "No records yet. First finish sets the bar!", _ovCenter);
            }
            for (int i = 0; i < count; i++)
            {
                var b = ranked[i];
                var row = new Rect(rect.x, y, rect.width, rowH);
                GUI.DrawTexture(row, i % 2 == 0 ? _texRowA : _texRowB);
                DrawRow(row, $"#{i + 1}", Store.DisplayName(b), Game.FormatTime(b.Time), "", _ovTagDim, _ovTime);
                y += rowH;
            }
            GUI.color = old;
        }

        private void DrawRecord(float vw)
        {
            TryLoadGameFont();
            const float width = 640, headerH = 60, rowH = 42;
            bool hasPrevious = !string.IsNullOrEmpty(_recordPrevName) && _recordPrevTime < float.MaxValue;
            var rect = new Rect((vw - width) / 2, 36, width, headerH + rowH * (hasPrevious ? 2 : 1));

            var old = GUI.color;
            GUI.color = new Color(1, 1, 1, Mathf.Clamp01(_recordUntil - Time.unscaledTime));

            var header = new Rect(rect.x, rect.y, rect.width, headerH);
            GUI.DrawTexture(header, _texHeader);
            ShadowLabel(new Rect(header.x + 20, header.y, header.width - 40, headerH), "NEW MAP RECORD!", _ovHeaderRecord);

            var row = new Rect(rect.x, header.yMax, rect.width, rowH);
            GUI.DrawTexture(row, _texRowA);
            DrawRow(row, "#1", _recordName, Game.FormatTime(_recordTime), "", _ovTagDim, _ovTime);

            if (hasPrevious)
            {
                var prev = new Rect(rect.x, row.yMax, rect.width, rowH);
                GUI.DrawTexture(prev, _texRowB);
                float gain = _recordPrevTime - _recordTime;
                ShadowLabel(prev, $"beat {_recordPrevName} ({Game.FormatTime(_recordPrevTime)}) by {gain:0.000} s", _ovCenter);
            }
            GUI.color = old;
        }

        // ---------------- Styles ----------------

        // Colours taken from the game's results screen.
        private static readonly Color HeaderRed = new Color(0.545f, 0f, 0f, 1f);
        private static readonly Color WindowRed = new Color(0.38f, 0.04f, 0.04f, 1f);
        private static readonly Color Salmon = new Color(1f, 0.43f, 0.42f, 1f);
        private static readonly Color RowA = new Color(0.95f, 0.45f, 0.35f, 1f);
        private static readonly Color RowB = new Color(1f, 0.58f, 0.36f, 1f);
        private static readonly Color TimeYellow = new Color(1f, 0.9f, 0.2f, 1f);
        private static readonly Color ButtonMuted = new Color(0.69f, 0.33f, 0.33f, 1f);
        private static readonly Color ButtonOrange = new Color(1f, 0.55f, 0f, 1f);

        private Texture2D _texBody, _texTitleBand, _texRowA, _texRowB, _texHeader, _texAccent, _texBarBack, _texBlack, _texShadow;
        private GUIStyle _groupHeader, _statValue, _barLabel, _ovBand, _ovSmall, _ovSmallGold, _ovCell, _ovCellGold, _ovColHead, _ovMoment,
            _ovRankS, _ovNameS, _ovTimeS, _ovTitleMid, _ovSectionS;
        private GUIStyle[] _medals;
        private GUIStyle _ovTitle, _ovSection, _ovRank, _ovName, _ovTime, _ovTimeSmall, _ovTimeDim, _ovTagGold, _ovTagGreen, _ovTagDim,
            _ovHeaderLeft, _ovHeaderRight, _ovHeaderRecord, _ovCenter;
        private Font _gameFont;
        private bool _gameFontTried;

        private static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        /// <summary>Borrows the font of the game's results screen title, if it is available at runtime.</summary>
        private void TryLoadGameFont()
        {
            if (_gameFontTried) return;
            var ui = FindObjectOfType<RaceResultsUI>();
            if (ui == null) return; // try again later
            _gameFontTried = true;
            try
            {
                var title = Access.Get<TMPro.TMP_Text>(ui, "header_title");
                _gameFont = title?.font?.sourceFontFile;
            }
            catch { _gameFont = null; }
            Plugin.Debug(_gameFont != null ? $"Using game font '{_gameFont.name}' for overlays." : "Game font not available; using default font.");
            if (_gameFont != null)
                foreach (var s in new[] { _ovTitle, _ovSection, _ovRank, _ovName, _ovTime, _ovTimeSmall, _ovTimeDim, _ovTagGold, _ovTagGreen, _ovTagDim, _ovHeaderLeft, _ovHeaderRight, _ovHeaderRecord, _ovCenter, _ovBand,
                         _ovSmall, _ovSmallGold, _ovCell, _ovCellGold, _ovColHead, _ovMoment,
                         _ovRankS, _ovNameS, _ovTimeS, _ovTitleMid, _ovSectionS })
                    s.font = _gameFont;
        }

        private void EnsureStyles()
        {
            if (_stylesReady && _panelTex != null) return;

            _panelTex = Tex(WindowRed);
            _texBody = Tex(new Color(0.93f, 0.42f, 0.32f, 1f));
            _texTitleBand = Tex(Salmon);
            _texRowA = Tex(RowA);
            _texRowB = Tex(RowB);
            _texHeader = Tex(HeaderRed);
            _texAccent = Tex(ButtonOrange);
            _texBarBack = Tex(new Color(0.2f, 0.02f, 0.02f, 1f));
            _texBlack = Tex(Color.black);
            _texShadow = Tex(new Color(0f, 0f, 0f, 0.45f));

            _panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(22, 22, 16, 16), border = new RectOffset(0, 0, 0, 0) };
            _panel.normal.background = _panelTex;

            _row = new GUIStyle { padding = new RectOffset(10, 10, 6, 6) };
            _row.normal.background = Tex(new Color(1f, 1f, 1f, 0.07f));

            _label = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = false };
            _label.normal.textColor = Color.white;
            _title = new GUIStyle(_label) { fontSize = 30, fontStyle = FontStyle.Bold };
            _header = new GUIStyle(_label) { fontSize = 24, fontStyle = FontStyle.Bold };
            _small = new GUIStyle(_label) { fontSize = 17, wordWrap = true };
            _small.normal.textColor = new Color(1f, 0.85f, 0.8f);
            _dim = new GUIStyle(_label) { fontSize = 20 };
            _dim.normal.textColor = new Color(1f, 0.7f, 0.65f);
            _gold = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            _gold.normal.textColor = TimeYellow;
            _green = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            _green.normal.textColor = new Color(0.55f, 1f, 0.55f);

            // Buttons like the game's: muted red, orange when hovered/selected.
            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 20, fontStyle = FontStyle.Bold, padding = new RectOffset(12, 12, 6, 6),
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(4, 4, 3, 3),
            };
            var muted = Tex(ButtonMuted);
            var orange = Tex(ButtonOrange);
            var pressed = Tex(new Color(0.85f, 0.45f, 0f, 1f));
            _button.normal.background = muted;
            _button.hover.background = orange;
            _button.active.background = pressed;
            _button.focused.background = muted;
            foreach (var st in new[] { _button.normal, _button.hover, _button.active, _button.focused }) st.textColor = Color.white;

            _tabOff = new GUIStyle(_button) { fontSize = 20 };
            _tabOn = new GUIStyle(_tabOff);
            _tabOn.normal = new GUIStyleState { background = orange, textColor = Color.white };
            _tabOn.hover = _tabOn.normal;

            _field = new GUIStyle(GUI.skin.textField) { fontSize = 22, padding = new RectOffset(8, 8, 6, 6), border = new RectOffset(0, 0, 0, 0) };
            var fieldTex = Tex(new Color(0.2f, 0.02f, 0.02f, 1f));
            var fieldFocus = Tex(new Color(0.28f, 0.04f, 0.04f, 1f));
            _field.normal.background = fieldTex;
            _field.hover.background = fieldTex;
            _field.focused.background = fieldFocus;
            foreach (var st in new[] { _field.normal, _field.hover, _field.focused, _field.active }) st.textColor = Color.white;

            // Overlay (results screen / popup) styles.
            var ov = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            ov.normal.textColor = Color.white;
            _ovTitle = new GUIStyle(ov) { fontSize = 44, alignment = TextAnchor.MiddleCenter };
            _ovSection = new GUIStyle(ov) { fontSize = 28, alignment = TextAnchor.MiddleCenter };
            _ovRank = new GUIStyle(ov) { fontSize = 28 };
            _ovName = new GUIStyle(ov) { fontSize = 26 };
            _ovTime = new GUIStyle(ov) { fontSize = 26 };
            _ovTime.normal.textColor = TimeYellow;
            _ovTimeSmall = new GUIStyle(_ovTime) { fontSize = 24 };
            _ovTimeDim = new GUIStyle(_ovTime);
            _ovTimeDim.normal.textColor = new Color(1f, 0.85f, 0.75f);
            _ovTagGold = new GUIStyle(ov) { fontSize = 22, alignment = TextAnchor.MiddleRight };
            _ovTagGold.normal.textColor = TimeYellow;
            _ovTagGreen = new GUIStyle(_ovTagGold);
            _ovTagGreen.normal.textColor = Color.white;
            _ovTagDim = new GUIStyle(_ovTagGold) { fontSize = 18, fontStyle = FontStyle.Normal };
            _ovTagDim.normal.textColor = new Color(1f, 0.92f, 0.88f);
            _ovHeaderLeft = new GUIStyle(ov) { fontSize = 32 };
            _ovHeaderRight = new GUIStyle(ov) { fontSize = 30, alignment = TextAnchor.MiddleRight };
            _ovHeaderRight.normal.textColor = TimeYellow;
            _ovHeaderRecord = new GUIStyle(ov) { fontSize = 34, alignment = TextAnchor.MiddleCenter };
            _ovHeaderRecord.normal.textColor = TimeYellow;
            _ovCenter = new GUIStyle(ov) { fontSize = 24, alignment = TextAnchor.MiddleCenter };

            _groupHeader = new GUIStyle(_label) { fontSize = 24, fontStyle = FontStyle.Bold };
            _groupHeader.normal.textColor = TimeYellow;
            _statValue = new GUIStyle(_label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            _barLabel = new GUIStyle(_label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            var gold = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            gold.normal.textColor = TimeYellow;
            var silver = new GUIStyle(gold);
            silver.normal.textColor = new Color(0.88f, 0.9f, 0.95f);
            var bronze = new GUIStyle(gold);
            bronze.normal.textColor = new Color(0.95f, 0.65f, 0.4f);
            _medals = new[] { gold, silver, bronze };
            _ovBand = new GUIStyle(_ovCenter) { fontSize = 22 };
            _ovBand.normal.textColor = TimeYellow;

            // Side panel: a bit smaller than the game's text, still easy to read from the couch.
            _ovSmall = new GUIStyle(ov) { fontSize = 23 };
            _ovSmallGold = new GUIStyle(_ovSmall);
            _ovSmallGold.normal.textColor = TimeYellow;
            _ovCell = new GUIStyle(ov) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            _ovCellGold = new GUIStyle(_ovCell);
            _ovCellGold.normal.textColor = TimeYellow;
            _ovColHead = new GUIStyle(ov) { fontSize = 16, fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleCenter };
            _ovColHead.normal.textColor = new Color(1f, 0.92f, 0.88f);
            _ovMoment = new GUIStyle(ov) { fontSize = 20, fontStyle = FontStyle.Italic };
            // Compact left-side rows (results, records, grind share one column).
            _ovRankS = new GUIStyle(_ovRank) { fontSize = 22 };
            _ovNameS = new GUIStyle(_ovName) { fontSize = 22 };
            _ovTimeS = new GUIStyle(_ovTime) { fontSize = 22 };
            _ovTitleMid = new GUIStyle(ov) { fontSize = 30, alignment = TextAnchor.MiddleCenter };
            _ovSectionS = new GUIStyle(ov) { fontSize = 26, alignment = TextAnchor.MiddleCenter };
            _ovMoment.normal.textColor = new Color(1f, 0.95f, 0.8f);

            _stylesReady = true;
        }
    }
}
