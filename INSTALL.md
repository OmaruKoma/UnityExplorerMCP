# 安装说明

## 前提条件

- Unity 游戏
- UnityExplorer (从 https://github.com/yukieiji/UnityExplorer 下载)
- BepInEx 或 MelonLoader
- Node.js (用于 MCP Server)
- .NET SDK (用于编译 MCP Bridge)

## 步骤 1: 编译 MCP Bridge

```powershell
cd D:\codespace\UnityExplorerMCP
.\build.ps1
```

或者手动编译：

```powershell
cd src\MCPBridge
dotnet build -c Release
```

## 步骤 2: 安装 MCP Bridge

### BepInEx

将编译好的 DLL 复制到 BepInEx 插件目录：

```powershell
Copy-Item "src\MCPBridge\bin\Release\UnityExplorer.MCPBridge.dll" "BepInEx\plugins\sinai-dev-UnityExplorer\"
```

### MelonLoader

将编译好的 DLL 复制到 MelonLoader Mods 目录：

```powershell
Copy-Item "src\MCPBridge\bin\Release\UnityExplorer.MCPBridge.dll" "Mods\"
```

## 步骤 3: 安装 MCP Server

```powershell
cd mcp-server
npm install
npm run build
```

## 步骤 4: 配置 OpenCode

将 `opencode.json` 复制到你的项目根目录：

```powershell
Copy-Item "opencode.json" "你的项目目录\"
```

或者将以下内容添加到你现有的 OpenCode 配置中：

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity": {
      "type": "local",
      "command": ["node", "C:\\UnityExplorerMCP\\mcp-server\\dist\\server.js"],
      "enabled": true,
      "environment": {
        "UNITY_BRIDGE_URL": "http://127.0.0.1:12345",
        "REQUEST_TIMEOUT": "30000"
      }
    }
  }
}
```

## 步骤 5: 启动

1. **启动 Unity 游戏** - 确保 UnityExplorer 已加载
2. **启动 MCP Server**:
   ```powershell
   cd mcp-server
   npm start
   ```
3. **启动 OpenCode**:
   ```powershell
   opencode
   ```

## 步骤 6: 验证

运行测试脚本验证安装：

```powershell
node test.js
```

或者在 OpenCode 中测试：

```
use unity_ping to check connection
```

## 故障排除

### MCP Bridge 未加载

1. 检查 Unity 控制台是否有错误信息
2. 确保 DLL 文件放在正确的目录
3. 确保 UnityExplorer 已正确加载

### MCP Server 无法启动

1. 确保已安装 Node.js
2. 运行 `npm install` 安装依赖
3. 运行 `npm run build` 编译 TypeScript
4. 检查端口 12345 是否被占用

### OpenCode 无法连接到 MCP Server

1. 确保 MCP Server 正在运行
2. 检查 `opencode.json` 配置是否正确
3. 确保 Node.js 路径正确

## 卸载

1. 从插件目录删除 `UnityExplorer.MCPBridge.dll`
2. 从项目目录删除 `opencode.json`
3. 删除 `mcp-server` 目录