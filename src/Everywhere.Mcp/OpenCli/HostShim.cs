using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;

namespace Everywhere.Mcp.OpenCli;

/// <summary>
/// SPEC §8 Phase 1 — JS-side shim that adapters import as
/// <c>@jackwener/opencli/registry</c> + <c>/errors</c>. Adapters do
/// <c>cli({ ... })</c>; the shim hands the record to the host via the
/// global <c>__opencliHost.register(...)</c> bridge.
///
/// Host-side this class also exposes the small surface adapters touch
/// when running: <c>fetch</c> (mapped to <see cref="HttpClient"/>) and
/// the registry sink.
/// </summary>
public sealed class HostShim
{
    /// <summary>Source for <c>@jackwener/opencli/registry</c>.</summary>
    public const string RegistrySource = """
        const Strategy = Object.freeze({
            PUBLIC: 'public',
            COOKIE: 'cookie',
            INTERCEPT: 'intercept',
            UI: 'ui',
            LOCAL: 'local',
        });
        const _commands = new Map();
        function fullName(site, name) { return site + '/' + name; }
        function cli(def) {
            if (!def || typeof def.site !== 'string' || typeof def.name !== 'string')
                throw new Error('cli({...}): site/name required');
            const id = fullName(def.site, def.name);
            _commands.set(id, def);
            try {
                globalThis.__opencliHost && globalThis.__opencliHost.register(id, JSON.stringify({
                    site: def.site, name: def.name,
                    description: def.description ?? '',
                    strategy: def.strategy ?? 'public',
                    browser: def.browser === true,
                    access: def.access ?? null,
                    domain: def.domain ?? null,
                    aliases: def.aliases ?? null,
                    args: def.args ?? [],
                    columns: def.columns ?? [],
                    hasFunc: typeof def.func === 'function',
                    pipeline: def.pipeline ?? null,
                }), def.func || null);
            } catch (e) {
                try { globalThis.__opencliHost && globalThis.__opencliHost.warn(String(e)); } catch {}
            }
        }
        function getRegistry() { return _commands; }
        function registerCommand(def) { return cli(def); }
        function onStartup() {}
        function onBeforeExecute() {}
        function onAfterExecute() {}
        export { cli, Strategy, getRegistry, fullName, registerCommand, onStartup, onBeforeExecute, onAfterExecute };
        """;

    /// <summary>Source for <c>@jackwener/opencli/errors</c> — covers
    /// every error class actually imported by the v1.8.5 adapter tree
    /// (ArgumentError, AuthRequiredError, CliError, CommandExecutionError,
    /// ConfigError, EmptyResultError, TimeoutError).</summary>
    public const string ErrorsSource = """
        class CliError extends Error {
            constructor(message, opts) {
                super(message);
                this.name = 'CliError';
                this.code = (opts && opts.code) || 'CLI_ERROR';
                this.details = (opts && opts.details) || null;
            }
        }
        class ArgumentError extends CliError {
            constructor(message, opts) { super(message, { ...opts, code: (opts && opts.code) || 'INVALID_ARGUMENT' }); this.name = 'ArgumentError'; }
        }
        class AuthRequiredError extends CliError {
            constructor(message, opts) { super(message || 'authentication required', { ...opts, code: (opts && opts.code) || 'AUTH_REQUIRED' }); this.name = 'AuthRequiredError'; }
        }
        class CommandExecutionError extends CliError {
            constructor(message, opts) { super(message, { ...opts, code: (opts && opts.code) || 'EXECUTION_FAILED' }); this.name = 'CommandExecutionError'; }
        }
        class ConfigError extends CliError {
            constructor(message, opts) { super(message, { ...opts, code: (opts && opts.code) || 'BAD_CONFIG' }); this.name = 'ConfigError'; }
        }
        class EmptyResultError extends CliError {
            constructor(message, opts) { super(message || 'no results', { ...opts, code: (opts && opts.code) || 'NO_DATA' }); this.name = 'EmptyResultError'; }
        }
        class TimeoutError extends CliError {
            constructor(message, opts) { super(message || 'timeout', { ...opts, code: (opts && opts.code) || 'TIMEOUT' }); this.name = 'TimeoutError'; }
        }
        function isCliError(e) { return e && (e.name === 'CliError' || e instanceof CliError); }
        function cliError(code, message, details) { return new CliError(message, { code, details }); }
        function getErrorMessage(e) {
            if (!e) return '';
            if (typeof e === 'string') return e;
            if (e.message) return String(e.message);
            try { return JSON.stringify(e); } catch { return String(e); }
        }
        function selectorError(selector, hint) { return new CommandExecutionError('selector failed: ' + selector + (hint ? ' (' + hint + ')' : '')); }
        const EXIT_CODES = Object.freeze({ OK: 0, GENERAL: 1, INVALID_ARGUMENT: 2, AUTH_REQUIRED: 3, NO_DATA: 4, EXECUTION_FAILED: 5, TIMEOUT: 124 });
        export { CliError, ArgumentError, AuthRequiredError, CommandExecutionError, ConfigError, EmptyResultError, TimeoutError, isCliError, cliError, getErrorMessage, selectorError, EXIT_CODES };
        """;

    /// <summary>Source for <c>@jackwener/opencli/utils</c> — empty
    /// passthroughs so adapter imports don't fail. These helpers are
    /// upstream-only conveniences (delay, sleep, etc.); when an adapter
    /// actually invokes one and we haven't implemented it, the resulting
    /// runtime error surfaces clearly via the opencli_run envelope.</summary>
    public const string UtilsSource = """
        const delay = (ms) => new Promise(r => setTimeout(r, ms));
        const sleep = delay;
        const range = (n) => Array.from({length: n}, (_, i) => i);
        const chunk = (arr, n) => { const o = []; for (let i = 0; i < arr.length; i += n) o.push(arr.slice(i, i + n)); return o; };
        const unique = (arr) => Array.from(new Set(arr));
        const compact = (arr) => arr.filter(x => x != null);
        const last = (arr) => arr.length ? arr[arr.length - 1] : undefined;
        const first = (arr) => arr.length ? arr[0] : undefined;
        const noop = () => {};
        const isRecord = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
        const isString = (v) => typeof v === 'string';
        const isNumber = (v) => typeof v === 'number' && !Number.isNaN(v);
        const formatBytes = (n) => { if (n == null) return ''; const u=['B','KB','MB','GB','TB']; let i=0; while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; } return n.toFixed(2) + ' ' + u[i]; };
        const formatCookieHeader = (cookies) => {
            if (!cookies) return '';
            if (Array.isArray(cookies)) return cookies.map(c => typeof c === 'string' ? c : (c.name + '=' + c.value)).join('; ');
            if (typeof cookies === 'string') return cookies;
            return Object.entries(cookies).map(([k, v]) => k + '=' + v).join('; ');
        };
        const saveBase64ToFile = () => { throw new Error('utils.saveBase64ToFile is not available in the embedded runtime'); };
        // Minimal HTML→Markdown — strip tags, preserve link text + URL.
        // Sufficient for adapter `description` / inline-content fields.
        const htmlToMarkdown = (html) => {
            if (!html || typeof html !== 'string') return '';
            return html
                .replace(/<\s*br\s*\/?>/gi, '\n')
                .replace(/<\s*\/p\s*>/gi, '\n\n')
                .replace(/<a [^>]*?href=["']([^"']+)["'][^>]*>(.*?)<\/a>/gi, '[$2]($1)')
                .replace(/<\s*strong[^>]*>(.*?)<\s*\/strong\s*>/gi, '**$1**')
                .replace(/<\s*em[^>]*>(.*?)<\s*\/em\s*>/gi, '*$1*')
                .replace(/<\s*code[^>]*>(.*?)<\s*\/code\s*>/gi, '`$1`')
                .replace(/<[^>]+>/g, '')
                .replace(/&nbsp;/g, ' ')
                .replace(/&lt;/g, '<')
                .replace(/&gt;/g, '>')
                .replace(/&amp;/g, '&')
                .replace(/&quot;/g, '"')
                .replace(/&#(\d+);/g, (_, n) => String.fromCharCode(+n))
                .replace(/\n{3,}/g, '\n\n')
                .trim();
        };
        const throwIfLoginWall = (text, hint) => {
            const t = (text || '').toString().toLowerCase();
            if (t.includes('login') || t.includes('sign in') || t.includes('登录')) {
                throw new Error('login wall detected' + (hint ? ': ' + hint : ''));
            }
        };
        const BROWSER_JSON_SNIFF_FN = `(() => { try { const t = document.body && document.body.innerText; if (!t) return null; const m = t.match(/\\{[\\s\\S]*\\}/); if (!m) return null; try { return JSON.parse(m[0]); } catch { return null; } } catch { return null; } })()`;
        export { delay, sleep, range, chunk, unique, compact, last, first, noop, isRecord, isString, isNumber, formatBytes, formatCookieHeader, saveBase64ToFile, htmlToMarkdown, throwIfLoginWall, BROWSER_JSON_SNIFF_FN };
        """;

    /// <summary>Source for <c>@jackwener/opencli/logger</c> — proxies
    /// console.</summary>
    public const string LoggerSource = """
        const _make = (lvl) => (...a) => { try { console[lvl] && console[lvl](...a); } catch {} };
        const logger = { debug: _make('debug'), info: _make('info'), warn: _make('warn'), error: _make('error'), log: _make('log') };
        const getLogger = () => logger;
        const log = _make('log');
        const debug = _make('debug');
        const info = _make('info');
        const warn = _make('warn');
        const error = _make('error');
        export { logger, getLogger, log, debug, info, warn, error };
        export default logger;
        """;

    /// <summary>Stub for <c>@jackwener/opencli/launcher</c> — present
    /// so adapter imports resolve. Adapters that actually invoke the
    /// launcher to spawn a host CLI will fail with a clear runtime error
    /// (the V8 isolate has no process spawn surface).</summary>
    public const string LauncherSource = """
        function _unsupported(name) { return () => { throw new Error('opencli/launcher.' + name + ' is not available in the embedded runtime'); }; }
        const launch = _unsupported('launch');
        const launchProcess = _unsupported('launchProcess');
        const spawn = _unsupported('spawn');
        const resolveElectronEndpoint = async () => null;
        export { launch, launchProcess, spawn, resolveElectronEndpoint };
        export default { launch, launchProcess, spawn, resolveElectronEndpoint };
        """;

    /// <summary>Stub for <c>@jackwener/opencli/download</c> + the
    /// sub-paths that v1.8.5 adapters reach for (`download/media-download`,
    /// `download/article-download`, `download/progress`).</summary>
    public const string DownloadSource = """
        function _unsupported(name) { return () => { throw new Error('opencli/download.' + name + ' is not available in the embedded runtime'); }; }
        const downloadFile = _unsupported('downloadFile');
        const articleDownload = _unsupported('articleDownload');
        const mediaDownload = _unsupported('mediaDownload');
        const downloadArticle = articleDownload;
        const downloadMedia = mediaDownload;
        const startProgress = () => ({ update: () => {}, finish: () => {} });
        const formatBytes = (n) => { if (n == null) return ''; const u=['B','KB','MB','GB','TB']; let i=0; while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; } return n.toFixed(2) + ' ' + u[i]; };
        const formatCookieHeader = (c) => {
            if (!c) return '';
            if (Array.isArray(c)) return c.map(x => typeof x === 'string' ? x : (x.name + '=' + x.value)).join('; ');
            if (typeof c === 'string') return c;
            return Object.entries(c).map(([k, v]) => k + '=' + v).join('; ');
        };
        const httpDownload = _unsupported('httpDownload');
        const checkYtdlp = async () => false;
        const sanitizeFilename = (name) => (name || '').toString().replace(/[\/\\?%*:|"<>]/g, '_').slice(0, 200);
        export { downloadFile, articleDownload, mediaDownload, downloadArticle, downloadMedia, startProgress, formatBytes, formatCookieHeader, httpDownload, checkYtdlp, sanitizeFilename };
        export default { downloadFile, articleDownload, mediaDownload, downloadArticle, downloadMedia, startProgress, formatBytes, formatCookieHeader, httpDownload, checkYtdlp, sanitizeFilename };
        """;

    /// <summary>Stub for <c>@jackwener/opencli/browser/*</c> sub-paths.</summary>
    public const string BrowserShimSource = """
        function _unsupported(name) { return () => { throw new Error('opencli/browser.' + name + ' is not available in the embedded runtime'); }; }
        class CDPBridge {
            constructor() { throw new Error('CDPBridge is not available in the embedded runtime'); }
        }
        class Page {
            constructor() { throw new Error('opencli/browser/page.Page is not available in the embedded runtime'); }
        }
        const cdp = _unsupported('cdp');
        const page = _unsupported('page');
        const utils = {};
        export { cdp, page, utils, CDPBridge, Page };
        export default { cdp, page, utils, CDPBridge, Page };
        """;

    /// <summary>Polyfill subset of Node's `path` module — pure string
    /// algorithms, safe to expose unrestricted.</summary>
    public const string NodePathSource = """
        const sep = '/';
        const delimiter = ':';
        function normalize(p) {
            if (!p) return '.';
            const isAbs = p.startsWith('/');
            const trailing = p.endsWith('/');
            const parts = p.split('/').filter(Boolean);
            const out = [];
            for (const part of parts) {
                if (part === '.') continue;
                if (part === '..') {
                    if (out.length && out[out.length - 1] !== '..') out.pop();
                    else if (!isAbs) out.push('..');
                } else out.push(part);
            }
            let res = out.join('/');
            if (isAbs) res = '/' + res;
            if (trailing && res && !res.endsWith('/')) res += '/';
            return res || (isAbs ? '/' : '.');
        }
        function join(...parts) {
            const filtered = parts.filter(p => typeof p === 'string' && p.length);
            if (!filtered.length) return '.';
            return normalize(filtered.join('/'));
        }
        function resolve(...parts) {
            let resolved = '';
            let abs = false;
            for (let i = parts.length - 1; i >= 0 && !abs; i--) {
                const p = parts[i];
                if (typeof p !== 'string' || !p) continue;
                resolved = p + '/' + resolved;
                abs = p.startsWith('/');
            }
            if (!abs) resolved = '/' + resolved;
            return normalize(resolved).replace(/\/$/, '') || '/';
        }
        function dirname(p) {
            if (!p) return '.';
            const i = p.lastIndexOf('/');
            if (i < 0) return '.';
            if (i === 0) return '/';
            return p.slice(0, i);
        }
        function basename(p, ext) {
            if (!p) return '';
            const i = p.lastIndexOf('/');
            let b = i >= 0 ? p.slice(i + 1) : p;
            if (ext && b.endsWith(ext)) b = b.slice(0, -ext.length);
            return b;
        }
        function extname(p) {
            if (!p) return '';
            const b = basename(p);
            const i = b.lastIndexOf('.');
            return i <= 0 ? '' : b.slice(i);
        }
        function isAbsolute(p) { return typeof p === 'string' && p.startsWith('/'); }
        function relative(from, to) {
            const f = resolve(from).split('/').filter(Boolean);
            const t = resolve(to).split('/').filter(Boolean);
            let i = 0; while (i < f.length && i < t.length && f[i] === t[i]) i++;
            return [...Array(f.length - i).fill('..'), ...t.slice(i)].join('/') || '.';
        }
        function parse(p) {
            const root = isAbsolute(p) ? '/' : '';
            const dir = dirname(p);
            const base = basename(p);
            const ext = extname(base);
            const name = ext ? base.slice(0, -ext.length) : base;
            return { root, dir, base, name, ext };
        }
        function format(o) {
            const dir = o.dir || o.root || '';
            const base = o.base || ((o.name || '') + (o.ext || ''));
            return dir ? (dir.endsWith('/') ? dir + base : dir + '/' + base) : base;
        }
        export { sep, delimiter, normalize, join, resolve, dirname, basename, extname, isAbsolute, relative, parse, format };
        export default { sep, delimiter, normalize, join, resolve, dirname, basename, extname, isAbsolute, relative, parse, format };
        """;

    /// <summary>Polyfill of `os` — values that have a sane meaning inside
    /// a sandbox.</summary>
    public const string NodeOsSource = """
        const platform = () => 'darwin';
        const arch = () => 'arm64';
        const tmpdir = () => '/tmp';
        const homedir = () => '/';
        const EOL = '\n';
        const hostname = () => 'opencli';
        const cpus = () => [];
        const totalmem = () => 0;
        const freemem = () => 0;
        const networkInterfaces = () => ({});
        export { platform, arch, tmpdir, homedir, EOL, hostname, cpus, totalmem, freemem, networkInterfaces };
        export default { platform, arch, tmpdir, homedir, EOL, hostname, cpus, totalmem, freemem, networkInterfaces };
        """;

    /// <summary>Polyfill of `crypto` — only the algorithms adapters
    /// actually call (md5/sha1/sha256/random). Implemented host-side via
    /// <see cref="HostShim.cryptoHashAsync"/> / <see cref="HostShim.cryptoRandomBytes"/>.</summary>
    public const string NodeCryptoSource = """
        function createHash(algo) {
            const buf = [];
            return {
                update(data) { buf.push(typeof data === 'string' ? data : __opencliHost.bytesToBase64(data)); return this; },
                digest(encoding) {
                    const joined = buf.join('');
                    const isText = buf.every(s => typeof s === 'string');
                    const out = __opencliHost.cryptoHash(algo, joined, isText, encoding || 'hex');
                    return out;
                },
            };
        }
        function randomBytes(n) {
            return __opencliHost.cryptoRandomBytes(n);
        }
        function randomUUID() { return __opencliHost.cryptoUuid(); }
        function createHmac(algo, key) {
            const buf = [];
            return {
                update(data) { buf.push(typeof data === 'string' ? data : __opencliHost.bytesToBase64(data)); return this; },
                digest(encoding) {
                    const joined = buf.join('');
                    const isText = buf.every(s => typeof s === 'string');
                    return __opencliHost.cryptoHmac(algo, key, joined, isText, encoding || 'hex');
                },
            };
        }
        export { createHash, randomBytes, randomUUID, createHmac };
        export default { createHash, randomBytes, randomUUID, createHmac };
        """;

    /// <summary>Stub for `fs` — read-only operations only, and only
    /// against paths the adapter author already trusts (no host fs
    /// access from JS).</summary>
    public const string NodeFsSource = """
        function _unsupported(name) { return () => { throw new Error('fs.' + name + ' is not available in the embedded runtime'); }; }
        const readFileSync = _unsupported('readFileSync');
        const readFile = _unsupported('readFile');
        const writeFileSync = _unsupported('writeFileSync');
        const writeFile = _unsupported('writeFile');
        const existsSync = () => false;
        const mkdirSync = _unsupported('mkdirSync');
        const stat = _unsupported('stat');
        const statSync = _unsupported('statSync');
        const promises = { readFile: _unsupported('promises.readFile'), writeFile: _unsupported('promises.writeFile'), mkdir: _unsupported('promises.mkdir'), stat: _unsupported('promises.stat') };
        const createReadStream = _unsupported('createReadStream');
        const createWriteStream = _unsupported('createWriteStream');
        export { readFileSync, readFile, writeFileSync, writeFile, existsSync, mkdirSync, stat, statSync, promises, createReadStream, createWriteStream };
        export default { readFileSync, readFile, writeFileSync, writeFile, existsSync, mkdirSync, stat, statSync, promises, createReadStream, createWriteStream };
        """;

    /// <summary>Stub for `child_process` — refused. Adapters that try
    /// to spawn a host process get a clear error rather than a silent
    /// hang or sandbox escape.</summary>
    public const string NodeChildProcessSource = """
        function _unsupported(name) { return () => { throw new Error('child_process.' + name + ' is not available in the embedded runtime'); }; }
        const exec = _unsupported('exec');
        const execSync = _unsupported('execSync');
        const execFile = _unsupported('execFile');
        const execFileSync = _unsupported('execFileSync');
        const spawn = _unsupported('spawn');
        const spawnSync = _unsupported('spawnSync');
        const fork = _unsupported('fork');
        export { exec, execSync, execFile, execFileSync, spawn, spawnSync, fork };
        export default { exec, execSync, execFile, execFileSync, spawn, spawnSync, fork };
        """;

    /// <summary>Stub for `http`/`https` — adapters that need HTTP should
    /// use the global <c>fetch</c>; expose minimal shape so static imports
    /// resolve.</summary>
    public const string NodeHttpSource = """
        function _unsupported(name) { return () => { throw new Error('http(s).' + name + ' is not available in the embedded runtime; use global fetch instead'); }; }
        const request = _unsupported('request');
        const get = _unsupported('get');
        const createServer = _unsupported('createServer');
        export { request, get, createServer };
        export default { request, get, createServer };
        """;

    // 16 MiB cap on the response body — protects the host from a
    // hostile/accidentally-large endpoint OOM-ing the process.
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS",
    };

    static HostShim()
    {
        // Make windows-1252 / gb2312 / shift_jis / etc. resolvable via
        // Encoding.GetEncoding — without this, RSS feeds outside UTF-8
        // silently mojibake.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly HttpClient _http;
    private readonly Action<string, JsonObject, ScriptObject?> _onRegister;
    private readonly Action<string> _onWarn;

    public HostShim(HttpClient http, Action<string, JsonObject, ScriptObject?> onRegister, Action<string> onWarn)
    {
        _http = http;
        _onRegister = onRegister;
        _onWarn = onWarn;
    }

    public void register(string id, string jsonMetadata, object? funcRef)
    {
        try
        {
            var meta = JsonNode.Parse(jsonMetadata)?.AsObject() ?? new JsonObject();
            var fn = funcRef as ScriptObject;
            _onRegister(id, meta, fn);
        }
        catch (Exception e)
        {
            _onWarn($"register {id}: {e.Message}");
        }
    }

    public void warn(string message) => _onWarn(message);

    // -------- crypto helpers (called from NodeCryptoSource) --------

    public string cryptoHash(string algo, string data, bool isText, string encoding)
    {
        var bytes = isText ? Encoding.UTF8.GetBytes(data) : Convert.FromBase64String(data);
        byte[] hash = algo.ToLowerInvariant() switch
        {
            "md5"    => System.Security.Cryptography.MD5.HashData(bytes),
            "sha1"   => System.Security.Cryptography.SHA1.HashData(bytes),
            "sha256" => System.Security.Cryptography.SHA256.HashData(bytes),
            "sha512" => System.Security.Cryptography.SHA512.HashData(bytes),
            _ => throw new ArgumentException($"crypto: unsupported hash algorithm '{algo}'"),
        };
        return Encode(hash, encoding);
    }

    public string cryptoHmac(string algo, string key, string data, bool isText, string encoding)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var dataBytes = isText ? Encoding.UTF8.GetBytes(data) : Convert.FromBase64String(data);
        byte[] hash = algo.ToLowerInvariant() switch
        {
            "sha1"   => System.Security.Cryptography.HMACSHA1.HashData(keyBytes, dataBytes),
            "sha256" => System.Security.Cryptography.HMACSHA256.HashData(keyBytes, dataBytes),
            "sha512" => System.Security.Cryptography.HMACSHA512.HashData(keyBytes, dataBytes),
            "md5"    => System.Security.Cryptography.HMACMD5.HashData(keyBytes, dataBytes),
            _ => throw new ArgumentException($"crypto: unsupported HMAC algorithm '{algo}'"),
        };
        return Encode(hash, encoding);
    }

    public string cryptoRandomBytes(int n)
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(n);
        return Convert.ToBase64String(bytes);
    }

    public string cryptoUuid() => Guid.NewGuid().ToString();

    public string bytesToBase64(object input)
    {
        if (input is byte[] arr) return Convert.ToBase64String(arr);
        if (input is string s) return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(input?.ToString() ?? ""));
    }

    private static string Encode(byte[] bytes, string encoding) => encoding.ToLowerInvariant() switch
    {
        "hex" => Convert.ToHexString(bytes).ToLowerInvariant(),
        "base64" => Convert.ToBase64String(bytes),
        "base64url" => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('='),
        _ => Convert.ToHexString(bytes).ToLowerInvariant(),
    };

    /// <summary>
    /// JS-visible <c>fetch(url, init)</c>. SPEC §3.1 — PUBLIC-strategy
    /// adapters use this to hit RSS / JSON / GraphQL endpoints. We map
    /// to .NET <see cref="HttpClient"/>; the returned object exposes
    /// .ok / .status / .statusText / .headers.get() / .text() / .json().
    ///
    /// Boundary checks (SPEC §2.1):
    /// <list type="bullet">
    ///   <item>http(s) only;</item>
    ///   <item>method allowlist — GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS;</item>
    ///   <item>no link-local / RFC1918 / IPv6 ULA / IPv4-mapped IPv6
    ///         loopback targets — blocks SSRF to 169.254.169.254 (cloud
    ///         metadata) and internal services. The check is best-effort;
    ///         a defence-in-depth handler with a ConnectCallback should be
    ///         used in production builds.</item>
    ///   <item>30s connect+headers / 60s body cap; 16 MiB response cap.</item>
    /// </list>
    /// </summary>
    public async Task<object> fetchAsync(string url, object? init, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) throw new ArgumentException("fetch: empty URL");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"fetch: invalid URL '{url}'");
        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"fetch: only http/https allowed, got '{uri.Scheme}'");
        if (IsBlockedHost(uri.Host))
            throw new ArgumentException($"fetch: blocked host '{uri.Host}' (link-local / loopback / private range)");

        using var msg = new HttpRequestMessage(HttpMethod.Get, uri);
        var contentHeaders = new List<(string Name, string Value)>();
        string? bodyContentType = null;

        if (init is ScriptObject so)
        {
            var method = (so.GetProperty("method") as string)?.Trim() ?? "GET";
            if (!AllowedMethods.Contains(method))
                throw new ArgumentException($"fetch: method '{method}' not allowed");
            msg.Method = HttpMethod.Parse(method.ToUpperInvariant());

            var headersObj = so.GetProperty("headers") as ScriptObject;
            if (headersObj != null)
            {
                foreach (var key in headersObj.PropertyNames)
                {
                    var raw = headersObj.GetProperty(key);
                    if (raw is null or Undefined) continue;
                    var v = raw.ToString() ?? "";
                    if (key.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
                    {
                        // Content-Length / Content-Encoding: let HttpClient
                        // compute these. Drop to avoid desync.
                        if (key.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("content-encoding", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                        {
                            bodyContentType = v;
                            continue; // applied via SerializeBody, don't double-add
                        }
                        contentHeaders.Add((key, v));
                    }
                    else
                    {
                        msg.Headers.TryAddWithoutValidation(key, v);
                    }
                }
            }

            var body = so.GetProperty("body");
            if (body is not null && body is not Undefined)
            {
                msg.Content = SerializeBody(body, bodyContentType, _onWarn);
                foreach (var (n, v) in contentHeaders)
                    msg.Content.Headers.TryAddWithoutValidation(n, v);
            }
        }

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(30));

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false);
        var ok = resp.IsSuccessStatusCode;
        var status = (int)resp.StatusCode;
        var statusText = resp.ReasonPhrase ?? "";

        var headersOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var setCookies = new List<string>();
        foreach (var h in resp.Headers)
        {
            if (h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                // RFC 6265 forbids comma-folding Set-Cookie because cookie
                // values can legitimately contain commas (e.g. Expires=...).
                setCookies.AddRange(h.Value);
            }
            else
            {
                headersOut[h.Key] = string.Join(",", h.Value);
            }
        }
        foreach (var h in resp.Content.Headers) headersOut[h.Key] = string.Join(",", h.Value);

        // Reset the deadline for body read.
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyCts.CancelAfter(TimeSpan.FromSeconds(60));

        // Bounded read.
        await using var stream = await resp.Content.ReadAsStreamAsync(bodyCts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buf.AsMemory(), bodyCts.Token).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > MaxResponseBytes)
                throw new InvalidOperationException($"fetch: response exceeded {MaxResponseBytes / (1024 * 1024)} MiB cap");
            ms.Write(buf, 0, read);
        }
        var bytes = ms.ToArray();

        var charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
        Encoding encoding = Encoding.UTF8;
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { /* unknown charset → keep UTF-8 */ }
        }
        var text = encoding.GetString(bytes);

        return new FetchResponse(ok, status, statusText, headersOut, setCookies, text);
    }

    private static HttpContent SerializeBody(object body, string? contentTypeHint, Action<string> onWarn)
    {
        if (body is ScriptObject so)
        {
            try
            {
                var engine = so.Engine;
                var serialized = engine.Script.JSON.stringify(so) as string ?? "null";
                var ct = string.IsNullOrEmpty(contentTypeHint) ? "application/json" : contentTypeHint;
                MediaTypeHeaderValue parsed;
                try { parsed = MediaTypeHeaderValue.Parse(ct); }
                catch (FormatException) { parsed = new MediaTypeHeaderValue("application/json"); }
                var c = new StringContent(serialized, Encoding.UTF8);
                c.Headers.ContentType = parsed;
                if (parsed.CharSet is null) parsed.CharSet = "utf-8";
                return c;
            }
            catch (Exception ex)
            {
                // Don't silently send "[object Object]" — surface the
                // failure so adapter authors notice.
                onWarn($"fetch: failed to serialize body ({ex.Message}); sending raw ToString fallback");
            }
        }
        var s = body.ToString() ?? "";
        var stringContent = new StringContent(s, Encoding.UTF8);
        if (!string.IsNullOrEmpty(contentTypeHint))
        {
            try { stringContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentTypeHint); }
            catch (FormatException) { }
        }
        return stringContent;
    }

    private static bool IsBlockedHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false; // public hostname — allow
        // Normalise IPv4-mapped IPv6 (e.g. ::ffff:127.0.0.1) before applying
        // the IPv4 rules.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 10) return true;                                   // 10/8
            if (bytes[0] == 172 && (bytes[1] & 0xf0) == 16) return true;       // 172.16/12
            if (bytes[0] == 192 && bytes[1] == 168) return true;               // 192.168/16
            if (bytes[0] == 169 && bytes[1] == 254) return true;               // link-local / cloud metadata
            if (bytes[0] == 100 && (bytes[1] & 0xc0) == 64) return true;       // 100.64/10 (CGNAT)
            if (bytes[0] == 0) return true;                                     // 0/8
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            if ((bytes[0] & 0xfe) == 0xfc) return true;                        // fc00::/7 ULA
        }
        return false;
    }
}

/// <summary>JS-visible fetch response. Surface mirrors `Response` for the
/// methods adapters actually call (text/json/ok/status/headers.get).</summary>
public sealed class FetchResponse
{
    private readonly string _text;

    public FetchResponse(bool ok, int status, string statusText, Dictionary<string, string> headers, IReadOnlyList<string> setCookies, string text)
    {
        this.ok = ok;
        this.status = status;
        this.statusText = statusText;
        this.headers = new HeadersMap(headers, setCookies);
        _text = text;
    }

    public bool ok { get; }
    public int status { get; }
    public string statusText { get; }
    public HeadersMap headers { get; }
    public Task<string> text() => Task.FromResult(_text);

    public Task<object?> json()
    {
        if (string.IsNullOrWhiteSpace(_text)) return Task.FromResult<object?>(null);
        try { return Task.FromResult<object?>(JsonSerializer.Deserialize<JsonNode>(_text)); }
        catch (JsonException ex) { return Task.FromException<object?>(ex); }
    }
}

/// <summary>Minimal `Headers`-shaped map (case-insensitive get).</summary>
public sealed class HeadersMap
{
    private readonly Dictionary<string, string> _headers;
    private readonly IReadOnlyList<string> _setCookies;

    public HeadersMap(Dictionary<string, string> headers, IReadOnlyList<string>? setCookies = null)
    {
        _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        _setCookies = setCookies ?? Array.Empty<string>();
    }

    public string? get(string name)
    {
        if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            return _setCookies.Count == 0 ? null : _setCookies[0];
        return _headers.TryGetValue(name, out var v) ? v : null;
    }

    public bool has(string name)
    {
        if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            return _setCookies.Count > 0;
        return _headers.ContainsKey(name);
    }

    /// <summary>Multi-value access — required for Set-Cookie (RFC 6265).</summary>
    public IReadOnlyList<string> getAll(string name)
    {
        if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            return _setCookies;
        return _headers.TryGetValue(name, out var v) ? new[] { v } : Array.Empty<string>();
    }

    public IEnumerable<string> keys()
    {
        foreach (var k in _headers.Keys) yield return k;
        if (_setCookies.Count > 0) yield return "Set-Cookie";
    }
}
