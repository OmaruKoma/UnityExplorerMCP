# UnityExplorerMCP

**面向 Unity 游戏的 Runtime MCP 桥。** 给 AI Agent 一个能直接操作**运行中游戏**的调试器级接口：
遍历场景、探索类型系统、读写字段与属性、调用方法、在进程内执行 C#、跟踪 BepInEx 日志——全部走 Model Context Protocol。

[![Release](https://img.shields.io/github/v/release/OmaruKoma/UnityExplorerMCP?color=2ea043&label=release)](https://github.com/OmaruKoma/UnityExplorerMCP/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-3da639.svg)](LICENSE)
[![Node](https://img.shields.io/badge/node-%E2%89%A518-339933?logo=nodedotjs&logoColor=white)](#环境要求)
[![Runtimes](https://img.shields.io/badge/runtime-IL2CPP%20%7C%20Mono-8250df)](#支持的-runtime)
[![MCP](https://img.shields.io/badge/MCP-stdio-6f42c1)](https://modelcontextprotocol.io)

[English](README.md) | 简体中文

**文档：** [项目主页](https://omarukoma.github.io/UnityExplorerMCP/) · [安装指南](INSTALL.md) · [Releases](https://github.com/OmaruKoma/UnityExplorerMCP/releases)

> **这不是 Unity Editor MCP。** 它附着在**发布后的游戏进程**上——不需要 Editor、不需要工程文件，
> 也没有“改完重编译再重启”的循环。

## 目录

- [为什么需要 Runtime MCP](#为什么需要-runtime-mcp)
- [快速开始](#快速开始)
- [1.3 新增](#13-新增)
- [功能](#功能)
- [架构](#架构)
- [支持的 Runtime](#支持的-runtime)
- [环境要求](#环境要求)
- [安装](#安装)
- [配置 MCP 客户端](#配置-mcp-客户端)
- [工具列表](#工具列表)
- [工作流](#工作流)
- [配置项](#配置项)
- [协议](#协议)
- [常见问题](#常见问题)
- [已知限制](#已知限制)
- [安全](#安全)
- [开发](#开发)
- [致谢](#致谢)
- [License](#license)

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
引擎（`mcs.dll`）用于执行 C#。开发与真机测试以维护中的
[yukieiji/UnityExplorer](https://github.com/yukieiji/UnityExplorer) 分支为准。

## 快速开始

1. **把桥放进游戏** —— 将对应 loader 的压缩包解到 `<GAME_ROOT>/BepInEx/plugins/`
   （DLL 与 `mcs.dll` 必须放在一起）。
2. **启动游戏** —— `BepInEx/LogOutput.log` 出现 `[MCPBridge] Started on port 12345`。
3. **把 Server 加进 MCP 客户端** —— `command` 指向
   `node <REPO_PATH>/mcp-server/dist/server.js`（无需写 URL，桥会被自动发现）。
4. **确认链路** —— 调用 `unity_capabilities`，它会返回实时后端、pid、会话、权限与限额。

预编译包见 [Releases](https://github.com/OmaruKoma/UnityExplorerMCP/releases)；没有匹配版本时
走下面的源码构建（方案 B）。

## 1.3 新增

- **46 个工具**（原 24）：快照/差异/撤销、时间切片实例扫描、按值搜索、数组分页、字节解码、
  标签别名、PNG 截图、活动日志。
- **桥内能力门禁 + 审计** —— `preset=read-only` 或逐项 `MCP_ALLOW_*`；变更类调用写入
  `unity_activity_log`。
- **多游戏发现** —— 每个桥发布 pid/port 记录，Server 端列出并选择目标，不再靠猜。
- **Streamable HTTP** —— `node dist/server.js --transport http`，供 URL 型客户端使用。
- **IL2CPP 正确性** —— 具体代理类型解析、基类私有成员查找（TMP 的 `m_text` 等原生字段
  会以属性形式暴露）、快照差异稳定。

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

**检视**

- **运行时检视** —— GameObject、组件、字段、属性、方法
- **跨程序集反射** —— 类型发现、成员搜索、活实例查找
- **能力发现** —— `unity_capabilities` 报告后端、pid、权限、特性、工具与限额
- **Harmony Hook 清单** —— 只读查看已 patch 的方法与 patch 归属
- **日志尾部** —— 读取 `BepInEx/LogOutput.log`，不锁游戏文件

**变更与验证**

- **读写与调用** —— 实例字段/属性、静态成员、含 `out`/`ref` 的方法
- **游戏内执行 C#** —— 自带 Mono.CSharp，不依赖 UI
- **快照 / 差异** —— 记录成员值后只看变化（`epsilon`、`rebase`）
- **撤销** —— 回滚 `set_field` / `set_property` / `set_static` 最近写入
- **按值搜索 / 数组 / 字节解码** —— 找到持有某文本或数字的对象；分页数组；hex/base64/text/json/msgpack
- **批量读** —— 一轮对话最多 10 个只读调用，替代 10 次往返

**规模化操作**

- **Session 感知句柄** —— `session:id`；过期句柄（含读取路径）以 `STALE_HANDLE` 快速失败
- **桥内权限 + 审计** —— `read-only` 预设或逐项开关；变更类调用写入活动日志
- **多游戏发现** —— 桥发布 pid/port 记录；`unity_list_bridges` / `unity_select_bridge`
- **时间切片实例扫描** —— start/poll，避免大游戏上阻塞主线程
- **标签与别名** —— 为类型/成员保存可读名称与备注（按游戏安装持久化）
- **方法追踪** —— hook 托管方法，读取真实调用的 caller、参数、返回值与调用方（Mono）
- **截图** —— 当前帧 PNG；可另存文件
- **LLM 友好输出** —— 记录列表转表、裁剪最大数组、分页、带哈希截断

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
- 监听仅限 loopback，每个 HTTP 连接处理一条 JSON 请求。优先用 `12345`，被占用时向后找空闲端口，
  实际端口写入 `%LOCALAPPDATA%\UnityExplorerMCP\bridges\<pid>.json`。
- MCP Server 默认 stdio；`--transport http` 走无状态 Streamable HTTP。它会发现所有运行中的桥；
  同时有多个游戏时不会擅自选择，需先 `unity_select_bridge`。

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
| 快照 / 差异 / 撤销 | ✅ | ✅ | 仅编译 |
| 实例扫描 / 按值搜索 | ✅ | ✅ | 仅编译 |
| 数组 / 字节解码 / 标签 | ✅ | ✅ | 仅编译 |
| 方法追踪（安装 hook） | ❌（仅 Mono） | ✅ 需要 Harmony | 仅编译 |
| PNG 截图 | ✅（反射实现） | ✅（反射实现） | 仅编译 |
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
   | `UnityExplorerMCP-1.3.0-IL2CPP.zip` | BepInEx 6 + IL2CPP 游戏 |
   | `UnityExplorerMCP-1.3.0-Mono-BepInEx6.zip` | BepInEx 6 + Mono（Unity Mono）游戏 |
   | `UnityExplorerMCP-1.3.0-Mono-BepInEx5.zip` | BepInEx 5 + Mono 游戏 |

2. 解压到 `<GAME_ROOT>/BepInEx/plugins/`。压缩包内是桥 DLL 与 `mcs.dll`
   （`unity_execute_csharp` 必需），两者必须放在同一目录。

3. 启动游戏，在 `BepInEx/LogOutput.log` 中确认：

   ```text
   MCP Bridge plugin loaded (IL2CPP backend)
   ```

4. 用 `unity-explorer-mcp-server-1.3.0.zip` 部署 Server：

   ```bash
   unzip unity-explorer-mcp-server-1.3.0.zip
   cd unity-explorer-mcp-server-1.3.0
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
除非要固定某一个游戏，不要设置 `UNITY_BRIDGE_URL`，交给发现机制。

**Claude Desktop** —— 参考 `claude_desktop_config.example.json`：

```json
{
  "mcpServers": {
    "unity": {
      "command": "node",
      "args": ["<REPO_PATH>/mcp-server/dist/server.js"],
      "env": {
        "UNITY_BRIDGE_TIMEOUT": "120000",
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
        "UNITY_BRIDGE_TIMEOUT": "120000"
      }
    }
  }
}
```

**URL 型客户端** —— 运行 `node dist/server.js --transport http --port 8765`，连接
`http://127.0.0.1:8765/mcp`。无状态传输，多个客户端可共用。

配好后让 Agent 调一次 `unity_capabilities`：能返回，整条链路就是通的。

## 工具列表

共 46 个工具。每次会话先调 `unity_capabilities`——它会说明当前附着游戏的后端、会话、pid、
权限、特性、限额与封送能力。

**会话与发现**

| 工具 | 用途 |
|---|---|
| `unity_capabilities` | 后端、会话、pid、权限、特性、限额、封送能力 |
| `unity_ping` / `unity_scene_info` | 连通性、当前场景、会话 |
| `unity_list_bridges` / `unity_select_bridge` | 发现运行中的游戏并选择目标 |
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
| `unity_inspect` / `unity_inspect_type` | 成员（含 static/instance，`member_limit`），带标签/别名 |
| `unity_search_members` | 跨程序集成员搜索（`name_contains`、过滤、分页） |
| `unity_find_objects_of_type` | 按类型找活实例（单例、Manager、控制器） |
| `unity_start_instance_scan` / `unity_get_instance_scan` | 时间切片扫描，带进度与分页 |
| `unity_search_values` | 找到持有某文本/数字的组件 |

**取值与调用**

| 工具 | 用途 |
|---|---|
| `unity_get_field` / `unity_set_field` | 实例字段 |
| `unity_get_property` / `unity_set_property` | 实例属性 |
| `unity_get_static` / `unity_set_static` | 静态字段与属性 |
| `unity_invoke_method` / `unity_invoke_static` | 方法调用（`out`/`ref` 由 `outArgs` 返回） |
| `unity_read_array` | 分页数组/列表成员；引用元素返回句柄 |
| `unity_decode_byte_array` | 把 `byte[]` 解成 text、JSON、hex、base64 或 msgpack |

**状态验证**

| 工具 | 用途 |
|---|---|
| `unity_snapshot_object` | 记录成员值，返回快照 id |
| `unity_diff_snapshot` | 只返回变化；支持 `against`、`rebase`、`epsilon` |
| `unity_undo_write` | 回滚最近标量写入 |
| `unity_batch` | 一轮最多 10 个只读调用，结果同序，28 KiB 预算 |

**标签、追踪、截图**

| 工具 | 用途 |
|---|---|
| `unity_list_labels` / `unity_set_label` / `unity_remove_label` | 按安装持久化的名称与备注 |
| `unity_start_method_trace` / `unity_stop_method_trace` / `unity_clear_method_trace` | hook 一个托管方法（Mono） |
| `unity_list_method_traces` / `unity_get_method_trace` / `unity_build_call_graph` | 读真实调用与 caller→target 边 |
| `unity_capture_screenshot` | 当前帧 PNG image content；可另存文件 |

**运行时**

| 工具 | 用途 |
|---|---|
| `unity_execute_csharp` | 在运行中的游戏里执行 C#（`byte[]` 用 `returnEncoding`） |
| `unity_list_hooks` | Harmony patch 清单（只读） |
| `unity_activity_log` | 最近变更/调用/追踪记录与结果 |
| `unity_tail_log` | BepInEx 日志尾部（`lines`、`keyword`，游戏关了也能用） |

对象身份用 session 感知句柄（`Shop#1:-33814`）。写操作与调用会校验 session——切场景或重启后，
用 `unity_resolve_path` 或查找类工具重新解析；桥会返回 `STALE_HANDLE`，而不是误改别的对象。

权限在**游戏内**执行（`BepInEx/config/UnityExplorerMCP.cfg` 或 `MCP_ALLOW_*`）：
`write`、`invoke`、`execute`、`trace` 可单独关闭，或整体 `preset=read-only`。
MCP Server 无法通过参数给自己提权。

列表结果在更短时会压成 `{cols, rows}` 表；超大返回从最大数组裁剪
（`truncated`、`trimmed_arrays`），而不是把 JSON 从中间截断。
方法追踪会安装真实 Harmony hook，因此仅限 Mono；IL2CPP 构建返回
`TRACE_UNSUPPORTED`，且 `unity_capabilities` 里 `method_trace: false`。

## 工作流

**运行时探索**

```text
unity_capabilities → search_members → find_objects_of_type → inspect
    → get_field / get_property → invoke_method / invoke_static
    → execute_csharp → 观察结果 → tail_log
```

**验证一次修改**

```text
unity_snapshot_object { handle }        → snap-1
unity_set_field { handle, field, value }
unity_diff_snapshot { snapshot: "snap-1", rebase: true }
unity_undo_write { steps: 1 }           # 差异不对就回滚
```

**一轮代替六次**

```text
unity_batch { calls: [
  { tool: "unity_scene_info" },
  { tool: "unity_find_objects_of_type", arguments: { type: "Game.ShopController" } },
  { tool: "unity_inspect", arguments: { handle: "Shop#1:-33814" } },
  { tool: "unity_get_field", arguments: { handle: "Shop#1:-33814", field: "_isReady" } }
] }
```

**哪对象显示这段文本？**

```text
unity_search_values { value: "购买", component_type: "Text" }
unity_decode_byte_array { handle, member: "packet", format: "msgpack" }
```

**Mod 开发**

先用桥探索并在运行时验证，再把最终 Mod 写成独立的 BepInEx 插件。
用 `unity_tail_log` 看 Mod 日志，用 `unity_list_hooks` 确认 patch 状态。成品 Mod 不依赖本项目。

## 配置项

| 变量 | 默认值 | 作用域 |
|---|---|---|
| `UNITY_BRIDGE_URL` | — | 固定某个桥地址；不设时走发现/端口扫描 |
| `UNITY_BRIDGE_PID` | — | 优先选择该 pid 的桥 |
| `UNITY_BRIDGE_TIMEOUT` | `120000` | Server → 桥请求超时（毫秒） |
| `UNITY_MCP_MAX_BYTES` | `1500000` | 工具结果文本硬上限 |
| `UNITY_RESULT_BUDGET` | `120000` | 模型侧结果预算，超出则裁剪最大数组 |
| `UNITY_RESULT_TABLES` | `1` | 设 `0` 关闭 `{cols, rows}` 表 |
| `UNITY_BATCH_BUDGET` | `28000` | `unity_batch` 结果预算 |
| `UNITY_DISCOVERY_DIR` | `%LOCALAPPDATA%\UnityExplorerMCP\bridges` | 发现记录目录 |
| `UNITY_GAME_ROOT` | — | `unity_tail_log` 自动定位游戏根目录 |
| `UNITY_MCP_TRANSPORT` | `stdio` | `stdio` 或 `http` |
| `UNITY_MCP_HTTP_PORT` | `8765` | Streamable HTTP 端口 |
| `MCP_BRIDGE_PORT` | `12345` | 首选桥端口；被占用时向后找 |
| `MCP_BRIDGE_PORT_SPAN` | `20` | 端口回退范围 |
| `MCP_PERMISSION_PRESET` | `full` | `read-only` 关闭 write/invoke/execute/trace |
| `MCP_ALLOW_WRITE` / `MCP_ALLOW_INVOKE` / `MCP_ALLOW_EXECUTE` / `MCP_ALLOW_TRACE` | `true` | 逐项权限 |
| `MCP_DISCOVERY` | `true` | 是否写发现记录 |
| `MCP_AUDIT` | `true` | 是否记录变更类调用 |
| `MCP_MAX_SNAPSHOTS` | `16` | 保留快照数 |
| `MCP_MAX_UNDO` | `32` | 撤销栈深度 |
| `MCP_MAX_TRACES` | `8` | 保留追踪会话数 |
| `MCP_MAX_TRACE_RECORDS` | `500` | 每个追踪的记录上限（连续相同调用合并） |
| `MCP_MAX_SCREENSHOT_BYTES` | `1000000` | PNG 缩放目标 |
| `MCP_MAX_SEARCH_COMPONENTS` | `50000` | `unity_search_values` 的组件扫描上限 |
| `MCP_ARRAY_LIMIT` | `500` | 数组内联上限（桥） |
| `MCP_OUTPUT_LIMIT` | `8192` | 字符串截断阈值（桥） |
| `MCP_BYTE_LIMIT` | `32768` | 字节截断阈值（桥） |

游戏内设置也可写在 `BepInEx/config/UnityExplorerMCP.cfg`（INI 风格
`key=value`：`preset`、`allow_write`、`allow_invoke`、`allow_execute`、`allow_trace`、
`mcp_bridge_port`、`discovery`、`audit`）。环境变量优先。标签保存在同目录的
`UnityExplorerMCP.labels.json`。

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
- **加载时超时** —— 切场景会占住 Unity 主线程。等游戏空闲后重试；`UNITY_BRIDGE_TIMEOUT` 太短会在加载期误报。
- **`STALE_HANDLE`** —— 会话已变化（切场景、重启）。重新解析句柄。
  读取路径也校验会话，旧 id 不会被静默映射到别的对象。
- **IL2CPP 字段显示为属性** —— Il2CppInterop 把原生字段（`m_text`、`m_Name` 等）投影成属性。
  `unity_get_field` / `unity_set_field` 会自动回退到属性读取；`unity_search_values`
  同时扫描字段与 `m_`/`_` 开头的属性投影。
- **`execute_csharp` 报 CS0584** —— 对 `Type[]` 优先用 `foreach` 而不是 LINQ；
  mcs 遇到裁剪过的 interop 存根会失败（UnityExplorer 自家 Console 同款问题）。
- **SRE 被裁剪** —— 按 UnityExplorer 的 corelibs 流程补对应 Unity 版本的文件。
- **同时开了多个游戏** —— `unity_list_bridges`，再 `unity_select_bridge { pid }` 或
  `{ port }`。客户端配置里去掉 `UNITY_BRIDGE_URL` 才会走发现。
- **端口被占用** —— 桥会自动后移并把实际端口写进发现记录；只有必须固定端口时才设 `MCP_BRIDGE_PORT`。
- **`PERMISSION_DENIED`** —— 游戏内配置关闭了该能力。改 `UnityExplorerMCP.cfg` 或设
  `MCP_ALLOW_*` 后重启游戏。
- **`TRACE_UNSUPPORTED`** —— 方法追踪需要 Mono 后端与 Harmony；IL2CPP 只有只读的
  `unity_list_hooks`。
- **截图全黑或失败** —— `ScreenCapture` 通过反射调用；某些运行时只有帧末才拿到正确画面。
  在游戏正常渲染时重试，或调大 `MCP_MAX_SCREENSHOT_BYTES`。
- **后端识别不对** —— 以 `unity_capabilities` 为准；你拷进去的 DLL 决定 IL2CPP 还是 Mono。

## 已知限制

- 只监听本地（`127.0.0.1`）；无鉴权、不做公网暴露（设计如此）。
- 方法追踪仅 Mono，且每个会话只对**一个托管方法**安装 Harmony hook。任意 patch、程序集加载、
  原生代码执行不在 MCP 面内。
- 无输入模拟（那是 Editor MCP 的地盘）。
- 截图编码走反射（Unity 各版本模块可用性不同）；少数运行时只有两条编码路径中的一条。
- MelonLoader 构建在 `#if ML` 之后，本阶段不产出也不文档化。
- BepInEx 6 Mono 可编译，暂无真机测试。

## 安全

桥会执行 Agent 发来的任意 C#，且监听 loopback。鉴权仍不在本阶段范围内，但权限门在桥内：
`preset=read-only`（或逐项 `MCP_ALLOW_*`）会阻止写入、调用、C# 执行与追踪，
所有变更类调用写入活动日志。把它当挂载的调试器：只对自己的游戏、自己的机器使用。
`set_*` 与 `invoke_*` 会修改运行时状态——先检视，再动手；实验时配合快照/差异与
`unity_undo_write`。

## 开发

```powershell
.\build.ps1 -Backend Server      # 只编 mcp-server
node test-tools.js               # 46 个工具、注解、batch 校验（不用开游戏）
node test.js                     # 真机桥测试（游戏开着）
$env:UNITY_BACKEND = "Mono"; node test.js
node verify.js                   # 仓库完整性
```

同一个 SDK 风格 C# 工程，两种配置：`Release_IL2CPP`（net6）与 `Release_Mono`（net35）。
源码共用，后端差异沿用 UnityExplorer 的 `CPP`/`MONO` 宏约定。产物进
`dist/il2cpp`、`dist/mono`、`dist/mono-bepinex5`。

## 致谢

本项目建立在此前的 Unity 运行时检视工作之上。

**运行时基础**

- [UnityExplorer](https://github.com/sinai-dev/UnityExplorer) —— 原版游戏内检视器。本桥模仿其
  C# Console 行为，并使用同一套 Mono.CSharp 引擎。UnityExplorer 采用 GPL-3.0，其代码不在本仓库中。
- [yukieiji/UnityExplorer](https://github.com/yukieiji/UnityExplorer) —— 本项目实际使用与真机测试的
  维护分支，包含更新的 IL2CPP / interop 支持。

**对比项目提供的思路**（仅设计评审参考，未复制代码）

- [Jadis0x/UnityRuntimeExplorer](https://github.com/Jadis0x/UnityRuntimeExplorer) —— 基于 URKit 的
  IL2CPP/Mono 实时检视器并集成 MCP。其以工具为中心的运行时模型启发了本项目的句柄化对象工作流，
  以及“游戏内桥 + 外部 Server”的分层。
- [snowyegret23/UnityInfoMCP](https://github.com/snowyegret23/UnityInfoMCP) —— 外部 MCP Server 加
  游戏内桥的检视 / 本地化工作流。其面向 LLM 的紧凑结果塑形启发了本项目的表格化、分页与截断策略。

**依赖**

- **Mono.CSharp**（`mcs.dll`）—— MIT，用于游戏内 C# 执行。
- [BepInEx](https://github.com/BepInEx/BepInEx) —— Mod 加载器集成。
- [Model Context Protocol](https://modelcontextprotocol.io) —— Server 传输层 SDK。

## License

[MIT](LICENSE)
