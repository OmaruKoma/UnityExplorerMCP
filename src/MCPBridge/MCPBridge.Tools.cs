using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
#if CPP
using Il2CppInterop.Runtime;
#endif

namespace UnityExplorer.MCPBridge
{
    public partial class MCPBridge
    {
        // ---- shared helpers ---------------------------------------------------

        private MCPResponse ToolError(string code, string message, object data = null)
        {
            var payload = new Dictionary<string, object>
            {
                { "code", code },
                { "message", message }
            };
            if (data != null) payload["data"] = data;
            return new MCPResponse { Success = false, Error = message, Data = payload };
        }

        private MCPResponse InvalidParams(string message)
        {
            return ToolError("INVALID_PARAMS", message);
        }

        /// <summary>
        /// Concrete managed type for a Unity object. IL2CPP proxies obtained
        /// through a base-typed collection report UnityEngine.Object; resolve
        /// the real class from the native klass so field/property lookup works.
        /// </summary>
        internal static Type ConcreteType(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            Type type = null;
            try { type = obj.GetType(); } catch { }
            if (type != null && type != typeof(UnityEngine.Object)
                && type != typeof(UnityEngine.Component) && type != typeof(UnityEngine.GameObject))
                return type;
#if CPP
            try
            {
                IntPtr klass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
                IntPtr nsPtr = IL2CPP.il2cpp_class_get_namespace(klass);
                IntPtr namePtr = IL2CPP.il2cpp_class_get_name(klass);
                string ns = Marshal.PtrToStringAnsi(nsPtr);
                string name = Marshal.PtrToStringAnsi(namePtr);
                if (!string.IsNullOrEmpty(name))
                {
                    string full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    Type resolved = AssemblyInspector.ResolveType(null, full);
                    if (resolved != null) return resolved;
                }
            }
            catch { }
#endif
            return type ?? typeof(UnityEngine.Object);
        }

        /// <summary>
        /// Re-wraps a base-typed IL2CPP proxy (Object/Component) into its concrete
        /// proxy so reflection members declared on the real type can be used.
        /// </summary>
        internal static UnityEngine.Object ConcreteProxy(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            Type type = ConcreteType(obj);
            if (type == null) return obj;
            Type actual = null;
            try { actual = obj.GetType(); } catch { }
            if (actual != null && type.IsAssignableFrom(actual)) return obj;
#if CPP
            try
            {
                UnityEngine.Object wrapped = Activator.CreateInstance(type, new object[] { obj.Pointer }) as UnityEngine.Object;
                if (wrapped != null) return wrapped;
            }
            catch { }
#endif
            return obj;
        }

        private static int SafePid()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        private void RecordUndo(string kind, int instanceId, string typeName, string member,
            Type valueType, object oldValue)
        {
            if (!MemberTools.IsScalar(valueType) || MemberTools.IsPointerLike(valueType)) return;
            UndoStore.Record(new UndoEntry
            {
                Kind = kind,
                InstanceId = instanceId,
                Handle = instanceId != 0 ? Handle.Format(instanceId) : null,
                TypeName = typeName,
                Member = member,
                OldValue = oldValue,
                OldText = oldValue != null ? oldValue.ToString() : "null",
                Session = SessionId,
                At = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Resolve a request target: session handle / instance id for a live
        /// object, or a type name for static members.
        /// </summary>
        private bool ResolveTarget(MCPRequest request, out Type type, out object instance,
            out bool isStatic, out string target, out MCPResponse error)
        {
            type = null;
            instance = null;
            isStatic = false;
            target = null;
            error = null;

            error = ValidateReadHandle(request);
            if (error != null) return false;

            int instanceId = ExtractInstanceId(request);
            if (instanceId != 0)
            {
                instance = FindObjectById(instanceId);
                if (instance == null)
                {
                    error = ToolError("OBJECT_NOT_FOUND", StaleHandleError(instanceId));
                    return false;
                }
                type = ConcreteType((UnityEngine.Object)instance);
                target = Handle.Format(instanceId);
                return true;
            }

            string typeName = ExtractString(request, "type_name")
                ?? ExtractString(request, "type") ?? ExtractString(request, "Type");
            if (string.IsNullOrEmpty(typeName))
            {
                error = InvalidParams("Missing handle/instance_id (live object) or type (static)");
                return false;
            }
            string assembly = ExtractString(request, "assembly") ?? ExtractString(request, "Assembly");
            type = AssemblyInspector.ResolveType(assembly, typeName);
            if (type == null)
            {
                error = ToolError("TYPE_NOT_FOUND", "Type not found: " + typeName);
                return false;
            }
            isStatic = true;
            target = "static:" + type.FullName;
            return true;
        }

        // ---- snapshots --------------------------------------------------------

        private MCPResponse HandleSnapshotObject(MCPRequest request)
        {
            Type type;
            object instance;
            bool isStatic;
            string target;
            MCPResponse error;
            if (!ResolveTarget(request, out type, out instance, out isStatic, out target, out error))
                return error;

            int limit = 200;
            bool includeProperties = false;
            string contains = null;
            if (request.Params is JsonElement j)
            {
                JsonElement tmp;
                if (j.TryGetProperty("member_limit", out tmp)) { try { limit = Math.Max(1, tmp.GetInt32()); } catch { } }
                if (j.TryGetProperty("include_properties", out tmp)) { try { includeProperties = tmp.GetBoolean(); } catch { } }
                if (j.TryGetProperty("member_contains", out tmp)) { try { contains = tmp.GetString(); } catch { } }
            }

            int total;
            SnapshotRecord record;
            try
            {
                record = SnapshotStore.Add(target, type, instance, isStatic, limit,
                    includeProperties, contains, SessionId, out total);
            }
            catch (Exception ex)
            {
                return ToolError("SNAPSHOT_FAILED", ex.Message);
            }

            var data = new Dictionary<string, object>
            {
                { "snapshot", record.Id },
                { "target", target },
                { "type", record.TypeName },
                { "members", record.Members.Count },
                { "total", total },
                { "session", record.Session },
                { "at", record.At.ToString("o") }
            };
            if (total > record.Members.Count)
            {
                data["truncated"] = true;
                data["note"] = "Increase member_limit to snapshot the remaining members.";
            }
            return new MCPResponse { Success = true, Data = data };
        }

        private MCPResponse HandleDiffSnapshot(MCPRequest request)
        {
            string id = ExtractString(request, "snapshot");
            string against = ExtractString(request, "against");
            bool rebase = false;
            double epsilon = 0;
            if (request.Params is JsonElement j)
            {
                JsonElement tmp;
                if (j.TryGetProperty("rebase", out tmp)) { try { rebase = tmp.GetBoolean(); } catch { } }
                if (j.TryGetProperty("epsilon", out tmp)) { try { epsilon = tmp.GetDouble(); } catch { } }
            }

            SnapshotRecord before = SnapshotStore.Get(id);
            if (before == null) return ToolError("SNAPSHOT_NOT_FOUND", "Unknown snapshot: " + id);

            Dictionary<string, string> afterCanonical = new Dictionary<string, string>();
            Dictionary<string, double> afterNumbers = new Dictionary<string, double>();

            if (!string.IsNullOrEmpty(against))
            {
                SnapshotRecord other = SnapshotStore.Get(against);
                if (other == null) return ToolError("SNAPSHOT_NOT_FOUND", "Unknown snapshot: " + against);
                foreach (KeyValuePair<string, string> pair in other.Canonical)
                    afterCanonical[pair.Key] = pair.Value;
                foreach (KeyValuePair<string, double> pair in other.Numbers)
                    afterNumbers[pair.Key] = pair.Value;
            }
            else
            {
                if (!string.Equals(before.Session, SessionId, StringComparison.Ordinal))
                    return ToolError("STALE_SNAPSHOT",
                        "Snapshot belongs to session " + before.Session + " (current " + SessionId + "). Take a new snapshot.");
                Type liveType = before.Type ?? AssemblyInspector.ResolveType(null, before.TypeName);
                object live = before.Static ? null : FindObjectById(before.InstanceId);
                if (!before.Static && live == null)
                    return ToolError("STALE_SNAPSHOT", "Snapshot target was destroyed. Take a new snapshot.");
                if (liveType == null)
                    return ToolError("TYPE_NOT_FOUND", "Type not found: " + before.TypeName);

                foreach (string member in before.Members)
                {
                    object value;
                    Type valueType;
                    bool isStatic;
                    bool isProperty;
                    string error;
                    if (!MemberTools.TryRead(liveType, live, member, out value, out valueType,
                        out isStatic, out isProperty, out error))
                        continue;
                    afterCanonical[member] = MemberTools.Canonical(value);
                    double number;
                    if (SnapshotStore.TryNumber(value, out number)) afterNumbers[member] = number;
                }
            }

            var changed = new List<Dictionary<string, object>>();
            int unchanged = 0;
            foreach (string member in before.Members)
            {
                string previous;
                if (!before.Canonical.TryGetValue(member, out previous)) continue;
                string current;
                if (!afterCanonical.TryGetValue(member, out current)) continue;

                if (string.Equals(previous, current, StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }
                double previousNumber;
                double currentNumber;
                if (epsilon > 0
                    && before.Numbers.TryGetValue(member, out previousNumber)
                    && afterNumbers.TryGetValue(member, out currentNumber)
                    && Math.Abs(previousNumber - currentNumber) <= epsilon)
                {
                    unchanged++;
                    continue;
                }
                changed.Add(new Dictionary<string, object>
                {
                    { "member", member },
                    { "from", previous },
                    { "to", current }
                });
            }

            var added = new List<string>();
            var removed = new List<string>();
            foreach (string member in afterCanonical.Keys)
                if (!before.Canonical.ContainsKey(member)) added.Add(member);
            foreach (string member in before.Members)
                if (!afterCanonical.ContainsKey(member)) removed.Add(member);

            if (rebase)
            {
                foreach (KeyValuePair<string, string> pair in afterCanonical)
                    before.Canonical[pair.Key] = pair.Value;
                before.Numbers.Clear();
                foreach (KeyValuePair<string, double> pair in afterNumbers)
                    before.Numbers[pair.Key] = pair.Value;
                before.At = DateTime.UtcNow;
            }

            var data = new Dictionary<string, object>
            {
                { "snapshot", before.Id },
                { "against", string.IsNullOrEmpty(against) ? null : against },
                { "checked", before.Members.Count },
                { "unchanged", unchanged },
                { "changed", changed },
                { "added", added },
                { "removed", removed },
                { "rebased", rebase }
            };
            if (changed.Count > 500)
            {
                data["changed"] = changed.GetRange(0, 500);
                data["truncated"] = true;
            }
            return new MCPResponse { Success = true, Data = data };
        }

        // ---- undo -------------------------------------------------------------

        private MCPResponse HandleUndoWrite(MCPRequest request)
        {
            int steps = 1;
            if (request.Params is JsonElement j)
            {
                JsonElement tmp;
                if (j.TryGetProperty("steps", out tmp)) { try { steps = tmp.GetInt32(); } catch { } }
            }
            if (steps < 1) steps = 1;
            if (steps > BridgeConfig.MaxUndo) steps = BridgeConfig.MaxUndo;

            List<UndoEntry> entries = UndoStore.Take(steps);
            if (entries.Count == 0)
                return ToolError("NOTHING_TO_UNDO", "No recorded writes to revert.");

            var results = new List<Dictionary<string, object>>();
            foreach (UndoEntry entry in entries)
            {
                var row = new Dictionary<string, object>
                {
                    { "member", entry.Member },
                    { "type", entry.TypeName },
                    { "kind", entry.Kind },
                    { "handle", entry.Handle },
                    { "restored", entry.OldText }
                };
                try
                {
                    Type type = AssemblyInspector.ResolveType(null, entry.TypeName);
                    object instance = entry.InstanceId != 0 ? FindObjectById(entry.InstanceId) : null;
                    if (type == null) throw new Exception("Type no longer resolves: " + entry.TypeName);
                    if (entry.InstanceId != 0 && instance == null)
                        throw new Exception("Object no longer exists (stale handle)");

                    FieldInfo field;
                    PropertyInfo property;
                    string error;
                    if (!MemberTools.TryResolveMember(type, entry.Member, out field, out property, out error))
                        throw new Exception(error);

                    object converted = MemberTools.ConvertForWrite(entry.OldValue,
                        field != null ? field.FieldType : property.PropertyType);
                    if (field != null) field.SetValue(field.IsStatic ? null : instance, converted);
                    else property.SetValue(property.GetIsStatic() ? null : instance, converted, null);
                    row["ok"] = true;
                }
                catch (Exception ex)
                {
                    row["ok"] = false;
                    row["error"] = ex.Message;
                }
                results.Add(row);
            }

            return new MCPResponse
            {
                Success = true,
                Data = new Dictionary<string, object>
                {
                    { "applied", results.Count },
                    { "remaining", UndoStore.Count },
                    { "results", results }
                }
            };
        }

        // ---- scans ------------------------------------------------------------

        private MCPResponse HandleStartInstanceScan(MCPRequest request)
        {
            string typeName = ExtractString(request, "type") ?? ExtractString(request, "Type");
            if (string.IsNullOrEmpty(typeName))
                return InvalidParams("Missing 'type' (full type name)");
            string assembly = ExtractString(request, "assembly") ?? ExtractString(request, "Assembly");
            bool includeInactive = true;
            if (request.Params is JsonElement j)
            {
                JsonElement tmp;
                if (j.TryGetProperty("include_inactive", out tmp)) { try { includeInactive = tmp.GetBoolean(); } catch { } }
            }
            try
            {
                return new MCPResponse { Success = true, Data = ScanJobs.Start(typeName, assembly, includeInactive) };
            }
            catch (Exception ex)
            {
                return ToolError("SCAN_FAILED", ex.Message);
            }
        }

        private MCPResponse HandleGetInstanceScan(MCPRequest request)
        {
            string id = ExtractString(request, "scan_id");
            if (string.IsNullOrEmpty(id)) return InvalidParams("Missing 'scan_id'");
            int limit = 100;
            int cursor = 0;
            if (request.Params is JsonElement j)
            {
                var paging = Paging.Read(j, 100);
                limit = paging.Limit > 0 ? paging.Limit : 100;
                cursor = paging.Cursor;
            }
            try
            {
                return new MCPResponse { Success = true, Data = ScanJobs.Get(id, limit, cursor) };
            }
            catch (Exception ex)
            {
                return ToolError("SCAN_NOT_FOUND", ex.Message);
            }
        }

        // ---- values -----------------------------------------------------------

        private MCPResponse HandleSearchValues(MCPRequest request)
        {
            string needle = ExtractString(request, "value");
            if (needle == null) return InvalidParams("Missing 'value'");
            string fieldContains = ExtractString(request, "field_contains");
            string componentType = ExtractString(request, "component_type");
            string scene = ExtractString(request, "scene");
            string matchMode = ExtractString(request, "match_mode");
            bool includeInactive = true;
            int limit = 100;
            int cursor = 0;
            if (request.Params is JsonElement j)
            {
                JsonElement tmp;
                if (j.TryGetProperty("include_inactive", out tmp)) { try { includeInactive = tmp.GetBoolean(); } catch { } }
                var paging = Paging.Read(j, 100);
                limit = paging.Limit > 0 ? paging.Limit : 100;
                cursor = paging.Cursor;
            }
            try
            {
                int scanned;
                bool truncated;
                var data = ValueTools.SearchValues(needle, fieldContains, componentType, scene,
                    includeInactive, matchMode, limit, cursor, out scanned, out truncated);
                return new MCPResponse { Success = true, Data = data };
            }
            catch (Exception ex)
            {
                return ToolError("SEARCH_FAILED", ex.Message);
            }
        }

        private MCPResponse HandleReadArray(MCPRequest request)
        {
            Type type;
            object instance;
            bool isStatic;
            string target;
            MCPResponse error;
            if (!ResolveTarget(request, out type, out instance, out isStatic, out target, out error))
                return error;
            string member = ExtractString(request, "member") ?? ExtractString(request, "field");
            if (string.IsNullOrEmpty(member)) return InvalidParams("Missing 'member'");

            int limit = 100;
            int cursor = 0;
            if (request.Params is JsonElement j)
            {
                var paging = Paging.Read(j, 100);
                limit = paging.Limit > 0 ? paging.Limit : 100;
                cursor = paging.Cursor;
            }
            try
            {
                var data = ValueTools.ReadArray(type, instance, member, limit, cursor);
                data["target"] = target;
                return new MCPResponse { Success = true, Data = data };
            }
            catch (Exception ex)
            {
                return ToolError("READ_ARRAY_FAILED", ex.Message);
            }
        }

        private MCPResponse HandleDecodeByteArray(MCPRequest request)
        {
            string dataText = ExtractString(request, "data");
            string format = ExtractString(request, "format") ?? "auto";
            string encoding = ExtractString(request, "encoding") ?? "hex";
            byte[] bytes = null;

            if (!string.IsNullOrEmpty(dataText))
            {
                try
                {
                    if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
                        bytes = Convert.FromBase64String(dataText.Trim());
                    else if (string.Equals(encoding, "text", StringComparison.OrdinalIgnoreCase))
                        bytes = Encoding.UTF8.GetBytes(dataText);
                    else
                        bytes = ValueSerializer.IsHex(dataText.Trim())
                            ? ValueSerializer.DecodeBytes(dataText)
                            : Encoding.UTF8.GetBytes(dataText);
                }
                catch (Exception ex)
                {
                    return ToolError("DECODE_FAILED", "Inline data is not valid " + encoding + ": " + ex.Message);
                }
            }
            else
            {
                Type type;
                object instance;
                bool isStatic;
                string target;
                MCPResponse error;
                if (!ResolveTarget(request, out type, out instance, out isStatic, out target, out error))
                    return error;
                string member = ExtractString(request, "member") ?? ExtractString(request, "field");
                if (string.IsNullOrEmpty(member)) return InvalidParams("Missing 'member' or inline 'data'");
                string readError;
                if (!ValueTools.TryGetByteArray(type, instance, member, out bytes, out readError))
                    return ToolError("DECODE_FAILED", readError);
            }

            try
            {
                var result = ValueTools.DecodeBytes(bytes, format);
                result["target"] = dataText != null ? "inline" : ExtractString(request, "member");
                return new MCPResponse { Success = true, Data = result };
            }
            catch (Exception ex)
            {
                return ToolError("DECODE_FAILED", ex.Message);
            }
        }

        // ---- labels -----------------------------------------------------------

        private MCPResponse HandleSetLabel(MCPRequest request)
        {
            string type = ExtractString(request, "type");
            string member = ExtractString(request, "member");
            string label = ExtractString(request, "label");
            string note = ExtractString(request, "note");
            try
            {
                return new MCPResponse { Success = true, Data = Labels.Set(type, member, label, note) };
            }
            catch (Exception ex)
            {
                return InvalidParams(ex.Message);
            }
        }

        private MCPResponse HandleRemoveLabel(MCPRequest request)
        {
            string type = ExtractString(request, "type");
            string member = ExtractString(request, "member");
            if (string.IsNullOrEmpty(type)) return InvalidParams("Missing 'type'");
            return new MCPResponse { Success = true, Data = Labels.Remove(type, member) };
        }

        private MCPResponse HandleListLabels(MCPRequest request)
        {
            string contains = ExtractString(request, "type_contains");
            int limit = 100;
            int cursor = 0;
            if (request.Params is JsonElement j)
            {
                var paging = Paging.Read(j, 100);
                limit = paging.Limit > 0 ? paging.Limit : 100;
                cursor = paging.Cursor;
            }
            return new MCPResponse { Success = true, Data = Labels.Page(contains, limit, cursor) };
        }

        // ---- tracing ----------------------------------------------------------

        private MCPResponse HandleStartMethodTrace(MCPRequest request)
        {
            string type = ExtractString(request, "type");
            string method = ExtractString(request, "method");
            string assembly = ExtractString(request, "assembly");
            int paramCount = -1;
            if (request.Params is JsonElement j)
            {
                JsonElement tmp;
                if (j.TryGetProperty("param_count", out tmp)) { try { paramCount = tmp.GetInt32(); } catch { } }
            }
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(method))
                return InvalidParams("Missing 'type' and/or 'method'");
            if (!MethodTracer.Available)
                return ToolError("TRACE_UNSUPPORTED",
                    "Method tracing is Mono-only and requires Harmony. Use list_hooks for the read-only inventory on IL2CPP.");
            try
            {
                return new MCPResponse { Success = true, Data = MethodTracer.Start(type, assembly, method, paramCount) };
            }
            catch (Exception ex)
            {
                return ToolError("TRACE_FAILED", ex.Message);
            }
        }

        private MCPResponse HandleStopMethodTrace(MCPRequest request)
        {
            string id = ExtractString(request, "trace_id");
            if (string.IsNullOrEmpty(id)) return InvalidParams("Missing 'trace_id'");
            try { return new MCPResponse { Success = true, Data = MethodTracer.Stop(id) }; }
            catch (Exception ex) { return ToolError("TRACE_NOT_FOUND", ex.Message); }
        }

        private MCPResponse HandleClearMethodTrace(MCPRequest request)
        {
            string id = ExtractString(request, "trace_id");
            if (string.IsNullOrEmpty(id)) return InvalidParams("Missing 'trace_id'");
            try { return new MCPResponse { Success = true, Data = MethodTracer.Clear(id) }; }
            catch (Exception ex) { return ToolError("TRACE_NOT_FOUND", ex.Message); }
        }

        private MCPResponse HandleListMethodTraces(MCPRequest request)
        {
            try
            {
                return new MCPResponse
                {
                    Success = true,
                    Data = new Dictionary<string, object>
                    {
                        { "traces", MethodTracer.List() },
                        { "supported", MethodTracer.Available }
                    }
                };
            }
            catch (Exception ex) { return ToolError("TRACE_FAILED", ex.Message); }
        }

        private MCPResponse HandleGetMethodTrace(MCPRequest request)
        {
            string id = ExtractString(request, "trace_id");
            if (string.IsNullOrEmpty(id)) return InvalidParams("Missing 'trace_id'");
            int limit = 100;
            int cursor = 0;
            if (request.Params is JsonElement j)
            {
                var paging = Paging.Read(j, 100);
                limit = paging.Limit > 0 ? paging.Limit : 100;
                cursor = paging.Cursor;
            }
            try { return new MCPResponse { Success = true, Data = MethodTracer.Get(id, cursor, limit) }; }
            catch (Exception ex) { return ToolError("TRACE_NOT_FOUND", ex.Message); }
        }

        private MCPResponse HandleBuildCallGraph(MCPRequest request)
        {
            string id = ExtractString(request, "trace_id");
            if (string.IsNullOrEmpty(id)) return InvalidParams("Missing 'trace_id'");
            int limit = 100;
            int cursor = 0;
            if (request.Params is JsonElement j)
            {
                var paging = Paging.Read(j, 100);
                limit = paging.Limit > 0 ? paging.Limit : 100;
                cursor = paging.Cursor;
            }
            try { return new MCPResponse { Success = true, Data = MethodTracer.BuildGraph(id, cursor, limit) }; }
            catch (Exception ex) { return ToolError("TRACE_NOT_FOUND", ex.Message); }
        }

        // ---- capture / audit --------------------------------------------------

        private MCPResponse HandleCaptureScreenshot(MCPRequest request)
        {
            int maxBytes = BridgeConfig.MaxScreenshotBytes;
            string outputPath = null;
            if (request.Params is JsonElement j)
            {
                JsonElement tmp;
                if (j.TryGetProperty("max_bytes", out tmp)) { try { maxBytes = Math.Max(10000, tmp.GetInt32()); } catch { } }
                if (j.TryGetProperty("output_path", out tmp)) { try { outputPath = tmp.GetString(); } catch { } }
            }
            try
            {
                return new MCPResponse { Success = true, Data = ScreenshotCapture.Capture(maxBytes, outputPath) };
            }
            catch (Exception ex)
            {
                return ToolError("CAPTURE_FAILED", ex.Message);
            }
        }

        private MCPResponse HandleActivityLog(MCPRequest request)
        {
            int limit = 50;
            int cursor = 0;
            if (request.Params is JsonElement j)
            {
                var paging = Paging.Read(j, 50);
                limit = paging.Limit > 0 ? paging.Limit : 50;
                cursor = paging.Cursor;
            }
            return new MCPResponse { Success = true, Data = AuditLog.Page(limit, cursor) };
        }
    }

    internal static class ReflectionHelpers
    {
        public static bool GetIsStatic(this PropertyInfo property)
        {
            MethodInfo getter = property.GetGetMethod(true);
            if (getter != null) return getter.IsStatic;
            MethodInfo setter = property.GetSetMethod(true);
            return setter != null && setter.IsStatic;
        }
    }
}
