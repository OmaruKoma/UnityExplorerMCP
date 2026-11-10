using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// Time-sliced instance scans: the expensive object snapshot happens at
    /// start, matching runs a bounded slice per frame so the main thread
    /// stays responsive and the client can poll progress.
    /// </summary>
    public static class ScanJobs
    {
        private const int SliceSize = 1500;
        private const int MaxJobs = 4;

        private sealed class Job
        {
            public string Id;
            public Type Type;
            public bool IncludeInactive;
            public UnityEngine.Object[] Objects;
            public int Total;
            public int Index;
            public int Processed;
            public int Dropped;
            public bool Done;
            public DateTime Started;
            public List<Dictionary<string, object>> Results = new List<Dictionary<string, object>>();
        }

        private static readonly Dictionary<string, Job> Jobs =
            new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        private static int _next;

        public static Dictionary<string, object> Start(string typeName, string assembly,
            bool includeInactive)
        {
            Type type = AssemblyInspector.ResolveType(assembly, typeName);
            if (type == null) throw new Exception("Type not found: " + typeName);

            var job = new Job
            {
                Type = type,
                IncludeInactive = includeInactive,
                Started = DateTime.UtcNow
            };
            UnityEngine.Object[] objects;
            try { objects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>(); }
            catch (Exception ex)
            {
                throw new Exception("Instance enumeration failed: " + ex.Message);
            }
            job.Objects = objects ?? new UnityEngine.Object[0];
            job.Total = job.Objects.Length;

            lock (Jobs)
            {
                _next++;
                job.Id = "scan-" + _next;
                Jobs[job.Id] = job;
                Evict();
            }

            return new Dictionary<string, object>
            {
                { "scan_id", job.Id },
                { "type", type.FullName },
                { "total", job.Total },
                { "processed", 0 },
                { "done", false }
            };
        }

        public static void Tick()
        {
            Job job = null;
            lock (Jobs)
            {
                foreach (Job candidate in Jobs.Values)
                {
                    if (!candidate.Done) { job = candidate; break; }
                }
            }
            if (job == null) return;

            int end = Math.Min(job.Objects.Length, job.Index + SliceSize);
            for (; job.Index < end; job.Index++)
            {
                UnityEngine.Object obj = job.Objects[job.Index];
                job.Processed++;
                if (obj == null) continue;
                try
                {
                    Type concrete = MCPBridge.ConcreteType(obj);
                    if (concrete == null || !job.Type.IsAssignableFrom(concrete)) continue;
                    GameObject go = obj as GameObject;
                    if (go == null && obj is Component) go = ((Component)obj).gameObject;
                    if (go != null && !job.IncludeInactive && !go.activeInHierarchy) continue;
                    if (job.Results.Count >= BridgeConfig.MaxScanResults)
                    {
                        job.Dropped++;
                        continue;
                    }
                    var entry = new Dictionary<string, object>
                    {
                        { "type", concrete.FullName ?? job.Type.FullName },
                        { "instance_id", obj.GetInstanceID() },
                        { "handle", Handle.Format(obj.GetInstanceID()) }
                    };
                    if (go != null)
                    {
                        entry["name"] = go.name;
                        entry["scene"] = go.scene.name;
                        entry["active"] = go.activeInHierarchy;
                        entry["path"] = AssemblyInspector.GetGameObjectPath(go);
                    }
                    else
                    {
                        entry["name"] = obj.name;
                    }
                    job.Results.Add(entry);
                }
                catch { }
            }

            if (job.Index >= job.Total)
            {
                job.Done = true;
                job.Objects = null; // results are all that matter after completion
            }
        }

        public static Dictionary<string, object> Get(string id, int limit, int cursor)
        {
            Job job;
            lock (Jobs)
            {
                if (!Jobs.TryGetValue(id ?? "", out job))
                    throw new Exception("Unknown scan id: " + id);
            }
            if (limit <= 0) limit = 100;
            if (limit > 250) limit = 250;
            if (cursor < 0) cursor = 0;

            var items = new List<Dictionary<string, object>>();
            int start = Math.Min(cursor, job.Results.Count);
            for (int i = start; i < job.Results.Count && items.Count < limit; i++)
                items.Add(job.Results[i]);
            int consumed = start + items.Count;
            return new Dictionary<string, object>
            {
                { "scan_id", job.Id },
                { "type", job.Type.FullName },
                { "total", job.Total },
                { "processed", job.Processed },
                { "done", job.Done },
                { "found", job.Results.Count },
                { "dropped", job.Dropped },
                { "items", items },
                { "next_cursor", consumed < job.Results.Count ? (object)consumed : null }
            };
        }

        private static void Evict()
        {
            if (Jobs.Count <= MaxJobs) return;
            string oldestId = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (KeyValuePair<string, Job> pair in Jobs)
            {
                if (!pair.Value.Done) continue;
                if (pair.Value.Started < oldest)
                {
                    oldest = pair.Value.Started;
                    oldestId = pair.Key;
                }
            }
            if (oldestId != null) Jobs.Remove(oldestId);
        }
    }
}
