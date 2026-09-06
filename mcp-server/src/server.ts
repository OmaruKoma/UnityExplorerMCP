#!/usr/bin/env node

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import axios, { AxiosInstance } from "axios";
import { z } from "zod";

// Configuration
const UNITY_BRIDGE_URL = process.env.UNITY_BRIDGE_URL || "http://127.0.0.1:12345";
const REQUEST_TIMEOUT = parseInt(process.env.REQUEST_TIMEOUT || "30000");

// Unity Bridge Client
class UnityBridgeClient {
  private client: AxiosInstance;
  private connected: boolean = false;

  constructor(baseUrl: string, timeout: number) {
    this.client = axios.create({
      baseURL: baseUrl,
      timeout: timeout,
      headers: {
        "Content-Type": "application/json",
      },
    });
  }

  async ping(): Promise<boolean> {
    try {
      const response = await this.client.post("/", {
        method: "ping",
        params: {},
      });
      this.connected = response.data.Success ?? response.data.success;
      return this.connected;
    } catch (error) {
      this.connected = false;
      return false;
    }
  }

  async request(method: string, params: any = {}): Promise<any> {
    try {
      const response = await this.client.post("/", {
        method,
        params,
      });
      return response.data;
    } catch (error: any) {
      if (error.response) {
        return error.response.data;
      }
      throw error;
    }
  }

  isConnected(): boolean {
    return this.connected;
  }
}

// MCP Server
class UnityExplorerMCPServer {
  private server: Server;
  private bridge: UnityBridgeClient;

  constructor() {
    this.server = new Server(
      {
        name: "unity-explorer-mcp-server",
        version: "1.0.0",
      },
      {
        capabilities: {
          tools: {},
        },
      }
    );

    this.bridge = new UnityBridgeClient(UNITY_BRIDGE_URL, REQUEST_TIMEOUT);
    this.setupHandlers();
  }

  private setupHandlers() {
    // List available tools
    this.server.setRequestHandler(ListToolsRequestSchema, async () => {
      return {
        tools: [
          {
            name: "unity_ping",
            description: "Ping the Unity Bridge to check connectivity",
            inputSchema: {
              type: "object",
              properties: {},
              required: [],
            },
          },
          {
            name: "unity_scene_info",
            description: "Get information about the current Unity scene",
            inputSchema: {
              type: "object",
              properties: {},
              required: [],
            },
          },
          {
            name: "unity_find_gameobjects",
            description: "Find GameObjects by name in the Unity scene",
            inputSchema: {
              type: "object",
              properties: {
                name: {
                  type: "string",
                  description: "Name to search for (partial match)",
                },
                include_inactive: {
                  type: "boolean",
                  description: "Include inactive GameObjects",
                  default: true,
                },
              },
              required: [],
            },
          },
          {
            name: "unity_get_gameobject",
            description: "Get detailed information about a specific GameObject",
            inputSchema: {
              type: "object",
              properties: {
                instance_id: {
                  type: "number",
                  description: "Instance ID of the GameObject",
                },
              },
              required: ["instance_id"],
            },
          },
          {
            name: "unity_get_components",
            description: "Get all components attached to a GameObject",
            inputSchema: {
              type: "object",
              properties: {
                instance_id: {
                  type: "number",
                  description: "Instance ID of the GameObject",
                },
              },
              required: ["instance_id"],
            },
          },
          {
            name: "unity_inspect",
            description: "Inspect an object and get its fields, properties, and methods",
            inputSchema: {
              type: "object",
              properties: {
                instance_id: {
                  type: "number",
                  description: "Instance ID of the object",
                },
                type_name: {
                  type: "string",
                  description: "Full type name (for static inspection)",
                },
              },
              required: [],
            },
          },
          {
            name: "unity_get_field",
            description: "Get the value of a field from an object",
            inputSchema: {
              type: "object",
              properties: {
                instance_id: {
                  type: "number",
                  description: "Instance ID of the object",
                },
                type: {
                  type: "string",
                  description: "Full type name of the object",
                },
                field: {
                  type: "string",
                  description: "Name of the field",
                },
              },
              required: ["field"],
            },
          },
          {
            name: "unity_set_field",
            description: "Set the value of a field on an object",
            inputSchema: {
              type: "object",
              properties: {
                instance_id: {
                  type: "number",
                  description: "Instance ID of the object",
                },
                type: {
                  type: "string",
                  description: "Full type name of the object",
                },
                field: {
                  type: "string",
                  description: "Name of the field",
                },
                value: {
                  description: "New value for the field",
                },
              },
              required: ["field", "value"],
            },
          },
          {
            name: "unity_get_property",
            description: "Get the value of a property from an object",
            inputSchema: {
              type: "object",
              properties: {
                instance_id: {
                  type: "number",
                  description: "Instance ID of the object",
                },
                type: {
                  type: "string",
                  description: "Full type name of the object",
                },
                property: {
                  type: "string",
                  description: "Name of the property",
                },
              },
              required: ["property"],
            },
          },
          {
            name: "unity_set_property",
            description: "Set the value of a property on an object",
            inputSchema: {
              type: "object",
              properties: {
                instance_id: {
                  type: "number",
                  description: "Instance ID of the object",
                },
                type: {
                  type: "string",
                  description: "Full type name of the object",
                },
                property: {
                  type: "string",
                  description: "Name of the property",
                },
                value: {
                  description: "New value for the property",
                },
              },
              required: ["property", "value"],
            },
          },
          {
            name: "unity_invoke_method",
            description: "Invoke a method on an object",
            inputSchema: {
              type: "object",
              properties: {
                instance_id: {
                  type: "number",
                  description: "Instance ID of the object",
                },
                type: {
                  type: "string",
                  description: "Full type name of the object",
                },
                method: {
                  type: "string",
                  description: "Name of the method",
                },
                args: {
                  type: "array",
                  description: "Method arguments",
                  items: {},
                },
              },
              required: ["method"],
            },
          },
          {
            name: "unity_hierarchy",
            description: "Get the hierarchy of GameObjects in the scene",
            inputSchema: {
              type: "object",
              properties: {
                max_depth: {
                  type: "number",
                  description: "Maximum depth to traverse",
                  default: 10,
                },
              },
              required: [],
            },
          },
          {
            name: "unity_execute_csharp",
            description: "Execute C# code in the Unity runtime",
            inputSchema: {
              type: "object",
              properties: {
                code: {
                  type: "string",
                  description: "C# code to execute",
                },
              },
              required: ["code"],
            },
          },
        ],
      };
    });

    // Handle tool calls
    this.server.setRequestHandler(CallToolRequestSchema, async (request) => {
      const { name, arguments: args } = request.params;

      // Ensure bridge is connected
      if (!this.bridge.isConnected()) {
        const connected = await this.bridge.ping();
        if (!connected) {
          return {
            content: [
              {
                type: "text",
                text: "Error: Cannot connect to Unity Bridge. Make sure UnityExplorer MCP Bridge is running in Unity.",
              },
            ],
            isError: true,
          };
        }
      }

      try {
        let result: any;

        switch (name) {
          case "unity_ping":
            result = await this.bridge.request("ping");
            break;

          case "unity_scene_info":
            result = await this.bridge.request("scene_info");
            break;

          case "unity_find_gameobjects":
            result = await this.bridge.request("find_gameobjects", {
              name: args?.name,
              include_inactive: args?.include_inactive ?? true,
            });
            break;

          case "unity_get_gameobject":
            result = await this.bridge.request("get_gameobject", {
              instance_id: args?.instance_id,
            });
            break;

          case "unity_get_components":
            result = await this.bridge.request("get_components", {
              instance_id: args?.instance_id,
            });
            break;

          case "unity_inspect":
            result = await this.bridge.request("inspect", {
              instance_id: args?.instance_id,
              type_name: args?.type_name,
            });
            break;

          case "unity_get_field":
            result = await this.bridge.request("get_field", {
              instance_id: args?.instance_id,
              type: args?.type,
              field: args?.field,
            });
            break;

          case "unity_set_field":
            result = await this.bridge.request("set_field", {
              instance_id: args?.instance_id,
              type: args?.type,
              field: args?.field,
              value: args?.value,
            });
            break;

          case "unity_get_property":
            result = await this.bridge.request("get_property", {
              instance_id: args?.instance_id,
              type: args?.type,
              property: args?.property,
            });
            break;

          case "unity_set_property":
            result = await this.bridge.request("set_property", {
              instance_id: args?.instance_id,
              type: args?.type,
              property: args?.property,
              value: args?.value,
            });
            break;

          case "unity_invoke_method":
            result = await this.bridge.request("invoke_method", {
              instance_id: args?.instance_id,
              type: args?.type,
              method: args?.method,
              args: args?.args,
            });
            break;

          case "unity_hierarchy":
            result = await this.bridge.request("hierarchy", {
              max_depth: args?.max_depth ?? 10,
            });
            break;

          case "unity_execute_csharp":
            result = await this.bridge.request("execute_csharp", {
              code: args?.code,
            });
            break;

          default:
            return {
              content: [
                {
                  type: "text",
                  text: `Error: Unknown tool: ${name}`,
                },
              ],
              isError: true,
            };
        }

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(result, null, 2),
            },
          ],
        };
      } catch (error: any) {
        return {
          content: [
            {
              type: "text",
              text: `Error: ${error.message || "Unknown error"}`,
            },
          ],
          isError: true,
        };
      }
    });
  }

  async run() {
    // Check initial connection
    const connected = await this.bridge.ping();
    if (connected) {
      console.error("Connected to Unity Bridge");
    } else {
      console.error("Warning: Cannot connect to Unity Bridge. Will retry when tools are called.");
    }

    const transport = new StdioServerTransport();
    await this.server.connect(transport);
    console.error("Unity Explorer MCP Server running on stdio");
  }
}

// Main
const server = new UnityExplorerMCPServer();
server.run().catch(console.error);