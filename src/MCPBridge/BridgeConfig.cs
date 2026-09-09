using System;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// Bridge-wide tunables. Every value has a compiled default and can be
    /// overridden from the environment (read once at startup). Reported
    /// verbatim by unity_capabilities so agents never guess the limits.
    /// </summary>
    public static class BridgeConfig
    {
        public static readonly string Backend =
#if CPP
            "IL2CPP";
#else
            "Mono";
#endif

        public static readonly int ArrayLimit = GetInt("MCP_ARRAY_LIMIT", 500);
        public static readonly int StringLimit = GetInt("MCP_OUTPUT_LIMIT", 8192);
        public static readonly int ByteLimit = GetInt("MCP_BYTE_LIMIT", 32768);
        public static readonly int RequestTimeoutMs = GetInt("MCP_REQUEST_TIMEOUT_MS", 30000);

        private static int GetInt(string name, int fallback)
        {
            try
            {
                string raw = Environment.GetEnvironmentVariable(name);
                int v;
                if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out v) && v > 0) return v;
            }
            catch { }
            return fallback;
        }
    }
}
