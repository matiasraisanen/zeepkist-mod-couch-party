using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CouchParty
{
    /// <summary>
    /// Reads and writes game fields by name.
    /// The reference libraries we compile against make every game field public, but in the real game
    /// many are private, and touching those directly throws FieldAccessException. Reflection doesn't care.
    /// </summary>
    internal static class Access
    {
        private static readonly Dictionary<(Type, string), FieldInfo> Cache = new Dictionary<(Type, string), FieldInfo>();
        private static readonly HashSet<string> Reported = new HashSet<string>();

        private static FieldInfo Field(Type type, string name)
        {
            if (Cache.TryGetValue((type, name), out var field)) return field;
            field = AccessTools.Field(type, name);
            Cache[(type, name)] = field;
            if (field == null && Reported.Add(type.Name + "." + name))
                Plugin.Log.LogWarning($"Game field {type.Name}.{name} not found (game update?).");
            return field;
        }

        public static T Get<T>(object obj, string name, T fallback = default)
        {
            if (obj == null) return fallback;
            try
            {
                var value = Field(obj.GetType(), name)?.GetValue(obj);
                return value is T t ? t : fallback;
            }
            catch (Exception e)
            {
                if (Reported.Add("get:" + name)) Plugin.Log.LogWarning($"Reading {name} failed: {e.Message}");
                return fallback;
            }
        }

        public static bool Set(object obj, string name, object value)
        {
            if (obj == null) return false;
            try
            {
                var field = Field(obj.GetType(), name);
                if (field == null) return false;
                field.SetValue(obj, value);
                return true;
            }
            catch (Exception e)
            {
                if (Reported.Add("set:" + name)) Plugin.Log.LogWarning($"Writing {name} failed: {e.Message}");
                return false;
            }
        }

        /// <summary>Reads a list/array field as a plain IList (element types may themselves be private).</summary>
        public static IList List(object obj, string name) => Get<IList>(obj, name);

        public static object Item(IList list, int index) =>
            list != null && index >= 0 && index < list.Count ? list[index] : null;
    }
}
