#if ML
using System;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(UnityExplorer.MCPBridge.MCPBridgeMod), "UnityExplorer MCP Bridge", "1.3.1", "UnityExplorer")]
[assembly: MelonGame(null, null)]

namespace UnityExplorer.MCPBridge
{
    public class MCPBridgeMod : MelonMod
    {
        private float _startTime = -1f;
        private float _nextAttempt;
        private bool _started;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("MCP Bridge mod loaded");
        }

        public override void OnUpdate()
        {
            if (_started || MCPBridge.Instance != null) return;
            if (_startTime < 0f) _startTime = Time.realtimeSinceStartup;
            // Give Unity a moment to finish its own bootstrap before touching scenes.
            if (Time.realtimeSinceStartup - _startTime < 2f) return;
            if (Time.realtimeSinceStartup < _nextAttempt) return;

            try
            {
                MCPBridge.Setup();
                _started = true;
                LoggerInstance.Msg("MCP Bridge started on port " + MCPBridge.Instance.Port);
            }
            catch (Exception ex)
            {
                _nextAttempt = Time.realtimeSinceStartup + 10f;
                LoggerInstance.Error("MCP Bridge setup failed: " + ex);
            }
        }
    }
}
#endif
