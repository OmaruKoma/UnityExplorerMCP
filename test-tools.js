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