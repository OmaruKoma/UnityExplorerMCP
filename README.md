# UnityExplorer MCP Bridge

让 OpenCode 通过 MCP 协议与运行中的 Unity 游戏交互。

## 支持的 UnityExplorer 分支

| 分支 | 版本 | 状态 |
|------|------|------|
| [yukieiji/UnityExplorer](https://github.com/yukieiji/UnityExplorer) | 4.12.7 | ✅ 已测试 |
| [sinai-dev/UnityExplorer](https://github.com/sinai-dev/UnityExplorer) | 4.12.7 | ✅ 兼容 |

> 基于 BepInEx 6.0 (IL2CPP + .NET 6 CoreCLR) 构建，Unity 版本 2022.3.x

## 架构

```
OpenCode → MCP Server (Node.js) → HTTP → MCP Bridge (C# DLL) → Unity API
```

- **MCP Bridge** (`MCPBridge.dll`): 注入 Unity 游戏进程，监听 `127.0.0.1:12345`
- **MCP Server** (`server.js`): 外部 Node.js 进程，转发 MCP 协议请求

## MCP 工具列表

| 工具名 | 说明 |
|--------|------|
| `unity_ping` | 测试连接 |
| `unity_scene_info` | 获取当前场景信息 |
| `unity_find_gameobjects` | 按名字查找 GameObject |
| `unity_get_gameobject` | 获取 GameObject 详情（位置、父子关系等） |
| `unity_get_components` | 获取 GameObject 上所有组件 |
| `unity_inspect` | 检查对象的字段、属性、方法 |
| `unity_get_field` | 获取字段值 |
| `unity_set_field` | 设置字段值 |
| `unity_get_property` | 获取属性值 |
| `unity_set_property` | 设置属性值 |
| `unity_invoke_method` | 调用方法 |
| `unity_hierarchy` | 获取场景层级结构 |
| `unity_execute_csharp` | 执行 C# 代码（需要 UnityExplorer C# Console） |

## 安装

### 1. Unity 端 (MCP Bridge)

将 `MCPBridge.dll` 复制到 Unity 游戏的 BepInEx 插件目录：

```
<游戏目录>/BepInEx/plugins/sinai-dev-UnityExplorer/MCPBridge.dll
```

### 2. OpenCode 端 (MCP Server)

在 `opencode.json` 中添加 MCP 配置：

```json
{
  "mcp": {
    "unity": {
      "type": "local",
      "command": ["node", "D:\\codespace\\UnityExplorerMCP\\mcp-server\\dist\\server.js"],
      "enabled": true,
      "environment": {
        "UNITY_BRIDGE_URL": "http://127.0.0.1:12345",
        "REQUEST_TIMEOUT": "30000"
      }
    }
  }
}
```

## 使用方法

1. 启动 Unity 游戏（MCP Bridge 自动加载）
2. 启动 OpenCode
3. 直接使用自然语言与 Unity 游戏交互

示例：
- "查看当前场景有哪些 GameObject"
- "找到玩家对象并获取它的位置"
- "修改某个变量的值"

## 环境要求

- **Unity 游戏**: BepInEx 6.0 (IL2CPP) + .NET 6 CoreCLR
- **OpenCode**: Node.js 18+
- **操作系统**: Windows

## 常见问题

### 连接失败
确保游戏已启动且 MCP Bridge 已加载（查看 `BepInEx/LogOutput.log` 确认）

### 崩溃
某些游戏可能需要额外的兼容性配置。查看日志排查问题

## 文件结构

```
UnityExplorerMCP/
├── src/MCPBridge/
│   ├── MCPBridge.cs          # Bridge 主逻辑
│   ├── MCPBridgePlugin.cs    # BepInEx 6 插件入口
│   ├── DTO.cs                # 数据模型
│   └── MCPBridge.csproj      # 编译配置
├── mcp-server/
│   ├── src/server.ts         # MCP Server 源码
│   └── dist/server.js        # 编译输出
└── opencode.json             # OpenCode 配置示例
```

## License

MIT
