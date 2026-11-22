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

            public int Crashes, WheelsLost;
            public double Distance;
            public float TopSpeed;

            // Bumper cars
            public int Bumps, Rams, GotRammed, Knockouts;
            public Vector3 LastVelocity;
            public int LastHitBy = -1;
            public float LastHitTime;
        }

        /// <summary>Gentler touches than this (relative speed, m/s) don't count as a bump.</summary>
        private const float MinBumpSpeed = 3f;
        /// <summary>One bump per pair of players per this many seconds (no counting a long scrape many times).</summary>
        private const float BumpCooldown = 1f;
        /// <summary>A crash this soon after a bump is a knockout for the other player.</summary>
        private const float KnockoutWindow = 2f;

        private static readonly Dictionary<Rigidbody, int> BodyToSlot = new Dictionary<Rigidbody, int>();
        private static readonly Dictionary<int, float> PairLastBump = new Dictionary<int, float>();

        /// <summary>Teleports (respawns) bigger than this per frame are not counted as driving.</summary>
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

            var winner = summary.Lines.FirstOrDefault(l => l.Finished);
            bool changed = false;
            foreach (var line in summary.Lines)
            {
                var profile = Store.GetSlotProfile(line.Slot);
                if (profile == null) continue;
                var st = profile.Stats;

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
            if (changed) Store.SaveProfiles();
        }

        private static void CommitLiveOnly()
        {
            bool changed = false;
            foreach (var kv in Slots)
            {
                var profile = Store.GetSlotProfile(kv.Key);
                if (profile == null) continue;
                AddLive(profile.Stats, kv.Value);
                changed = true;
            }
            Slots.Clear();
            if (changed) Store.SaveProfiles();
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
            Plugin.Debug($"Finish on {live.WheelsAtFinish} wheel(s).");
        }

        private static void AddLive(PlayerStats st, Live live)
        {
            st.Crashes += live.Crashes;
            st.WheelsLost += live.WheelsLost;
            st.DistanceMeters += live.Distance;
            st.TopSpeedKmh = Mathf.Max(st.TopSpeedKmh, live.TopSpeed);
            st.Bumps += live.Bumps;
            st.Rams += live.Rams;
            st.GotRammed += live.GotRammed;
            st.Knockouts += live.Knockouts;
            live.Crashes = live.WheelsLost = 0;
            live.Bumps = live.Rams = live.GotRammed = live.Knockouts = 0;
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

                if (!Slots.TryGetValue(slot, out var live) || !ReferenceEquals(live.Player, p) || live.Car == null)
                {
                    live = new Live { Player = p };
                    live.Car = Access.Get<Component>(p, "cc");
                    live.Character = Access.Get<object>(p, "character");
                    if (live.Car != null)
                    {
                        live.Body = live.Car.GetComponent<Rigidbody>() ?? live.Car.GetComponentInParent<Rigidbody>();
                        live.Wheels = live.Car.GetComponentsInChildren<DamageWheel>(true).Cast<Component>().ToList();
                        live.WheelDead = live.Wheels.Select(w => Access.Get(w, "isdead", false)).ToArray();
                    }
                    if (live.Body == null) live.Body = Access.Get<Rigidbody>(live.Character, "rb");
                    live.WasDead = Access.Get(live.Character, "isdead", false);
                    Slots[slot] = live;
                    WatchCollisions(slot, live);
                }

                if (!_loggedMapping)
                    Plugin.Debug($"Stats: PlayersReady[{i}] index={index} -> Player {slot + 1}, car={(live.Car != null)}, body={(live.Body != null)}, wheels={live.Wheels.Count}");
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
            if (otherBody == null || !BodyToSlot.TryGetValue(otherBody, out int other) || other == slot) return;
            if (collision.relativeVelocity.magnitude < MinBumpSpeed) return;
            if (!Slots.TryGetValue(slot, out var a) || !Slots.TryGetValue(other, out var b)) return;

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
            a.LastHitBy = other; a.LastHitTime = now;
            b.LastHitBy = slot;  b.LastHitTime = now;
            Plugin.Debug($"Bump: Player {slot + 1} and Player {other + 1} at {collision.relativeVelocity.magnitude * 3.6f:0} km/h " +
                         $"(rammer: Player {(rammer == a ? slot : other) + 1})");
        }

        private static void Sample(Live live)
        {
            bool finished = Access.Get(live.Player, "finished", false);

            // Crash: the character goes from alive to dead.
            bool dead = Access.Get(live.Character, "isdead", false);
            if (dead && !live.WasDead && !finished)
            {
                live.Crashes++;
                // Knocked out by whoever bumped into them just before.
                if (live.LastHitBy >= 0 && Time.unscaledTime - live.LastHitTime < KnockoutWindow &&
                    Slots.TryGetValue(live.LastHitBy, out var attacker))
                {
                    attacker.Knockouts++;
                    Plugin.Debug($"Knockout! Player {live.LastHitBy + 1} took out a player.");
                }
            }
            live.WasDead = dead;
            if (live.Body != null) live.LastVelocity = live.Body.velocity;

            // Wheels: each wheel going from attached to broken off.
            for (int w = 0; w < live.Wheels.Count; w++)
            {
                if (live.Wheels[w] == null) continue;
                bool broken = Access.Get(live.Wheels[w], "isdead", false);
                if (broken && !live.WheelDead[w] && !finished) live.WheelsLost++;
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
                if (dead) Plugin.Debug("On foot finish: crossed the line without the car!");
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

        private void OnCollisionEnter(Collision collision) => StatsTracker.OnCarCollision(Slot, collision);
    }
}
