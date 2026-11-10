using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// Bounded managed-method tracing through Harmony (reflected, so the build
    /// needs no Harmony reference). Mono only: patching IL2CPP interop proxies
    /// is not reliable, so the IL2CPP build reports the capability as off.
    /// </summary>
    public static class MethodTracer
    {
        public sealed class Record
        {
            public int Seq;
            public int Repeat = 1;
            public int LastSeq;
            public string At;
            public string Caller;
            public string Instance;
            public string Args;
            public string Result;
            public int Thread;
            public long ElapsedMs;
        }

        public sealed class Session
        {
            public string Id;
            public string TypeName;
            public string Method;
            public string Signature;
            public string Status = "active";
            public bool Active = true;
            public List<Record> Records = new List<Record>();
            public int Total;
            public DateTime Started;
            public object Harmony;
            public MethodInfo Original;
            public string HarmonyId;
        }

        private sealed class OpenCall
        {
            public Session Session;
            public long StartTicks;
        }

        [ThreadStatic]
        private static Stack<OpenCall> _open;

        private static readonly Dictionary<string, Session> Sessions =
            new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Lock = new object();
        private static int _next;

        public static bool Available
        {
            get
            {
#if CPP
                return false;
#else
                return HarmonyType() != null;
#endif
            }
        }

        public static Dictionary<string, object> Start(string typeName, string assembly,
            string methodName, int paramCount)
        {
            if (!Available)
                throw new Exception("TRACE_UNSUPPORTED: method tracing is Mono-only and needs Harmony loaded");
            Type type = AssemblyInspector.ResolveType(assembly, typeName);
            if (type == null) throw new Exception("Type not found: " + typeName);

            MethodInfo target = null;
            foreach (MethodInfo candidate in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static))
            {
                if (candidate.Name != methodName) continue;
                if (candidate.IsAbstract || candidate.ContainsGenericParameters) continue;
                if (paramCount > 0 && candidate.GetParameters().Length != paramCount) continue;
                target = candidate;
                break;
            }
            if (target == null)
                throw new Exception("Method not found: " + type.FullName + "." + methodName);

            string harmonyId = "unityexplorer.mcp.trace." + Guid.NewGuid().ToString("N");
            object harmony = CreateHarmony(harmonyId);
            Patch(harmony, target, harmonyId);

            var session = new Session
            {
                TypeName = type.FullName,
                Method = target.Name,
                Signature = Signature(target),
                Started = DateTime.UtcNow,
                Harmony = harmony,
                Original = target,
                HarmonyId = harmonyId,
                Status = "active"
            };
            lock (Lock)
            {
                _next++;
                session.Id = "trace-" + _next;
                Sessions[session.Id] = session;
                Evict();
            }
            return Summary(session);
        }

        public static Dictionary<string, object> Stop(string id)
        {
            Session session = Require(id);
            session.Active = false;
            session.Status = "stopped";
            try { Unpatch(session.Harmony, session.Original, session.HarmonyId); }
            catch { }
            return Summary(session);
        }

        public static Dictionary<string, object> Clear(string id)
        {
            Session session = Require(id);
            lock (Lock) { session.Records.Clear(); session.Total = 0; }
            return Summary(session);
        }

        public static List<Dictionary<string, object>> List()
        {
            var result = new List<Dictionary<string, object>>();
            lock (Lock)
            {
                foreach (Session session in Sessions.Values) result.Add(Summary(session));
            }
            return result;
        }

        public static Dictionary<string, object> Get(string id, int cursor, int limit)
        {
            Session session = Require(id);
            if (limit <= 0) limit = 100;
            if (limit > 250) limit = 250;
            if (cursor < 0) cursor = 0;

            List<Record> snapshot;
            int total;
            lock (Lock)
            {
                total = session.Records.Count;
                snapshot = session.Records.GetRange(
                    Math.Min(cursor, total),
                    Math.Max(0, Math.Min(limit, total - Math.Min(cursor, total))));
            }
            var items = new List<Dictionary<string, object>>();
            foreach (Record record in snapshot)
            {
                items.Add(new Dictionary<string, object>
                {
                    { "seq", record.Seq },
                    { "last_seq", record.LastSeq },
                    { "repeat", record.Repeat },
                    { "at", record.At },
                    { "caller", record.Caller },
                    { "instance", record.Instance },
                    { "args", record.Args },
                    { "result", record.Result },
                    { "thread", record.Thread },
                    { "elapsed_ms", record.ElapsedMs }
                });
            }
            int consumed = Math.Min(cursor, total) + items.Count;
            return new Dictionary<string, object>
            {
                { "trace_id", session.Id },
                { "target", session.TypeName + "." + session.Method },
                { "status", session.Status },
                { "records", items },
                { "total", total },
                { "captured", session.Total },
                { "next_cursor", consumed < total ? (object)consumed : null }
            };
        }

        public static Dictionary<string, object> BuildGraph(string id, int cursor, int limit)
        {
            Session session = Require(id);
            if (limit <= 0) limit = 100;
            if (limit > 250) limit = 250;
            if (cursor < 0) cursor = 0;

            var edges = new Dictionary<string, Dictionary<string, object>>();
            lock (Lock)
            {
                foreach (Record record in session.Records)
                {
                    string caller = string.IsNullOrEmpty(record.Caller) ? "<unknown>" : record.Caller;
                    string key = caller + " -> " + session.TypeName + "." + session.Method;
                    Dictionary<string, object> edge;
                    if (!edges.TryGetValue(key, out edge))
                    {
                        edge = new Dictionary<string, object>
                        {
                            { "from", caller },
                            { "to", session.TypeName + "." + session.Method },
                            { "count", 0 },
                            { "sample_args", record.Args }
                        };
                        edges[key] = edge;
                    }
                    edge["count"] = (int)edge["count"] + record.Repeat;
                }
            }

            var ordered = new List<Dictionary<string, object>>(edges.Values);
            ordered.Sort((a, b) => ((int)b["count"]).CompareTo((int)a["count"]));
            var items = new List<Dictionary<string, object>>();
            for (int i = cursor; i < ordered.Count && items.Count < limit; i++) items.Add(ordered[i]);
            int consumed = Math.Min(cursor, ordered.Count) + items.Count;
            return new Dictionary<string, object>
            {
                { "trace_id", session.Id },
                { "edges", items },
                { "total", ordered.Count },
                { "next_cursor", consumed < ordered.Count ? (object)consumed : null }
            };
        }

        private static void Prefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                Session session = FindSession(__originalMethod);
                if (session == null || !session.Active) return;
                if (_open == null) _open = new Stack<OpenCall>();
                _open.Push(new OpenCall { Session = session, StartTicks = DateTime.UtcNow.Ticks });
                session.Total++;
            }
            catch { }
        }

        private static void Postfix(object __instance, object[] __args, ref object __result,
            MethodBase __originalMethod)
        {
            try
            {
                Session session = FindSession(__originalMethod);
                if (session == null) return;
                OpenCall open = null;
                if (_open != null)
                {
                    var temp = new Stack<OpenCall>();
                    while (_open.Count > 0)
                    {
                        OpenCall candidate = _open.Pop();
                        if (candidate.Session == session) { open = candidate; break; }
                        temp.Push(candidate);
                    }
                    while (temp.Count > 0) _open.Push(temp.Pop());
                }
                if (open == null) return;

                long elapsed = (DateTime.UtcNow.Ticks - open.StartTicks) / TimeSpan.TicksPerMillisecond;
                string args = SerializeArgs(__args);
                string caller = DescribeCaller();
                string instanceText = DescribeInstance(__instance);
                string result = SafeSerialize(__result);

                lock (Lock)
                {
                    Record last = session.Records.Count > 0
                        ? session.Records[session.Records.Count - 1] : null;
                    if (last != null && last.Caller == caller && last.Args == args
                        && last.Instance == instanceText)
                    {
                        last.Repeat++;
                        last.LastSeq = session.Total;
                        last.ElapsedMs += elapsed;
                        last.Result = result;
                    }
                    else
                    {
                        session.Records.Add(new Record
                        {
                            Seq = session.Total,
                            LastSeq = session.Total,
                            At = DateTime.UtcNow.ToString("o"),
                            Caller = caller,
                            Instance = instanceText,
                            Args = args,
                            Result = result,
                            Thread = System.Threading.Thread.CurrentThread.ManagedThreadId,
                            ElapsedMs = elapsed
                        });
                        while (session.Records.Count > BridgeConfig.MaxTraceRecords)
                            session.Records.RemoveAt(0);
                    }
                }
            }
            catch { }
        }

        private static string SerializeArgs(object[] args)
        {
            try
            {
                var list = new List<object>();
                if (args != null)
                {
                    int take = Math.Min(args.Length, 16);
                    for (int i = 0; i < take; i++) list.Add(ValueSerializer.Serialize(args[i], "hex"));
                }
                string json = JsonSerializer.Serialize(list);
                return json.Length > 2000 ? json.Substring(0, 2000) + "..." : json;
            }
            catch { return "<unserializable>"; }
        }

        private static string SafeSerialize(object value)
        {
            try
            {
                string json = JsonSerializer.Serialize(ValueSerializer.Serialize(value, "hex"));
                return json.Length > 1000 ? json.Substring(0, 1000) + "..." : json;
            }
            catch { return "<unserializable>"; }
        }

        private static string DescribeInstance(object instance)
        {
            if (instance == null) return null;
            try
            {
                UnityEngine.Object uo = instance as UnityEngine.Object;
                if (uo != null) return uo.GetType().Name + ":" + uo.name;
                string text = instance.ToString();
                return text != null && text.Length > 120 ? text.Substring(0, 120) : text;
            }
            catch { return instance.GetType().Name; }
        }

        private static string DescribeCaller()
        {
            try
            {
                var trace = new StackTrace(2, false);
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    MethodBase method = trace.GetFrame(i).GetMethod();
                    if (method == null) continue;
                    Type declaring = method.DeclaringType;
                    string full = declaring != null ? declaring.FullName : null;
                    if (string.IsNullOrEmpty(full)) continue;
                    if (full.StartsWith("UnityExplorer.MCPBridge.MethodTracer")) continue;
                    if (full.StartsWith("HarmonyLib.")) continue;
                    return full + "." + method.Name;
                }
            }
            catch { }
            return null;
        }

        private static Session FindSession(MethodBase original)
        {
            if (original == null) return null;
            lock (Lock)
            {
                foreach (Session session in Sessions.Values)
                    if (session.Active && session.Original != null && session.Original.Equals(original))
                        return session;
            }
            return null;
        }

        private static Session Require(string id)
        {
            lock (Lock)
            {
                Session session;
                if (Sessions.TryGetValue(id ?? "", out session)) return session;
            }
            throw new Exception("Unknown trace id: " + id);
        }

        private static Dictionary<string, object> Summary(Session session)
        {
            int count;
            lock (Lock) count = session.Records.Count;
            return new Dictionary<string, object>
            {
                { "trace_id", session.Id },
                { "target", session.TypeName + "." + session.Method },
                { "signature", session.Signature },
                { "status", session.Status },
                { "active", session.Active },
                { "records", count },
                { "captured", session.Total },
                { "started_at", session.Started.ToString("o") }
            };
        }

        private static void Evict()
        {
            if (Sessions.Count <= BridgeConfig.MaxTraceSessions) return;
            string oldestId = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (KeyValuePair<string, Session> pair in Sessions)
            {
                if (pair.Value.Active) continue;
                if (pair.Value.Started < oldest)
                {
                    oldest = pair.Value.Started;
                    oldestId = pair.Key;
                }
            }
            if (oldestId != null) Sessions.Remove(oldestId);
        }

        private static string Signature(MethodInfo method)
        {
            var parts = new List<string>();
            foreach (ParameterInfo parameter in method.GetParameters())
                parts.Add(parameter.ParameterType.Name + " " + parameter.Name);
            return method.ReturnType.Name + " " + method.Name + "(" + string.Join(", ", parts.ToArray()) + ")";
        }

        // ---- Harmony reflection -------------------------------------------------

        private static Type _harmonyType;
        private static bool _harmonyProbed;

        private static Type HarmonyType()
        {
            if (_harmonyProbed) return _harmonyType;
            _harmonyProbed = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType("HarmonyLib.Harmony");
                    if (type != null) { _harmonyType = type; return type; }
                }
                catch { }
            }
            return null;
        }

        private static object CreateHarmony(string id)
        {
            Type type = HarmonyType();
            if (type == null) throw new Exception("Harmony is not loaded");
            try { return Activator.CreateInstance(type, new object[] { id }); }
            catch
            {
                try { return Activator.CreateInstance(type); }
                catch (Exception ex) { throw new Exception("Cannot create Harmony instance: " + ex.Message); }
            }
        }

        private static void Patch(object harmony, MethodInfo target, string id)
        {
            Type harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
            if (harmonyMethodType == null) throw new Exception("HarmonyMethod type not found");
            MethodInfo prefix = typeof(MethodTracer).GetMethod("Prefix",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postfix = typeof(MethodTracer).GetMethod("Postfix",
                BindingFlags.Static | BindingFlags.NonPublic);
            object prefixHandle = Activator.CreateInstance(harmonyMethodType, new object[] { prefix });
            object postfixHandle = Activator.CreateInstance(harmonyMethodType, new object[] { postfix });

            MethodInfo patch = null;
            foreach (MethodInfo candidate in harmony.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (candidate.Name != "Patch") continue;
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length < 3) continue;
                if (!parameters[0].ParameterType.IsAssignableFrom(typeof(MethodInfo))) continue;
                if (patch == null || parameters.Length > patch.GetParameters().Length)
                    patch = candidate;
            }
            if (patch == null) throw new Exception("Harmony.Patch signature not found");

            ParameterInfo[] ps = patch.GetParameters();
            object[] args = new object[ps.Length];
            args[0] = target;
            if (ps.Length > 1) args[1] = prefixHandle;
            if (ps.Length > 2) args[2] = postfixHandle;
            try { patch.Invoke(harmony, args); }
            catch (Exception ex)
            {
                throw new Exception("Harmony patch failed: " + (ex.InnerException ?? ex).Message);
            }
        }

        private static void Unpatch(object harmony, MethodInfo target, string id)
        {
            foreach (MethodInfo candidate in harmony.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (candidate.Name != "Unpatch") continue;
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != 3) continue;
                object[] args = new object[3];
                args[0] = target;
                Type patchType = parameters[1].ParameterType;
                Type underlying = Nullable.GetUnderlyingType(patchType);
                if (underlying != null) patchType = underlying;
                args[1] = Enum.Parse(patchType, "All");
                args[2] = id;
                candidate.Invoke(harmony, args);
                return;
            }
        }

        private static Type FindType(string fullName)
        {
            Type direct = Type.GetType(fullName);
            if (direct != null) return direct;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }
    }
}
