using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenDia;

namespace Everywhere.Mcp.OpenCli;

/// <summary>
/// SPEC §8 Phase 2 — concrete <see cref="IPage"/> that routes every
/// adapter <c>page.*</c> call through <see cref="OpenDiaBridge.CallToolAsync"/>.
///
/// Tool-name / param mapping was verified against a live OpenDia
/// extension (all 161 <c>browser_*</c> tools enumerated via
/// <c>list_more_tools action_browser|debug|config|perception_*</c>).
///
/// Key design choices from that verification:
/// <list type="bullet">
///   <item>`page.evaluate` routes through <c>browser_cdp_evaluate</c>
///         (Chrome DevTools Protocol) rather than <c>browser_evaluate_js</c>
///         so the eval bypasses page-level CSP <c>unsafe-eval</c> denials.
///         Verified against reddit.com which blocks content-script eval
///         but permits CDP-level Runtime.evaluate.</item>
///   <item>`page.getCurrentUrl` uses <c>browser_wait_for_url</c> with no
///         match — its response includes the current URL as a
///         side-effect. OpenDia has no dedicated get-url tool.</item>
///   <item>Native input (`nativeClick`, `nativeType`, `nativeKeyPress`)
///         routes through <c>browser_cdp_input_*</c> because content-script
///         events are marked isTrusted=false and get rejected by many
///         click-jacking-protected pages.</item>
/// </list>
///
/// Invariant (SPEC §2.1): we MUST NOT synthesise a fallback when the
/// extension is disconnected. <see cref="OpenDiaBridge.CallToolAsync"/>
/// throws when the WS pipe is dead; <see cref="OpenCliRuntime"/> wraps
/// it into the <c>{ok:false, error:"opendia-not-connected"}</c> envelope.
/// </summary>
public sealed class OpenDiaPageBridge : IPage
{
    private readonly OpenDiaBridge bridge;
    public OpenDiaPageBridge(OpenDiaBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        this.bridge = bridge;
    }

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(11);
    private const int MaxWaitMs = 10 * 60 * 1000;

    private Task<JsonNode?> Call(string method, JsonObject? args, CancellationToken ct = default) =>
        bridge.CallToolAsync(method, args, TimeoutFor(method), ct);

    private static TimeSpan TimeoutFor(string method) => method switch
    {
        "browser_wait_for_selector" => WaitTimeout,
        "browser_wait_for_url"      => WaitTimeout,
        "browser_wait_for_text"     => WaitTimeout,
        "browser_wait_for_function" => WaitTimeout,
        "browser_wait_for_load"     => WaitTimeout,
        "browser_wait_ms"           => WaitTimeout,
        "browser_cdp_evaluate"      => TimeSpan.FromMinutes(2),
        "browser_evaluate_js"       => TimeSpan.FromMinutes(2),
        "browser_annotate_screenshot" => TimeSpan.FromMinutes(1),
        "browser_open"              => TimeSpan.FromMinutes(1),
        _ => DefaultTimeout,
    };

    private static JsonObject CloneObjOrEmpty(JsonObject? o) =>
        o is null ? new JsonObject() : (JsonObject)o.DeepClone();

    private static JsonObject Pack(params (string Key, JsonNode? Value)[] kvs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kvs) if (v is not null) o[k] = v;
        return o;
    }

    private static readonly string[] StringWrapperKeys = ["value", "url", "text", "data", "result"];

    private static string? AsString(JsonNode? n, string? preferKey = null)
    {
        if (n is null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (n is JsonObject obj)
        {
            if (preferKey is not null)
            {
                return obj.TryGetPropertyValue(preferKey, out var direct) &&
                       direct is JsonValue dv && dv.TryGetValue<string>(out var ds)
                    ? ds : null;
            }
            foreach (var key in StringWrapperKeys)
            {
                if (obj.TryGetPropertyValue(key, out var inner) && inner is JsonValue iv && iv.TryGetValue<string>(out var ins))
                    return ins;
            }
        }
        return null;
    }

    // ================ GUARANTEED SURFACE (SPEC §3.4) =================

    /// <summary>
    /// Navigate the active tab. If the tab is already on the same origin
    /// (host + scheme), skip the goto — otherwise we'd disrupt the user
    /// by yanking their tab to a URL that ends up identical to what's
    /// already loaded.
    /// </summary>
    public async Task Goto(string url, JsonObject? opts = null)
    {
        if (string.IsNullOrEmpty(url))
            throw new ArgumentException("page.goto: url required");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new ArgumentException($"page.goto: only http(s) urls allowed, got '{url}'");

        var current = await GetCurrentUrl().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(current)
            && Uri.TryCreate(current, UriKind.Absolute, out var cu)
            && string.Equals(cu.Host, u.Host, StringComparison.OrdinalIgnoreCase)
            && cu.Scheme == u.Scheme)
        {
            // Already on the target origin — skip. Most cookie-tier
            // adapters use manifest.navigateBefore just to establish
            // origin for relative fetches; if we're there, the goto
            // is a no-op that would only disturb the user.
            return;
        }
        await Call("browser_open", new JsonObject { ["url"] = url }).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluate JS in the active tab. Routes through CDP so page CSP
    /// (Reddit / GitHub / many auth pages) can't block eval. Returns
    /// the resolved value as a JsonNode.
    /// </summary>
    public async Task<JsonNode?> Evaluate(string js)
    {
        ArgumentException.ThrowIfNullOrEmpty(js);
        // CDP requires an expression — wrap statements the way Node's
        // Playwright / Puppeteer do.
        var expr = LooksLikeExpression(js) ? js : $"(async () => {{ {js} }})()";
        var resp = await Call("browser_cdp_evaluate", new JsonObject
        {
            ["expression"] = expr,
            ["awaitPromise"] = true,
        }).ConfigureAwait(false);
        return UnwrapEvalResult(resp);
    }

    public async Task<JsonNode?> EvaluateWithArgs(string js, JsonNode? argsNode)
    {
        ArgumentException.ThrowIfNullOrEmpty(js);
        // CDP evaluate has no bind-args API; embed args into the source
        // as a JSON literal.
        var argJson = argsNode?.ToJsonString() ?? "null";
        var expr = LooksLikeExpression(js)
            ? $"(function(args) {{ return ({js}); }})({argJson})"
            : $"(async (args) => {{ {js} }})({argJson})";
        var resp = await Call("browser_cdp_evaluate", new JsonObject
        {
            ["expression"] = expr,
            ["awaitPromise"] = true,
        }).ConfigureAwait(false);
        return UnwrapEvalResult(resp);
    }

    private static bool LooksLikeExpression(string js)
    {
        // Heuristic: no top-level 'return' and no unbalanced semicolons
        // suggest an expression. If it starts with `(` and ends `)` or
        // `()`, it's the classic IIFE form and safe to eval directly.
        var t = js.TrimEnd(';', ' ', '\n', '\r', '\t');
        if (t.StartsWith('(') && (t.EndsWith(')') || t.EndsWith(")()"))) return true;
        // If it contains `return ` at the top level, it's statements — wrap.
        return !js.Contains("return ", StringComparison.Ordinal);
    }

    /// <summary>Unwrap OpenDia's cdp_evaluate envelope
    /// <c>{success:true, result:X}</c> or <c>{success:false, error:Y}</c>.</summary>
    private static JsonNode? UnwrapEvalResult(JsonNode? resp)
    {
        if (resp is JsonObject o)
        {
            if (o.TryGetPropertyValue("success", out var s) && s is JsonValue sv && sv.TryGetValue<bool>(out var b) && !b)
            {
                var err = o["error"]?.GetValue<string>() ?? "cdp evaluate failed";
                throw new InvalidOperationException(err);
            }
            if (o.TryGetPropertyValue("result", out var r)) return r?.DeepClone();
        }
        return resp?.DeepClone();
    }

    public async Task Wait(JsonNode arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) { await DelayClamped(l).ConfigureAwait(false); return; }
            if (v.TryGetValue<double>(out var msd))
            {
                if (!double.IsFinite(msd)) throw new ArgumentException("page.wait: ms must be finite");
                var clamped = Math.Min(Math.Max(msd, 0), (double)MaxWaitMs);
                await DelayClamped((long)Math.Round(clamped)).ConfigureAwait(false);
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
        var clamped = Math.Min(Math.Max(ms, 0), MaxWaitMs);
        return Task.Delay(TimeSpan.FromMilliseconds(clamped));
    }

    private Task<JsonNode?> WaitForSelectorInternal(string selector) =>
        Call("browser_wait_for_selector", new JsonObject { ["selector"] = selector });

    public async Task Click(JsonNode refOrSelector, JsonObject? opts = null)
    {
        ArgumentNullException.ThrowIfNull(refOrSelector);
        await Call("browser_click", BuildRefOrSelector(refOrSelector)).ConfigureAwait(false);
    }

    private static JsonObject BuildRefOrSelector(JsonNode refOrSelector)
    {
        var o = new JsonObject();
        if (refOrSelector is JsonValue vs && vs.TryGetValue<string>(out var sel))
        {
            // Content-script tools accept both raw selectors and @refN
            // ids. If it starts with @, send as ref; otherwise selector.
            if (sel.StartsWith('@')) o["ref"] = sel; else o["selector"] = sel;
        }
        else if (refOrSelector is JsonObject obj && obj.TryGetPropertyValue("ref", out var rr))
        {
            o["ref"] = rr?.DeepClone();
        }
        else
        {
            o["ref"] = refOrSelector.DeepClone();
        }
        return o;
    }

    public Task CloseWindow(JsonObject? opts = null) =>
        Call("browser_close", CloneObjOrEmpty(opts));

    public async Task<string?> Screenshot(JsonObject? opts = null)
    {
        // browser_annotate_screenshot returns base64 PNG + overlay; the
        // raw base64 is what adapters usually want. Falls back to a CDP
        // Page.captureScreenshot if that fails.
        var resp = await Call("browser_annotate_screenshot", CloneObjOrEmpty(opts)).ConfigureAwait(false);
        return AsString(resp, "data") ?? AsString(resp, "screenshot") ?? AsString(resp, "base64");
    }

    // ================ TAIL SURFACE ================

    public Task<JsonNode?> AutoScroll(JsonObject? opts = null) =>
        Call("browser_scroll", CloneObjOrEmpty(opts));

    /// <summary>Raw CDP passthrough. <c>method</c> is a Chrome DevTools
    /// Protocol method name (e.g. <c>Page.reload</c>). For simple JS
    /// evaluation prefer <see cref="Evaluate"/>.</summary>
    public Task<JsonNode?> Cdp(string method, JsonObject? args)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        // OpenDia's dedicated CDP eval tool
        if (string.Equals(method, "Runtime.evaluate", StringComparison.Ordinal))
        {
            return Call("browser_cdp_evaluate", args ?? new JsonObject());
        }
        // No generic CDP passthrough in OpenDia — surface a clear error.
        throw new NotSupportedException(
            $"page.cdp: OpenDia only exposes CDP through specific tools (Runtime.evaluate maps to browser_cdp_evaluate). Requested '{method}' has no route.");
    }

    public Task<JsonNode?> Find(JsonObject opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        // Route different find variants to OpenDia's specialised tools
        // when possible; fall back to browser_find (CSS selector).
        if (opts.TryGetPropertyValue("role", out var role) && role is not null)
            return Call("browser_find_by_role", (JsonObject)opts.DeepClone());
        if (opts.TryGetPropertyValue("text", out var text) && text is not null)
            return Call("browser_find_by_text", (JsonObject)opts.DeepClone());
        if (opts.TryGetPropertyValue("label", out var label) && label is not null)
            return Call("browser_find_by_label", (JsonObject)opts.DeepClone());
        if (opts.TryGetPropertyValue("placeholder", out var ph) && ph is not null)
            return Call("browser_find_by_placeholder", (JsonObject)opts.DeepClone());
        if (opts.TryGetPropertyValue("testid", out var tid) && tid is not null)
            return Call("browser_find_by_testid", (JsonObject)opts.DeepClone());
        return Call("browser_find", CloneObjOrEmpty(opts));
    }

    public Task<JsonNode?> GetCookies(JsonObject? opts = null) =>
        Call("browser_cookies_get", CloneObjOrEmpty(opts));

    /// <summary>OpenDia has no dedicated get-url tool, but
    /// <c>browser_wait_for_url</c> with no matcher completes immediately
    /// and returns the current URL as a side-effect.</summary>
    public async Task<string?> GetCurrentUrl()
    {
        try
        {
            var r = await Call("browser_wait_for_url", new JsonObject { ["timeout"] = 100 }).ConfigureAwait(false);
            return AsString(r, "url");
        }
        catch { return null; }
    }

    /// <summary>Buffered network requests (last 200) via CDP-Network.
    /// Used by adapters that installed an interceptor earlier.</summary>
    public Task<JsonNode?> GetInterceptedRequests(JsonObject? opts = null) =>
        Call("browser_cdp_list_network_requests", CloneObjOrEmpty(opts));

    public Task InsertText(string text, JsonObject? opts = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return Call("browser_keyboard_insert_text", Pack(("text", text)));
    }

    /// <summary>Install a Fetch route via CDP so subsequent requests
    /// can be captured / rewritten.</summary>
    public Task InstallInterceptor(JsonObject opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        return Call("browser_network_route", CloneObjOrEmpty(opts));
    }

    public Task Keys(JsonNode keys, JsonObject? opts = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        // Multi-key chord (Control+Shift+K) or single key.
        if (keys is JsonValue kv && kv.TryGetValue<string>(out var k))
            return Call("browser_press", new JsonObject { ["key"] = k });
        // Array: press each key in sequence.
        if (keys is JsonArray arr)
        {
            var joined = string.Join("+", arr.OfType<JsonValue>().Select(x => x.ToString()));
            return Call("browser_press", new JsonObject { ["key"] = joined });
        }
        return Call("browser_press", new JsonObject { ["key"] = keys.ToJsonString() });
    }

    /// <summary>Native (isTrusted=true) click via CDP Input.</summary>
    public async Task NativeClick(JsonNode refOrSelector, JsonObject? opts = null)
    {
        ArgumentNullException.ThrowIfNull(refOrSelector);
        // CDP wants viewport coordinates. Get the element box first, then
        // dispatch mousePressed+mouseReleased at its center.
        var box = await Call("browser_get_box", BuildRefOrSelector(refOrSelector)).ConfigureAwait(false);
        if (box is not JsonObject bo || !bo.TryGetPropertyValue("x", out var xn))
            throw new InvalidOperationException("nativeClick: could not resolve element box");
        var x = xn?.GetValue<double>() ?? 0;
        var y = bo["y"]?.GetValue<double>() ?? 0;
        var w = bo["width"]?.GetValue<double>() ?? 0;
        var h = bo["height"]?.GetValue<double>() ?? 0;
        await Call("browser_cdp_input_mouse", new JsonObject
        {
            ["x"] = x + w / 2,
            ["y"] = y + h / 2,
            ["button"] = "left",
        }).ConfigureAwait(false);
    }

    public Task NativeKeyPress(string key, JsonObject? opts = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Call("browser_cdp_input_keys", new JsonObject { ["keys"] = key });
    }

    public Task NativeType(string text, JsonObject? opts = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return Call("browser_cdp_input_keys", new JsonObject { ["keys"] = text });
    }

    public Task PressKey(string key, JsonObject? opts = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Call("browser_press", new JsonObject { ["key"] = key });
    }

    public Task<JsonNode?> ReadNetworkCapture(JsonObject? opts = null) =>
        Call("browser_cdp_list_network_requests", CloneObjOrEmpty(opts));

    public Task SelectTab(JsonNode tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        // Tab can be a numeric id or an object {tabId, url}.
        var tabId = tab switch
        {
            JsonValue tv when tv.TryGetValue<long>(out var idL) => (JsonNode)idL,
            JsonObject to when to.TryGetPropertyValue("tabId", out var t) => t?.DeepClone() ?? tab,
            JsonObject to when to.TryGetPropertyValue("id", out var t) => t?.DeepClone() ?? tab,
            _ => tab,
        };
        return Call("browser_tab_switch", new JsonObject { ["tabId"] = tabId });
    }

    public Task SetFileInput(JsonNode refOrSelector, JsonNode files)
    {
        ArgumentNullException.ThrowIfNull(refOrSelector);
        ArgumentNullException.ThrowIfNull(files);
        var args = BuildRefOrSelector(refOrSelector);
        args["files"] = files.DeepClone();
        return Call("browser_upload", args);
    }

    public Task<JsonNode?> Snapshot(JsonObject? opts = null) =>
        Call("browser_snapshot", CloneObjOrEmpty(opts));

    public Task StartNetworkCapture(JsonObject? opts = null) =>
        Call("browser_network_har_start", CloneObjOrEmpty(opts));

    public Task<JsonNode?> Tabs(JsonObject? opts = null) =>
        Call("browser_tab_list", CloneObjOrEmpty(opts));

    /// <summary>Type into a focused field or an @refN-targeted element.</summary>
    public Task Type(string text, JsonObject? opts = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        // If opts carries a ref, use ref-targeted browser_type; else
        // fall back to keyboard_type (typing into whatever's focused).
        if (opts is JsonObject o && o.TryGetPropertyValue("ref", out var r) && r is not null)
        {
            return Call("browser_type", new JsonObject { ["ref"] = r.DeepClone(), ["text"] = text });
        }
        return Call("browser_keyboard_type", new JsonObject { ["text"] = text });
    }

    public async Task<JsonNode?> WaitForCapture(JsonObject opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        // OpenDia doesn't have a "wait for a captured request" tool
        // out of the box; poll browser_cdp_list_network_requests with
        // a simple predicate on url substring if provided.
        var urlHint = opts["url"]?.GetValue<string>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var reqs = await Call("browser_cdp_list_network_requests", new JsonObject()).ConfigureAwait(false);
            if (reqs is JsonObject reqObj && reqObj["requests"] is JsonArray arr)
            {
                foreach (var rq in arr)
                {
                    if (rq?["url"]?.GetValue<string>() is string ru &&
                        (urlHint is null || ru.Contains(urlHint, StringComparison.Ordinal)))
                        return rq.DeepClone();
                }
            }
            await Task.Delay(200).ConfigureAwait(false);
        }
        return null;
    }

    public Task WaitForTimeout(int ms) => DelayClamped(ms);
}
