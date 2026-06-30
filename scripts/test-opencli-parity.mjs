#!/usr/bin/env node
// Parity check: same N adapters in Node end-to-end AND through MCP
// HTTP. Any adapter that's OK in Node but FAIL via MCP is a C#-side
// shim gap. Use this as the closed-loop test for shim completeness:
//
//   node scripts/test-opencli-parity.mjs
//
// Requires Everywhere running on http://127.0.0.1:7878/mcp.

import { readFileSync, existsSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { register } from 'node:module';

const REPO = resolve(fileURLToPath(import.meta.url), '../..');
const POC = resolve(REPO, 'bench/opencli/poc');
register('./loader.mjs', pathToFileURL(POC + '/').href);
const host = await import(pathToFileURL(`${POC}/host.mjs`).href);
const { executePipeline } = await import('@jackwener/opencli/pipeline');

// 30+ adapters across ~20 sites — covers RSS, JSON API, fetch+parse,
// pipeline (HN), pipeline+fetch_each, math sequences, package
// registries, code search. Browser adapters skipped (they need
// OpenDia connected and are tested separately).
const ADAPTERS = [
  // RSS / atom
  ['36kr',       'news',         { limit: 2 }],
  // Math / scientific
  ['oeis',       'search',       { query: 'fibonacci', limit: 2 }],
  ['oeis',       'sequence',     { id: 'A000045' }],
  // Package registries
  ['crates',     'search',       { query: 'serde', limit: 2 }],
  ['pypi',       'package',      { name: 'requests' }],
  // Hacker News (pipeline-heavy)
  ['hackernews', 'read',         { id: '1' }],
  ['hackernews', 'top',          { limit: 2 }],
  ['hackernews', 'best',         { limit: 2 }],
  ['hackernews', 'new',          { limit: 2 }],
  ['hackernews', 'ask',          { limit: 2 }],
  ['hackernews', 'show',         { limit: 2 }],
  ['hackernews', 'jobs',         { limit: 2 }],
  ['hackernews', 'user',         { user: 'pg' }],
  ['hackernews', 'search',       { query: 'rust', limit: 2 }],
  // Code-host trending
  ['gitee',      'trending',     { limit: 2 }],
  ['gitee',      'user',         { name: 'oschina' }],
  // Misc PUBLIC fetch
  ['uiverse',    'code',         { id: '1' }],
  ['aibase',     'news',         { limit: 2 }],
  // Less-mainstream PUBLIC adapters (sanity coverage)
  ['ctrip',      'hotel-suggest', { keyword: 'beijing' }],
  ['sinablog',   'search',       { keyword: 'tech', limit: 2 }],
];

const MCP = process.env.EVERYWHERE_MCP || 'http://127.0.0.1:7878/mcp';

async function runNode(site, name, args) {
  const path = `${REPO}/3rd/opencli/clis/${site}/${name}.js`;
  if (!existsSync(path)) return { ok: false, err: 'missing' };
  await import(pathToFileURL(path).href);
  const def = host.getRegistry().get(`${site}/${name}`);
  if (!def) return { ok: false, err: 'not registered' };
  try {
    let result;
    if (typeof def.func === 'function') {
      // Upstream signature: (page, args) or (args).
      result = def.func.length >= 2 ? await def.func(null, args) : await def.func(args);
    } else if (def.pipeline) {
      result = await executePipeline(def.browser ? null : null, def.pipeline, { args });
    }
    return { ok: true, len: Array.isArray(result) ? result.length : 1, type: Array.isArray(result) ? 'array' : typeof result };
  } catch (e) { return { ok: false, err: e?.message ?? String(e) }; }
}

async function runMcp(site, name, args) {
  try {
    const resp = await fetch(MCP, {
      method: 'POST',
      headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream' },
      body: JSON.stringify({
        jsonrpc: '2.0', id: 1, method: 'tools/call',
        params: { name: 'opencli_run', arguments: { site, name, arguments_json: JSON.stringify(args) } },
      }),
    });
    const txt = await resp.text();
    const line = txt.split('\n').find(l => l.startsWith('{') || l.startsWith('data: {'));
    if (!line) return { ok: false, err: `bad MCP response: ${txt.slice(0, 200)}` };
    const j = JSON.parse(line.replace(/^data:\s*/, ''));
    if (j.error) return { ok: false, err: j.error.message };
    const env = JSON.parse(j.result.content[0].text);
    if (!env.ok) return { ok: false, err: env.error, code: env.code };
    return { ok: true, len: Array.isArray(env.data) ? env.data.length : 1, type: Array.isArray(env.data) ? 'array' : typeof env.data };
  } catch (e) { return { ok: false, err: e?.message ?? String(e) }; }
}

console.error(`[parity] vs ${MCP}\n`);
const results = [];
for (const [site, name, args] of ADAPTERS) {
  process.stderr.write(`  ${site}/${name} ... `);
  const node = await runNode(site, name, args);
  const mcp = await runMcp(site, name, args);
  const verdict =
    node.ok && mcp.ok ? '✅ BOTH'
    : node.ok && !mcp.ok ? '❌ MCP-FAIL'
    : !node.ok && mcp.ok ? '⚠️ NODE-FAIL'
    : '? BOTH-FAIL';
  console.error(`${verdict}  node=${JSON.stringify(node)}  mcp=${JSON.stringify(mcp)}`);
  results.push({ site, name, node, mcp });
}

console.error('\n[summary]');
const both = results.filter(r => r.node.ok && r.mcp.ok).length;
const mcpFail = results.filter(r => r.node.ok && !r.mcp.ok);
const nodeFail = results.filter(r => !r.node.ok && r.mcp.ok).length;
console.error(`  both OK: ${both}/${results.length}`);
console.error(`  MCP-fail (shim gaps): ${mcpFail.length}/${results.length}`);
console.error(`  NODE-fail (test setup): ${nodeFail}/${results.length}`);

if (mcpFail.length) {
  console.error('\n[shim gaps]');
  for (const r of mcpFail) {
    console.error(`  ${r.site}/${r.name}: ${r.mcp.code || ''} ${r.mcp.err}`);
  }
}

writeFileSync(`${REPO}/bench/opencli/results/parity.json`, JSON.stringify(results, null, 2));
console.error(`\nfull report → bench/opencli/results/parity.json`);
