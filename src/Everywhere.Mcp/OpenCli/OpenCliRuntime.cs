using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
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
    // Engine boot is refreshable on failure — Lazy<Task<>> would cache
    // a faulted Task forever and permanently poison every subsequent
    // call after a transient boot error.
    private Task<V8ScriptEngine>? _engineTask;
    private readonly object _engineBootLock = new();
    private readonly ConcurrentDictionary<string, AdapterDef> _registry = new(StringComparer.Ordinal);
    // Manifest-only metadata, populated from cli-manifest.json BEFORE V8
    // boots. We never load any adapter .js until opencli_run hits one —
    // saves ~1.3s of cold-start, ~95% of the V8 isolate working set, and
    // turns "loaded=263 failed=1029" into "loaded=N where N = adapters
    // the user actually used".
    private readonly ConcurrentDictionary<string, ManifestEntry> _manifest = new(StringComparer.Ordinal);
    // Cached unloaded AdapterDef per manifest entry — avoids paying
    // O(meta-size) DeepClone on every ListAsync/Resolve.
    private readonly ConcurrentDictionary<string, AdapterDef> _unloadedDefs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadGates = new(StringComparer.Ordinal);
    // Manifest load is shared via a single Task so concurrent callers
    // never observe the half-loaded state. Cancellation passed in by
    // any one caller is intentionally ignored on the shared load (the
    // Task uses CancellationToken.None) — otherwise one caller could
    // poison the load for everyone else.
    private Task? _manifestLoadTask;
    private readonly object _manifestLoadLock = new();
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _invokeGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    // Tracked outside the Lazy<Task<>> so DisposeAsync can tear down a
    // partially-constructed engine even when the boot Task ends faulted.
    private V8ScriptEngine? _engineInstance;

    private sealed record ManifestEntry(string Site, string Name, JsonObject Raw, string? ModulePath);

    public string ClisDir => _clisDir;
    public string UpstreamSha { get; private set; } = "unknown";

    public OpenCliRuntime(string clisDir, string manifestPath, HttpClient http, ILogger<OpenCliRuntime>? log = null)
    {
        _clisDir = clisDir;
        _manifestPath = manifestPath;
        _http = http;
        _log = log;
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

    /// <summary>SPEC §4.1 — return the registry sorted by site/name.
    /// Reads from cli-manifest.json (cheap, no V8) so callers see every
    /// adapter without paying the JS load cost. <see cref="AdapterDef.Func"/>
    /// is null for entries that haven't been opened yet.</summary>
    public async Task<IReadOnlyList<AdapterDef>> ListAsync(CancellationToken ct = default)
    {
        await EnsureManifestLoadedAsync(ct).ConfigureAwait(false);
        return _manifest.Values
            .Select(e => _registry.TryGetValue(e.Site + "/" + e.Name, out var def)
                ? def
                : _unloadedDefs.GetOrAdd(e.Site + "/" + e.Name, _ => ManifestEntryToDef(e)))
            .OrderBy(d => d.Site, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Resolve metadata only — does NOT load the adapter .js.
    /// Use <see cref="InvokeAsync"/> when you need the func closure too.</summary>
    public async Task<AdapterDef?> Resolve(string site, string name, CancellationToken ct = default)
    {
        await EnsureManifestLoadedAsync(ct).ConfigureAwait(false);
        var key = site + "/" + name;
        if (_registry.TryGetValue(key, out var loaded)) return loaded;
        if (_manifest.TryGetValue(key, out var entry))
            return _unloadedDefs.GetOrAdd(key, _ => ManifestEntryToDef(entry));
        return null;
    }

    private Task EnsureManifestLoadedAsync(CancellationToken ct)
    {
        // Shared in-flight Task — concurrent callers all await the same
        // load and observe a fully-populated _manifest before continuing.
        // The shared load itself uses CancellationToken.None so a single
        // caller's cancellation cannot poison the result for others.
        Task? task;
        lock (_manifestLoadLock)
        {
            if (_manifestLoadTask is { IsCompletedSuccessfully: true }) return Task.CompletedTask;
            if (_manifestLoadTask is null || _manifestLoadTask.IsFaulted || _manifestLoadTask.IsCanceled)
            {
                _manifestLoadTask = Task.Run(() => LoadManifestAsync(CancellationToken.None));
            }
            task = _manifestLoadTask;
        }
        return task.WaitAsync(ct);
    }

    private async Task LoadManifestAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!File.Exists(_manifestPath))
        {
            _log?.LogWarning("opencli: manifest not found at {Path}", _manifestPath);
            return;
        }
        // UPSTREAM_SHA sits next to the manifest.
        try
        {
            var shaPath = Path.Combine(Path.GetDirectoryName(_manifestPath) ?? _clisDir, "UPSTREAM_SHA");
            if (File.Exists(shaPath)) UpstreamSha = (await File.ReadAllTextAsync(shaPath, ct).ConfigureAwait(false)).Trim();
        }
        catch { /* best-effort */ }

        await using var fs = File.OpenRead(_manifestPath);
        var doc = await JsonNode.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
        var arr = doc as JsonArray ?? (doc as JsonObject)?["commands"] as JsonArray;
        if (arr is null)
        {
            _log?.LogWarning("opencli: manifest at {Path} has unexpected shape", _manifestPath);
            return;
        }
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var site = o["site"]?.GetValue<string>();
            var name = o["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(name)) continue;
            var modulePath = o["modulePath"]?.GetValue<string>() ?? o["sourceFile"]?.GetValue<string>();
            _manifest[site + "/" + name] = new ManifestEntry(site, name, (JsonObject)o.DeepClone(), modulePath);
        }
        _log?.LogInformation("opencli manifest loaded in {Ms}ms entries={Count} sha={Sha} clisDir={ClisDir}",
            sw.ElapsedMilliseconds, _manifest.Count, UpstreamSha, _clisDir);
    }

    private static AdapterDef ManifestEntryToDef(ManifestEntry e)
    {
        var meta = e.Raw;
        IReadOnlyList<string>? aliases = null;
        if (meta["aliases"] is JsonArray al)
        {
            var list = new List<string>(al.Count);
            foreach (var v in al) if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) list.Add(s);
            if (list.Count > 0) aliases = list;
        }
        return new AdapterDef(
            site: e.Site,
            name: e.Name,
            description: meta["description"]?.GetValue<string>() ?? "",
            strategy: (meta["strategy"]?.GetValue<string>() ?? "public").Trim().ToLowerInvariant(),
            browser: TryBoolFromManifest(meta, "browser") ?? false,
            access: meta["access"]?.GetValue<string?>(),
            domain: meta["domain"]?.GetValue<string?>(),
            aliases: aliases,
            args: (meta["args"] as JsonArray)?.DeepClone() as JsonArray,
            columns: (meta["columns"] as JsonArray)?.DeepClone() as JsonArray,
            func: null, // not loaded yet
            pipeline: meta["pipeline"]?.DeepClone());
    }

    private static bool? TryBoolFromManifest(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    /// <summary>Lazy-load one adapter's .js so its func closure lands in
    /// the live registry. Idempotent and per-adapter-serialised so two
    /// concurrent opencli_run calls on the same site/name don't race the
    /// Execute. Returns the loaded def, or throws if the .js fails.</summary>
    private async Task<AdapterDef> EnsureAdapterLoadedAsync(string site, string name, CancellationToken ct)
    {
        var key = site + "/" + name;
        if (_registry.TryGetValue(key, out var existing) && existing.Func is not null) return existing;

        if (!_manifest.TryGetValue(key, out var entry))
            throw new InvalidOperationException($"adapter {key} not in manifest");

        var gate = _loadGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_registry.TryGetValue(key, out existing) && existing.Func is not null) return existing;

            var rel = entry.ModulePath;
            if (string.IsNullOrEmpty(rel))
                rel = $"{site}/{name}.js";
            var path = Path.Combine(_clisDir, rel);
            // Containment guard — manifest.modulePath is untrusted-ish
            // (it lives in 3rd/opencli/cli-manifest.json which we vendor,
            // but a poisoned or hand-edited manifest could absolute-path
            // its way out). Path.Combine of (".../clis", "/etc/passwd")
            // happily returns "/etc/passwd"; canonicalise first.
            var canonical = Path.GetFullPath(path);
            var clisCanonical = Path.GetFullPath(_clisDir);
            var clisWithSep = clisCanonical.EndsWith(Path.DirectorySeparatorChar)
                ? clisCanonical : clisCanonical + Path.DirectorySeparatorChar;
            var pathCmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!canonical.StartsWith(clisWithSep, pathCmp))
                throw new UnauthorizedAccessException(
                    $"adapter {key}: manifest.modulePath '{rel}' resolves outside _clisDir ({clisCanonical})");
            if (!File.Exists(canonical))
                throw new FileNotFoundException($"adapter source not found: {canonical}", canonical);

            var engine = await GetEngineTask().ConfigureAwait(false);
            var src = await File.ReadAllTextAsync(canonical, ct).ConfigureAwait(false);
            var info = new DocumentInfo(new Uri(canonical)) { Category = ModuleCategory.Standard };
            // Serialize against in-flight invokes — V8 isolate is single-
            // threaded and concurrent Execute against another InvokeAsync's
            // _invokeGate-held call would corrupt the script state machine.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            await _invokeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                engine.Execute(info, src);
            }
            finally
            {
                _invokeGate.Release();
            }

            // The shim's cli({...}) callback writes into _registry.
            if (_registry.TryGetValue(key, out var loaded))
            {
                // Pipeline-only adapter: synthesise a func that delegates
                // to the vendored upstream pipeline runner. Adapter never
                // sees a func itself; we just need the C# side to have
                // a callable handle.
                if (loaded.Func is null && loaded.Pipeline is not null)
                {
                    await EnsurePipelineRunnerLoadedAsync(engine).ConfigureAwait(false);
                    // CRITICAL: bake the pipeline config into a per-adapter
                    // closure via a factory, NOT a shared global. A previous
                    // version stashed the JSON on `__opencliPipelineJson`
                    // and every synthesised func re-read that global —
                    // so all pipeline-only adapters ended up running
                    // whichever pipeline was loaded LAST.
                    using var linkedSynth = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                    await _invokeGate.WaitAsync(linkedSynth.Token).ConfigureAwait(false);
                    ScriptObject? synth;
                    try
                    {
                        engine.Script.__opencliFactoryArg = loaded.Pipeline.ToJsonString();
                        engine.Script.__opencliFactoryNeedsBrowser = loaded.Browser;
                        // The upstream pipeline `fetch` step branches on
                        // `page === null` — when null, it uses the global
                        // `fetch` shim; otherwise it calls `page.fetchJson`.
                        // For PUBLIC (non-browser) pipeline adapters we
                        // MUST pass null, otherwise the C# Phase1StubPage
                        // gets called as `page.fetchJson(...)` and
                        // ClearScript throws `NoExplicitConv` (no such
                        // method on the host object). Bake the
                        // browser-vs-public flag into each per-adapter
                        // closure.
                        engine.Execute("""
                            globalThis.__opencliFactoryResult = (function (jsonStr, needsBrowser) {
                                const cfg = JSON.parse(jsonStr);
                                const runner = globalThis.__opencliPipelineRunner;
                                // Synthesised func mirrors upstream
                                // signature (page, args).
                                return (page, args) => runner.executePipeline(
                                    needsBrowser ? page : null,
                                    cfg,
                                    { args: args ?? {} });
                            })(__opencliFactoryArg, __opencliFactoryNeedsBrowser);
                        """);
                        synth = engine.Script.__opencliFactoryResult as ScriptObject;
                        try
                        {
                            engine.Script.__opencliFactoryArg = null;
                            engine.Script.__opencliFactoryNeedsBrowser = null;
                            engine.Script.__opencliFactoryResult = null;
                        }
                        catch { }
                    }
                    finally
                    {
                        _invokeGate.Release();
                    }
                    if (synth is null)
                        throw new InvalidOperationException($"adapter {key}: failed to synthesise pipeline func");
                    var withFunc = new AdapterDef(
                        site: loaded.Site, name: loaded.Name, description: loaded.Description,
                        strategy: loaded.Strategy, browser: loaded.Browser, access: loaded.Access,
                        domain: loaded.Domain, aliases: loaded.Aliases,
                        args: loaded.Args, columns: loaded.Columns,
                        func: synth, pipeline: loaded.Pipeline);
                    _registry[key] = withFunc;
                    // The unloaded snapshot is now stale — drop it so the
                    // JSON clones it carries can be GC'd.
                    _unloadedDefs.TryRemove(key, out _);
                    return withFunc;
                }
                if (loaded.Func is not null)
                {
                    _unloadedDefs.TryRemove(key, out _);
                    return loaded;
                }
            }
            throw new InvalidOperationException($"adapter {key} did not register after load");
        }
        finally
        {
            gate.Release();
        }
    }

    private Task? _pipelineRunnerLoadTask;
    private readonly object _pipelineRunnerLock = new();

    private Task EnsurePipelineRunnerLoadedAsync(V8ScriptEngine engine)
    {
        // Cache the load Task itself so concurrent callers all await the
        // same import. A failed load can be retried by the next caller —
        // we don't poison the slot.
        Task t;
        lock (_pipelineRunnerLock)
        {
            if (_pipelineRunnerLoadTask is { IsCompletedSuccessfully: true }) return Task.CompletedTask;
            if (_pipelineRunnerLoadTask is null || _pipelineRunnerLoadTask.IsFaulted || _pipelineRunnerLoadTask.IsCanceled)
            {
                _pipelineRunnerLoadTask = LoadPipelineRunnerAsync(engine);
            }
            t = _pipelineRunnerLoadTask;
        }
        return t;
    }

    private async Task LoadPipelineRunnerAsync(V8ScriptEngine engine)
    {
        // Surface import errors via a sentinel global so the polling loop
        // doesn't have to depend on the fire-and-forget promise reaching us.
        await _invokeGate.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
        try
        {
            engine.Execute("""
                (async () => {
                    try {
                        const m = await import('@jackwener/opencli/pipeline');
                        globalThis.__opencliPipelineRunner = m;
                    } catch (e) {
                        globalThis.__opencliPipelineRunnerError = String((e && e.message) || e);
                    }
                })();
            """);
        }
        finally
        {
            _invokeGate.Release();
        }
        // ClearScript reads undefined globals as Microsoft.ClearScript.Undefined.Value,
        // NOT as C# null — `is not null` would always be true, so the
        // poll would return on iteration 1 with the runner still missing.
        // Check the type explicitly.
        for (int i = 0; i < 500; i++)
        {
            var runner = engine.Script.__opencliPipelineRunner;
            if (runner is not null && runner is not Undefined) return;
            var errVal = engine.Script.__opencliPipelineRunnerError;
            if (errVal is string err)
                throw new InvalidOperationException($"pipeline runner import failed: {err}");
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new InvalidOperationException("pipeline runner failed to load within 5s (module @jackwener/opencli/pipeline missing from vendored runtime?)");
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
        await EnsureManifestLoadedAsync(ct).ConfigureAwait(false);
        var key = site + "/" + name;
        if (!_manifest.TryGetValue(key, out var manifestEntry))
        {
            return Failure(site, name, "RUNTIME_NOT_FOUND", $"adapter {key} not in manifest", sw);
        }
        // Pipeline-only adapters used to short-circuit; now they go
        // through EnsureAdapterLoadedAsync which synthesises a func that
        // delegates to the vendored upstream pipeline runner.
        AdapterDef def;
        try
        {
            def = await EnsureAdapterLoadedAsync(site, name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(site, name, "ADAPTER_LOAD_FAILED", ex.Message, sw);
        }
        if (def.Func is null)
        {
            return Failure(site, name, "RUNTIME_PIPELINE_ONLY",
                "adapter has no func after load (likely pipeline-only)", sw);
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
            var engine = await GetEngineTask().ConfigureAwait(false);
            try
            {
                engine.Script.__opencliPage = page;
                engine.Script.__opencliArgs = args.DeepClone().ToJsonString();
                engine.Script.__opencliFn = def.Func;
                engine.Script.__opencliFnBrowser = def.Browser;
                // SPEC upstream: manifest.navigateBefore is the URL the
                // adapter framework must navigate the browser tab to
                // BEFORE calling the adapter func. Cookie-tier adapters
                // (reddit/*, bilibili/*, ...) rely on this so their
                // relative `fetch('/api/...')` calls hit the right origin
                // with the user's logged-in cookies.
                string? navBefore = null;
                if (manifestEntry.Raw.TryGetPropertyValue("navigateBefore", out var nb) &&
                    nb is JsonValue nbv && nbv.TryGetValue<string>(out var nbs) &&
                    !string.IsNullOrEmpty(nbs))
                {
                    navBefore = nbs;
                }
                engine.Script.__opencliNavigateBefore = navBefore;
                // Serialise the result inside the IIFE so JSON.stringify
                // errors (circular structures, Date / BigInt) surface as
                // adapter exceptions rather than silently flipping a
                // successful run into RUNTIME_SCRIPT_ERROR after the fact.
                // JSON.parse on the args is inside the inner try so a
                // malformed serialisation surfaces through __opencliCallError
                // rather than masquerading as a clean `{data:null,ok:true}`.
                // Use globalThis assignments (not `let`) so a second call
                // doesn't trip "Identifier has already been declared" — V8
                // keeps top-level `let` bindings alive across Execute() calls
                // on the same engine.
                engine.Execute("""
                    globalThis.__opencliCallResultJson = null;
                    globalThis.__opencliCallError = null;
                    // Wrap the host IPage as a JS-shaped object — C# methods
                    // come through as PascalCase (page.Goto), but upstream
                    // adapters call camelCase (page.goto). Re-export each
                    // entry as a forwarding function so both names work.
                    // Stash on globalThis with `||=` so we install the
                    // wrapper exactly once — top-level `const` declarations
                    // persist across engine.Execute() calls and would throw
                    // 'Identifier already declared' on the second invoke.
                    globalThis.__wrapPage = globalThis.__wrapPage || ((host) => {
                        if (host === null || host === undefined) return host;
                        // Reflect the C# surface: every PascalCase method becomes
                        // a camelCase alias that forwards to it. We can't easily
                        // enumerate host members in JS, so map the methods we
                        // know IPage exposes (matches IPage.cs).
                        const map = {
                            autoScroll: 'AutoScroll',
                            cdp: 'Cdp',
                            click: 'Click',
                            closeWindow: 'CloseWindow',
                            evaluate: 'Evaluate',
                            evaluateWithArgs: 'EvaluateWithArgs',
                            find: 'Find',
                            getCookies: 'GetCookies',
                            getCurrentUrl: 'GetCurrentUrl',
                            getInterceptedRequests: 'GetInterceptedRequests',
                            goto: 'Goto',
                            insertText: 'InsertText',
                            installInterceptor: 'InstallInterceptor',
                            keys: 'Keys',
                            nativeClick: 'NativeClick',
                            nativeKeyPress: 'NativeKeyPress',
                            nativeType: 'NativeType',
                            pressKey: 'PressKey',
                            readNetworkCapture: 'ReadNetworkCapture',
                            screenshot: 'Screenshot',
                            selectTab: 'SelectTab',
                            setFileInput: 'SetFileInput',
                            snapshot: 'Snapshot',
                            startNetworkCapture: 'StartNetworkCapture',
                            tabs: 'Tabs',
                            type: 'Type',
                            wait: 'Wait',
                            waitForCapture: 'WaitForCapture',
                            waitForTimeout: 'WaitForTimeout',
                        };
                        // JSON-shape methods (return C# JsonNode?): the host
                        // reference has no JS-visible fields. JSON round-trip
                        // to a plain JS object so adapter code can `result.kind`.
                        const jsonReturns = new Set([
                            'evaluate', 'evaluateWithArgs', 'cdp', 'find',
                            'getCookies', 'getInterceptedRequests', 'readNetworkCapture',
                            'installInterceptor', 'startNetworkCapture', 'waitForCapture',
                            'snapshot', 'tabs', 'setFileInput', 'nativeClick',
                            'nativeKeyPress', 'nativeType', 'screenshot',
                        ]);
                        const proxy = {};
                        for (const [js, cs] of Object.entries(map)) {
                            if (jsonReturns.has(js)) {
                                proxy[js] = async (...args) => {
                                    const r = await host[cs](...args);
                                    if (r === null || r === undefined) return r;
                                    // r is a C# JsonNode host reference — its
                                    // ToString() is the compact JSON. Round-trip
                                    // to a plain JS value.
                                    // JsonNode.ToJsonString() always returns valid JSON
                                    // (unlike ToString() which wraps primitives in quotes).
                                    try { return JSON.parse(r.ToJsonString()); }
                                    catch { return r; }
                                };
                            } else {
                                proxy[js] = (...args) => host[cs](...args);
                            }
                        }
                        return proxy;
                    });
                    globalThis.__opencliCallPromise = (async () => {
                        try {
                            const cargs = JSON.parse(globalThis.__opencliArgs);
                            const cpage = globalThis.__wrapPage(globalThis.__opencliPage);
                            // navigateBefore: some adapters (cookie-tier)
                            // require the tab to be on their origin so
                            // relative `fetch('/api/...')` works with the
                            // logged-in cookies. Best-effort — swallow
                            // errors so a stray failure here doesn't hide
                            // the adapter's own errors.
                            if (globalThis.__opencliNavigateBefore && cpage && typeof cpage.goto === 'function') {
                                try { await cpage.goto(globalThis.__opencliNavigateBefore); }
                                catch (e) { try { __opencliHost.warn('navigateBefore failed: ' + (e && e.message || e)); } catch {} }
                            }
                            // Upstream func signatures observed in v1.8.5:
                            //   async (page, args)  — browser=true adapters
                            //   async (page)        — browser=true, no args
                            //   async (args)        — browser=false PUBLIC
                            //   async ()            — env-driven
                            // The .length arity tells us how many, and
                            // def.browser tells us WHICH single-arg role.
                            const fn = globalThis.__opencliFn;
                            const arity = fn.length;
                            const isBrowser = globalThis.__opencliFnBrowser === true;
                            const r = arity >= 2 ? await fn(cpage, cargs)
                                    : arity === 1
                                        ? (isBrowser ? await fn(cpage) : await fn(cargs))
                                        : await fn();
                            globalThis.__opencliCallResultJson = JSON.stringify(r === undefined ? null : r);
                        } catch (e) {
                            const code = (e && e.code != null) ? String(e.code) : 'RUNTIME_ERROR';
                            const msg  = (e && e.message != null) ? String(e.message) : String(e);
                            globalThis.__opencliCallError = { code, message: msg };
                        }
                    })();
                """);
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
            V8ScriptEngineFlags.EnableDynamicModuleImports |
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
        // Vendored upstream runtime tree — for now we know it sits one
        // level up from clisDir. The pipeline runner + its sibling
        // `errors`/`utils`/`logger`/`interceptor` modules live here.
        var runtimeDir = Path.GetFullPath(Path.Combine(_clisDir, "..", "runtime"));

        // File-route map: bare specifier → real .js on disk. Resolves
        // adapter-side `import { CliError } from '@jackwener/opencli/errors'`
        // and pipeline-side `from '../../errors.js'` to the SAME module
        // instance, so instanceof checks across the boundary work.
        var fileRoutes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(runtimeDir))
        {
            string Rt(params string[] parts) => Path.Combine(new[] { runtimeDir }.Concat(parts).ToArray());
            void Add(string specifier, string path) { if (File.Exists(path)) fileRoutes[specifier] = path; }
            Add("@jackwener/opencli/errors",    Rt("errors.js"));
            Add("@jackwener/opencli/utils",     Rt("utils.js"));
            Add("@jackwener/opencli/logger",    Rt("logger.js"));
            Add("@jackwener/opencli/pipeline",  Rt("pipeline", "index.js"));
            Add("@jackwener/opencli/interceptor", Rt("interceptor.js"));
        }

        var loader = new OpenCliDocumentLoader(_clisDir,
            shims: new Dictionary<string, string>
            {
                // Registry shim is hand-rolled — it bridges cli({...}) →
                // __opencliHost.register, which the vendored upstream
                // file does not do.
                ["@jackwener/opencli/registry"]                  = HostShim.RegistrySource,
                // Fallback shims if the vendored runtime tree is absent:
                // these only kick in when the corresponding fileRoute
                // didn't land (e.g. publish output missing 3rd/opencli/runtime).
                ["@jackwener/opencli/launcher"]                  = HostShim.LauncherSource,
                ["@jackwener/opencli/download"]                  = HostShim.DownloadSource,
                ["@jackwener/opencli/download/article-download"] = HostShim.DownloadSource,
                ["@jackwener/opencli/download/media-download"]   = HostShim.DownloadSource,
                ["@jackwener/opencli/download/progress"]         = HostShim.DownloadSource,
                ["@jackwener/opencli/browser/cdp"]               = HostShim.BrowserShimSource,
                ["@jackwener/opencli/browser/page"]              = HostShim.BrowserShimSource,
                ["@jackwener/opencli/browser/utils"]             = HostShim.BrowserShimSource,
                // Inline fallbacks for these — fileRoutes will override
                // when the vendored tree is present.
                ["@jackwener/opencli/errors"]                    = HostShim.ErrorsSource,
                ["@jackwener/opencli/utils"]                     = HostShim.UtilsSource,
                ["@jackwener/opencli/logger"]                    = HostShim.LoggerSource,
                // Node built-ins — both `node:foo` and bare `foo`.
                ["node:path"] = HostShim.NodePathSource,            ["path"] = HostShim.NodePathSource,
                ["node:os"]   = HostShim.NodeOsSource,              ["os"]   = HostShim.NodeOsSource,
                ["node:crypto"] = HostShim.NodeCryptoSource,        ["crypto"] = HostShim.NodeCryptoSource,
                ["node:fs"] = HostShim.NodeFsSource,                ["fs"] = HostShim.NodeFsSource,
                ["node:fs/promises"] = HostShim.NodeFsSource,
                ["node:child_process"] = HostShim.NodeChildProcessSource, ["child_process"] = HostShim.NodeChildProcessSource,
                ["node:http"]  = HostShim.NodeHttpSource,           ["http"]  = HostShim.NodeHttpSource,
                ["node:https"] = HostShim.NodeHttpSource,           ["https"] = HostShim.NodeHttpSource,
                ["node:vm"] = HostShim.NodeVmSource,                ["vm"] = HostShim.NodeVmSource,
            },
            fileRoutes: fileRoutes,
            extraRoots: new[] { runtimeDir });
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
            // V8 isolate doesn't ship URL / URLSearchParams as globals
            // (they're WHATWG specs, not part of ES2024). Many adapters
            // build query strings via `new URL(...)` / `new URLSearchParams(...)`.
            // V8 isolate ships no setTimeout/setInterval by default —
            // those are host runtime APIs. Adapters that reach for
            // them (either directly, or via AbortSignal.timeout) get
            // 'setTimeout is not defined'. Route to the .NET host
            // helper so Node-shaped code works.
            globalThis.setTimeout = globalThis.setTimeout || function (fn, ms, ...a) {
                return __opencliHost.scheduleTimer(fn, Number(ms) || 0, a || null, false);
            };
            globalThis.setInterval = globalThis.setInterval || function (fn, ms, ...a) {
                return __opencliHost.scheduleTimer(fn, Number(ms) || 0, a || null, true);
            };
            globalThis.clearTimeout = globalThis.clearTimeout || function (id) {
                __opencliHost.cancelTimer(id);
            };
            globalThis.clearInterval = globalThis.clearInterval || function (id) {
                __opencliHost.cancelTimer(id);
            };
            globalThis.queueMicrotask = globalThis.queueMicrotask || function (fn) {
                Promise.resolve().then(fn);
            };
            // V8 isolate default globals miss AbortController/AbortSignal
            // (WHATWG DOM). Ship a minimal shim — timers only,
            // .aborted flag, .reason. Enough for adapters that pass
            // `signal: AbortSignal.timeout(ms)` into fetch. Real
            // abort semantics (canceling in-flight requests) are
            // covered by HostShim.fetchAsync's own 30s timeout.
            globalThis.AbortController = globalThis.AbortController || class AbortController {
                constructor() {
                    this.signal = new globalThis.AbortSignal();
                }
                abort(reason) {
                    this.signal._abort(reason);
                }
            };
            globalThis.AbortSignal = globalThis.AbortSignal || class AbortSignal {
                constructor() {
                    this.aborted = false;
                    this.reason = undefined;
                    this._listeners = [];
                }
                _abort(reason) {
                    if (this.aborted) return;
                    this.aborted = true;
                    this.reason = reason;
                    for (const cb of this._listeners) { try { cb(); } catch {} }
                }
                addEventListener(_event, cb) { this._listeners.push(cb); }
                removeEventListener(_event, cb) { this._listeners = this._listeners.filter(l => l !== cb); }
                static timeout(ms) {
                    const sig = new globalThis.AbortSignal();
                    setTimeout(() => sig._abort(new Error(`AbortSignal.timeout: ${ms}ms elapsed`)), ms);
                    return sig;
                }
                static abort(reason) {
                    const sig = new globalThis.AbortSignal();
                    sig._abort(reason);
                    return sig;
                }
            };
            globalThis.URLSearchParams = globalThis.URLSearchParams || class URLSearchParams {
                constructor(init) {
                    this._params = [];
                    if (!init) return;
                    if (typeof init === 'string') {
                        const s = init.startsWith('?') ? init.slice(1) : init;
                        for (const pair of s.split('&').filter(Boolean)) {
                            const eq = pair.indexOf('=');
                            const k = eq < 0 ? pair : pair.slice(0, eq);
                            const v = eq < 0 ? '' : pair.slice(eq + 1);
                            this._params.push([decodeURIComponent(k.replace(/\+/g, ' ')), decodeURIComponent(v.replace(/\+/g, ' '))]);
                        }
                    } else if (Array.isArray(init)) {
                        for (const [k, v] of init) this._params.push([String(k), String(v)]);
                    } else if (typeof init === 'object') {
                        for (const k of Object.keys(init)) this._params.push([k, String(init[k])]);
                    }
                }
                append(k, v) { this._params.push([String(k), String(v)]); }
                delete(k) { this._params = this._params.filter(p => p[0] !== k); }
                get(k) { const p = this._params.find(p => p[0] === k); return p ? p[1] : null; }
                getAll(k) { return this._params.filter(p => p[0] === k).map(p => p[1]); }
                has(k) { return this._params.some(p => p[0] === k); }
                set(k, v) { this.delete(k); this.append(k, v); }
                forEach(fn) { for (const [k, v] of this._params) fn(v, k, this); }
                keys() { return this._params.map(p => p[0])[Symbol.iterator](); }
                values() { return this._params.map(p => p[1])[Symbol.iterator](); }
                entries() { return this._params.slice()[Symbol.iterator](); }
                [Symbol.iterator]() { return this.entries(); }
                toString() {
                    return this._params
                        .map(([k, v]) => encodeURIComponent(k) + '=' + encodeURIComponent(v))
                        .join('&');
                }
            };
            globalThis.URL = globalThis.URL || class URL {
                constructor(url, base) {
                    const parsed = __opencliHost.parseUrl(String(url), base ? String(base) : null);
                    if (parsed.error) throw new TypeError('Invalid URL: ' + url);
                    this.href = parsed.href;
                    this.origin = parsed.origin;
                    this.protocol = parsed.protocol;
                    this.host = parsed.host;
                    this.hostname = parsed.hostname;
                    this.port = parsed.port;
                    this.pathname = parsed.pathname;
                    this.search = parsed.search;
                    this.hash = parsed.hash;
                    this.searchParams = new globalThis.URLSearchParams(parsed.search);
                }
                toString() { return this.href; }
                toJSON() { return this.href; }
            };
            // Wrap host FetchResponse so json() returns a NATIVE JS value
            // via V8's JSON.parse, not a .NET JsonNode (host object that
            // Array.isArray / Object.entries / .map can't see through).
            globalThis.fetch = async (url, init) => {
                // Coerce URL objects / anything-toString-able to string —
                // C# HostShim.fetchAsync takes `string url` and would
                // otherwise fail with 'BadArgTypes' on `new URL(...)`.
                const hostResp = await __opencliHost.fetchAsync(String(url), init || null);
                const hostText = await hostResp.text();
                return {
                    ok: hostResp.ok,
                    status: hostResp.status,
                    statusText: hostResp.statusText,
                    headers: hostResp.headers,
                    text: async () => hostText,
                    json: async () => JSON.parse(hostText),
                };
            };
            globalThis.console = { log: (...a) => __opencliHost.warn(a.map(String).join(' ')),
                                   warn: (...a) => __opencliHost.warn(a.map(String).join(' ')),
                                   error: (...a) => __opencliHost.warn(a.map(String).join(' ')),
                                   debug: () => {}, info: () => {} };
            // Stub Node `process` global so adapters / vendored runtime
            // that reach for process.env.* / process.stderr.write don't
            // ReferenceError. We honour a small allowlist of env vars via
            // the host helper; everything else returns undefined.
            globalThis.process = {
                env: new Proxy({}, { get: (_t, k) => __opencliHost.getEnv(String(k)) }),
                stderr: { write: (s) => __opencliHost.warn(String(s)) },
                stdout: { write: (s) => __opencliHost.warn(String(s)) },
                platform: __opencliHost.osPlatform(),
                arch: __opencliHost.osArch(),
                versions: { node: '20.0.0' },
                cwd: () => '/',
                exit: (code) => { throw new Error('process.exit(' + code + ') called'); },
                nextTick: (fn) => Promise.resolve().then(fn),
            };
        """);

        // Adapters are NOT loaded eagerly — EnsureAdapterLoadedAsync
        // pulls each .js on first opencli_run for that site/name. Boot
        // remains under 100ms.
        engine.CollectGarbage(true);

        try
        {
            var shaPath = Path.Combine(Path.GetDirectoryName(_manifestPath) ?? _clisDir, "UPSTREAM_SHA");
            if (File.Exists(shaPath))
                UpstreamSha = (await File.ReadAllTextAsync(shaPath, ct).ConfigureAwait(false)).Trim();
        }
        catch (OperationCanceledException) { /* dispose-time cancellation is fine */ }
        catch (Exception ex) { _log?.LogDebug(ex, "opencli: UPSTREAM_SHA read failed"); }

        _log?.LogInformation("opencli runtime booted in {Ms}ms (lazy-load mode) sha={Sha} clisDir={ClisDir}",
            sw.ElapsedMilliseconds, UpstreamSha, _clisDir);
        return engine;
    }

    private static AdapterDef AdapterFromMeta(string id, JsonObject meta, ScriptObject? func)
    {
        var siteName = id.Split('/', 2);
        var site = TryString(meta, "site") ?? siteName[0];
        var name = TryString(meta, "name") ?? (siteName.Length > 1 ? siteName[1] : id);
        // Clone JSON nodes so the cached AdapterDef owns detachable
        // copies; otherwise sharing parents with `meta` produces
        // "node already has a parent" the first time we re-emit them.
        return new AdapterDef(
            site: site,
            name: name,
            description: TryString(meta, "description") ?? "",
            strategy: (TryString(meta, "strategy") ?? "public").Trim().ToLowerInvariant(),
            browser: TryBool(meta, "browser") ?? false,
            access: TryString(meta, "access"),
            domain: TryString(meta, "domain"),
            aliases: TryStringArray(meta, "aliases"),
            args: (meta["args"] as JsonArray)?.DeepClone() as JsonArray,
            columns: (meta["columns"] as JsonArray)?.DeepClone() as JsonArray,
            func: func,
            pipeline: meta["pipeline"]?.DeepClone());
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
        // Wait for the invoke gate before disposing V8 / the gate itself,
        // otherwise an in-flight InvokeAsync would (a) execute JS against
        // a disposed isolate, or (b) call _invokeGate.Release() on a
        // disposed semaphore in its finally block.
        try
        {
            // If the wait times out we still proceed to dispose — the
            // logged warning surfaces the rare case where an in-flight
            // adapter is wedged in V8.
            var acquired = await _invokeGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!acquired)
                _log?.LogWarning("opencli runtime: invoke gate did not drain within 5s; disposing engine while a JS call may be in-flight");
        }
        catch (ObjectDisposedException) { /* already disposed */ }
        catch (Exception ex) { _log?.LogDebug(ex, "opencli runtime: gate wait failed"); }

        var engine = _engineInstance;
        if (engine is not null)
        {
            try { engine.Dispose(); }
            catch (Exception ex) { _log?.LogDebug(ex, "opencli runtime dispose failed"); }
            _engineInstance = null;
        }
        else if (_engineTask is { IsCompletedSuccessfully: true })
        {
            try { _engineTask!.Result.Dispose(); }
            catch (Exception ex) { _log?.LogDebug(ex, "opencli runtime dispose failed"); }
        }

        // Dispose accumulated per-adapter load gates so their lazy wait
        // handles release.
        foreach (var gate in _loadGates.Values)
        {
            try { gate.Dispose(); } catch { }
        }
        _loadGates.Clear();

        try { _invokeGate.Dispose(); } catch { }
        _disposeCts.Dispose();
    }
}
