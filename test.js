#!/usr/bin/env node

/**
 * UnityExplorer MCP Bridge Test Script
 *
 * Tests the Unity Bridge HTTP API and the MCP Server tool registration.
 * Run after starting Unity with the MCP Bridge.
 *
 * Bridge envelope uses PascalCase (Success/Data/Error); helpers accept both.
 */

const axios = require('axios');
const { spawn } = require('child_process');
const path = require('path');

const UNITY_BRIDGE_URL = process.env.UNITY_BRIDGE_URL || 'http://127.0.0.1:12345';

const ok = (r) => r.data.Success ?? r.data.success;
const payload = (r) => r.data.Data ?? r.data.data;
const errMsg = (r) => r.data.Error ?? r.data.error;

async function testUnityBridge() {
  console.log('Testing Unity Bridge connection...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'ping',
      params: {}
    }, { timeout: 5000 });

    if (ok(response)) {
      const d = payload(response);
      console.log('✓ Unity Bridge is connected');
      console.log(`  Unity Version: ${d.UnityVersion}`);
      console.log(`  Bridge Version: ${d.BridgeVersion}`);
      console.log(`  SessionId: ${d.SessionId || d.sessionId || 'n/a'}`);
      return true;
    } else {
      console.log('✗ Unity Bridge returned error:', errMsg(response));
      return false;
    }
  } catch (error) {
    console.log('✗ Cannot connect to Unity Bridge');
    console.log('  Make sure Unity is running with MCP Bridge loaded');
    return false;
  }
}

async function testSceneInfo() {
  console.log('\nTesting scene info...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'scene_info',
      params: {}
    }, { timeout: 5000 });

    if (ok(response)) {
      const d = payload(response);
      console.log('✓ Scene info retrieved');
      console.log(`  Active Scene: ${d.ActiveScene.Name}`);
      console.log(`  Loaded Scenes: ${d.LoadedScenes.length}`);
      return true;
    } else {
      console.log('✗ Scene info error:', errMsg(response));
      return false;
    }
  } catch (error) {
    console.log('✗ Cannot get scene info:', error.message);
    return false;
  }
}

async function testFindGameObjects() {
  console.log('\nTesting find GameObjects...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'find_gameobjects',
      params: {
        name: '',
        include_inactive: true
      }
    }, { timeout: 5000 });

    if (ok(response)) {
      const d = payload(response);
      console.log('✓ GameObjects found');
      console.log(`  Count: ${d.length}`);
      if (d.length > 0) {
        console.log(`  First: ${d[0].Name} (ID: ${d[0].InstanceId})`);
      }
      return true;
    } else {
      console.log('✗ Find GameObjects error:', errMsg(response));
      return false;
    }
  } catch (error) {
    console.log('✗ Cannot find GameObjects:', error.message);
    return false;
  }
}

async function testHierarchy() {
  console.log('\nTesting hierarchy...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'hierarchy',
      params: {
        max_depth: 2
      }
    }, { timeout: 5000 });

    if (ok(response)) {
      const d = payload(response);
      console.log('✓ Hierarchy retrieved');
      console.log(`  Root Objects: ${d.length}`);
      return true;
    } else {
      console.log('✗ Hierarchy error:', errMsg(response));
      return false;
    }
  } catch (error) {
    console.log('✗ Cannot get hierarchy:', error.message);
    return false;
  }
}

async function testExecuteCSharp() {
  console.log('\nTesting execute_csharp (P0)...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'execute_csharp',
      params: { code: 'System.AppDomain.CurrentDomain.GetAssemblies().Length' }
    }, { timeout: 35000 });

    if (ok(response) && typeof payload(response).result === 'number') {
      console.log(`✓ execute_csharp returned assembly count: ${payload(response).result}`);
      return true;
    } else {
      console.log('✗ execute_csharp error:', errMsg(response) || JSON.stringify(payload(response)));
      return false;
    }
  } catch (error) {
    console.log('✗ execute_csharp failed:', error.message);
    return false;
  }
}

async function testExecuteCSharpReflect() {
  console.log('\nTesting execute_csharp reflection listing (P0)...');

  try {
    // NOTE: loop-based, not LINQ-over-Type[]: mcs resolves extension methods by
    // enumerating all referenced assemblies and chokes on stripped UnityEngine
    // interop stubs (CS0584 ImageConversion) — same quirk as UnityExplorer's
    // own C# Console, which carries an assembly blacklist for this.
    const code = 'var names = new System.Collections.Generic.List<string>(); foreach (var t in typeof(string).Assembly.GetTypes()) { names.Add(t.FullName); if (names.Count >= 10) break; } names;';
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'execute_csharp',
      params: { code }
    }, { timeout: 35000 });

    const d = payload(response);
    if (ok(response) && d.result && Array.isArray(d.result.items) && d.result.items.length === 10) {
      console.log(`✓ reflection listing returned 10 type names, first: ${d.result.items[0]}`);
      return true;
    } else {
      console.log('✗ reflection listing error:', errMsg(response) || JSON.stringify(payload(response)));
      return false;
    }
  } catch (error) {
    console.log('✗ reflection listing failed:', error.message);
    return false;
  }
}

async function testListAssemblies() {
  console.log('\nTesting list_assemblies (P1)...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'list_assemblies',
      params: { filter: 'System' }
    }, { timeout: 10000 });

    if (ok(response) && payload(response).count > 0) {
      console.log(`✓ Assemblies listed: ${payload(response).count} (filter=System)`);
      return true;
    } else {
      console.log('✗ list_assemblies error:', errMsg(response));
      return false;
    }
  } catch (error) {
    console.log('✗ list_assemblies failed:', error.message);
    return false;
  }
}

async function testInspectType() {
  console.log('\nTesting inspect_type (P1)...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'inspect_type',
      params: { type: 'System.Math' }
    }, { timeout: 10000 });

    const d = payload(response);
    const methods = ok(response) && d.methods;
    if (methods && methods.some(m => m.name === 'Max' && m.isStatic)) {
      console.log(`✓ inspect_type System.Math OK (${methods.length} methods, Max is static)`);
      return true;
    } else {
      console.log('✗ inspect_type error:', errMsg(response) || 'Max not found');
      return false;
    }
  } catch (error) {
    console.log('✗ inspect_type failed:', error.message);
    return false;
  }
}

async function testInvokeStatic() {
  console.log('\nTesting invoke_static System.Math.Max(3,5) (P1/P3)...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'invoke_static',
      params: { type: 'System.Math', method: 'Max', args: [3, 5] }
    }, { timeout: 10000 });

    if (ok(response) && String(payload(response).resultValue) === '5') {
      console.log('✓ invoke_static Max(3,5) = 5');
      return true;
    } else {
      console.log('✗ invoke_static error:', errMsg(response) || JSON.stringify(payload(response)));
      return false;
    }
  } catch (error) {
    console.log('✗ invoke_static failed:', error.message);
    return false;
  }
}

async function testInvokeStaticOutParam() {
  console.log('\nTesting invoke_static out-param (P3: int.TryParse)...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'invoke_static',
      params: { type: 'System.Int32', method: 'TryParse', args: ['42', null] }
    }, { timeout: 10000 });

    const d = payload(response);
    const outVal = ok(response) && d.outArgs && d.outArgs[0] && d.outArgs[0].value;
    if (ok(response) && d.resultValue === true && Number(outVal) === 42) {
      console.log('✓ out-param round-trip OK: TryParse("42") -> true, out=42');
      return true;
    } else {
      console.log('✗ out-param error:', errMsg(response) || JSON.stringify(d));
      return false;
    }
  } catch (error) {
    console.log('✗ out-param failed:', error.message);
    return false;
  }
}

async function testResolvePath() {
  console.log('\nTesting resolve_path (P1 handle stabilization)...');

  try {
    const find = await axios.post(UNITY_BRIDGE_URL, {
      method: 'find_gameobjects',
      params: { name: '', include_inactive: true }
    }, { timeout: 10000 });

    const list = payload(find);
    if (!ok(find) || !list || list.length === 0 || !list[0].Path) {
      console.log('✗ resolve_path skipped: no GameObject with Path found');
      return false;
    }
    const targetPath = list[0].Path;
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'resolve_path',
      params: { path: targetPath }
    }, { timeout: 10000 });

    if (ok(response) && payload(response).InstanceId === list[0].InstanceId) {
      console.log(`✓ resolve_path round-trip OK: ${targetPath}`);
      return true;
    } else {
      console.log('✗ resolve_path error:', errMsg(response) || 'ID mismatch');
      return false;
    }
  } catch (error) {
    console.log('✗ resolve_path failed:', error.message);
    return false;
  }
}

async function testCapabilities() {
  console.log('\nTesting capabilities (P0-1)...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'capabilities',
      params: {}
    }, { timeout: 10000 });

    const d = payload(response);
    const backend = process.env.UNITY_BACKEND || 'IL2CPP';
    if (ok(response) && d.backend && d.session_id && Array.isArray(d.enabled_tools)
        && d.limits && d.marshalling && d.marshalling.out === true) {
      console.log(`✓ capabilities: backend=${d.backend} (expect ${backend}), tools=${d.enabled_tools.length}, limits=${JSON.stringify(d.limits)}`);
      if (d.backend !== backend) {
        console.log(`  NOTE: backend mismatch (bridge says ${d.backend}, test expects ${backend})`);
      }
      return true;
    } else {
      console.log('✗ capabilities error:', errMsg(response) || 'missing fields');
      return false;
    }
  } catch (error) {
    console.log('✗ capabilities failed:', error.message);
    return false;
  }
}

async function testSessionHandle() {
  console.log('\nTesting session-aware handle (P0-2)...');

  try {
    // Fresh handle round-trip via resolve_path.
    const find = await axios.post(UNITY_BRIDGE_URL, {
      method: 'find_gameobjects',
      params: { name: '', include_inactive: true, limit: 5 }
    }, { timeout: 10000 });

    const page = payload(find);
    const items = page.items || page;
    if (!ok(find) || !items || items.length === 0 || !items[0].Handle) {
      console.log('✗ session handle skipped: no Handle in find response');
      return false;
    }
    const handle = items[0].Handle;
    if (handle.indexOf('#') < 0 || handle.indexOf(':') < 0) {
      console.log('✗ bad handle format:', handle);
      return false;
    }
    // Use the handle on a read.
    const get = await axios.post(UNITY_BRIDGE_URL, {
      method: 'get_gameobject',
      params: { handle }
    }, { timeout: 10000 });
    if (!ok(get) || payload(get).InstanceId !== items[0].InstanceId) {
      console.log('✗ handle read error:', errMsg(get) || 'ID mismatch');
      return false;
    }
    // Stale handle must fail fast with STALE_HANDLE on a write.
    const stale = await axios.post(UNITY_BRIDGE_URL, {
      method: 'invoke_method',
      params: { handle: 'NoScene#999:12345', method: 'ToString', args: [] }
    }, { timeout: 10000 });
    const sdata = payload(stale) || {};
    if (!ok(stale) && sdata.code === 'STALE_HANDLE' && sdata.current_session && sdata.suggest === 'resolve_path') {
      console.log(`✓ handle OK (${handle}); stale write fast-fails with STALE_HANDLE`);
      return true;
    } else {
      console.log('✗ stale handle error:', errMsg(stale) || JSON.stringify(sdata));
      return false;
    }
  } catch (error) {
    console.log('✗ session handle failed:', error.message);
    return false;
  }
}

async function testPagination() {
  console.log('\nTesting pagination (P0-3)...');

  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'find_gameobjects',
      params: { name: '', include_inactive: true, limit: 3, cursor: 0 }
    }, { timeout: 15000 });

    const d = payload(response);
    if (!ok(response) || !d.items || d.items.length > 3 || typeof d.total !== 'number') {
      console.log('✗ pagination error:', errMsg(response) || 'bad envelope');
      return false;
    }
    let cursorOk = true;
    if (d.next_cursor != null) {
      const page2 = await axios.post(UNITY_BRIDGE_URL, {
        method: 'find_gameobjects',
        params: { name: '', include_inactive: true, limit: 3, cursor: d.next_cursor }
      }, { timeout: 15000 });
      const d2 = payload(page2);
      cursorOk = ok(page2) && d2.items && d2.items.length > 0
        && d2.items[0].InstanceId !== d.items[0].InstanceId;
    }
    const hier = await axios.post(UNITY_BRIDGE_URL, {
      method: 'hierarchy',
      params: { max_depth: 2, limit: 5 }
    }, { timeout: 15000 });
    const h = payload(hier);
    if (!ok(hier) || !h.items || !('truncated' in h)) {
      console.log('✗ hierarchy paging error:', errMsg(hier) || 'bad envelope');
      return false;
    }
    if (cursorOk) {
      console.log(`✓ pagination OK (find total=${d.total}, hierarchy truncated=${h.truncated})`);
      return true;
    } else {
      console.log('✗ cursor page mismatch');
      return false;
    }
  } catch (error) {
    console.log('✗ pagination failed:', error.message);
    return false;
  }
}

async function testMCPServer() {
  console.log('\nTesting MCP Server tool registration (spawn stdio)...');

  return new Promise((resolve) => {
    const serverPath = path.join(__dirname, 'mcp-server', 'dist', 'server.js');
    const server = spawn('node', [serverPath], { stdio: ['pipe', 'pipe', 'pipe'] });
    let out = '';
    const timer = setTimeout(() => {
      console.log('✗ Timeout waiting for MCP Server response');
      server.kill();
      resolve(false);
    }, 8000);

    server.stdout.on('data', (data) => {
      out += data.toString();
      try {
        const response = JSON.parse(out);
        if (response.result && response.result.tools) {
          clearTimeout(timer);
          const names = response.result.tools.map(t => t.name);
          const expected = 19;
          console.log(`✓ MCP Server registered ${names.length} tools`);
          server.kill();
          resolve(names.length === expected);
        }
      } catch (e) { /* partial JSON, wait for more */ }
    });

    server.stderr.on('data', () => { /* ignore startup logs */ });
    server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/list', params: {} }) + '\n');
  });
}

async function runTests() {
  console.log('=== UnityExplorer MCP Bridge Test ===\n');

  const backend = process.env.UNITY_BACKEND || 'IL2CPP';
  console.log(`Backend under test: ${backend}\n`);

  const results = {
    unityBridge: await testUnityBridge(),
    sceneInfo: await testSceneInfo(),
    findGameObjects: await testFindGameObjects(),
    hierarchy: await testHierarchy(),
    executeCSharp: await testExecuteCSharp(),
    executeReflect: await testExecuteCSharpReflect(),
    listAssemblies: await testListAssemblies(),
    inspectType: await testInspectType(),
    invokeStatic: await testInvokeStatic(),
    invokeOutParam: await testInvokeStaticOutParam(),
    resolvePath: await testResolvePath(),
    capabilities: await testCapabilities(),
    sessionHandle: await testSessionHandle(),
    pagination: await testPagination(),
    mcpServer: await testMCPServer()
  };

  console.log('\n=== Test Results ===');
  for (const [k, v] of Object.entries(results)) {
    console.log(`${v ? '✓ PASS' : '✗ FAIL'}  ${k}`);
  }

  const passed = Object.values(results).filter(r => r === true).length;
  const failed = Object.values(results).filter(r => r === false).length;
  console.log(`\nBackend: ${backend} | Passed: ${passed} | Failed: ${failed} | Skipped: 0`);

  const allPassed = failed === 0;
  console.log(`Overall: ${allPassed ? '✓ ALL TESTS PASSED' : '✗ SOME TESTS FAILED'}`);

  process.exit(allPassed ? 0 : 1);
}

runTests().catch(console.error);
