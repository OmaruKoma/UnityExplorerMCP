#!/usr/bin/env node

/**
 * UnityExplorer MCP Bridge Test Script
 * 
 * This script tests the connection to the Unity Bridge and MCP Server.
 * Run this after starting Unity with the MCP Bridge and the MCP Server.
 */

const axios = require('axios');

const UNITY_BRIDGE_URL = process.env.UNITY_BRIDGE_URL || 'http://127.0.0.1:12345';
const MCP_SERVER_URL = 'http://localhost:3000';

async function testUnityBridge() {
  console.log('Testing Unity Bridge connection...');
  
  try {
    const response = await axios.post(UNITY_BRIDGE_URL, {
      method: 'ping',
      params: {}
    }, { timeout: 5000 });
    
    if (response.data.success) {
      console.log('✓ Unity Bridge is connected');
      console.log(`  Unity Version: ${response.data.data.UnityVersion}`);
      console.log(`  Bridge Version: ${response.data.data.BridgeVersion}`);
      return true;
    } else {
      console.log('✗ Unity Bridge returned error:', response.data.error);
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
    
    if (response.data.success) {
      console.log('✓ Scene info retrieved');
      console.log(`  Active Scene: ${response.data.data.ActiveScene.Name}`);
      console.log(`  Loaded Scenes: ${response.data.data.LoadedScenes.length}`);
      return true;
    } else {
      console.log('✗ Scene info error:', response.data.error);
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
    
    if (response.data.success) {
      console.log('✓ GameObjects found');
      console.log(`  Count: ${response.data.data.length}`);
      if (response.data.data.length > 0) {
        console.log(`  First: ${response.data.data[0].Name} (ID: ${response.data.data[0].InstanceId})`);
      }
      return true;
    } else {
      console.log('✗ Find GameObjects error:', response.data.error);
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
    
    if (response.data.success) {
      console.log('✓ Hierarchy retrieved');
      console.log(`  Root Objects: ${response.data.data.length}`);
      return true;
    } else {
      console.log('✗ Hierarchy error:', response.data.error);
      return false;
    }
  } catch (error) {
    console.log('✗ Cannot get hierarchy:', error.message);
    return false;
  }
}

async function testMCPServer() {
  console.log('\nTesting MCP Server...');
  
  try {
    const response = await axios.post(MCP_SERVER_URL, {
      jsonrpc: '2.0',
      id: 1,
      method: 'tools/list',
      params: {}
    }, { timeout: 5000 });
    
    if (response.data.result && response.data.result.tools) {
      console.log('✓ MCP Server is running');
      console.log(`  Available Tools: ${response.data.result.tools.length}`);
      return true;
    } else {
      console.log('✗ MCP Server returned invalid response');
      return false;
    }
  } catch (error) {
    console.log('✗ Cannot connect to MCP Server');
    console.log('  Make sure MCP Server is running (npm start)');
    return false;
  }
}

async function runTests() {
  console.log('=== UnityExplorer MCP Bridge Test ===\n');
  
  const results = {
    unityBridge: await testUnityBridge(),
    sceneInfo: await testSceneInfo(),
    findGameObjects: await testFindGameObjects(),
    hierarchy: await testHierarchy(),
    mcpServer: await testMCPServer()
  };
  
  console.log('\n=== Test Results ===');
  console.log(`Unity Bridge: ${results.unityBridge ? '✓ PASS' : '✗ FAIL'}`);
  console.log(`Scene Info: ${results.sceneInfo ? '✓ PASS' : '✗ FAIL'}`);
  console.log(`Find GameObjects: ${results.findGameObjects ? '✓ PASS' : '✗ FAIL'}`);
  console.log(`Hierarchy: ${results.hierarchy ? '✓ PASS' : '✗ FAIL'}`);
  console.log(`MCP Server: ${results.mcpServer ? '✓ PASS' : '✗ FAIL'}`);
  
  const allPassed = Object.values(results).every(r => r);
  console.log(`\nOverall: ${allPassed ? '✓ ALL TESTS PASSED' : '✗ SOME TESTS FAILED'}`);
  
  process.exit(allPassed ? 0 : 1);
}

runTests().catch(console.error);