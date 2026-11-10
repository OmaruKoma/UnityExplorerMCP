using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace UnityExplorer.MCPBridge
{
    public sealed class LabelEntry
    {
        public string Type { get; set; }
        public string Member { get; set; }
        public string Label { get; set; }
        public string Note { get; set; }
        public string Updated { get; set; }
    }

    /// <summary>
    /// Per-install labels for types/members, plus deterministic aliases for
    /// members whose names cannot be read (stripped IL2CPP metadata).
    /// </summary>
    public static class Labels
    {
        private static readonly object Lock = new object();
        private static List<LabelEntry> _entries;
        private static bool _loaded;

        public static string FilePath()
        {
            return Path.Combine(BridgeConfig.ConfigDirectory(), "UnityExplorerMCP.labels.json");
        }

        public static string AliasFor(Type owner, string name)
        {
            if (!string.IsNullOrEmpty(name) && name[0] != '<') return null;
            string ownerName = owner != null ? (owner.FullName ?? owner.Name) : "?";
            string hash = ValueSerializer.Sha256Hex(
                System.Text.Encoding.UTF8.GetBytes(ownerName + "." + (name ?? "")));
            return "m_" + (hash ?? "000000").Substring(0, 6);
        }

        public static void Decorate(Type owner, string member, MemberValue entry)
        {
            if (entry == null) return;
            LabelEntry label = Find(owner != null ? owner.FullName : null, member);
            if (label != null)
            {
                entry.Label = label.Label;
                entry.Note = label.Note;
            }
        }

        public static void Decorate(Dictionary<string, object> entry, string typeName, string member)
        {
            if (entry == null) return;
            LabelEntry label = Find(typeName, member);
            if (label != null)
            {
                entry["label"] = label.Label;
                if (!string.IsNullOrEmpty(label.Note)) entry["label_note"] = label.Note;
            }
            if (!string.IsNullOrEmpty(member) && member[0] != '<') return;
            string hash = ValueSerializer.Sha256Hex(
                System.Text.Encoding.UTF8.GetBytes((typeName ?? "?") + "." + (member ?? "")));
            entry["alias"] = "m_" + (hash ?? "000000").Substring(0, 6);
        }

        public static LabelEntry Find(string type, string member)
        {
            lock (Lock)
            {
                EnsureLoaded();
                foreach (LabelEntry entry in _entries)
                {
                    if (string.Equals(entry.Type, type, StringComparison.Ordinal)
                        && string.Equals(entry.Member, member, StringComparison.Ordinal))
                        return entry;
                }
            }
            return null;
        }

        /// <summary>Accepts a real member name, a saved label, or an alias.</summary>
        public static string ResolveMemberName(Type type, string requested)
        {
            if (type == null || string.IsNullOrEmpty(requested)) return requested;
            lock (Lock)
            {
                EnsureLoaded();
                foreach (LabelEntry entry in _entries)
                {
                    if (string.Equals(entry.Type, type.FullName, StringComparison.Ordinal)
                        && string.Equals(entry.Label, requested, StringComparison.Ordinal))
                        return entry.Member;
                }
            }
            if (!requested.StartsWith("m_", StringComparison.Ordinal)) return requested;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static;
            try
            {
                foreach (FieldInfo field in type.GetFields(flags))
                    if (string.Equals(AliasFor(type, field.Name), requested, StringComparison.Ordinal))
                        return field.Name;
                foreach (PropertyInfo property in type.GetProperties(flags))
                    if (string.Equals(AliasFor(type, property.Name), requested, StringComparison.Ordinal))
                        return property.Name;
                foreach (MethodInfo method in type.GetMethods(flags))
                    if (string.Equals(AliasFor(type, method.Name), requested, StringComparison.Ordinal))
                        return method.Name;
            }
            catch { }
            return requested;
        }

        public static Dictionary<string, object> Set(string type, string member, string label, string note)
        {
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(label))
                throw new Exception("set_label needs 'type' and 'label'");
            lock (Lock)
            {
                EnsureLoaded();
                LabelEntry entry = null;
                foreach (LabelEntry existing in _entries)
                {
                    if (string.Equals(existing.Type, type, StringComparison.Ordinal)
                        && string.Equals(existing.Member, member, StringComparison.Ordinal))
                    {
                        entry = existing;
                        break;
                    }
                }
                if (entry == null)
                {
                    entry = new LabelEntry { Type = type, Member = member };
                    _entries.Add(entry);
                }
                entry.Label = label;
                entry.Note = note;
                entry.Updated = DateTime.UtcNow.ToString("o");
                Save();
                return new Dictionary<string, object>
                {
                    { "type", entry.Type },
                    { "member", entry.Member },
                    { "label", entry.Label },
                    { "note", entry.Note },
                    { "total", _entries.Count }
                };
            }
        }

        public static Dictionary<string, object> Remove(string type, string member)
        {
            lock (Lock)
            {
                EnsureLoaded();
                int removed = _entries.RemoveAll(e =>
                    string.Equals(e.Type, type, StringComparison.Ordinal)
                    && string.Equals(e.Member, member, StringComparison.Ordinal));
                if (removed > 0) Save();
                return new Dictionary<string, object>
                {
                    { "removed", removed },
                    { "total", _entries.Count }
                };
            }
        }

        public static Dictionary<string, object> Page(string typeContains, int limit, int cursor)
        {
            if (limit <= 0) limit = 100;
            if (limit > 500) limit = 500;
            lock (Lock)
            {
                EnsureLoaded();
                var matched = new List<LabelEntry>();
                foreach (LabelEntry entry in _entries)
                {
                    if (!string.IsNullOrEmpty(typeContains)
                        && (entry.Type ?? "").IndexOf(typeContains, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    matched.Add(entry);
                }
                matched.Sort((a, b) => string.CompareOrdinal(a.Type + "." + a.Member, b.Type + "." + b.Member));
                int start = Math.Min(Math.Max(0, cursor), matched.Count);
                var items = new List<Dictionary<string, object>>();
                for (int i = start; i < matched.Count && items.Count < limit; i++)
                {
                    LabelEntry e = matched[i];
                    items.Add(new Dictionary<string, object>
                    {
                        { "type", e.Type },
                        { "member", e.Member },
                        { "label", e.Label },
                        { "note", e.Note },
                        { "updated", e.Updated }
                    });
                }
                int consumed = start + items.Count;
                return new Dictionary<string, object>
                {
                    { "items", items },
                    { "total", matched.Count },
                    { "next_cursor", consumed < matched.Count ? (object)consumed : null }
                };
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _entries = new List<LabelEntry>();
            try
            {
                string path = FilePath();
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path);
                JsonElement root = (JsonElement)JsonSerializer.Deserialize<object>(json, new JsonSerializerOptions());
                if (root.ValueKind != JsonValueKind.Array) return;
                foreach (JsonElement item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var entry = new LabelEntry();
                    JsonElement value;
                    if (item.TryGetProperty("Type", out value) || item.TryGetProperty("type", out value))
                        entry.Type = value.GetString();
                    if (item.TryGetProperty("Member", out value) || item.TryGetProperty("member", out value))
                        entry.Member = value.GetString();
                    if (item.TryGetProperty("Label", out value) || item.TryGetProperty("label", out value))
                        entry.Label = value.GetString();
                    if (item.TryGetProperty("Note", out value) || item.TryGetProperty("note", out value))
                        entry.Note = value.GetString();
                    if (item.TryGetProperty("Updated", out value) || item.TryGetProperty("updated", out value))
                        entry.Updated = value.GetString();
                    if (!string.IsNullOrEmpty(entry.Type) && !string.IsNullOrEmpty(entry.Label))
                        _entries.Add(entry);
                }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                string path = FilePath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(_entries);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch { }
        }
    }
}
