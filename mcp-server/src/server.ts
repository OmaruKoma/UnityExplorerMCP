#!/usr/bin/env node

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { createServer as createHttpServer } from "node:http";
import { tailLog } from "./logTail.js";
import { BATCH_ALLOWED, TOOL_DEFS, TOOL_NAMES } from "./tools.js";
import { renderResult, RESULT_BUDGET } from "./compaction.js";
import { BridgeManager } from "./bridge.js";

const VERSION = "1.3.1";
const BATCH_BUDGET = positiveInt(process.env.UNITY_BATCH_BUDGET, 28_000);
const MAX_TOOL_TEXT = positiveInt(process.env.UNITY_MCP_MAX_BYTES, 1_500_000);
const connectors = new BridgeManager();

function positiveInt(raw: string | undefined, fallback: number): number {
  const value = Number.parseInt(raw ?? "", 10);
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

type ToolContent =
  | { type: "text"; text: string }
  | { type: "image"; data: string; mimeType: string };

interface ToolResult {
  content: ToolContent[];
  isError?: boolean;
}

interface BridgeCall {
  envelope: any;
  text: string;
  failed: boolean;
}

const BRIDGE_ROUTES: Record<
  string,
  (args: any) => { method: string; params: Record<string, unknown> }
> = {
  unity_ping: () => ({ method: "ping", params: {} }),
  unity_scene_info: () => ({ method: "scene_info", params: {} }),
  unity_capabilities: () => ({ method: "capabilities", params: {} }),
  unity_find_gameobjects: (a) => ({
    method: "find_gameobjects",
    params: {
      name: a?.name,
      include_inactive: a?.include_inactive ?? true,
      limit: a?.limit,
      cursor: a?.cursor,
      name_contains: a?.name_contains,
    },
  }),
  unity_get_gameobject: (a) => ({
    method: "get_gameobject",
    params: { instance_id: a?.instance_id, handle: a?.handle },
  }),
  unity_get_components: (a) => ({
    method: "get_components",
    params: { instance_id: a?.instance_id, handle: a?.handle },
  }),
  unity_inspect: (a) => ({
    method: "inspect",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      type_name: a?.type_name,
      member_limit: a?.member_limit,
    },
  }),
  unity_get_field: (a) => ({
    method: "get_field",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      type: a?.type,
      field: a?.field,
    },
  }),
  unity_set_field: (a) => ({
    method: "set_field",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      type: a?.type,
      field: a?.field,
      value: a?.value,
    },
  }),
  unity_get_property: (a) => ({
    method: "get_property",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      type: a?.type,
      property: a?.property,
    },
  }),
  unity_set_property: (a) => ({
    method: "set_property",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      type: a?.type,
      property: a?.property,
      value: a?.value,
    },
  }),
  unity_invoke_method: (a) => ({
    method: "invoke_method",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      type: a?.type,
      method: a?.method,
      args: a?.args,
      returnEncoding: a?.returnEncoding ?? "hex",
    },
  }),
  unity_hierarchy: (a) => ({
    method: "hierarchy",
    params: {
      max_depth: a?.max_depth ?? 10,
      name_contains: a?.name_contains,
      limit: a?.limit,
    },
  }),
  unity_execute_csharp: (a) => ({
    method: "execute_csharp",
    params: { code: a?.code, returnEncoding: a?.returnEncoding ?? "hex" },
  }),
  unity_list_assemblies: (a) => ({
    method: "list_assemblies",
    params: { filter: a?.filter, limit: a?.limit, cursor: a?.cursor },
  }),
  unity_inspect_type: (a) => ({
    method: "inspect_type",
    params: {
      type: a?.type,
      assembly: a?.assembly,
      member_limit: a?.member_limit,
      member_contains: a?.member_contains,
    },
  }),
  unity_invoke_static: (a) => ({
    method: "invoke_static",
    params: {
      type: a?.type,
      assembly: a?.assembly,
      method: a?.method,
      args: a?.args,
      returnEncoding: a?.returnEncoding ?? "hex",
    },
  }),
  unity_resolve_path: (a) => ({
    method: "resolve_path",
    params: { path: a?.path },
  }),
  unity_search_members: (a) => ({
    method: "search_members",
    params: {
      name_contains: a?.name_contains,
      type_filter: a?.type_filter,
      member_kind: a?.member_kind,
      assembly_filter: a?.assembly_filter,
      limit: a?.limit,
      cursor: a?.cursor,
    },
  }),
  unity_find_objects_of_type: (a) => ({
    method: "find_objects_of_type",
    params: {
      type: a?.type,
      assembly: a?.assembly,
      limit: a?.limit,
      cursor: a?.cursor,
    },
  }),
  unity_start_instance_scan: (a) => ({
    method: "start_instance_scan",
    params: {
      type: a?.type,
      assembly: a?.assembly,
      include_inactive: a?.include_inactive,
    },
  }),
  unity_get_instance_scan: (a) => ({
    method: "get_instance_scan",
    params: { scan_id: a?.scan_id, cursor: a?.cursor, limit: a?.limit },
  }),
  unity_search_values: (a) => ({
    method: "search_values",
    params: {
      value: a?.value,
      field_contains: a?.field_contains,
      component_type: a?.component_type,
      scene: a?.scene,
      include_inactive: a?.include_inactive,
      match_mode: a?.match_mode,
      limit: a?.limit,
      cursor: a?.cursor,
    },
  }),
  unity_get_static: (a) => ({
    method: "get_static",
    params: {
      type: a?.type,
      member: a?.member,
      assembly: a?.assembly,
      returnEncoding: a?.returnEncoding ?? "hex",
    },
  }),
  unity_set_static: (a) => ({
    method: "set_static",
    params: {
      type: a?.type,
      member: a?.member,
      value: a?.value,
      assembly: a?.assembly,
    },
  }),
  unity_read_array: (a) => ({
    method: "read_array",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      member: a?.member,
      cursor: a?.cursor,
      limit: a?.limit,
    },
  }),
  unity_decode_byte_array: (a) => ({
    method: "decode_byte_array",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      member: a?.member,
      data: a?.data,
      encoding: a?.encoding,
      format: a?.format,
    },
  }),
  unity_snapshot_object: (a) => ({
    method: "snapshot_object",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      type: a?.type,
      member_contains: a?.member_contains,
      member_limit: a?.member_limit,
      include_properties: a?.include_properties,
    },
  }),
  unity_diff_snapshot: (a) => ({
    method: "diff_snapshot",
    params: {
      snapshot: a?.snapshot,
      against: a?.against,
      rebase: a?.rebase,
      epsilon: a?.epsilon,
    },
  }),
  unity_undo_write: (a) => ({
    method: "undo_write",
    params: { steps: a?.steps },
  }),
  unity_list_labels: (a) => ({
    method: "list_labels",
    params: {
      type_contains: a?.type_contains,
      limit: a?.limit,
      cursor: a?.cursor,
    },
  }),
  unity_set_label: (a) => ({
    method: "set_label",
    params: {
      type: a?.type,
      member: a?.member,
      label: a?.label,
      note: a?.note,
    },
  }),
  unity_remove_label: (a) => ({
    method: "remove_label",
    params: { type: a?.type, member: a?.member },
  }),
  unity_start_method_trace: (a) => ({
    method: "start_method_trace",
    params: {
      type: a?.type,
      method: a?.method,
      assembly: a?.assembly,
      param_count: a?.param_count,
    },
  }),
  unity_stop_method_trace: (a) => ({
    method: "stop_method_trace",
    params: { trace_id: a?.trace_id },
  }),
  unity_clear_method_trace: (a) => ({
    method: "clear_method_trace",
    params: { trace_id: a?.trace_id },
  }),
  unity_list_method_traces: () => ({
    method: "list_method_traces",
    params: {},
  }),
  unity_get_method_trace: (a) => ({
    method: "get_method_trace",
    params: {
      trace_id: a?.trace_id,
      cursor: a?.cursor,
      limit: a?.limit,
    },
  }),
  unity_build_call_graph: (a) => ({
    method: "build_call_graph",
    params: { trace_id: a?.trace_id, cursor: a?.cursor, limit: a?.limit },
  }),
  unity_list_hooks: (a) => ({
    method: "list_hooks",
    params: { limit: a?.limit, cursor: a?.cursor },
  }),
  unity_activity_log: (a) => ({
    method: "activity_log",
    params: { limit: a?.limit, cursor: a?.cursor },
  }),
  unity_capture_screenshot: (a) => ({
    method: "capture_screenshot",
    params: { output_path: a?.output_path, max_bytes: a?.max_bytes },
  }),
  unity_dump_material: (a) => ({
    method: "dump_material",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      property_limit: a?.property_limit,
      include_defaults: a?.include_defaults,
      include_hidden: a?.include_hidden,
    },
  }),
  unity_render_object: (a) => ({
    method: "render_object",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      width: a?.width,
      height: a?.height,
    },
  }),
  unity_frame_camera: (a) => ({
    method: "frame_camera",
    params: {
      instance_id: a?.instance_id,
      handle: a?.handle,
      target: a?.target,
      target_handle: a?.target_handle,
      camera_name: a?.camera_name,
      distance_scale: a?.distance_scale,
      offset_x: a?.offset_x,
      offset_y: a?.offset_y,
      look_at: a?.look_at,
      restore: a?.restore,
      reverse: a?.reverse,
    },
  }),
};

async function bridgeCall(
  method: string,
  params: Record<string, unknown>
): Promise<BridgeCall> {
  const envelope = await connectors.request(method, params);
  const failed =
    envelope && typeof envelope === "object" && envelope.Success === false;
  let text = renderResult(envelope, Math.min(RESULT_BUDGET, MAX_TOOL_TEXT));
  if (Buffer.byteLength(text, "utf8") > MAX_TOOL_TEXT) {
    text = text.slice(0, MAX_TOOL_TEXT);
  }
  return { envelope, text, failed };
}

function bridgeError(message: string): ToolResult {
  return { content: [{ type: "text", text: `Error: ${message}` }], isError: true };
}

async function ensureConnection(): Promise<ToolResult | null> {
  if (connectors.isConnected()) return null;
  let ok = false;
  try {
    ok = await connectors.ping();
  } catch (error: any) {
    if (error?.bridges) {
      const lines = error.bridges
        .map(
          (b: any) =>
            `pid ${b.pid ?? "?"} port ${b.port} ${b.backend ?? ""} ${
              b.session_id ?? ""
            }`
        )
        .join("\n");
      return bridgeError(
        `${error.message}\n${lines}\nCall unity_list_bridges, then unity_select_bridge.`
      );
    }
    return bridgeError(error?.message ?? String(error));
  }
  if (!ok) {
    return bridgeError(
      "Cannot connect to a Unity bridge. Start the game with the MCP bridge loaded, or call unity_list_bridges to see discovered targets."
    );
  }
  return null;
}

function stringifySummary(envelope: any, extra: Record<string, unknown> = {}) {
  const data = envelope?.Data ?? envelope?.data ?? {};
  const copy: Record<string, unknown> = { ...data, ...extra };
  if (typeof copy.base64 === "string") {
    copy.base64_bytes = copy.base64.length;
    delete copy.base64;
  }
  return JSON.stringify(copy, null, 2);
}

async function runTool(name: string, args: any): Promise<ToolResult> {
  // Local tools first: they must work with the game down.
  if (name === "unity_tail_log") {
    const logResult = await tailLog({
      path: args?.path as string | undefined,
      lines: (args?.lines as number | undefined) ?? 100,
      keyword: args?.keyword as string | undefined,
    });
    return { content: [{ type: "text", text: renderResult(logResult) }] };
  }

  if (name === "unity_list_bridges") {
    const bridges = await connectors.list();
    return {
      content: [
        {
          type: "text",
          text: renderResult({ bridges, selected: connectors.selected }),
        },
      ],
    };
  }

  if (name === "unity_select_bridge") {
    try {
      const selected = await connectors.select(args?.pid, args?.port);
      return {
        content: [
          { type: "text", text: renderResult({ ok: true, selected }) },
        ],
      };
    } catch (error: any) {
      return bridgeError(error?.message ?? String(error));
    }
  }

  if (name === "unity_batch") {
    return runBatch(args);
  }

  if (!TOOL_NAMES.has(name)) return bridgeError(`Unknown tool: ${name}`);
  const route = BRIDGE_ROUTES[name];
  if (!route) return bridgeError(`Tool has no bridge route: ${name}`);

  const offline = await ensureConnection();
  if (offline) return offline;

  const { method, params } = route(args ?? {});
  const call = await bridgeCall(method, params);

  if (name === "unity_capture_screenshot" || name === "unity_render_object") {
    const data = call.envelope?.Data ?? call.envelope?.data;
    if (!call.failed && data?.base64) {
      return {
        content: [
          { type: "text", text: stringifySummary(call.envelope) },
          {
            type: "image",
            data: data.base64,
            mimeType: data.mime_type ?? "image/png",
          },
        ],
      };
    }
  }

  return {
    content: [{ type: "text", text: call.text }],
    ...(call.failed ? { isError: true } : {}),
  };
}

/**
 * Getter-shaped names, mirroring BridgePermissions.IsReadOnlyMethod. Batch uses
 * this to decide whether an invoke_method call is a read or a mutation.
 */
const READ_ONLY_EXACT = new Set(["length", "count", "name", "tostring", "clone", "equals"]);
const READ_ONLY_PREFIXES = [
  "get", "is", "has", "can", "find", "contains", "count", "peek",
  "toarray", "tolist", "tostring", "lookup", "resolve", "query",
  "read", "enumerate", "calculate", "compute", "check",
];

function isReadOnlyMethod(name: string): boolean {
  const lower = name.trim().toLowerCase();
  if (!lower) return false;
  if (READ_ONLY_EXACT.has(lower)) return true;
  return READ_ONLY_PREFIXES.some((prefix) => lower.startsWith(prefix));
}

async function runBatch(args: any): Promise<ToolResult> {
  const calls = Array.isArray(args?.calls) ? args.calls : [];
  if (calls.length === 0) return bridgeError("batch needs at least one call");
  if (calls.length > 10) return bridgeError("batch accepts at most 10 calls");

  const offline = await ensureConnection();
  if (offline) return offline;

  const results: any[] = [];
  let used = 0;
  for (let i = 0; i < calls.length; i++) {
    const call = calls[i] ?? {};
    const tool = String(call.tool ?? "");
    const invokeTarget = tool === "unity_invoke_method" ? String(call.arguments?.method ?? "") : "";
    if (!BATCH_ALLOWED.has(tool)) {
      results.push({
        tool,
        ok: false,
        error: {
          code: "BATCH_TOOL_NOT_ALLOWED",
          message: `${tool} is not a read-only batch tool`,
        },
      });
      continue;
    }
    if (tool === "unity_invoke_method" && !isReadOnlyMethod(invokeTarget)) {
      // Reads like GetFloat/GetColor/GetTexture are batched; setters, Destroy
      // and anything else get-free stay out, so a batch can never mutate.
      results.push({
        tool,
        method: invokeTarget,
        ok: false,
        error: {
          code: "BATCH_METHOD_NOT_READ_ONLY",
          message:
            `${invokeTarget || "(missing method)"} is not a getter-shaped name; ` +
            "batch only allows Get*/Is*/Has*/Find*/ToArray* etc.",
        },
      });
      continue;
    }
    if (used >= BATCH_BUDGET) {
      results.push({
        tool,
        ok: false,
        error: { code: "BATCH_BUDGET_EXHAUSTED" },
      });
      continue;
    }

    let entry: any;
    if (tool === "unity_tail_log") {
      try {
        const logResult = await tailLog({
          path: call.arguments?.path,
          lines: call.arguments?.lines ?? 100,
          keyword: call.arguments?.keyword,
        });
        entry = { print: JSON.stringify(logResult, null, 2), result: logResult };
      } catch (error: any) {
        entry = { error: { code: "TAIL_LOG_FAILED", message: error?.message } };
      }
    } else {
      const route = BRIDGE_ROUTES[tool];
      const { method, params } = route(call.arguments ?? {});
      const envelope = await connectors.request(method, params);
      if (envelope?.Success === false) {
        entry = {
          error: {
            code: envelope?.Data?.code ?? "BRIDGE_ERROR",
            message: envelope?.Error ?? "bridge call failed",
            data: envelope?.Data,
          },
        };
      } else {
        entry = { result: envelope?.Data ?? envelope?.data };
      }
    }

    const serialized = JSON.stringify(entry);
    if (used + serialized.length > BATCH_BUDGET) {
      results.push({
        tool,
        ok: false,
        error: { code: "BATCH_BUDGET_EXHAUSTED" },
      });
      continue;
    }
    used += serialized.length;
    results.push(
      entry.error
        ? { tool, ok: false, error: entry.error }
        : { tool, ok: true, result: entry.result }
    );
  }

  return {
    content: [
      {
        type: "text",
        text: renderResult({ results, bytes: used }, BATCH_BUDGET),
      },
    ],
  };
}

function buildServer(): Server {
  const server = new Server(
    { name: "unity-explorer-mcp-server", version: VERSION },
    { capabilities: { tools: {} } }
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => ({
    tools: TOOL_DEFS,
  }));

  server.setRequestHandler(CallToolRequestSchema, async (request): Promise<any> => {
    try {
      return await runTool(request.params.name, request.params.arguments ?? {});
    } catch (error: any) {
      // Nothing may escape: the host would turn it into an opaque -32603.
      return {
        content: [
          {
            type: "text",
            text: `ERROR: ${error?.message ?? String(error)}\n${
              error?.stack ?? ""
            }`,
          },
        ],
        isError: true,
      };
    }
  });

  return server;
}

function parseFlag(flag: string): string | undefined {
  const index = process.argv.indexOf(flag);
  return index >= 0 ? process.argv[index + 1] : undefined;
}

async function runStdio(): Promise<void> {
  const connected = await connectors.ping().catch(() => false);
  console.error(
    connected
      ? "Connected to a Unity bridge"
      : "Warning: no Unity bridge yet; tools retry on call"
  );
  const server = buildServer();
  await server.connect(new StdioServerTransport());
  console.error(`Unity Explorer MCP ${VERSION} running on stdio`);
}

async function readJsonBody(req: any, limit = 20 * 1024 * 1024): Promise<any> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > limit) throw new Error("request body too large");
    chunks.push(Buffer.from(chunk));
  }
  if (chunks.length === 0) return undefined;
  const text = Buffer.concat(chunks).toString("utf8").trim();
  if (!text) return undefined;
  return JSON.parse(text);
}

async function runHttp(host: string, port: number): Promise<void> {
  const http = createHttpServer(async (req, res) => {
    if (req.method === "GET" && (req.url === "/" || req.url === "/health")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(
        JSON.stringify({
          status: "ok",
          transport: "streamable-http",
          version: VERSION,
          bridge: connectors.selected,
        })
      );
      return;
    }
    if (req.url !== "/mcp") {
      res.writeHead(404, { "Content-Type": "text/plain" });
      res.end("not found");
      return;
    }
    if (req.method !== "POST") {
      res.writeHead(405, { "Content-Type": "text/plain" });
      res.end("stateless mode accepts POST only");
      return;
    }

    try {
      const body = await readJsonBody(req);
      const server = buildServer();
      const transport = new StreamableHTTPServerTransport({
        sessionIdGenerator: undefined,
      });
      res.on("close", () => {
        void transport.close();
        void server.close();
      });
      await server.connect(transport);
      await transport.handleRequest(req, res, body);
    } catch (error: any) {
      if (!res.headersSent) {
        res.writeHead(400, { "Content-Type": "application/json" });
      }
      res.end(
        JSON.stringify({
          jsonrpc: "2.0",
          error: { code: -32700, message: error?.message ?? "bad request" },
          id: null,
        })
      );
    }
  });

  await new Promise<void>((resolve) => http.listen(port, host, resolve));
  console.error(
    `Unity Explorer MCP ${VERSION} listening on http://${host}:${port}/mcp`
  );
}

async function main(): Promise<void> {
  if (process.argv.includes("--help")) {
    console.log(`unity-explorer-mcp-server ${VERSION}

  node dist/server.js [--transport stdio|http] [--host 127.0.0.1] [--port 8765]

Environment:
  UNITY_BRIDGE_URL        explicit bridge URL (wins over discovery)
  UNITY_BRIDGE_PID        select a discovered bridge by pid
  UNITY_BRIDGE_TIMEOUT    bridge request timeout in ms (default 120000)
  UNITY_MCP_MAX_BYTES     hard cap on tool-result text
  UNITY_RESULT_BUDGET     model-facing result budget (default 120000)
  UNITY_RESULT_TABLES     set 0 to disable {cols, rows} tables
  UNITY_BATCH_BUDGET      batch result budget (default 28000)
  UNITY_DISCOVERY_DIR     bridge discovery directory
  UNITY_MCP_TRANSPORT     stdio (default) or http
  UNITY_MCP_HTTP_PORT     HTTP port (default 8765)
`);
    return;
  }

  const mode =
    parseFlag("--transport") ?? process.env.UNITY_MCP_TRANSPORT ?? "stdio";
  if (mode === "http") {
    const host =
      parseFlag("--host") ?? process.env.UNITY_MCP_HTTP_HOST ?? "127.0.0.1";
    const port = positiveInt(
      parseFlag("--port") ?? process.env.UNITY_MCP_HTTP_PORT,
      8765
    );
    await runHttp(host, port);
    return;
  }
  await runStdio();
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
