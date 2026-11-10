using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// Publishes one small JSON record per running game under
    /// %LOCALAPPDATA%\UnityExplorerMCP\bridges so the MCP server can find
    /// every live bridge without scanning a fixed port.
    /// </summary>
    public static class BridgeDiscovery
    {
        private static string _file;
        private static readonly object Lock = new object();

        public static string Directory()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.Combine(Path.GetTempPath(), "UnityExplorerMCP");
            return Path.Combine(Path.Combine(baseDir, "UnityExplorerMCP"), "bridges");
        }

        public static string Publish(int port)
        {
            if (!BridgeConfig.DiscoveryEnabled) return null;
            lock (Lock)
            {
                try
                {
                    string dir = Directory();
                    System.IO.Directory.CreateDirectory(dir);
                    _file = Path.Combine(dir, "bridge-" + SafePid() + ".json");
                    var record = new Dictionary<string, object>
                    {
                        { "pid", SafePid() },
                        { "port", port },
                        { "backend", BridgeConfig.Backend },
                        { "loader", "BepInEx" },
                        { "unity_version", SafeUnityVersion() },
                        { "session_id", SafeSession() },
                        { "bridge_version", "1.3.1" },
                        { "started_at", DateTime.UtcNow.ToString("o") }
                    };
                    string json = JsonSerializer.Serialize(record);
                    string temp = _file + ".tmp";
                    File.WriteAllText(temp, json);
                    if (File.Exists(_file)) File.Delete(_file);
                    File.Move(temp, _file);
                    return _file;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[MCPBridge] Discovery publish failed: " + ex.Message);
                    return null;
                }
            }
        }

        public static void Unpublish()
        {
            lock (Lock)
            {
                try
                {
                    if (!string.IsNullOrEmpty(_file) && File.Exists(_file)) File.Delete(_file);
                }
                catch { }
                _file = null;
            }
        }

        private static int SafePid()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        private static string SafeUnityVersion()
        {
            try { return Application.unityVersion; }
            catch { return "unknown"; }
        }

        private static string SafeSession()
        {
            try { return MCPBridge.SessionId; }
            catch { return "unknown"; }
        }
    }
}
