// Node ESM loader hook — rewrite `@jackwener/opencli/*` to host.mjs.
// Use:  node --import "data:text/javascript,import { register } from
// 'node:module'; import { pathToFileURL } from 'node:url';
// register('./loader.mjs', pathToFileURL('./'));"   bench/opencli/poc/freeze.mjs
const SHIM = new URL('./host.mjs', import.meta.url).href;
const map = new Map([
  ['@jackwener/opencli/registry', SHIM],
  ['@jackwener/opencli/errors',   SHIM],
]);
export function resolve(specifier, context, nextResolve) {
  if (map.has(specifier)) return { url: map.get(specifier), shortCircuit: true };
  return nextResolve(specifier, context);
}
