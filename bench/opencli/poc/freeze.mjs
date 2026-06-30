// SPEC §11.4 — write expected.json for each bench fixture by running
// the upstream OpenCLI adapter through the host shim. One call per
// fixture; commit the result alongside UPSTREAM_SHA.
//
// Usage (Node 20+):
//   node --import "data:text/javascript,import {register} from 'node:module';import {pathToFileURL} from 'node:url';register('./loader.mjs', pathToFileURL(import.meta.url.replace(/freeze\\.mjs$/, '')))" \
//     bench/opencli/poc/freeze.mjs

import { readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import * as host from './host.mjs';

const POC_DIR = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(POC_DIR, '..', '..', '..');
const FIXTURE_DIR = resolve(REPO_ROOT, 'bench', 'opencli', 'fixtures');
const CLIS_DIR = resolve(REPO_ROOT, '3rd', 'opencli', 'clis');

function makeStubPage() {
  // The freeze run does NOT exercise the browser surface — DOM /
  // browser fixtures are frozen by hand on the agent host (SPEC §11.4
  // schema-equal). Calls here throw, which makes mis-categorised PUBLIC
  // fixtures fail loudly.
  return new Proxy({}, {
    get(_t, p) { return () => { throw new Error(`page.${String(p)} not available in PoC freeze`); }; }
  });
}

async function loadAdapter(site, name) {
  const adapterPath = join(CLIS_DIR, site, `${name}.js`);
  if (!existsSync(adapterPath)) throw new Error(`adapter missing: ${adapterPath}`);
  await import(pathToFileURL(adapterPath).href);
  const def = host.getRegistry().get(`${site}/${name}`);
  if (!def) throw new Error(`adapter ${site}/${name} did not register`);
  return def;
}

async function freezeOne(fixtureId) {
  const dir = join(FIXTURE_DIR, fixtureId);
  if (!existsSync(join(dir, 'args.json'))) return null;
  const meta = existsSync(join(dir, 'meta.json'))
    ? JSON.parse(readFileSync(join(dir, 'meta.json'), 'utf8'))
    : null;
  if (!meta) throw new Error(`${fixtureId}: meta.json missing`);

  const { site, name, strategy, browser, compare } = meta;
  if (browser || strategy !== 'public') {
    console.error(`[freeze] skip ${fixtureId}: browser/cookie fixture (freeze on agent host)`);
    return null;
  }

  const args = JSON.parse(readFileSync(join(dir, 'args.json'), 'utf8'));
  for (const a of meta.args_defaults ?? []) {
    if (args[a.name] === undefined && a.default !== undefined) args[a.name] = a.default;
  }

  const def = await loadAdapter(site, name);
  if (typeof def.func !== 'function')
    throw new Error(`${fixtureId}: adapter has no func (pipeline-only — out of scope)`);

  const t0 = Date.now();
  const data = await def.func(args, makeStubPage());
  const elapsed = Date.now() - t0;
  console.error(`[freeze] ${fixtureId} ${site}/${name} ms=${elapsed}`);

  const expected = {
    schema_version: '1',
    compare: compare ?? 'byte-equal',
    meta: { site, name, strategy, browser: !!browser },
    args,
    data,
  };
  writeFileSync(join(dir, 'expected.json'), JSON.stringify(expected, null, 2));
  return fixtureId;
}

async function main() {
  if (!existsSync(FIXTURE_DIR)) {
    console.error(`[freeze] no fixtures at ${FIXTURE_DIR}`);
    return;
  }
  const ids = readdirSync(FIXTURE_DIR).filter(e => !e.startsWith('.'));
  const out = [];
  for (const id of ids) {
    try {
      const r = await freezeOne(id);
      if (r) out.push(r);
    } catch (e) {
      console.error(`[freeze] ${id} FAILED: ${e.message}`);
    }
  }
  console.error(`[freeze] froze ${out.length}/${ids.length} fixtures`);
}

main();
