using System;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace CouchParty
{
    /// <summary>
    /// Friend names as the friends set them themselves.
    /// Friend.Name returns the nickname you gave them (if any). Steam's persona-name history entry 0
    /// is their own current name, so we read that instead.
    /// </summary>
    internal static class SteamNames
    {
        private static bool _resolved;
        private static object _friendsInterface;
        private static MethodInfo _historyMethod;

        public static string OwnName(Friend friend)
        {
            string fallback = friend.Name;
            try
            {
                Resolve();
                if (_historyMethod == null) return fallback;
                var name = _historyMethod.Invoke(_friendsInterface, new object[] { friend.Id, 0 }) as string;
                return string.IsNullOrWhiteSpace(name) ? fallback : name;
            }
            catch (Exception e)
            {
                Plugin.Debug($"Reading own Steam name failed, using nickname: {e.Message}");
                return fallback;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            _friendsInterface = AccessTools.Property(typeof(SteamFriends), "Internal")?.GetValue(null, null);
            if (_friendsInterface != null)
                _historyMethod = AccessTools.Method(_friendsInterface.GetType(), "GetFriendPersonaNameHistory");
            if (_historyMethod == null)
                Plugin.Log.LogWarning("Steam name history not available; friend names may show your nicknames.");
        }
    }
}
