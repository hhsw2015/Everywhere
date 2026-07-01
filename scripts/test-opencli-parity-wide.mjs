#!/usr/bin/env node
// Wide parity — walk cli-manifest.json and pick every PUBLIC adapter
// whose args have defaults (so we can call it with empty {} and both
// Node and MCP see the same input). Diff Node result vs MCP result;
// aggregate errors so shim gaps show up in one pass.

import { readFileSync, existsSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { register } from 'node:module';

const REPO = resolve(fileURLToPath(import.meta.url), '../..');
const POC = resolve(REPO, 'bench/opencli/poc');
register('./loader.mjs', pathToFileURL(POC + '/').href);
const host = await import(pathToFileURL(`${POC}/host.mjs`).href);
const { executePipeline } = await import('@jackwener/opencli/pipeline');

const manifest = JSON.parse(readFileSync(`${REPO}/3rd/opencli/cli-manifest.json`, 'utf8'));

// Only PUBLIC (no browser, no cookie/intercept/ui) — those need real
// browsers. Only adapters whose required args are all satisfiable
// from defaults / no-required. Cap per-site to avoid one site
// dominating.
const bySite = new Map();
for (const c of manifest) {
    if (c.strategy !== 'public') continue;
    if (c.browser === true) continue;
    const needsRequired = (c.args || []).some(a => a.required && a.default === undefined);
    if (needsRequired) continue;
    if (!bySite.has(c.site)) bySite.set(c.site, []);
    if (bySite.get(c.site).length < 2) bySite.get(c.site).push(c);
}
const ADAPTERS = [...bySite.values()].flat();
console.error(`[parity-wide] ${ADAPTERS.length} adapters across ${bySite.size} sites`);

const MCP = process.env.EVERYWHERE_MCP || 'http://127.0.0.1:7878/mcp';
const TIMEOUT_MS = 20000;

function withTimeout(promise, ms, label) {
    return Promise.race([
        promise,
        new Promise((_, r) => setTimeout(() => r(new Error(`TIMEOUT_${ms}ms:${label}`)), ms)),
    ]);
}

async function runNode(cmd) {
    const path = `${REPO}/3rd/opencli/clis/${cmd.site}/${cmd.name}.js`;
    if (!existsSync(path)) return { ok: false, err: 'missing' };
    try {
        await import(pathToFileURL(path).href);
    } catch (e) { return { ok: false, err: `load: ${e?.message ?? e}` }; }
    const def = host.getRegistry().get(`${cmd.site}/${cmd.name}`);
    if (!def) return { ok: false, err: 'not registered' };
    const args = {};
    for (const a of def.args || []) if (a.default !== undefined) args[a.name] = a.default;
    try {
        let result;
        if (typeof def.func === 'function') {
            const arity = def.func.length;
            const call =
                arity >= 2 ? def.func(null, args)
              : arity === 1 ? (def.browser ? def.func(null) : def.func(args))
              : def.func();
            result = await withTimeout(call, TIMEOUT_MS, 'node-func');
        } else if (def.pipeline) {
            result = await withTimeout(
                executePipeline(def.browser ? null : null, def.pipeline, { args }),
                TIMEOUT_MS, 'node-pipeline');
        } else {
            return { ok: false, err: 'no func/pipeline' };
        }
        return {
            ok: true,
            len: Array.isArray(result) ? result.length : (result == null ? 0 : 1),
            type: Array.isArray(result) ? 'array' : typeof result,
        };
    } catch (e) { return { ok: false, err: (e?.message ?? String(e)).slice(0, 200) }; }
}

async function runMcp(cmd) {
    try {
        const path = `${REPO}/3rd/opencli/clis/${cmd.site}/${cmd.name}.js`;
        let argsFromManifest = {};
        try {
            await import(pathToFileURL(path).href);
            const def = host.getRegistry().get(`${cmd.site}/${cmd.name}`);
            for (const a of def?.args || []) if (a.default !== undefined) argsFromManifest[a.name] = a.default;
        } catch {}
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
        const resp = await fetch(MCP, {
            method: 'POST',
            signal: controller.signal,
            headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream' },
            body: JSON.stringify({
                jsonrpc: '2.0', id: 1, method: 'tools/call',
                params: { name: 'opencli_run',
                          arguments: { site: cmd.site, name: cmd.name, arguments_json: JSON.stringify(argsFromManifest) } },
            }),
        });
        clearTimeout(timer);
        const txt = await resp.text();
        const line = txt.split('\n').find(l => l.startsWith('{') || l.startsWith('data: {'));
        if (!line) return { ok: false, err: `bad MCP: ${txt.slice(0, 200)}` };
        const j = JSON.parse(line.replace(/^data:\s*/, ''));
        if (j.error) return { ok: false, err: j.error.message };
        const env = JSON.parse(j.result.content[0].text);
        if (!env.ok) return { ok: false, err: (env.error ?? '').slice(0, 200), code: env.code };
        return {
            ok: true,
            len: Array.isArray(env.data) ? env.data.length : (env.data == null ? 0 : 1),
            type: Array.isArray(env.data) ? 'array' : typeof env.data,
        };
    } catch (e) { return { ok: false, err: (e?.message ?? String(e)).slice(0, 200) }; }
}

const results = [];
let idx = 0;
for (const cmd of ADAPTERS) {
    idx++;
    process.stderr.write(`  [${String(idx).padStart(3)}/${ADAPTERS.length}] ${cmd.site}/${cmd.name} ... `);
    const node = await runNode(cmd);
    const mcp = await runMcp(cmd);
    let verdict = '?';
    if (node.ok && mcp.ok) verdict = '✅';
    else if (node.ok && !mcp.ok) verdict = '❌ MCP-FAIL';
    else if (!node.ok && mcp.ok) verdict = '⚠️ NODE-FAIL';
    else verdict = '· BOTH-FAIL';
    process.stderr.write(`${verdict}\n`);
    results.push({ site: cmd.site, name: cmd.name, node, mcp });
}

console.error('\n[summary]');
const bothOK = results.filter(r => r.node.ok && r.mcp.ok);
const mcpFail = results.filter(r => r.node.ok && !r.mcp.ok);
const nodeFail = results.filter(r => !r.node.ok && r.mcp.ok);
const bothFail = results.filter(r => !r.node.ok && !r.mcp.ok);
console.error(`  both OK:  ${bothOK.length}/${results.length}`);
console.error(`  MCP-fail (shim gaps): ${mcpFail.length}`);
console.error(`  NODE-fail (test setup): ${nodeFail.length}`);
console.error(`  BOTH-FAIL: ${bothFail.length}`);

// Aggregate MCP-fail by error message so shim gaps cluster.
const gaps = new Map();
for (const r of mcpFail) {
    const key = (r.mcp.err || '').split(/[:(]/)[0].trim().slice(0, 80);
    if (!gaps.has(key)) gaps.set(key, []);
    gaps.get(key).push(`${r.site}/${r.name}`);
}
if (gaps.size) {
    console.error('\n[shim gaps by error kind]');
    for (const [k, sites] of [...gaps.entries()].sort((a, b) => b[1].length - a[1].length)) {
        console.error(`  ${String(sites.length).padStart(3)}× ${k}`);
        console.error(`         e.g. ${sites.slice(0, 3).join(', ')}`);
    }
}

writeFileSync(`${REPO}/bench/opencli/results/parity-wide.json`, JSON.stringify({
    total: results.length,
    bothOK: bothOK.length,
    mcpFail: mcpFail.length,
    nodeFail: nodeFail.length,
    bothFail: bothFail.length,
    results,
}, null, 2));
console.error(`\nfull report → bench/opencli/results/parity-wide.json`);
