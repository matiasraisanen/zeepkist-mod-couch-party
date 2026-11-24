using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CouchParty
{
    /// <summary>
    /// Collects per-player stats during local multiplayer races by watching the game's per-player
    /// objects (ReadyToReset: one per player, with its car and character), and adds them to the
    /// players' profiles when the race is saved.
    /// </summary>
    internal static class StatsTracker
    {
        private class Live
        {
            public int Slot;
            public object Player;          // ReadyToReset
            public Component Car;          // New_ControlCar
            public Rigidbody Body;
            public object Character;       // DamageCharacterScript
            public List<Component> Wheels = new List<Component>();
            public bool[] WheelDead = new bool[0];
            public bool WasDead;
            public bool WasFinished;
            public int WheelsAtFinish = -1; // attached wheels when crossing the finish
            public bool DeadAtFinish;       // character had ejected/crashed when crossing the finish
            public Vector3? LastPos;

            public int Crashes, Ejects, Timeouts, WheelsLost;
            public double Distance;
            public float TopSpeed;

            // Bumper cars
            public int CarToCarHits, RammedOthers, GotRammed, Knockouts;
            public Vector3 LastVelocity;
            public int LastHitBy = -1;
            public float LastHitTime;
            public int WheelsKnockedOff;
            public int CatsPetted;
            // When things happened (unscaled time), so the race's moments can be listed in order.
            public float JumpAt, CatAt, WheelAt, RamAt, TopSpeedAt, FinishAt;
            public int CatCrashes;
            public bool CatArmed;        // hit a cat and not safely back on the ground yet
            public string DeathReason;   // from the game, e.g. "Gameplay_Crashed"
            public string OutReason;     // the game took them out (timeout)
            public string KillCaller;    // what the game said when killing the character (tells ejects from crashes)
            public bool KillForced;
            public float CatHitTime;

            // Knockout tracking: armed by a bump, cleared once they're safely back on the ground.
            public bool KoArmed;
            // Crashed (or ejected) while marked: the rammer's knockout, unless they still cross the finish on foot.
            // Decided when the race ends.
            public int KoPendingBy = -1;
            public float KoPendingAt;
            public List<object> CustomWheels = new List<object>(); // New_CustomWheel (has isGrounded)
            public float LastBodyContact = -10f;  // car body touching something that isn't another racer
            public float GroundedSince = -1f;      // -1 while airborne
            public float LastTrackContact = -10f;  // car body touching the track (not the map's ground floor)
            public float LastGroundFloorContact = -10f;
            public float OnTrackSince = -1f;       // -1 while not on the track (in the air or on the map's ground)
            public bool WasOnGroundFloor;

            // Air time
            public float AirStart = -1f;           // -1 while on the ground
            public Vector3 AirStartPos;            // where the jump started (for the debug log)
            public float AirPeakY;                 // highest point of the jump
            public double Air;
            public float LongestJump;
            public int BigJumps;

            // Stunts. The car's own axes (local space) are found once it's rolling straight, since the
            // model's "forward" isn't known in advance. Rotation is added up around them during each jump.
            public bool AxesKnown;
            public Vector3 Up, Forward, Right;
            public Quaternion LastRot;
            public bool HasLastRot;
            public float Pitch, Roll, Yaw;          // degrees this jump; + pitch = nose down = front flip
            public List<Stunt> Pending = new List<Stunt>(); // landed, waiting to see if they crash right after
            public int FrontFlips, Backflips, BarrelRolls, Spins180, Spins360, Spins720, Spins1080;
            public int BiggestSpin;                 // exact degrees of the biggest spin landed
            public int MostFlips, MostRolls;        // most flips / rolls landed in one jump
            public float StuntAt;
            public bool ReverseAtFinish;            // crossed the finish rear wheels first
        }

        /// <summary>One landed jump's tricks.</summary>
        private class Stunt
        {
            public int Front, Back, Rolls;
            public int Spin;        // the trick: 180, 360... up to 1080 (0 = no spin)
            public int SpinDegrees; // how far it actually turned
            public string Detail;   // for the debug log: air time and degrees around each axis
            public float Seconds;   // time in the air (for the longest jump)
            public bool BigJump;    // 1 s or more in the air
            public bool AtFinish;   // "landed" by crossing the finish mid-air: counts only for a valid finish
            public bool HasTricks => Front + Back + Rolls + Spin > 0;
            public float LandedAt;
            public string Name;
        }

        /// <summary>After landing, wait this long before speed counts towards top speed.</summary>
        private const float TopSpeedLandingGrace = 1f;
        /// <summary>Wheels that must touch the track for a speed to count as driving.</summary>
        private const int TopSpeedMinWheels = 2;

        /// <summary>Shorter hops than this aren't counted as air time.</summary>
        private const float MinJumpSeconds = 0.3f;
        /// <summary>Jumps at least this long count as big jumps (landed like a stunt).</summary>
        private const float BigJumpSeconds = 2f; // 1 s made every few seconds a "big jump" on normal maps (logs, 2026-11-24)

        /// <summary>Gentler touches than this (relative speed, m/s) don't count as a bump.</summary>
        private const float MinBumpSpeed = 3f;
        /// <summary>One bump per pair of players per this many seconds (no counting a long scrape many times).</summary>
        private const float BumpCooldown = 1f;
        /// <summary>A crash within this long after getting rammed (or hitting a cat) counts, if they stay on their wheels.</summary>
        private const float KnockoutWindow = 3f;
        /// <summary>After the window, they must have been on the ground this long to be "recovered" (longer if they're in the air).</summary>
        private const float RecoveredAfter = 1f;
        /// <summary>A possible knockout expires after this long no matter what.</summary>
        private const float MaxKnockoutSeconds = 15f;

        /// <summary>A stunt only counts if they don't crash this soon after landing.</summary>
        private const float StuntLandingGrace = 3f;
        /// <summary>How far short of a full flip or roll still counts (nobody lands at exactly 360).</summary>
        private const float FlipSlack = 60f;
        /// <summary>How far short of a half turn a spin can be and still count (135 = a 180, 315 = a 360).</summary>
        private const float SpinSlack = 45f;
        /// <summary>Spin tricks are named up to this (bigger ones are a "1080+"); the exact degrees are kept for the Hall of Fame.</summary>
        private const int MaxSpinTrick = 1080;
        /// <summary>Crossing the finish moving at least this far off the car's nose = finishing in reverse.</summary>
        private const float ReverseAngle = 120f;
        /// <summary>Speed (m/s) and max drift angle for working out which way the car model faces.</summary>
        private const float AxisDetectSpeed = 20f / 3.6f;
        private const float AxisDetectMaxAngle = 25f;

        private static readonly Dictionary<Rigidbody, int> BodyToSlot = new Dictionary<Rigidbody, int>();
        /// <summary>Colliders of the map's flat ground (GameMaster.groundFloor): touching it is "off the track".</summary>
        private static readonly HashSet<Collider> GroundFloor = new HashSet<Collider>();
        /// <summary>Players already out when the race started (ejected during the countdown), counted once per round.</summary>
        private static readonly HashSet<int> OutBeforeStart = new HashSet<int>();
        /// <summary>Notable things that happened this race (priority: lower = more interesting).</summary>
        private static readonly List<(int priority, float at, string text)> Moments = new List<(int, float, string)>();
        private static float _roundStart;

        /// <summary>Remembers a moment; <paramref name="at"/> is when it happened (unscaled time, default now).</summary>
        private static void AddMoment(int priority, string text, float at = -1f)
        {
            if (Moments.Any(m => m.text == text)) return;
            Moments.Add((priority, at >= 0 ? at : RaceTime, text));
        }

        private static float At(int slot, Func<Live, float> when) =>
            Slots.TryGetValue(slot, out var live) && when(live) > 0 ? when(live) : RaceTime;
        private static readonly Dictionary<int, float> PairLastBump = new Dictionary<int, float>();
        /// <summary>Last hit per (player, cat), so one hit counts once.</summary>
        private static readonly Dictionary<(int, int), float> CatLastHit = new Dictionary<(int, int), float>();
        private const float CatHitCooldown = 2f;

        /// <summary>Jumps in position bigger than this per frame (e.g. a reset) are not counted as driving.</summary>
        private const float MaxStepMeters = 25f;

        private static readonly Dictionary<int, Live> Slots = new Dictionary<int, Live>();
        private static bool _tracking;
        private static GameMaster _gm;
        private static float _nextRefresh;
        private static bool _loggedMapping;

        // ---------------- Round lifecycle ----------------

        public static void OnRoundStarted()
        {
            CommitLiveOnly(); // a round that was restarted without results still counts its crashes etc.
            Slots.Clear();
            BodyToSlot.Clear();
            GroundFloor.Clear();
            PairLastBump.Clear();
            OutBeforeStart.Clear();
            Moments.Clear();
            _roundStart = RaceTime;
            CatLastHit.Clear();
            _gm = null;
            _nextRefresh = 0;
            _tracking = Game.IsLocalMultiplayer();
        }

        public static void OnQuit()
        {
            CommitLiveOnly();
            Slots.Clear();
            _tracking = false;
        }

        /// <summary>Race saved: add live stats plus the result to each profiled player.</summary>
        public static void CommitRace(RaceSummary summary)
        {
            // Not saved (e.g. the map couldn't be worked out): the crashes, hits, stunts etc. still count.
            if (summary == null) { CommitLiveOnly(); _tracking = false; return; }

            SettleKnockouts(summary);
            SettleFinishStunts(summary);
            FillHighlights(summary);

            var winner = summary.Lines.FirstOrDefault(l => l.Finished);
            bool changed = false;
            foreach (var line in summary.Lines)
            {
                var profile = Store.GetSlotProfile(line.Slot);
                if (profile == null) continue;
                profile.MyStats.Updated = DateTime.UtcNow;
                // This PC's lifetime entry (totals are summed over all PCs) and tonight's stats get the same numbers.
                foreach (var st in new[] { profile.MyStats, Session.StatsFor(profile) })
                {
                    st.Races++;
                    if (line.Finished)
                    {
                        st.Finishes++;
                        st.FinishTimeSeconds += line.Time;
                        if (line == winner && summary.Lines.Count >= 2) st.Wins++;
                    }
                    else if (line.CrossedFinish) st.ShortcutFinishes++;
                    else st.Dnfs++;
                    if (line.Finished && Slots.TryGetValue(line.Slot, out var atFinish))
                    {
                        CountWheelFinish(st, atFinish);
                        if (atFinish.DeadAtFinish) st.OnFootFinishes++;
                        else if (atFinish.ReverseAtFinish) st.ReverseFinishes++;
                    }
                    if (line.NewPersonalBest) st.PersonalBests++;
                    if (line.NewMapRecord) st.RecordsBroken++;
                }
                if (Slots.TryGetValue(line.Slot, out var live))
                {
                    if (line.Finished && !live.DeadAtFinish && live.Wheels.Count >= 4 && live.WheelsAtFinish >= 0 && live.WheelsAtFinish < 4)
                        Plugin.Debug($"Player {live.Slot + 1} finished on {live.WheelsAtFinish} wheel(s)!");
                    AddLive(live, profile.MyStats, Session.StatsFor(profile));
                }
                changed = true;
            }
            // Someone tracked during the race but missing from the results: keep what they did anyway.
            foreach (var kv in Slots)
            {
                if (summary.Lines.Any(l => l.Slot == kv.Key)) continue;
                var profile = Store.GetSlotProfile(kv.Key);
                if (profile == null) continue;
                profile.MyStats.Updated = DateTime.UtcNow;
                AddLive(kv.Value, profile.MyStats, Session.StatsFor(profile));
                changed = true;
            }
            Slots.Clear();
            _tracking = false;
            if (changed)
            {
                Store.StatsChanged();
                Store.SaveProfiles();
                Session.StatsChanged();
            }
        }

        /// <summary>Copies this race's numbers (and its best moments) into the summary for the results screen.</summary>
        private static void FillHighlights(RaceSummary summary)
        {
            summary.Highlights.Clear();
            foreach (var line in summary.Lines)
            {
                Slots.TryGetValue(line.Slot, out var live);
                summary.Highlights.Add(new RaceHighlight
                {
                    Slot = line.Slot,
                    Name = line.Name,
                    TopSpeed = live?.TopSpeed ?? 0,
                    LongestJump = live?.LongestJump ?? 0,
                    Hits = live?.CarToCarHits ?? 0,
                    Knockouts = live?.Knockouts ?? 0,
                    Cats = live?.CatsPetted ?? 0,
                    Crashes = live?.Crashes ?? 0,
                    WheelsLost = live?.WheelsLost ?? 0,
                    Rams = live?.RammedOthers ?? 0,
                });
                if (line.NewPersonalBest && line.PreviousBest < float.MaxValue && line.PreviousBest - line.Time >= 0.5f)
                    AddMoment(2, $"{line.Name} improved their PB by {line.PreviousBest - line.Time:0.0} s", At(line.Slot, l => l.FinishAt));
                if (live == null || !line.Finished) continue;
                if (live.DeadAtFinish) AddMoment(1, $"{line.Name} finished on foot!", At(line.Slot, l => l.FinishAt));
                else if (live.ReverseAtFinish) AddMoment(1, $"{line.Name} finished backwards!", At(line.Slot, l => l.FinishAt));
                else if (live.WheelsAtFinish >= 0 && live.WheelsAtFinish < 4 && live.Wheels.Count >= 4)
                    AddMoment(2, $"{line.Name} finished on {live.WheelsAtFinish} wheel{(live.WheelsAtFinish == 1 ? "" : "s")}", At(line.Slot, l => l.FinishAt));
            }

            // Photo finish: the two fastest valid finishers within a quarter of a second.
            var finishers = summary.Lines.Where(l => l.Finished).OrderBy(l => l.Time).ToList();
            if (finishers.Count >= 2 && finishers[1].Time - finishers[0].Time < 0.25f)
                AddMoment(1, $"Photo finish! {finishers[0].Name} beat {finishers[1].Name} by {finishers[1].Time - finishers[0].Time:0.000} s",
                    At(finishers[1].Slot, l => l.FinishAt));

            var wheels = summary.Highlights.OrderByDescending(h => h.WheelsLost).FirstOrDefault();
            if (wheels != null && wheels.WheelsLost >= 2) AddMoment(3, $"{wheels.Name} lost {wheels.WheelsLost} wheels", At(wheels.Slot, l => l.WheelAt));
            var rams = summary.Highlights.OrderByDescending(h => h.Rams).FirstOrDefault();
            if (rams != null && rams.Rams >= 2) AddMoment(3, $"{rams.Name} rammed others {rams.Rams} times", At(rams.Slot, l => l.RamAt));

            // Fill up with the race's showpieces: the biggest jump and the most cats.
            var jump = summary.Highlights.OrderByDescending(h => h.LongestJump).FirstOrDefault();
            if (jump != null && jump.LongestJump >= BigJumpSeconds) AddMoment(3, $"{jump.Name} flew {jump.LongestJump:0.0} s", At(jump.Slot, l => l.JumpAt));
            var cats = summary.Highlights.OrderByDescending(h => h.Cats).FirstOrDefault();
            if (cats != null && cats.Cats >= 2) AddMoment(3, $"{cats.Name} petted {cats.Cats} cats", At(cats.Slot, l => l.CatAt));
            else if (cats != null && cats.Cats == 1) AddMoment(4, $"{cats.Name} petted a cat", At(cats.Slot, l => l.CatAt));

            var fastest = summary.Highlights.OrderByDescending(h => h.TopSpeed).FirstOrDefault();
            if (fastest != null && fastest.TopSpeed > 0) AddMoment(5, $"Fastest: {fastest.Name} {fastest.TopSpeed:0} km/h", At(fastest.Slot, l => l.TopSpeedAt));

            // The 5 most interesting moments,
            // listed in the order they happened, with the race time.
            const int maxMoments = 5;
            summary.Moments = Moments.OrderBy(m => m.priority).ThenBy(m => m.at).Take(maxMoments)
                .OrderBy(m => m.at)
                .Select(m => $"{RaceClock(m.at - _roundStart)}   {m.text}")
                .ToList();
        }

        private static string RaceClock(float seconds)
        {
            if (seconds < 0) seconds = 0;
            return $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";
        }

        private static void CommitLiveOnly()
        {
            SettleKnockouts(null);
            SettleFinishStunts(null);
            bool changed = false;
            foreach (var kv in Slots)
            {
                var profile = Store.GetSlotProfile(kv.Key);
                if (profile == null) continue;
                profile.MyStats.Updated = DateTime.UtcNow;
                AddLive(kv.Value, profile.MyStats, Session.StatsFor(profile));
                changed = true;
            }
            Slots.Clear();
            if (changed)
            {
                Store.StatsChanged();
                Store.SaveProfiles();
                Session.StatsChanged();
            }
        }

        /// <summary>Valid finish with wheels missing: bump the 3/2/1/0-wheel counter. Only when they drove over the line.</summary>
        private static void CountWheelFinish(PlayerStats st, Live live)
        {
            if (live.DeadAtFinish) return; // on foot: the car's wheels don't matter any more
            if (live.Wheels.Count < 4 || live.WheelsAtFinish < 0) return; // couldn't see the wheels
            switch (live.WheelsAtFinish)
            {
                case 3: st.ThreeWheelFinishes++; break;
                case 2: st.TwoWheelFinishes++; break;
                case 1: st.OneWheelFinishes++; break;
                case 0: st.ZeroWheelFinishes++; break;
            }
        }

        /// <summary>Adds the live counters to every target (lifetime and tonight), then clears them so they are counted once.</summary>
        private static void AddLive(Live live, params PlayerStats[] targets)
        {
            foreach (var st in targets) AddLiveTo(st, live);
            live.CatsPetted = live.CatCrashes = 0;
            live.Air = 0;
            live.BigJumps = 0;
            live.FrontFlips = live.Backflips = live.BarrelRolls = live.BiggestSpin = live.MostFlips = live.MostRolls = 0;
            live.Spins180 = live.Spins360 = live.Spins720 = live.Spins1080 = 0;
            live.Crashes = live.Ejects = live.Timeouts = live.WheelsLost = 0;
            live.CarToCarHits = live.RammedOthers = live.GotRammed = live.Knockouts = live.WheelsKnockedOff = 0;
            live.Distance = 0;
        }

        private static void AddLiveTo(PlayerStats st, Live live)
        {
            st.Crashes += live.Crashes;
            st.Ejects += live.Ejects;
            st.Timeouts += live.Timeouts;
            st.WheelsLost += live.WheelsLost;
            st.DistanceMeters += live.Distance;
            st.TopSpeedKmh = Mathf.Max(st.TopSpeedKmh, live.TopSpeed);
            st.CarToCarHits += live.CarToCarHits;
            st.RammedOthers += live.RammedOthers;
            st.GotRammed += live.GotRammed;
            st.Knockouts += live.Knockouts;
            st.WheelsKnockedOff += live.WheelsKnockedOff;
            st.CatsPetted += live.CatsPetted;
            st.CatCrashes += live.CatCrashes;
            st.AirSeconds += live.Air;
            st.LongestJumpSeconds = Mathf.Max(st.LongestJumpSeconds, live.LongestJump);
            st.BigJumps += live.BigJumps;
            st.FrontFlips += live.FrontFlips;
            st.Backflips += live.Backflips;
            st.BarrelRolls += live.BarrelRolls;
            st.MostFlipsInJump = Math.Max(st.MostFlipsInJump, live.MostFlips);
            st.MostRollsInJump = Math.Max(st.MostRollsInJump, live.MostRolls);
            st.Spins180 += live.Spins180; st.Spins360 += live.Spins360; st.Spins720 += live.Spins720; st.Spins1080 += live.Spins1080;
            st.BiggestSpin = Math.Max(st.BiggestSpin, live.BiggestSpin);
        }

        // ---------------- Per-frame tracking ----------------

        /// <summary>
        /// Game time, not real time: it stops while the game is paused (seen: pausing mid-air gave a "164 s jump").
        /// Every race timing (air time, knockout marks, stunt landings, cats, moments) uses it.
        /// </summary>
        private static float RaceTime => Time.time;
        private static bool _paused;

        public static void Tick()
        {
            if (!_tracking) return;
            bool paused = Time.timeScale == 0f;
            if (paused != _paused)
            {
                _paused = paused;
                Plugin.Debug(paused ? "Game paused: the race clock stops." : "Game resumed.");
            }
            try
            {
                if (_gm == null || RaceTime >= _nextRefresh)
                {
                    _nextRefresh = RaceTime + 1f;
                    if (_gm == null) _gm = UnityEngine.Object.FindObjectOfType<GameMaster>();
                    if (GroundFloor.Count == 0 && _gm != null)
                    {
                        var floor = Access.Get<GameObject>(_gm, "groundFloor");
                        if (floor != null)
                            foreach (var c in floor.GetComponentsInChildren<Collider>(true)) GroundFloor.Add(c);
                        Plugin.Debug($"Stats: map ground floor {(floor != null ? $"'{floor.name}' with {GroundFloor.Count} collider(s)" : "not found")}.");
                    }
                    Bind();
                }
                foreach (var kv in Slots) Sample(kv.Value);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Stats tracking stopped for this round: {e.Message}");
                _tracking = false;
            }
        }

        /// <summary>Finds each player's car, character and wheels (once per second until found).</summary>
        private static void Bind()
        {
            var players = Access.List(_gm, "PlayersReady");
            if (players == null) return;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null) continue;
                int index = Access.Get(p, "index", -1);
                int slot = index >= 0 && index < Game.MaxSlots ? index : i;

                bool isNew = !Slots.TryGetValue(slot, out var live) || !ReferenceEquals(live.Player, p);
                if (isNew)
                {
                    live = new Live { Player = p, Slot = slot };
                    live.Character = Access.Get<object>(p, "character");
                    live.WasDead = Access.Get(live.Character, "isdead", false);
                    Slots[slot] = live;
                    // Out before the start signal: nobody can crash while held at the start, so that's an eject.
                    if (live.WasDead && OutBeforeStart.Add(slot))
                    {
                        live.Crashes++;
                        live.Ejects++;
                        Plugin.Debug($"Player {slot + 1} crashed: eject (ejected before the start).");
                    }
                }

                // Find the car (and its wheels) if we don't have it yet; keep the counts gathered so far.
                if (live.Car == null)
                {
                    live.Car = Access.Get<Component>(p, "cc");
                    if (live.Car != null)
                    {
                        live.Body = live.Car.GetComponent<Rigidbody>() ?? live.Car.GetComponentInParent<Rigidbody>();
                        live.Wheels = live.Car.GetComponentsInChildren<DamageWheel>(true).Cast<Component>().ToList();
                        live.WheelDead = live.Wheels.Select(w => Access.Get(w, "isdead", false)).ToArray();
                        var custom = Access.List(live.Car, "customWheels");
                        if (custom != null) live.CustomWheels = custom.Cast<object>().Where(w => w != null).ToList();
                        if (live.CustomWheels.Count == 0)
                            live.CustomWheels = live.Wheels.Select(w => Access.Get<object>(w, "theActualWheel")).Where(w => w != null).ToList();
                    }
                    if (live.Body == null) live.Body = Access.Get<Rigidbody>(live.Character, "rb");
                    if (live.Car != null || isNew) WatchCollisions(slot, live);
                }

                if (!_loggedMapping)
                    Plugin.Debug($"Stats: PlayersReady[{i}] index={index} -> Player {slot + 1}, car={(live.Car != null)}, body={(live.Body != null)}, " +
                                 $"wheels={live.Wheels.Count}, ground sensors={live.CustomWheels.Count}");
            }
            _loggedMapping = true;
        }

        // ---------------- Bumper cars ----------------

        /// <summary>Maps every rigidbody of a player's car and character to their slot, and listens for hits on the car.</summary>
        private static void WatchCollisions(int slot, Live live)
        {
            var bodies = new List<Rigidbody>();
            if (live.Car != null) bodies.AddRange(live.Car.GetComponentsInChildren<Rigidbody>(true));
            if (live.Character is Component ch) bodies.AddRange(ch.GetComponentsInChildren<Rigidbody>(true));
            if (live.Body != null) bodies.Add(live.Body);
            foreach (var rb in bodies)
                if (rb != null) BodyToSlot[rb] = slot;

            if (live.Body == null) return;
            var reporter = live.Body.GetComponent<CarCollisionReporter>() ?? live.Body.gameObject.AddComponent<CarCollisionReporter>();
            reporter.Slot = slot;
        }

        /// <summary>Called by a car's <see cref="CarCollisionReporter"/> for every physics hit.</summary>
        public static void OnCarCollision(int slot, Collision collision)
        {
            if (!_tracking || collision == null) return;
            var otherBody = collision.rigidbody != null ? collision.rigidbody : collision.collider != null ? collision.collider.attachedRigidbody : null;
            if (otherBody == null) return;
            if (!BodyToSlot.TryGetValue(otherBody, out int other))
            {
                CheckCat(slot, otherBody, collision);
                return;
            }
            if (other == slot) return;
            if (collision.relativeVelocity.magnitude < MinBumpSpeed) return;
            if (!Slots.TryGetValue(slot, out var a) || !Slots.TryGetValue(other, out var b)) return;
            if (!StillRacing(a) || !StillRacing(b)) return; // finished, crashed or out: coasting wrecks don't count

            // Both cars report the same hit: count it once per pair per cooldown.
            int pair = Mathf.Min(slot, other) * 10 + Mathf.Max(slot, other);
            float now = RaceTime;
            if (PairLastBump.TryGetValue(pair, out float last) && now - last < BumpCooldown) return;
            PairLastBump[pair] = now;

            // The rammer is whoever was moving towards the other one faster (velocities from just before the hit).
            Vector3 posA = a.Body != null ? a.Body.position : Vector3.zero;
            Vector3 posB = b.Body != null ? b.Body.position : Vector3.zero;
            Vector3 dir = (posB - posA).normalized;
            float towardsB = Vector3.Dot(a.LastVelocity, dir);
            float towardsA = Vector3.Dot(b.LastVelocity, -dir);
            var rammer = towardsB >= towardsA ? a : b;
            var victim = rammer == a ? b : a;

            a.CarToCarHits++;
            b.CarToCarHits++;
            rammer.RammedOthers++;
            rammer.RamAt = now;
            victim.GotRammed++;
            // Only the rammed player is marked, with the rammer as the culprit. If the rammer crashes
            // afterwards, that's their own crash: nobody gets a knockout or a wheel for it.
            victim.LastHitBy = rammer == a ? slot : other;
            victim.LastHitTime = now;
            victim.KoArmed = true;
            Plugin.Debug($"Car-to-car hit: Player {slot + 1} ({a.LastVelocity.magnitude * 3.6f:0} km/h, {towardsB * 3.6f:0} towards the other) and " +
                         $"Player {other + 1} ({b.LastVelocity.magnitude * 3.6f:0} km/h, {towardsA * 3.6f:0} towards the other), " +
                         $"impact {collision.relativeVelocity.magnitude * 3.6f:0} km/h -> rammer: Player {(rammer == a ? slot : other) + 1}");
        }

        /// <summary>A car hit something that isn't a racer: was it one of the walking cats?</summary>
        private static void CheckCat(int slot, Rigidbody body, Collision collision)
        {
            var cat = body.GetComponentInParent<AnimalController>();
            if (cat == null || !Slots.TryGetValue(slot, out var live) || !StillRacing(live)) return;
            if (collision.relativeVelocity.magnitude < 1f) return; // resting against it doesn't count

            var key = (slot, cat.GetInstanceID());
            float now = RaceTime;
            if (CatLastHit.TryGetValue(key, out float last) && now - last < CatHitCooldown) return;
            CatLastHit[key] = now;

            live.CatsPetted++;
            live.CatAt = now;
            live.CatArmed = true;
            live.CatHitTime = now;
            Plugin.Debug($"Cat #{cat.GetInstanceID()} petted by Player {slot + 1} at {collision.relativeVelocity.magnitude * 3.6f:0} km/h!");
        }

        /// <summary>Car body is touching something (OnCollisionStay). Only the track/scenery counts as ground.</summary>
        public static void OnCarContact(int slot, Collision collision)
        {
            if (!_tracking || collision == null || !Slots.TryGetValue(slot, out var live)) return;
            var otherBody = collision.rigidbody != null ? collision.rigidbody : collision.collider != null ? collision.collider.attachedRigidbody : null;
            if (otherBody != null && BodyToSlot.ContainsKey(otherBody)) return; // another racer (or own parts) isn't the ground
            live.LastBodyContact = RaceTime;
            if (collision.collider != null && GroundFloor.Contains(collision.collider)) live.LastGroundFloorContact = RaceTime;
            else live.LastTrackContact = RaceTime;
        }

        /// <summary>
        /// On the track: a wheel touching something that isn't the map's ground floor, or the car body sliding on
        /// such a thing. Landing on the map's flat ground (after falling off) is not "on the track".
        /// </summary>
        private static bool IsOnTrack(Live live)
        {
            if (RaceTime - live.LastTrackContact < 0.25f) return true;
            foreach (var w in live.CustomWheels)
            {
                if (!Access.Get(w, "isGrounded", false)) continue;
                var hit = Access.Get<RaycastHit>(w, "hit");
                if (hit.collider == null || !GroundFloor.Contains(hit.collider)) return true;
            }
            return false;
        }

        /// <summary>Wheels touching the track right now (not the map's ground floor).</summary>
        private static int WheelsOnTrack(Live live)
        {
            int n = 0;
            foreach (var w in live.CustomWheels)
            {
                if (!Access.Get(w, "isGrounded", false)) continue;
                var hit = Access.Get<RaycastHit>(w, "hit");
                if (hit.collider == null || !GroundFloor.Contains(hit.collider)) n++;
            }
            return n;
        }

        private static bool IsOnGroundFloor(Live live)
        {
            if (RaceTime - live.LastGroundFloorContact < 0.25f) return true;
            foreach (var w in live.CustomWheels)
            {
                if (!Access.Get(w, "isGrounded", false)) continue;
                var hit = Access.Get<RaycastHit>(w, "hit");
                if (hit.collider != null && GroundFloor.Contains(hit.collider)) return true;
            }
            return false;
        }

        private static bool IsGrounded(Live live)
        {
            if (RaceTime - live.LastBodyContact < 0.25f) return true;
            foreach (var w in live.CustomWheels)
                if (Access.Get(w, "isGrounded", false)) return true;
            return false;
        }

        // ---------------- How a player's race ended (from the game) ----------------

        /// <summary>The game kills the character: remember who asked, which tells an eject from a crash.</summary>
        public static void NoteKill(object character, bool forced, string caller, string reason)
        {
            var live = Slots.Values.FirstOrDefault(l => ReferenceEquals(l.Character, character));
            if (live == null || live.WasDead || Access.Get(character, "isdead", false)) return; // only the first, fatal call
            live.KillCaller = caller;
            live.KillForced = forced;
            if (live.DeathReason == null) live.DeathReason = reason;
            Plugin.Debug($"Player {live.Slot + 1} kill request: caller '{caller}', forced {forced}, reason {reason}.");
        }

        /// <summary>
        /// Eject (pressed Y): the race code (ReadyToReset) kills the character with death reason "crashed".
        /// Hard crashes come from the character's own damage code (DamageCharacterScript); hazards like kill
        /// blocks report their own death reason. Timeouts also come from ReadyToReset but are signalled first,
        /// so they're handled before this is asked. (Seen in logs: eject = 'ReadyToReset.cs', forced True;
        /// hard crash = 'DamageCharacterScript.cs', forced False.)
        /// </summary>
        private static bool IsEject(Live live)
        {
            bool fromRaceCode = (live.KillCaller ?? "").StartsWith("ReadyToReset", StringComparison.OrdinalIgnoreCase);
            bool plainCrash = live.DeathReason == null || live.DeathReason == "Gameplay_Crashed";
            return fromRaceCode && plainCrash;
        }

        /// <summary>Not across the line, not crashed and not taken out by the game.</summary>
        private static bool StillRacing(Live live) => !live.WasFinished && !live.WasDead && live.OutReason == null;

        private static Live FindLive(object player) => Slots.Values.FirstOrDefault(l => ReferenceEquals(l.Player, player));

        /// <summary>The game took a player out without a crash (timeout / removed).</summary>
        public static void MarkOut(object player, string reason)
        {
            var live = FindLive(player);
            if (live == null || live.OutReason != null) return;
            live.OutReason = reason;
            Plugin.Debug($"Player {live.Slot + 1}: {reason}.");
        }

        /// <summary>The game reports why a player died.</summary>
        public static void NoteDeathReason(object player, string reason)
        {
            var live = FindLive(player);
            if (live == null) return;
            live.DeathReason = reason;
            Plugin.Debug($"Player {live.Slot + 1} death reason from the game: {PrettyReason(reason)} ({reason}).");
        }

        private static string PrettyReason(string key)
        {
            switch (key)
            {
                case null: return "reason not reported yet";
                case "Gameplay_Crashed": return "crashed";
                case "Gameplay_Death_Eye": return "eye";
                case "Gameplay_Death_Ghost": return "ghost";
                case "Gameplay_Death_Sticky": return "sticky";
                case "Gameplay_Death_FoundFootage": return "found footage";
                case "Gameplay_Death_KillBlock": return "kill block";
                case "Gameplay_Death_Logic": return "level logic";
                default: return key;
            }
        }

        private static readonly Vector3[] CarAxes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        private static string AxisName(Vector3 a) =>
            a == Vector3.right ? "+X" : a == Vector3.left ? "-X" : a == Vector3.up ? "+Y" : a == Vector3.down ? "-Y" : a == Vector3.forward ? "+Z" : "-Z";

        /// <summary>
        /// Works out which of the car model's own axes point up and forward: up is the one closest to the sky
        /// while on the track, forward the one closest to the direction it's rolling (only while rolling straight).
        /// </summary>
        private static void DetectAxes(Live live)
        {
            if (live.Body == null) return;
            Vector3 v = live.Body.velocity;
            if (v.magnitude < AxisDetectSpeed) return;
            var tr = live.Body.transform;
            Vector3 up = CarAxes.OrderByDescending(a => Vector3.Dot(tr.TransformDirection(a), Vector3.up)).First();
            Vector3 fwd = CarAxes.Where(a => a != up && a != -up)
                .OrderByDescending(a => Vector3.Dot(tr.TransformDirection(a), v)).First();
            float off = Vector3.Angle(tr.TransformDirection(fwd), v);
            if (off > AxisDetectMaxAngle) return; // sliding sideways: try again later
            live.Up = up;
            live.Forward = fwd;
            live.Right = Vector3.Cross(up, fwd);
            live.AxesKnown = true;
            Plugin.Debug($"Player {live.Slot + 1}: car axes: up {AxisName(up)}, forward {AxisName(fwd)}, right {AxisName(live.Right)} (rolling {off:0}° off the nose).");
        }

        /// <summary>Adds this frame's rotation, measured around the car's own axes, to the current jump.</summary>
        private static void AddRotation(Live live, Quaternion rot)
        {
            if (!live.AxesKnown) return;
            var delta = Quaternion.Inverse(live.LastRot) * rot; // rotation in the car's own frame
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (float.IsNaN(axis.x) || float.IsInfinity(axis.x) || angle == 0f) return;
            if (angle > 180f) { angle -= 360f; }
            Vector3 turn = axis * angle;
            live.Pitch += Vector3.Dot(turn, live.Right);
            live.Roll += Vector3.Dot(turn, live.Forward);
            live.Yaw += Vector3.Dot(turn, live.Up);
        }

        /// <summary>
        /// A jump ended: turn the rotation into flips, rolls and a spin (and note a big jump), and hold them until
        /// we know they didn't crash.
        /// </summary>
        private static void EndJump(Live live, float seconds, float now, bool dead, bool finished, bool onTrack)
        {
            float pitch = live.Pitch, roll = live.Roll, yaw = live.Yaw;
            live.Pitch = live.Roll = live.Yaw = 0f;
            bool big = seconds >= BigJumpSeconds;
            int flips = 0, rolls = 0, spin = 0;
            if (live.AxesKnown) // rotation only means something once we know which way the car faces
            {
                flips = (int)((Mathf.Abs(pitch) + FlipSlack) / 360f);
                rolls = (int)((Mathf.Abs(roll) + FlipSlack) / 360f);
                spin = (int)((Mathf.Abs(yaw) + SpinSlack) / 180f) * 180;
            }
            if (live.AxesKnown && (Mathf.Abs(pitch) >= 90f || Mathf.Abs(roll) >= 90f || Mathf.Abs(yaw) >= 90f))
                Plugin.Debug($"Player {live.Slot + 1} rotated in the air ({seconds:0.0} s): flip {pitch:+0;-0;0}° (+ = forwards), roll {roll:+0;-0;0}°, spin {yaw:+0;-0;0}°" +
                             $" -> {flips} flip(s), {rolls} roll(s), {spin} spin" +
                             $"{(dead ? ", but crashed" : finished ? ", crossing the finish" : !onTrack ? ", but landed off the track (doesn't count)" : "")}.");
            // Every real jump goes through the landing check too: the longest jump has to be landed.
            if (flips == 0 && rolls == 0 && spin == 0 && seconds < MinJumpSeconds) return;
            if (dead) return;
            // Only a landing on the real track counts, not the map's flat ground: falling off the map
            // and tumbling onto it can produce silly 10x spins.
            if (!onTrack && !finished) return;
            var stunt = new Stunt { Front = pitch > 0 ? flips : 0, Back = pitch < 0 ? flips : 0, Rolls = rolls, Spin = Mathf.Min(spin, MaxSpinTrick), SpinDegrees = spin > 0 ? Mathf.RoundToInt(Mathf.Abs(yaw)) : 0, LandedAt = now };
            stunt.Seconds = seconds;
            stunt.BigJump = big;
            stunt.AtFinish = finished && !onTrack;
            stunt.Name = stunt.HasTricks ? StuntName(stunt.Front, stunt.Back, rolls, spin) : big ? "big jump" : $"{seconds:0.0} s jump";
            stunt.Detail = $"{seconds:0.0} s in the air; flip {pitch:+0;-0;0}°, roll {roll:+0;-0;0}°, spin {yaw:+0;-0;0}°";
            live.Pending.Add(stunt); // each landing gets its own timer; a crash cancels all of them
            if (stunt.AtFinish && (stunt.HasTricks || stunt.BigJump))
                Plugin.Debug($"Player {live.Slot + 1} crossed the finish mid-air with a {stunt.Name}: counts if the finish is valid (every checkpoint).");
        }

        /// <summary>
        /// Race over: stunts "landed" by crossing the finish mid-air count only for a valid finish. Falling off the
        /// map through the finish line (a shortcut finish) isn't landing anything. Without results, they don't count.
        /// </summary>
        private static void SettleFinishStunts(RaceSummary summary)
        {
            foreach (var live in Slots.Values)
            {
                var line = summary?.Lines.FirstOrDefault(l => l.Slot == live.Slot);
                for (int i = live.Pending.Count - 1; i >= 0; i--)
                {
                    var s = live.Pending[i];
                    if (!s.AtFinish) continue;
                    if (line != null && line.Finished) CreditStunt(live, i);
                    else
                    {
                        live.Pending.RemoveAt(i);
                        if (s.HasTricks || s.BigJump)
                            Plugin.Debug($"Player {live.Slot + 1}'s {s.Name} crossed the finish mid-air, but the finish wasn't valid (missed checkpoints): doesn't count.");
                    }
                }
            }
        }

        private static string StuntName(int front, int back, int rolls, int spin)
        {
            string Times(int n, string trick) => n == 1 ? trick : n == 2 ? "double " + trick : n == 3 ? "triple " + trick : $"{n}x {trick}";
            var parts = new List<string>();
            if (front > 0) parts.Add(Times(front, "front flip"));
            if (back > 0) parts.Add(Times(back, "backflip"));
            if (rolls > 0) parts.Add(Times(rolls, "barrel roll"));
            if (spin > 0) parts.Add(spin > MaxSpinTrick ? $"{MaxSpinTrick}+" : spin.ToString());
            return string.Join(" + ", parts);
        }

        private static void CreditStunt(Live live, int index)
        {
            var s = live.Pending[index];
            live.Pending.RemoveAt(index);
            live.FrontFlips += s.Front;
            live.Backflips += s.Back;
            live.BarrelRolls += s.Rolls;
            if (s.BigJump) live.BigJumps++;
            if (s.Seconds > live.LongestJump) { live.LongestJump = s.Seconds; live.JumpAt = s.LandedAt; }
            // Best single jump this race (kept as "most in one jump"). Logged so odd ones can be checked afterwards.
            string best = "";
            if (s.Front + s.Back > live.MostFlips) { live.MostFlips = s.Front + s.Back; best += $" Most flips in one jump this race: {live.MostFlips}."; } // one of them is always 0
            if (s.Rolls > live.MostRolls) { live.MostRolls = s.Rolls; best += $" Most rolls in one jump this race: {live.MostRolls}."; }
            if (s.Spin > 0)
            {
                // Four counters: 540s go with the 360s, 900s with the 720s.
                if (s.Spin >= 1080) live.Spins1080++;
                else if (s.Spin >= 720) live.Spins720++;
                else if (s.Spin >= 360) live.Spins360++;
                else live.Spins180++;
                live.BiggestSpin = Mathf.Max(live.BiggestSpin, s.SpinDegrees);
            }
            if (s.HasTricks)
            {
                live.StuntAt = s.LandedAt;
                AddMoment(2, $"{Game.SlotName(live.Slot)} landed a {s.Name}", s.LandedAt); // a plain big jump has its own "flew X s" moment
            }
            if (s.HasTricks || s.BigJump) Plugin.Debug($"Stunt landed! Player {live.Slot + 1}: {s.Name} ({s.Detail}).{best}");
        }

        /// <summary>For the debug log: how long ago this player was last rammed, and whether that still counts.</summary>
        private static string SinceRammed(Live live)
        {
            if (live.LastHitBy < 0) return "";
            float ago = RaceTime - live.LastHitTime;
            return $" Last rammed by Player {live.LastHitBy + 1} {ago:0.0} s ago ({(live.KoArmed ? "still marked: counts for them" : "mark cleared: doesn't count")}).";
        }

        /// <summary>
        /// A marked player crashed: hold the knockout until the race ends. If they eject and still reach the
        /// finish on foot, the ram didn't knock them out after all.
        /// </summary>
        private static void HoldKnockout(Live victim, float now)
        {
            victim.KoArmed = false;
            if (victim.LastHitBy < 0) return;
            victim.KoPendingBy = victim.LastHitBy;
            victim.KoPendingAt = now;
            Plugin.Debug($"Player {victim.Slot + 1} crashed while marked: a knockout for Player {victim.LastHitBy + 1}, unless they still reach the finish.");
        }

        /// <summary>The race is over: held knockouts count, except for players who crossed the finish after all.</summary>
        private static void SettleKnockouts(RaceSummary summary)
        {
            foreach (var victim in Slots.Values)
            {
                if (victim.KoPendingBy < 0) continue;
                int by = victim.KoPendingBy;
                victim.KoPendingBy = -1;
                bool finished = victim.WasFinished || (summary?.Lines.Any(l => l.Slot == victim.Slot && l.CrossedFinish) ?? false);
                if (finished)
                {
                    Plugin.Debug($"Player {victim.Slot + 1} reached the finish after all: no knockout for Player {by + 1}.");
                    continue;
                }
                if (!Slots.TryGetValue(by, out var attacker)) continue;
                attacker.Knockouts++;
                AddMoment(1, $"{Game.SlotName(by)} knocked out {Game.SlotName(victim.Slot)}", victim.KoPendingAt);
                Plugin.Debug($"Knockout! Player {by + 1} took out Player {victim.Slot + 1}.");
            }
        }

        private static void Sample(Live live)
        {
            // "finished" means the player's round is over (also after dying);
            // "actuallyFinished" means they really crossed the finish line.
            bool finished = Access.Get(live.Player, "actuallyFinished", false);

            // Crash: the character goes from alive to dead.
            float now = RaceTime;
            bool dead = Access.Get(live.Character, "isdead", false);
            // Out before this frame (ejected earlier): only then is crossing the line an on foot finish. A crash in
            // the very frame they cross is just a finish (the game counts it as one).
            bool outBefore = live.WasDead;
            if (dead && !live.WasDead && !finished)
            {
                // A hard crash, an eject or a timeout (too slow): all end the race early, all count as a crash.
                // Ejects and timeouts are also counted on their own (Ejector seat / Snail).
                live.Crashes++;
                string kind;
                if (live.OutReason != null) { live.Timeouts++; kind = "timeout"; }
                else if (IsEject(live)) { live.Ejects++; kind = "eject"; }
                else kind = "hard crash";
                Plugin.Debug($"Player {live.Slot + 1} crashed: {kind} (reason: {PrettyReason(live.DeathReason)}, caller: '{live.KillCaller}', forced: {live.KillForced}).{SinceRammed(live)}");
                if (live.KoArmed) HoldKnockout(live, now);
                foreach (var s in live.Pending)
                    Plugin.Debug($"Player {live.Slot + 1} crashed {now - s.LandedAt:0.0} s after landing a {s.Name}: doesn't count.");
                live.Pending.Clear();
                if (live.CatArmed)
                {
                    live.CatArmed = false;
                    live.CatCrashes++;
                    AddMoment(2, $"{Game.SlotName(live.Slot)} crashed because of a cat");
                    Plugin.Debug($"Player {live.Slot + 1} crashed because of a cat!");
                }
            }
            live.WasDead = dead;

            // Possible knockout: cleared once they're safely back on the ground (or after a long time).
            bool grounded = IsGrounded(live);
            if (!grounded) live.GroundedSince = -1f;
            else if (live.GroundedSince < 0) live.GroundedSince = now;
            bool onTrack = IsOnTrack(live);
            if (!onTrack) live.OnTrackSince = -1f;
            else if (live.OnTrackSince < 0) live.OnTrackSince = now;
            bool onFloor = !onTrack && IsOnGroundFloor(live);
            if (onFloor && !live.WasOnGroundFloor && !finished && !dead)
                Plugin.Debug($"Player {live.Slot + 1} landed on the map's ground (off the track).");
            live.WasOnGroundFloor = onFloor;
            // Air time: a jump runs from leaving the ground until touching it again (or crashing / finishing).
            bool airborne = !grounded && !dead && !finished;
            Quaternion rot = live.Body != null ? live.Body.transform.rotation : Quaternion.identity;
            if (airborne)
            {
                if (live.AirStart < 0)
                {
                    live.AirStart = now; live.Pitch = live.Roll = live.Yaw = 0f;
                    if (live.Body != null) { live.AirStartPos = live.Body.position; live.AirPeakY = live.AirStartPos.y; }
                }
                if (live.Body != null) live.AirPeakY = Mathf.Max(live.AirPeakY, live.Body.position.y);
                if (live.HasLastRot) AddRotation(live, rot);
            }
            else if (live.AirStart >= 0)
            {
                float jump = now - live.AirStart;
                if (jump >= MinJumpSeconds && live.Body != null)
                {
                    // Every jump, to tune what counts as a big one: how long, how far, how high, how far down.
                    Vector3 end = live.Body.position;
                    float far = Vector3.Distance(new Vector3(live.AirStartPos.x, 0, live.AirStartPos.z), new Vector3(end.x, 0, end.z));
                    Plugin.Debug($"Player {live.Slot + 1} jump: {jump:0.00} s, {far:0} m far, rose {live.AirPeakY - live.AirStartPos.y:0.0} m, " +
                                 $"landed {live.AirStartPos.y - end.y:0.0} m below the takeoff{(dead ? " (crashed)" : finished ? " (finish)" : !onTrack ? " (off the track)" : "")}.");
                }
                EndJump(live, jump, now, dead, finished, onTrack);
                live.AirStart = -1f;
                if (jump >= MinJumpSeconds)
                {
                    live.Air += jump;
                    // Big jumps and the longest jump are counted like stunts: landed on the track, no crash right after (CreditStunt).
                }
            }

            if (live.Body != null) { live.LastRot = rot; live.HasLastRot = true; }
            // Landed a stunt a while ago (or crossed the finish) and still in one piece: it counts.
            // One that "landed" by crossing the finish mid-air waits for the results (SettleFinishStunts).
            if (!dead)
                for (int i = live.Pending.Count - 1; i >= 0; i--)
                {
                    if (live.Pending[i].AtFinish) continue;
                    if (finished || now - live.Pending[i].LandedAt >= StuntLandingGrace) CreditStunt(live, i);
                }
            if (!live.AxesKnown && grounded && onTrack && !dead && !finished) DetectAxes(live);

            if (live.CatArmed && !finished)
            {
                bool recovered = onTrack && now - live.CatHitTime >= KnockoutWindow && now - live.OnTrackSince >= RecoveredAfter;
                if (recovered || now - live.CatHitTime > MaxKnockoutSeconds) live.CatArmed = false;
            }

            if (live.KoArmed && !finished)
            {
                bool recovered = onTrack && now - live.LastHitTime >= KnockoutWindow && now - live.OnTrackSince >= RecoveredAfter;
                bool expired = now - live.LastHitTime > MaxKnockoutSeconds;
                if (recovered || expired)
                {
                    live.KoArmed = false;
                    Plugin.Debug($"Player {live.Slot + 1} {(recovered ? "recovered from" : "is no longer marked after")} the ram by Player {live.LastHitBy + 1} " +
                                 $"({now - live.LastHitTime:0.0} s after it): a crash or lost wheel from now on is their own.");
                }
            }
            Vector3 prevVelocity = live.LastVelocity; // from the frame before, in case the game stops the car at the finish
            if (live.Body != null) live.LastVelocity = live.Body.velocity;

            // Wheels: each wheel going from attached to broken off.
            for (int w = 0; w < live.Wheels.Count; w++)
            {
                if (live.Wheels[w] == null) continue;
                bool broken = Access.Get(live.Wheels[w], "isdead", false);
                // Only while still racing: wheels that come off in a crash (how many depends on how violent it was) or after an eject don't count.
                if (broken && !live.WheelDead[w] && !finished && !dead)
                {
                    live.WheelsLost++;
                    live.WheelAt = now;
                    Plugin.Debug($"Player {live.Slot + 1} lost a wheel.{SinceRammed(live)}");
                    // Still marked after getting rammed: the wheel goes to the rammer, just like a crash would.
                    if (live.KoArmed && Slots.TryGetValue(live.LastHitBy, out var sniper))
                    {
                        sniper.WheelsKnockedOff++;
                        Plugin.Debug($"Wheel knocked off! Player {live.LastHitBy + 1} took a wheel from Player {live.Slot + 1}.");
                    }
                }
                live.WheelDead[w] = broken;
            }

            // The moment they cross the finish: how many wheels are still on?
            if (finished && !live.WasFinished)
            {
                int attached = 0;
                for (int w = 0; w < live.Wheels.Count; w++)
                    if (live.Wheels[w] != null && !live.WheelDead[w]) attached++;
                live.WheelsAtFinish = attached;
                live.FinishAt = now;
                live.DeadAtFinish = outBefore;
                string direction = "";
                if (!outBefore && live.AxesKnown && live.Body != null && prevVelocity.magnitude > 1f)
                {
                    float angle = Vector3.Angle(live.Body.transform.TransformDirection(live.Forward), prevVelocity);
                    live.ReverseAtFinish = angle >= ReverseAngle;
                    direction = $", moving {angle:0}° off the car's nose{(live.ReverseAtFinish ? " = BACKWARDS!" : "")}";
                }
                Plugin.Debug($"Player {live.Slot + 1} crossed the finish: {attached} wheel(s) on{(outBefore ? ", ON FOOT (ejected)" : "")}{direction}");
            }
            live.WasFinished = finished;

            // Distance and speed while actually racing.
            Transform t = live.Body != null ? live.Body.transform : live.Car != null ? live.Car.transform : null;
            if (t == null) return;
            var pos = t.position;
            if (!finished && !dead && live.LastPos.HasValue)
            {
                float step = Vector3.Distance(pos, live.LastPos.Value);
                if (step < MaxStepMeters) live.Distance += step;
                // Top speed: driving on the track, with at least two wheels on it (not the map's ground floor, and not
                // the car body scraping a wall or scenery on the way down), and not right after landing.
                int wheelsOnTrack = WheelsOnTrack(live);
                if (live.Body != null && wheelsOnTrack >= TopSpeedMinWheels && now - live.OnTrackSince >= TopSpeedLandingGrace)
                {
                    var v = live.Body.velocity;
                    float speed = v.magnitude * 3.6f;
                    if (speed > live.TopSpeed)
                    {
                        // Logged from 150 km/h up, to check the fastest speeds are real driving, not a fall.
                        if (speed >= 150f && speed - live.TopSpeed >= 5f)
                            Plugin.Debug($"Player {live.Slot + 1} top speed {speed:0} km/h: {wheelsOnTrack} wheel(s) on the track, " +
                                         $"falling {Mathf.Max(0f, -v.y) * 3.6f:0} km/h, {now - live.OnTrackSince:0.0} s since landing.");
                        live.TopSpeed = speed;
                        live.TopSpeedAt = now;
                    }
                }
            }
            live.LastPos = pos;
        }
    }

    /// <summary>Attached to each local player's car during a race; forwards physics hits to the stats tracker.</summary>
    public class CarCollisionReporter : MonoBehaviour
    {
        public int Slot;

        private void OnCollisionEnter(Collision collision)
        {
            StatsTracker.OnCarCollision(Slot, collision);
            StatsTracker.OnCarContact(Slot, collision);
        }

        private void OnCollisionStay(Collision collision) => StatsTracker.OnCarContact(Slot, collision);
    }
}
