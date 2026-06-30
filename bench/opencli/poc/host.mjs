// SPEC §11.4 — Node PoC mirror of the C# host shim.
// Adapters import @jackwener/opencli/registry; loader.mjs rewrites the
// specifier to this file. Outputs from this PoC freeze the bench
// fixtures (expected.json); the C# runtime must match byte-for-byte
// on PUBLIC fetch fixtures, schema-equal on DOM / browser fixtures.

const registry = new Map();

export const Strategy = Object.freeze({
  PUBLIC: 'public',
  LOCAL: 'local',
  COOKIE: 'cookie',
  INTERCEPT: 'intercept',
  UI: 'ui',
});

export function cli(def) {
  const key = `${def.site}/${def.name}`;
  registry.set(key, def);
  return def;
}
export function getRegistry() { return registry; }
export function fullName(site, name) { return `${site}/${name}`; }
export function registerCommand(def) { return cli(def); }
export function onStartup() {}
export function onBeforeExecute() {}
export function onAfterExecute() {}

export class CliError extends Error {
  constructor(code, message, hint) { super(message); this.code = code; this.hint = hint; }
}
export class ArgumentError extends CliError { constructor(m) { super('INVALID_ARGUMENT', m); } }
export class CommandExecutionError extends CliError { constructor(m) { super('EXECUTION_FAILED', m); } }
export class EmptyResultError extends CliError { constructor(m) { super('NO_DATA', m); } }
export function isCliError(e) { return e instanceof CliError; }
export function cliError(code, message, details) { return new CliError(code, message, details); }
