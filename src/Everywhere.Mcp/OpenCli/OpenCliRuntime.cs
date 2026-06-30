using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.OpenCli;

/// <summary>
/// SPEC §8 — V8 isolate hosting OpenCLI adapter <c>.js</c> files.
///
/// Lifecycle:
/// <list type="bullet">
///   <item>Lazy boot — <see cref="Resolve(string, string)"/> / <see cref="ListAsync"/>
///         triggers the V8 engine creation. Other Everywhere MCP tools never
///         touch this surface, so their cold-start budget stays intact
///         (SPEC §8 Phase 3 'Restart-tolerance').</item>
///   <item>Single shared isolate; no host fs / net / process access.
///         <see cref="HostShim.fetchAsync"/> is the only network egress.</item>
///   <item>After each adapter load batch we call <c>engine.CollectGarbage</c>;
///         closures retained via <see cref="AdapterDef.Func"/> still survive
///         because V8 keeps a live reference from <c>__opencliFuncs</c>.</item>
/// </list>
/// </summary>
public sealed class OpenCliRuntime : IAsyncDisposable
{
    private readonly ILogger<OpenCliRuntime>? _log;
    private readonly string _clisDir;
    private readonly string _manifestPath;
    private readonly Lazy<Task<V8ScriptEngine>> _engine;
    private readonly ConcurrentDictionary<string, AdapterDef> _registry = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _invokeGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    // Tracked outside the Lazy<Task<>> so DisposeAsync can tear down a
    // partially-constructed engine even when the boot Task ends faulted.
    private V8ScriptEngine? _engineInstance;

    public string ClisDir => _clisDir;
    public string UpstreamSha { get; private set; } = "unknown";

    public OpenCliRuntime(string clisDir, string manifestPath, HttpClient http, ILogger<OpenCliRuntime>? log = null)
    {
        _clisDir = clisDir;
        _manifestPath = manifestPath;
        _http = http;
        _log = log;
        _engine = new Lazy<Task<V8ScriptEngine>>(BootEngineAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>SPEC §4.1 — return the full registry sorted by site/name.</summary>
    public async Task<IReadOnlyList<AdapterDef>> ListAsync(CancellationToken ct = default)
    {
        await _engine.Value.WaitAsync(ct).ConfigureAwait(false);
        return _registry.Values
            .OrderBy(r => r.Site, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<AdapterDef?> Resolve(string site, string name, CancellationToken ct = default)
    {
        await _engine.Value.WaitAsync(ct).ConfigureAwait(false);
        return _registry.TryGetValue($"{site}/{name}", out var def) ? def : null;
    }

    /// <summary>
    /// SPEC §4.3 — run an adapter's <c>func(args)</c>. The Phase 1
    /// <see cref="Phase1StubPage"/> short-circuits every <c>page.*</c>
    /// call with <see cref="Phase2NotReadyException"/>; Phase 2 swaps
    /// it for <see cref="OpenDiaPageBridge"/>.
    /// </summary>
    public async Task<JsonObject> InvokeAsync(string site, string name, JsonObject args, IPage page, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var def = await Resolve(site, name, ct).ConfigureAwait(false);
        if (def is null)
        {
            return Failure(site, name, "RUNTIME_NOT_FOUND", $"adapter {site}/{name} not registered", sw);
        }
        if (def.Func is null)
        {
            return Failure(site, name, "RUNTIME_PIPELINE_ONLY",
                "adapter ships a pipeline (no func); pipeline runner is out-of-scope for Phase 1", sw);
        }

        // V8 is single-threaded per isolate; gate-acquisition is the only
        // cancellable point — once the JS IIFE is scheduled the gate must
        // be released only AFTER the promise settles. Link the caller's
        // token with our dispose token so a queued waiter aborts cleanly
        // when the runtime is being torn down.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        await _invokeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var engine = await _engine.Value.ConfigureAwait(false);
            engine.Script.__opencliPage = page;
            engine.Script.__opencliArgs = args.DeepClone().ToJsonString();
            engine.Script.__opencliFn = def.Func;
            // Serialise the result inside the IIFE so JSON.stringify errors
            // (circular structures, Date / BigInt) surface as adapter
            // exceptions rather than silently flipping a successful run
            // into RUNTIME_SCRIPT_ERROR after the fact.
            engine.Execute("""
                let __opencliCallArgs = JSON.parse(__opencliArgs);
                let __opencliCallResultJson = null;
                let __opencliCallError = null;
                let __opencliCallPromise = (async () => {
                    try {
                        const r = await __opencliFn(__opencliCallArgs, __opencliPage);
                        __opencliCallResultJson = JSON.stringify(r === undefined ? null : r);
                    } catch (e) {
                        const code = (e && e.code != null) ? String(e.code) : 'RUNTIME_ERROR';
                        const msg  = (e && e.message != null) ? String(e.message) : String(e);
                        __opencliCallError = { code, message: msg };
                    }
                })();
            """);
            try
            {
                await ((Task)engine.Script.__opencliCallPromise).ConfigureAwait(false);

                var err = engine.Script.__opencliCallError as ScriptObject;
                if (err is not null)
                {
                    var code = err.GetProperty("code")?.ToString() ?? "RUNTIME_ERROR";
                    var msg = err.GetProperty("message")?.ToString() ?? "unknown";
                    return Failure(site, name, code, msg, sw);
                }
                var resultText = engine.Script.__opencliCallResultJson as string;
                var data = string.IsNullOrEmpty(resultText) ? null : JsonNode.Parse(resultText);
                _log?.LogInformation("opencli_run site={Site} name={Name} ms={Ms} ok=true",
                    site, name, sw.Elapsed.TotalMilliseconds);
                return new JsonObject
                {
                    ["schema_version"] = "1",
                    ["ok"] = true,
                    ["data"] = data,
                    ["site"] = site,
                    ["name"] = name,
                    ["elapsed_ms"] = sw.Elapsed.TotalMilliseconds,
                };
            }
            finally
            {
                // Drop references to the per-call IPage / args / func so V8
                // doesn't pin them in the shared isolate until the next call.
                try
                {
                    engine.Script.__opencliPage = null;
                    engine.Script.__opencliArgs = null;
                    engine.Script.__opencliFn = null;
                    engine.Script.__opencliCallResultJson = null;
                    engine.Script.__opencliCallError = null;
                }
                catch { /* engine may already be torn down */ }
            }
        }
        catch (Phase2NotReadyException ex)
        {
            return Failure(site, name, "BROWSER_NOT_READY", ex.Message, sw);
        }
        catch (ScriptEngineException sex)
        {
            return Failure(site, name, "RUNTIME_SCRIPT_ERROR", sex.Message, sw);
        }
        catch (Exception ex)
        {
            return Failure(site, name, "RUNTIME_HOST_ERROR", ex.Message, sw);
        }
        finally
        {
            _invokeGate.Release();
        }
    }

    private JsonObject Failure(string site, string name, string code, string message, Stopwatch sw)
    {
        var ms = sw.Elapsed.TotalMilliseconds;
        _log?.LogInformation("opencli_run site={Site} name={Name} ms={Ms} ok=false code={Code}", site, name, ms, code);
        return new JsonObject
        {
            ["schema_version"] = "1",
            ["ok"] = false,
            ["error"] = message,
            ["code"] = code,
            ["site"] = site,
            ["name"] = name,
            ["elapsed_ms"] = ms,
        };
    }

    private async Task<V8ScriptEngine> BootEngineAsync()
    {
        var sw = Stopwatch.StartNew();
        var ct = _disposeCts.Token;
        var engine = new V8ScriptEngine(
            V8ScriptEngineFlags.EnableTaskPromiseConversion |
            V8ScriptEngineFlags.EnableDateTimeConversion |
            V8ScriptEngineFlags.DisableGlobalMembers);
        // Publish the engine reference IMMEDIATELY so a faulted boot can
        // still be torn down by DisposeAsync.
        _engineInstance = engine;

        try
        {
            return await BootEngineInnerAsync(engine, sw, ct).ConfigureAwait(false);
        }
        catch
        {
            try { engine.Dispose(); } catch { }
            _engineInstance = null;
            throw;
        }
    }

    private async Task<V8ScriptEngine> BootEngineInnerAsync(V8ScriptEngine engine, Stopwatch sw, CancellationToken ct)
    {
        var loader = new OpenCliDocumentLoader(_clisDir, HostShim.RegistrySource, HostShim.ErrorsSource);
        engine.DocumentSettings.Loader = loader;
        engine.DocumentSettings.AccessFlags =
            DocumentAccessFlags.EnableFileLoading |
            DocumentAccessFlags.EnforceRelativePrefix |
            DocumentAccessFlags.AllowCategoryMismatch;

        var hostShim = new HostShim(_http,
            onRegister: (id, meta, func) =>
            {
                var def = AdapterFromMeta(id, meta, func);
                if (!_registry.TryAdd(id, def))
                {
                    _log?.LogWarning("opencli register: duplicate adapter id {Id}; keeping first registration", id);
                }
            },
            onWarn: msg => _log?.LogWarning("opencli shim: {Message}", msg));
        engine.AddHostObject("__opencliHost", hostShim);

        engine.Execute("""
            globalThis.fetch = (url, init) => __opencliHost.fetchAsync(url, init || null);
            globalThis.console = { log: (...a) => __opencliHost.warn(a.map(String).join(' ')),
                                   warn: (...a) => __opencliHost.warn(a.map(String).join(' ')),
                                   error: (...a) => __opencliHost.warn(a.map(String).join(' ')),
                                   debug: () => {}, info: () => {} };
        """);

        int loaded = 0, failed = 0;
        if (Directory.Exists(_clisDir))
        {
            foreach (var path in Directory.EnumerateFiles(_clisDir, "*.js", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                if (path.EndsWith(".test.js", StringComparison.Ordinal)) continue;
                try
                {
                    var src = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    var info = new DocumentInfo(new Uri(path)) { Category = ModuleCategory.Standard };
                    engine.Execute(info, src);
                    loaded++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    failed++;
                    _log?.LogDebug(ex, "opencli adapter failed to load: {Path}", path);
                }
            }
        }
        engine.CollectGarbage(true);

        try
        {
            var shaPath = Path.Combine(Path.GetDirectoryName(_manifestPath) ?? _clisDir, "UPSTREAM_SHA");
            if (File.Exists(shaPath))
                UpstreamSha = (await File.ReadAllTextAsync(shaPath, ct).ConfigureAwait(false)).Trim();
        }
        catch (OperationCanceledException) { /* dispose-time cancellation is fine */ }
        catch (Exception ex) { _log?.LogDebug(ex, "opencli: UPSTREAM_SHA read failed"); }

        _log?.LogInformation("opencli runtime booted in {Ms}ms loaded={Loaded} failed={Failed} sha={Sha}",
            sw.ElapsedMilliseconds, loaded, failed, UpstreamSha);
        return engine;
    }

    private static AdapterDef AdapterFromMeta(string id, JsonObject meta, ScriptObject? func)
    {
        var siteName = id.Split('/', 2);
        var site = TryString(meta, "site") ?? siteName[0];
        var name = TryString(meta, "name") ?? (siteName.Length > 1 ? siteName[1] : id);
        return new AdapterDef(
            Site: site,
            Name: name,
            Description: TryString(meta, "description") ?? "",
            Strategy: (TryString(meta, "strategy") ?? "public").Trim().ToLowerInvariant(),
            Browser: TryBool(meta, "browser") ?? false,
            Access: TryString(meta, "access"),
            Domain: TryString(meta, "domain"),
            Aliases: TryStringArray(meta, "aliases"),
            // Clone JSON nodes so the cached AdapterDef owns detachable
            // copies; otherwise sharing parents with `meta` produces
            // "node already has a parent" the first time we re-emit them.
            Args: (meta["args"] as JsonArray)?.DeepClone() as JsonArray,
            Columns: (meta["columns"] as JsonArray)?.DeepClone() as JsonArray,
            Func: func,
            Pipeline: meta["pipeline"]?.DeepClone());
    }

    private static string? TryString(JsonObject meta, string key)
    {
        if (!meta.TryGetPropertyValue(key, out var node) || node is null) return null;
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private static bool? TryBool(JsonObject meta, string key)
    {
        if (!meta.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    private static IReadOnlyList<string>? TryStringArray(JsonObject meta, string key)
    {
        if (!meta.TryGetPropertyValue(key, out var node) || node is not JsonArray arr) return null;
        var list = new List<string>(arr.Count);
        foreach (var e in arr)
        {
            if (e is JsonValue v && v.TryGetValue<string>(out var s)) list.Add(s);
        }
        return list.Count == 0 ? null : list;
    }

    public async ValueTask DisposeAsync()
    {
        try { _disposeCts.Cancel(); } catch { }
        // Dispose via the tracked field so a faulted boot still releases V8.
        var engine = _engineInstance;
        if (engine is not null)
        {
            try { engine.Dispose(); }
            catch (Exception ex) { _log?.LogDebug(ex, "opencli runtime dispose failed"); }
            _engineInstance = null;
        }
        else if (_engine.IsValueCreated && _engine.Value.IsCompletedSuccessfully)
        {
            try { _engine.Value.Result.Dispose(); }
            catch (Exception ex) { _log?.LogDebug(ex, "opencli runtime dispose failed"); }
        }
        _invokeGate.Dispose();
        _disposeCts.Dispose();
        await Task.CompletedTask;
    }
}
