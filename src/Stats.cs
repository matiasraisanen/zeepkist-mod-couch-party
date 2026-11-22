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
            public int Bumps, Rams, GotRammed, Knockouts;
            public Vector3 LastVelocity;
            public int LastHitBy = -1;
            public float LastHitTime;
            public int WheelsKnockedOff;
            public int CatsHugged;
            public int CatCrashes;
            public bool CatArmed;        // hit a cat and not safely back on the ground yet
            public string DeathReason;   // from the game, e.g. "Gameplay_Crashed"
            public string OutReason;     // the game took them out (timeout)
            public string KillCaller;    // what the game said when killing the character (tells ejects from crashes)
            public bool KillForced;
            public float CatHitTime;

            // Knockout tracking: armed by a bump, cleared once they're safely back on the ground.
            public bool KoArmed;
            public List<object> CustomWheels = new List<object>(); // New_CustomWheel (has isGrounded)
            public float LastBodyContact = -10f;  // car body touching something that isn't another racer
            public float GroundedSince = -1f;      // -1 while airborne

            // Air time
            public float AirStart = -1f;           // -1 while on the ground
            public double Air;
            public float LongestJump;
            public int BigJumps;
        }

        /// <summary>Shorter hops than this aren't counted as air time.</summary>
        private const float MinJumpSeconds = 0.3f;
        /// <summary>Jumps at least this long count as big jumps.</summary>
        private const float BigJumpSeconds = 1f;

        /// <summary>Gentler touches than this (relative speed, m/s) don't count as a bump.</summary>
        private const float MinBumpSpeed = 3f;
        /// <summary>One bump per pair of players per this many seconds (no counting a long scrape many times).</summary>
        private const float BumpCooldown = 1f;
        /// <summary>A crash within this long after a bump (or cat hit) counts, if they stay on their wheels.</summary>
        private const float KnockoutWindow = 4f;
        /// <summary>After the window, they must have been on the ground this long to be "recovered" (longer if they're in the air).</summary>
        private const float RecoveredAfter = 1f;
        /// <summary>A possible knockout expires after this long no matter what.</summary>
        private const float MaxKnockoutSeconds = 15f;
        /// <summary>A wheel breaking this soon after a bump counts as knocked off by the other player.</summary>
        private const float WheelKnockWindow = 1f;

        private static readonly Dictionary<Rigidbody, int> BodyToSlot = new Dictionary<Rigidbody, int>();
        /// <summary>Players already out when the race started (ejected during the countdown), counted once per round.</summary>
        private static readonly HashSet<int> OutBeforeStart = new HashSet<int>();
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
            PairLastBump.Clear();
            OutBeforeStart.Clear();
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
            if (summary == null) { Slots.Clear(); _tracking = false; return; }

            // Still marked as possibly knocked out and never finished: pushed off for good.
            foreach (var line in summary.Lines)
                if (!line.CrossedFinish && Slots.TryGetValue(line.Slot, out var victim) && victim.KoArmed)
                    CreditKnockout(victim, "didn't finish");

            var winner = summary.Lines.FirstOrDefault(l => l.Finished);
            bool changed = false;
            foreach (var line in summary.Lines)
            {
                var profile = Store.GetSlotProfile(line.Slot);
                if (profile == null) continue;
                var st = profile.MyStats; // this PC's entry; totals are summed over all PCs
                st.Updated = DateTime.UtcNow;

                st.Races++;
                if (line.Finished)
                {
                    st.Finishes++;
                    st.FinishTimeSeconds += line.Time;
                    if (line == winner && summary.Lines.Count >= 2) st.Wins++;
                }
                else if (line.CrossedFinish) st.Shortcuts++;
                else st.Dnfs++;
                if (line.Finished && Slots.TryGetValue(line.Slot, out var atFinish))
                {
                    CountWheelFinish(st, atFinish);
                    if (atFinish.DeadAtFinish) st.OnFootFinishes++;
                }
                if (line.NewPersonalBest) st.PersonalBests++;
                if (line.NewMapRecord) st.RecordsBroken++;

                if (Slots.TryGetValue(line.Slot, out var live)) AddLive(st, live);
                changed = true;
            }
            Slots.Clear();
            _tracking = false;
            if (changed)
            {
                Store.StatsChanged();
                Store.SaveProfiles();
            }
        }

        private static void CommitLiveOnly()
        {
            bool changed = false;
            foreach (var kv in Slots)
            {
                var profile = Store.GetSlotProfile(kv.Key);
                if (profile == null) continue;
                var st = profile.MyStats;
                st.Updated = DateTime.UtcNow;
                AddLive(st, kv.Value);
                changed = true;
            }
            Slots.Clear();
            if (changed)
            {
                Store.StatsChanged();
                Store.SaveProfiles();
            }
        }

        /// <summary>Valid finish with wheels missing: bump the 3/2/1/0-wheel counter.</summary>
        private static void CountWheelFinish(PlayerStats st, Live live)
        {
            if (live.Wheels.Count < 4 || live.WheelsAtFinish < 0) return; // couldn't see the wheels
            switch (live.WheelsAtFinish)
            {
                case 3: st.ThreeWheelFinishes++; break;
                case 2: st.TwoWheelFinishes++; break;
                case 1: st.OneWheelFinishes++; break;
                case 0: st.ZeroWheelFinishes++; break;
            }
            if (live.WheelsAtFinish < 4) Plugin.Debug($"Player {live.Slot + 1} finished on {live.WheelsAtFinish} wheel(s)!");
        }

        private static void AddLive(PlayerStats st, Live live)
        {
            st.Crashes += live.Crashes;
            st.Ejects += live.Ejects;
            st.Timeouts += live.Timeouts;
            st.WheelsLost += live.WheelsLost;
            st.DistanceMeters += live.Distance;
            st.TopSpeedKmh = Mathf.Max(st.TopSpeedKmh, live.TopSpeed);
            st.Bumps += live.Bumps;
            st.Rams += live.Rams;
            st.GotRammed += live.GotRammed;
            st.Knockouts += live.Knockouts;
            st.WheelsKnockedOff += live.WheelsKnockedOff;
            st.CatsHugged += live.CatsHugged;
            st.CatCrashes += live.CatCrashes;
            live.CatsHugged = live.CatCrashes = 0;
            st.AirSeconds += live.Air;
            st.LongestJumpSeconds = Mathf.Max(st.LongestJumpSeconds, live.LongestJump);
            st.BigJumps += live.BigJumps;
            live.Air = 0;
            live.BigJumps = 0;
            live.Crashes = live.Ejects = live.Timeouts = live.WheelsLost = 0;
            live.Bumps = live.Rams = live.GotRammed = live.Knockouts = live.WheelsKnockedOff = 0;
            live.Distance = 0;
        }

        // ---------------- Per-frame tracking ----------------

        public static void Tick()
        {
            if (!_tracking) return;
            try
            {
                if (_gm == null || Time.unscaledTime >= _nextRefresh)
                {
                    _nextRefresh = Time.unscaledTime + 1f;
                    if (_gm == null) _gm = UnityEngine.Object.FindObjectOfType<GameMaster>();
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
            float now = Time.unscaledTime;
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

            a.Bumps++;
            b.Bumps++;
            rammer.Rams++;
            victim.GotRammed++;
            a.LastHitBy = other; a.LastHitTime = now; a.KoArmed = true;
            b.LastHitBy = slot;  b.LastHitTime = now; b.KoArmed = true;
            Plugin.Debug($"Bump: Player {slot + 1} ({a.LastVelocity.magnitude * 3.6f:0} km/h, {towardsB * 3.6f:0} towards the other) and " +
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
            float now = Time.unscaledTime;
            if (CatLastHit.TryGetValue(key, out float last) && now - last < CatHitCooldown) return;
            CatLastHit[key] = now;

            live.CatsHugged++;
            live.CatArmed = true;
            live.CatHitTime = now;
            Plugin.Debug($"Cat #{cat.GetInstanceID()} hugged by Player {slot + 1} at {collision.relativeVelocity.magnitude * 3.6f:0} km/h!");
        }

        /// <summary>Car body is touching something (OnCollisionStay). Only the track/scenery counts as ground.</summary>
        public static void OnCarContact(int slot, Collision collision)
        {
            if (!_tracking || collision == null || !Slots.TryGetValue(slot, out var live)) return;
            var otherBody = collision.rigidbody != null ? collision.rigidbody : collision.collider != null ? collision.collider.attachedRigidbody : null;
            if (otherBody != null && BodyToSlot.ContainsKey(otherBody)) return; // another racer (or own parts) isn't the ground
            live.LastBodyContact = Time.unscaledTime;
        }

        private static bool IsGrounded(Live live)
        {
            if (Time.unscaledTime - live.LastBodyContact < 0.25f) return true;
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

        private static void CreditKnockout(Live victim, string how)
        {
            victim.KoArmed = false;
            if (victim.LastHitBy < 0 || !Slots.TryGetValue(victim.LastHitBy, out var attacker)) return;
            attacker.Knockouts++;
            Plugin.Debug($"Knockout! Player {victim.LastHitBy + 1} took out Player {victim.Slot + 1} ({how}).");
        }

        private static void Sample(Live live)
        {
            // "finished" means the player's round is over (also after dying);
            // "actuallyFinished" means they really crossed the finish line.
            bool finished = Access.Get(live.Player, "actuallyFinished", false);

            // Crash: the character goes from alive to dead.
            bool dead = Access.Get(live.Character, "isdead", false);
            if (dead && !live.WasDead && !finished)
            {
                // A hard crash, an eject or a timeout (too slow): all end the race early, all count as a crash.
                // Ejects and timeouts are also counted on their own (Ejector seat / Snail).
                live.Crashes++;
                string kind;
                if (live.OutReason != null) { live.Timeouts++; kind = "timeout"; }
                else if (IsEject(live)) { live.Ejects++; kind = "eject"; }
                else kind = "hard crash";
                Plugin.Debug($"Player {live.Slot + 1} crashed: {kind} (reason: {PrettyReason(live.DeathReason)}, caller: '{live.KillCaller}', forced: {live.KillForced}).");
                if (live.KoArmed) CreditKnockout(live, "crashed");
                if (live.CatArmed)
                {
                    live.CatArmed = false;
                    live.CatCrashes++;
                    Plugin.Debug($"Player {live.Slot + 1} crashed because of a cat!");
                }
            }
            live.WasDead = dead;

            // Possible knockout: cleared once they're safely back on the ground (or after a long time).
            float now = Time.unscaledTime;
            bool grounded = IsGrounded(live);
            if (!grounded) live.GroundedSince = -1f;
            else if (live.GroundedSince < 0) live.GroundedSince = now;
            // Air time: a jump runs from leaving the ground until touching it again (or crashing / finishing).
            bool airborne = !grounded && !dead && !finished;
            if (airborne)
            {
                if (live.AirStart < 0) live.AirStart = now;
            }
            else if (live.AirStart >= 0)
            {
                float jump = now - live.AirStart;
                live.AirStart = -1f;
                if (jump >= MinJumpSeconds)
                {
                    live.Air += jump;
                    live.LongestJump = Mathf.Max(live.LongestJump, jump);
                    if (jump >= BigJumpSeconds)
                    {
                        live.BigJumps++;
                        Plugin.Debug($"Player {live.Slot + 1}: {jump:0.0} s of air time!");
                    }
                }
            }

            if (live.CatArmed && !finished)
            {
                bool recovered = grounded && now - live.CatHitTime >= KnockoutWindow && now - live.GroundedSince >= RecoveredAfter;
                if (recovered || now - live.CatHitTime > MaxKnockoutSeconds) live.CatArmed = false;
            }

            if (live.KoArmed && !finished)
            {
                bool recovered = grounded && now - live.LastHitTime >= KnockoutWindow && now - live.GroundedSince >= RecoveredAfter;
                if (recovered || now - live.LastHitTime > MaxKnockoutSeconds) live.KoArmed = false;
            }
            if (live.Body != null) live.LastVelocity = live.Body.velocity;

            // Wheels: each wheel going from attached to broken off.
            for (int w = 0; w < live.Wheels.Count; w++)
            {
                if (live.Wheels[w] == null) continue;
                bool broken = Access.Get(live.Wheels[w], "isdead", false);
                if (broken && !live.WheelDead[w] && !finished)
                {
                    live.WheelsLost++;
                    if (live.LastHitBy >= 0 && now - live.LastHitTime < WheelKnockWindow &&
                        Slots.TryGetValue(live.LastHitBy, out var sniper))
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
                live.DeadAtFinish = dead;
                Plugin.Debug($"Player {live.Slot + 1} crossed the finish: {attached} wheel(s) on{(dead ? ", ON FOOT (ejected)" : "")}");
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
                if (live.Body != null) live.TopSpeed = Mathf.Max(live.TopSpeed, live.Body.velocity.magnitude * 3.6f);
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
