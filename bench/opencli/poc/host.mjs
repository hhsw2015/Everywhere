// SPEC §11.4 — Node PoC mirror of the C# host shim. The shim source
// here MUST stay in lock-step with HostShim.RegistrySource +
// HostShim.ErrorsSource — the load-coverage test fails the moment they
// drift.

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
  constructor(message, opts) {
    super(message);
    this.name = 'CliError';
    this.code = (opts && opts.code) || 'CLI_ERROR';
    this.details = (opts && opts.details) || null;
  }
}
export class ArgumentError extends CliError {
  constructor(message, opts) { super(message, { ...opts, code: (opts && opts.code) || 'INVALID_ARGUMENT' }); this.name = 'ArgumentError'; }
}
export class AuthRequiredError extends CliError {
  constructor(message, opts) { super(message || 'authentication required', { ...opts, code: (opts && opts.code) || 'AUTH_REQUIRED' }); this.name = 'AuthRequiredError'; }
}
export class CommandExecutionError extends CliError {
  constructor(message, opts) { super(message, { ...opts, code: (opts && opts.code) || 'EXECUTION_FAILED' }); this.name = 'CommandExecutionError'; }
}
export class ConfigError extends CliError {
  constructor(message, opts) { super(message, { ...opts, code: (opts && opts.code) || 'BAD_CONFIG' }); this.name = 'ConfigError'; }
}
export class EmptyResultError extends CliError {
  constructor(message, opts) { super(message || 'no results', { ...opts, code: (opts && opts.code) || 'NO_DATA' }); this.name = 'EmptyResultError'; }
}
export class TimeoutError extends CliError {
  constructor(message, opts) { super(message || 'timeout', { ...opts, code: (opts && opts.code) || 'TIMEOUT' }); this.name = 'TimeoutError'; }
}
export function isCliError(e) { return e && (e.name === 'CliError' || e instanceof CliError); }
export function cliError(code, message, details) { return new CliError(message, { code, details }); }
export function getErrorMessage(e) {
  if (!e) return '';
  if (typeof e === 'string') return e;
  if (e.message) return String(e.message);
  try { return JSON.stringify(e); } catch { return String(e); }
}
export function selectorError(selector, hint) { return new CommandExecutionError('selector failed: ' + selector + (hint ? ' (' + hint + ')' : '')); }
export const EXIT_CODES = Object.freeze({ OK: 0, GENERAL: 1, INVALID_ARGUMENT: 2, AUTH_REQUIRED: 3, NO_DATA: 4, EXECUTION_FAILED: 5, TIMEOUT: 124 });
