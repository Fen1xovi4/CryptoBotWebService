/** Helpers for the Tester parameter optimizer: discovering sweepable numeric fields in a
 * built configJson and applying a combination back onto it (mirrors the backend's
 * ConfigJsonMutator path syntax: "takeProfitPercent", "levels[0].entrySpreadPercent"). */

export interface NumericPathEntry {
  path: string;
  value: number;
}

// Numeric enum discriminators — varying them over a grid is meaningless.
const EXCLUDED_KEYS = new Set(['mode', 'positionMode', 'skimMode']);

export function collectNumericPaths(configJson: string): NumericPathEntry[] {
  let root: unknown;
  try {
    root = JSON.parse(configJson);
  } catch {
    return [];
  }
  const out: NumericPathEntry[] = [];
  const walk = (node: unknown, prefix: string) => {
    if (node === null || typeof node !== 'object') return;
    if (Array.isArray(node)) {
      node.forEach((item, i) => walk(item, `${prefix}[${i}]`));
      return;
    }
    for (const [key, value] of Object.entries(node as Record<string, unknown>)) {
      const path = prefix ? `${prefix}.${key}` : key;
      if (typeof value === 'number') {
        if (!EXCLUDED_KEYS.has(key)) out.push({ path, value });
      } else if (value && typeof value === 'object') {
        walk(value, path);
      }
    }
  };
  walk(root, '');
  return out;
}

export function applyParamsToConfig(baseConfigJson: string, params: Record<string, number>): string {
  const root = JSON.parse(baseConfigJson) as Record<string, unknown>;
  for (const [path, value] of Object.entries(params)) {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let cur: any = root;
    const segs = path.split('.');
    for (let i = 0; i < segs.length; i++) {
      const m = /^([^[\]]+)(?:\[(\d+)\])?$/.exec(segs[i]);
      if (!m) throw new Error(`Некорректный путь параметра: ${path}`);
      const name = m[1];
      const idx = m[2] === undefined ? null : Number(m[2]);
      if (i === segs.length - 1) {
        if (idx === null) cur[name] = value;
        else cur[name][idx] = value;
      } else {
        cur = idx === null ? cur[name] : cur[name]?.[idx];
        if (cur === undefined || cur === null) throw new Error(`Путь не найден в конфиге: ${path}`);
      }
    }
  }
  return JSON.stringify(root);
}

/** Number of values a from/to/step range produces, or null when the inputs are invalid. */
export function countRangeValues(from: number, to: number, step: number): number | null {
  if (!Number.isFinite(from) || !Number.isFinite(to) || !(step > 0) || to < from) return null;
  return Math.floor((to - from) / step + 1e-9) + 1;
}

/** Prefill: ±2 steps around the current value (5 combos), integer-stepped for integer fields. */
export function suggestRange(value: number): { from: string; to: string; step: string } {
  let step: number;
  if (Number.isInteger(value)) {
    step = 1;
  } else {
    const raw = Math.abs(value) / 10 || 0.1;
    step = Number(Math.pow(10, Math.floor(Math.log10(raw))).toPrecision(1));
  }
  const decimals = step >= 1 ? 0 : Math.min(8, -Math.floor(Math.log10(step)));
  const lo = value < 0 ? value - 2 * step : Math.max(0, value - 2 * step);
  const hi = value + 2 * step;
  return { from: lo.toFixed(decimals), to: hi.toFixed(decimals), step: step.toFixed(decimals) };
}
