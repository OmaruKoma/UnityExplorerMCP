using System;
using System.Collections.Generic;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// Capability checks for tool dispatch. Permissions are bridge-side only:
    /// the MCP server cannot grant itself access by sending arguments.
    /// </summary>
    public static class BridgePermissions
    {
        private static readonly string[] WriteMethods =
        {
            "set_field", "set_property", "set_static", "undo_write",
            "set_label", "remove_label"
        };

        private static readonly string[] InvokeMethods = { "invoke_method", "invoke_static" };
        private static readonly string[] TraceMethods =
        {
            "start_method_trace", "stop_method_trace", "clear_method_trace"
        };

        public static bool IsAllowed(string method)
        {
            return Capability(method) == null;
        }

        /// <summary>Returns the denied capability name, or null when allowed.</summary>
        public static string Capability(string method)
        {
            string m = (method ?? "").ToLowerInvariant();
            if (Array.IndexOf(WriteMethods, m) >= 0 && !BridgeConfig.AllowWrite) return "write";
            if (Array.IndexOf(InvokeMethods, m) >= 0 && !BridgeConfig.AllowInvoke) return "invoke";
            if (m == "execute_csharp" && !BridgeConfig.AllowExecute) return "execute";
            if (Array.IndexOf(TraceMethods, m) >= 0 && !BridgeConfig.AllowTrace) return "trace";
            return null;
        }

        public static bool IsMutating(string method)
        {
            string m = (method ?? "").ToLowerInvariant();
            return Array.IndexOf(WriteMethods, m) >= 0
                || Array.IndexOf(InvokeMethods, m) >= 0
                || Array.IndexOf(TraceMethods, m) >= 0
                || m == "execute_csharp";
        }

        public static Dictionary<string, object> DeniedData(string capability)
        {
            return new Dictionary<string, object>
            {
                { "code", "PERMISSION_DENIED" },
                { "capability", capability },
                { "hint", "Enable it in BepInEx/config/UnityExplorerMCP.cfg or MCP_ALLOW_* env vars, then restart the game." }
            };
        }
    }

    /// <summary>Bounded in-memory audit log for mutating / invoking / tracing calls.</summary>
    public static class AuditLog
    {
        private const int Capacity = 512;
        private static readonly List<Dictionary<string, object>> Entries =
            new List<Dictionary<string, object>>();
        private static readonly object Lock = new object();
        private static int _next;

        public static void Record(string method, string target, bool ok, long elapsedMs, string summary)
        {
            if (!BridgeConfig.AuditEnabled) return;
            lock (Lock)
            {
                _next++;
                Entries.Add(new Dictionary<string, object>
                {
                    { "seq", _next },
                    { "at", DateTime.UtcNow.ToString("o") },
                    { "method", method },
                    { "target", target },
                    { "ok", ok },
                    { "elapsed_ms", elapsedMs },
                    { "summary", summary }
                });
                if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);
            }
        }

        public static Dictionary<string, object> Page(int limit, int cursor)
        {
            if (limit <= 0) limit = 50;
            if (limit > 200) limit = 200;
            if (cursor < 0) cursor = 0;
            lock (Lock)
            {
                int total = Entries.Count;
                var items = new List<Dictionary<string, object>>();
                // Newest first.
                for (int i = total - 1 - cursor; i >= 0 && items.Count < limit; i--)
                    items.Add(Entries[i]);
                int consumed = cursor + items.Count;
                return new Dictionary<string, object>
                {
                    { "items", items },
                    { "total", total },
                    { "next_cursor", consumed < total ? (object)(consumed) : null }
                };
            }
        }

        public static string DescribeRequest(MCPRequest request, string method)
        {
            var parts = new List<string>();
            if (request != null && request.Params is System.Text.Json.JsonElement j)
            {
                AppendIf(j, parts, "handle");
                AppendIf(j, parts, "instance_id");
                AppendIf(j, parts, "type");
                AppendIf(j, parts, "field");
                AppendIf(j, parts, "property");
                AppendIf(j, parts, "member");
                AppendIf(j, parts, "method");
                AppendIf(j, parts, "trace_id");
                AppendIf(j, parts, "snapshot");
            }
            return parts.Count == 0 ? method : method + " " + string.Join(" ", parts.ToArray());
        }

        private static void AppendIf(System.Text.Json.JsonElement j, List<string> parts, string key)
        {
            try
            {
                System.Text.Json.JsonElement value;
                if (!j.TryGetProperty(key, out value)) return;
                string text = value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? value.GetString()
                    : value.GetRawText();
                if (!string.IsNullOrEmpty(text)) parts.Add(key + "=" + text);
            }
            catch { }
        }

    }
}
