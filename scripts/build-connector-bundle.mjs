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

import { mkdirSync, existsSync, writeFileSync, rmSync, readFileSync } from 'node:fs';
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

// Phase 1 allowlist. Every provider listed must have both
// `src/providers/<name>/definition.ts` and `.../executors.ts`.
const PROVIDERS = ['github'];

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
// atob is a V8 built-in in the ClearScript engine; if it turns out to be
// missing at runtime, swap this for a host-side base64 helper.
const BUFFER_SHIM_SOURCE = `
export const Buffer = {
  from(input, encoding) {
    if (encoding !== 'base64') {
      throw new Error("BufferShim: only base64 encoding is supported, got: " + encoding);
    }
    const bin = atob(input);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  },
};
export default { Buffer };
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
      `providers[${JSON.stringify(s)}] = { definition: def${i}, executors: exe${i}.executors, credentialValidators: exe${i}.credentialValidators };`
    )),
    "globalThis.__connectorProviders = providers;",
  ];
  const entryPath = join(SRC, '.connector-entry.mjs');
  writeFileSync(entryPath, entryLines.join('\n'), 'utf8');

  // Buffer shim on disk so esbuild can resolve it via `inject`.
  const bufferShimPath = join(SRC, '.buffer-shim.mjs');
  writeFileSync(bufferShimPath, BUFFER_SHIM_SOURCE, 'utf8');

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
    banner: { js: RESPONSE_BRIDGE },
    alias: {
      // Redirect Node's node:buffer to our polyfill. atob is a V8 built-in
      // in ClearScript, so no host bridge is needed.
      'node:buffer': bufferShimPath,
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
    alias: { 'node:buffer': bufferShimPath },
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
