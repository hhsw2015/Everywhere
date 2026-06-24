using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    // 8 MiB hard cap on a single inbound message — protects against a
    // hostile/buggy extension streaming bytes forever and OOM-ing the
    // host process. Real opendia messages are well under 1 MiB even
    // for full DOM extracts.
    private const int MaxIncomingBytes = 8 * 1024 * 1024;

    private readonly ILogger<OpenDiaBridge> _logger;
    private readonly object _gate = new();
    // Outbound writes from CallToolAsync, the ping/pong responder, and the
    // close-on-replace path can race. WebSocket.SendAsync is NOT thread-safe;
    // serialise every write through this gate or risk corrupted frames that
    // crash the extension.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket? _extSocket;
    private CancellationTokenSource? _socketCts;
    private Task? _acceptLoop;

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

    public async Task StartAsync(int port = 5555, CancellationToken cancellationToken = default)
    {
        // HttpListener -> WebSocket upgrade is the simplest cross-platform
        // surface we can stand up without pulling Kestrel into yet another
        // hosting bundle. Loopback-only by design — the extension runs on
        // the same machine and exposing this to the LAN would let any
        // co-located process drive the user's browser.
        await StopAsync().ConfigureAwait(false);
        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        // opendia extension hardcodes ws://localhost:5555 (not 127.0.0.1)
        // and will not auto-discover unless an HTTP server is also up on
        // port+1. Bind both names — HttpListener matches request Host
        // headers literally, so a "localhost" Host on a 127.0.0.1-only
        // prefix returns 404. Skip "+:port" / "*:port" deliberately —
        // this is a desktop bridge, not a network service.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            _listener.Start();
        }
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

        try { localCts?.Cancel(); } catch { /* ignore */ }
        try { localListener?.Stop(); } catch { /* ignore */ }
        try { localListener?.Close(); } catch { /* ignore */ }

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
            try { sock.Abort(); } catch { /* ignore */ }
            try { sock.Dispose(); } catch { /* ignore */ }
        }
        StateChanged?.Invoke();

        try { localCts?.Dispose(); } catch { /* ignore */ }
        // Wait for accept loop to drain so a subsequent StartAsync on the
        // same port doesn't race the listener teardown.
        if (localLoop is not null)
        {
            try { return localLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* loop already gone */ }
        }
        return Task.CompletedTask;
    }

    [Obsolete("Synchronous Stop kept for compatibility; new callers should await StopAsync.")]
    public void Stop() => _ = StopAsync();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
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

        // Replace any stale socket — opendia spec: only one extension owns
        // the bridge at a time. Reject every in-flight call on the old
        // socket so callers don't hang.
        WebSocket? oldSocket;
        CancellationTokenSource? oldSessionCts;
        lock (_gate)
        {
            oldSocket = _extSocket;
            oldSessionCts = _socketCts;
            _extSocket = socket;
            _socketCts = sessionCts;
            // Drain pending — they were tied to the old socket's id space.
            foreach (var pending in _pending.Values)
                pending.Tcs.TrySetException(new InvalidOperationException(
                    "OpenDia extension reconnected; in-flight call rejected"));
            _pending.Clear();
        }
        try { oldSessionCts?.Cancel(); } catch { /* ignore */ }
        try { oldSessionCts?.Dispose(); } catch { /* ignore */ }
        if (oldSocket is not null)
        {
            try { oldSocket.Abort(); } catch { /* old socket may be dead already */ }
            try { oldSocket.Dispose(); } catch { /* ignore */ }
        }

        _logger.LogInformation("OpenDiaBridge: extension connected");
        StateChanged?.Invoke();

        // Fixed receive frame buffer; payload accumulator grows up to
        // MaxIncomingBytes. UTF-8 decoder handles multi-byte codepoints
        // straddling frame boundaries — naive Encoding.UTF8.GetString on
        // each frame would produce replacement chars and break JSON parse.
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
                        try { socket.Abort(); } catch { /* ignore */ }
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
        catch (OperationCanceledException) { /* expected on shutdown / replace */ }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "OpenDiaBridge: extension socket closed");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentBuffer);
            ArrayPool<char>.Shared.Return(rentChars);
            CancellationTokenSource? toDispose = null;
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
                    toDispose = _socketCts;
                    _socketCts = null;
                }
            }
            try { toDispose?.Dispose(); } catch { /* ignore */ }
            try { socket.Dispose(); } catch { /* ignore */ }
            _logger.LogInformation("OpenDiaBridge: extension disconnected");
            StateChanged?.Invoke();
        }
    }

    private static async Task CloseGracefulAsync(WebSocket socket, string reason)
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None)
                        .ConfigureAwait(false);
        }
        catch { /* socket may already be torn down */ }
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
            pending!.Tcs.TrySetException(new OpenDiaToolException(error!));
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
        var effective = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTimeOffset.UtcNow + effective;
        Exception? lastError = null;
        // Chrome Manifest V3 service workers die from idle (~30s) and the
        // ext immediately re-opens — sometimes mid-call. Retry on a fresh
        // socket each time the bridge reports "extension reconnected" or
        // "extension disconnected mid-call" until the overall tool-call
        // deadline runs out.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            WebSocket? socket = null;
            var waitDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            if (waitDeadline > deadline) waitDeadline = deadline;
            while (DateTimeOffset.UtcNow < waitDeadline)
            {
                lock (_gate) { socket = _extSocket; }
                if (socket?.State == WebSocketState.Open) break;
                try { await Task.Delay(150, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
            if (socket is null || socket.State != WebSocketState.Open)
            {
                lastError = new OpenDiaToolException(
                    "OpenDia extension not connected. Install/reload the OpenDia browser extension.");
                break;
            }

            var id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Interlocked.Increment(ref _callCounter)}";
            var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) { _pending[id] = new PendingCall(tcs); }

            var msg = new JsonObject
            {
                ["id"] = id,
                ["method"] = toolName,
                ["params"] = args is null ? new JsonObject() : args.DeepClone(),
            };
            try { await SendAsync(socket, msg, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                lock (_gate) { _pending.Remove(id); }
                lastError = ex;
                continue; // retry on fresh socket
            }

            var perAttemptCap = remaining < TimeSpan.FromSeconds(8) ? remaining : TimeSpan.FromSeconds(8);
            using var attemptCts = new CancellationTokenSource(perAttemptCap);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, attemptCts.Token);
            try
            {
                return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                lock (_gate) { _pending.Remove(id); }
                lastError = new TimeoutException(
                    $"OpenDia tool '{toolName}' attempt {attempt + 1} timed out after {perAttemptCap.TotalSeconds:0}s");
                continue;
            }
            catch (OperationCanceledException)
            {
                lock (_gate) { _pending.Remove(id); }
                throw;
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains("reconnected") || ex.Message.Contains("disconnected"))
            {
                // Extension flapped — try again on the next live socket.
                lastError = ex;
                continue;
            }
        }
        throw lastError ?? new TimeoutException(
            $"OpenDia tool '{toolName}' timed out after {effective.TotalSeconds:0}s without a stable connection");
    }

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    private async Task SendAsync(WebSocket socket, JsonNode payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(_jsonOpts));
        // WebSocket.SendAsync is single-writer — serialise every outbound
        // payload to keep frame ordering intact and prevent interleaved
        // partial messages.
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

    private sealed record PendingCall(TaskCompletionSource<JsonNode?> Tcs);
}

/// <summary>
/// Exception raised when the opendia extension reports an error, is not
/// connected, or otherwise refuses to satisfy a tool call. Callers (MCP
/// tool wrappers) catch this to format a clean error envelope rather than
/// propagating a raw .NET stack trace to the agent.
/// </summary>
public sealed class OpenDiaToolException : Exception
{
    public OpenDiaToolException(string message) : base(message) { }
}
