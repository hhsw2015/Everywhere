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
[McpServerToolType]
public static class ClipboardTools
{
    [McpServerTool(Name = "clipboard_read", ReadOnly = true)]
    [Description(
        "Read the macOS general pasteboard as plain text. " +
        "Returns {has_text:bool, text:string}. " +
        "Cheap; prefer this for inspecting the clipboard. SPEC ab agent_browser_clipboard_read.")]
    public static CallToolResult ClipboardRead(IClipboardReader reader)
    {
        try
        {
            var text = reader.GetText() ?? string.Empty;
            return JsonOk(new { has_text = !string.IsNullOrEmpty(text), text });
        }
        catch (Exception ex) { return ToolErrors.FromException(ex, "clipboard_read"); }
    }

    [McpServerTool(Name = "clipboard_write")]
    [Description(
        "DANGEROUS: replace the macOS general pasteboard with the given text. " +
        "SPEC ab agent_browser_clipboard_write.")]
    public static CallToolResult ClipboardWrite(IClipboardWriter writer, string text)
    {
        try
        {
            if (!writer.IsAvailable) return JsonOk(new { ok = false, error = "clipboard write not available on this host" });
            writer.SetText(text ?? string.Empty);
            return JsonOk(new { ok = true, bytes = (text ?? string.Empty).Length });
        }
        catch (Exception ex) { return ToolErrors.FromException(ex, "clipboard_write"); }
    }

    [McpServerTool(Name = "clipboard_copy")]
    [Description(
        "DANGEROUS: copy a string to the macOS general pasteboard (alias for clipboard_write). " +
        "SPEC ab agent_browser_clipboard_copy.")]
    public static CallToolResult ClipboardCopy(IClipboardWriter writer, string text)
    {
        try
        {
            if (!writer.IsAvailable) return JsonOk(new { ok = false, error = "clipboard write not available on this host" });
            writer.SetText(text ?? string.Empty);
            return JsonOk(new { ok = true, bytes = (text ?? string.Empty).Length });
        }
        catch (Exception ex) { return ToolErrors.FromException(ex, "clipboard_copy"); }
    }

    [McpServerTool(Name = "clipboard_paste")]
    [Description(
        "Read the macOS general pasteboard (alias for clipboard_read). " +
        "SPEC ab agent_browser_clipboard_paste.")]
    public static CallToolResult ClipboardPaste(IClipboardReader reader)
    {
        try
        {
            var text = reader.GetText() ?? string.Empty;
            return JsonOk(new { has_text = !string.IsNullOrEmpty(text), text });
        }
        catch (Exception ex) { return ToolErrors.FromException(ex, "clipboard_paste"); }
    }

    private static CallToolResult JsonOk(object payload) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
    };
}
