const { spawn } = require('child_process');
const path = require('path');

// Start MCP Server (paths resolved from this script's location — no hardcoded dirs)
const server = spawn('node', ['dist/server.js'], {
  cwd: path.join(__dirname, 'mcp-server'),
  stdio: ['pipe', 'pipe', 'pipe']
});

const requests = [
  { jsonrpc: '2.0', id: 1, method: 'tools/list', params: {} },
  {
    jsonrpc: '2.0',
    id: 2,
    method: 'tools/call',
    params: { name: 'unity_batch', arguments: { calls: [] } }
  }
];

let buffer = '';
let responses = 0;
let tools = [];

function send(index) {
  if (index >= requests.length) return;
  server.stdin.write(JSON.stringify(requests[index]) + '\n');
}

server.stdout.on('data', (data) => {
  buffer += data.toString();
  let newline;
  while ((newline = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    let response;
    try {
      response = JSON.parse(line);
    } catch {
      continue;
    }
    responses++;
    if (response.id === 1 && response.result?.tools) {
      tools = response.result.tools;
      checkCatalog();
      send(1);
    } else if (response.id === 2) {
      checkBatchGuard(response);
      finish(0);
    }
  }
});

function checkCatalog() {
  console.log(`Tools registered: ${tools.length}`);
  const required = [
    'unity_capabilities', 'unity_batch', 'unity_list_bridges', 'unity_select_bridge',
    'unity_snapshot_object', 'unity_diff_snapshot', 'unity_undo_write',
    'unity_start_instance_scan', 'unity_get_instance_scan', 'unity_search_values',
    'unity_read_array', 'unity_decode_byte_array', 'unity_set_label',
    'unity_remove_label', 'unity_list_labels', 'unity_start_method_trace',
    'unity_stop_method_trace', 'unity_clear_method_trace', 'unity_list_method_traces',
    'unity_get_method_trace', 'unity_build_call_graph', 'unity_capture_screenshot',
    'unity_activity_log', 'unity_execute_csharp', 'unity_tail_log',
    'unity_dump_material', 'unity_render_object'
  ];
  const names = tools.map((t) => t.name);
  const missing = required.filter((n) => !names.includes(n));
  const noAnnotations = tools.filter((t) => !t.annotations).map((t) => t.name);
  const writeFlaggedRead = tools
    .filter(
      (t) =>
        (t.name === 'unity_set_field' || t.name === 'unity_execute_csharp') &&
        t.annotations.readOnlyHint !== false
    )
    .map((t) => t.name);
  const readFlaggedWrite = tools
    .filter(
      (t) => t.name === 'unity_get_field' && t.annotations.readOnlyHint !== true
    )
    .map((t) => t.name);

  if (missing.length) console.log(`MISSING TOOLS: ${missing.join(', ')}`);
  if (noAnnotations.length)
    console.log(`MISSING ANNOTATIONS: ${noAnnotations.join(', ')}`);
  if (writeFlaggedRead.length)
    console.log(`WRITE FLAGGED READ-ONLY: ${writeFlaggedRead.join(', ')}`);
  if (readFlaggedWrite.length)
    console.log(`READ FLAGGED WRITE: ${readFlaggedWrite.join(', ')}`);

  if (missing.length || noAnnotations.length || writeFlaggedRead.length || readFlaggedWrite.length) {
    finish(1);
  }
}

function checkBatchGuard(response) {
  const text = response.result?.content?.[0]?.text ?? '';
  if (response.result?.isError && /at least one call/.test(text)) {
    console.log('batch validation: ok (empty batch rejected before bridge call)');
  } else {
    console.log('batch validation: FAILED -> ' + text.slice(0, 200));
    finish(1);
  }
}

let finished = false;
function finish(code) {
  if (finished) return;
  finished = true;
  server.kill();
  process.exit(code);
}

server.stderr.on('data', () => {
  // Startup notices are expected on stderr; stdout carries MCP only.
});

send(0);

setTimeout(() => {
  console.log(`Timeout waiting for responses (got ${responses})`);
  server.kill();
  process.exit(1);
}, 8000);
