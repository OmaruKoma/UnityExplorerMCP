/**
 * Tool catalog. Every tool carries MCP annotations so clients can
 * auto-approve reads and confirm writes, plus a compact description
 * (the whole catalog is re-read by the model on every session).
 */

export type ToolEffect = "read" | "write" | "exec" | "state";

export interface ToolDef {
  name: string;
  description: string;
  inputSchema: Record<string, unknown>;
  annotations: {
    readOnlyHint: boolean;
    destructiveHint?: boolean;
    idempotentHint?: boolean;
    openWorldHint: boolean;
  };
}

function schema(
  properties: Record<string, unknown> = {},
  required: string[] = []
): Record<string, unknown> {
  const result: Record<string, unknown> = {
    type: "object",
    properties,
    additionalProperties: false,
  };
  if (required.length > 0) result.required = required;
  return result;
}

function tool(
  name: string,
  description: string,
  input: Record<string, unknown>,
  effect: ToolEffect = "read",
  idempotent?: boolean
): ToolDef {
  const annotations: ToolDef["annotations"] = {
    readOnlyHint: effect === "read",
    openWorldHint: false,
  };
  if (effect !== "read") {
    annotations.destructiveHint = effect === "write" || effect === "exec";
    annotations.idempotentHint = idempotent ?? false;
  }
  return { name, description, inputSchema: input, annotations };
}

const handle = {
  handle: {
    type: "string",
    description: "Session-aware handle (session:id) from find/resolve/hierarchy",
  },
};
const instanceId = {
  instance_id: {
    type: "number",
    description: "Legacy numeric instance id; prefer handle",
  },
};
const cursor = {
  cursor: { type: "number", description: "Start offset for paging" },
};
const limit = {
  limit: { type: "number", description: "Max items per page" },
};

export const TOOL_DEFS: ToolDef[] = [
  // ---- session / discovery
  tool(
    "unity_ping",
    "Ping the bridge; returns status, Unity version, session id, backend and pid.",
    schema()
  ),
  tool(
    "unity_capabilities",
    "Discover backend (IL2CPP/Mono), loader, session, pid, permissions, enabled tools, limits and marshalling support. Call this first.",
    schema()
  ),
  tool(
    "unity_scene_info",
    "Current active scene plus every loaded scene (name, build index, root count).",
    schema()
  ),
  tool(
    "unity_list_bridges",
    "List live game bridges discovered on this machine (pid, port, backend, session). Use before unity_select_bridge when several games run.",
    schema()
  ),
  tool(
    "unity_select_bridge",
    "Select which discovered game bridge later calls target. Select by pid or port.",
    schema(
      {
        pid: { type: "number", description: "Target process id" },
        port: { type: "number", description: "Target bridge port" },
      },
      []
    ),
    "state"
  ),

  // ---- scene and objects
  tool(
    "unity_find_gameobjects",
    "Find GameObjects by name (partial). Returns session-aware handles. Pass limit/cursor for large scenes.",
    schema({
      name: { type: "string", description: "Name to search for (partial match)" },
      include_inactive: {
        type: "boolean",
        description: "Include inactive GameObjects",
        default: true,
      },
      ...limit,
      ...cursor,
      name_contains: {
        type: "string",
        description: "Additional case-insensitive substring filter",
      },
    })
  ),
  tool(
    "unity_get_gameobject",
    "GameObject details: identity, path, transform, parent and children. Prefer handle over instance_id.",
    schema({ ...instanceId, ...handle })
  ),
  tool(
    "unity_get_components",
    "List components on a GameObject with type names, ids and enabled state.",
    schema({ ...instanceId, ...handle })
  ),
  tool(
    "unity_hierarchy",
    "Scene hierarchy tree. Use name_contains/limit to keep output small.",
    schema({
      max_depth: {
        type: "number",
        description: "Maximum depth to traverse (1-20, default 10)",
        default: 10,
      },
      name_contains: {
        type: "string",
        description: "Keep only matching subtrees (ancestors of matches survive)",
      },
      limit: {
        type: "number",
        description: "Max nodes returned (response carries truncated flag)",
      },
    })
  ),
  tool(
    "unity_resolve_path",
    "Re-resolve a Hierarchy path (Root/Child) to a fresh instance id after scene changes or restarts.",
    schema({ path: { type: "string", description: "Hierarchy path" } }, [
      "path",
    ])
  ),

  // ---- types and reflection
  tool(
    "unity_inspect",
    "Inspect a live object: fields, properties and methods (member lists capped by member_limit).",
    schema({
      ...instanceId,
      ...handle,
      type_name: {
        type: "string",
        description: "Full type name (for static inspection)",
      },
      member_limit: {
        type: "number",
        description: "Max fields/methods returned (default 200)",
      },
    })
  ),
  tool(
    "unity_inspect_type",
    "Inspect a type by full name: fields/properties/methods with static/instance, params and return types. Includes saved labels and aliases.",
    schema(
      {
        type: { type: "string", description: "Full type name (e.g. System.Math)" },
        assembly: {
          type: "string",
          description: "Optional assembly name to resolve the type from",
        },
        member_limit: {
          type: "number",
          description: "Max members per list (default 200)",
        },
        member_contains: {
          type: "string",
          description: "Case-insensitive substring filter on member names",
        },
      },
      ["type"]
    )
  ),
  tool(
    "unity_list_assemblies",
    "List loaded assemblies (optional substring filter). Pass limit/cursor for paging.",
    schema({
      filter: {
        type: "string",
        description: "Optional case-insensitive substring filter on assembly name",
      },
      ...limit,
      ...cursor,
    })
  ),
  tool(
    "unity_search_members",
    "Search fields/properties/methods across loaded assemblies by name. Returns assembly/type/member/signature plus labels and aliases.",
    schema(
      {
        name_contains: {
          type: "string",
          description: "Substring to search for in member names (required)",
        },
        type_filter: {
          type: "string",
          description: "Optional substring filter on type full names",
        },
        member_kind: {
          type: "string",
          description: "method, field, property or all (default all)",
        },
        assembly_filter: {
          type: "string",
          description: "Optional substring filter on assembly names",
        },
        ...limit,
        ...cursor,
      },
      ["name_contains"]
    )
  ),
  tool(
    "unity_find_objects_of_type",
    "Find live instances of a type by full name (singletons, managers, controllers). Returns handles.",
    schema(
      {
        type: { type: "string", description: "Full type name" },
        assembly: {
          type: "string",
          description: "Optional assembly to resolve the type from",
        },
        ...limit,
        ...cursor,
      },
      ["type"]
    )
  ),
  tool(
    "unity_start_instance_scan",
    "Start a time-sliced instance scan for a type. Returns a scan_id immediately; poll unity_get_instance_scan for progress and results.",
    schema(
      {
        type: { type: "string", description: "Full type name" },
        assembly: {
          type: "string",
          description: "Optional assembly to resolve the type from",
        },
        include_inactive: {
          type: "boolean",
          description: "Include inactive GameObject-owned instances",
        },
      },
      ["type"]
    ),
    "state"
  ),
  tool(
    "unity_get_instance_scan",
    "Poll a scan started by unity_start_instance_scan: progress, done flag and a page of handles.",
    schema(
      {
        scan_id: { type: "string", description: "Scan id from start_instance_scan" },
        ...cursor,
        ...limit,
      },
      ["scan_id"]
    )
  ),
  tool(
    "unity_search_values",
    "Search live component field values (strings/numbers) across the scene. Finds which object holds a known text or value.",
    schema(
      {
        value: { type: "string", description: "Value to look for" },
        field_contains: {
          type: "string",
          description: "Only fields whose name contains this text",
        },
        component_type: {
          type: "string",
          description: "Only components whose type name contains this text",
        },
        scene: { type: "string", description: "Only objects in this scene" },
        include_inactive: { type: "boolean", default: true },
        match_mode: {
          type: "string",
          description: "contains (default), exact, starts_with or ends_with",
        },
        ...limit,
        ...cursor,
      },
      ["value"]
    )
  ),

  // ---- values and calls
  tool(
    "unity_get_field",
    "Read an instance field. Prefer handle over instance_id.",
    schema(
      {
        ...instanceId,
        ...handle,
        type: { type: "string", description: "Full type name of the object" },
        field: { type: "string", description: "Field name" },
      },
      ["field"]
    )
  ),
  tool(
    "unity_set_field",
    "Write an instance field. The previous value is recorded for unity_undo_write. Stale handles fail fast.",
    schema(
      {
        ...instanceId,
        ...handle,
        type: { type: "string", description: "Full type name of the object" },
        field: { type: "string", description: "Field name" },
        value: { description: "New value" },
      },
      ["field", "value"]
    ),
    "write"
  ),
  tool(
    "unity_get_property",
    "Read an instance property (runs the getter).",
    schema(
      {
        ...instanceId,
        ...handle,
        type: { type: "string", description: "Full type name of the object" },
        property: { type: "string", description: "Property name" },
      },
      ["property"]
    )
  ),
  tool(
    "unity_set_property",
    "Write an instance property. The previous value is recorded for unity_undo_write.",
    schema(
      {
        ...instanceId,
        ...handle,
        type: { type: "string", description: "Full type name of the object" },
        property: { type: "string", description: "Property name" },
        value: { description: "New value" },
      },
      ["property", "value"]
    ),
    "write"
  ),
  tool(
    "unity_get_static",
    "Read a static field or property by type and member name.",
    schema(
      {
        type: { type: "string", description: "Full type name" },
        member: { type: "string", description: "Static field or property name" },
        assembly: { type: "string", description: "Optional assembly name" },
        returnEncoding: {
          type: "string",
          description: "byte[] encoding: hex (default) or base64",
        },
      },
      ["type", "member"]
    )
  ),
  tool(
    "unity_set_static",
    "Write a static field or property. The previous value is recorded for unity_undo_write.",
    schema(
      {
        type: { type: "string", description: "Full type name" },
        member: { type: "string", description: "Static field or property name" },
        value: { description: "New value" },
        assembly: { type: "string", description: "Optional assembly name" },
      },
      ["type", "member", "value"]
    ),
    "write"
  ),
  tool(
    "unity_invoke_method",
    "Invoke an instance method. out/ref params are returned in outArgs; byte[] honours returnEncoding.",
    schema(
      {
        ...instanceId,
        ...handle,
        type: { type: "string", description: "Full type name of the object" },
        method: { type: "string", description: "Method name" },
        args: { type: "array", description: "Method arguments", items: {} },
        returnEncoding: {
          type: "string",
          description: "byte[] encoding: hex (default) or base64",
        },
      },
      ["method"]
    ),
    "exec"
  ),
  tool(
    "unity_invoke_static",
    "Invoke a static method; out/ref params are returned in outArgs.",
    schema(
      {
        type: { type: "string", description: "Full type name" },
        method: { type: "string", description: "Static method name" },
        args: { type: "array", description: "Arguments", items: {} },
        assembly: { type: "string", description: "Optional assembly name" },
        returnEncoding: {
          type: "string",
          description: "byte[] encoding: hex (default) or base64",
        },
      },
      ["type", "method"]
    ),
    "exec"
  ),

  // ---- arrays, bytes, batches
  tool(
    "unity_read_array",
    "Page through an array or list field/property. Reference elements get handles.",
    schema(
      {
        ...instanceId,
        ...handle,
        member: { type: "string", description: "Field or property holding the array" },
        ...cursor,
        limit: { type: "number", description: "Items per page (default 100, max 250)" },
      },
      ["member"]
    )
  ),
  tool(
    "unity_decode_byte_array",
    "Decode a byte[] from a member or an inline hex/base64 string as text, json, hex or msgpack.",
    schema({
      ...instanceId,
      ...handle,
      member: { type: "string", description: "Field/property holding byte[]" },
      data: {
        type: "string",
        description: "Inline data (hex or base64) when no member is used",
      },
      encoding: {
        type: "string",
        description: "Inline data encoding: hex (default) or base64",
      },
      format: {
        type: "string",
        description: "auto (default), text, json, hex or msgpack",
      },
    })
  ),
  tool(
    "unity_batch",
    "Run up to 10 read-only tool calls in one turn. Returns results in order; each is {ok, result} or {ok, error}.",
    schema(
      {
        calls: {
          type: "array",
          description: "Read-only calls: {tool, arguments}",
          items: {
            type: "object",
            properties: {
              tool: { type: "string" },
              arguments: { type: "object" },
            },
            required: ["tool"],
          },
        },
      },
      ["calls"]
    )
  ),

  // ---- snapshots and undo
  tool(
    "unity_snapshot_object",
    "Remember an object's member values and return a snapshot id. Use unity_diff_snapshot after the game changes.",
    schema({
      ...instanceId,
      ...handle,
      type: { type: "string", description: "Type name for static fields" },
      member_contains: {
        type: "string",
        description: "Only members whose name contains this text",
      },
      member_limit: { type: "number", description: "Max members (default 200)" },
      include_properties: {
        type: "boolean",
        description: "Include property getters (default false)",
      },
    })
  ),
  tool(
    "unity_diff_snapshot",
    "Show only what changed since a snapshot. rebase makes the current state the new baseline; epsilon ignores float noise.",
    schema(
      {
        snapshot: { type: "string", description: "Snapshot id" },
        against: {
          type: "string",
          description: "Optional second snapshot to compare against",
        },
        rebase: { type: "boolean", description: "Update the baseline to now" },
        epsilon: {
          type: "number",
          description: "Ignore numeric changes smaller than this",
        },
      },
      ["snapshot"]
    )
  ),
  tool(
    "unity_undo_write",
    "Revert the most recent scalar writes made through set_field/set_property/set_static.",
    schema({
      steps: {
        type: "number",
        description: "How many writes to revert (default 1, max 32)",
      },
    }),
    "write"
  ),

  // ---- labels
  tool(
    "unity_list_labels",
    "List saved type/member labels and notes.",
    schema({
      type_contains: {
        type: "string",
        description: "Filter by type full name substring",
      },
      ...limit,
      ...cursor,
    })
  ),
  tool(
    "unity_set_label",
    "Save a readable name/note for a type or member. Targets persist per game install and appear in search results.",
    schema(
      {
        type: { type: "string", description: "Full type name" },
        member: {
          type: "string",
          description: "Member name; omit to label the type itself",
        },
        label: { type: "string", description: "Short readable name" },
        note: { type: "string", description: "Optional longer note" },
      },
      ["type", "label"]
    ),
    "write"
  ),
  tool(
    "unity_remove_label",
    "Remove a saved type/member label.",
    schema(
      {
        type: { type: "string", description: "Full type name" },
        member: { type: "string", description: "Member name; omit for the type" },
      },
      ["type"]
    ),
    "write"
  ),

  // ---- tracing
  tool(
    "unity_start_method_trace",
    "Hook a managed method and record real game calls (caller, args, result, thread, timing). Mono backend only.",
    schema(
      {
        type: { type: "string", description: "Declaring type full name" },
        method: { type: "string", description: "Method name" },
        assembly: { type: "string", description: "Optional assembly name" },
        param_count: {
          type: "number",
          description: "Disambiguate overloads by parameter count",
        },
      },
      ["type", "method"]
    ),
    "state"
  ),
  tool(
    "unity_stop_method_trace",
    "Detach a trace hook while keeping its captured records.",
    schema({ trace_id: { type: "string", description: "Trace id" } }, [
      "trace_id",
    ]),
    "state"
  ),
  tool(
    "unity_clear_method_trace",
    "Drop the captured records of a trace session.",
    schema({ trace_id: { type: "string", description: "Trace id" } }, [
      "trace_id",
    ]),
    "state"
  ),
  tool(
    "unity_list_method_traces",
    "List trace sessions with target, state and captured record count.",
    schema()
  ),
  tool(
    "unity_get_method_trace",
    "Read captured calls: caller, args, result, thread, elapsed ms and repeat count.",
    schema(
      {
        trace_id: { type: "string", description: "Trace id" },
        ...cursor,
        ...limit,
      },
      ["trace_id"]
    )
  ),
  tool(
    "unity_build_call_graph",
    "Aggregate captured calls into caller→target edges with counts and a sample of arguments.",
    schema(
      {
        trace_id: { type: "string", description: "Trace id" },
        ...limit,
        ...cursor,
      },
      ["trace_id"]
    )
  ),

  // ---- capture / logs / hooks
  tool(
    "unity_capture_screenshot",
    "Capture the current game frame and return it as a PNG image. Optional output_path keeps a copy on disk.",
    schema({
      output_path: {
        type: "string",
        description: "Optional full path to also write the PNG",
      },
      max_bytes: {
        type: "number",
        description: "Downscale until the PNG fits this many bytes (default 1000000)",
      },
    }),
    "state"
  ),
  tool(
    "unity_list_hooks",
    "List Harmony-patched methods (read-only inventory: method + patch owners).",
    schema({ ...limit, ...cursor })
  ),
  tool(
    "unity_activity_log",
    "Recent mutating/invoking/tracing calls handled by the bridge, with outcome and duration.",
    schema({ ...limit, ...cursor })
  ),
  tool(
    "unity_tail_log",
    "Read the tail of BepInEx LogOutput.log without locking the game (works while the game is down).",
    schema({
      path: {
        type: "string",
        description: "Game root dir or full log path (default auto-detects)",
      },
      lines: {
        type: "number",
        description: "Number of tail lines (default 100, max 2000)",
      },
      keyword: { type: "string", description: "Case-insensitive keyword filter" },
    })
  ),
  tool(
    "unity_execute_csharp",
    "Execute C# in the running game and return the result (honours returnEncoding for byte[]). Gated by allow_execute.",
    schema(
      {
        code: { type: "string", description: "C# code to execute" },
        returnEncoding: {
          type: "string",
          description: "byte[] return encoding: hex (default) or base64",
        },
      },
      ["code"]
    ),
    "exec"
  ),
  tool(
    "unity_dump_material",
    "Dump a Material in one call: shader, render queue, keywords and every non-default shader property (float/color/vector/texture). Textures report their name inline. Replaces GetFloat×N + GetColor×N + GetTexture×N.",
    schema({
      ...instanceId,
      ...handle,
      property_limit: {
        type: "number",
        description: "Max properties returned (default 200)",
      },
      include_defaults: {
        type: "boolean",
        description: "Also return properties left at their default (default false)",
      },
      include_hidden: {
        type: "boolean",
        description: "Include HideInInspector properties (default false)",
      },
    })
  ),
  tool(
    "unity_render_object",
    "Render one object offscreen to PNG with a throwaway camera. Works while the game is paused or covered by UI, unlike capture_screenshot. Handles inactive objects via a mesh proxy (see proxy_meshes). Does not draw UI meshes, skinned bind pose only, and MaterialPropertyBlock overrides are not copied.",
    schema({
      ...instanceId,
      ...handle,
      width: { type: "number", description: "Image width (default 512, max 2048)" },
      height: { type: "number", description: "Image height (default 512, max 2048)" },
    })
  ),
  tool(
    "unity_frame_camera",
    "Point a real game camera at a target object (front view, framed to its bounds). Moves the live camera, so the player view and subsequent screenshots change. Pass restore=true to put the camera back immediately; the previous transform is always returned. Defaults to the main camera and leaves it in the new position.",
    schema({
      target_handle: {
        type: "string",
        description: "Handle of the object to frame (e.g. a character root)",
      },
      target: { type: "number", description: "Instance id of the target (alternative to target_handle)" },
      camera_name: {
        type: "string",
        description: 'Camera to drive: a GameObject name, or "main" (default: main, else any)',
      },
      ...handle,
      distance_scale: {
        type: "number",
        description: "Multiplier on the framing distance (default 1.15; raise to pull back)",
      },
      offset_x: { type: "number", description: "Sideways offset in target radii" },
      offset_y: { type: "number", description: "Vertical offset in target radii" },
      look_at: { type: "boolean", description: "Aim at the target centre (default true)" },
      restore: {
        type: "boolean",
        description: "Restore the camera transform immediately after framing (default false)",
      },
    }, ["target_handle"]),
    "exec"
  ),
];

export const TOOL_NAMES = new Set(TOOL_DEFS.map((t) => t.name));

/** Tools allowed inside unity_batch: read-only and free of side effects. */
export const BATCH_ALLOWED = new Set(
  TOOL_DEFS.filter(
    (t) =>
      (t.annotations.readOnlyHint || t.name === "unity_invoke_method") &&
      t.name !== "unity_batch" &&
      t.name !== "unity_capture_screenshot" &&
      t.name !== "unity_render_object" &&
      t.name !== "unity_list_bridges" &&
      t.name !== "unity_select_bridge"
  ).map((t) => t.name)
);
