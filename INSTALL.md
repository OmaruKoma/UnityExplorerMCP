# Installation

> All paths below are relative to your local clone of this repository.
> Replace `<GAME_ROOT>` with the folder of your Unity game (the one containing `BepInEx/`).

## Requirements

- A Unity game with **BepInEx** installed (IL2CPP or Mono) and **UnityExplorer** loaded
- **Node.js 18+** (MCP Server)
- **.NET SDK** (building the C# bridge)

## Step 1: Build

```powershell
git clone <repository-url>
cd UnityExplorerMCP
.\build.ps1
```

Build backends separately if needed:

```powershell
.\build.ps1 -Backend IL2CPP        # dist/il2cpp/MCPBridge.IL2CPP.dll
.\build.ps1 -Backend Mono          # dist/mono/MCPBridge.Mono.dll (BepInEx 6 Unity Mono)
.\build.ps1 -Backend MonoBepInEx5  # dist/mono-bepinex5/MCPBridge.Mono.dll (BepInEx 5)
.\build.ps1 -Backend Server        # mcp-server only
```

Reference assemblies resolve from environment variables (no hardcoded paths):

| Variable | Meaning | Default |
|---|---|---|
| `UNITY_GAME_DIR` | IL2CPP game root with `BepInEx/` | — (required for IL2CPP builds) |
| `MONO_REF_ROOT` | BepInEx Mono refs (`BepInEx/`, `UnityEngine.dll`, `mcs.dll`) | `UnityExplorer/lib/net35` (sibling checkout) |

Example:

```powershell
$env:UNITY_GAME_DIR = "<GAME_ROOT>"
.\build.ps1 -Backend IL2CPP
```

## Step 2: Install the bridge DLL

Copy **both** files (the C# engine `mcs.dll` is required for `execute_csharp`):

| Game runtime | Copy from | Copy to |
|---|---|---|
| IL2CPP (BepInEx 6) | `dist/il2cpp/` | `<GAME_ROOT>/BepInEx/plugins/` |
| Mono (BepInEx 6 Unity Mono) | `dist/mono/` | `<GAME_ROOT>/BepInEx/plugins/` |
| Mono (BepInEx 5) | `dist/mono-bepinex5/` | `<GAME_ROOT>/BepInEx/plugins/` |

```powershell
Copy-Item "dist\mono-bepinex5\MCPBridge.Mono.dll", "dist\mono-bepinex5\mcs.dll" "<GAME_ROOT>\BepInEx\plugins\"
```

Restart the game after copying (BepInEx loads plugins at startup only).
Confirm in `BepInEx/LogOutput.log`:

```text
MCP Bridge plugin loaded (Mono backend)
```

MelonLoader is not covered in this phase; use a BepInEx setup.

## Step 3: Configure an MCP client

Copy the matching example and replace `<REPO_PATH>` / `<GAME_ROOT_WITH_BEPINEX>`:

- OpenCode: `opencode-example.json` → your `opencode.json`
- Claude Desktop: `claude_desktop_config.example.json` → Claude's config file
- Cursor: `cursor-mcp.example.json` → Cursor's MCP config

Environment variables (all optional except where noted):

| Variable | Default | Purpose |
|---|---|---|
| `UNITY_BRIDGE_URL` | `http://127.0.0.1:12345` | Bridge address (protocol default, keep as-is for local games) |
| `MCP_BRIDGE_PORT` | `12345` | Bridge listen port, read in-game (match with `UNITY_BRIDGE_URL`) |
| `REQUEST_TIMEOUT` | `30000` | MCP Server request timeout (ms) |
| `UNITY_GAME_ROOT` | — | Game root for `unity_tail_log` auto-detection |
| `MCP_ARRAY_LIMIT` | `500` | Max inlined array items (bridge) |
| `MCP_OUTPUT_LIMIT` | `8192` | Max string chars before truncation (bridge) |
| `MCP_BYTE_LIMIT` | `32768` | Max raw bytes before truncation (bridge) |

## Step 4: Run

1. Start the Unity game (UnityExplorer + MCP Bridge loaded)
2. Start your MCP client (it launches `mcp-server/dist/server.js` via stdio)
3. In the agent, start with `unity_capabilities`, then explore

## Step 5: Verify

```powershell
node test.js                    # live bridge tests (needs the game running)
node test-tools.js              # MCP tool registration (no game needed)
node verify.js                  # repo file integrity
```

Run backend-specific tests with:

```powershell
$env:UNITY_BACKEND = "Mono"     # or "IL2CPP" (default)
node test.js
```

## Troubleshooting

### Bridge DLL not loaded

- The DLL must match the loader: BepInEx 5 games need the `mono-bepinex5` build
  (a BepInEx 6 build is silently skipped by the BepInEx 5 chainloader — check the
  plugin count in the log).
- `mcs.dll` must sit next to the bridge DLL or `execute_csharp` is unavailable.

### Requests time out while the game loads

Scene loads block Unity's main thread; the bridge answers again once loading
finishes. Short-timeout tests may fail during loads — re-run when idle.

### `execute_csharp` reports CS0584 (mcs internal error)

The Mono.CSharp compiler enumerates referenced assemblies for extension-method
resolution and chokes on stripped UnityEngine interop stubs (UnityExplorer's own
C# Console has the same quirk). Prefer `foreach` loops over LINQ-on-`Type[]`.

### C# evaluator unavailable (SRE stripped)

Follow UnityExplorer's corelibs procedure
(`https://unity.bepinex.dev/corlibs/`) for your Unity version.

## Uninstall

1. Delete `MCPBridge.*.dll` + `mcs.dll` from the game's `BepInEx/plugins/`
2. Remove the `unity` MCP server entry from your client config
