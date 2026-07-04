#!/usr/bin/env node
// SPEC docs/specs/everywhere-connector.md §10.2 — bundle/manifest drift
// check. Runs after build-connector-bundle.mjs and asserts:
//   1. globalThis.__connectorProviders exposes at least one provider.
//   2. The manifest.services list equals the runtime provider set (no
//      manifest-only entries, no bundle-only entries).
//   3. Every provider in the manifest carries at least one action id.
//
// Fails with non-zero exit + human message on drift so CI catches
// upstream renames / typo bumps before shipping to LLM callers whose
// tool descriptions cache the manifest.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { runInThisContext } from 'node:vm';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(__dirname, '..');
const DIST = join(REPO, '3rd/open-connector/dist');
const BUNDLE = join(DIST, 'connector.bundle.js');
const MANIFEST = join(DIST, 'connector-manifest.json');

function bail(msg) {
  console.error(`[verify-connector-bundle] ${msg}`);
  process.exit(1);
}

const bundleSrc = (() => {
  try { return readFileSync(BUNDLE, 'utf8'); }
  catch (e) { bail(`bundle missing at ${BUNDLE}: ${e.message}`); }
})();
const manifest = (() => {
  try { return JSON.parse(readFileSync(MANIFEST, 'utf8')); }
  catch (e) { bail(`manifest missing/invalid at ${MANIFEST}: ${e.message}`); }
})();

// Attach a stub host bridge to the real globalThis before running the
// IIFE — the bundle's prepended fetch shim reads globalThis.__connectorHost
// at boot and throws if missing.
globalThis.__connectorHost = {
  fetchAsync: () => Promise.resolve({}),
  getCredential: () => null,
  warn: () => {},
  cryptoHash: () => '0'.repeat(64),
  cryptoHmac: () => '0'.repeat(64),
  cryptoRandomBytes: () => '',
  cryptoUuid: () => '00000000-0000-0000-0000-000000000000',
  transitMaxBytes: () => 0,
};
try {
  runInThisContext(bundleSrc);
} catch (e) {
  bail(`bundle failed to execute: ${e.message}`);
}

const runtime = globalThis.__connectorProviders || {};
const runtimeSet = new Set(Object.keys(runtime));
const manifestSet = new Set((manifest.services || []).map((s) => s.service));

if (runtimeSet.size === 0) bail('bundle produced no providers');

const runtimeOnly = [...runtimeSet].filter((s) => !manifestSet.has(s));
const manifestOnly = [...manifestSet].filter((s) => !runtimeSet.has(s));
if (runtimeOnly.length || manifestOnly.length) {
  bail(
    `bundle/manifest drift:\n` +
    (runtimeOnly.length ? `  bundle-only: ${runtimeOnly.join(', ')}\n` : '') +
    (manifestOnly.length ? `  manifest-only: ${manifestOnly.join(', ')}\n` : ''),
  );
}

const noActions = (manifest.services || []).filter((s) => !Array.isArray(s.actions) || s.actions.length === 0);
if (noActions.length) {
  bail(`services without actions: ${noActions.map((s) => s.service).join(', ')}`);
}

console.error(
  `[verify-connector-bundle] OK  providers=${runtimeSet.size}  actions=${
    (manifest.services || []).reduce((n, s) => n + (s.actions?.length || 0), 0)
  }`,
);
