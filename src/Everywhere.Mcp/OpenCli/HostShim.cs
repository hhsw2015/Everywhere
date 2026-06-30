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
        export { CliError, ArgumentError, AuthRequiredError, CommandExecutionError, ConfigError, EmptyResultError, TimeoutError, isCliError, cliError };
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
        export { delay, sleep, range, chunk, unique, compact, last, first, noop };
        """;

    /// <summary>Source for <c>@jackwener/opencli/logger</c> — proxies
    /// console.</summary>
    public const string LoggerSource = """
        const _make = (lvl) => (...a) => { try { console[lvl] && console[lvl](...a); } catch {} };
        const logger = { debug: _make('debug'), info: _make('info'), warn: _make('warn'), error: _make('error'), log: _make('log') };
        const getLogger = () => logger;
        export { logger, getLogger };
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
        export { launch, launchProcess, spawn };
        export default { launch, launchProcess, spawn };
        """;

    /// <summary>Stub for <c>@jackwener/opencli/download</c>.</summary>
    public const string DownloadSource = """
        function _unsupported(name) { return () => { throw new Error('opencli/download.' + name + ' is not available in the embedded runtime'); }; }
        const downloadFile = _unsupported('downloadFile');
        const articleDownload = _unsupported('articleDownload');
        const mediaDownload = _unsupported('mediaDownload');
        export { downloadFile, articleDownload, mediaDownload };
        export default { downloadFile, articleDownload, mediaDownload };
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
