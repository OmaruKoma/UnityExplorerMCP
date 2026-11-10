/**
 * Bridge target management: HTTP transport, discovery files written by the
 * in-game bridge, optional port scan fallback, and target selection when
 * several games run at once.
 */
import axios, { AxiosInstance } from "axios";
import { promises as fs } from "node:fs";
import os from "node:os";
import path from "node:path";

export interface BridgeInfo {
  pid?: number;
  port: number;
  url: string;
  backend?: string;
  loader?: string;
  unity_version?: string;
  session_id?: string;
  bridge_version?: string;
  started_at?: string;
  alive: boolean;
  current: boolean;
}

export class MultiBridgeError extends Error {
  readonly bridges: BridgeInfo[];
  constructor(bridges: BridgeInfo[]) {
    super(
      `Multiple game bridges are running (${bridges
        .map((b) => `pid ${b.pid ?? "?"} port ${b.port}`)
        .join(", ")}). Call unity_select_bridge with pid or port.`
    );
    this.bridges = bridges;
  }
}

const EXPLICIT_URL = process.env.UNITY_BRIDGE_URL?.trim() || "";
const EXPLICIT_PID = process.env.UNITY_BRIDGE_PID?.trim() || "";
const DEFAULT_URL = "http://127.0.0.1:12345";
const DEFAULT_PORT = 12345;
const PORT_SCAN_SPAN = 20;
const BRIDGE_TIMEOUT = positiveInt(process.env.UNITY_BRIDGE_TIMEOUT, 120_000);
const PING_TIMEOUT = Math.min(BRIDGE_TIMEOUT, 5_000);
const PROBE_TIMEOUT = Math.min(PING_TIMEOUT, 1_500);
const DISCOVERY_TTL_MS = 2_000;

export const DISCOVERY_DIR =
  process.env.UNITY_DISCOVERY_DIR ||
  path.join(
    process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local"),
    "UnityExplorerMCP",
    "bridges"
  );

function positiveInt(raw: string | undefined, fallback: number): number {
  const value = Number.parseInt(raw ?? "", 10);
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

function urlForPort(port: number): string {
  return `http://127.0.0.1:${port}`;
}

function bridgeFromRecord(record: any): BridgeInfo | null {
  const port = Number(record?.port ?? record?.Port);
  if (!Number.isInteger(port) || port <= 0 || port > 65535) return null;
  return {
    pid: Number(record?.pid ?? record?.Pid) || undefined,
    port,
    url: urlForPort(port),
    backend: record?.backend ?? record?.Backend,
    loader: record?.loader ?? record?.Loader,
    unity_version: record?.unity_version ?? record?.UnityVersion,
    session_id: record?.session_id ?? record?.SessionId,
    bridge_version: record?.bridge_version ?? record?.BridgeVersion,
    started_at: record?.started_at ?? record?.StartedAt,
    alive: false,
    current: false,
  };
}

export class BridgeManager {
  private clients = new Map<string, AxiosInstance>();
  private targets = new Map<string, BridgeInfo>();
  private selectedUrl: string | null = EXPLICIT_URL || null;
  private connected = new Map<string, boolean>();
  private discoveryAt = 0;
  private discovering: Promise<BridgeInfo[]> | null = null;

  get selected(): string | null {
    return this.selectedUrl;
  }

  private client(url: string): AxiosInstance {
    let client = this.clients.get(url);
    if (!client) {
      client = axios.create({
        baseURL: url,
        timeout: BRIDGE_TIMEOUT,
        headers: { "Content-Type": "application/json" },
        maxContentLength: Infinity,
        maxBodyLength: Infinity,
      });
      this.clients.set(url, client);
    }
    return client;
  }

  async pingUrl(
    url: string,
    timeout: number = PING_TIMEOUT
  ): Promise<{ alive: boolean; data?: any }> {
    try {
      const response = await this.client(url).post(
        "/",
        { method: "ping", params: {} },
        { timeout }
      );
      const data = response.data;
      const alive = Boolean(data?.Success ?? data?.success);
      this.connected.set(url, alive);
      return { alive, data: data?.Data ?? data?.data };
    } catch {
      this.connected.set(url, false);
      return { alive: false };
    }
  }

  /** Discover live bridges: discovery files first, port scan as fallback. */
  async discover(force = false): Promise<BridgeInfo[]> {
    const now = Date.now();
    if (!force && now - this.discoveryAt < DISCOVERY_TTL_MS) {
      return [...this.targets.values()];
    }
    if (this.discovering) return this.discovering;

    this.discovering = (async () => {
      const records = new Map<number, BridgeInfo>();

      if (EXPLICIT_URL) {
        let port = 0;
        try { port = Number(new URL(EXPLICIT_URL).port) || 0; } catch {}
        records.set(port || -1, {
          port,
          url: EXPLICIT_URL,
          alive: false,
          current: false,
        });
      }

      try {
        const files = await fs.readdir(DISCOVERY_DIR);
        for (const file of files) {
          if (!file.endsWith(".json")) continue;
          try {
            const raw = await fs.readFile(path.join(DISCOVERY_DIR, file), "utf8");
            const info = bridgeFromRecord(JSON.parse(raw));
            if (!info) continue;
            if (EXPLICIT_PID && String(info.pid) !== EXPLICIT_PID) continue;
            records.set(info.port, info);
          } catch {
            // A half-written or stale file is not a discovery failure.
          }
        }
      } catch {
        // No discovery directory yet: fall through to the port scan.
      }

      if (records.size === 0 && !EXPLICIT_URL) {
        for (let offset = 0; offset < PORT_SCAN_SPAN; offset++) {
          const port = DEFAULT_PORT + offset;
          records.set(port, {
            port,
            url: urlForPort(port),
            alive: false,
            current: false,
          });
        }
      }

      const checked = await Promise.all(
        [...records.values()].map(async (info) => {
          const ping = await this.pingUrl(info.url, PROBE_TIMEOUT);
          if (!ping.alive) return { ...info, alive: false };
          const data = ping.data ?? {};
          return {
            ...info,
            pid: info.pid ?? (Number(data.Pid ?? data.pid) || undefined),
            backend: info.backend ?? data.Backend ?? data.backend,
            loader: info.loader ?? data.Loader ?? data.loader,
            unity_version: info.unity_version ?? data.UnityVersion,
            session_id: info.session_id ?? data.SessionId,
            bridge_version: info.bridge_version ?? data.BridgeVersion,
            alive: true,
          };
        })
      );

      // Stale discovery files can outlive a crashed game: if nothing answered,
      // fall back to scanning the default port range.
      if (!EXPLICIT_URL && !checked.some((info) => info.alive)) {
        const ports: number[] = [];
        for (let offset = 0; offset < PORT_SCAN_SPAN; offset++) {
          ports.push(DEFAULT_PORT + offset);
        }
        const probes = await Promise.all(
          ports.map(async (port) => {
            const ping = await this.pingUrl(urlForPort(port), PROBE_TIMEOUT);
            return { port, ping };
          })
        );
        for (const { port, ping } of probes) {
          if (!ping.alive) continue;
          const data = ping.data ?? {};
          checked.push({
            port,
            url: urlForPort(port),
            pid: Number(data.Pid ?? data.pid) || undefined,
            backend: data.Backend ?? data.backend,
            loader: data.Loader ?? data.loader,
            unity_version: data.UnityVersion,
            session_id: data.SessionId,
            bridge_version: data.BridgeVersion,
            alive: true,
            current: false,
          });
        }
      }

      this.targets = new Map(
        checked.map((info) => [info.url, info])
      );
      this.discoveryAt = Date.now();
      return checked;
    })();

    try {
      return await this.discovering;
    } finally {
      this.discovering = null;
    }
  }

  async list(): Promise<BridgeInfo[]> {
    const bridges = await this.discover(true);
    if (this.selectedUrl && !bridges.some((b) => b.url === this.selectedUrl)) {
      // An explicit URL may point at a bridge without a discovery file.
      bridges.push({
        port: Number(new URL(this.selectedUrl).port) || 0,
        url: this.selectedUrl,
        alive: this.connected.get(this.selectedUrl) ?? false,
        current: true,
      });
    }
    return bridges.map((b) => ({ ...b, current: b.url === this.selectedUrl }));
  }

  async select(pid?: number, port?: number): Promise<BridgeInfo> {
    const bridges = await this.discover(true);
    const match = bridges.find(
      (b) =>
        b.alive &&
        (pid === undefined || b.pid === pid) &&
        (port === undefined || b.port === port)
    );
    if (!match) {
      throw new Error(
        `No live bridge matched ${pid !== undefined ? `pid ${pid}` : ""}${
          pid !== undefined && port !== undefined ? " / " : ""
        }${port !== undefined ? `port ${port}` : ""}.`
      );
    }
    this.selectedUrl = match.url;
    this.connected.set(match.url, true);
    return match;
  }

  /**
   * Pick a target before a bridge call. Explicit URL wins; a single live
   * bridge is selected automatically; several live bridges require a choice.
   */
  async ensureTarget(): Promise<string> {
    if (this.selectedUrl) return this.selectedUrl;

    const bridges = await this.discover();
    const alive = bridges.filter((b) => b.alive);
    if (alive.length === 1) {
      this.selectedUrl = alive[0].url;
      this.connected.set(alive[0].url, true);
      return alive[0].url;
    }
    if (alive.length > 1) throw new MultiBridgeError(alive);

    this.selectedUrl = DEFAULT_URL;
    return this.selectedUrl;
  }

  isConnected(): boolean {
    return this.selectedUrl
      ? this.connected.get(this.selectedUrl) === true
      : false;
  }

  async ping(): Promise<boolean> {
    const url = await this.ensureTarget();
    const result = await this.pingUrl(url);
    return result.alive;
  }

  /** Never throws: transport failures are folded into the bridge envelope. */
  async request(method: string, params: any = {}): Promise<any> {
    let url: string;
    try {
      url = await this.ensureTarget();
    } catch (error: any) {
      return {
        Success: false,
        Data: {
          code: "MULTIPLE_BRIDGES",
          bridges: (error as MultiBridgeError).bridges?.map((b) => ({
            pid: b.pid,
            port: b.port,
            backend: b.backend,
            session_id: b.session_id,
          })),
        },
        Error: error?.message ?? String(error),
      };
    }

    try {
      const response = await this.client(url).post("/", { method, params });
      const data = response.data;
      if (!data || typeof data !== "object") {
        return {
          Success: false,
          Data: null,
          Error: `bridge ${method} returned non-object response (${typeof data})`,
        };
      }
      this.connected.set(url, data.Success ?? data.success ?? true);
      return data;
    } catch (error: any) {
      this.connected.set(url, false);
      if (error?.response?.data) return error.response.data;
      const detail =
        error?.code === "ECONNABORTED"
          ? `timed out after ${BRIDGE_TIMEOUT}ms`
          : error?.message ?? String(error);
      return {
        Success: false,
        Data: null,
        Error: `bridge ${method} failed: ${detail} (url ${url})`,
      };
    }
  }
}
