using System;
using UnityEngine;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace UnityExplorer.MCPBridge
{
    [BepInPlugin("com.sinai.unityexplorer.mcpbridge", "UnityExplorer MCP Bridge", "1.0.0")]
    public class MCPBridgePlugin : BasePlugin
    {
        public static MCPBridgePlugin Instance;
        
        public override void Load()
        {
            Instance = this;
            
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<MCPBridgeInitializer>();
                
                var go = new GameObject("MCPBridgeInitializer");
                global::UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                
                go.AddComponent<MCPBridgeInitializer>();
                
                Log.LogMessage("MCP Bridge plugin loaded");
            }
            catch (Exception ex)
            {
                Log.LogError("Failed to initialize MCP Bridge: " + ex.ToString());
            }
        }
    }
    
    public class MCPBridgeInitializer : MonoBehaviour
    {
        private float _startTime;
        private bool _initialized;
        
        public MCPBridgeInitializer(IntPtr ptr) : base(ptr) { }
        
        internal void Awake()
        {
            _startTime = Time.realtimeSinceStartup;
        }
        
        internal void Update()
        {
            if (_initialized) return;
            
            if (Time.realtimeSinceStartup - _startTime > 2f)
            {
                _initialized = true;
                MCPBridge.Setup();
                global::UnityEngine.Object.Destroy(this.gameObject);
            }
        }
    }
}