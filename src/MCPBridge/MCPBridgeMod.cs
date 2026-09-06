#if ML
using MelonLoader;
using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: MelonInfo(typeof(UnityExplorer.MCPBridge.MCPBridgeMod), "UnityExplorer MCP Bridge", "1.0.0", "UnityExplorer")]
[assembly: MelonGame(null, null)]

namespace UnityExplorer.MCPBridge
{
    public class MCPBridgeMod : MelonMod
    {
        private static MCPBridgeMod _instance;
        
        public override void OnInitializeMelon()
        {
            _instance = this;
            LoggerInstance.Msg("MCP Bridge mod loaded");
        }
        
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Setup MCP Bridge after scene loads
            if (MCPBridge.Instance == null)
            {
                MCPBridge.Setup();
            }
        }
    }
}
#endif