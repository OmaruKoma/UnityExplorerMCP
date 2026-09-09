# UnityExplorerMCP

[English](README.md) | 简体中文

**UnityExplorerMCP 是面向 Unity 游戏的 Runtime MCP 桥。**它把 UnityExplorer 的运行时检视、反射和 C# 执行能力暴露给 MCP 兼容的 AI Agent，用于运行时调试、探索、实验和 AI 辅助 Mod 开发。

这**不是** Unity Editor MCP，也**不是** OpenCode 专属项目。OpenCode 只是受支持的 MCP 客户端之一。

典型流程：

```text
运行中的 Unity 游戏
        ↓
UnityExplorer（运行时探索层）
        ↓
UnityExplorerMCP（Agent 接口层）
        ↓
MCP 客户端 / AI Agent（OpenCode、Claude、Cursor……）
        ↓
检视 Runtime → 实验 → 验证
        ↓
产出独立 Mod
```

UnityExplorer 可作为开发期的运行时探索层，最终 Mod 不需要依赖 UnityExplorer（除非 Mod 本身需要它）。

## 功能

- 运行时检视（GameObject、组件、字段、属性、方法）
- 全程序集反射与类型发现
- 字段 / 属性读写、方法调用、静态访问
- 在运行的游戏中执行 C#（自带 Mono.CSharp，不依赖 UI）
- Session 感知的对象句柄（`session:id`，过期句柄快速失败）
- 能力发现（`unity_capabilities`——每次先调它）
- 分页与 LLM 友好的输出（限额、游标、带 sha256 的截断）
- 只读的 Harmony Hook 清单
- 读 BepInEx 日志尾部（不锁游戏文件）

## 架构

```text
                MCP 客户端
        ┌──────────┼──────────┐
     OpenCode    Claude     Cursor
        │          │           │
        └──────────┼───────────┘
                   ↓ (stdio)
              MCP Server (Node.js)
                   ↓ (HTTP, 127.0.0.1:12345)
              MCP Bridge（C# DLL，注入游戏）
                   ↓
              Runtime 后端
             ┌─────┴──────┐
             │            │
           Mono        IL2CPP
             │            │
             └─────┬──────┘
                   ↓
        UnityExplorer / Unity Runtime
```

桥把所有 Unity API 调用都放到游戏主线程执行。Tool 层代码后端无关，
IL2CPP 专有路径（`il2cpp_*`、原生封送）隔离在 `#if CPP` 内。Mono 后端
（net35，没有 `System.Text.Json`）自带最小兼容 JSON 层，调用方写法一致。

## 支持的 Runtime

| 功能 | IL2CPP (BepInEx 6) | Mono (BepInEx 5) | Mono (BepInEx 6) |
|---|---|---|---|
| 运行时检视 | ✅ 真机测过 | ✅ 真机测过 | 仅编译 |
| 反射 / 类型发现 | ✅ | ✅ | 仅编译 |
| 字段 / 属性读写 | ✅ | ✅ | 仅编译 |
| 方法调用（含 `out`/`ref`） | ✅ | ✅ | 仅编译 |
| 静态调用 + 静态字段读写 | ✅ | ✅ | 仅编译 |
| C# 执行 | ✅ | ✅ | 仅编译 |
| 对象句柄 + Session | ✅ | ✅ | 仅编译 |
| 能力发现 / 分页 / 截断 | ✅ | ✅ | 仅编译 |
| Hook 清单（只读） | ✅ | ✅ | 仅编译 |
| 日志尾部 | ✅（Server 侧） | ✅（Server 侧） | ✅（Server 侧） |

已在 Unity 2022.3（IL2CPP）与 Unity 2019.4（Mono、BepInEx 5.4）真机验证。
BepInEx 6 Mono 与主干同源编译，但暂无真机运行时；如有运行时差异，
以 `unity_capabilities` 的实时返回为准。

相关但不同的概念：**Runtime MCP**（本项目，面向活的游戏进程） vs
**Unity Editor MCP**（编辑器自动化） vs **UnityExplorer**（游戏内 UI 探索器） vs
**MCP 客户端**（Agent 宿主） vs **BepInEx**（Mod 加载器） vs **Mono / IL2CPP**（脚本后端）。

## 环境要求

- 已装 **BepInEx**（IL2CPP 或 Mono）且加载了 **UnityExplorer** 的 Unity 游戏
- **Node.js 18+**（MCP Server）
- **.NET SDK**（编译 C# 桥）

## 安装

完整指南见 [INSTALL.md](INSTALL.md)。简版：

```powershell
git clone <repository-url>
cd UnityExplorerMCP
.\build.ps1                       # IL2CPP + Mono + MCP Server
```

```powershell
.\build.ps1 -Backend IL2CPP       # dist/il2cpp/MCPBridge.IL2CPP.dll
.\build.ps1 -Backend Mono         # dist/mono/MCPBridge.Mono.dll（BepInEx 6 Unity Mono）
.\build.ps1 -Backend MonoBepInEx5 # dist/mono-bepinex5/MCPBridge.Mono.dll（BepInEx 5）
```

把对应的 `MCPBridge.*.dll` **和 `mcs.dll`** 一起拷到 `<GAME_ROOT>/BepInEx/plugins/`，
重启游戏，并在 `BepInEx/LogOutput.log` 里确认 `MCP Bridge plugin loaded`。

引用程序集走环境变量解析（`UNITY_GAME_DIR`、`MONO_REF_ROOT`），仓库不含任何本机路径。

## MCP 客户端配置

OpenCode 只是受支持客户端之一。复制对应示例，把 `<REPO_PATH>` /
`<GAME_ROOT_WITH_BEPINEX>` 换成本机路径：

### OpenCode

`opencode-example.json` → 你的 `opencode.json`：

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

### Claude

见 `claude_desktop_config.example.json`（标准 `mcpServers` 格式）。

### Cursor

见 `cursor-mcp.example.json`（标准 `mcpServers` 格式）。

## 工具列表

共 24 个工具。每次先调 `unity_capabilities`。

| 工具 | 用途 |
|---|---|
| `unity_capabilities` | 后端、会话、工具、限额、封送能力 |
| `unity_ping` / `unity_scene_info` | 连通性、场景、会话 |
| `unity_find_gameobjects` | 按名查找（句柄，`limit`/`cursor`） |
| `unity_get_gameobject` / `unity_get_components` | 详情、组件 |
| `unity_inspect` / `unity_inspect_type` | 成员（含 static/instance，`member_limit`） |
| `unity_search_members` | 跨程序集成员搜索 |
| `unity_find_objects_of_type` | 按类型找活实例（单例、Manager） |
| `unity_get_field` / `unity_set_field` | 实例字段 |
| `unity_get_property` / `unity_set_property` | 实例属性 |
| `unity_get_static` / `unity_set_static` | 静态字段/属性 |
| `unity_invoke_method` / `unity_invoke_static` | 方法调用（`out`/`ref` 走 `outArgs`） |
| `unity_resolve_path` | 按 Hierarchy 路径换新鲜句柄 |
| `unity_hierarchy` | 场景树（`max_depth`、`name_contains`、`limit`） |
| `unity_list_assemblies` | 程序集列表（`filter`、分页） |
| `unity_list_hooks` | Harmony patch 清单（只读） |
| `unity_execute_csharp` | 在运行的游戏中执行 C# |
| `unity_tail_log` | BepInEx 日志尾部（本地，游戏关了也能用） |

对象身份用 session 感知句柄（`Shop#1:-33814`）；写操作校验 session，
过期句柄以 `STALE_HANDLE` 快速失败。大输出分页（`limit`/`cursor`）或截断
（`truncated`/`length`/`sha256`/`head`/`tail`）。

## 运行时探索工作流

```text
unity_capabilities
        ↓
unity_search_members
        ↓
unity_find_objects_of_type
        ↓
inspect
        ↓
get_field / get_property
        ↓
invoke_method / invoke_static
        ↓
execute_csharp
        ↓
观察运行时结果
```

## Mod 开发工作流

先用桥探索验证，再写成独立 BepInEx 插件。用 `unity_tail_log` 看 Mod 日志，
用 `unity_list_hooks` 确认 patch 状态。

## 配置

| 变量 | 默认值 | 作用域 |
|---|---|---|
| `UNITY_BRIDGE_URL` | `http://127.0.0.1:12345` | Server → 桥地址（协议默认值） |
| `MCP_BRIDGE_PORT` | `12345` | 桥监听端口（游戏内读取，与 `UNITY_BRIDGE_URL` 保持一致） |
| `REQUEST_TIMEOUT` | `30000` | Server 请求超时（毫秒） |
| `UNITY_GAME_ROOT` | — | `unity_tail_log` 自动定位游戏根目录 |
| `MCP_ARRAY_LIMIT` | `500` | 数组内联上限（桥） |
| `MCP_OUTPUT_LIMIT` | `8192` | 字符串截断阈值（桥） |
| `MCP_BYTE_LIMIT` | `32768` | 字节截断阈值（桥） |
| `UNITY_BACKEND` | `IL2CPP` | `test.js` 期望标签 |
| `UNITY_GAME_DIR` / `MONO_REF_ROOT` | — | 编译期引用位置 |

## 常见问题

- **桥 DLL 没加载**：DLL 必须匹配 loader——BepInEx 5 游戏会静默跳过 BepInEx 6 版
  （看日志里的插件计数）。`mcs.dll` 必须和桥 DLL 放同一目录，否则没有 `execute_csharp`。
- **加载时请求超时**：切场景会占住 Unity 主线程，等空闲再跑；短超时测试在加载期会误报。
- **`execute_csharp` 报 CS0584**：对 `Type[]` 优先用 `foreach` 而不是 LINQ
  （mcs 遇到裁剪过的 interop 存根会失败，UnityExplorer 自家 Console 同款问题）。
- **SRE 被裁剪**：按 UnityExplorer 的 corelibs 流程补对应 Unity 版本的文件。

## 已知限制

- 只监听本地（`127.0.0.1`）；无鉴权、不做公网暴露（设计如此）。
- 不开放任意 Harmony patch（清单只读）。
- 无截图 / 输入模拟（那是 Editor MCP 的地盘，本项目不做）。
- 本阶段不覆盖 MelonLoader。
- BepInEx 6 Mono 后端可编译，暂无真机测试。

## 安全 / 注意事项

桥只监听 loopback，但会执行 Agent 发来的任意 C#——把它当调试器用：
只连自己的游戏、自己的机器。`set_*` / `invoke_*` 会改活运行时状态，先检视后动手。

## 开发

```powershell
.\build.ps1 -Backend Server      # 只编 mcp-server
node test.js                     # 真机桥测试（游戏开着）
$env:UNITY_BACKEND = "Mono"; node test.js
node test-tools.js               # 工具注册（不用开游戏）
node verify.js                   # 仓库完整性
```

## 构建

同一个 SDK 风格工程，两种配置：`Release_IL2CPP`（net6）与 `Release_Mono`
（net35）。源码共用，后端差异走 `CPP` / `MONO` 宏，沿用 UnityExplorer 自家的
约定。产物进 `dist/il2cpp`、`dist/mono`、`dist/mono-bepinex5`。

## License

MIT
