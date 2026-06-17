using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// On a Snapshot-Context hotkey press, captures a MINIMAL pointer to whatever the
/// user was looking at — focused app key, window title, browser URL, selected text —
/// and writes it atomically to a well-known JSON file. The Claude Code
/// UserPromptSubmit hook reads + deletes the file the next time the user hits Enter.
///
/// Mirrors what Everywhere's ChatWindow itself stashes when the global hotkey
/// fires: a reference to where the user was, NOT the entire a11y tree or a
/// screenshot. The agent decides whether to deep-dive via get_focused_context /
/// get_app_context / screenshot once it sees the pointer.
/// </summary>
public sealed class ContextStashWriter
{
    private static readonly string StashPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "Application Support",
        "Everywhere",
        "context-stash.json");

    private readonly IVisualElementContext _context;
    private readonly IBrowserUrlReader _browserUrl;
    private readonly SelectionCache _selectionCache;
    private readonly ILogger<ContextStashWriter> _logger;

    public ContextStashWriter(
        IVisualElementContext context,
        IBrowserUrlReader browserUrl,
        SelectionCache selectionCache,
        ILogger<ContextStashWriter> logger)
    {
        _context = context;
        _browserUrl = browserUrl;
        _selectionCache = selectionCache;
        _logger = logger;
    }

    public async Task CaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var focused = _context.FocusedElement;
            var topLevel = WalkToTopLevel(focused) ?? focused;
            var pid = topLevel?.ProcessId ?? 0;
            var appKey = pid > 0 ? AppKey.FromProcessId(pid) : null;

            string? url = null;
            if (pid > 0)
            {
                try { url = _browserUrl.GetUrl(pid); } catch { }
            }

            string? selectionText = null;
            string? selectionApp = null;
            if (_selectionCache.GetFresh() is { } cached)
            {
                selectionText = cached.Text;
                selectionApp = cached.AppKey;
            }
            else if (focused?.GetSelectionText() is { Length: > 0 } liveSel)
            {
                selectionText = liveSel;
                selectionApp = appKey;
            }

            var payload = new ContextSnapshotPayload(
                CapturedAtUtc: DateTimeOffset.UtcNow,
                App: appKey,
                ProcessId: pid > 0 ? pid : null,
                WindowTitle: topLevel?.Name,
                Url: url,
                SelectedText: selectionText,
                SelectedApp: selectionApp);

            await WriteAtomicAsync(FormatForHook(payload), cancellationToken);
            _logger.LogInformation("Context stash captured for {App} ({Title}).", appKey, topLevel?.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Context stash capture failed");
        }
    }

    /// <summary>
    /// Single line the hook streams verbatim into the agent prompt:
    /// <c>[everywhere-ctx] app=arc title="..." url=... selection="..."</c>
    /// Plus a structured JSON line below for agents that prefer to parse.
    /// Tree summary and screenshot intentionally omitted — agent calls the MCP
    /// tools when it actually needs them.
    /// </summary>
    private static string FormatForHook(ContextSnapshotPayload p)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[everywhere-ctx] ");
        if (p.App is { Length: > 0 }) sb.Append("app=").Append(p.App).Append(' ');
        if (p.WindowTitle is { Length: > 0 })
        {
            var title = p.WindowTitle.Length > 80 ? p.WindowTitle[..80] + "…" : p.WindowTitle;
            sb.Append("title=\"").Append(title.Replace('"', '\'')).Append("\" ");
        }
        if (p.Url is { Length: > 0 }) sb.Append("url=").Append(p.Url).Append(' ');
        if (p.SelectedText is { Length: > 0 })
        {
            var sel = p.SelectedText.Length > 200 ? p.SelectedText[..200] + "…" : p.SelectedText;
            sel = sel.Replace('\n', ' ').Replace('\r', ' ').Replace('"', '\'');
            sb.Append("selection=\"").Append(sel).Append('"');
        }
        sb.Append('\n');

        sb.Append("[everywhere-ctx-json] ");
        sb.Append(JsonSerializer.Serialize(p, ContextSnapshotPayload.SerializerOptions));
        sb.Append('\n');
        return sb.ToString();
    }

    private static async Task WriteAtomicAsync(string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(StashPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = StashPath + ".tmp";
        await File.WriteAllTextAsync(tmp, content, cancellationToken);
        File.Move(tmp, StashPath, overwrite: true);
    }

    private static IVisualElement? WalkToTopLevel(IVisualElement? element)
    {
        var current = element;
        for (var i = 0; current != null && i < 32; i++)
        {
            if (current.Type == VisualElementType.TopLevel) return current;
            current = current.Parent;
        }
        return current;
    }
}

internal sealed record ContextSnapshotPayload(
    [property: JsonPropertyName("captured_at_utc")] DateTimeOffset CapturedAtUtc,
    [property: JsonPropertyName("app")] string? App,
    [property: JsonPropertyName("process_id")] int? ProcessId,
    [property: JsonPropertyName("window_title")] string? WindowTitle,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("selected_text")] string? SelectedText,
    [property: JsonPropertyName("selected_app")] string? SelectedApp)
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
