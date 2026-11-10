using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// Bridge-wide tunables and permission gates. Values come from the
    /// environment first, then BepInEx/config/UnityExplorerMCP.cfg, then the
    /// compiled defaults below. unity_capabilities reports the effective set.
    /// </summary>
    public static class BridgeConfig
    {
        public static readonly string Backend =
#if CPP
            "IL2CPP";
#else
            "Mono";
#endif

        private static readonly Dictionary<string, string> FileSettings = LoadFile();

        public static readonly int ArrayLimit = GetInt("MCP_ARRAY_LIMIT", 500);
        public static readonly int StringLimit = GetInt("MCP_OUTPUT_LIMIT", 8192);
        public static readonly int ByteLimit = GetInt("MCP_BYTE_LIMIT", 32768);
        public static readonly int RequestTimeoutMs = GetInt("MCP_REQUEST_TIMEOUT_MS", 30000);
        public static readonly int PreferredPort = GetInt("MCP_BRIDGE_PORT", 12345);
        public static readonly int PortSpan = GetInt("MCP_BRIDGE_PORT_SPAN", 20);
        public static readonly bool DiscoveryEnabled = GetBool("MCP_DISCOVERY", "discovery", true);
        public static readonly bool AuditEnabled = GetBool("MCP_AUDIT", "audit", true);

        public static readonly int MaxSnapshots = GetInt("MCP_MAX_SNAPSHOTS", 16);
        public static readonly int MaxUndo = GetInt("MCP_MAX_UNDO", 32);
        public static readonly int MaxTraceSessions = GetInt("MCP_MAX_TRACES", 8);
        public static readonly int MaxTraceRecords = GetInt("MCP_MAX_TRACE_RECORDS", 500);
        public static readonly int MaxScanResults = GetInt("MCP_MAX_SCAN_RESULTS", 5000);
        public static readonly int MaxSearchComponents = GetInt("MCP_MAX_SEARCH_COMPONENTS", 50000);
        public static readonly int MaxScreenshotBytes = GetInt("MCP_MAX_SCREENSHOT_BYTES", 1000000);

        private static readonly string Preset =
            (Get("MCP_PERMISSION_PRESET", "preset") ?? "full").Trim().ToLowerInvariant();

        public static readonly bool AllowWrite =
            Preset != "read-only" && GetBool("MCP_ALLOW_WRITE", "allow_write", true);
        public static readonly bool AllowInvoke =
            Preset != "read-only" && GetBool("MCP_ALLOW_INVOKE", "allow_invoke", true);
        public static readonly bool AllowExecute =
            Preset != "read-only" && GetBool("MCP_ALLOW_EXECUTE", "allow_execute", true);
        public static readonly bool AllowTrace =
            Preset != "read-only" && GetBool("MCP_ALLOW_TRACE", "allow_trace", true);
        /// <summary>
        /// Getter-shaped invoke_method calls (GetFloat, GetTexture, ...) stay
        /// available in the read-only preset; setters and destructive calls do not.
        /// </summary>
        public static readonly bool AllowInvokeReadOnly =
            Preset == "read-only" && GetBool("MCP_ALLOW_INVOKE_READONLY", "allow_invoke_readonly", true);

        public static Dictionary<string, object> Permissions()
        {
            return new Dictionary<string, object>
            {
                { "preset", Preset },
                { "read", true },
                { "write", AllowWrite },
                { "invoke", AllowInvoke },
                { "invoke_read_only", AllowInvoke || AllowInvokeReadOnly },
                { "execute", AllowExecute },
                { "trace", AllowTrace }
            };
        }

        private static string Get(string envName, string fileKey)
        {
            try
            {
                string value = Environment.GetEnvironmentVariable(envName);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            catch { }
            string stored;
            if (FileSettings.TryGetValue(fileKey, out stored)) return stored;
            return null;
        }

        private static int GetInt(string envName, int fallback)
        {
            string raw = Get(envName, envName.ToLowerInvariant());
            int v;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out v) && v > 0) return v;
            return fallback;
        }

        private static bool GetBool(string envName, string fileKey, bool fallback)
        {
            string raw = Get(envName, fileKey);
            if (string.IsNullOrEmpty(raw)) return fallback;
            raw = raw.Trim().ToLowerInvariant();
            return raw == "1" || raw == "true" || raw == "yes" || raw == "on";
        }

        private static Dictionary<string, string> LoadFile()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dir = ConfigDirectory();
                string path = Path.Combine(dir, "UnityExplorerMCP.cfg");
                if (!File.Exists(path)) return result;
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return result;
        }

        public static string ConfigDirectory()
        {
            try
            {
                string data = Application.dataPath;
                if (!string.IsNullOrEmpty(data))
                {
                    string root = Path.GetDirectoryName(data);
                    if (!string.IsNullOrEmpty(root))
                        return Path.Combine(Path.Combine(root, "BepInEx"), "config");
                }
            }
            catch { }
            return Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "BepInEx"), "config");
        }
    }
}
