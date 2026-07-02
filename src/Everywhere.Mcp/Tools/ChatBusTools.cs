using System.ComponentModel;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenDia;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC docs/specs/opendia-cebian-merge.md §Phase 4 — six MCP tools plus a
/// long-poll subscribe that proxy chat state to/from the OpenDia extension's
/// sidepanel. Registered under <see cref="Meta.TierGate"/>'s <c>chat</c>
/// domain — hidden until <c>activate_domain("chat")</c>.
///
/// The extension owns the chat store (see SPEC §3 Ownership). This layer is
/// stateless apart from subscriber cursors in <see cref="OpenDiaChatBus"/>.
///
/// Error envelope matches SPEC §5.2: canonical
/// <c>{ok:false, code, message, details?}</c> JSON string.
/// </summary>
[McpServerToolType]
public sealed class ChatBusTools
{
    private readonly OpenDiaChatBus _bus;
    private readonly OpenDiaBridge _bridge;

    public ChatBusTools(OpenDiaChatBus bus, OpenDiaBridge bridge)
    {
        _bus = bus;
        _bridge = bridge;
    }

    [McpServerTool(Name = "chat_list")]
    [Description("List chats owned by the OpenDia sidepanel. Returns { chats: [{chat_id, title, updated_at, message_count}] }.")]
    public async Task<string> ChatList(CancellationToken ct = default)
    {
        if (!_bridge.IsConnected) return ExtNotConnected();
        try
        {
            var result = await _bus.ListAsync(ct).ConfigureAwait(false);
            return Ok(result);
        }
        catch (Exception ex) { return Fail("BUS_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "chat_read")]
    [Description("Read messages from a chat, optionally since a monotonic msg_id (strictly greater).")]
    public async Task<string> ChatRead(
        [Description("uuid v4 of the chat.")] string chat_id,
        [Description("Optional monotonic msg_id watermark; only messages with msg_id > since_msg_id are returned.")]
        long? since_msg_id = null,
        [Description("Optional max messages (extension may cap further).")]
        int? limit = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(chat_id)) return Fail("CHAT_NOT_FOUND", "chat_id required");
        if (!_bridge.IsConnected) return ExtNotConnected();
        try
        {
            var result = await _bus.ReadAsync(chat_id, since_msg_id, limit, ct).ConfigureAwait(false);
            return Ok(result);
        }
        catch (OpenDiaToolException ex) when (ex.Message.Contains("CHAT_NOT_FOUND", StringComparison.Ordinal))
        {
            return Fail("CHAT_NOT_FOUND", ex.Message, new JsonObject { ["chat_id"] = chat_id });
        }
        catch (Exception ex) { return Fail("BUS_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "chat_send")]
    [Description("Append a message. role∈{user,assistant,tool}. client_msg_id is a uuid used for idempotency; the extension rejects duplicates with IDEMPOTENCY_CONFLICT if the content differs.")]
    public async Task<string> ChatSend(
        [Description("Target chat_id (uuid v4).")] string chat_id,
        [Description("Caller-generated uuid v4 idempotency key.")] string client_msg_id,
        [Description("One of: user | assistant | tool.")] string role,
        [Description("Message text (for role=tool: JSON-serialized result).")] string text,
        [Description("Optional tool call payload {name, args}. Reserved for role=assistant.")]
        JsonObject? tool_call = null,
        [Description("Optional metadata bag passed through untouched.")]
        JsonObject? metadata = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(chat_id)) return Fail("CHAT_NOT_FOUND", "chat_id required");
        if (string.IsNullOrEmpty(client_msg_id)) return Fail("INVALID_ROLE", "client_msg_id required");
        if (role is not ("user" or "assistant" or "tool"))
        {
            return Fail("INVALID_ROLE", $"role={role}", new JsonObject
            {
                ["provided"] = role,
                ["allowed"] = new JsonArray("user", "assistant", "tool"),
            });
        }
        if (!_bridge.IsConnected) return ExtNotConnected();
        try
        {
            var result = await _bus.SendAsync(chat_id, client_msg_id, role, text, tool_call, metadata, ct)
                                    .ConfigureAwait(false);
            return Ok(result);
        }
        catch (OpenDiaToolException ex) when (ex.Message.Contains("IDEMPOTENCY_CONFLICT", StringComparison.Ordinal))
        {
            return Fail("IDEMPOTENCY_CONFLICT", ex.Message, new JsonObject { ["client_msg_id"] = client_msg_id });
        }
        catch (OpenDiaToolException ex) when (ex.Message.Contains("CHAT_NOT_FOUND", StringComparison.Ordinal))
        {
            return Fail("CHAT_NOT_FOUND", ex.Message, new JsonObject { ["chat_id"] = chat_id });
        }
        catch (Exception ex) { return Fail("BUS_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "chat_create")]
    [Description("Create a chat. Extension generates the chat_id (uuid v4).")]
    public async Task<string> ChatCreate(
        [Description("Optional title. Extension may derive one from first user message when null.")]
        string? title = null,
        [Description("Optional Chrome tab id to associate for context linking.")]
        int? tab_hint = null,
        CancellationToken ct = default)
    {
        if (!_bridge.IsConnected) return ExtNotConnected();
        try
        {
            var result = await _bus.CreateAsync(title, tab_hint, ct).ConfigureAwait(false);
            return Ok(result);
        }
        catch (Exception ex) { return Fail("BUS_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "chat_delete")]
    [Description("Delete a chat and all its messages.")]
    public async Task<string> ChatDelete(
        [Description("uuid v4 of the chat to delete.")] string chat_id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(chat_id)) return Fail("CHAT_NOT_FOUND", "chat_id required");
        if (!_bridge.IsConnected) return ExtNotConnected();
        try
        {
            var result = await _bus.DeleteAsync(chat_id, ct).ConfigureAwait(false);
            return Ok(result);
        }
        catch (OpenDiaToolException ex) when (ex.Message.Contains("CHAT_NOT_FOUND", StringComparison.Ordinal))
        {
            return Fail("CHAT_NOT_FOUND", ex.Message, new JsonObject { ["chat_id"] = chat_id });
        }
        catch (Exception ex) { return Fail("BUS_ERROR", ex.Message); }
    }

    [McpServerTool(Name = "chat_subscribe")]
    [Description("Long-poll for new messages. Blocks up to timeout_ms for the next chat_appended push; on timeout returns {ok:true, timed_out:true, messages:[]}. Pass since_msg_id=last_seen after reconnects.")]
    public async Task<string> ChatSubscribe(
        [Description("Target chat_id (uuid v4).")] string chat_id,
        [Description("Optional monotonic watermark. Extension backfills messages with msg_id > since_msg_id before returning.")]
        long? since_msg_id = null,
        [Description("Max wait time before returning empty. Default 30_000ms; extension cap applies.")]
        int timeout_ms = 30_000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(chat_id)) return Fail("CHAT_NOT_FOUND", "chat_id required");
        if (!_bridge.IsConnected) return ExtNotConnected();

        OpenDiaChatBus.Subscription? sub = null;
        try
        {
            sub = await _bus.SubscribeAsync(chat_id, since_msg_id, ct).ConfigureAwait(false);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, timeout_ms)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                var frame = await sub.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                return Ok(new JsonObject
                {
                    ["chat_id"] = chat_id,
                    ["timed_out"] = false,
                    ["frame"] = frame.DeepClone(),
                    ["last_msg_id"] = sub.Sub.LastSeenMsgId,
                });
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return Ok(new JsonObject
                {
                    ["chat_id"] = chat_id,
                    ["timed_out"] = true,
                    ["messages"] = new JsonArray(),
                    ["last_msg_id"] = sub.Sub.LastSeenMsgId,
                });
            }
        }
        catch (OpenDiaToolException ex) when (ex.Message.Contains("CHAT_NOT_FOUND", StringComparison.Ordinal))
        {
            return Fail("CHAT_NOT_FOUND", ex.Message, new JsonObject { ["chat_id"] = chat_id });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("BUS_ERROR", ex.Message);
        }
        finally
        {
            if (sub is not null) await sub.DisposeAsync().ConfigureAwait(false);
        }
    }

    // -----------------------------------------------------------------
    // Envelope helpers — canonical shapes per SPEC §5.2.
    // -----------------------------------------------------------------

    private static string ExtNotConnected() => Fail("EXTENSION_NOT_CONNECTED", "Browser extension is not connected.");

    private static string Ok(JsonNode? result)
    {
        var envelope = new JsonObject
        {
            ["ok"] = true,
        };
        if (result is JsonObject obj)
        {
            foreach (var kv in obj) envelope[kv.Key] = kv.Value?.DeepClone();
        }
        else if (result is not null)
        {
            envelope["result"] = result.DeepClone();
        }
        return envelope.ToJsonString();
    }

    private static string Fail(string code, string message, JsonObject? details = null)
    {
        var payload = new JsonObject
        {
            ["ok"] = false,
            ["code"] = code,
            ["message"] = message,
        };
        if (details is not null) payload["details"] = details;
        return payload.ToJsonString();
    }
}
