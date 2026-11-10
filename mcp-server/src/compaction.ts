/**
 * Result shaping for LLM clients:
 *  - record lists become {cols, rows} tables (keys once per page, not per row)
 *  - oversized payloads are trimmed from their largest array instead of
 *    cutting the JSON string in the middle
 */

export const RESULT_BUDGET = parsePositiveInt(
  process.env.UNITY_RESULT_BUDGET,
  120_000
);
export const TABLES_ENABLED = process.env.UNITY_RESULT_TABLES !== "0";

function parsePositiveInt(raw: string | undefined, fallback: number): number {
  const value = Number.parseInt(raw ?? "", 10);
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return false;
  }
  const proto = Object.getPrototypeOf(value);
  return proto === Object.prototype || proto === null;
}

/** Convert arrays of uniform records into a compact table when it is shorter. */
export function compactTables(value: unknown): unknown {
  if (Array.isArray(value)) {
    const compacted = value.map(compactTables);
    return tabulate(compacted) ?? compacted;
  }
  if (isPlainObject(value)) {
    for (const key of Object.keys(value)) {
      value[key] = compactTables(value[key]);
    }
  }
  return value;
}

function tabulate(rows: unknown[]): unknown | null {
  if (rows.length < 3) return null;
  const columns: string[] = [];
  for (const row of rows) {
    if (!isPlainObject(row) || Object.keys(row).length < 2) return null;
    for (const key of Object.keys(row)) {
      if (!columns.includes(key)) {
        columns.push(key);
        if (columns.length > 32) return null;
      }
    }
  }
  // A column that is null in every row carries nothing.
  const kept = columns.filter(
    (column) =>
      !rows.every((row) => {
        const cell = (row as Record<string, unknown>)[column];
        return cell === undefined || cell === null;
      })
  );
  if (kept.length < 2) return null;
  const cells = rows.map((row) =>
    kept.map((column) => {
      const cell = (row as Record<string, unknown>)[column];
      return cell === undefined ? null : cell;
    })
  );
  const table = { cols: kept, rows: cells };
  return JSON.stringify(table).length < JSON.stringify(rows).length
    ? table
    : null;
}

interface ArrayCandidate {
  parent: unknown;
  key: string | number | null;
  arr: unknown[];
  path: string;
  size: number;
}

function collectArrays(
  value: unknown,
  path: string,
  parent: unknown,
  key: string | number | null,
  out: ArrayCandidate[]
): void {
  if (Array.isArray(value)) {
    if (value.length > 1) {
      out.push({
        parent,
        key,
        arr: value,
        path,
        size: safeStringify(value).length,
      });
    }
    value.forEach((item, index) =>
      collectArrays(item, `${path}[${index}]`, value, index, out)
    );
    return;
  }
  if (isPlainObject(value)) {
    for (const childKey of Object.keys(value)) {
      collectArrays(
        value[childKey],
        `${path}.${childKey}`,
        value,
        childKey,
        out
      );
    }
  }
}

function safeStringify(value: unknown): string {
  const seen = new WeakSet<object>();
  try {
    return JSON.stringify(
      value,
      (_key, val) => {
        if (typeof val === "bigint") return val.toString();
        if (typeof val === "function")
          return `[function ${val.name || "anonymous"}]`;
        if (typeof val === "object" && val !== null) {
          if (seen.has(val)) return "[circular]";
          seen.add(val);
        }
        return val;
      },
      2
    );
  } catch (error: any) {
    return `[unserializable result: ${error?.message ?? error}]`;
  }
}

/**
 * Render a result as text within `budget` bytes. The largest arrays are
 * halved first, then the result is annotated with `truncated` and
 * `trimmed_arrays` so the caller knows to page for the rest.
 */
export function renderResult(
  value: unknown,
  budget: number = RESULT_BUDGET
): string {
  const prepared = TABLES_ENABLED ? compactTables(value) : value;
  let text = safeStringify(prepared);
  if (Buffer.byteLength(text, "utf8") <= budget) return text;

  const trimmed: string[] = [];
  for (let attempt = 0; attempt < 10; attempt++) {
    const candidates: ArrayCandidate[] = [];
    collectArrays(prepared, "$", null, null, candidates);
    candidates.sort((a, b) => b.size - a.size);
    const largest = candidates[0];
    if (!largest) break;

    const keep = Math.max(1, Math.floor(largest.arr.length / 2));
    if (largest.parent && largest.key !== null) {
      (largest.parent as any)[largest.key] = largest.arr.slice(0, keep);
    }
    trimmed.push(`${largest.path} (${keep}/${largest.arr.length})`);
    text = safeStringify(prepared);
    if (Buffer.byteLength(text, "utf8") <= budget) break;
  }

  // Annotate the root so the model knows the payload is partial.
  let annotated: unknown;
  if (isPlainObject(prepared)) {
    prepared.truncated = true;
    prepared.trimmed_arrays = trimmed;
    annotated = prepared;
  } else {
    annotated = {
      items: prepared,
      truncated: true,
      trimmed_arrays: trimmed,
    };
  }
  text = safeStringify(annotated);
  if (Buffer.byteLength(text, "utf8") > budget) {
    const kept = Buffer.from(text, "utf8")
      .subarray(0, budget)
      .toString("utf8");
    return `${kept}\n... [truncated: kept ${budget} of ${Buffer.byteLength(
      text,
      "utf8"
    )} bytes; narrow the query (limit/cursor) or raise UNITY_RESULT_BUDGET]`;
  }
  return text;
}
