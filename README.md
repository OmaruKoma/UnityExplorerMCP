# UnityExplorer MCP Bridge

让 OpenCode 通过 MCP 协议与运行中的 Unity 游戏交互，覆盖场景查看与逆向调试（C# 执行、类型自省、静态调用、日志流）。

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

- **MCP Bridge** (`MCPBridge.dll` + `mcs.dll`): 注入 Unity 游戏进程，监听 `127.0.0.1:12345`，所有 Unity API 调用都在主线程执行
- **MCP Server** (`server.js`): 外部 Node.js 进程，转发 MCP 协议请求；`unity_tail_log` 由它直接读磁盘，不经过游戏

## MCP 工具列表

| 工具名 | 说明 |
|--------|------|
| `unity_ping` | 测试连接（返回 `SessionId`，格式 `场景名#计数`，用于识别句柄是否过期） |
| `unity_scene_info` | 获取当前场景信息（含 `SessionId`） |
| `unity_find_gameobjects` | 按名字查找 GameObject（返回 `Path`，配合 `resolve_path` 使用） |
| `unity_get_gameobject` | 获取 GameObject 详情（位置、父子关系等） |
| `unity_get_components` | 获取 GameObject 上所有组件 |
| `unity_inspect` | 检查对象的字段、属性、方法 |
| `unity_get_field` | 获取字段值 |
| `unity_set_field` | 设置字段值 |
| `unity_get_property` | 获取属性值 |
| `unity_set_property` | 设置属性值 |
| `unity_invoke_method` | 调用方法（实例/static 均可；支持重载解析，`out/ref` 经 `outArgs` 返回） |
| `unity_hierarchy` | 获取场景层级结构 |
| `unity_execute_csharp` | 执行 C# 代码（桥自带 Mono.CSharp 编译执行，byte[] 返回默认 hex） |
| `unity_list_assemblies` | 列出已加载程序集（可选子串过滤） |
| `unity_inspect_type` | 按类型全名自省：字段/属性/方法签名（含 static/instance、参数与返回类型） |
| `unity_invoke_static` | 调用 static 方法；`out/ref` 参数经 `outArgs` 返回，byte[] 按 hex/base64 封送 |
| `unity_resolve_path` | 按 Hierarchy 路径（如 `Root/Child`）重新解析出新鲜 `instance_id` |
| `unity_tail_log` | 读 BepInEx `LogOutput.log` 尾部（Node 本地共享读，不锁游戏；游戏未启动也能用） |

## 安装

### 1. Unity 端 (MCP Bridge)

编译后把 **两个文件** 都复制到插件目录（`mcs.dll` 是 C# 执行引擎，缺了它 `execute_csharp` 不可用）：

```
src\MCPBridge\bin\Release\MCPBridge.dll  →  <游戏目录>/BepInEx/plugins/MCPBridge.dll
src\MCPBridge\bin\Release\mcs.dll         →  <游戏目录>/BepInEx/plugins/mcs.dll
```

> BepInEx 只在启动时加载插件：覆盖 DLL 后必须重启游戏。`LogOutput.log` 看到 `MCP Bridge plugin loaded` 即加载成功。

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
        "REQUEST_TIMEOUT": "30000",
        "UNITY_GAME_ROOT": "D:\\F95 Game\\aidealrays\\Aidealrays_Ver_2.1"
      }
    }
  }
}
```

`UNITY_GAME_ROOT` 可选：给 `unity_tail_log` 做默认日志路径自动定位，不配也能用 `path` 参数显式指定。

## 使用方法

1. 启动 Unity 游戏（MCP Bridge 自动加载）
2. 启动 OpenCode
3. 直接使用自然语言与 Unity 游戏交互

场景查看示例：
- "查看当前场景有哪些 GameObject"
- "找到玩家对象并获取它的位置"
- "修改某个变量的值"

逆向调试示例：
- "列出已加载程序集里名字带 IKA 的" → `unity_list_assemblies`
- "看下 `IKA9nt.Encrypter.EncrypterCore` 有哪些静态方法" → `unity_inspect_type`
- "调 `System.Math.Max(3,5)` 试试桥通不通" → `unity_invoke_static`
- "调带 out 参数的方法，把 out 值也拿回来" → `unity_invoke_static`（看 `outArgs`）
- "返回 byte[] 的方法，用 hex 给我" → `unity_invoke_static` / `unity_execute_csharp`（`returnEncoding: hex|base64`）
- "刚才的 instance_id 失效了，按路径重新解析" → `unity_resolve_path`（`find` 返回的 `Path` 直接拿来用）
- "把 BepInEx 日志最后 50 行拿来，过滤包含 password 的" → `unity_tail_log`

## 封送规则（invoke / execute_csharp 统一）

- 字符串与基础类型 → JSON 原生值
- `byte[]` / `Il2CppStructArray<byte>` → `{ encoding, data }`（默认 hex，可选 base64）
- 数组 / `List<T>` / Il2Cpp 数组 → `{ length, items }`（最多内联 500 项）
- `out`/`ref` 参数 → 调用后随 `outArgs: [{ index, name, type, value }]` 一并返回；传参时可用 `null` 占位，也可省略纯 out 参数（紧凑写法）
- 未知对象 → `{ type, instance_id, preview }` 句柄；`{ instance_id }` 可作为后续调用的参数传回
- 游戏侧异常 → 原样返回异常类型 + 信息 + Il2Cpp 堆栈（不再是一句 `IL2CPP Exception occurred`）

## 句柄稳定性

`instance_id`（Unity `GetInstanceID()`）是进程内临时编号，场景切换/重启游戏即失效。失效时桥会明确报错并提示用 `resolve_path`，而不会静默返回空：

```
Stale handle -126: object destroyed or scene changed (session Top#0).
Re-run find_gameobjects or resolve_path with the Hierarchy path instead of guessing IDs.
```

推荐流程：`find_gameobjects` 拿 `Path` → `resolve_path` 换新鲜 `instance_id` → 再 `get/inspect/invoke`。`ping` 返回的 `SessionId` 变了就说明旧句柄全过期。

## 环境要求

- **Unity 游戏**: BepInEx 6.0 (IL2CPP) + .NET 6 CoreCLR
- **OpenCode**: Node.js 18+
- **操作系统**: Windows

## 常见问题

### 连接失败
确保游戏已启动且 MCP Bridge 已加载（查看 `BepInEx/LogOutput.log` 确认，或直接用 `unity_tail_log` 看）。

### execute_csharp 编译报错 CS0584（mcs 内部错误）
mcs 在解析扩展方法/LINQ 时会枚举全部已引用程序集，遇到被裁剪的 UnityEngine interop 存根会失败——UnityExplorer 自家 C# Console 有同款问题。改用 `foreach` 循环写法即可，功能不受影响。

### C# evaluator 不可用
桥初始化失败时会返回明确原因。IL2CPP 下若提示 `NotSupportedException`（SRE 被裁剪），按 UnityExplorer 官方做法补 corlibs：从 https://unity.bepinex.dev/corlibs/ 下载对应 Unity 版本的 `mscorlib.dll`，放到游戏 `*_Data/Managed/` 或 doorstop `corlibs` 目录。

### 崩溃
某些游戏可能需要额外的兼容性配置。用 `unity_tail_log` 看日志排查问题。

## 验证

```powershell
node test.js        # 桥 HTTP 全链路（含 P0/P1/P3 真机用例）
node test-tools.js  # MCP 工具注册检查（18 个）
node verify.js      # 文件完整性检查
```

## 文件结构

```
UnityExplorerMCP/
├── src/MCPBridge/
│   ├── MCPBridge.cs          # Bridge 主逻辑（请求分发、Il2Cpp 调用、会话计数）
│   ├── CSharpExecutor.cs     # P0：自带 Mono.CSharp 编译执行
│   ├── AssemblyInspector.cs  # P1：程序集/类型自省、静态解析、路径解析
│   ├── ValueSerializer.cs    # P3：封送规则、对象注册表、Il2Cpp 线程 attach
│   ├── MCPBridgePlugin.cs    # BepInEx 6 插件入口
│   ├── DTO.cs                # 数据模型
│   └── MCPBridge.csproj      # 编译配置（含 mcs.dll 引用）
├── mcp-server/
│   ├── src/server.ts         # MCP Server 源码（18 工具定义与转发）
│   ├── src/logTail.ts        # P2：日志尾部读取（共享读）
│   └── dist/                 # 编译输出
├── test.js                   # 桥测试（含新工具真机用例）
├── test-tools.js             # 工具注册测试
├── verify.js                 # 完整性校验
└── opencode.json             # OpenCode 配置示例
```

## License

MIT
