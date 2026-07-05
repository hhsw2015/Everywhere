using System.Diagnostics;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.Connector;

/// <summary>
/// SPEC docs/specs/everywhere-connector.md §3.1 — dedicated ClearScript V8
/// isolate hosting the open-connector provider bundle. Separate engine
/// from <see cref="OpenCli.OpenCliRuntime"/> to prevent global namespace
/// collisions and fault isolation between the two subsystems.
///
/// Lifecycle:
/// <list type="bullet">
///   <item>Lazy boot — engine is created on the first <see cref="InvokeAsync"/>
///         call. Cheap ops (<see cref="ListManifest"/>) read the on-disk
///         manifest.json without touching V8.</item>
///   <item>Bundle is loaded once at boot from
///         <c>Resources/connector/connector.bundle.js</c>. It publishes
///         <c>globalThis.__connectorProviders</c>.</item>
///   <item>Refresh-on-fault: a faulted boot Task is re-attempted by the
///         next caller (mirrors <see cref="OpenCliRuntime"/>).</item>
/// </list>
/// </summary>
public sealed class ConnectorRuntime : IAsyncDisposable
{
    private readonly ILogger<ConnectorRuntime>? _log;
    private readonly string _bundleDir;
    private readonly HttpClient _http;
    private readonly ICredentialResolver _credentials;
    private Task<V8ScriptEngine>? _engineTask;
    private readonly object _engineBootLock = new();
    private readonly SemaphoreSlim _invokeGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private V8ScriptEngine? _engineInstance;
    // Cached during BootEngineAsync so InvokeAsync can set the per-call
    // connection scope without walking the ClearScript engine.Script bag.
    private ConnectorHostShim? _hostShim;

    // Manifest read once, kept in memory. Refreshed on next process boot.
    private ConnectorManifest? _manifest;
    private readonly object _manifestLock = new();

    private readonly TransitFileStore? _transit;

    public ConnectorRuntime(
        string bundleDir,
        HttpClient http,
        ICredentialResolver credentials,
        ILogger<ConnectorRuntime>? log = null,
        TransitFileStore? transit = null)
    {
        _bundleDir = bundleDir;
        _http = http;
        _credentials = credentials;
        _log = log;
        _transit = transit;
    }

    /// <summary>Optional OAuth refresher — set post-construction by DI to
    /// break the ConnectorRuntime ↔ OAuthFlowService cycle
    /// (OAuthFlowService itself needs a ConnectorRuntime reference to
    /// resolve provider auth definitions). When set, InvokeAsync
    /// preemptively refreshes near-expiry OAuth tokens.</summary>
    public IOAuthRefresher? OAuthRefresher { get; set; }

    public string BundleDir => _bundleDir;
    public string UpstreamSha { get; private set; } = "unknown";

    /// <summary>Read the on-disk manifest (cheap; no V8).</summary>
    public ConnectorManifest ListManifest()
    {
        lock (_manifestLock)
        {
            if (_manifest is not null) return _manifest;
            var manifestPath = Path.Combine(_bundleDir, "connector-manifest.json");
            if (!File.Exists(manifestPath))
            {
                _log?.LogWarning("connector: manifest not found at {Path}", manifestPath);
                _manifest = new ConnectorManifest(Array.Empty<ConnectorService>(), "missing");
                return _manifest;
            }
            var shaPath = Path.Combine(_bundleDir, "UPSTREAM_SHA");
            if (File.Exists(shaPath))
            {
                try { UpstreamSha = File.ReadAllText(shaPath).Trim(); }
                catch { /* best-effort */ }
            }
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(manifestPath));
                var servicesNode = node?["services"] as JsonArray ?? new JsonArray();
                var services = new List<ConnectorService>();
                foreach (var svcNode in servicesNode)
                {
                    if (svcNode is not JsonObject svc) continue;
                    var actions = new List<ConnectorAction>();
                    if (svc["actions"] is JsonArray acts)
                    {
                        foreach (var a in acts)
                        {
                            if (a is not JsonObject act) continue;
                            actions.Add(new ConnectorAction(
                                Id: act["id"]?.GetValue<string>() ?? "",
                                Service: act["service"]?.GetValue<string>() ?? "",
                                Name: act["name"]?.GetValue<string>() ?? "",
                                Description: act["description"]?.GetValue<string>() ?? "",
                                RequiredScopes: (act["requiredScopes"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>(),
                                InputSchema: act["inputSchema"]?.DeepClone(),
                                OutputSchema: act["outputSchema"]?.DeepClone()));
                        }
                    }
                    // auth[] carries the AuthDefinition array upstream
                    // ships. OAuthFlowService reads this so the curated
                    // map no longer has to be maintained by hand.
                    var authArr = (svc["auth"] as JsonArray)?.DeepClone() as JsonArray;
                    services.Add(new ConnectorService(
                        Service: svc["service"]?.GetValue<string>() ?? "",
                        DisplayName: svc["displayName"]?.GetValue<string>() ?? "",
                        Categories: (svc["categories"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>(),
                        AuthTypes: (svc["authTypes"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>(),
                        HomepageUrl: svc["homepageUrl"]?.GetValue<string>(),
                        Actions: actions,
                        Auth: authArr));
                }
                _manifest = new ConnectorManifest(services, UpstreamSha);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "connector: manifest parse failed");
                _manifest = new ConnectorManifest(Array.Empty<ConnectorService>(), "parse-error");
            }
            return _manifest;
        }
    }

    /// <summary>SPEC §8.3 — execute one provider action inside the V8
    /// isolate. Envelope adaptation (upstream ExecutionResult → shared
    /// envelope) happens here so callers only handle one shape.
    /// SPEC Phase 12 — <paramref name="connectionName"/> routes credential
    /// lookup to a named connection ("work" → github:work); null uses
    /// the default connection.</summary>
    public Task<JsonObject> InvokeAsync(string service, string actionName, JsonObject input, CancellationToken ct = default)
        => InvokeAsync(service, actionName, input, connectionName: null, ct: ct);

    public async Task<JsonObject> InvokeAsync(string service, string actionName, JsonObject input, string? connectionName, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(service)) return Failure(service, actionName, "invalid_input", "service is required", sw);
        if (string.IsNullOrWhiteSpace(actionName)) return Failure(service, actionName, "invalid_input", "action name is required", sw);

        var manifest = ListManifest();
        var svc = manifest.Services.FirstOrDefault(s => s.Service == service);
        if (svc is null) return Failure(service, actionName, "RUNTIME_NOT_FOUND", $"service '{service}' not in manifest", sw);
        var act = svc.Actions.FirstOrDefault(a => a.Name == actionName);
        if (act is null) return Failure(service, actionName, "RUNTIME_NOT_FOUND", $"action '{service}.{actionName}' not in manifest", sw);

        // SPEC Phase 6 — opportunistic OAuth refresh. Fire-and-forget on
        // failure so a broken refresh doesn't take down the whole call
        // path; upstream will surface a real 401 to the agent and the
        // user can reconnect. Best-effort only.
        if (OAuthRefresher is not null && OAuthRefresher.NeedsRefresh(service))
        {
            try { await OAuthRefresher.TryRefreshAsync(service, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "connector refresh: pre-invoke refresh failed for {Service}", service);
            }
        }

        V8ScriptEngine engine;
        try
        {
            engine = await GetEngineTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(service, actionName, "RUNTIME_HOST_ERROR", $"V8 boot failed: {ex.Message}", sw);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        await _invokeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            _hostShim?.SetConnectionScope(connectionName);
            try
            {
                engine.Script.__connectorActionId = act.Id;
                engine.Script.__connectorService = service;
                engine.Script.__connectorInputJson = input.ToJsonString();
                engine.Script.__connectorResultJson = null;
                engine.Script.__connectorError = null;

                engine.Execute("""
                    globalThis.__connectorCallPromise = (async () => {
                        try {
                            const providers = globalThis.__connectorProviders || {};
                            const provider = providers[globalThis.__connectorService];
                            if (!provider) {
                                throw { code: 'RUNTIME_NOT_FOUND',
                                        message: 'provider not loaded in bundle: ' + globalThis.__connectorService };
                            }
                            const executor = provider.executors && provider.executors[globalThis.__connectorActionId];
                            if (typeof executor !== 'function') {
                                throw { code: 'RUNTIME_NOT_FOUND',
                                        message: 'executor missing for action: ' + globalThis.__connectorActionId };
                            }
                            const input = JSON.parse(globalThis.__connectorInputJson);
                            const executionContext = {
                                async getCredential(svc) {
                                    const raw = globalThis.__connectorHost.getCredential(svc);
                                    if (!raw) return undefined;
                                    try { return JSON.parse(raw); }
                                    catch { return undefined; }
                                },
                                // Phase 8 — transitFiles bridge. Upstream
                                // executors call `.create(File)` (a
                                // browser File instance) and `.read(fileId)`.
                                // Adapt to the base64 host bridge.
                                transitFiles: (globalThis.__connectorHost.transitMaxBytes && globalThis.__connectorHost.transitMaxBytes() > 0) ? {
                                    maxBytes: globalThis.__connectorHost.transitMaxBytes(),
                                    async create(file) {
                                        const buf = new Uint8Array(await file.arrayBuffer());
                                        let bin = '';
                                        for (let i = 0; i < buf.length; i++) bin += String.fromCharCode(buf[i]);
                                        const b64 = btoa(bin);
                                        const raw = globalThis.__connectorHost.transitCreate(b64, file.name || 'upload', file.type || 'application/octet-stream');
                                        return JSON.parse(raw);
                                    },
                                    async read(fileId) {
                                        const raw = globalThis.__connectorHost.transitRead(fileId);
                                        if (!raw) throw new Error('transit file not found: ' + fileId);
                                        const meta = JSON.parse(raw);
                                        const bin = atob(meta.base64);
                                        const buf = new Uint8Array(bin.length);
                                        for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i);
                                        return {
                                            file: new File([buf], meta.name, { type: meta.mimeType }),
                                            sizeBytes: meta.sizeBytes,
                                            name: meta.name,
                                            mimeType: meta.mimeType,
                                        };
                                    },
                                    async delete(fileId) {
                                        return globalThis.__connectorHost.transitDelete(fileId);
                                    },
                                } : undefined,
                            };
                            const result = await executor(input, executionContext);
                            // Upstream ExecutionResult: { ok, output?, error? }.
                            globalThis.__connectorResultJson = JSON.stringify(result);
                        } catch (e) {
                            const code = (e && e.code != null) ? String(e.code) : 'provider_error';
                            const msg  = (e && e.message != null) ? String(e.message) : String(e);
                            globalThis.__connectorError = { code, message: msg };
                        }
                    })();
                """);

                await ((Task)engine.Script.__connectorCallPromise).ConfigureAwait(false);

                var err = engine.Script.__connectorError as ScriptObject;
                if (err is not null)
                {
                    var code = err.GetProperty("code")?.ToString() ?? "provider_error";
                    var msg = err.GetProperty("message")?.ToString() ?? "unknown";
                    return Failure(service, actionName, code, msg, sw);
                }

                var resultText = engine.Script.__connectorResultJson as string;
                if (string.IsNullOrEmpty(resultText))
                    return Failure(service, actionName, "RUNTIME_HOST_ERROR", "executor returned no result", sw);

                var upstreamResult = JsonNode.Parse(resultText) as JsonObject;
                if (upstreamResult is null)
                    return Failure(service, actionName, "RUNTIME_HOST_ERROR", "executor result is not an object", sw);

                var ok = upstreamResult["ok"]?.GetValue<bool>() ?? false;
                if (ok)
                {
                    return new JsonObject
                    {
                        ["schema_version"] = "1",
                        ["ok"] = true,
                        ["service"] = service,
                        ["name"] = actionName,
                        ["data"] = upstreamResult["output"]?.DeepClone(),
                        ["elapsed_ms"] = sw.Elapsed.TotalMilliseconds,
                    };
                }
                var upstreamErr = upstreamResult["error"] as JsonObject;
                var errCode = upstreamErr?["code"]?.GetValue<string>() ?? "provider_error";
                var errMsg = upstreamErr?["message"]?.GetValue<string>() ?? "provider action failed";
                return Failure(service, actionName, errCode, errMsg, sw);
            }
            finally
            {
                _hostShim?.SetConnectionScope(null);
                try
                {
                    engine.Script.__connectorInputJson = null;
                    engine.Script.__connectorResultJson = null;
                    engine.Script.__connectorError = null;
                    engine.Script.__connectorActionId = null;
                    engine.Script.__connectorService = null;
                }
                catch { }
            }
        }
        catch (ScriptEngineException sex)
        {
            return Failure(service, actionName, "RUNTIME_SCRIPT_ERROR", sex.Message, sw);
        }
        catch (Exception ex)
        {
            return Failure(service, actionName, "RUNTIME_HOST_ERROR", ex.Message, sw);
        }
        finally
        {
            _invokeGate.Release();
        }
    }

    private JsonObject Failure(string? service, string? name, string code, string message, Stopwatch sw)
    {
        var ms = sw.Elapsed.TotalMilliseconds;
        _log?.LogInformation("connector_run service={Service} name={Name} ms={Ms} ok=false code={Code}", service, name, ms, code);
        var envelope = new JsonObject
        {
            ["schema_version"] = "1",
            ["ok"] = false,
            ["service"] = service,
            ["name"] = name,
            ["code"] = code,
            ["error"] = message,
            ["elapsed_ms"] = ms,
        };
        // Actionable hint for Phase 1 credential-missing case.
        if (code == "authorization_failed" && !string.IsNullOrEmpty(service))
        {
            envelope["hint"] = $"Set env var EVERYWHERE_CONNECTOR_{service!.ToUpperInvariant()}_PAT (Phase 1).";
        }
        return envelope;
    }

    private Task<V8ScriptEngine> GetEngineTask()
    {
        lock (_engineBootLock)
        {
            if (_engineTask is null || (_engineTask.IsCompleted && _engineTask.IsFaulted))
            {
                _engineTask = Task.Run(BootEngineAsync);
            }
            return _engineTask;
        }
    }

    private async Task<V8ScriptEngine> BootEngineAsync()
    {
        var sw = Stopwatch.StartNew();
        var engine = new V8ScriptEngine(
            V8ScriptEngineFlags.EnableTaskPromiseConversion |
            V8ScriptEngineFlags.EnableDateTimeConversion |
            V8ScriptEngineFlags.DisableGlobalMembers);
        _engineInstance = engine;
        try
        {
            var bundlePath = Path.Combine(_bundleDir, "connector.bundle.js");
            if (!File.Exists(bundlePath))
                throw new FileNotFoundException($"connector bundle not found: {bundlePath}", bundlePath);

            // Reuse OpenCLI's HostShim for its fetchAsync (SSRF-guarded HTTP egress).
            // We only wire the register hook to a no-op — the connector bundle
            // doesn't use it (no cli({...}) pattern; providers register onto
            // globalThis.__connectorProviders directly).
            var fetchShim = new HostShim(_http,
                onRegister: (_, _, _) => { },
                onWarn: m => _log?.LogWarning("connector host: {Message}", m));
            var connectorHost = new ConnectorHostShim(fetchShim, _credentials,
                m => _log?.LogWarning("connector: {Message}", m),
                transit: _transit);
            _hostShim = connectorHost;
            engine.AddHostObject("__connectorHost", connectorHost);

            // Minimal global surface expected by upstream code.
            engine.Execute("""
                // Phase 9 — pragmatic URL/URLSearchParams polyfill.
                // Upstream providers call `new URL(base, path)`, `url.searchParams.set`,
                // `url.toString()`, and read pathname/host/origin.
                // Full WHATWG parser is heavy; this covers what open-connector
                // executors actually reach for. Falls through to native when
                // ClearScript exposes it.
                if (typeof globalThis.URL === 'undefined') {
                    globalThis.URLSearchParams = class URLSearchParams {
                        constructor(init) {
                            this._pairs = [];
                            if (typeof init === 'string') {
                                const s = init.replace(/^\?/, '');
                                if (s) for (const p of s.split('&')) {
                                    const eq = p.indexOf('=');
                                    this._pairs.push(eq < 0
                                        ? [decodeURIComponent(p), '']
                                        : [decodeURIComponent(p.slice(0, eq)), decodeURIComponent(p.slice(eq + 1).replace(/\+/g, ' '))]);
                                }
                            } else if (init && typeof init === 'object') {
                                for (const k of Object.keys(init)) this._pairs.push([k, String(init[k])]);
                            }
                        }
                        get(k) { for (const [pk, pv] of this._pairs) if (pk === k) return pv; return null; }
                        set(k, v) { this.delete(k); this._pairs.push([k, String(v)]); }
                        append(k, v) { this._pairs.push([k, String(v)]); }
                        has(k) { return this._pairs.some(([pk]) => pk === k); }
                        delete(k) { this._pairs = this._pairs.filter(([pk]) => pk !== k); }
                        toString() {
                            return this._pairs
                                .map(([k, v]) => encodeURIComponent(k) + '=' + encodeURIComponent(v).replace(/%20/g, '+'))
                                .join('&');
                        }
                        forEach(cb) { for (const [k, v] of this._pairs) cb(v, k, this); }
                        entries() { return this._pairs[Symbol.iterator](); }
                        keys() { return this._pairs.map(p => p[0])[Symbol.iterator](); }
                        values() { return this._pairs.map(p => p[1])[Symbol.iterator](); }
                        [Symbol.iterator]() { return this.entries(); }
                    };
                    globalThis.URL = class URL {
                        constructor(url, base) {
                            let full;
                            if (base && !/^[a-z][a-z0-9+\-.]*:/i.test(url)) {
                                const baseUrl = new URL(base);
                                if (url.startsWith('//')) full = baseUrl.protocol + url;
                                else if (url.startsWith('/')) full = baseUrl.origin + url;
                                else full = baseUrl.origin + baseUrl.pathname.replace(/[^/]*$/, '') + url;
                            } else {
                                full = String(url);
                            }
                            const m = /^([a-z][a-z0-9+\-.]*):\/\/([^/?#]+)([^?#]*)(\?[^#]*)?(#.*)?$/i.exec(full);
                            if (!m) throw new TypeError('Invalid URL: ' + full);
                            this.protocol = m[1].toLowerCase() + ':';
                            this.host = m[2];
                            const at = m[2].indexOf('@');
                            const auth = at >= 0 ? m[2].slice(0, at) : '';
                            const hostPort = at >= 0 ? m[2].slice(at + 1) : m[2];
                            const colon = hostPort.lastIndexOf(':');
                            this.hostname = colon >= 0 ? hostPort.slice(0, colon) : hostPort;
                            this.port = colon >= 0 ? hostPort.slice(colon + 1) : '';
                            this.username = auth.split(':')[0] || '';
                            this.password = auth.split(':')[1] || '';
                            this.pathname = m[3] || '/';
                            this.search = m[4] || '';
                            this.hash = m[5] || '';
                            this.origin = this.protocol + '//' + hostPort;
                            this.searchParams = new URLSearchParams(this.search);
                            const self = this;
                            const origSet = this.searchParams.set.bind(this.searchParams);
                            const origAppend = this.searchParams.append.bind(this.searchParams);
                            const origDelete = this.searchParams.delete.bind(this.searchParams);
                            const sync = () => { const s = self.searchParams.toString(); self.search = s ? '?' + s : ''; };
                            this.searchParams.set = (k, v) => { origSet(k, v); sync(); };
                            this.searchParams.append = (k, v) => { origAppend(k, v); sync(); };
                            this.searchParams.delete = (k) => { origDelete(k); sync(); };
                        }
                        get href() { return this.toString(); }
                        toString() {
                            let out = this.origin + this.pathname;
                            if (this.search) out += this.search;
                            if (this.hash) out += this.hash;
                            return out;
                        }
                    };
                }
                // Phase 9 — TextEncoder / TextDecoder polyfill. Buffer
                // and crypto shims lean on TextEncoder to turn strings
                // into UTF-8 byte arrays. ClearScript's V8 doesn't
                // expose either global by default.
                if (typeof globalThis.TextEncoder === 'undefined') {
                    globalThis.TextEncoder = class TextEncoder {
                        get encoding() { return 'utf-8'; }
                        encode(str) {
                            str = String(str ?? '');
                            const out = [];
                            for (let i = 0; i < str.length; i++) {
                                let c = str.charCodeAt(i);
                                if (c < 0x80) { out.push(c); continue; }
                                if (c < 0x800) {
                                    out.push(0xc0 | (c >> 6));
                                    out.push(0x80 | (c & 0x3f));
                                    continue;
                                }
                                if (c >= 0xd800 && c <= 0xdbff && i + 1 < str.length) {
                                    const c2 = str.charCodeAt(i + 1);
                                    if (c2 >= 0xdc00 && c2 <= 0xdfff) {
                                        const cp = 0x10000 + (((c - 0xd800) << 10) | (c2 - 0xdc00));
                                        i++;
                                        out.push(0xf0 | (cp >> 18));
                                        out.push(0x80 | ((cp >> 12) & 0x3f));
                                        out.push(0x80 | ((cp >> 6) & 0x3f));
                                        out.push(0x80 | (cp & 0x3f));
                                        continue;
                                    }
                                }
                                out.push(0xe0 | (c >> 12));
                                out.push(0x80 | ((c >> 6) & 0x3f));
                                out.push(0x80 | (c & 0x3f));
                            }
                            return new Uint8Array(out);
                        }
                    };
                }
                if (typeof globalThis.TextDecoder === 'undefined') {
                    globalThis.TextDecoder = class TextDecoder {
                        constructor(label) { this._label = (label || 'utf-8').toLowerCase(); }
                        get encoding() { return this._label; }
                        decode(buf) {
                            const bytes = buf instanceof Uint8Array ? buf : new Uint8Array(buf.buffer || buf);
                            let s = '';
                            let i = 0;
                            while (i < bytes.length) {
                                const b = bytes[i++];
                                if (b < 0x80) { s += String.fromCharCode(b); continue; }
                                if ((b & 0xe0) === 0xc0) {
                                    s += String.fromCharCode(((b & 0x1f) << 6) | (bytes[i++] & 0x3f));
                                    continue;
                                }
                                if ((b & 0xf0) === 0xe0) {
                                    const c = ((b & 0x0f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f);
                                    s += String.fromCharCode(c);
                                    continue;
                                }
                                if ((b & 0xf8) === 0xf0) {
                                    const cp = ((b & 0x07) << 18) | ((bytes[i++] & 0x3f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f);
                                    const off = cp - 0x10000;
                                    s += String.fromCharCode(0xd800 | (off >> 10), 0xdc00 | (off & 0x3ff));
                                    continue;
                                }
                            }
                            return s;
                        }
                    };
                }
                if (typeof globalThis.atob === 'undefined') {
                    // ClearScript's V8 sometimes omits atob. Buffer/crypto
                    // shims need it; provide a minimal implementation.
                    const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
                    globalThis.atob = function (str) {
                        str = String(str ?? '').replace(/=+$/, '');
                        let s = '';
                        let bits = 0, buffer = 0;
                        for (let i = 0; i < str.length; i++) {
                            const c = B64.indexOf(str[i]);
                            if (c < 0) continue;
                            buffer = (buffer << 6) | c;
                            bits += 6;
                            if (bits >= 8) {
                                bits -= 8;
                                s += String.fromCharCode((buffer >> bits) & 0xff);
                            }
                        }
                        return s;
                    };
                }
                if (typeof globalThis.btoa === 'undefined') {
                    const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
                    globalThis.btoa = function (str) {
                        str = String(str ?? '');
                        let out = '';
                        for (let i = 0; i < str.length; i += 3) {
                            const a = str.charCodeAt(i);
                            const b = i + 1 < str.length ? str.charCodeAt(i + 1) : 0;
                            const c = i + 2 < str.length ? str.charCodeAt(i + 2) : 0;
                            out += B64[a >> 2];
                            out += B64[((a & 3) << 4) | (b >> 4)];
                            out += i + 1 < str.length ? B64[((b & 15) << 2) | (c >> 6)] : '=';
                            out += i + 2 < str.length ? B64[c & 63] : '=';
                        }
                        return out;
                    };
                }
                globalThis.setTimeout = globalThis.setTimeout || function (fn, ms) { return 0; };
                globalThis.clearTimeout = globalThis.clearTimeout || function () {};
                // Some vendored providers reference `Buffer` as a global
                // (Node convention) without importing node:buffer. Alias
                // to the same shim esbuild inlines via node:buffer.
                if (typeof globalThis.Buffer === 'undefined') {
                    globalThis.Buffer = {
                        from(input, encoding) {
                            encoding = (encoding || 'utf8').toLowerCase();
                            if (encoding === 'base64') {
                                const bin = atob(String(input));
                                const out = new Uint8Array(bin.length);
                                for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
                                return out;
                            }
                            if (encoding === 'hex') {
                                const s = String(input);
                                const out = new Uint8Array(s.length / 2);
                                for (let i = 0; i < out.length; i++) out[i] = parseInt(s.substr(i * 2, 2), 16);
                                return out;
                            }
                            return new TextEncoder().encode(String(input));
                        },
                        isBuffer(x) { return x instanceof Uint8Array; },
                        concat(list) {
                            let total = 0;
                            for (const b of list) total += b.length;
                            const out = new Uint8Array(total);
                            let off = 0;
                            for (const b of list) { out.set(b, off); off += b.length; }
                            return out;
                        },
                    };
                }
            """);

            // Load the bundle. The IIFE assigns globalThis.__connectorProviders
            // and installs the fetch shim.
            var bundleSrc = await File.ReadAllTextAsync(bundlePath, _disposeCts.Token).ConfigureAwait(false);
            var info = new DocumentInfo(new Uri(bundlePath)) { Category = ModuleCategory.Standard };
            engine.Execute(info, bundleSrc);

            // Sanity — must have at least one provider registered.
            var providers = engine.Script.__connectorProviders;
            if (providers is null || providers is Undefined)
                throw new InvalidOperationException("connector bundle did not publish globalThis.__connectorProviders");

            _log?.LogInformation("connector runtime booted in {Ms}ms bundle={Bundle} sha={Sha}",
                sw.ElapsedMilliseconds, bundlePath, UpstreamSha);
            return engine;
        }
        catch
        {
            try { engine.Dispose(); } catch { }
            _engineInstance = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        try
        {
            if (_engineTask is not null)
            {
                try
                {
                    var eng = await _engineTask.ConfigureAwait(false);
                    eng.Dispose();
                }
                catch { }
            }
            else if (_engineInstance is not null)
            {
                try { _engineInstance.Dispose(); } catch { }
            }
        }
        finally
        {
            _invokeGate.Dispose();
            _disposeCts.Dispose();
        }
    }
}

public sealed record ConnectorManifest(IReadOnlyList<ConnectorService> Services, string UpstreamSha);

/// <summary>SPEC Phase 6 — optional OAuth refresher wired to
/// <see cref="ConnectorRuntime.OAuthRefresher"/> after DI construction.
/// Present as an interface so tests can substitute a stub without
/// pulling in the whole OAuthFlowService dependency tree.</summary>
public interface IOAuthRefresher
{
    bool NeedsRefresh(string service, int marginSeconds = 60);
    Task<bool> TryRefreshAsync(string service, CancellationToken ct = default);
}

public sealed record ConnectorService(
    string Service,
    string DisplayName,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> AuthTypes,
    string? HomepageUrl,
    IReadOnlyList<ConnectorAction> Actions,
    JsonArray? Auth = null);

public sealed record ConnectorAction(
    string Id,
    string Service,
    string Name,
    string Description,
    IReadOnlyList<string> RequiredScopes,
    JsonNode? InputSchema,
    JsonNode? OutputSchema);
