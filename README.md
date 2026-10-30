# UnityExplorerMCP

**Runtime MCP bridge for Unity games.** Give an AI agent a debugger-grade grip on a *running* Unity game: walk the scene, explore the type system, read and write fields and properties, call methods, execute C# in-process and tail the BepInEx log — through the Model Context Protocol.

[![Release](https://img.shields.io/github/v/release/OmaruKoma/UnityExplorerMCP?color=2ea043&label=release)](https://github.com/OmaruKoma/UnityExplorerMCP/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-3da639.svg)](LICENSE)
[![Node](https://img.shields.io/badge/node-%E2%89%A518-339933?logo=nodedotjs&logoColor=white)](#requirements)
[![Runtimes](https://img.shields.io/badge/runtime-IL2CPP%20%7C%20Mono-8250df)](#supported-runtimes)
[![MCP](https://img.shields.io/badge/MCP-stdio-6f42c1)](https://modelcontextprotocol.io)

English | [简体中文](README.zh-CN.md)

> **This is not a Unity Editor MCP.** It attaches to the shipped game process — no Editor, no project files, no recompile-and-restart loop.

## Why a runtime MCP

| | Unity Editor MCP | UnityExplorerMCP |
|---|---|---|
| Attaches to | Editor play mode / assets | The running game process |
| Needs | Unity project | BepInEx in the game |
| Sees | Serialized data, Editor APIs | Live scene, runtime instances, private state |
| Writes | Assets, Editor state | Live runtime state |
| Good for | Authoring, asset automation | Mod debugging, RE, live experiments |

[UnityExplorer](https://github.com/sinai-dev/UnityExplorer) is the manual, in-game UI for the
same job. This project is the agent-facing API layer: it does not require UnityExplorer to be
loaded at runtime, and ships the same Mono.CSharp engine (`mcs.dll`) for C# execution.

## What it looks like

```text
> "The shop button does nothing. Find the shop controller, work out what it
  expects, and call it the way the UI would."

unity_capabilities          {}                                  → backend, session, limits
unity_find_objects_of_type  { "type": "Game.ShopController" }    → handles
unity_inspect               { "handle": "ShopController#7:-33814" }
unity_search_members        { "name_contains": "Open" }
unity_get_field             { "handle": "ShopController#7:-33814", "field": "_isReady" }
unity_invoke_method         { "handle": "ShopController#7:-33814", "method": "Open" }
unity_tail_log              { "lines": 50 }                      → what happened next
```

Every response is sized for an LLM: lists are paged (`limit`/`cursor`), large payloads are
truncated (`truncated`/`length`/`sha256`/`head`/`tail`), and errors are structured code + message.

## Features

- **Live runtime inspection** — GameObjects, components, fields, properties, methods
- **Reflection across loaded assemblies** — type discovery, member search, live instances
- **Reads and writes** — instance fields/properties, statics, method calls including `out`/`ref`
- **C# execution inside the game** — self-hosted Mono.CSharp, no UI dependency
- **Session-aware handles** — `session:id`; stale handles fail fast with `STALE_HANDLE`
- **Capability discovery** — `unity_capabilities` reports backend, session, tools and limits
- **LLM-sized output** — pagination, truncation with hashes, bounded member lists
- **Harmony hook inventory** — read-only view of patched methods and patch owners
- **Log tailing** — read `BepInEx/LogOutput.log` without locking the game

## Architecture

```text
                 MCP Client (Claude, Cursor, OpenCode, ...)
                                    │ stdio
                                    ▼
                          MCP Server (Node.js)
                                    │ HTTP · 127.0.0.1:12345
                                    ▼
                       MCP Bridge (C# DLL, in-game)
                                    │
                        ┌───────────┴───────────┐
                     Mono                    IL2CPP
                        └───────────┬───────────┘
                                    ▼
                          Unity Runtime (main thread)
```

- The bridge executes every Unity API call on the **game's main thread**; requests queue until
  the frame is free (scene loads block, so long loads can time out — retry when idle).
- Tool-layer code is backend-neutral. IL2CPP-only paths (`il2cpp_*`, raw marshalling) sit behind
  `#if CPP`; the Mono build (net35, no `System.Text.Json`) carries a minimal compatible JSON layer
  so call sites stay identical.
- The listener is loopback-only and speaks one JSON request per HTTP connection.

## Supported runtimes

| Feature | IL2CPP (BepInEx 6) | Mono (BepInEx 5) | Mono (BepInEx 6) |
|---|---|---|---|
| Runtime inspection | ✅ live-tested | ✅ live-tested | build only |
| Reflection / type discovery | ✅ | ✅ | build only |
| Field / property access | ✅ | ✅ | build only |
| Method invocation (incl. `out`/`ref`) | ✅ | ✅ | build only |
| Static invocation + static field access | ✅ | ✅ | build only |
| C# execution | ✅ | ✅ | build only |
| Object handles + sessions | ✅ | ✅ | build only |
| Capabilities / pagination / truncation | ✅ | ✅ | build only |
| Harmony hook inventory (read-only) | ✅ | ✅ | build only |
| Log tail | ✅ (server-side) | ✅ (server-side) | ✅ (server-side) |

Live-tested on Unity 2022.3 (IL2CPP, BepInEx 6) and Unity 2019.4 (Mono, BepInEx 5.4).
The BepInEx 6 Mono build comes from the same sources but has no live runtime test yet — any
runtime difference is reported truthfully by `unity_capabilities`, never assumed.

## Requirements

- A Unity game with **BepInEx 5 or 6** (IL2CPP or Mono)
- **Node.js 18+** — MCP server
- **.NET SDK** — only if you build the C# bridge yourself

UnityExplorer itself is **not** required at runtime.

## Install

### Option A — prebuilt (recommended)

1. Download the bridge for your loader from [Releases](https://github.com/OmaruKoma/UnityExplorerMCP/releases):

   | Asset | For |
   |---|---|
   | `UnityExplorerMCP-<version>-IL2CPP.zip` | IL2CPP games on BepInEx 6 |
   | `UnityExplorerMCP-<version>-Mono-BepInEx6.zip` | Mono games on BepInEx 6 (Unity Mono) |
   | `UnityExplorerMCP-<version>-Mono-BepInEx5.zip` | Mono games on BepInEx 5 |

2. Extract the zip into `<GAME_ROOT>/BepInEx/plugins/`. It contains the bridge DLL and `mcs.dll`
   (required for `unity_execute_csharp`), which must stay side by side.

3. Start the game and confirm in `BepInEx/LogOutput.log`:

   ```text
   MCP Bridge plugin loaded (IL2CPP backend)
   ```

4. Set up the server from `unity-explorer-mcp-server-<version>.zip`:

   ```bash
   unzip unity-explorer-mcp-server-<version>.zip
   cd unity-explorer-mcp-server-<version>
   npm ci --omit=dev
   node dist/server.js        # speaks MCP on stdio
   ```

### Option B — from source

```powershell
git clone https://github.com/OmaruKoma/UnityExplorerMCP.git
cd UnityExplorerMCP
.\build.ps1                       # IL2CPP + Mono + MCP server
```

```powershell
.\build.ps1 -Backend IL2CPP       # dist/il2cpp/MCPBridge.IL2CPP.dll
.\build.ps1 -Backend Mono         # dist/mono/MCPBridge.Mono.dll        (BepInEx 6 Unity Mono)
.\build.ps1 -Backend MonoBepInEx5 # dist/mono-bepinex5/MCPBridge.Mono.dll (BepInEx 5)
.\build.ps1 -Backend Server       # mcp-server only
```

Build references come from environment variables, never from committed machine paths:

| Variable | Used by | Meaning |
|---|---|---|
| `UNITY_GAME_DIR` | IL2CPP build | Game root containing `BepInEx/` (interop + core assemblies) |
| `MONO_REF_ROOT` | Mono build | BepInEx Mono refs: `BepInEx/`, `UnityEngine.dll`, `mcs.dll` |

See [INSTALL.md](INSTALL.md) for the long version, including the BepInEx 5 flavor.

## Configure your MCP client

Any stdio MCP client works. Replace `<REPO_PATH>` with your server location and
`<GAME_ROOT_WITH_BEPINEX>` with the game root (only needed for log tail).

**Claude Desktop** — `claude_desktop_config.example.json`:

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

**Cursor** — `cursor-mcp.example.json`, same `mcpServers` shape.

**OpenCode** — `opencode-example.json`:

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

Then ask your agent to call `unity_capabilities`. If it answers, the whole chain is live.

## Tools

24 tools. Start every session with `unity_capabilities` — it reports backend, session, enabled
tools, limits and marshalling support for the game you are attached to.

**Discovery**

| Tool | Purpose |
|---|---|
| `unity_capabilities` | Backend, session, tools, limits, marshalling |
| `unity_ping` / `unity_scene_info` | Connectivity, current scene, session |
| `unity_list_assemblies` | Loaded assemblies (`filter`, paging) |

**Scene and objects**

| Tool | Purpose |
|---|---|
| `unity_hierarchy` | Scene tree (`max_depth`, `name_contains`, `limit`) |
| `unity_find_gameobjects` | Find by name (handles, `limit`/`cursor`) |
| `unity_get_gameobject` / `unity_get_components` | Object details, attached components |
| `unity_resolve_path` | Fresh handle from a Hierarchy path |

**Types and reflection**

| Tool | Purpose |
|---|---|
| `unity_inspect` / `unity_inspect_type` | Members with static/instance info (`member_limit`) |
| `unity_search_members` | Cross-assembly member search (`name_contains`, filters, paging) |
| `unity_find_objects_of_type` | Live instances of a type (singletons, managers, controllers) |

**Values and calls**

| Tool | Purpose |
|---|---|
| `unity_get_field` / `unity_set_field` | Instance fields |
| `unity_get_property` / `unity_set_property` | Instance properties |
| `unity_get_static` / `unity_set_static` | Static fields and properties |
| `unity_invoke_method` / `unity_invoke_static` | Calls (`out`/`ref` returned as `outArgs`) |

**Runtime**

| Tool | Purpose |
|---|---|
| `unity_execute_csharp` | C# in the running game (`returnEncoding` for `byte[]`) |
| `unity_list_hooks` | Harmony patch inventory (read-only) |
| `unity_tail_log` | BepInEx log tail (`lines`, `keyword`, works without the game) |

Object identity uses session-aware handles (`Shop#1:-33814`). Writes and invocations validate the
session — after a scene change or restart, re-resolve with `unity_resolve_path` or a find call;
the bridge answers `STALE_HANDLE` instead of touching an unrelated object.

## Workflows

**Runtime exploration**

```text
unity_capabilities → search_members → find_objects_of_type → inspect
    → get_field / get_property → invoke_method / invoke_static
    → execute_csharp → observe → tail_log
```

**Mod development**

Explore with the bridge, verify behavior live, then write the final mod as an independent
BepInEx plugin. Use `unity_tail_log` to watch your mod's logs and `unity_list_hooks` to confirm
patch state. The finished mod does not depend on this project.

## Configuration

| Variable | Default | Scope |
|---|---|---|
| `UNITY_BRIDGE_URL` | `http://127.0.0.1:12345` | Server → bridge address |
| `REQUEST_TIMEOUT` | `30000` | Server request timeout (ms) |
| `UNITY_GAME_ROOT` | — | Game root for `unity_tail_log` auto-detection |
| `MCP_BRIDGE_PORT` | `12345` | Bridge listen port (read in-game; keep in sync with `UNITY_BRIDGE_URL`) |
| `MCP_ARRAY_LIMIT` | `500` | Max inlined array items (bridge) |
| `MCP_OUTPUT_LIMIT` | `8192` | Max string characters before truncation (bridge) |
| `MCP_BYTE_LIMIT` | `32768` | Max raw bytes before truncation (bridge) |

## Protocol

The bridge is a plain loopback HTTP endpoint, so you can probe it by hand:

```bash
curl -s http://127.0.0.1:12345/ -H "Content-Type: application/json" \
     -d '{"method":"ping","params":{}}'
```

```json
{"Success":true,"Data":{...}}
```

`method` is one of the bridge-side methods the server maps tools onto. Anything that speaks
stdio MCP can use the bridge through the bundled Node server; direct HTTP is for debugging.

## Troubleshooting

- **DLL not loaded** — the bridge build must match the loader. BepInEx 5 silently skips BepInEx 6
  builds (check the plugin count in `LogOutput.log`). `mcs.dll` must sit next to the bridge DLL,
  or `unity_execute_csharp` fails.
- **Timeouts during loads** — scene loads block Unity's main thread. Wait until the game is idle
  and retry; a short `REQUEST_TIMEOUT` will misreport during loading.
- **`STALE_HANDLE`** — the session changed (scene load, restart). Re-resolve the handle.
- **`execute_csharp` CS0584** — prefer `foreach` over LINQ on `Type[]`; mcs trips on stripped
  interop stubs (the same quirk affects UnityExplorer's own console).
- **SRE stripped** — follow UnityExplorer's corelibs procedure for your Unity version.
- **Port already in use** — another bridge instance or tool holds `12345`; change
  `MCP_BRIDGE_PORT` and `UNITY_BRIDGE_URL` together.
- **Wrong backend reported** — `unity_capabilities` is authoritative; the DLL you copied decides
  IL2CPP vs Mono.

## Limitations

- Loopback only (`127.0.0.1`); no auth and no public exposure by design.
- No arbitrary Harmony patching — the hook inventory is read-only.
- No screenshots or input simulation (Editor-MCP territory).
- MelonLoader builds behind `#if ML` are not produced or documented in this phase.
- BepInEx 6 Mono builds but has no live runtime test yet.

## Security

The bridge executes arbitrary C# sent by the agent and listens on loopback with no
authentication. Treat it like an attached debugger: run it only against games you own, on your
own machine. `set_*` and `invoke_*` mutate live runtime state — inspect first, change second.

## Development

```powershell
.\build.ps1 -Backend Server      # mcp-server only
node test-tools.js               # tool registration, no game needed
node test.js                     # live bridge tests (game running)
$env:UNITY_BACKEND = "Mono"; node test.js
node verify.js                   # repo integrity
```

One SDK-style C# project, two configurations: `Release_IL2CPP` (net6) and `Release_Mono` (net35).
Sources are shared; backend differences follow UnityExplorer's `CPP`/`MONO` define convention.
Outputs land in `dist/il2cpp`, `dist/mono`, `dist/mono-bepinex5`.

## Acknowledgements

- [UnityExplorer](https://github.com/sinai-dev/UnityExplorer) — runtime inspection toolkit and
  reference for the C# console behavior this bridge mirrors. UnityExplorer is GPL-3.0 and is not
  part of this codebase.
- **Mono.CSharp** (`mcs.dll`) — MIT, bundled for in-game C# execution.
- [BepInEx](https://github.com/BepInEx/BepInEx) — mod loader integration.
- [Model Context Protocol](https://modelcontextprotocol.io) SDK for the server transport.

## License

[MIT](LICENSE)
