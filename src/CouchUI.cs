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
        public enum Tab { Players, Profiles, Leaderboard, Stats, Fame }

        public static CouchUI Instance { get; private set; }

        private const float BaseHeight = 1080f;
        private const int WindowId = 0x0C0C;

        // Main window
        private bool _open;
        private Tab _tab = Tab.Players;
        private Rect _windowRect = new Rect(80, 80, 900, 860);
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
        private string _statsProfileId;
        private bool _lbAllRuns;        // Leaderboard tab: all runs instead of best per player
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
                try { ZeepSDK.Messaging.MessengerApi.LogSuccess($"Couch Party loaded. Press {Plugin.ToggleWindowKey.Value}", 5f); }
                catch (Exception e) { Plugin.Debug("Messenger not available: " + e.Message); }
            }

            if (Input.GetKeyDown(Plugin.ToggleWindowKey.Value))
            {
                _open = !_open;
                _pickingForSlot = -1;
                _friendPicker = false;
            }

            if (InGameScene) StatsTracker.Tick();

            // F9: on the results screen, switch best-per-player / all runs.
            // Otherwise show the top times for a few seconds (press again to hide). Only while racing.
            if (Input.GetKeyDown(Plugin.ShowRecordsKey.Value) && InGameScene)
            {
                if (_results != null) _resultsAllRuns = !_resultsAllRuns;
                else if (PopupVisible) _popupUntil = 0;
                else ShowPreRace();
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

            if (_results != null) DrawResults(vw, vh);
            if (Time.unscaledTime < _recordUntil) DrawRecord(vw);
            else if (_results == null && PopupVisible) DrawPreRace(vw);

            if (_open)
            {
                _windowRect.width = Mathf.Min(_windowRect.width, vw - 20);
                _windowRect.height = Mathf.Min(_windowRect.height, vh - 20);
                _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, "", _panel);
                _windowRect.x = Mathf.Clamp(_windowRect.x, 0, vw - _windowRect.width);
                _windowRect.y = Mathf.Clamp(_windowRect.y, 0, vh - _windowRect.height);
            }

            // Keep clicks and key presses from reaching the game while the mouse is over our window or a text box has focus.
            if (Event.current.type == EventType.Repaint)
            {
                var mouse = new Vector2(Input.mousePosition.x / scale, (Screen.height - Input.mousePosition.y) / scale);
                string focused = GUI.GetNameOfFocusedControl();
                bool typing = focused == NewNameControl || focused == RenameControl || focused == SearchControl;
                _blockInput = _open && (_windowRect.Contains(mouse) || typing);
            }
        }

        // ---------------- Main window ----------------

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Couch Party", _title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", _button, GUILayout.Width(110))) _open = false;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            TabButton("Players", Tab.Players);
            TabButton("Profiles", Tab.Profiles);
            TabButton("Leaderboard", Tab.Leaderboard);
            TabButton("Stats", Tab.Stats);
            TabButton("Hall of Fame", Tab.Fame);
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
            }
            GUILayout.EndScrollView();

            GUILayout.Label($"{Plugin.ToggleWindowKey.Value}: open/close this window   {Plugin.ShowRecordsKey.Value}: show this map's top times (races are always recorded)", _small);
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
                        if (GUILayout.Button("Really delete?", _button, GUILayout.Width(170))) { Store.DeleteProfile(p); _confirmDeleteId = null; Game.ApplyNames(); }
                        if (GUILayout.Button("No", _button, GUILayout.Width(60))) _confirmDeleteId = null;
                    }
                    else if (GUILayout.Button("Delete", _button, GUILayout.Width(100))) _confirmDeleteId = p.Id;
                }
                GUILayout.EndHorizontal();

                var st = p.Stats;
                GUILayout.Label($"{st.Races} races  |  {st.Wins} wins  |  {RecordsHeld(p)} map records held", _small);
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
                p.Name = _renameText.Trim();
                Store.SaveProfiles();
                Game.ApplyNames();
                _renamingId = null;
            }
            else if (cancel) _renamingId = null;
        }

        private void AddProfile(string name, ulong steamId)
        {
            Store.Profiles.Profiles.Add(new Profile { Name = name, SteamId = steamId, OutfitVersion = 2 });
            Store.SaveProfiles();
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

        private static int WinRate(PlayerStats st) => st.Races > 0 ? Mathf.RoundToInt(100f * st.Wins / st.Races) : 0;

        private List<Profile> ProfilesWithStats() =>
            Store.Profiles.Profiles.Where(p => p.Stats != null && p.Stats.Races > 0)
                .OrderByDescending(p => p.Stats.Wins).ThenByDescending(p => p.Stats.Races).ToList();

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

            var st = selected.Stats;
            GUILayout.BeginVertical(_row);
            GUILayout.BeginHorizontal();
            GUILayout.Label(selected.Name, _title);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{st.Wins} wins in {st.Races} races", _gold);
            GUILayout.EndHorizontal();
            ProgressBar($"Win rate {WinRate(st)}%", WinRate(st) / 100f);
            GUILayout.Space(8);

            float col = (_windowRect.width - 90) / 2f;
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(col));
            StatGroup("Racing");
            StatRow("Races", st.Races);
            StatRow("Wins", st.Wins);
            StatRow("Finishes", st.Finishes);
            StatRow("DNFs", st.Dnfs);
            StatRow("Shortcut tries", st.Shortcuts);
            GUILayout.Space(8);
            StatGroup("Records");
            StatRow("Map records held", RecordsHeld(selected));
            StatRow("Records broken", st.RecordsBroken);
            StatRow("Personal bests", st.PersonalBests);
            StatRow("Time on the clock", Game.FormatDuration(st.FinishTimeSeconds));
            GUILayout.Space(8);
            StatGroup("Bumper cars");
            StatRow("Bumps", st.Bumps);
            StatRow("Rams", st.Rams);
            StatRow("Got rammed", st.GotRammed);
            StatRow("Knockouts", st.Knockouts);
            GUILayout.EndVertical();

            GUILayout.Space(20);
            GUILayout.BeginVertical(GUILayout.Width(col));
            StatGroup("Carnage");
            StatRow("Crashes", st.Crashes);
            StatRow("Wheels lost", st.WheelsLost);
            StatRow("3-wheel finishes", st.ThreeWheelFinishes);
            StatRow("2-wheel finishes", st.TwoWheelFinishes);
            StatRow("1-wheel finishes", st.OneWheelFinishes);
            StatRow("No-wheel finishes", st.ZeroWheelFinishes);
            StatRow("On foot finishes", st.OnFootFinishes);
            GUILayout.Space(8);
            StatGroup("Driving");
            StatRow("Distance driven", Km(st.DistanceMeters));
            StatRow("Distance per race", Km(st.Races > 0 ? st.DistanceMeters / st.Races : 0));
            StatRow("Top speed", $"{st.TopSpeedKmh:0} km/h");
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(10);
            if (_confirmResetStats)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Reset everyone's stats? (Map records are kept.)", _label);
                if (GUILayout.Button("Yes, reset", _button, GUILayout.Width(150)))
                {
                    foreach (var p in Store.Profiles.Profiles) p.Stats = new PlayerStats();
                    Store.SaveProfiles();
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
                ("Most wins", "Most races won", p => p.Stats.Wins, v => $"{v:0} wins"),
                ("Best win rate", "Highest share of races won (3+ races)", p => p.Stats.Races >= 3 ? WinRate(p.Stats) : 0, v => $"{v:0}%"),
                ("Record hoarder", "Holds the most map records right now", p => RecordsHeld(p), v => $"{v:0} maps"),
                ("Record breaker", "Broke an existing map record most often", p => p.Stats.RecordsBroken, v => $"{v:0} records"),
                ("PB machine", "Beat their own best time most often", p => p.Stats.PersonalBests, v => $"{v:0} PBs"),
                ("Speed demon", "Highest top speed ever", p => p.Stats.TopSpeedKmh, v => $"{v:0} km/h"),
                ("Road tripper", "Longest total distance driven", p => p.Stats.DistanceMeters, v => Km(v)),
                ("Crash king", "Crashed the most times", p => p.Stats.Crashes, v => $"{v:0} crashes"),
                ("Wheel donor", "Lost the most wheels", p => p.Stats.WheelsLost, v => $"{v:0} wheels"),
                ("Limping home", "Most finishes with wheels missing", p => p.Stats.DamagedFinishes, v => $"{v:0} finishes"),
                ("No-wheel hero", "Most finishes with no wheels left", p => p.Stats.ZeroWheelFinishes, v => $"{v:0} finishes"),
                ("Stuntman", "Most finishes on foot, after ejecting from the car", p => p.Stats.OnFootFinishes, v => $"{v:0} on foot finishes"),
                ("Shortcut hunter", "Crossed the finish but missed checkpoints most often", p => p.Stats.Shortcuts, v => $"{v:0} tries"),
                ("Bumper car champ", "Drove into other racers the most", p => p.Stats.Rams, v => $"{v:0} rams"),
                ("Demolition derby", "Most knockouts: a racer crashed within 2 s of bumping into them", p => p.Stats.Knockouts, v => $"{v:0} knockouts"),
                ("Punching bag", "Got rammed by other racers the most", p => p.Stats.GotRammed, v => $"{v:0} times"),
            };

            float col = (_windowRect.width - 90) / 2f;
            for (int i = 0; i < categories.Count; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(i + 2, categories.Count); j++)
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
                    if (j == i) GUILayout.Space(10);
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(8);
            }
        }

        private static string Medal(int place) => place == 0 ? "1st" : place == 1 ? "2nd" : "3rd";

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
                            Store.Leaderboard.Levels.Remove(kv.Key);
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

        // The game's results screen, measured in 1080p units relative to the screen centre (16:9).
        private const float GameColumnLeft = -768f;  // left edge of the "Race Results" column
        private const float GameColumnRight = 151f;  // divider between "Race Results" and "Championship"
        private const float GameColumnTop = 238f;
        private const float GameColumnBottom = 843f;

        private void DrawResults(float vw, float vh)
        {
            TryLoadGameFont();
            float cx = vw / 2f;
            var area = new Rect(cx + GameColumnLeft, GameColumnTop, GameColumnRight - GameColumnLeft, GameColumnBottom - GameColumnTop);
            if (area.x < 8) { area.width -= 8 - area.x; area.x = 8; }

            GUI.DrawTexture(area, _texBody);

            var titleRect = new Rect(area.x, area.y, area.width, 60);
            GUI.DrawTexture(titleRect, _texTitleBand);
            ShadowLabel(titleRect, "Race Results", _ovTitle);

            var recordLine = _results.Lines.FirstOrDefault(l => l.NewMapRecord);
            bool showRecordBand = recordLine != null && _results.PreviousRecordTime < float.MaxValue;

            int lines = _results.Lines.Count;
            int topCount = Plugin.ResultsTopCount.Value;
            int top = Mathf.Min(topCount, _resultsAllRuns ? _results.AllRuns.Count : _results.AllTime.Count);

            float y = titleRect.yMax;
            const float sectionH = 40, bandH = 40;
            float remaining = area.yMax - y - sectionH - (showRecordBand ? bandH : 0);
            float raceRowH = Mathf.Clamp(remaining / Mathf.Max(1, lines + Mathf.Max(1, top)), 26, 44);
            float recordRowH = Mathf.Clamp((remaining - lines * raceRowH) / Mathf.Max(1, top), 24, 44);

            // This race.
            int pos = 0;
            for (int i = 0; i < lines; i++)
            {
                var line = _results.Lines[i];
                var row = new Rect(area.x, y, area.width, raceRowH);
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
                    line.Finished ? _ovTime : _ovTimeDim);
                y += raceRowH;
            }

            // "X beat Y's record by Z s"
            if (showRecordBand)
            {
                var band = new Rect(area.x, y, area.width, bandH);
                GUI.DrawTexture(band, _texHeader);
                float gain = _results.PreviousRecordTime - recordLine.Time;
                string text = _results.PreviousRecordName == recordLine.Name
                    ? $"{recordLine.Name} beat their own record by {gain:0.000} s"
                    : $"{recordLine.Name} beat {_results.PreviousRecordName}'s {Game.FormatTime(_results.PreviousRecordTime)} by {gain:0.000} s";
                ShadowLabel(band, text, _ovBand);
                y += bandH;
            }

            // Map records: best per player (default) or all runs. Click the header or press F9 to switch.
            var sectionRect = new Rect(area.x, y, area.width, sectionH);
            GUI.DrawTexture(sectionRect, _texTitleBand);
            string view = _resultsAllRuns ? "all runs" : "best per player";
            ShadowLabel(sectionRect, $"Map Records: {view}", _ovSection);
            ShadowLabel(new Rect(sectionRect.x, sectionRect.y, sectionRect.width - 12, sectionH), $"{Plugin.ShowRecordsKey.Value}: switch", _ovTagDim);
            if (GUI.Button(sectionRect, GUIContent.none, GUIStyle.none)) _resultsAllRuns = !_resultsAllRuns;
            y = sectionRect.yMax;

            if (top == 0) ShadowLabel(new Rect(area.x, y, area.width, 44), "No valid times yet", _ovCenter);
            for (int i = 0; i < top; i++)
            {
                var row = new Rect(area.x, y, area.width, recordRowH);
                if (row.yMax > area.yMax + 1) break;
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
                DrawRow(row, $"{i + 1}.", name, Game.FormatTime(time), fresh ? "new!" : "", _ovTagGold, i == 0 ? _ovTime : _ovTimeSmall);
                y += recordRowH;
            }
        }

        private void DrawRow(Rect row, string rank, string name, string time, string tag, GUIStyle tagStyle, GUIStyle timeStyle)
        {
            float h = row.height;
            ShadowLabel(new Rect(row.x + 16, row.y, 70, h), rank, _ovRank);
            ShadowLabel(new Rect(row.x + 90, row.y, row.width * 0.42f, h), name, _ovName);
            ShadowLabel(new Rect(row.x + row.width * 0.47f, row.y, row.width * 0.22f, h), time, timeStyle);
            if (!string.IsNullOrEmpty(tag))
                ShadowLabel(new Rect(row.x + row.width * 0.70f, row.y, row.width * 0.29f, h), tag, tagStyle);
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
        private static readonly Color WindowRed = new Color(0.38f, 0.04f, 0.04f, 0.97f);
        private static readonly Color Salmon = new Color(1f, 0.43f, 0.42f, 0.97f);
        private static readonly Color RowA = new Color(0.95f, 0.45f, 0.35f, 0.97f);
        private static readonly Color RowB = new Color(1f, 0.58f, 0.36f, 0.97f);
        private static readonly Color TimeYellow = new Color(1f, 0.9f, 0.2f, 1f);
        private static readonly Color ButtonMuted = new Color(0.69f, 0.33f, 0.33f, 1f);
        private static readonly Color ButtonOrange = new Color(1f, 0.55f, 0f, 1f);

        private Texture2D _texBody, _texTitleBand, _texRowA, _texRowB, _texHeader, _texAccent, _texBarBack;
        private GUIStyle _groupHeader, _statValue, _barLabel, _ovBand;
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
                foreach (var s in new[] { _ovTitle, _ovSection, _ovRank, _ovName, _ovTime, _ovTimeSmall, _ovTimeDim, _ovTagGold, _ovTagGreen, _ovTagDim, _ovHeaderLeft, _ovHeaderRight, _ovHeaderRecord, _ovCenter, _ovBand })
                    s.font = _gameFont;
        }

        private void EnsureStyles()
        {
            if (_stylesReady && _panelTex != null) return;

            _panelTex = Tex(WindowRed);
            _texBody = Tex(new Color(0.93f, 0.42f, 0.32f, 0.97f));
            _texTitleBand = Tex(Salmon);
            _texRowA = Tex(RowA);
            _texRowB = Tex(RowB);
            _texHeader = Tex(HeaderRed);
            _texAccent = Tex(ButtonOrange);
            _texBarBack = Tex(new Color(0.2f, 0.02f, 0.02f, 1f));

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

            _stylesReady = true;
        }
    }
}
