using System.Text.Json.Nodes;

namespace Everywhere.Mcp.OpenCli;

/// <summary>
/// SPEC §3.4 — the surface OpenCLI adapters call on <c>page.*</c>.
/// Phase 1 ships <see cref="Phase1StubPage"/> with every method
/// throwing <see cref="Phase2NotReadyException"/>; Phase 2 replaces
/// the stub with <see cref="OpenDiaPageBridge"/>, which routes each
/// call through <c>OpenDiaBridge.CallToolAsync</c>.
///
/// Lint Rule 7 walks <c>3rd/opencli/clis/**/*.js</c> for <c>page.&lt;name&gt;(</c>
/// calls and requires every distinct <c>name</c> to appear here.
/// New upstream symbols → lint fail → SPEC review.
///
/// Method list reflects the exact <c>page.*</c> set in upstream
/// <c>v1.8.5</c>; sorted alphabetically.
/// </summary>
public interface IPage
{
    Task<JsonNode?> AutoScroll(JsonObject? opts = null);
    Task<JsonNode?> Cdp(string method, JsonObject? args = null);
    Task Click(JsonNode refOrSelector, JsonObject? opts = null);
    Task CloseWindow(JsonObject? opts = null);
    Task<JsonNode?> Evaluate(string js);
    Task<JsonNode?> EvaluateWithArgs(string js, JsonNode? args);
    Task<JsonNode?> Find(JsonObject opts);
    Task<JsonNode?> GetCookies(JsonObject? opts = null);
    Task<string?> GetCurrentUrl();
    Task<JsonNode?> GetInterceptedRequests(JsonObject? opts = null);
    Task Goto(string url, JsonObject? opts = null);
    Task InsertText(string text, JsonObject? opts = null);
    Task InstallInterceptor(JsonObject opts);
    Task Keys(JsonNode keys, JsonObject? opts = null);
    Task NativeClick(JsonNode refOrSelector, JsonObject? opts = null);
    Task NativeKeyPress(string key, JsonObject? opts = null);
    Task NativeType(string text, JsonObject? opts = null);
    Task PressKey(string key, JsonObject? opts = null);
    Task<JsonNode?> ReadNetworkCapture(JsonObject? opts = null);
    Task<string?> Screenshot(JsonObject? opts = null);
    Task SelectTab(JsonNode tab);
    Task SetFileInput(JsonNode refOrSelector, JsonNode files);
    Task<JsonNode?> Snapshot(JsonObject? opts = null);
    Task StartNetworkCapture(JsonObject? opts = null);
    Task<JsonNode?> Tabs(JsonObject? opts = null);
    Task Type(string text, JsonObject? opts = null);
    Task Wait(JsonNode arg);
    Task<JsonNode?> WaitForCapture(JsonObject opts);
    Task WaitForTimeout(int ms);
}

/// <summary>
/// Thrown by Phase 1's IPage stub when an adapter touches the browser
/// surface before Phase 2 wires it up. <see cref="HostShim"/> catches
/// this and reports it as an <c>opencli_run</c> error envelope with
/// <c>code: "BROWSER_NOT_READY"</c>.
/// </summary>
public sealed class Phase2NotReadyException : Exception
{
    public Phase2NotReadyException(string method)
        : base(BuildSafeMessage(method))
    {
        Method = NormalizeMethod(method);
    }
    public Phase2NotReadyException(string method, Exception inner)
        : base(BuildSafeMessage(method), inner)
    {
        Method = NormalizeMethod(method);
    }
    /// <summary>Always non-null. Set to <c>"&lt;unknown&gt;"</c> when
    /// the caller passed null/empty so structured fields agree with
    /// the human-readable message.</summary>
    public string Method { get; }
    private static string NormalizeMethod(string? method) => string.IsNullOrEmpty(method) ? "<unknown>" : method;

    // Validation that throws would lose the inner exception when called
    // from the (string, Exception) ctor. Tolerate null/empty `method`
    // and emit a placeholder so the wrapped cause survives.
    private static string BuildSafeMessage(string? method)
        => string.IsNullOrEmpty(method)
            ? "page.<unknown>: browser surface not ready (Phase 2 of SPEC docs/specs/everywhere-opencli-adapters.md)"
            : $"page.{method}: browser surface not ready (Phase 2 of SPEC docs/specs/everywhere-opencli-adapters.md)";
}

/// <summary>Phase 1 stub IPage — every method returns a faulted
/// <see cref="Task"/> carrying <see cref="Phase2NotReadyException"/>,
/// so callers see a normal Promise rejection rather than a synchronous
/// throw across the V8 boundary.</summary>
public sealed class Phase1StubPage : IPage
{
    public static readonly Phase1StubPage Instance = new();
    public Phase1StubPage() { }
    private static Task Fail(string method) => Task.FromException(new Phase2NotReadyException(method));
    private static Task<T> Fail<T>(string method) => Task.FromException<T>(new Phase2NotReadyException(method));

    public Task<JsonNode?> AutoScroll(JsonObject? opts = null) => Fail<JsonNode?>("autoScroll");
    public Task<JsonNode?> Cdp(string method, JsonObject? args = null) => Fail<JsonNode?>("cdp");
    public Task Click(JsonNode refOrSelector, JsonObject? opts = null) => Fail("click");
    public Task CloseWindow(JsonObject? opts = null) => Fail("closeWindow");
    public Task<JsonNode?> Evaluate(string js) => Fail<JsonNode?>("evaluate");
    public Task<JsonNode?> EvaluateWithArgs(string js, JsonNode? args) => Fail<JsonNode?>("evaluateWithArgs");
    public Task<JsonNode?> Find(JsonObject opts) => Fail<JsonNode?>("find");
    public Task<JsonNode?> GetCookies(JsonObject? opts = null) => Fail<JsonNode?>("getCookies");
    public Task<string?> GetCurrentUrl() => Fail<string?>("getCurrentUrl");
    public Task<JsonNode?> GetInterceptedRequests(JsonObject? opts = null) => Fail<JsonNode?>("getInterceptedRequests");
    public Task Goto(string url, JsonObject? opts = null) => Fail("goto");
    public Task InsertText(string text, JsonObject? opts = null) => Fail("insertText");
    public Task InstallInterceptor(JsonObject opts) => Fail("installInterceptor");
    public Task Keys(JsonNode keys, JsonObject? opts = null) => Fail("keys");
    public Task NativeClick(JsonNode refOrSelector, JsonObject? opts = null) => Fail("nativeClick");
    public Task NativeKeyPress(string key, JsonObject? opts = null) => Fail("nativeKeyPress");
    public Task NativeType(string text, JsonObject? opts = null) => Fail("nativeType");
    public Task PressKey(string key, JsonObject? opts = null) => Fail("pressKey");
    public Task<JsonNode?> ReadNetworkCapture(JsonObject? opts = null) => Fail<JsonNode?>("readNetworkCapture");
    public Task<string?> Screenshot(JsonObject? opts = null) => Fail<string?>("screenshot");
    public Task SelectTab(JsonNode tab) => Fail("selectTab");
    public Task SetFileInput(JsonNode refOrSelector, JsonNode files) => Fail("setFileInput");
    public Task<JsonNode?> Snapshot(JsonObject? opts = null) => Fail<JsonNode?>("snapshot");
    public Task StartNetworkCapture(JsonObject? opts = null) => Fail("startNetworkCapture");
    public Task<JsonNode?> Tabs(JsonObject? opts = null) => Fail<JsonNode?>("tabs");
    public Task Type(string text, JsonObject? opts = null) => Fail("type");
    public Task Wait(JsonNode arg) => Fail("wait");
    public Task<JsonNode?> WaitForCapture(JsonObject opts) => Fail<JsonNode?>("waitForCapture");
    public Task WaitForTimeout(int ms) => Fail("waitForTimeout");
}
