using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace CouchParty
{
    /// <summary>
    /// Remembers each profile's outfit (saved automatically when a race starts) and picks it again
    /// on the player-select screen when that player joins.
    ///
    /// Cosmetics are identified by their complete id (family + item). The player-select panel stores
    /// a position in its own list instead: index = page * boxesPerPage + box.
    /// </summary>
    internal static class Outfits
    {
        private class State
        {
            public float ActiveSince = -1f;
            public string AppliedProfileId;
        }

        /// <summary>Everything needed to drive one cosmetic category on a player-select panel.</summary>
        private class Category
        {
            public string What;              // for logs
            public string ManagerList;       // PlayerManager list holding each slot's current item
            public string IndexField, PageField, BoxField, BoxesField, PageChangedField; // panel fields
            public string DisplayIndex;      // wardrobe: complete id -> display position
            public string[] WardrobeLists;   // wardrobe lists/shelves that might match the panel's order
        }

        private static readonly Category Soapbox = new Category
        {
            What = "soapbox", ManagerList = "playersSoapbox",
            IndexField = "playerSoapbox", PageField = "playerSoapboxPage", BoxField = "playerSoapboxBox",
            BoxesField = "soapboxBoxes", PageChangedField = "didWeChangeSoapboxPage",
            DisplayIndex = "display_zeepkists_index",
            WardrobeLists = new[] { "display_zeepkists", "zeepkistList", "zeepkistShelfs", "everyZeepkist", "unlockedZeepkist" },
        };

        private static readonly Category Hat = new Category
        {
            What = "hat", ManagerList = "playersHat",
            IndexField = "playerHat", PageField = "playerHatPage", BoxField = "playerHatBox",
            BoxesField = "hatBoxes", PageChangedField = "didWeChangeHatPage",
            DisplayIndex = "display_hats_index",
            WardrobeLists = new[] { "display_hats", "hatList", "hatShelfs", "everyHat", "unlockedHat" },
        };

        private static readonly Category Color = new Category
        {
            What = "color", ManagerList = "playersColor",
            IndexField = "playerColor",
            DisplayIndex = "display_colors_index",
            WardrobeLists = new[] { "display_colors", "colorList", "colorShelfs", "everyColor", "unlockedColor" },
        };

        // Keyed by the player-select character panel's instance id.
        private static readonly Dictionary<int, State> States = new Dictionary<int, State>();
        private static readonly Dictionary<int, string> LastLogged = new Dictionary<int, string>();

        /// <summary>Wait this long after a player joins before preselecting, so the game's own join setup is done.</summary>
        private const float JoinSettleSeconds = 0.4f;

        /// <summary>Upper bound for the last-resort scan through panel positions.</summary>
        private const int MaxScan = 400;

        // ---------------- Autosave ----------------

        /// <summary>Copies what each profiled player is wearing into their profile.</summary>
        public static void AutosaveFromSlots()
        {
            if (Game.IsOnline) return;
            bool changed = false;
            for (int slot = 0; slot < Game.MaxSlots; slot++)
            {
                var profile = Store.GetSlotProfile(slot);
                if (profile == null || !Game.IsSlotActive(slot)) continue;
                if (!Game.TryReadSlotOutfit(slot, out var soapbox, out var hat, out var color)) continue;

                int s = Game.CompleteId(soapbox), h = Game.CompleteId(hat), c = Game.CompleteId(color);
                if (profile.SoapboxId == s && profile.HatId == h && profile.ColorId == c) continue;

                profile.SoapboxId = s;
                profile.HatId = h;
                profile.ColorId = c;
                profile.SoapboxName = Game.CosmeticName(soapbox);
                profile.HatName = Game.CosmeticName(hat);
                profile.ColorName = Game.CosmeticName(color);
                profile.OutfitVersion = 2;
                changed = true;
                Plugin.Log.LogInfo($"Saved outfit for '{profile.Name}' (Player {slot + 1}): " +
                                   $"{profile.SoapboxName} [{s}], {profile.HatName} [{h}], {profile.ColorName} [{c}]");
            }
            if (changed) Store.SaveProfiles();
        }

        // ---------------- Preselect on the player-select screen ----------------

        public static void ResetPanel(object character)
        {
            if (character is UnityEngine.Object o) States.Remove(o.GetInstanceID());
        }

        /// <summary>Called every frame per player panel on the player-select screen.</summary>
        public static void Tick(object character, int slot, bool active, Profile profile)
        {
            if (!(character is UnityEngine.Object o)) return;
            int id = o.GetInstanceID();
            if (!States.TryGetValue(id, out var state)) States[id] = state = new State();

            if (!active)
            {
                state.ActiveSince = -1f;
                state.AppliedProfileId = null; // rejoining picks the outfit again
                return;
            }
            if (state.ActiveSince < 0) state.ActiveSince = Time.unscaledTime;
            if (Time.unscaledTime - state.ActiveSince < JoinSettleSeconds) return;
            if (profile == null || state.AppliedProfileId == profile.Id) return;

            state.AppliedProfileId = profile.Id; // one attempt per join/profile change
            if (profile.HasOutfit) Preselect(character, slot, profile);
        }

        /// <summary>True while we drive a panel ourselves (mutes the per-change selection log).</summary>
        private static bool _busy;

        private static void Preselect(object character, int slot, Profile profile)
        {
            _busy = true;
            try
            {
                var wardrobe = Access.Get<object>(Access.Get<object>(PlayerManager.Instance, "objectsList"), "wardrobe");

                string soapbox = TrySelect(character, slot, wardrobe, Soapbox, profile.SoapboxId);
                string hat = TrySelect(character, slot, wardrobe, Hat, profile.HatId);
                string color = TrySelect(character, slot, wardrobe, Color, profile.ColorId);

                Access.Set(character, "shouldWeUpdateTheModel", true);
                Plugin.Log.LogInfo($"Preselected outfit for '{profile.Name}' (Player {slot + 1}): soapbox {soapbox}, hat {hat}, color {color}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Preselecting outfit failed: {e}");
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Selects one saved cosmetic on a panel. Returns a short status for the log.</summary>
        private static string TrySelect(object character, int slot, object wardrobe, Category cat, int savedId)
        {
            if (savedId < 0) return "none saved";
            if (Game.CompleteId(Game.SlotCosmetic(slot, cat.ManagerList)) == savedId) return "already selected";

            int oldIndex = Access.Get(character, cat.IndexField, 0);
            int oldPage = cat.PageField != null ? Access.Get(character, cat.PageField, 0) : 0;
            int oldBox = cat.BoxField != null ? Access.Get(character, cat.BoxField, 0) : 0;
            int perPage = cat.BoxesField != null ? Access.List(character, cat.BoxesField)?.Count ?? 0 : 0;

            // 1) The wardrobe's display-index table (complete id -> position on the player-select screen).
            var table = Access.Get<IDictionary>(wardrobe, cat.DisplayIndex);
            if (table != null && table.Contains(savedId) && table[savedId] is int shown && Apply(character, slot, cat, perPage, shown, savedId))
                return $"ok (display index {shown})";

            // 2) Positions in the wardrobe's lists.
            var tried = new HashSet<int>();
            foreach (var listName in cat.WardrobeLists)
            {
                int i = IndexOfCompleteId(Flatten(Access.Get<object>(wardrobe, listName)), savedId);
                if (i < 0 || !tried.Add(i)) continue;
                if (Apply(character, slot, cat, perPage, i, savedId)) return $"ok ({listName}[{i}])";
            }

            // 3) Last resort: step through the panel's positions until the game confirms the item.
            int lastId = int.MinValue, repeats = 0;
            for (int i = 0; i < MaxScan; i++)
            {
                if (tried.Contains(i)) continue;
                if (Apply(character, slot, cat, perPage, i, savedId)) return $"ok (scan {i})";
                // Past the end of the list the game keeps returning the same item: stop.
                int got = Game.CompleteId(Game.SlotCosmetic(slot, cat.ManagerList));
                repeats = got == lastId ? repeats + 1 : 0;
                lastId = got;
                if (repeats > 20) break;
            }

            // Nothing worked (e.g. not unlocked on this account): put the player's own selection back.
            Access.Set(character, cat.IndexField, oldIndex);
            if (cat.PageField != null) Access.Set(character, cat.PageField, oldPage);
            if (cat.BoxField != null) Access.Set(character, cat.BoxField, oldBox);
            if (cat.PageChangedField != null) Access.Set(character, cat.PageChangedField, true);
            Publish(character);
            return "NOT FOUND (locked or removed?)";
        }

        /// <summary>Puts a panel at a list position and returns true if the game then has the wanted item.</summary>
        private static bool Apply(object character, int slot, Category cat, int perPage, int index, int savedId)
        {
            Access.Set(character, cat.IndexField, index);
            if (perPage > 0)
            {
                Access.Set(character, cat.PageField, index / perPage);
                Access.Set(character, cat.BoxField, index % perPage);
                Access.Set(character, cat.PageChangedField, true);
            }
            try { Publish(character); }
            catch { return false; } // index out of range for the game
            return Game.CompleteId(Game.SlotCosmetic(slot, cat.ManagerList)) == savedId;
        }

        /// <summary>Turns a list, a dictionary (values) or a list of shelves (their cosmetics) into one sequence.</summary>
        private static IEnumerable<object> Flatten(object source)
        {
            if (source == null) yield break;
            IEnumerable items = source is IDictionary dict ? dict.Values : source as IEnumerable;
            if (items == null) yield break;
            foreach (var item in items)
            {
                var shelfItems = Access.Get<IEnumerable>(item, "cosmetics");
                if (shelfItems != null && !(item is string))
                {
                    foreach (var inner in shelfItems) yield return inner;
                }
                else yield return item;
            }
        }

        private static int IndexOfCompleteId(IEnumerable<object> items, int id)
        {
            int i = 0;
            foreach (var item in items)
            {
                if (Game.CompleteId(item) == id) return i;
                i++;
            }
            return -1;
        }

        private static void Publish(object character) =>
            AccessTools.Method(character.GetType(), "PublishResults")?.Invoke(character, null);

        // ---------------- Diagnostics ----------------

        /// <summary>Logs a panel's raw selection values whenever they change.</summary>
        public static void LogSelection(object character, int slot)
        {
            if (_busy || !(character is UnityEngine.Object o) || !Plugin.DebugLogging.Value) return;
            string state =
                $"soapbox idx={Access.Get(character, "playerSoapbox", -1)} hat idx={Access.Get(character, "playerHat", -1)} color idx={Access.Get(character, "playerColor", -1)} " +
                $"=> {Describe(Game.SlotCosmetic(slot, "playersSoapbox"))} / {Describe(Game.SlotCosmetic(slot, "playersHat"))} / {Describe(Game.SlotCosmetic(slot, "playersColor"))}";
            int id = o.GetInstanceID();
            if (LastLogged.TryGetValue(id, out var last) && last == state) return;
            LastLogged[id] = state;
            Plugin.Debug($"Player {slot + 1} selection: {state}");
        }

        private static string Describe(object cosmetic) => $"{Game.CosmeticName(cosmetic) ?? "-"} [{Game.CompleteId(cosmetic)}]";
    }
}
