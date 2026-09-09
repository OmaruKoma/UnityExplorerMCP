# UnityExplorerMCP

English | [简体中文](README.zh-CN.md)

**UnityExplorerMCP is a runtime MCP bridge for Unity games.** It exposes UnityExplorer's runtime inspection, reflection and C# execution capabilities to MCP-compatible AI agents — for runtime debugging, exploration, experimentation and AI-assisted mod development.

This is **not** a Unity Editor MCP and **not** an OpenCode-exclusive project. OpenCode is one supported MCP client.

Typical flow:

```text
Running Unity Game
        ↓
UnityExplorer (runtime exploration layer)
        ↓
UnityExplorerMCP (agent interface)
        ↓
MCP Client / AI Agent (OpenCode, Claude, Cursor, ...)
        ↓
Inspect Runtime → Experiment → Verify
        ↓
Create a standalone Mod
```

UnityExplorer can be used as the runtime exploration layer during development. The final mod does not need to depend on UnityExplorer unless the mod itself requires it.

## Features

- Runtime inspection (GameObjects, components, fields, properties, methods)
- Reflection and type discovery across loaded assemblies
- Field / property access, method invocation, static access
- C# execution inside the running game (self-hosted Mono.CSharp, no UI dependency)
- Session-aware runtime object handles (`session:id`, stale handles fail fast)
- Capability discovery (`unity_capabilities` — call it first)
- Paginated, LLM-sized outputs (limits, cursors, truncation with sha256)
- Read-only Harmony hook inventory
- BepInEx log tailing without locking the game

## Architecture

```text
                MCP Client
        ┌──────────┼──────────┐
     OpenCode    Claude     Cursor
        │          │           │
        └──────────┼───────────┘
                   ↓ (stdio)
              MCP Server (Node.js)
                   ↓ (HTTP, 127.0.0.1:12345)
              MCP Bridge (C# DLL, in-game)
                   ↓
            Runtime Backend
             ┌─────┴──────┐
             │            │
           Mono        IL2CPP
             │            │
             └─────┬──────┘
                   ↓
     UnityExplorer / Unity Runtime
```

The bridge executes every Unity API call on the game's main thread. Tool-layer code is
backend-neutral; IL2CPP-only paths (`il2cpp_*`, raw marshalling) are isolated behind
`#if CPP`. The Mono backend (net35, no `System.Text.Json`) ships a minimal compatible
JSON layer so call sites stay identical.

## Supported Runtimes

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

Live-tested on Unity 2022.3 (IL2CPP) and Unity 2019.4 (Mono, BepInEx 5.4).
BepInEx 6 Mono builds from the same sources but has no live runtime test yet —
runtime differences, if any, are reported truthfully by `unity_capabilities`.

Related but distinct: **Runtime MCP** (this project, live game process) vs **Unity Editor MCP**
(Editor automation) vs **UnityExplorer** (in-game UI explorer) vs **MCP Client**
(the agent host) vs **BepInEx** (the mod loader) vs **Mono / IL2CPP** (scripting backends).

## Requirements

- A Unity game with **BepInEx** (IL2CPP or Mono) and **UnityExplorer** loaded
- **Node.js 18+** (MCP Server)
- **.NET SDK** (building the C# bridge)

## Installation

See [INSTALL.md](INSTALL.md) for the full guide. Short version:

```powershell
git clone <repository-url>
cd UnityExplorerMCP
.\build.ps1                       # IL2CPP + Mono + MCP Server
```

```powershell
.\build.ps1 -Backend IL2CPP       # dist/il2cpp/MCPBridge.IL2CPP.dll
.\build.ps1 -Backend Mono         # dist/mono/MCPBridge.Mono.dll (BepInEx 6 Unity Mono)
.\build.ps1 -Backend MonoBepInEx5 # dist/mono-bepinex5/MCPBridge.Mono.dll (BepInEx 5)
```

Copy the matching `MCPBridge.*.dll` **plus `mcs.dll`** to `<GAME_ROOT>/BepInEx/plugins/`,
restart the game, and confirm `MCP Bridge plugin loaded` in `BepInEx/LogOutput.log`.

Reference assemblies resolve from environment variables (`UNITY_GAME_DIR`,
`MONO_REF_ROOT`) — the repo contains no machine-specific paths.

## MCP Client Configuration

OpenCode is one supported client. Copy the matching example and replace
`<REPO_PATH>` / `<GAME_ROOT_WITH_BEPINEX>`:

### OpenCode

`opencode-example.json` → your `opencode.json`:

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

See `claude_desktop_config.example.json` (standard `mcpServers` format).

### Cursor

See `cursor-mcp.example.json` (standard `mcpServers` format).

## Available Tools

24 tools. Start every session with `unity_capabilities`.

| Tool | Purpose |
|---|---|
| `unity_capabilities` | Backend, session, tools, limits, marshalling |
| `unity_ping` / `unity_scene_info` | Connectivity, scene, session |
| `unity_find_gameobjects` | Find by name (handles, `limit`/`cursor`) |
| `unity_get_gameobject` / `unity_get_components` | Details, components |
| `unity_inspect` / `unity_inspect_type` | Members with static/instance info (`member_limit`) |
| `unity_search_members` | Cross-assembly member search |
| `unity_find_objects_of_type` | Live instances (singletons, managers) |
| `unity_get_field` / `unity_set_field` | Instance fields |
| `unity_get_property` / `unity_set_property` | Instance properties |
| `unity_get_static` / `unity_set_static` | Static fields/properties |
| `unity_invoke_method` / `unity_invoke_static` | Calls (`out`/`ref` via `outArgs`) |
| `unity_resolve_path` | Fresh handle from a Hierarchy path |
| `unity_hierarchy` | Scene tree (`max_depth`, `name_contains`, `limit`) |
| `unity_list_assemblies` | Assembly list (`filter`, paging) |
| `unity_list_hooks` | Harmony patch inventory (read-only) |
| `unity_execute_csharp` | C# in the running game |
| `unity_tail_log` | BepInEx log tail (local, no game needed) |

Object identity uses session-aware handles (`Shop#1:-33814`); writes validate the
session and stale handles fail fast with `STALE_HANDLE`. Large outputs are paged
(`limit`/`cursor`) or truncated (`truncated`/`length`/`sha256`/`head`/`tail`).

## Runtime Exploration Workflow

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
observe runtime results
```

## Modding Workflow

Explore with the bridge, verify behavior live, then write the final mod as an
independent BepInEx plugin. Use `unity_tail_log` to watch your mod's logs and
`unity_list_hooks` to confirm patch state.

## Configuration

| Variable | Default | Scope |
|---|---|---|
| `UNITY_BRIDGE_URL` | `http://127.0.0.1:12345` | Server → bridge address (protocol default) |
| `MCP_BRIDGE_PORT` | `12345` | Bridge listen port, read in-game (match with `UNITY_BRIDGE_URL`) |
| `REQUEST_TIMEOUT` | `30000` | Server request timeout (ms) |
| `UNITY_GAME_ROOT` | — | Game root for `unity_tail_log` auto-detection |
| `MCP_ARRAY_LIMIT` | `500` | Max inlined array items (bridge) |
| `MCP_OUTPUT_LIMIT` | `8192` | Max string chars before truncation (bridge) |
| `MCP_BYTE_LIMIT` | `32768` | Max raw bytes before truncation (bridge) |
| `UNITY_BACKEND` | `IL2CPP` | `test.js` expectation label |
| `UNITY_GAME_DIR` / `MONO_REF_ROOT` | — | Build-time reference locations |

## Troubleshooting

- **Bridge DLL not loaded**: match the DLL to the loader — BepInEx 5 games silently
  skip BepInEx 6 builds (check the plugin count in the log). `mcs.dll` must sit
  next to the bridge DLL for `execute_csharp`.
- **Timeouts during loads**: scene loads block Unity's main thread; re-run when idle.
- **`execute_csharp` CS0584**: prefer `foreach` over LINQ-on-`Type[]` (mcs quirk with
  stripped interop stubs, shared with UnityExplorer's own console).
- **SRE stripped**: follow UnityExplorer's corelibs procedure for your Unity version.

## Limitations

- Localhost only (`127.0.0.1`); no auth, no public exposure by design.
- No arbitrary Harmony patching (inventory is read-only).
- No screenshots / input simulation (Editor-MCP territory, out of scope).
- MelonLoader is not covered in this phase.
- BepInEx 6 Mono backend builds but has no live runtime test yet.

## Security / Safety

The bridge listens on loopback only and executes arbitrary C# the agent sends —
treat it like a debugger: run only against games you own, on your own machine.
`set_*` / `invoke_*` mutate live runtime state; prefer inspection first.

## Development

```powershell
.\build.ps1 -Backend Server      # mcp-server only
node test.js                     # live bridge tests (game running)
$env:UNITY_BACKEND = "Mono"; node test.js
node test-tools.js               # tool registration (no game needed)
node verify.js                   # repo integrity
```

## Building

One SDK-style project, two configurations: `Release_IL2CPP` (net6) and
`Release_Mono` (net35). Shared sources; backend differences via `CPP`/`MONO`
defines following UnityExplorer's own convention. Outputs go to
`dist/il2cpp`, `dist/mono`, `dist/mono-bepinex5`.

## License

MIT
