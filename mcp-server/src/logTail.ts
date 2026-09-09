import * as fs from "fs";
import * as path from "path";

export interface TailLogOptions {
  /** Explicit log file path, or game root dir (auto-appends BepInEx/LogOutput.log) */
  path?: string;
  lines?: number;
  keyword?: string;
  /** Max bytes to read from the file tail (default 256KB) */
  maxBytes?: number;
}

export interface TailLogResult {
  path: string;
  totalBytes: number;
  returnedLines: number;
  lines: string[];
}

const DEFAULT_GAME_ROOTS = [
  process.env.UNITY_GAME_ROOT || "",
];

/**
 * P2: tail BepInEx LogOutput.log without locking the game.
 * Node opens the file read-only (shared read on Windows), so the running
 * game keeps writing while we read the tail.
 */
export async function tailLog(opts: TailLogOptions = {}): Promise<TailLogResult> {
  const lines = Math.min(Math.max(opts.lines ?? 100, 1), 2000);
  const maxBytes = opts.maxBytes ?? 256 * 1024;

  let filePath = (opts.path || "").trim();
  if (!filePath) {
    for (const root of DEFAULT_GAME_ROOTS) {
      if (!root) continue;
      const candidate = path.join(root, "BepInEx", "LogOutput.log");
      if (fs.existsSync(candidate)) {
        filePath = candidate;
        break;
      }
    }
    if (!filePath) {
      throw new Error(
        "No log path given and UNITY_GAME_ROOT is not set. " +
        "Pass the game root dir or full BepInEx/LogOutput.log path via 'path', " +
        "or set the UNITY_GAME_ROOT environment variable."
      );
    }
  } else if (fs.existsSync(filePath) && fs.statSync(filePath).isDirectory()) {
    filePath = path.join(filePath, "BepInEx", "LogOutput.log");
  } else if (!filePath.toLowerCase().endsWith(".log")) {
    // Treat as game root dir.
    const candidate = path.join(filePath, "BepInEx", "LogOutput.log");
    if (fs.existsSync(candidate)) filePath = candidate;
  }

  let handle: fs.promises.FileHandle | null = null;
  try {
    handle = await fs.promises.open(filePath, "r");
    const stat = await handle.stat();
    const start = Math.max(0, stat.size - maxBytes);
    const length = stat.size - start;
    const buffer = Buffer.alloc(length);
    await handle.read(buffer, 0, length, start);
    const text = buffer.toString("utf8");
    let all = text.split(/\r?\n/);
    // Drop the first chunk line (likely partial) when we didn't read from 0.
    if (start > 0 && all.length > 0) all = all.slice(1);
    // Drop trailing empty line from final newline.
    if (all.length > 0 && all[all.length - 1] === "") all.pop();

    const keyword = (opts.keyword || "").trim().toLowerCase();
    const filtered = keyword
      ? all.filter((l) => l.toLowerCase().includes(keyword))
      : all;

    return {
      path: filePath,
      totalBytes: stat.size,
      returnedLines: Math.min(lines, filtered.length),
      lines: filtered.slice(-lines),
    };
  } catch (err: any) {
    if (err?.code === "ENOENT") {
      throw new Error(
        `Log file not found: ${filePath}. Pass the game root dir or full BepInEx/LogOutput.log path via 'path'.`
      );
    }
    throw err;
  } finally {
    if (handle) await handle.close().catch(() => {});
  }
}
