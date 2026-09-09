#!/usr/bin/env node

/**
 * UnityExplorer MCP Integration Verification Script
 * 
 * Verifies that all components are properly set up.
 */

const fs = require('fs');
const path = require('path');

const BASE_DIR = __dirname;

console.log('=== UnityExplorer MCP Integration Verification ===\n');

const checks = [
  // MCP Bridge files
  { name: 'MCPBridge.cs', path: 'src\\MCPBridge\\MCPBridge.cs', required: true },
  { name: 'CSharpExecutor.cs (P0)', path: 'src\\MCPBridge\\CSharpExecutor.cs', required: true },
  { name: 'AssemblyInspector.cs (P1)', path: 'src\\MCPBridge\\AssemblyInspector.cs', required: true },
  { name: 'ValueSerializer.cs (P3)', path: 'src\\MCPBridge\\ValueSerializer.cs', required: true },
  { name: 'MCPBridgePlugin.cs', path: 'src\\MCPBridge\\MCPBridgePlugin.cs', required: true },
  { name: 'MCPBridgeMod.cs', path: 'src\\MCPBridge\\MCPBridgeMod.cs', required: true },
  { name: 'DTO.cs', path: 'src\\MCPBridge\\DTO.cs', required: true },
  { name: 'MCPBridge.csproj', path: 'src\\MCPBridge\\MCPBridge.csproj', required: true },
  { name: 'MCPBridge.IL2CPP.dll', path: 'dist\\il2cpp\\MCPBridge.IL2CPP.dll', required: true },
  { name: 'MCPBridge.Mono.dll', path: 'dist\\mono\\MCPBridge.Mono.dll', required: true },
  { name: 'MCPBridge.Mono.dll (BepInEx 5)', path: 'dist\\mono-bepinex5\\MCPBridge.Mono.dll', required: true },
  
  // MCP Server files
  { name: 'server.ts', path: 'mcp-server\\src\\server.ts', required: true },
  { name: 'logTail.ts (P2)', path: 'mcp-server\\src\\logTail.ts', required: true },
  { name: 'Compiled logTail.js (P2)', path: 'mcp-server\\dist\\logTail.js', required: true },
  { name: 'package.json', path: 'mcp-server\\package.json', required: true },
  { name: 'tsconfig.json', path: 'mcp-server\\tsconfig.json', required: true },
  { name: 'Compiled server.js', path: 'mcp-server\\dist\\server.js', required: true },
  
  // Configuration
  { name: 'opencode-example.json', path: 'opencode-example.json', required: true },
  { name: 'claude_desktop_config.example.json', path: 'claude_desktop_config.example.json', required: true },
  { name: 'cursor-mcp.example.json', path: 'cursor-mcp.example.json', required: true },
  { name: '.env', path: 'mcp-server\\.env', required: false },
  
  // Documentation
  { name: 'README.md', path: 'README.md', required: true },
  { name: 'README.zh-CN.md', path: 'README.zh-CN.md', required: true },
  { name: 'INSTALL.md', path: 'INSTALL.md', required: true },
  { name: 'SUMMARY.md', path: 'SUMMARY.md', required: true },
  
  // Build scripts
  { name: 'build.ps1', path: 'build.ps1', required: true },
  { name: 'test.js', path: 'test.js', required: true },
];

let allPassed = true;
let passedCount = 0;
let failedCount = 0;

console.log('Checking files:\n');

checks.forEach(check => {
  const fullPath = path.join(BASE_DIR, check.path);
  const exists = fs.existsSync(fullPath);
  
  if (exists) {
    const stats = fs.statSync(fullPath);
    console.log(`✓ ${check.name}`);
    console.log(`  Path: ${check.path}`);
    console.log(`  Size: ${stats.size} bytes`);
    passedCount++;
  } else {
    console.log(`✗ ${check.name} - MISSING`);
    console.log(`  Path: ${check.path}`);
    if (check.required) {
      allPassed = false;
      failedCount++;
    }
  }
  console.log('');
});

// Check node_modules
const nodeModulesPath = path.join(BASE_DIR, 'mcp-server', 'node_modules');
const nodeModulesExists = fs.existsSync(nodeModulesPath);
console.log(`✓ node_modules installed: ${nodeModulesExists}`);

console.log('\n=== Verification Summary ===');
console.log(`Passed: ${passedCount}`);
console.log(`Failed: ${failedCount}`);
console.log(`Overall: ${allPassed ? '✓ ALL CHECKS PASSED' : '✗ SOME CHECKS FAILED'}`);

if (allPassed) {
  console.log('\n=== Next Steps ===');
  console.log('1. Compile MCP Bridge: .\\build.ps1');
  console.log('2. Copy the matching dist/<backend> DLL + mcs.dll to the game BepInEx/plugins/');
  console.log('3. Start Unity game with UnityExplorer');
  console.log('4. Configure your MCP client from opencode-example.json (or Claude/Cursor examples)');
  console.log('5. Start the client and begin with unity_capabilities');
}

process.exit(allPassed ? 0 : 1);