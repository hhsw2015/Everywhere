#!/usr/bin/env node
// SPEC §11 — exhaustive load-coverage test. Drives every vendored
// adapter through the same shim layout the C# host uses, then groups
// failures by root cause so we can fix categories rather than
// one-offs. Run from the repo root.
//
// This is a Node-side mirror of OpenCliRuntime.EnsureAdapterLoadedAsync
// — it does NOT need a build, and crucially does not need to spawn V8.
// Differences from C# host: Node's import resolver is the *real* one
// here, so a green run on Node guarantees the C# loader has at minimum
// the right shim coverage.

import { readdirSync, readFileSync, existsSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { register } from 'node:module';

const REPO = resolve(fileURLToPath(import.meta.url), '../..');
const POC = join(REPO, 'bench/opencli/poc');
const CLIS = join(REPO, '3rd/opencli/clis');

// Reuse the bench PoC loader so '@jackwener/opencli/*' resolves to the
// host shim, plus add Node-builtin shorts the C# loader also handles.
register('./loader.mjs', pathToFileURL(POC + '/').href);

const stats = { ok: 0, byReason: new Map() };
function bumpReason(key, sample) {
  if (!stats.byReason.has(key)) stats.byReason.set(key, []);
  stats.byReason.get(key).push(sample);
}

function shouldSkip(rel) {
  // Pipelines have no func and SPEC §2.4 #1 keeps the runner out of
  // scope. We still try to load them — the failure mode is "registers
  // ok but no func", not a load error.
  if (rel.endsWith('.test.js')) return true;
  return false;
}

async function loadOne(rel) {
  const abs = join(CLIS, rel);
  try {
    // @ts-ignore
    await import(pathToFileURL(abs).href);
    stats.ok++;
  } catch (e) {
    const msg = (e && e.message) || String(e);
    let reason = 'other';
    if (/Cannot find module ['"]?node:/.test(msg) || /Cannot find package ['"]?node:/.test(msg)) reason = 'node-builtin-import';
    else if (/Cannot find module ['"]?@jackwener\/opencli\//.test(msg)) reason = 'opencli-subpath-missing';
    else if (/Cannot find module ['"]?\.\.?\//.test(msg)) reason = 'relative-missing';
    else if (/Cannot find package/.test(msg)) reason = 'bare-package-missing';
    else if (/Unexpected token/.test(msg) || /SyntaxError/.test(msg)) reason = 'syntax';
    else reason = 'other:' + msg.split('\n')[0].slice(0, 80);
    bumpReason(reason, { rel, msg: msg.split('\n')[0].slice(0, 200) });
  }
}

function walk(dir, acc = []) {
  for (const ent of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, ent.name);
    if (ent.isDirectory()) walk(p, acc);
    else if (p.endsWith('.js')) acc.push(p);
  }
  return acc;
}

const files = walk(CLIS).filter(p => !shouldSkip(p));
console.error(`[load-coverage] testing ${files.length} adapters...`);

// Sequential to keep registry/console output coherent.
for (const f of files) {
  if (shouldSkip(f.replace(CLIS + '/', ''))) continue;
  await loadOne(f.replace(CLIS + '/', ''));
}

console.error(`\n[load-coverage] OK=${stats.ok}/${files.length}  fail=${files.length - stats.ok}`);
const reasons = [...stats.byReason.entries()].sort((a, b) => b[1].length - a[1].length);
for (const [r, samples] of reasons) {
  console.error(`  ${samples.length.toString().padStart(5)}  ${r}`);
  console.error(`        e.g. ${samples[0].rel}`);
  console.error(`             ${samples[0].msg}`);
}

const out = join(REPO, 'bench/opencli/results/loadability.json');
writeFileSync(out, JSON.stringify({
  total: files.length,
  ok: stats.ok,
  fail: files.length - stats.ok,
  byReason: Object.fromEntries(reasons.map(([k, v]) => [k, { count: v.length, samples: v.slice(0, 5) }])),
}, null, 2));
console.error(`\n[load-coverage] full report → ${out.replace(REPO + '/', '')}`);
process.exit(0);
