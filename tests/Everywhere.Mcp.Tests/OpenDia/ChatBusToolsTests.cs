using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenDia;
using Everywhere.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Mcp.Tests.OpenDia;

/// <summary>
/// SPEC docs/specs/opendia-cebian-merge.md §Phase 4 acceptance tests.
/// The bridge is not started in these tests — <see cref="OpenDiaBridge.IsConnected"/>
/// stays false so the daemon-side tools return canonical error envelopes
/// per SPEC §5.2 without ever touching a socket. The one place that needs
/// a live push simulation uses <see cref="OpenDiaBridge.RaisePushFrameForTest"/>.
/// </summary>
[TestFixture]
public sealed class ChatBusToolsTests
{
    private static (OpenDiaBridge Bridge, OpenDiaChatBus Bus, ChatBusTools Tools) NewSet()
    {
        var bridge = new OpenDiaBridge(NullLogger<OpenDiaBridge>.Instance);
        var bus = new OpenDiaChatBus(bridge, NullLogger<OpenDiaChatBus>.Instance);
        var tools = new ChatBusTools(bus, bridge);
        return (bridge, bus, tools);
    }

    [Test]
    public async Task ChatList_NoExtension_ReturnsExtensionNotConnected()
    {
        var (_, _, tools) = NewSet();
        var json = JsonNode.Parse(await tools.ChatList())!.AsObject();
        Assert.That(json["ok"]!.GetValue<bool>(), Is.False);
        Assert.That(json["code"]!.GetValue<string>(), Is.EqualTo("EXTENSION_NOT_CONNECTED"));
    }

    [Test]
    public async Task ChatRead_NoExtension_ReturnsExtensionNotConnected()
    {
        var (_, _, tools) = NewSet();
        var json = JsonNode.Parse(await tools.ChatRead("00000000-0000-4000-8000-000000000001"))!.AsObject();
        Assert.That(json["code"]!.GetValue<string>(), Is.EqualTo("EXTENSION_NOT_CONNECTED"));
    }

    [Test]
    public async Task ChatSend_InvalidRole_ReturnsInvalidRole()
    {
        var (_, _, tools) = NewSet();
        var json = JsonNode.Parse(await tools.ChatSend(
            chat_id: "00000000-0000-4000-8000-000000000001",
            client_msg_id: "00000000-0000-4000-8000-00000000cafe",
            role: "system",
            text: "hi"))!.AsObject();
        Assert.That(json["code"]!.GetValue<string>(), Is.EqualTo("INVALID_ROLE"));
        Assert.That(json["details"]!["provided"]!.GetValue<string>(), Is.EqualTo("system"));
    }

    [Test]
    public async Task ChatSend_MissingChatId_ReturnsChatNotFound()
    {
        var (_, _, tools) = NewSet();
        var json = JsonNode.Parse(await tools.ChatSend(
            chat_id: "",
            client_msg_id: "00000000-0000-4000-8000-00000000cafe",
            role: "user",
            text: "hi"))!.AsObject();
        Assert.That(json["code"]!.GetValue<string>(), Is.EqualTo("CHAT_NOT_FOUND"));
    }

    [Test]
    public async Task ChatSend_MissingClientMsgId_ReturnsInvalidRole()
    {
        // Idempotency key is a strict prereq; we use the INVALID_ROLE code here
        // because it's the closest canonical code in SPEC §5.2 for
        // caller-side parameter shape errors (INVALID_ROLE spans role and
        // required-field failures). Keeps the error surface compact.
        var (_, _, tools) = NewSet();
        var json = JsonNode.Parse(await tools.ChatSend(
            chat_id: "00000000-0000-4000-8000-000000000001",
            client_msg_id: "",
            role: "user",
            text: "hi"))!.AsObject();
        Assert.That(json["code"]!.GetValue<string>(), Is.EqualTo("INVALID_ROLE"));
    }

    [Test]
    public async Task ChatSubscribe_NoExtension_ReturnsExtensionNotConnected()
    {
        var (_, _, tools) = NewSet();
        var json = JsonNode.Parse(await tools.ChatSubscribe(
            chat_id: "00000000-0000-4000-8000-000000000001",
            timeout_ms: 50))!.AsObject();
        Assert.That(json["code"]!.GetValue<string>(), Is.EqualTo("EXTENSION_NOT_CONNECTED"));
    }

    [Test]
    public async Task ChatBus_PushFrame_WakesSubscriberChannel()
    {
        // Bypass ChatBusTools (needs a connected bridge). Exercise the
        // OpenDiaChatBus event plumbing directly: register a subscriber via
        // the internal test hook, simulate a chat_appended push, assert the
        // channel yields the frame and LastSeenMsgId advances.
        var bridge = new OpenDiaBridge(NullLogger<OpenDiaBridge>.Instance);
        using var bus = new OpenDiaChatBus(bridge, NullLogger<OpenDiaChatBus>.Instance);
        var sub = bus.RegisterSubscriberForTest("test-sub-1", "chat-A");
        var pushed = new JsonObject
        {
            ["type"] = "chat_appended",
            ["sub_id"] = "test-sub-1",
            ["chat_id"] = "chat-A",
            ["msg"] = new JsonObject { ["msg_id"] = 42L, ["role"] = "user", ["text"] = "hello" },
        };
        bridge.RaisePushFrameForTest(pushed);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var received = await sub.Reader.ReadAsync(cts.Token);
        Assert.That(received["type"]!.GetValue<string>(), Is.EqualTo("chat_appended"));
        Assert.That(received["msg"]!["msg_id"]!.GetValue<long>(), Is.EqualTo(42L));
        Assert.That(sub.Sub.LastSeenMsgId, Is.EqualTo(42L));
    }

    [Test]
    public void OpenDiaChatBus_UnknownPushType_Dropped()
    {
        var bridge = new OpenDiaBridge(NullLogger<OpenDiaBridge>.Instance);
        using var bus = new OpenDiaChatBus(bridge, NullLogger<OpenDiaChatBus>.Instance);
        // No subscriber registered → push must not throw and must be a no-op.
        Assert.DoesNotThrow(() => bridge.RaisePushFrameForTest(new JsonObject
        {
            ["type"] = "unknown_push",
            ["sub_id"] = "nobody",
        }));
    }
}
