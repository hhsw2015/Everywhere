// SPEC §11 — compare a freshly captured output against expected.json.
// Two modes:
//   byte-equal   — JSON-stringified payloads must match exactly
//   schema-equal — same keys, types, array lengths within ±20%
//
// Stdin: `{ actual, expected }` JSON. Stdout: `{ ok, mode, drift }`.

import { readFileSync } from 'node:fs';

function readAllStdin() {
  return new Promise((resolve) => {
    const chunks = [];
    process.stdin.on('data', c => chunks.push(c));
    process.stdin.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
  });
}

function shapeOf(v) {
  if (v === null) return 'null';
  if (Array.isArray(v)) return 'array';
  return typeof v;
}

function schemaCompare(a, b, path = '$') {
  const sa = shapeOf(a), sb = shapeOf(b);
  if (sa !== sb) return [`${path}: type ${sa} vs ${sb}`];
  if (sa === 'array') {
    const la = a.length, lb = b.length;
    const tolerance = Math.max(1, Math.ceil(lb * 0.2));
    if (Math.abs(la - lb) > tolerance) return [`${path}: array length ${la} vs ${lb} (tolerance ${tolerance})`];
    const out = [];
    for (let i = 0; i < Math.min(la, lb); i++) {
      out.push(...schemaCompare(a[i], b[i], `${path}[${i}]`));
    }
    return out;
  }
  if (sa === 'object') {
    const ka = Object.keys(a).sort(), kb = Object.keys(b).sort();
    const missing = kb.filter(k => !ka.includes(k));
    const extra = ka.filter(k => !kb.includes(k));
    const out = [];
    if (missing.length) out.push(`${path}: missing keys [${missing.join(',')}]`);
    if (extra.length) out.push(`${path}: extra keys [${extra.join(',')}]`);
    for (const k of kb) if (ka.includes(k)) out.push(...schemaCompare(a[k], b[k], `${path}.${k}`));
    return out;
  }
  return [];
}

const input = JSON.parse(await readAllStdin());
const { actual, expected } = input;
const mode = expected.compare ?? 'byte-equal';

let drift = [];
if (mode === 'byte-equal') {
  const a = JSON.stringify(actual.data);
  const e = JSON.stringify(expected.data);
  if (a !== e) drift = ['data: byte-equal mismatch'];
} else {
  drift = schemaCompare(actual.data, expected.data, '$.data');
}

process.stdout.write(JSON.stringify({ ok: drift.length === 0, mode, drift }, null, 2));
