# UnityExplorerMCP

**Runtime MCP bridge for Unity games.** Give an AI agent a debugger-grade grip on a *running* Unity game: walk the scene, explore the type system, read and write fields and properties, call methods, execute C# in-process and tail the BepInEx log — through the Model Context Protocol.

UnityExplorerMCP connects MCP-compatible AI clients to live Unity game processes through a BepInEx plugin. It supports both IL2CPP and Mono, and is designed for runtime inspection, mod debugging, and live experimentation without requiring the Unity Editor or game project files.

[![Release](https://img.shields.io/github/v/release/OmaruKoma/UnityExplorerMCP?color=2ea043&label=release)](https://github.com/OmaruKoma/UnityExplorerMCP/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-3da639.svg)](LICENSE)
[![Node](https://img.shields.io/badge/node-%E2%89%A518-339933?logo=nodedotjs&logoColor=white)](#requirements)
[![Runtimes](https://img.shields.io/badge/runtime-IL2CPP%20%7C%20Mono-8250df)](#supported-runtimes)
[![MCP](https://img.shields.io/badge/MCP-stdio-6f42c1)](https://modelcontextprotocol.io)

English | [简体中文](README.zh-CN.md)

**Docs:** [Project site](https://omarukoma.github.io/UnityExplorerMCP/) · [Install guide](INSTALL.md) · [Releases](https://github.com/OmaruKoma/UnityExplorerMCP/releases)

> **This is not a Unity Editor MCP.** It attaches to the shipped game process — no Editor, no project files, no recompile-and-restart loop.

## Contents

- [Why a runtime MCP](#why-a-runtime-mcp)
- [Quick start](#quick-start)
- [What's new in 1.3](#whats-new-in-13)
- [Features](#features)
- [Architecture](#architecture)
- [Supported runtimes](#supported-runtimes)
- [Requirements](#requirements)
- [Install](#install)
- [Configure your MCP client](#configure-your-mcp-client)
- [Tools](#tools)
- [Workflows](#workflows)
- [Configuration](#configuration)
- [Protocol](#protocol)
- [Troubleshooting](#troubleshooting)
- [Limitations](#limitations)
- [Security](#security)
- [Development](#development)
- [Acknowledgements](#acknowledgements)
- [License](#license)

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
Development and testing target the maintained
[yukieiji/UnityExplorer](https://github.com/yukieiji/UnityExplorer) fork.

## Quick start

1. **Drop the bridge into the game** — extract the zip matching your loader into
   `<GAME_ROOT>/BepInEx/plugins/` (the DLL and `mcs.dll` must stay side by side).
2. **Start the game** — `BepInEx/LogOutput.log` should contain
   `[MCPBridge] Started on port 12345`.
3. **Add the server to your MCP client** — point `command` at
   `node <REPO_PATH>/mcp-server/dist/server.js` (no URL needed; bridges are discovered).
4. **Verify** — call `unity_capabilities`: backend, pid, session, permissions and limits come
   back from the live process.

Prebuilt zips live on the [Releases](https://github.com/OmaruKoma/UnityExplorerMCP/releases)
page; if none matches the version you need, build from source (Option B below).

## What's new in 1.3

- **46 tools** (up from 24): snapshots/diff/undo, time-sliced instance scans, value search,
  array paging, byte decoding, labels/aliases, PNG capture, activity log.
- **Bridge-side capability gates + audit** — `preset=read-only` or per-capability
  `MCP_ALLOW_*`; mutating calls are recorded in `unity_activity_log`.
- **Multi-game discovery** — each bridge publishes a pid/port record; the server lists and
  selects targets instead of guessing.
- **Streamable HTTP transport** — `node dist/server.js --transport http` for URL-based clients.
- **IL2CPP correctness** — concrete proxy resolution and base-class private member lookup
  (TMP's `m_text`, native fields projected as properties), stable snapshot diffs.

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

**Inspect**

- **Live runtime inspection** — GameObjects, components, fields, properties, methods
- **Reflection across loaded assemblies** — type discovery, member search, live instances
- **Capability discovery** — `unity_capabilities` reports backend, pid, permissions, features and limits
- **Harmony hook inventory** — read-only view of patched methods and patch owners
- **Log tailing** — read `BepInEx/LogOutput.log` without locking the game

**Change and verify**

- **Reads and writes** — instance fields/properties, statics, method calls including `out`/`ref`
- **C# execution inside the game** — self-hosted Mono.CSharp, no UI dependency
- **Snapshots and diffs** — capture member values, then see only what changed (`epsilon`, `rebase`)
- **Undo** — revert recent scalar writes made through `set_field` / `set_property` / `set_static`
- **Value search, arrays, byte decoding** — find the object holding a text/number; page arrays; decode hex/base64/text/json/msgpack
- **Batch reads** — up to 10 read-only calls per turn instead of 10 round-trips

**Operate safely at scale**

- **Session-aware handles** — `session:id`; stale handles fail fast with `STALE_HANDLE`, including on reads
- **Bridge-side permissions + audit** — read-only preset or per-capability switches; mutating calls are logged
- **Multi-game discovery** — the bridge publishes a pid/port record; `unity_list_bridges` / `unity_select_bridge`
- **Time-sliced instance scans** — start/poll instead of blocking the main thread on large games
- **Labels and aliases** — readable names/notes for types and members, per game install
- **Method tracing** — hook a managed method and read real calls, args, results and callers (Mono)
- **Screenshots** — capture the current frame as PNG; optional file copy
- **LLM-sized output** — tables for record lists, largest-array trimming, pagination, hash truncation

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
- The listener is loopback-only and speaks one JSON request per HTTP connection. It prefers port
  `12345` and walks forward if that port is busy; the chosen port is published under
  `%LOCALAPPDATA%\UnityExplorerMCP\bridges\<pid>.json`.
- The MCP server speaks stdio by default, or stateless Streamable HTTP with
  `--transport http`. It discovers every running bridge, and refuses to guess when several games
  are live until `unity_select_bridge` picks one.

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
| Snapshots / diff / undo | ✅ | ✅ | build only |
| Instance scans / value search | ✅ | ✅ | build only |
| Arrays / byte decode / labels | ✅ | ✅ | build only |
| Method tracing (installs hooks) | ❌ (Mono-only) | ✅ requires Harmony | build only |
| PNG screenshot | ✅ (reflection-based) | ✅ (reflection-based) | build only |
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

1. Download the bridge for your loader from [Releases](https://github.com/OmaruKoma/UnityExplorerMCP/releases) (assets are `1.3.0` for this version):

   | Asset | For |
   |---|---|
   | `UnityExplorerMCP-1.3.0-IL2CPP.zip` | IL2CPP games on BepInEx 6 |
   | `UnityExplorerMCP-1.3.0-Mono-BepInEx6.zip` | Mono games on BepInEx 6 (Unity Mono) |
   | `UnityExplorerMCP-1.3.0-Mono-BepInEx5.zip` | Mono games on BepInEx 5 |

2. Extract the zip into `<GAME_ROOT>/BepInEx/plugins/`. It contains the bridge DLL and `mcs.dll`
   (required for `unity_execute_csharp`), which must stay side by side.

3. Start the game and confirm in `BepInEx/LogOutput.log`:

   ```text
   MCP Bridge plugin loaded (IL2CPP backend)
   ```

4. Set up the server from `unity-explorer-mcp-server-1.3.0.zip`:

   ```bash
   unzip unity-explorer-mcp-server-1.3.0.zip
   cd unity-explorer-mcp-server-1.3.0
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
`<GAME_ROOT_WITH_BEPINEX>` with the game root (only needed for log tail). Leave
`UNITY_BRIDGE_URL` unset unless you want to pin one specific game; discovery handles the rest.

**Claude Desktop** — `claude_desktop_config.example.json`:

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
        "UNITY_BRIDGE_TIMEOUT": "120000"
      }
    }
  }
}
```

**URL-based clients** — run `node dist/server.js --transport http --port 8765` and connect to
`http://127.0.0.1:8765/mcp`. The transport is stateless, so several clients can share it.

Then ask your agent to call `unity_capabilities`. If it answers, the whole chain is live.

## Tools

46 tools. Start every session with `unity_capabilities` — it reports backend, session, pid,
permissions, features, limits and marshalling support for the game you are attached to.

**Session and discovery**

| Tool | Purpose |
|---|---|
| `unity_capabilities` | Backend, session, pid, permissions, features, limits, marshalling |
| `unity_ping` / `unity_scene_info` | Connectivity, current scene, session |
| `unity_list_bridges` / `unity_select_bridge` | Discover live games and choose one |
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
| `unity_inspect` / `unity_inspect_type` | Members with static/instance info (`member_limit`), labels/aliases |
| `unity_search_members` | Cross-assembly member search (`name_contains`, filters, paging) |
| `unity_find_objects_of_type` | Live instances of a type (singletons, managers, controllers) |
| `unity_start_instance_scan` / `unity_get_instance_scan` | Time-sliced scan with progress and paging |
| `unity_search_values` | Find the component holding a known text/number value |

**Values and calls**

| Tool | Purpose |
|---|---|
| `unity_get_field` / `unity_set_field` | Instance fields |
| `unity_get_property` / `unity_set_property` | Instance properties |
| `unity_get_static` / `unity_set_static` | Static fields and properties |
| `unity_invoke_method` / `unity_invoke_static` | Calls (`out`/`ref` returned as `outArgs`) |
| `unity_read_array` | Page an array/list member; reference elements get handles |
| `unity_decode_byte_array` | Decode a `byte[]` as text, JSON, hex, base64 or msgpack |

**State verification**

| Tool | Purpose |
|---|---|
| `unity_snapshot_object` | Remember member values and return a snapshot id |
| `unity_diff_snapshot` | Only what changed; `against`, `rebase`, `epsilon` |
| `unity_undo_write` | Revert the most recent scalar writes |
| `unity_batch` | Up to 10 read-only calls in one turn, ordered results, 28 KiB budget |

**Labels, tracing, capture**

| Tool | Purpose |
|---|---|
| `unity_list_labels` / `unity_set_label` / `unity_remove_label` | Per-install names and notes |
| `unity_start_method_trace` / `unity_stop_method_trace` / `unity_clear_method_trace` | Hook a managed method (Mono) |
| `unity_list_method_traces` / `unity_get_method_trace` / `unity_build_call_graph` | Read real calls and caller→target edges |
| `unity_capture_screenshot` | Current frame as PNG image content; optional file copy |

**Runtime**

| Tool | Purpose |
|---|---|
| `unity_execute_csharp` | C# in the running game (`returnEncoding` for `byte[]`) |
| `unity_list_hooks` | Harmony patch inventory (read-only) |
| `unity_activity_log` | Recent mutating/invoking/tracing calls with outcome |
| `unity_tail_log` | BepInEx log tail (`lines`, `keyword`, works without the game) |

Object identity uses session-aware handles (`Shop#1:-33814`). Writes and invocations validate the
session — after a scene change or restart, re-resolve with `unity_resolve_path` or a find call;
the bridge answers `STALE_HANDLE` instead of touching an unrelated object.

Permissions are enforced **inside the game** (`BepInEx/config/UnityExplorerMCP.cfg` or
`MCP_ALLOW_*`): `write`, `invoke`, `execute` and `trace` can be disabled individually, or the
whole bridge pinned to `preset=read-only`. The server cannot grant itself more access.

List results are tabulated (`{cols, rows}`) when that is shorter, and an oversized reply is
trimmed from its largest array (`truncated`, `trimmed_arrays`) instead of being cut mid-JSON.
Method tracing installs real Harmony hooks, so it is Mono-only; the IL2CPP build reports
`TRACE_UNSUPPORTED` and `method_trace: false` in `unity_capabilities`.

## Workflows

**Runtime exploration**

```text
unity_capabilities → search_members → find_objects_of_type → inspect
    → get_field / get_property → invoke_method / invoke_static
    → execute_csharp → observe → tail_log
```

**Verify a change**

```text
unity_snapshot_object { handle }        → snap-1
unity_set_field { handle, field, value }
unity_diff_snapshot { snapshot: "snap-1", rebase: true }
unity_undo_write { steps: 1 }           # if the diff is wrong
```

**One turn instead of six**

```text
unity_batch { calls: [
  { tool: "unity_scene_info" },
  { tool: "unity_find_objects_of_type", arguments: { type: "Game.ShopController" } },
  { tool: "unity_inspect", arguments: { handle: "Shop#1:-33814" } },
  { tool: "unity_get_field", arguments: { handle: "Shop#1:-33814", field: "_isReady" } }
] }
```

**Which object shows this text?**

```text
unity_search_values { value: "Buy now", component_type: "Text" }
unity_decode_byte_array { handle, member: "packet" , format: "msgpack" }
```

**Mod development**

Explore with the bridge, verify behavior live, then write the final mod as an independent
BepInEx plugin. Use `unity_tail_log` to watch your mod's logs and `unity_list_hooks` to confirm
patch state. The finished mod does not depend on this project.

## Configuration

| Variable | Default | Scope |
|---|---|---|
| `UNITY_BRIDGE_URL` | — | Pins one bridge URL; when unset, discovery/port scan picks the target |
| `UNITY_BRIDGE_PID` | — | Prefer the discovered bridge with this process id |
| `UNITY_BRIDGE_TIMEOUT` | `120000` | Server → bridge request timeout (ms); raise for long `execute_csharp` |
| `UNITY_MCP_MAX_BYTES` | `1500000` | Hard cap on tool-result text |
| `UNITY_RESULT_BUDGET` | `120000` | Model-facing result budget before largest-array trimming |
| `UNITY_RESULT_TABLES` | `1` | Set `0` to keep record lists as object arrays |
| `UNITY_BATCH_BUDGET` | `28000` | Budget for `unity_batch` results |
| `UNITY_DISCOVERY_DIR` | `%LOCALAPPDATA%\UnityExplorerMCP\bridges` | Bridge discovery records |
| `UNITY_GAME_ROOT` | — | Game root for `unity_tail_log` auto-detection |
| `UNITY_MCP_TRANSPORT` | `stdio` | `stdio` or `http` |
| `UNITY_MCP_HTTP_PORT` | `8765` | Streamable HTTP port (`--transport http`) |
| `MCP_BRIDGE_PORT` | `12345` | Preferred bridge port; walks forward when busy |
| `MCP_BRIDGE_PORT_SPAN` | `20` | How many ports to try after the preferred one |
| `MCP_PERMISSION_PRESET` | `full` | `read-only` disables write/invoke/execute/trace |
| `MCP_ALLOW_WRITE` / `MCP_ALLOW_INVOKE` / `MCP_ALLOW_EXECUTE` / `MCP_ALLOW_TRACE` | `true` | Per-capability gates |
| `MCP_DISCOVERY` | `true` | Write the discovery record |
| `MCP_AUDIT` | `true` | Record mutating calls in the activity log |
| `MCP_MAX_SNAPSHOTS` | `16` | Retained snapshots |
| `MCP_MAX_UNDO` | `32` | Retained writes for `unity_undo_write` |
| `MCP_MAX_TRACES` | `8` | Retained trace sessions |
| `MCP_MAX_TRACE_RECORDS` | `500` | Records per trace (grouped calls share a row) |
| `MCP_MAX_SCREENSHOT_BYTES` | `1000000` | PNG downscale target |
| `MCP_MAX_SEARCH_COMPONENTS` | `50000` | Component scan cap for `unity_search_values` |
| `MCP_ARRAY_LIMIT` | `500` | Max inlined array items (bridge) |
| `MCP_OUTPUT_LIMIT` | `8192` | Max string characters before truncation (bridge) |
| `MCP_BYTE_LIMIT` | `32768` | Max raw bytes before truncation (bridge) |

In-game settings can also live in `BepInEx/config/UnityExplorerMCP.cfg` (INI-style
`key=value`: `preset`, `allow_write`, `allow_invoke`, `allow_execute`, `allow_trace`,
`mcp_bridge_port`, `discovery`, `audit`). Environment variables win. Labels are stored in
`UnityExplorerMCP.labels.json` next to that config.

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
  and retry; a short `UNITY_BRIDGE_TIMEOUT` will misreport during loading.
- **`STALE_HANDLE`** — the session changed (scene load, restart). Re-resolve the handle.
  Reads validate the session too, so an old id is never silently mapped to a different object.
- **IL2CPP fields look like properties** — Il2CppInterop projects native fields (`m_text`,
  `m_Name`, ...) as properties. `unity_get_field` / `unity_set_field` fall back to the property,
  and `unity_search_values` scans both fields and `m_`/`_` property projections.
- **`execute_csharp` CS0584** — prefer `foreach` over LINQ on `Type[]`; mcs trips on stripped
  interop stubs (the same quirk affects UnityExplorer's own console).
- **SRE stripped** — follow UnityExplorer's corelibs procedure for your Unity version.
- **Several games running** — `unity_list_bridges`, then `unity_select_bridge { pid }` or
  `{ port }`. Remove `UNITY_BRIDGE_URL` from the client config so discovery is used.
- **Port already in use** — the bridge walks to the next free port and publishes the actual one;
  pin it with `MCP_BRIDGE_PORT` only if you need a fixed port.
- **`PERMISSION_DENIED`** — the in-game config disabled that capability. Enable it in
  `UnityExplorerMCP.cfg` or set `MCP_ALLOW_*`, then restart the game.
- **`TRACE_UNSUPPORTED`** — method tracing needs the Mono backend and Harmony; IL2CPP only has the
  read-only `unity_list_hooks` inventory.
- **Screenshot is black or fails** — `ScreenCapture` is invoked by reflection; some runtimes only
  produce a correct frame at end of frame. Try again while the game is rendering, or raise
  `MCP_MAX_SCREENSHOT_BYTES`.
- **Wrong backend reported** — `unity_capabilities` is authoritative; the DLL you copied decides
  IL2CPP vs Mono.

## Limitations

- Loopback only (`127.0.0.1`); no auth and no public exposure by design.
- Method tracing is Mono-only and installs Harmony hooks on one managed method per session.
  Arbitrary patching, assembly loading and raw native execution stay outside the MCP surface.
- No input simulation (Editor-MCP territory).
- Screenshot encoding is reflection-based because Unity module availability differs across
  versions; a few runtimes only expose `EncodeToPNG` through one of the two paths.
- MelonLoader builds behind `#if ML` are not produced or documented in this phase.
- BepInEx 6 Mono builds but has no live runtime test yet.

## Security

The bridge executes arbitrary C# sent by the agent and listens on loopback. Authentication is
still out of scope, but the capability gate is bridge-side: `preset=read-only` (or individual
`MCP_ALLOW_*` switches) blocks writes, invocations, C# execution and tracing, and every mutating
call is written to the in-memory activity log. Treat it like an attached debugger: run it only
against games you own, on your own machine. `set_*` and `invoke_*` mutate live runtime state —
inspect first, change second; use snapshots/diff and `unity_undo_write` while experimenting.

## Development

```powershell
.\build.ps1 -Backend Server      # mcp-server only
node test-tools.js               # 46 tools, annotations, batch guard (no game needed)
node test.js                     # live bridge tests (game running)
$env:UNITY_BACKEND = "Mono"; node test.js
node verify.js                   # repo integrity
```

One SDK-style C# project, two configurations: `Release_IL2CPP` (net6) and `Release_Mono` (net35).
Sources are shared; backend differences follow UnityExplorer's `CPP`/`MONO` define convention.
Outputs land in `dist/il2cpp`, `dist/mono`, `dist/mono-bepinex5`.

## Acknowledgements

This project stands on the Unity runtime-inspection work that came before it.

**Runtime foundation**

- [UnityExplorer](https://github.com/sinai-dev/UnityExplorer) — the original in-game inspector.
  This bridge mirrors its C# console behavior and uses the same Mono.CSharp engine. UnityExplorer
  is GPL-3.0 and is not part of this codebase.
- [yukieiji/UnityExplorer](https://github.com/yukieiji/UnityExplorer) — the maintained fork this
  project is developed and tested against, with updated IL2CPP/interop support.

**Ideas from peer projects** (design review only; no code copied)

- [Jadis0x/UnityRuntimeExplorer](https://github.com/Jadis0x/UnityRuntimeExplorer) — a live
  IL2CPP/Mono inspector with MCP integration, built on URKit. Its tool-centric runtime model
  informed our handle-based object workflow and the in-game bridge / external server split.
- [snowyegret23/UnityInfoMCP](https://github.com/snowyegret23/UnityInfoMCP) — an external MCP
  server plus in-game bridge for inspection and localization workflows. Its compact, LLM-sized
  result shaping informed our tables, paging and truncation strategy.

**Dependencies**

- **Mono.CSharp** (`mcs.dll`) — MIT, bundled for in-game C# execution.
- [BepInEx](https://github.com/BepInEx/BepInEx) — mod loader integration.
- [Model Context Protocol](https://modelcontextprotocol.io) SDK for the server transport.

## License

[MIT](LICENSE)
