using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.OpenDia;

/// <summary>
/// 1:1 port of opendia-mcp/server.js's websocket-bridge half. The opendia
/// Chrome / Firefox extension connects as a WS client on :5555; we route
/// MCP tool calls through it. Wire protocol, replace-on-reconnect
/// semantics, and in-flight handling all match upstream — anything else
/// breaks the extension's assumptions.
/// </summary>
public sealed class OpenDiaBridge : IAsyncDisposable
{
    private const int MaxIncomingBytes = 8 * 1024 * 1024;

    private readonly ILogger<OpenDiaBridge> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket? _extSocket;
    private CancellationTokenSource? _socketCts;
    private Task? _acceptLoop;
    private HttpListener? _listener;
    private CancellationTokenSource? _serverCts;

    private readonly Dictionary<string, PendingCall> _pending = new();
    private long _callCounter;

    public IReadOnlyList<JsonObject> AvailableTools { get; private set; } = Array.Empty<JsonObject>();
    public bool IsConnected => _extSocket?.State == WebSocketState.Open;
    public event Action? StateChanged;

    public OpenDiaBridge(ILogger<OpenDiaBridge> logger) => _logger = logger;

    public async Task StartAsync(int port = 5555, CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add($"http://localhost:{port}/");
        try { _listener.Start(); }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "OpenDiaBridge: failed to bind ws://127.0.0.1:{Port}/", port);
            _serverCts.Dispose();
            _serverCts = null;
            _listener = null;
            return;
        }
        _logger.LogInformation("OpenDiaBridge: listening on ws://127.0.0.1:{Port}/", port);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_serverCts.Token));
    }

    public Task StopAsync()
    {
        var localCts = _serverCts;
        var localListener = _listener;
        var localLoop = _acceptLoop;
        _serverCts = null;
        _listener = null;
        _acceptLoop = null;
        try { localCts?.Cancel(); } catch { }
        try { localListener?.Stop(); } catch { }
        try { localListener?.Close(); } catch { }

        WebSocket? sock;
        lock (_gate)
        {
            sock = _extSocket;
            _extSocket = null;
            _socketCts?.Cancel();
            _socketCts = null;
            AvailableTools = Array.Empty<JsonObject>();
            foreach (var p in _pending.Values)
                p.Tcs.TrySetException(new InvalidOperationException("OpenDia bridge stopping"));
            _pending.Clear();
        }
        if (sock is not null)
        {
            try { sock.Abort(); } catch { }
            try { sock.Dispose(); } catch { }
        }
        StateChanged?.Invoke();
        try { localCts?.Dispose(); } catch { }
        if (localLoop is not null)
        {
            try { return localLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        return Task.CompletedTask;
    }

    [Obsolete("Use StopAsync.")]
    public void Stop() => _ = StopAsync();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

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
        try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException or WebSocketException)
        {
            _logger.LogWarning(ex, "OpenDiaBridge: websocket upgrade failed");
            return;
        }

        var socket = wsCtx.WebSocket;
        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);

        // ---- 1:1 port of node server.js wss.on('connection', ws => ...) ----
        //
        // Replace any old socket the same way upstream does: gracefully
        // CLOSE it, then take ownership. CRITICAL: do NOT abort the old
        // socket and do NOT reject in-flight tool calls here. Upstream
        // server.js relies on the new socket inheriting the same call-id
        // namespace because the extension hands the same socket reference
        // around. The old socket's close handler then runs with the guard
        // `if (chromeExtensionSocket === ws) {...}` which is false
        // (we've already swapped in the new ws) — so it logs "stale
        // socket closed" and leaves pending alone.
        WebSocket? oldSocket;
        CancellationTokenSource? oldSessionCts;
        lock (_gate)
        {
            oldSocket = _extSocket;
            oldSessionCts = _socketCts;
            _extSocket = socket;
            _socketCts = sessionCts;
        }
        if (oldSocket is not null)
        {
            _logger.LogInformation("OpenDiaBridge: replacing existing extension connection");
            try { await CloseGracefulAsync(oldSocket, "replaced").ConfigureAwait(false); }
            catch { /* old socket may be half-closed already */ }
            try { oldSessionCts?.Cancel(); } catch { }
            try { oldSessionCts?.Dispose(); } catch { }
            try { oldSocket.Dispose(); } catch { }

            // Replay pending tool calls onto the new socket. The opendia
            // ext (Chrome MV3) calls ensureConnection() on every inbound
            // tool message — that ALWAYS opens a fresh socket, so any
            // call we sent on the old socket got dropped when the old
            // socket closed. The ext now sits idle on the new socket
            // waiting for us to repeat the request.
            List<(string id, JsonObject msg)> toReplay = new();
            lock (_gate)
            {
                foreach (var kv in _pending)
                {
                    if (kv.Value.LastSent is not null)
                        toReplay.Add((kv.Key, (JsonObject)kv.Value.LastSent.DeepClone()));
                }
            }
            foreach (var (id, m) in toReplay)
            {
                try
                {
                    await SendAsync(socket, m, sessionCts.Token).ConfigureAwait(false);
                    _logger.LogInformation(
                        "OpenDiaBridge: replayed pending call id={Id} on fresh socket", id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "OpenDiaBridge: failed to replay pending call id={Id}", id);
                }
            }
        }

        _logger.LogInformation("OpenDiaBridge: extension connected");
        StateChanged?.Invoke();

        var rentBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var rentChars = ArrayPool<char>.Shared.Rent(64 * 1024);
        var decoder = Encoding.UTF8.GetDecoder();
        var sb = new StringBuilder();
        try
        {
            while (socket.State == WebSocketState.Open && !sessionCts.IsCancellationRequested)
            {
                sb.Clear();
                decoder.Reset();
                WebSocketReceiveResult result;
                var totalBytes = 0;
                do
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(rentBuffer), sessionCts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseGracefulAsync(socket, "client close").ConfigureAwait(false);
                        return;
                    }
                    totalBytes += result.Count;
                    if (totalBytes > MaxIncomingBytes)
                    {
                        _logger.LogWarning(
                            "OpenDiaBridge: incoming message exceeded {Cap} bytes; dropping connection.",
                            MaxIncomingBytes);
                        try { socket.Abort(); } catch { }
                        return;
                    }
                    var charsWritten = decoder.GetChars(
                        rentBuffer, 0, result.Count, rentChars, 0,
                        flush: result.EndOfMessage);
                    sb.Append(rentChars, 0, charsWritten);
                } while (!result.EndOfMessage);

                HandleIncoming(socket, sb.ToString(), sessionCts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "OpenDiaBridge: extension socket closed");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentBuffer);
            ArrayPool<char>.Shared.Return(rentChars);

            // ---- 1:1 port of node server.js ws.on('close', () => ...) ----
            //
            // ONLY clear tracked state if THIS socket is still the active
            // one. A stale close event from a previous (already-replaced)
            // socket must NOT null out the freshly reconnected extension
            // or reject the pending calls that now belong to the new
            // socket.
            CancellationTokenSource? toDispose = null;
            bool wasActive;
            lock (_gate)
            {
                wasActive = _extSocket == socket;
                if (wasActive)
                {
                    _extSocket = null;
                    AvailableTools = Array.Empty<JsonObject>();
                    foreach (var pending in _pending.Values)
                        pending.Tcs.TrySetException(new InvalidOperationException(
                            "OpenDia extension disconnected mid-call"));
                    _pending.Clear();
                    toDispose = _socketCts;
                    _socketCts = null;
                }
            }
            try { toDispose?.Dispose(); } catch { }
            try { socket.Dispose(); } catch { }

            if (wasActive)
            {
                _logger.LogInformation("OpenDiaBridge: extension disconnected");
                StateChanged?.Invoke();
            }
            else
            {
                _logger.LogDebug("OpenDiaBridge: stale extension socket closed (already replaced)");
            }
        }
    }

    private static async Task CloseGracefulAsync(WebSocket socket, string reason)
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None)
                        .ConfigureAwait(false);
        }
        catch { }
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

        // Tool response — matched by id.
        var id = obj["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id)) return;
        PendingCall? pending;
        lock (_gate)
        {
            if (!_pending.Remove(id!, out pending))
                return;
        }

        // opendia ext error shape: either a plain `error: "<msg>"` string
        // OR an `{error: {message: "..."}}` object (mirrors node server
        // line 1567: pending.reject(new Error(message.error.message)) AND
        // server.js line 1614 returns whatever error came in).
        var errNode = obj["error"];
        if (errNode is not null)
        {
            string errMsg;
            if (errNode is JsonObject errObj)
                errMsg = errObj["message"]?.GetValue<string>() ?? errNode.ToJsonString();
            else
                errMsg = errNode.GetValue<string>();
            pending!.Tcs.TrySetException(new OpenDiaToolException(errMsg));
        }
        else
        {
            pending!.Tcs.TrySetResult(obj["result"]);
        }
    }

    /// <summary>
    /// 1:1 port of upstream `callBrowserTool` (server.js:1521). Throws
    /// immediately when ext is not connected; otherwise sends + awaits
    /// matched id response with a 30s cap. No retry, no replace-time
    /// rejection — those were our bugs.
    /// </summary>
    public async Task<JsonNode?> CallToolAsync(string toolName, JsonNode? args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        WebSocket? socket;
        lock (_gate) { socket = _extSocket; }
        if (socket is null || socket.State != WebSocketState.Open)
            throw new OpenDiaToolException(
                "Browser Extension not connected. Make sure the extension is installed and active.");

        // Same id format as node: `${Date.now()}-${++callIdCounter}`.
        var id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Interlocked.Increment(ref _callCounter)}";
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var msg = new JsonObject
        {
            ["id"] = id,
            ["method"] = toolName,
            ["params"] = args is null ? new JsonObject() : args.DeepClone(),
        };
        lock (_gate)
        {
            _pending[id] = new PendingCall(tcs) { LastSent = msg };
        }

        try { await SendAsync(socket, msg, ct).ConfigureAwait(false); }
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
            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            lock (_gate) { _pending.Remove(id); }
            throw new TimeoutException("Tool call timeout");
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _pending.Remove(id); }
            throw;
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    private async Task SendAsync(WebSocket socket, JsonNode payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(_jsonOpts));
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                        .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private sealed class PendingCall
    {
        public PendingCall(TaskCompletionSource<JsonNode?> tcs) { Tcs = tcs; }
        public TaskCompletionSource<JsonNode?> Tcs { get; }
        // We remember the original outbound JSON so a reconnecting
        // extension (Chrome MV3 forces a fresh socket on every tool
        // call) can be re-sent the same request on the new socket.
        public JsonObject? LastSent { get; set; }
    }
}

public sealed class OpenDiaToolException : Exception
{
    public OpenDiaToolException(string message) : base(message) { }
}
