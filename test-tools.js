const { spawn } = require('child_process');

// Start MCP Server
const server = spawn('node', ['dist/server.js'], {
  cwd: 'D:\\codespace\\UnityExplorerMCP\\mcp-server',
  stdio: ['pipe', 'pipe', 'pipe']
});

// Send tools/list request
const request = JSON.stringify({
  jsonrpc: '2.0',
  id: 1,
  method: 'tools/list',
  params: {}
});

server.stdin.write(request + '\n');

// Collect response
let responseData = '';
server.stdout.on('data', (data) => {
  responseData += data.toString();
  
  // Check if we have a complete response
  try {
    const response = JSON.parse(responseData);
    if (response.result && response.result.tools) {
      console.log('MCP Tools registered successfully:');
      console.log('==================================');
      response.result.tools.forEach((tool, index) => {
        console.log(`${index + 1}. ${tool.name}`);
        console.log(`   ${tool.description}`);
        console.log('');
      });
      const expected = ['unity_ping', 'unity_scene_info', 'unity_find_gameobjects',
        'unity_get_gameobject', 'unity_get_components', 'unity_inspect', 'unity_get_field',
        'unity_set_field', 'unity_get_property', 'unity_set_property', 'unity_invoke_method',
        'unity_hierarchy', 'unity_execute_csharp', 'unity_list_assemblies', 'unity_inspect_type',
        'unity_invoke_static', 'unity_resolve_path', 'unity_tail_log', 'unity_capabilities'];
      const names = response.result.tools.map(t => t.name);
      const missing = expected.filter(n => !names.includes(n));
      if (missing.length > 0) {
        console.log('MISSING TOOLS: ' + missing.join(', '));
        server.kill();
        process.exit(1);
      }
      console.log(`All ${expected.length} tools present (18 existing + unity_capabilities).`);
      server.kill();
      process.exit(0);
    }
  } catch (e) {
    // Not complete JSON yet, wait for more data
  }
});

server.stderr.on('data', (data) => {
  // Ignore stderr for this test
});

// Timeout after 5 seconds
setTimeout(() => {
  console.log('Timeout waiting for response');
  server.kill();
  process.exit(1);
}, 5000);