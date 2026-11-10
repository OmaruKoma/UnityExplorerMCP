using System;
using System.Collections.Generic;

namespace UnityExplorer.MCPBridge
{
    public sealed class SnapshotRecord
    {
        public string Id;
        public string Target;
        public string Session;
        public string TypeName;
        public bool Static;
        public int InstanceId;
        public object Live;
        public Type Type;
        public List<string> Members = new List<string>();
        public Dictionary<string, string> Canonical = new Dictionary<string, string>();
        public Dictionary<string, double> Numbers = new Dictionary<string, double>();
        public DateTime At;
    }

    /// <summary>Bounded member-value snapshots used by diff_snapshot.</summary>
    public static class SnapshotStore
    {
        private static readonly List<SnapshotRecord> Records = new List<SnapshotRecord>();
        private static readonly object Lock = new object();
        private static int _next;

        public static SnapshotRecord Add(string target, Type type, object instance,
            bool isStatic, int memberLimit, bool includeProperties, string memberContains,
            string session, out int memberCount)
        {
            List<MemberValue> members = MemberTools.Enumerate(
                type, isStatic ? null : instance, includeProperties,
                memberContains, memberLimit, out memberCount);

            var record = new SnapshotRecord
            {
                Target = target,
                Session = session,
                TypeName = type != null ? (type.FullName ?? type.Name) : null,
                Static = isStatic,
                InstanceId = instance is UnityEngine.Object ? ((UnityEngine.Object)instance).GetInstanceID() : 0,
                Live = instance,
                Type = type,
                At = DateTime.UtcNow
            };
            foreach (MemberValue member in members)
            {
                if (member.Name == null) continue;
                if (!record.Canonical.ContainsKey(member.Name))
                {
                    record.Members.Add(member.Name);
                    record.Canonical[member.Name] = MemberTools.Canonical(member.Value);
                }
                double number;
                if (TryNumber(member.Value, out number)) record.Numbers[member.Name] = number;
            }

            lock (Lock)
            {
                _next++;
                record.Id = "snap-" + _next;
                Records.Add(record);
                while (Records.Count > BridgeConfig.MaxSnapshots)
                    Records.RemoveAt(0);
            }
            return record;
        }

        public static SnapshotRecord Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (Lock)
            {
                foreach (SnapshotRecord record in Records)
                    if (string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase))
                        return record;
            }
            return null;
        }

        public static bool TryNumber(object value, out double number)
        {
            number = 0;
            if (value == null) return false;
            Type t = value.GetType();
            if (!(t.IsPrimitive || t == typeof(decimal))) return false;
            if (t == typeof(bool) || t == typeof(char)) return false;
            try { number = Convert.ToDouble(value); return true; }
            catch { return false; }
        }

    }
}
