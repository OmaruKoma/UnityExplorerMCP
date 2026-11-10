using System;
using System.Collections.Generic;

namespace UnityExplorer.MCPBridge
{
    public sealed class UndoEntry
    {
        public string Kind;          // field | property | static_field | static_property
        public string Handle;
        public int InstanceId;
        public string TypeName;
        public string Member;
        public object OldValue;
        public string OldText;
        public string Session;
        public DateTime At;
    }

    /// <summary>Last N scalar writes, newest last. Undo pops from the end.</summary>
    public static class UndoStore
    {
        private static readonly List<UndoEntry> Entries = new List<UndoEntry>();
        private static readonly object Lock = new object();

        public static void Record(UndoEntry entry)
        {
            if (entry == null) return;
            lock (Lock)
            {
                Entries.Add(entry);
                while (Entries.Count > BridgeConfig.MaxUndo) Entries.RemoveAt(0);
            }
        }

        public static List<UndoEntry> Take(int steps)
        {
            var taken = new List<UndoEntry>();
            lock (Lock)
            {
                while (taken.Count < steps && Entries.Count > 0)
                {
                    int last = Entries.Count - 1;
                    taken.Add(Entries[last]);
                    Entries.RemoveAt(last);
                }
            }
            return taken;
        }

        public static int Count
        {
            get { lock (Lock) return Entries.Count; }
        }
    }
}
