#!/usr/bin/env node
// SPEC docs/specs/everywhere-connector.md §5 — build IIFE bundle from
// vendored open-connector TypeScript sources.
//
// CLI:
//   node scripts/build-connector-bundle.mjs \
//       --src 3rd/open-connector --out 3rd/open-connector/dist
//
// Emits:
//   dist/connector.bundle.js       IIFE, publishes globalThis.__connectorProviders
//   dist/connector-manifest.json   flat provider/action metadata for cheap listing
//
// Providers to include are hard-coded in PROVIDERS below (Phase 1
// allowlist keeps the bundle small and audit-friendly). Bump the array
// as Phase 2/3 providers get vetted.
//
// Buffer shim: core/cast.ts imports `node:buffer` for Buffer.from(str, "base64")
// only. We alias node:buffer to a 5-line polyfill so the V8 isolate never
// sees a Node import.
//
// Response wrapper: the IIFE prepends a wrapFetchResponse() bridge that
// adapts OpenCLI-shared HostShim.fetchAsync's C# FetchResponse (sync fields)
// into a web-standard Response (async .text/.json, .headers.get).
// See §6.

import { mkdirSync, existsSync, writeFileSync, rmSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { execSync } from 'node:child_process';

const __dirname = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);

function arg(name, fallback) {
  const i = process.argv.indexOf(`--${name}`);
  return i >= 0 ? process.argv[i + 1] : fallback;
}

const REPO = resolve(__dirname, '..');
const SRC = resolve(arg('src', join(REPO, '3rd/open-connector')));
const OUT = resolve(arg('out', join(SRC, 'dist')));

// Phase 9 — allowlist is now derived by scanning the vendored provider
// tree. A provider ships when:
//   1. src/providers/<name>/definition.ts AND executors.ts exist.
//   2. It only imports node:* modules our shims cover (buffer, crypto).
//   3. Its directory name is a valid identifier segment (letters, digits,
//      underscores, but must not start with a digit — esbuild's entry
//      generator emits `import ... as def0` which is safe, but the
//      manifest keys become object keys; we accept digit-starts by using
//      bracket notation everywhere).
// Explicit skip-list handles cases we've reviewed and want to hold back.
const SKIP_PROVIDERS = new Set([
  // Providers importing external npm packages we don't bundle:
  //   flomo, jin10 — @modelcontextprotocol/sdk
  //   linux_do    — rss-parser
  // Their executors run in a Node host, not V8. Skip until we ship a
  // browser-compatible port.
  'flomo',
  'jin10',
  'linux_do',
]);

const ALLOWED_NODE_SUBSPECIFIERS = new Set(['node:buffer', 'node:crypto']);

function scanProviders(srcDir) {
  const providersRoot = join(srcDir, 'src/providers');
  if (!existsSync(providersRoot)) return [];
  const out = [];
  for (const entry of readdirSync(providersRoot)) {
    if (SKIP_PROVIDERS.has(entry)) continue;
    const dir = join(providersRoot, entry);
    let s;
    try { s = statSync(dir); } catch { continue; }
    if (!s.isDirectory()) continue;
    if (!existsSync(join(dir, 'definition.ts'))) continue;
    if (!existsSync(join(dir, 'executors.ts'))) continue;
    if (hasUnsupportedNodeImport(dir)) continue;
    out.push(entry);
  }
  return out.sort();
}

function hasUnsupportedNodeImport(dir) {
  for (const rel of readdirSync(dir)) {
    if (!rel.endsWith('.ts')) continue;
    const src = readFileSync(join(dir, rel), 'utf8');
    // node:*
    const re = /^\s*import\s.*from\s+["'](node:[^"']+)["']/gm;
    let m;
    while ((m = re.exec(src))) {
      if (!ALLOWED_NODE_SUBSPECIFIERS.has(m[1])) return true;
    }
    // Any bare-package import (not relative, not node:*) — we don't
    // bundle npm packages, so treat as unsupported. Same as SKIP_PROVIDERS
    // list, but catches new upstream additions automatically.
    const rePkg = /^\s*import\s.*from\s+["']([^./"'][^"']*)["']/gm;
    while ((m = rePkg.exec(src))) {
      if (!m[1].startsWith('node:')) return true;
    }
  }
  return false;
}

const PROVIDERS = scanProviders(SRC);
if (PROVIDERS.length === 0) {
  throw new Error(`[build-connector-bundle] no providers found under ${SRC}/src/providers`);
}

async function loadEsbuild() {
  // Prefer a project-local esbuild; else use `npx --yes esbuild@0.24.0`,
  // which caches after the first fetch. Explicit version pin avoids
  // silent behavior drift across dev machines.
  try {
    return require('esbuild');
  } catch {
    const ESBUILD_VERSION = '0.24.0';
    // Resolve the on-disk path where npx cached esbuild. We use the
    // .mjs API entry so we can `import()` it below without spawning
    // per-build subprocesses.
    let modulePath;
    try {
      modulePath = execSync(
        `npx --yes -p esbuild@${ESBUILD_VERSION} node -p "require.resolve('esbuild')"`,
        { encoding: 'utf8' },
      ).trim();
    } catch (err) {
      throw new Error(
        `[build-connector-bundle] esbuild not installed and npx fallback failed: ${err.message}\n` +
          `  Fix: npm install -g esbuild@${ESBUILD_VERSION} or add esbuild to a repo-root package.json.`,
      );
    }
    console.error(`[build-connector-bundle] using npx-cached esbuild@${ESBUILD_VERSION} from ${modulePath}`);
    // eslint-disable-next-line no-return-await
    return await import(modulePath);
  }
}

// Buffer shim as a virtual module esbuild resolves via `alias`.
// Covers the Buffer.from(str, encoding) uses currently in the vendored
// tree: base64 (core/cast.ts), utf8 (feishu, chargebee), hex (bark).
const BUFFER_SHIM_SOURCE = `
function toBytes(input, encoding) {
  encoding = (encoding || 'utf8').toLowerCase();
  if (input instanceof Uint8Array) return input;
  if (typeof input !== 'string') {
    // Node's Buffer.from(number/array/arraybuffer) — pass through.
    if (Array.isArray(input)) return new Uint8Array(input);
    if (input && input.buffer) return new Uint8Array(input.buffer);
    throw new TypeError("BufferShim: unsupported input " + typeof input);
  }
  if (encoding === 'utf8' || encoding === 'utf-8') {
    return new TextEncoder().encode(input);
  }
  if (encoding === 'base64' || encoding === 'base64url') {
    let s = input;
    if (encoding === 'base64url') {
      s = s.replace(/-/g, '+').replace(/_/g, '/');
      while (s.length % 4) s += '=';
    }
    const bin = atob(s);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }
  if (encoding === 'hex') {
    if (input.length % 2 !== 0) throw new Error("BufferShim: hex string must have even length");
    const out = new Uint8Array(input.length / 2);
    for (let i = 0; i < out.length; i++) out[i] = parseInt(input.substr(i * 2, 2), 16);
    return out;
  }
  if (encoding === 'ascii' || encoding === 'latin1' || encoding === 'binary') {
    const out = new Uint8Array(input.length);
    for (let i = 0; i < input.length; i++) out[i] = input.charCodeAt(i) & 0xff;
    return out;
  }
  throw new Error("BufferShim: unsupported encoding " + encoding);
}

function bytesToString(bytes, encoding) {
  encoding = (encoding || 'utf8').toLowerCase();
  if (encoding === 'utf8' || encoding === 'utf-8') return new TextDecoder().decode(bytes);
  if (encoding === 'base64') {
    let bin = '';
    for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return btoa(bin);
  }
  if (encoding === 'base64url') {
    let bin = '';
    for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return btoa(bin).replace(/\\+/g, '-').replace(/\\//g, '_').replace(/=+$/, '');
  }
  if (encoding === 'hex') {
    let s = '';
    for (let i = 0; i < bytes.length; i++) s += bytes[i].toString(16).padStart(2, '0');
    return s;
  }
  if (encoding === 'ascii' || encoding === 'latin1' || encoding === 'binary') {
    let s = '';
    for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
    return s;
  }
  throw new Error("BufferShim: unsupported encoding " + encoding);
}

function makeBuffer(bytes) {
  // Duck-typed Buffer: Uint8Array + toString(encoding). Enough for the
  // callers we've audited; extend when a provider needs .slice() etc.
  const b = new Uint8Array(bytes);
  b.toString = function(encoding) { return bytesToString(this, encoding); };
  return b;
}

export const Buffer = {
  from(input, encoding) { return makeBuffer(toBytes(input, encoding)); },
  concat(list) {
    let total = 0;
    for (const b of list) total += b.length;
    const out = new Uint8Array(total);
    let off = 0;
    for (const b of list) { out.set(b, off); off += b.length; }
    return makeBuffer(out);
  },
  isBuffer(x) { return x && x instanceof Uint8Array; },
  alloc(n) { return makeBuffer(new Uint8Array(n)); },
};
export default { Buffer };
`;

// node:crypto shim. Only the sync stateful bits providers actually reach
// for: createHash / createHmac / randomBytes / randomUUID. Uses the
// host bridge (__connectorHost.crypto*) so we get identical semantics
// to what OpenCLI already ships (mirrors HostShim.crypto* helpers).
const CRYPTO_SHIM_SOURCE = `
function bytesFrom(input, encoding) {
  if (input instanceof Uint8Array) return input;
  if (typeof input !== 'string') {
    if (Array.isArray(input)) return new Uint8Array(input);
    throw new TypeError("cryptoShim: unsupported input " + typeof input);
  }
  encoding = (encoding || 'utf8').toLowerCase();
  if (encoding === 'utf8' || encoding === 'utf-8') return new TextEncoder().encode(input);
  if (encoding === 'hex') {
    const out = new Uint8Array(input.length / 2);
    for (let i = 0; i < out.length; i++) out[i] = parseInt(input.substr(i * 2, 2), 16);
    return out;
  }
  if (encoding === 'base64') {
    const bin = atob(input);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }
  return new TextEncoder().encode(input);
}

function toBase64(bytes) {
  let bin = '';
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin);
}

function toHex(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i++) s += bytes[i].toString(16).padStart(2, '0');
  return s;
}

function encodeDigest(bytes, encoding) {
  encoding = (encoding || 'buffer').toLowerCase();
  if (encoding === 'hex') return toHex(bytes);
  if (encoding === 'base64') return toBase64(bytes);
  if (encoding === 'base64url') return toBase64(bytes).replace(/\\+/g, '-').replace(/\\//g, '_').replace(/=+$/, '');
  // Return the raw bytes when no encoding requested.
  return bytes;
}

class Hash {
  constructor(algorithm) {
    this._alg = algorithm;
    this._chunks = [];
  }
  update(data, encoding) {
    this._chunks.push(bytesFrom(data, encoding));
    return this;
  }
  digest(encoding) {
    const total = this._chunks.reduce((n, b) => n + b.length, 0);
    const buf = new Uint8Array(total);
    let off = 0;
    for (const c of this._chunks) { buf.set(c, off); off += c.length; }
    // Delegate to host bridge for the actual hash (ClearScript exposes
    // the same helpers OpenCLI HostShim publishes at __connectorHost).
    const host = globalThis.__connectorHost;
    if (!host || typeof host.cryptoHash !== 'function') {
      throw new Error("cryptoShim: __connectorHost.cryptoHash missing (Phase 5 host bridge not wired)");
    }
    // Host expects (algo, data, isText, encoding); pass base64 so binary
    // stays intact through the marshaller.
    const hex = host.cryptoHash(this._alg, toBase64(buf), false, 'hex');
    if (!encoding || encoding === 'buffer') {
      // Convert hex → bytes for buffer callers.
      const out = new Uint8Array(hex.length / 2);
      for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
      return out;
    }
    if (encoding === 'hex') return hex;
    // Convert hex → bytes → target encoding (via TextEncoder path won't
    // work for binary; use base64 conversion helper).
    const bytes = new Uint8Array(hex.length / 2);
    for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
    return encodeDigest(bytes, encoding);
  }
}

class Hmac {
  constructor(algorithm, key) {
    this._alg = algorithm;
    this._key = bytesFrom(key);
    this._chunks = [];
  }
  update(data, encoding) {
    this._chunks.push(bytesFrom(data, encoding));
    return this;
  }
  digest(encoding) {
    const total = this._chunks.reduce((n, b) => n + b.length, 0);
    const buf = new Uint8Array(total);
    let off = 0;
    for (const c of this._chunks) { buf.set(c, off); off += c.length; }
    const host = globalThis.__connectorHost;
    if (!host || typeof host.cryptoHmac !== 'function') {
      throw new Error("cryptoShim: __connectorHost.cryptoHmac missing");
    }
    const keyB64 = toBase64(this._key);
    const dataB64 = toBase64(buf);
    const hex = host.cryptoHmac(this._alg, keyB64, dataB64, false, 'hex');
    if (!encoding || encoding === 'buffer') {
      const out = new Uint8Array(hex.length / 2);
      for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
      return out;
    }
    if (encoding === 'hex') return hex;
    const bytes = new Uint8Array(hex.length / 2);
    for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
    return encodeDigest(bytes, encoding);
  }
}

export function createHash(algorithm) { return new Hash(algorithm); }
export function createHmac(algorithm, key) { return new Hmac(algorithm, key); }
export function randomBytes(n) {
  const host = globalThis.__connectorHost;
  if (host && typeof host.cryptoRandomBytes === 'function') {
    const b64 = host.cryptoRandomBytes(n);
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }
  // Fallback to WebCrypto — synchronous getRandomValues.
  const out = new Uint8Array(n);
  (globalThis.crypto || {}).getRandomValues && globalThis.crypto.getRandomValues(out);
  return out;
}
export function randomUUID() {
  const host = globalThis.__connectorHost;
  if (host && typeof host.cryptoUuid === 'function') return host.cryptoUuid();
  return globalThis.crypto && globalThis.crypto.randomUUID ? globalThis.crypto.randomUUID() : '00000000-0000-0000-0000-000000000000';
}
export default { createHash, createHmac, randomBytes, randomUUID };
`;

// fetch bridge, prepended verbatim to the bundle.
// See spec §6. The host's FetchResponse (C#) already exposes ok/status/
// statusText/headers.get/text()/json() with signatures matching the
// upstream `Response` API — no wrapper needed. The bridge just forwards
// url + init to __connectorHost.fetchAsync.
const RESPONSE_BRIDGE = `
(function () {
  const host = globalThis.__connectorHost;
  if (!host || typeof host.fetchAsync !== 'function') {
    throw new Error('connector bundle: __connectorHost.fetchAsync missing');
  }
  // ClearScript's host bridge accepts a JS object for the second param;
  // fetchAsync reads .method/.headers/.body via ScriptObject.GetProperty.
  // No JSON.stringify needed.
  globalThis.fetch = (url, init) => host.fetchAsync(String(url), init ?? null);
})();
`;

async function main() {
  if (existsSync(OUT)) rmSync(OUT, { recursive: true, force: true });
  mkdirSync(OUT, { recursive: true });

  // Sanity: every allowlisted provider has the expected files.
  for (const service of PROVIDERS) {
    const def = join(SRC, 'src/providers', service, 'definition.ts');
    const exe = join(SRC, 'src/providers', service, 'executors.ts');
    if (!existsSync(def) || !existsSync(exe)) {
      throw new Error(`[build-connector-bundle] provider ${service} missing definition.ts or executors.ts`);
    }
  }

  // Synthesize a virtual entry that imports every allowlisted provider
  // and publishes it on globalThis.__connectorProviders.
  const entryLines = [
    "// GENERATED — see scripts/build-connector-bundle.mjs",
    ...PROVIDERS.flatMap((s, i) => [
      `import { provider as def${i} } from "./src/providers/${s}/definition.ts";`,
      `import * as exe${i} from "./src/providers/${s}/executors.ts";`,
    ]),
    "const providers = {};",
    ...PROVIDERS.map((s, i) => (
      // credentialValidators is optional (no_auth providers omit it).
      // Access via bracket lookup to keep esbuild static-analysis happy.
      `providers[${JSON.stringify(s)}] = { definition: def${i}, executors: exe${i}.executors, credentialValidators: exe${i}["credentialValidators"] };`
    )),
    "globalThis.__connectorProviders = providers;",
  ];
  const entryPath = join(SRC, '.connector-entry.mjs');
  writeFileSync(entryPath, entryLines.join('\n'), 'utf8');

  // Buffer + crypto shims on disk so esbuild can resolve them via `alias`.
  const bufferShimPath = join(SRC, '.buffer-shim.mjs');
  const cryptoShimPath = join(SRC, '.crypto-shim.mjs');
  writeFileSync(bufferShimPath, BUFFER_SHIM_SOURCE, 'utf8');
  writeFileSync(cryptoShimPath, CRYPTO_SHIM_SOURCE, 'utf8');

  const esbuild = await loadEsbuild();

  const bundleOut = join(OUT, 'connector.bundle.js');
  await esbuild.build({
    entryPoints: [entryPath],
    outfile: bundleOut,
    bundle: true,
    format: 'iife',
    platform: 'browser',
    target: 'es2022',
    logLevel: 'warning',
    // no_auth providers legitimately don't export credentialValidators;
    // suppress esbuild's static-shape warning for them.
    logOverride: { 'import-is-undefined': 'silent' },
    banner: { js: RESPONSE_BRIDGE },
    alias: {
      // Redirect Node built-ins to polyfills. atob is a V8 built-in;
      // node:crypto is bridged to __connectorHost.crypto* which piggybacks
      // on OpenCLI's HostShim implementation of the same helpers.
      'node:buffer': bufferShimPath,
      'node:crypto': cryptoShimPath,
    },
    define: {
      'process.env.NODE_ENV': '"production"',
    },
  });

  // Emit the manifest from provider definitions. This lets connector_list /
  // connector_describe answer without booting V8.
  //
  // We can't import TS at the CLI level. Solution: run a second esbuild
  // build that produces a tiny "manifest emit" JS to stdout, then eval.
  const manifestEntry = join(SRC, '.manifest-entry.mjs');
  writeFileSync(
    manifestEntry,
    [
      ...PROVIDERS.map((s, i) => `import { provider as def${i} } from "./src/providers/${s}/definition.ts";`),
      "const services = [",
      ...PROVIDERS.map((s, i) => `  { ...def${i} },`),
      "];",
      "globalThis.__connectorManifest = services;",
    ].join('\n'),
    'utf8',
  );
  const manifestBundle = join(OUT, '.manifest.tmp.js');
  await esbuild.build({
    entryPoints: [manifestEntry],
    outfile: manifestBundle,
    bundle: true,
    format: 'iife',
    platform: 'browser',
    target: 'es2022',
    logLevel: 'warning',
    alias: {
      'node:buffer': bufferShimPath,
      'node:crypto': cryptoShimPath,
    },
  });

  // Load the manifest bundle in a sandbox to extract the service list.
  const manifestSrc = readFileSync(manifestBundle, 'utf8');
  const sandbox = {};
  new Function('globalThis', manifestSrc).call(sandbox, sandbox);
  const services = sandbox.__connectorManifest || [];

  const manifestJson = {
    schemaVersion: '1',
    upstreamSha: readFileSync(join(SRC, 'UPSTREAM_SHA'), 'utf8').trim(),
    generatedAt: new Date(0).toISOString(), // deterministic
    services: services.map((svc) => ({
      service: svc.service,
      displayName: svc.displayName,
      categories: svc.categories,
      authTypes: svc.authTypes,
      homepageUrl: svc.homepageUrl,
      // auth[] carries every AuthDefinition upstream declares — OAuth
      // clients need authorizationUrl / tokenUrl / scopes / auth method
      // without going back to definition.ts. Ship the full array so
      // OAuthFlowService no longer needs a hand-curated map.
      auth: svc.auth || [],
      actions: (svc.actions || []).map((a) => ({
        id: a.id,
        service: a.service,
        name: a.name,
        description: a.description,
        requiredScopes: a.requiredScopes,
        inputSchema: a.inputSchema,
        outputSchema: a.outputSchema,
      })),
    })),
  };
  writeFileSync(
    join(OUT, 'connector-manifest.json'),
    JSON.stringify(manifestJson, null, 2),
    'utf8',
  );

  // Clean up temp files.
  rmSync(entryPath, { force: true });
  rmSync(manifestEntry, { force: true });
  rmSync(bufferShimPath, { force: true });
  rmSync(cryptoShimPath, { force: true });
  rmSync(manifestBundle, { force: true });

  const bundleBytes = readFileSync(bundleOut).length;
  const actionCount = services.reduce((n, s) => n + (s.actions?.length || 0), 0);
  console.error(
    `[build-connector-bundle] bundle=${bundleBytes} bytes  providers=${services.length}  actions=${actionCount}  out=${OUT}`,
  );
}

main().catch((err) => {
  console.error(err.stack || err.message);
  process.exit(1);
});
