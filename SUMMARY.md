# UnityExplorer → OpenCode MCP 集成完成

## 项目概述

成功将 yukieiji/UnityExplorer 接入 OpenCode，使 OpenCode Agent 能够通过 MCP 与正在运行的 Unity 游戏进行交互。

## 已完成的工作

### 1. 分析 UnityExplorer 项目结构

- 分析了 UnityExplorer 的项目结构
- 找到了 Runtime、Inspector、Object Explorer、Reflection、C# Console 相关 API
- 了解了 BepInEx 和 MelonLoader 的加载机制
- 确认了 Unity 主线程执行要求

### 2. 设计 MCP Bridge 架构

```
OpenCode Agent
    ↓
MCP
    ↓
MCP Server (Node.js/TypeScript)
    ↓
HTTP (127.0.0.1:12345)
    ↓
UnityExplorer MCP Bridge (C#)
    ↓
UnityExplorer API
    ↓
Unity Runtime
```

### 3. 实现 UnityExplorer MCP Bridge

**新增文件：**
- `src/MCPBridge/MCPBridge.cs` - 主要的 Bridge 实现
- `src/MCPBridge/MCPBridgePlugin.cs` - BepInEx 插件
- `src/MCPBridge/MCPBridgeMod.cs` - MelonLoader Mod
- `src/MCPBridge/DTO.cs` - 数据传输对象
- `src/MCPBridge/MCPBridge.csproj` - 项目文件

**实现的功能：**
- HTTP 服务器监听 `127.0.0.1:12345`
- 请求队列和主线程调度
- Unity API 线程安全调用
- 结构化 JSON 响应
- 超时和错误处理

### 4. 实现 MCP 工具

| 工具名 | 描述 | 状态 |
|--------|------|------|
| `unity_ping` | 检查连接 | ✓ |
| `unity_scene_info` | 获取场景信息 | ✓ |
| `unity_find_gameobjects` | 查找 GameObject | ✓ |
| `unity_get_gameobject` | 获取 GameObject 信息 | ✓ |
| `unity_get_components` | 获取组件 | ✓ |
| `unity_inspect` | 检查对象 | ✓ |
| `unity_get_field` | 获取字段值 | ✓ |
| `unity_set_field` | 设置字段值 | ✓ |
| `unity_get_property` | 获取属性值 | ✓ |
| `unity_set_property` | 设置属性值 | ✓ |
| `unity_invoke_method` | 调用方法 | ✓ |
| `unity_hierarchy` | 获取层级结构 | ✓ |
| `unity_execute_csharp` | 执行 C# 代码 | ✓ |

### 5. 实现外部 MCP Server

**新增文件：**
- `mcp-server/src/server.ts` - MCP Server 实现
- `mcp-server/package.json` - 项目配置
- `mcp-server/tsconfig.json` - TypeScript 配置

**功能：**
- MCP 协议实现
- 工具定义和参数验证
- 请求转发到 Unity Bridge
- 错误处理

### 6. 配置 OpenCode

**新增文件：**
- `opencode-example.json` - OpenCode 配置示例

**配置：**
```json
{
  "mcp": {
    "unity": {
      "type": "local",
      "command": ["node", "<REPO_PATH>/mcp-server/dist/server.js"],
      "enabled": true,
      "environment": {
        "UNITY_BRIDGE_URL": "http://127.0.0.1:12345",
        "REQUEST_TIMEOUT": "30000"
      }
    }
  }
}
```

### 7. 文档和测试

**新增文件：**
- `README.md` - 项目说明
- `INSTALL.md` - 安装说明
- `build.ps1` - 构建脚本
- `test.js` - 测试脚本

## 安装步骤

### 1. 编译 MCP Bridge

```powershell
cd /path/to/UnityExplorerMCP
.\build.ps1
```

### 2. 安装 MCP Bridge

**BepInEx:**（详见 `INSTALL.md` 的后端对照表）

```powershell
Copy-Item "dist\mono-bepinex5\MCPBridge.Mono.dll", "dist\mono-bepinex5\mcs.dll" "<GAME_ROOT>\BepInEx\plugins\"
```

### 3. 安装 MCP Server

```powershell
cd mcp-server
npm install
npm run build
```

### 4. 配置 MCP 客户端

以 `opencode-example.json`（或 Claude / Cursor 示例）为模板，填入本机路径。

### 5. 启动

1. 启动 Unity 游戏
2. 启动 MCP Server: `npm start`
3. 启动 OpenCode: `opencode`

## 使用示例

```
# 检查连接
use unity_ping

# 获取场景信息
use unity_scene_info

# 查找 GameObject
use unity_find_gameobjects with name "Player"

# 获取组件
use unity_get_components with instance_id 12345

# 检查对象
use unity_inspect with instance_id 12345

# 获取字段值
use unity_get_field with instance_id 12345 and field "health"

# 设置字段值
use unity_set_field with instance_id 12345 and field "health" and value 100

# 调用方法
use unity_invoke_method with instance_id 12345 and method "Heal" and args [50]

# 获取层级结构
use unity_hierarchy with max_depth 5

# 执行 C# 代码
use unity_execute_csharp with code "Debug.Log(\"Hello from MCP!\");"
```

## 安全特性

- 只监听 `127.0.0.1`，不监听公网
- 所有请求都有超时限制
- C# 执行有超时限制
- 层级结构有深度限制
- 结果数量有限制
- 防止无限递归
- 防止超大 JSON

## 项目结构

```
UnityExplorerMCP/
├── src/
│   └── MCPBridge/
│       ├── MCPBridge.cs          # 主要的 Bridge 实现
│       ├── MCPBridgePlugin.cs    # BepInEx 插件
│       ├── MCPBridgeMod.cs       # MelonLoader Mod
│       ├── DTO.cs                # 数据传输对象
│       └── MCPBridge.csproj      # 项目文件
├── mcp-server/
│   ├── src/
│   │   └── server.ts             # MCP Server 实现
│   ├── package.json
│   └── tsconfig.json
├── UnityExplorer/                # UnityExplorer 源码
├── build.ps1                     # 构建脚本
├── opencode-example.json         # OpenCode 配置示例（另有 Claude / Cursor 示例）
├── test.js                       # 测试脚本
├── README.md                     # 项目说明
└── INSTALL.md                    # 安装说明
```

## 验证 MCP 连接成功

1. 运行测试脚本：
   ```powershell
   node test.js
   ```

2. 在 OpenCode 中测试：
   ```
   use unity_ping
   ```

3. 检查 Unity 控制台是否有 "MCP Bridge started on port 12345" 消息

## 注意事项

- Unity API 必须在主线程执行，Bridge 已处理此问题
- 所有 Unity Object 引用使用 instance_id
- 不直接序列化 Unity Object，而是转换为轻量 DTO
- 正确处理 Unity Object 已销毁的情况
- 正确处理 Scene 切换

## 后续改进建议

1. 添加 WebSocket 支持以提高性能
2. 添加认证机制
3. 添加更多 Unity API 工具
4. 优化大对象的序列化
5. 添加缓存机制