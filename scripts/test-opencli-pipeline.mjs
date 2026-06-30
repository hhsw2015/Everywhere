#!/usr/bin/env node
// Drive a pipeline-only adapter through the vendored upstream pipeline
// runner — proves the C# host's synthesised-func path works the same
// way Node would. Picks a couple of well-known pipeline adapters and
// runs them with stubbed fetch responses.
import { readFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { register } from 'node:module';

const REPO = resolve(fileURLToPath(import.meta.url), '../..');
const POC = resolve(REPO, 'bench/opencli/poc');
const RUNTIME = resolve(REPO, '3rd/opencli/runtime');
const CLIS = resolve(REPO, '3rd/opencli/clis');

if (!existsSync(`${RUNTIME}/pipeline/index.js`)) {
  console.error('runtime tree missing — run scripts/sync-opencli.mjs first');
  process.exit(1);
}

register('./loader.mjs', pathToFileURL(POC + '/').href);

const host = await import(pathToFileURL(`${POC}/host.mjs`).href);
const { executePipeline } = await import('@jackwener/opencli/pipeline');

async function tryRun(siteName, adapterName, args) {
  const path = `${CLIS}/${siteName}/${adapterName}.js`;
  if (!existsSync(path)) {
    console.error(`  skip ${siteName}/${adapterName} (missing)`);
    return;
  }
  // Reset registry between runs so we don't see stale state.
  await import(pathToFileURL(path).href);
  const def = host.getRegistry().get(`${siteName}/${adapterName}`);
  if (!def) {
    console.error(`  ${siteName}/${adapterName}: did not register`);
    return;
  }
  if (def.func) {
    console.error(`  ${siteName}/${adapterName}: has direct func (not pipeline)`);
    return;
  }
  if (!def.pipeline) {
    console.error(`  ${siteName}/${adapterName}: no pipeline`);
    return;
  }
  console.error(`  ${siteName}/${adapterName}: pipeline (${def.pipeline.length} steps)`);
  try {
    const t0 = Date.now();
    const result = await executePipeline(null, def.pipeline, { args });
    const ms = Date.now() - t0;
    const summary = Array.isArray(result) ? `array(${result.length})`
      : (result && typeof result === 'object') ? `object(${Object.keys(result).length} keys)`
      : typeof result;
    console.error(`    OK in ${ms}ms → ${summary}`);
  } catch (e) {
    console.error(`    FAIL: ${e?.message || e}`);
  }
}

// Hackernews top is the canonical pipeline-only adapter.
console.error('[pipeline-test]');
await tryRun('hackernews', 'top', { limit: 3 });
await tryRun('hackernews', 'best', { limit: 3 });
await tryRun('hackernews', 'new', { limit: 3 });
console.error('[pipeline-test] done');
