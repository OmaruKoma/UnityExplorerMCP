# UnityExplorerMCP

**面向 Unity 游戏的 Runtime MCP 桥。** 给 AI Agent 一个能直接操作**运行中游戏**的调试器级接口：
遍历场景、探索类型系统、读写字段与属性、调用方法、在进程内执行 C#、跟踪 BepInEx 日志——全部走 Model Context Protocol。

[![Release](https://img.shields.io/github/v/release/OmaruKoma/UnityExplorerMCP?color=2ea043&label=release)](https://github.com/OmaruKoma/UnityExplorerMCP/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-3da639.svg)](LICENSE)
[![Node](https://img.shields.io/badge/node-%E2%89%A518-339933?logo=nodedotjs&logoColor=white)](#环境要求)
[![Runtimes](https://img.shields.io/badge/runtime-IL2CPP%20%7C%20Mono-8250df)](#支持的-runtime)
[![MCP](https://img.shields.io/badge/MCP-stdio-6f42c1)](https://modelcontextprotocol.io)

[English](README.md) | 简体中文

> **这不是 Unity Editor MCP。** 它附着在**发布后的游戏进程**上——不需要 Editor、不需要工程文件，
> 也没有“改完重编译再重启”的循环。

## 为什么需要 Runtime MCP

| | Unity Editor MCP | UnityExplorerMCP |
|---|---|---|
| 附着对象 | Editor 播放模式 / 资源 | 运行中的游戏进程 |
| 前置条件 | Unity 工程 | 游戏内已装 BepInEx |
| 能看到 | 序列化数据、Editor API | 活场景、运行时实例、私有状态 |
| 能改动 | 资源、Editor 状态 | 运行时状态 |
| 适合 | 制作、资源自动化 | Mod 调试、逆向、实时实验 |

[UnityExplorer](https://github.com/sinai-dev/UnityExplorer) 是同一件事的手动游戏内 UI。
本项目是面向 Agent 的 API 层：运行时**不需要**加载 UnityExplorer，并自带同一套 Mono.CSharp
引擎（`mcs.dll`）用于执行 C#。

## 使用示例

```text
> “商店按钮点了没反应。找到商店控制器，看它需要什么，然后按 UI 的方式调用它。”

unity_capabilities          {}                                  → 后端、会话、限额
unity_find_objects_of_type  { "type": "Game.ShopController" }    → 句柄
unity_inspect               { "handle": "ShopController#7:-33814" }
unity_search_members        { "name_contains": "Open" }
unity_get_field             { "handle": "ShopController#7:-33814", "field": "_isReady" }
unity_invoke_method         { "handle": "ShopController#7:-33814", "method": "Open" }
unity_tail_log              { "lines": 50 }                      → 看接下来发生了什么
```

所有返回都为 LLM 做了尺寸控制：列表分页（`limit`/`cursor`），大对象截断
（`truncated`/`length`/`sha256`/`head`/`tail`），错误返回结构化 code + message。

## 功能

- **运行时检视** —— GameObject、组件、字段、属性、方法
- **跨程序集反射** —— 类型发现、成员搜索、活实例查找
- **读写与调用** —— 实例字段/属性、静态成员、含 `out`/`ref` 的方法
- **游戏内执行 C#** —— 自带 Mono.CSharp，不依赖 UI
- **Session 感知句柄** —— `session:id`；过期句柄以 `STALE_HANDLE` 快速失败
- **能力发现** —— `unity_capabilities` 报告后端、会话、工具与限额
- **LLM 友好输出** —— 分页、带哈希的截断、成员列表上限
- **Harmony Hook 清单** —— 只读查看已 patch 的方法与 patch 归属
- **日志尾部** —— 读取 `BepInEx/LogOutput.log`，不锁游戏文件

## 架构

```text
                 MCP 客户端（Claude、Cursor、OpenCode……）
                                    │ stdio
                                    ▼
                          MCP Server (Node.js)
                                    │ HTTP · 127.0.0.1:12345
                                    ▼
                       MCP Bridge（C# DLL，注入游戏）
                                    │
                        ┌───────────┴───────────┐
                     Mono                    IL2CPP
                        └───────────┬───────────┘
                                    ▼
                          Unity Runtime（主线程）
```

- 桥把所有 Unity API 调用都排在**游戏主线程**执行；帧空闲前请求排队，切场景会阻塞，
  加载期请求可能超时——等游戏空闲后重试。
- Tool 层代码与后端无关。IL2CPP 专有路径（`il2cpp_*`、原生封送）在 `#if CPP` 内；
  Mono 构建（net35，没有 `System.Text.Json`）自带最小兼容 JSON 层，调用方写法一致。
- 监听仅限 loopback，每个 HTTP 连接处理一条 JSON 请求。

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

已在 Unity 2022.3（IL2CPP、BepInEx 6）与 Unity 2019.4（Mono、BepInEx 5.4）真机验证。
BepInEx 6 Mono 与主干同源编译，但暂无真机运行时；如有运行时差异，以 `unity_capabilities`
的实时返回为准，不做假设。

## 环境要求

- 装好 **BepInEx 5 或 6**（IL2CPP 或 Mono）的 Unity 游戏
- **Node.js 18+** —— MCP Server
- **.NET SDK** —— 仅在自行编译 C# 桥时需要

运行时**不需要** UnityExplorer 本体。

## 安装

### 方式 A —— 直接用预编译包（推荐）

1. 到 [Releases](https://github.com/OmaruKoma/UnityExplorerMCP/releases) 下载对应加载器的桥：

   | 资源 | 适用 |
   |---|---|
   | `UnityExplorerMCP-<version>-IL2CPP.zip` | BepInEx 6 + IL2CPP 游戏 |
   | `UnityExplorerMCP-<version>-Mono-BepInEx6.zip` | BepInEx 6 + Mono（Unity Mono）游戏 |
   | `UnityExplorerMCP-<version>-Mono-BepInEx5.zip` | BepInEx 5 + Mono 游戏 |

2. 解压到 `<GAME_ROOT>/BepInEx/plugins/`。压缩包内是桥 DLL 与 `mcs.dll`
   （`unity_execute_csharp` 必需），两者必须放在同一目录。

3. 启动游戏，在 `BepInEx/LogOutput.log` 中确认：

   ```text
   MCP Bridge plugin loaded (IL2CPP backend)
   ```

4. 用 `unity-explorer-mcp-server-<version>.zip` 部署 Server：

   ```bash
   unzip unity-explorer-mcp-server-<version>.zip
   cd unity-explorer-mcp-server-<version>
   npm ci --omit=dev
   node dist/server.js        # 以 stdio 提供 MCP 服务
   ```

### 方式 B —— 从源码构建

```powershell
git clone https://github.com/OmaruKoma/UnityExplorerMCP.git
cd UnityExplorerMCP
.\build.ps1                       # IL2CPP + Mono + MCP Server
```

```powershell
.\build.ps1 -Backend IL2CPP       # dist/il2cpp/MCPBridge.IL2CPP.dll
.\build.ps1 -Backend Mono         # dist/mono/MCPBridge.Mono.dll          （BepInEx 6 Unity Mono）
.\build.ps1 -Backend MonoBepInEx5 # dist/mono-bepinex5/MCPBridge.Mono.dll （BepInEx 5）
.\build.ps1 -Backend Server       # 只编 mcp-server
```

编译期引用一律来自环境变量，仓库不含任何本机路径：

| 变量 | 用于 | 含义 |
|---|---|---|
| `UNITY_GAME_DIR` | IL2CPP 构建 | 含 `BepInEx/` 的游戏根目录（interop + core 程序集） |
| `MONO_REF_ROOT` | Mono 构建 | BepInEx Mono 引用：`BepInEx/`、`UnityEngine.dll`、`mcs.dll` |

完整说明（含 BepInEx 5 变体）见 [INSTALL.md](INSTALL.md)。

## 配置 MCP 客户端

任何 stdio MCP 客户端都可以。把 `<REPO_PATH>` 换成 Server 实际路径，
`<GAME_ROOT_WITH_BEPINEX>` 换成游戏根目录（仅 `unity_tail_log` 需要）。

**Claude Desktop** —— 参考 `claude_desktop_config.example.json`：

```json
{
  "mcpServers": {
    "unity": {
      "command": "node",
      "args": ["<REPO_PATH>/mcp-server/dist/server.js"],
      "env": {
        "UNITY_BRIDGE_URL": "http://127.0.0.1:12345",
        "REQUEST_TIMEOUT": "30000",
        "UNITY_GAME_ROOT": "<GAME_ROOT_WITH_BEPINEX>"
      }
    }
  }
}
```

**Cursor** —— 见 `cursor-mcp.example.json`，同样是 `mcpServers` 结构。

**OpenCode** —— 见 `opencode-example.json`：

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

配好后让 Agent 调一次 `unity_capabilities`：能返回，整条链路就是通的。

## 工具列表

共 24 个工具。每次会话先调 `unity_capabilities`——它会说明当前附着游戏的后端、会话、
可用工具、限额与封送能力。

**发现**

| 工具 | 用途 |
|---|---|
| `unity_capabilities` | 后端、会话、工具、限额、封送能力 |
| `unity_ping` / `unity_scene_info` | 连通性、当前场景、会话 |
| `unity_list_assemblies` | 已加载程序集（`filter`、分页） |

**场景与对象**

| 工具 | 用途 |
|---|---|
| `unity_hierarchy` | 场景树（`max_depth`、`name_contains`、`limit`） |
| `unity_find_gameobjects` | 按名查找（句柄、`limit`/`cursor`） |
| `unity_get_gameobject` / `unity_get_components` | 对象详情、挂载组件 |
| `unity_resolve_path` | 按 Hierarchy 路径换新鲜句柄 |

**类型与反射**

| 工具 | 用途 |
|---|---|
| `unity_inspect` / `unity_inspect_type` | 成员（含 static/instance，`member_limit`） |
| `unity_search_members` | 跨程序集成员搜索（`name_contains`、过滤、分页） |
| `unity_find_objects_of_type` | 按类型找活实例（单例、Manager、控制器） |

**取值与调用**

| 工具 | 用途 |
|---|---|
| `unity_get_field` / `unity_set_field` | 实例字段 |
| `unity_get_property` / `unity_set_property` | 实例属性 |
| `unity_get_static` / `unity_set_static` | 静态字段与属性 |
| `unity_invoke_method` / `unity_invoke_static` | 方法调用（`out`/`ref` 由 `outArgs` 返回） |

**运行时**

| 工具 | 用途 |
|---|---|
| `unity_execute_csharp` | 在运行中的游戏里执行 C#（`byte[]` 用 `returnEncoding`） |
| `unity_list_hooks` | Harmony patch 清单（只读） |
| `unity_tail_log` | BepInEx 日志尾部（`lines`、`keyword`，游戏关了也能用） |

对象身份用 session 感知句柄（`Shop#1:-33814`）。写操作与调用会校验 session——切场景或重启后，
用 `unity_resolve_path` 或查找类工具重新解析；桥会返回 `STALE_HANDLE`，而不是误改别的对象。

## 工作流

**运行时探索**

```text
unity_capabilities → search_members → find_objects_of_type → inspect
    → get_field / get_property → invoke_method / invoke_static
    → execute_csharp → 观察结果 → tail_log
```

**Mod 开发**

先用桥探索并在运行时验证，再把最终 Mod 写成独立的 BepInEx 插件。
用 `unity_tail_log` 看 Mod 日志，用 `unity_list_hooks` 确认 patch 状态。成品 Mod 不依赖本项目。

## 配置项

| 变量 | 默认值 | 作用域 |
|---|---|---|
| `UNITY_BRIDGE_URL` | `http://127.0.0.1:12345` | Server → 桥地址 |
| `REQUEST_TIMEOUT` | `30000` | Server 请求超时（毫秒） |
| `UNITY_GAME_ROOT` | — | `unity_tail_log` 自动定位游戏根目录 |
| `MCP_BRIDGE_PORT` | `12345` | 桥监听端口（游戏内读取，与 `UNITY_BRIDGE_URL` 保持一致） |
| `MCP_ARRAY_LIMIT` | `500` | 数组内联上限（桥） |
| `MCP_OUTPUT_LIMIT` | `8192` | 字符串截断阈值（桥） |
| `MCP_BYTE_LIMIT` | `32768` | 字节截断阈值（桥） |

## 协议

桥就是一个本地 HTTP 端点，可以手动探测：

```bash
curl -s http://127.0.0.1:12345/ -H "Content-Type: application/json" \
     -d '{"method":"ping","params":{}}'
```

```json
{"Success":true,"Data":{...}}
```

`method` 是 Server 映射工具时使用的桥端方法名。任何 stdio MCP 客户端通过随附的 Node Server
使用即可；直接 HTTP 仅用于调试。

## 常见问题

- **DLL 没加载** —— 桥必须匹配 loader。BepInEx 5 会静默跳过 BepInEx 6 构建（看日志里的插件计数）。
  `mcs.dll` 必须与桥 DLL 同目录，否则 `unity_execute_csharp` 不可用。
- **加载时超时** —— 切场景会占住 Unity 主线程。等游戏空闲后重试；`REQUEST_TIMEOUT` 太短会在加载期误报。
- **`STALE_HANDLE`** —— 会话已变化（切场景、重启）。重新解析句柄。
- **`execute_csharp` 报 CS0584** —— 对 `Type[]` 优先用 `foreach` 而不是 LINQ；
  mcs 遇到裁剪过的 interop 存根会失败（UnityExplorer 自家 Console 同款问题）。
- **SRE 被裁剪** —— 按 UnityExplorer 的 corelibs 流程补对应 Unity 版本的文件。
- **端口被占用** —— 另一个桥实例或工具占着 `12345`；同时修改 `MCP_BRIDGE_PORT` 与 `UNITY_BRIDGE_URL`。
- **后端识别不对** —— 以 `unity_capabilities` 为准；你拷进去的 DLL 决定 IL2CPP 还是 Mono。

## 已知限制

- 只监听本地（`127.0.0.1`）；无鉴权、不做公网暴露（设计如此）。
- 不开放任意 Harmony patch——Hook 清单只读。
- 无截图 / 输入模拟（那是 Editor MCP 的地盘）。
- MelonLoader 构建在 `#if ML` 之后，本阶段不产出也不文档化。
- BepInEx 6 Mono 可编译，暂无真机测试。

## 安全

桥会执行 Agent 发来的任意 C#，且监听 loopback、无鉴权。把它当挂载的调试器：
只对自己的游戏、自己的机器使用。`set_*` 与 `invoke_*` 会修改运行时状态——先检视，再动手。

## 开发

```powershell
.\build.ps1 -Backend Server      # 只编 mcp-server
node test-tools.js               # 工具注册（不用开游戏）
node test.js                     # 真机桥测试（游戏开着）
$env:UNITY_BACKEND = "Mono"; node test.js
node verify.js                   # 仓库完整性
```

同一个 SDK 风格 C# 工程，两种配置：`Release_IL2CPP`（net6）与 `Release_Mono`（net35）。
源码共用，后端差异沿用 UnityExplorer 的 `CPP`/`MONO` 宏约定。产物进
`dist/il2cpp`、`dist/mono`、`dist/mono-bepinex5`。

## 致谢

- [UnityExplorer](https://github.com/sinai-dev/UnityExplorer) —— 运行时检视工具，也是本桥
  所模仿的 C# Console 行为的参照。UnityExplorer 采用 GPL-3.0，其代码不在本仓库中。
- **Mono.CSharp**（`mcs.dll`）—— MIT，用于游戏内 C# 执行。
- [BepInEx](https://github.com/BepInEx/BepInEx) —— Mod 加载器集成。
- [Model Context Protocol](https://modelcontextprotocol.io) —— Server 传输层 SDK。

## License

[MIT](LICENSE)
