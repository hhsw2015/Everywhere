using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenDia;

namespace Everywhere.Mcp.OpenCli;

/// <summary>
/// SPEC §8 Phase 2 — concrete <see cref="IPage"/> that routes every
/// adapter <c>page.*</c> call through <see cref="OpenDiaBridge.CallToolAsync"/>.
///
/// Invariant (SPEC §2.1): we MUST NOT synthesise a fallback when the
/// extension is disconnected. <see cref="OpenDiaBridge.CallToolAsync"/>
/// throws <c>OpenDiaToolException("Browser Extension not connected...")</c>
/// in that case; <see cref="OpenCliRuntime"/> wraps it into the
/// <c>{ok:false, error:"opendia-not-connected"}</c> envelope from §2.1.
/// </summary>
public sealed class OpenDiaPageBridge(OpenDiaBridge bridge) : IPage
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(11); // > MaxWaitMs slack
    private const int MaxWaitMs = 10 * 60 * 1000; // 10 min — bigger than any sane adapter sleep

    /// <summary>
    /// Forwards to <see cref="OpenDiaBridge.CallToolAsync"/>, which is the
    /// single source of truth for "extension connected?". We do NOT
    /// pre-check <c>IsConnected</c> — that would be TOCTOU-racy and
    /// drift from the bridge's authoritative error message.
    /// </summary>
    private Task<JsonNode?> Call(string method, JsonObject? args, CancellationToken ct = default) =>
        bridge.CallToolAsync(method, args, TimeoutFor(method), ct);

    private static TimeSpan TimeoutFor(string method) => method switch
    {
        // Long-poll style operations — the browser side blocks until the
        // condition fires. Default 30s is too short.
        "browser_page_wait_for"   => WaitTimeout,
        "browser_network_wait"    => WaitTimeout,
        "browser_evaluate_js"     => TimeSpan.FromMinutes(2),
        "browser_screenshot"      => TimeSpan.FromMinutes(1),
        "browser_auto_scroll"     => TimeSpan.FromMinutes(2),
        "browser_page_navigate"   => TimeSpan.FromMinutes(1),
        _ => DefaultTimeout,
    };

    private static JsonObject Pack(params (string Key, JsonNode? Value)[] kvs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kvs) if (v is not null) o[k] = v;
        return o;
    }

    private static string? AsString(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        // Some browser_* tools wrap a string in {"value":"..."} or {"url":"..."}.
        if (n is JsonObject obj)
        {
            foreach (var key in new[] { "value", "url", "text", "data" })
            {
                if (obj.TryGetPropertyValue(key, out var inner) && inner is JsonValue iv && iv.TryGetValue<string>(out var ins))
                    return ins;
            }
        }
        // No string-shaped payload — return null so callers can distinguish
        // "no result" from "real empty string".
        return null;
    }

    // ---------- SPEC §3.4 guaranteed surface ----------

    public Task Goto(string url, JsonObject? opts = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new ArgumentException($"page.goto: only http(s) urls allowed, got '{url}'");
        return Call("browser_page_navigate", Pack(("url", url), ("options", opts?.DeepClone())));
    }

    public Task<JsonNode?> Evaluate(string js)
    {
        // OpenCLI page.evaluate accepts both expression and full-script
        // forms. Forward `js` verbatim — wrapping it in
        // `(() => { return ({js}); })()` would force expression context
        // and break multi-statement scripts (OCR review #5). The browser
        // tool is responsible for the `eval` semantics.
        ArgumentException.ThrowIfNullOrEmpty(js);
        return Call("browser_evaluate_js", new JsonObject { ["script"] = js });
    }

    public Task<JsonNode?> EvaluateWithArgs(string js, JsonNode? argsNode)
    {
        ArgumentException.ThrowIfNullOrEmpty(js);
        return Call("browser_evaluate_js", Pack(("script", js), ("args", argsNode?.DeepClone())));
    }

    public async Task Wait(JsonNode arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg is JsonValue v)
        {
            // Cover every numeric kind System.Text.Json may parse the
            // value as: long, int, double, decimal.
            if (v.TryGetValue<long>(out var l))    { await DelayClamped(l).ConfigureAwait(false); return; }
            if (v.TryGetValue<int>(out var i))     { await DelayClamped(i).ConfigureAwait(false); return; }
            if (v.TryGetValue<double>(out var msd))
            {
                if (!double.IsFinite(msd))
                    throw new ArgumentException("page.wait: ms must be finite");
                await DelayClamped((long)Math.Round(msd)).ConfigureAwait(false);
                return;
            }
            if (v.TryGetValue<string>(out var sel))
            {
                await WaitForSelectorInternal(sel).ConfigureAwait(false);
                return;
            }
        }
        throw new ArgumentException("page.wait: expected number ms or selector string");
    }

    private static Task DelayClamped(long ms)
    {
        // Clamp instead of throw — Playwright-style adapters often pass
        // `deadline - now` which can be negative when the deadline has
        // already passed; that should be a no-op, not a hard error.
        var clamped = Math.Min(Math.Max(ms, 0), MaxWaitMs);
        return Task.Delay(TimeSpan.FromMilliseconds(clamped));
    }

    private Task<JsonNode?> WaitForSelectorInternal(string selector) =>
        Call("browser_page_wait_for", new JsonObject
        {
            ["condition_type"] = "element_visible",
            ["selector"] = selector,
        });

    public async Task Click(JsonNode refOrSelector, JsonObject? opts = null)
    {
        ArgumentNullException.ThrowIfNull(refOrSelector);
        var args = BuildRefOrSelector(refOrSelector);
        if (opts != null) args["options"] = opts.DeepClone();
        await Call("browser_click", args).ConfigureAwait(false);
    }

    private static JsonObject BuildRefOrSelector(JsonNode refOrSelector)
    {
        var o = new JsonObject();
        if (refOrSelector is JsonValue vs && vs.TryGetValue<string>(out var sel))
            o["selector"] = sel;
        else
            o["ref"] = refOrSelector.DeepClone();
        return o;
    }

    public Task CloseWindow(JsonObject? opts = null) =>
        Call("browser_close_window", opts?.DeepClone() ?? new JsonObject());

    public async Task<string?> Screenshot(JsonObject? opts = null)
    {
        var resp = await Call("browser_screenshot", opts?.DeepClone() ?? new JsonObject()).ConfigureAwait(false);
        return AsString(resp);
    }

    // ---------- SPEC §3.4 tail surface ----------

    public Task<JsonNode?> AutoScroll(JsonObject? opts = null) =>
        Call("browser_auto_scroll", opts?.DeepClone() ?? new JsonObject());

    public Task<JsonNode?> Cdp(string method, JsonObject? args) =>
        Call("browser_cdp", Pack(("method", method), ("params", args?.DeepClone())));

    public Task<JsonNode?> Find(JsonObject opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        return Call("browser_find", opts.DeepClone());
    }

    public Task<JsonNode?> GetCookies(JsonObject? opts = null) =>
        Call("browser_cookies_get", opts?.DeepClone() ?? new JsonObject());

    public async Task<string?> GetCurrentUrl()
    {
        var r = await Call("browser_get_url", new JsonObject()).ConfigureAwait(false);
        return AsString(r);
    }

    public Task<JsonNode?> GetInterceptedRequests(JsonObject? opts = null) =>
        Call("browser_intercepted_get", opts?.DeepClone() ?? new JsonObject());

    public Task InsertText(string text, JsonObject? opts = null) =>
        Call("browser_insert_text", Pack(("text", text), ("options", opts?.DeepClone())));

    public Task InstallInterceptor(JsonObject opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        return Call("browser_interceptor_install", opts.DeepClone());
    }

    public Task Keys(JsonNode keys, JsonObject? opts = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return Call("browser_press", Pack(("key", keys.DeepClone()), ("options", opts?.DeepClone())));
    }

    public Task NativeClick(JsonNode refOrSelector, JsonObject? opts = null)
    {
        ArgumentNullException.ThrowIfNull(refOrSelector);
        var args = BuildRefOrSelector(refOrSelector);
        if (opts != null) args["options"] = opts.DeepClone();
        return Call("browser_native_click", args);
    }

    public Task NativeKeyPress(string key, JsonObject? opts = null) =>
        Call("browser_native_press", Pack(("key", key), ("options", opts?.DeepClone())));

    public Task NativeType(string text, JsonObject? opts = null) =>
        Call("browser_native_type", Pack(("text", text), ("options", opts?.DeepClone())));

    public Task PressKey(string key, JsonObject? opts = null) =>
        Call("browser_press", Pack(("key", key), ("options", opts?.DeepClone())));

    public Task<JsonNode?> ReadNetworkCapture(JsonObject? opts = null) =>
        Call("browser_network_read", opts?.DeepClone() ?? new JsonObject());

    public Task SelectTab(JsonNode tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return Call("browser_tabs_select", Pack(("tab", tab.DeepClone())));
    }

    public Task SetFileInput(JsonNode refOrSelector, JsonNode files)
    {
        ArgumentNullException.ThrowIfNull(refOrSelector);
        ArgumentNullException.ThrowIfNull(files);
        var args = BuildRefOrSelector(refOrSelector);
        args["files"] = files.DeepClone();
        return Call("browser_set_file_input", args);
    }

    public Task<JsonNode?> Snapshot(JsonObject? opts = null) =>
        Call("browser_snapshot", opts?.DeepClone() ?? new JsonObject());

    public Task StartNetworkCapture(JsonObject? opts = null) =>
        Call("browser_network_start", opts?.DeepClone() ?? new JsonObject());

    public Task<JsonNode?> Tabs(JsonObject? opts = null) =>
        Call("browser_tabs", opts?.DeepClone() ?? new JsonObject());

    public Task Type(string text, JsonObject? opts = null) =>
        Call("browser_fill", Pack(("value", text), ("options", opts?.DeepClone())));

    public Task<JsonNode?> WaitForCapture(JsonObject opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        return Call("browser_network_wait", opts.DeepClone());
    }

    public Task WaitForTimeout(int ms) => DelayClamped(ms);
}
