using System.ComponentModel;
using System.Text.Json;
using Everywhere.Mcp.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC parity rows: clipboard_read / clipboard_write / clipboard_copy /
/// clipboard_paste. Wraps IClipboardReader / IClipboardWriter on the
/// Everywhere side, satisfying the §3.1 universal-twice invariant
/// (browser-side has its own clipboard via the page, Everywhere-side has
/// the macOS general pasteboard).
/// </summary>
/// IMPORTANT: instance class, not static. The MCP server SDK reflects
/// on method-parameter types when building each tool's input JSON
/// schema. For interface params it treats them as inputs unless the
/// SDK already knows they're DI-resolvable. IClipboardReader squeaks
/// through (no public properties, mostly), but IClipboardWriter was
/// being added to the schema. Constructor injection sidesteps the
/// reflection altogether — same fix as BatchTool.
[McpServerToolType]
public sealed class ClipboardTools
{
    private readonly IClipboardReader _reader;
    private readonly IClipboardWriter _writer;

    public ClipboardTools(IClipboardReader reader, IClipboardWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    [McpServerTool(Name = "clipboard_read", ReadOnly = true)]
    [Description(
        "Read the macOS general pasteboard as plain text. " +
        "Returns {has_text:bool, text:string}. " +
        "Cheap; prefer this for inspecting the clipboard. SPEC ab agent_browser_clipboard_read.")]
    public CallToolResult ClipboardRead() => DoRead("clipboard_read");

    [McpServerTool(Name = "clipboard_paste", ReadOnly = true)]
    [Description(
        "Read the macOS general pasteboard (alias for clipboard_read). " +
        "SPEC ab agent_browser_clipboard_paste.")]
    public CallToolResult ClipboardPaste() => DoRead("clipboard_paste");

    [McpServerTool(Name = "clipboard_write")]
    [Description(
        "DANGEROUS: replace the macOS general pasteboard with the given text. " +
        "SPEC ab agent_browser_clipboard_write.")]
    public CallToolResult ClipboardWrite(string text) => DoWrite(text, "clipboard_write");

    [McpServerTool(Name = "clipboard_copy")]
    [Description(
        "DANGEROUS: copy a string to the macOS general pasteboard (alias for clipboard_write). " +
        "SPEC ab agent_browser_clipboard_copy.")]
    public CallToolResult ClipboardCopy(string text) => DoWrite(text, "clipboard_copy");

    private CallToolResult DoRead(string label)
    {
        try
        {
            var text = _reader.GetText() ?? string.Empty;
            return JsonOk(new { has_text = !string.IsNullOrEmpty(text), text });
        }
        catch (Exception ex) { return ToolErrors.FromException(ex, label); }
    }

    private CallToolResult DoWrite(string text, string label)
    {
        try
        {
            if (!_writer.IsAvailable()) return JsonOk(new { ok = false, error = "clipboard write not available on this host" });
            _writer.SetText(text ?? string.Empty);
            return JsonOk(new { ok = true, bytes = (text ?? string.Empty).Length });
        }
        catch (Exception ex) { return ToolErrors.FromException(ex, label); }
    }

    private static CallToolResult JsonOk(object payload) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
    };
}
