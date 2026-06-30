#!/usr/bin/env node
// SPEC §11 — runnability matrix. Loads N representative adapters
// through the same shim layout the C# host uses (Node ESM loader hook
// rewrites @jackwener/opencli/* → vendored runtime + Node built-ins).
// For each adapter we try to actually invoke `def.func(args, stubPage)`
// where stubPage emulates Phase1StubPage / OpenDiaPageBridge.
//
// Three outcomes per adapter:
//   ok       — invoked, returned a value
//   nodata   — invoked, returned null/empty (often live API + filter)
//   error    — threw; the error message is the classification key
//
// Aggregates errors by message so we see "23 adapters fail because of
// X, 8 adapters fail because of Y" rather than chasing them one at a
// time. This is the spec-shaped completeness check that turns
// "compile/install/test/fail/repeat" into "see all gaps once".

import { readdirSync, readFileSync, existsSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { register } from 'node:module';

const REPO = resolve(fileURLToPath(import.meta.url), '../..');
const POC = join(REPO, 'bench/opencli/poc');
const CLIS = join(REPO, '3rd/opencli/clis');
const RUNTIME = join(REPO, '3rd/opencli/runtime');

if (!existsSync(`${RUNTIME}/pipeline/index.js`)) {
  console.error('runtime tree missing — run scripts/sync-opencli.mjs first');
  process.exit(1);
}
register('./loader.mjs', pathToFileURL(POC + '/').href);

const host = await import(pathToFileURL(`${POC}/host.mjs`).href);
const { executePipeline } = await import('@jackwener/opencli/pipeline');

// PUBLIC adapters with `func` — should all run via global fetch.
// Mix of static (RSS / known-good) and live (third-party APIs).
// Picked across many sites to expose shim gaps quickly.
const PUBLIC_FUNC_ADAPTERS = [
  ['36kr',         'news',        { limit: 2 }],
  ['pypi',         'downloads',   { name: 'requests' }],
  ['oeis',         'search',      { query: 'fibonacci', limit: 2 }],
  ['oeis',         'sequence',    { id: 'A000045' }],
  ['ctrip',        'search',      { keyword: 'beijing', limit: 2 }],
  ['gitee',        'trending',    { limit: 2 }],
  ['crates',       'search',      { query: 'serde', limit: 2 }],
  ['hackernews',   'read',        { id: '1' }],
  ['gitee',        'user',        { name: 'oschina' }],
  ['uiverse',      'preview',     {} ],
];

// Pipeline-only adapters. Run via vendored executePipeline.
const PIPELINE_ADAPTERS = [
  ['hackernews', 'top',  { limit: 2 }],
  ['hackernews', 'best', { limit: 2 }],
  ['hackernews', 'new',  { limit: 2 }],
  ['hackernews', 'ask',  { limit: 2 }],
  ['hackernews', 'show', { limit: 2 }],
  ['hackernews', 'jobs', { limit: 2 }],
  ['hackernews', 'user', { user: 'pg' }],
  ['hackernews', 'search', { query: 'rust', limit: 2 }],
];

// Browser-strategy adapters. Need a stub page that mimics what the
// OpenDiaPageBridge would do — Node side we just have to accept the
// calls without crashing. If the adapter only goto + evaluate, we can
// fetch the URL ourselves in stub and run a tiny DOM extraction.
const BROWSER_ADAPTERS = [
  ['36kr',     'hot',     { limit: 2 }],
  ['bilibili', 'hot',     { limit: 2 }],
  ['weibo',    'hot',     { limit: 2 }],
];

// JS stub for IPage that mirrors OpenDiaPageBridge's surface — every
// method exists so adapters don't get `page.X is not a function`.
// Adapters that try to do anything substantive throw a structured error
// that we classify; many will short-circuit cleanly.
function makeStubPage() {
  const stub = () => Promise.reject(new Error('PAGE_STUB:not implemented in Node test harness'));
  return new Proxy({}, {
    get(_t, prop) {
      // page.evaluate(js) — adapters often check feature counts; return 0
      if (prop === 'evaluate' || prop === 'evaluateWithArgs') {
        return async (_js) => 0;
      }
      // page.wait / waitForTimeout
      if (prop === 'wait' || prop === 'waitForTimeout') {
        return async (_arg) => undefined;
      }
      if (prop === 'goto') return async (_url) => undefined;
      if (prop === 'getCurrentUrl') return async () => '';
      if (prop === 'snapshot' || prop === 'tabs') return async () => ({});
      if (prop === 'getCookies') return async () => [];
      if (prop === 'closeWindow') return async () => undefined;
      return stub;
    },
  });
}

// ---- runner --------------------------------------------------------

const stats = {
  publicFunc:    { total: 0, ok: 0, nodata: 0, errors: new Map() },
  pipeline:      { total: 0, ok: 0, nodata: 0, errors: new Map() },
  browser:       { total: 0, ok: 0, nodata: 0, errors: new Map() },
};

function bumpErr(bucket, msg, where) {
  msg = String(msg || '').split('\n')[0].slice(0, 200);
  if (!bucket.has(msg)) bucket.set(msg, []);
  bucket.get(msg).push(where);
}

async function loadAdapter(site, name) {
  const path = `${CLIS}/${site}/${name}.js`;
  if (!existsSync(path)) return null;
  try {
    await import(pathToFileURL(path).href);
    return host.getRegistry().get(`${site}/${name}`);
  } catch (e) {
    return { __loadError: e };
  }
}

async function runOne(category, [site, name, args]) {
  const bucket = stats[category];
  bucket.total++;
  const where = `${site}/${name}`;
  const def = await loadAdapter(site, name);
  if (!def) { bumpErr(bucket.errors, 'adapter file missing', where); return; }
  if (def.__loadError) { bumpErr(bucket.errors, `load: ${def.__loadError.message}`, where); return; }

  const page = makeStubPage();
  try {
    let result;
    if (typeof def.func === 'function') {
      // upstream func signature is (args) for PUBLIC and (args, page)
      // for browser. PUBLIC adapter doesn't receive a page argument.
      const fnArity = def.func.length;
      // Upstream signature: (page, args) or (args).
      result = await Promise.race([
        fnArity >= 2 ? def.func(page, args) : def.func(args),
        new Promise((_, r) => setTimeout(() => r(new Error('TIMEOUT_15s')), 15000)),
      ]);
    } else if (def.pipeline) {
      // PUBLIC pipeline (no browser) → pass null page.
      const usePage = def.browser ? page : null;
      result = await Promise.race([
        executePipeline(usePage, def.pipeline, { args }),
        new Promise((_, r) => setTimeout(() => r(new Error('TIMEOUT_15s')), 15000)),
      ]);
    } else {
      bumpErr(bucket.errors, 'no func and no pipeline', where);
      return;
    }
    if (result == null || (Array.isArray(result) && result.length === 0)) {
      bucket.nodata++;
      bumpErr(bucket.errors, '(nodata: returned null or [])', where);
    } else {
      bucket.ok++;
    }
  } catch (e) {
    bumpErr(bucket.errors, e?.message ?? String(e), where);
  }
}

async function run(category, adapters) {
  console.error(`\n[${category}]`);
  for (const a of adapters) {
    const where = `${a[0]}/${a[1]}`;
    process.stderr.write(`  ${where} ... `);
    const before = stats[category].ok;
    await runOne(category, a);
    const after = stats[category].ok;
    process.stderr.write(after > before ? 'OK\n' : '\n');
  }
}

await run('publicFunc', PUBLIC_FUNC_ADAPTERS);
await run('pipeline',   PIPELINE_ADAPTERS);
await run('browser',    BROWSER_ADAPTERS);

console.error('\n[summary]');
for (const [cat, b] of Object.entries(stats)) {
  console.error(`  ${cat}: ok=${b.ok}/${b.total}  nodata=${b.nodata}`);
  const errs = [...b.errors.entries()].sort((a, b) => b[1].length - a[1].length);
  for (const [msg, sites] of errs.slice(0, 6)) {
    console.error(`    ${sites.length.toString().padStart(2)}×  ${msg}`);
    console.error(`         e.g. ${sites[0]}`);
  }
}

const out = join(REPO, 'bench/opencli/results/runnability.json');
writeFileSync(out, JSON.stringify(stats, (_, v) =>
  v instanceof Map ? Object.fromEntries([...v.entries()].map(([k, list]) => [k, list])) : v, 2));
console.error(`\nfull report → ${out.replace(REPO + '/', '')}`);
