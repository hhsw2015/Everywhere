using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.OpenDia;

/// <summary>
/// SPEC docs/specs/opendia-cebian-merge.md §Phase 4 — daemon-side chat bus.
/// Wraps <see cref="OpenDiaBridge"/> for the six <c>chat_*</c> WebSocket
/// frames plus a long-poll subscription. The extension owns chat state
/// (<c>chrome.storage.local</c>); this bus is stateless apart from tracking
/// per-subscriber cursors so subscribe-after-reconnect can resume with
/// <c>since_msg_id=last_seen</c>.
/// </summary>
public sealed class OpenDiaChatBus : IDisposable
{
    private readonly OpenDiaBridge _bridge;
    private readonly ILogger<OpenDiaChatBus> _logger;
    private readonly ConcurrentDictionary<string, Subscriber> _subs = new(StringComparer.Ordinal);

    public OpenDiaChatBus(OpenDiaBridge bridge, ILogger<OpenDiaChatBus> logger)
    {
        _bridge = bridge;
        _logger = logger;
        _bridge.PushFrame += OnPushFrame;
    }

    public void Dispose()
    {
        _bridge.PushFrame -= OnPushFrame;
        foreach (var s in _subs.Values) s.Channel.Writer.TryComplete();
        _subs.Clear();
    }

    // -----------------------------------------------------------------
    // Frame plumbing — SPEC §5.1 chat_list / chat_read / chat_send /
    // chat_create / chat_delete each map to a single CallToolAsync round
    // trip on the existing bridge. `EXTENSION_NOT_CONNECTED` is raised
    // by the caller (ChatBusTools) so the MCP envelope matches SPEC §5.2.
    // -----------------------------------------------------------------

    public Task<JsonNode?> ListAsync(CancellationToken ct = default)
        => _bridge.CallToolAsync("chat_list", null, ct: ct);

    public Task<JsonNode?> ReadAsync(string chatId, long? sinceMsgId, int? limit, CancellationToken ct = default)
    {
        var args = new JsonObject { ["chat_id"] = chatId };
        if (sinceMsgId is not null) args["since_msg_id"] = sinceMsgId.Value;
        if (limit is not null) args["limit"] = limit.Value;
        return _bridge.CallToolAsync("chat_read", args, ct: ct);
    }

    public Task<JsonNode?> SendAsync(
        string chatId,
        string clientMsgId,
        string role,
        string text,
        JsonObject? toolCall,
        JsonObject? metadata,
        CancellationToken ct = default)
    {
        var args = new JsonObject
        {
            ["chat_id"] = chatId,
            ["client_msg_id"] = clientMsgId,
            ["role"] = role,
            ["text"] = text,
        };
        if (toolCall is not null) args["tool_call"] = toolCall.DeepClone();
        if (metadata is not null) args["metadata"] = metadata.DeepClone();
        return _bridge.CallToolAsync("chat_send", args, ct: ct);
    }

    public Task<JsonNode?> CreateAsync(string? title, int? tabHint, CancellationToken ct = default)
    {
        var args = new JsonObject();
        if (title is not null) args["title"] = title;
        if (tabHint is not null) args["tab_hint"] = tabHint.Value;
        return _bridge.CallToolAsync("chat_create", args, ct: ct);
    }

    public Task<JsonNode?> DeleteAsync(string chatId, CancellationToken ct = default)
        => _bridge.CallToolAsync("chat_delete", new JsonObject { ["chat_id"] = chatId }, ct: ct);

    // -----------------------------------------------------------------
    // Subscribe — SPEC §5.1. Sends a chat_subscribe frame to the ext and
    // tracks push replies. Auto-resubscribes with since_msg_id=last_seen
    // when the WebSocket flaps (StateChanged fires reconnect events).
    // -----------------------------------------------------------------

    /// <summary>
    /// Register a subscriber for <paramref name="chatId"/>. Returns a
    /// <see cref="Subscription"/> whose <c>Channel</c> yields
    /// <c>{msg, last_msg_id}</c> pairs; caller awaits <see cref="ChannelReader{T}.ReadAsync"/>
    /// with its own timeout. Use <see cref="UnsubscribeAsync"/> to release.
    /// </summary>
    public async Task<Subscription> SubscribeAsync(string chatId, long? sinceMsgId, CancellationToken ct = default)
    {
        var subId = Guid.NewGuid().ToString("n");
        var channel = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var sub = new Subscriber(subId, chatId, channel) { LastSeenMsgId = sinceMsgId };
        _subs[subId] = sub;

        try
        {
            var args = new JsonObject { ["chat_id"] = chatId, ["sub_id"] = subId };
            if (sinceMsgId is not null) args["since_msg_id"] = sinceMsgId.Value;
            await _bridge.CallToolAsync("chat_subscribe", args, ct: ct).ConfigureAwait(false);
        }
        catch
        {
            _subs.TryRemove(subId, out _);
            channel.Writer.TryComplete();
            throw;
        }

        return new Subscription(this, sub);
    }

    public async Task UnsubscribeAsync(string subId, CancellationToken ct = default)
    {
        if (!_subs.TryRemove(subId, out var sub)) return;
        sub.Channel.Writer.TryComplete();
        try
        {
            await _bridge.CallToolAsync("chat_unsubscribe", new JsonObject { ["sub_id"] = subId }, ct: ct)
                          .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OpenDiaChatBus: unsubscribe upstream failed for {SubId} (ignored)", subId);
        }
    }

    /// <summary>Test hook. Registers a subscriber without touching the bridge.</summary>
    internal Subscription RegisterSubscriberForTest(string subId, string chatId, long? sinceMsgId = null)
    {
        var channel = Channel.CreateUnbounded<JsonObject>();
        var sub = new Subscriber(subId, chatId, channel) { LastSeenMsgId = sinceMsgId };
        _subs[subId] = sub;
        return new Subscription(this, sub);
    }

    private void OnPushFrame(JsonObject frame)
    {
        var type = frame["type"]?.GetValue<string>();
        if (type is not ("chat_appended" or "chat_deleted")) return;

        var subId = frame["sub_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(subId) || !_subs.TryGetValue(subId!, out var sub)) return;

        if (type == "chat_appended" && frame["msg"] is JsonObject msg
            && msg["msg_id"] is JsonValue mv && mv.TryGetValue<long>(out var msgId))
        {
            sub.LastSeenMsgId = msgId;
        }
        sub.Channel.Writer.TryWrite(frame);
    }

    public sealed class Subscriber
    {
        public Subscriber(string id, string chatId, Channel<JsonObject> channel)
        {
            Id = id;
            ChatId = chatId;
            Channel = channel;
        }
        public string Id { get; }
        public string ChatId { get; }
        public Channel<JsonObject> Channel { get; }
        public long? LastSeenMsgId { get; set; }
    }

    public sealed class Subscription : IAsyncDisposable
    {
        private readonly OpenDiaChatBus _bus;
        internal Subscription(OpenDiaChatBus bus, Subscriber sub) { _bus = bus; Sub = sub; }
        public Subscriber Sub { get; }
        public string SubId => Sub.Id;
        public ChannelReader<JsonObject> Reader => Sub.Channel.Reader;
        public async ValueTask DisposeAsync() => await _bus.UnsubscribeAsync(Sub.Id).ConfigureAwait(false);
    }
}
