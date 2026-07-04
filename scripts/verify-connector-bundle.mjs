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
//
// PRECONDITION on the bundle: providers must not invoke any host bridge
// method at module-init time. This checker's stub host is intentionally
// hostile (fetchAsync rejects, cryptoHash throws) so any accidental
// import-time host call fails loudly here rather than silently masking
// drift as "bundle failed to execute".

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { createContext, runInContext } from 'node:vm';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(__dirname, '..');
const DIST = join(REPO, '3rd/open-connector/dist');
const BUNDLE = join(DIST, 'connector.bundle.js');
const MANIFEST = join(DIST, 'connector-manifest.json');

// Custom Error type + top-level catch, so `bail` unambiguously
// terminates control flow. Static analyzers can now see that any code
// after a `bail(...)` call in a catch block is unreachable.
class VerifyFailure extends Error {}
function bail(msg) { throw new VerifyFailure(msg); }

try {
  const bundleSrc = (() => {
    try { return readFileSync(BUNDLE, 'utf8'); }
    catch (e) { bail(`bundle missing at ${BUNDLE}: ${e.message}`); }
  })();
  const manifest = (() => {
    try { return JSON.parse(readFileSync(MANIFEST, 'utf8')); }
    catch (e) { bail(`manifest missing/invalid at ${MANIFEST}: ${e.message}`); }
  })();

  if (!Array.isArray(manifest.services)) {
    bail('manifest.services missing or not an array');
  }

  // Isolated vm context — keeps our Node globals (fetch/crypto/etc.)
  // out of reach of the ~800-provider IIFE and prevents bundle globals
  // from bleeding back into this process. Hostile stubs surface any
  // accidental host-bridge call at import time as a loud failure.
  const sandbox = {
    console,
    // TextEncoder / TextDecoder / URL / atob / btoa are Node built-in
    // globals; expose them so the bundle's fetch/crypto shims can boot.
    TextEncoder, TextDecoder, URL, URLSearchParams, atob, btoa,
    setTimeout, clearTimeout, setInterval, clearInterval,
    Uint8Array, Buffer, Promise, JSON, Object, Array, String, Number, Boolean,
    Error, TypeError, RangeError, Symbol, Map, Set, Date, Math, RegExp,
    Reflect, Proxy,
    __connectorHost: {
      fetchAsync: () => Promise.reject(new Error('verify: host bridge fetchAsync must not be called at module init')),
      getCredential: () => { throw new Error('verify: host bridge getCredential must not be called at module init'); },
      warn: () => {},
      cryptoHash: () => { throw new Error('verify: host bridge cryptoHash must not be called at module init'); },
      cryptoHmac: () => { throw new Error('verify: host bridge cryptoHmac must not be called at module init'); },
      cryptoRandomBytes: () => { throw new Error('verify: host bridge cryptoRandomBytes must not be called at module init'); },
      cryptoUuid: () => { throw new Error('verify: host bridge cryptoUuid must not be called at module init'); },
      transitMaxBytes: () => 0,
      transitCreate: () => { throw new Error('verify: host bridge transitCreate must not be called at module init'); },
      transitRead: () => { throw new Error('verify: host bridge transitRead must not be called at module init'); },
      transitDelete: () => { throw new Error('verify: host bridge transitDelete must not be called at module init'); },
    },
  };
  sandbox.globalThis = sandbox;
  createContext(sandbox);

  try {
    runInContext(bundleSrc, sandbox);
  } catch (e) {
    bail(`bundle failed to execute: ${e.message}`);
  }

  const runtime = sandbox.__connectorProviders || {};
  const runtimeSet = new Set(Object.keys(runtime));
  const manifestSet = new Set(manifest.services.map((s) => s.service));

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

  const noActions = manifest.services.filter((s) => !Array.isArray(s.actions) || s.actions.length === 0);
  if (noActions.length) {
    bail(`services without actions: ${noActions.map((s) => s.service).join(', ')}`);
  }

  console.error(
    `[verify-connector-bundle] OK  providers=${runtimeSet.size}  actions=${
      manifest.services.reduce((n, s) => n + (s.actions?.length || 0), 0)
    }`,
  );
} catch (err) {
  if (err instanceof VerifyFailure) {
    console.error(`[verify-connector-bundle] ${err.message}`);
    process.exit(1);
  }
  throw err;
}
