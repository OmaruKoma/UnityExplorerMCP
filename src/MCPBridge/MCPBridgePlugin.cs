using System;
using UnityEngine;
using BepInEx;
using BepInEx.Logging;
#if MONO
#if BIPUNITY
using BepInEx.Unity.Mono;
#endif
#else
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
#endif

namespace UnityExplorer.MCPBridge
{
    // Loader entry. Mirrors UnityExplorer's own MONO/CPP split:
    //  Mono   -> BaseUnityPlugin + Awake() + Logger
    //  IL2CPP -> BasePlugin + Load() + Log (+ ClassInjector for the behaviour)
    [BepInPlugin("com.sinai.unityexplorer.mcpbridge", "UnityExplorer MCP Bridge", "1.2.0")]
    public class MCPBridgePlugin :
#if MONO
        BaseUnityPlugin
#else
        BasePlugin
#endif
    {
        public static MCPBridgePlugin Instance;

#if MONO
        internal void Awake()
        {
            Instance = this;
            try
            {
                var go = new GameObject("MCPBridgeInitializer");
                global::UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<MCPBridgeInitializer>();
                Logger.LogMessage("MCP Bridge plugin loaded (Mono backend)");
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to initialize MCP Bridge: " + ex.ToString());
            }
        }
#else
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

                Log.LogMessage("MCP Bridge plugin loaded (IL2CPP backend)");
            }
            catch (Exception ex)
            {
                Log.LogError("Failed to initialize MCP Bridge: " + ex.ToString());
            }
        }
#endif
    }

    public class MCPBridgeInitializer : MonoBehaviour
    {
        private float _startTime;
        private bool _initialized;

#if CPP
        public MCPBridgeInitializer(IntPtr ptr) : base(ptr) { }
#endif

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
