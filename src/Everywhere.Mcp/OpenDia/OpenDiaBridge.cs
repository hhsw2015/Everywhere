using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.OpenDia;

/// <summary>
/// Native C# replacement for the opendia node MCP server. Speaks the same
/// websocket protocol the opendia browser extension already uses, so users
/// only install the unmodified Chrome/Firefox extension from opendia.co —
/// no node.js dependency, no separate npm install, no spawned subprocess.
///
/// Wire protocol (extension is the WS client, we are the server on :5555):
///   ext -> us:  { type: "register", tools: [{ name, description, inputSchema }, ...] }
///   ext -> us:  { type: "ping" }                       => we respond { type: "pong", timestamp: ... }
///   us  -> ext: { id: "<unique>", method: "<tool>", params: {...} }
///   ext -> us:  { id: "<same>", result: ... }          OR  { id, error: "..." }
///
/// Single-extension model: a fresh connection replaces any stale one and
/// in-flight calls on the old socket are rejected so the agent gets a real
/// error instead of waiting 30s. Mirrors the upstream behaviour.
/// </summary>
public sealed class OpenDiaBridge : IAsyncDisposable
{
    private readonly ILogger<OpenDiaBridge> _logger;
    private readonly object _gate = new();
    private WebSocket? _extSocket;
    private CancellationTokenSource? _socketCts;

    private readonly Dictionary<string, PendingCall> _pending = new();
    private long _callCounter;

    /// <summary>Tools the connected extension last registered. Empty when no ext connected.</summary>
    public IReadOnlyList<JsonObject> AvailableTools { get; private set; } = Array.Empty<JsonObject>();

    public bool IsConnected => _extSocket?.State == WebSocketState.Open;

    public event Action? StateChanged;

    public OpenDiaBridge(ILogger<OpenDiaBridge> logger)
    {
        _logger = logger;
    }

    private HttpListener? _listener;
    private CancellationTokenSource? _serverCts;

    public Task StartAsync(int port = 5555, CancellationToken cancellationToken = default)
    {
        // HttpListener -> WebSocket upgrade is the simplest cross-platform
        // surface we can stand up without pulling Kestrel into yet another
        // hosting bundle. Loopback-only by design — the extension runs on
        // the same machine and exposing this to the LAN would let any
        // co-located process drive the user's browser.
        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _logger.LogInformation("OpenDiaBridge: listening on ws://127.0.0.1:{Port}/", port);
        _ = Task.Run(() => AcceptLoopAsync(_serverCts.Token));
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (HttpListenerException) { return; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                ctx.Response.Close();
                continue;
            }

            _ = Task.Run(() => HandleSocketAsync(ctx, ct));
        }
    }

    private async Task HandleSocketAsync(HttpListenerContext ctx, CancellationToken parentCt)
    {
        WebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenDiaBridge: websocket upgrade failed");
            return;
        }

        var socket = wsCtx.WebSocket;
        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);

        // Replace any stale socket — opendia spec: only one extension owns
        // the bridge at a time. Reject every in-flight call on the old
        // socket so callers don't hang.
        WebSocket? oldSocket;
        lock (_gate)
        {
            oldSocket = _extSocket;
            _extSocket = socket;
            _socketCts?.Cancel();
            _socketCts = sessionCts;
            // Drain pending — they were tied to the old socket's id space.
            foreach (var pending in _pending.Values)
                pending.Tcs.TrySetException(new InvalidOperationException(
                    "OpenDia extension reconnected; in-flight call rejected"));
            _pending.Clear();
        }
        if (oldSocket is not null)
        {
            try { await oldSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); }
            catch { /* old socket may be dead already */ }
        }

        _logger.LogInformation("OpenDiaBridge: extension connected");
        StateChanged?.Invoke();

        var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
        var sb = new StringBuilder();
        try
        {
            while (socket.State == WebSocketState.Open && !sessionCts.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, sessionCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer.AsSpan(0, result.Count)));
                } while (!result.EndOfMessage);

                HandleIncoming(socket, sb.ToString(), sessionCts.Token);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown / replace */ }
        catch (WebSocketException ex)
        {
            _logger.LogInformation(ex, "OpenDiaBridge: extension socket error (probably normal close)");
        }
        finally
        {
            lock (_gate)
            {
                if (_extSocket == socket)
                {
                    _extSocket = null;
                    AvailableTools = Array.Empty<JsonObject>();
                    foreach (var pending in _pending.Values)
                        pending.Tcs.TrySetException(new InvalidOperationException(
                            "OpenDia extension disconnected mid-call"));
                    _pending.Clear();
                }
            }
            try { socket.Dispose(); } catch { /* ignore */ }
            _logger.LogInformation("OpenDiaBridge: extension disconnected");
            StateChanged?.Invoke();
        }
    }

    private void HandleIncoming(WebSocket socket, string raw, CancellationToken ct)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "OpenDiaBridge: ignoring non-JSON ext message ({Bytes} bytes)", raw.Length);
            return;
        }
        if (node is not JsonObject obj) return;

        var type = obj["type"]?.GetValue<string>();
        if (type == "register")
        {
            var tools = obj["tools"]?.AsArray();
            if (tools is null) return;
            var list = new List<JsonObject>(tools.Count);
            foreach (var t in tools)
                if (t is JsonObject to)
                    list.Add(to);
            AvailableTools = list;
            _logger.LogInformation("OpenDiaBridge: extension registered {Count} tools.", list.Count);
            StateChanged?.Invoke();
            return;
        }

        if (type == "ping")
        {
            _ = SendAsync(socket, new JsonObject
            {
                ["type"] = "pong",
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, ct);
            return;
        }

        // Tool response (matched by id).
        var id = obj["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id)) return;
        PendingCall? pending;
        lock (_gate)
        {
            if (!_pending.Remove(id!, out pending))
                return;
        }
        var error = obj["error"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(error))
        {
            pending!.Tcs.TrySetException(new InvalidOperationException(error!));
        }
        else
        {
            // result can be any JsonNode shape — pass through.
            pending!.Tcs.TrySetResult(obj["result"]);
        }
    }

    /// <summary>
    /// Forward an MCP tool call to the connected extension. Returns the
    /// raw `result` JsonNode from the extension or throws on timeout /
    /// disconnect / extension-reported error.
    /// </summary>
    public async Task<JsonNode?> CallToolAsync(string toolName, JsonNode? args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        WebSocket? socket;
        lock (_gate) { socket = _extSocket; }
        if (socket is null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException(
                "OpenDia extension not connected. Install/reload the OpenDia browser extension.");

        // Date.now() alone collides under burst load — opendia upstream hit
        // this and added a counter; do the same.
        var id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Interlocked.Increment(ref _callCounter)}";
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingCall(tcs);

        lock (_gate) { _pending[id] = pending; }

        var msg = new JsonObject
        {
            ["id"] = id,
            ["method"] = toolName,
            ["params"] = args is null ? new JsonObject() : args.DeepClone(),
        };
        try { await SendAsync(socket, msg, ct); }
        catch
        {
            lock (_gate) { _pending.Remove(id); }
            throw;
        }

        var effective = timeout ?? TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(effective);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            lock (_gate) { _pending.Remove(id); }
            throw new TimeoutException($"OpenDia tool '{toolName}' timed out after {effective.TotalSeconds:0}s");
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    private static async Task SendAsync(WebSocket socket, JsonNode payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(_jsonOpts));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { _serverCts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        WebSocket? sock;
        lock (_gate) { sock = _extSocket; _extSocket = null; }
        if (sock is not null)
        {
            try { await sock.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None); }
            catch { /* ignore */ }
        }
    }

    private sealed record PendingCall(TaskCompletionSource<JsonNode?> Tcs);
}
